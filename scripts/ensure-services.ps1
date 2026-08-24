#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Idempotent watchdog for the three long-running review services. Starts whatever is not
    listening, leaves whatever is alone, and writes a line per run so that a silent failure
    becomes visible the next time somebody looks.

.DESCRIPTION
    Managed services, started in this order:

        1. review host        :5051   LmStreaming.Sample.exe
        2. achieveai daemon   :5080   CodeReviewDaemon.Sample.exe --review achieveai
        3. mcqdb daemon       :5082   CodeReviewDaemon.Sample.exe --review mcqdb

    The order is not cosmetic. Both daemons read the review host over S2S
    (CodeReviewDaemon:LmStreamingBaseUrl = http://localhost:5051); a daemon that starts
    first simply fails its reviews until the host appears.

    LIVENESS IS DECIDED BY PORT, NOT BY PROCESS NAME. Two traps make anything else wrong:

      * These services run as APPHOSTS - LmStreaming.Sample.exe and
        CodeReviewDaemon.Sample.exe - not under dotnet.exe. A process sweep filtered on
        Name='dotnet.exe' matches NOTHING and concludes every service is dead, so the
        watchdog starts duplicates on every tick. Where this script does look at processes
        it is only to NAME the owner of a port in the log, and it matches the apphost names.

      * The redirected-stdout .out.log files do NOT update their modification time as they
        are written. File mtime is not a liveness signal here and is never used as one.

    IDEMPOTENT BY CONSTRUCTION. A service whose port is listening is reported healthy and
    is not touched. A named mutex additionally prevents two runs from overlapping, which a
    five-minute timer over a start sequence that can take two minutes would otherwise do.

.PARAMETER ReviewHostBinDir
    Published deployment directory holding LmStreaming.Sample.exe.

.PARAMETER DaemonBinDir
    Published deployment directory holding CodeReviewDaemon.Sample.exe. Both daemon
    profiles run from the same deployment; they differ only by --review <tenant>.

.PARAMETER RunDir
    Where the watchdog log and the services' redirected stdout/stderr land.

.PARAMETER DryRun
    Report what would be started and start nothing. Use this to inspect the watchdog's
    decisions without touching a live box.

.EXAMPLE
    ./scripts/ensure-services.ps1
    The scheduled-task invocation. Starts what is down, leaves what is up.

.EXAMPLE
    ./scripts/ensure-services.ps1 -DryRun
    Print the decisions only.
#>
[CmdletBinding()]
param(
    # The review host gets its OWN deployment, deliberately NOT
    # B:\published\LmStreaming.Sample. That directory is the interactive chat host, which
    # runs continuously on :5050 and carries an .env written for it: a Traefik-fronted
    # Auth:Webhook:PublicBaseUrl for the REMOTE gateway, DeepSeek and M365 secrets, and a
    # macOS SandboxGateway:WorkspaceBasePath. Pointing the watchdog there starts a SECOND
    # instance out of one deployment - two processes sharing one .env, one conversations/
    # and one notify-waits.db - and hands the review host a webhook base that is not its
    # own port, which is the exact condition that ends reviews with zero sub-agents and a
    # 38-char "No new findings since the last review."
    [string] $ReviewHostBinDir = 'B:\published\review-host',
    [int]    $ReviewHostPort = 5051,

    [string] $DaemonBinDir = 'B:\published\CodeReviewDaemon.Sample',
    [int]    $AchieveAiPort = 5080,
    [int]    $McqdbPort = 5082,

    [string] $RunDir = 'B:\sources\LmDotnetTools\.run',

    # Resolved from $PSScriptRoot, not from $PWD: the scheduled task runs with an arbitrary
    # working directory, and a relative default would resolve against it.
    [string] $RestartReviewHostScript,

    [int]    $StartTimeoutSeconds = 240,
    [int]    $DaemonReadyTimeoutSeconds = 120,

    [switch] $DryRun
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$script:RunId = [guid]::NewGuid().ToString('N').Substring(0, 8)
# Initialised at script scope so that a dot-sourced Invoke-Main does not read an undefined
# variable under Set-StrictMode.
$script:RestartReviewHostScriptResolved = $RestartReviewHostScript
$script:LogPath = Join-Path $RunDir 'ensure-services.log'
$script:MaxLogBytes = 5MB

# ---------------------------------------------------------------------------
# Logging
# ---------------------------------------------------------------------------

# Every run appends here. This file is the entire point of the exercise: the watchdog this
# replaces died silently, the scheduled task kept returning 0 against a script that no
# longer existed, and nothing anywhere recorded that the services had not been checked in
# weeks. A log that says "checked, healthy" is what makes "no log at all" diagnostic.
function Write-RunLog {
    param(
        [Parameter(Mandatory)] [ValidateSet('INFO', 'WARN', 'ERROR')] [string] $Level,
        [Parameter(Mandatory)] [string] $Message
    )

    $stamp = [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ss.fffZ')
    $line = '{0} pid={1} run={2} {3,-5} {4}' -f $stamp, $PID, $script:RunId, $Level, $Message

    switch ($Level) {
        'ERROR' { Write-Host $line -ForegroundColor Red }
        'WARN' { Write-Host $line -ForegroundColor Yellow }
        default { Write-Host $line }
    }

    # Logging must never be the thing that fails the watchdog: a locked or unwritable log
    # is a reason to keep going and complain, not a reason to leave the services down.
    try {
        $dir = Split-Path -Parent $script:LogPath
        if (-not (Test-Path -LiteralPath $dir)) {
            New-Item -ItemType Directory -Path $dir -Force | Out-Null
        }
        # Size-based rotation. At one run every five minutes this file grows forever
        # otherwise; a watchdog that eventually fills the disk it watches is not a watchdog.
        $existing = Get-Item -LiteralPath $script:LogPath -ErrorAction SilentlyContinue
        if ($null -ne $existing -and $existing.Length -gt $script:MaxLogBytes) {
            $rolled = '{0}.{1}' -f $script:LogPath, [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ')
            Move-Item -LiteralPath $script:LogPath -Destination $rolled -Force
        }
        Add-Content -LiteralPath $script:LogPath -Value $line -Encoding utf8
    }
    catch {
        Write-Warning "ensure-services: could not append to '$($script:LogPath)': $($_.Exception.Message)"
    }
}

# ---------------------------------------------------------------------------
# Liveness
# ---------------------------------------------------------------------------

function Test-PortListening {
    param([Parameter(Mandatory)] [int] $Port)

    $connections = @(Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue)
    return ($connections.Count -gt 0)
}

# Names the process holding a port, for the log only. Never used to DECIDE liveness - the
# listening socket already decided that.
#
# Process.Path is an ETS ScriptProperty over MainModule.FileName: for a process running
# elevated or as another user the underlying Win32Exception is swallowed and the getter
# yields $null rather than throwing. So the null branch is the load-bearing one, and it
# reports the name it does have instead of pretending the lookup failed entirely.
function Get-PortOwnerDescription {
    param([Parameter(Mandatory)] [int] $Port)

    $owners = @(
        Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue |
            Select-Object -ExpandProperty OwningProcess -Unique
    )
    if ($owners.Count -eq 0) { return '(no listener)' }

    $described = foreach ($owningPid in $owners) {
        $proc = Get-Process -Id $owningPid -ErrorAction SilentlyContinue
        if ($null -eq $proc) {
            "pid $owningPid (process gone)"
        }
        else {
            $imagePath = $null
            try { $imagePath = $proc.Path } catch { $imagePath = $null }
            if ([string]::IsNullOrEmpty($imagePath)) {
                "pid $owningPid ($($proc.ProcessName), path unavailable)"
            }
            else {
                "pid $owningPid ($($proc.ProcessName) at $imagePath)"
            }
        }
    }
    return ($described -join '; ')
}

function Wait-PortListening {
    param(
        [Parameter(Mandatory)] [int] $Port,
        [Parameter(Mandatory)] [int] $TimeoutSeconds
    )

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        if (Test-PortListening -Port $Port) { return $true }
        Start-Sleep -Seconds 2
    }
    return (Test-PortListening -Port $Port)
}

# ---------------------------------------------------------------------------
# Starters
# ---------------------------------------------------------------------------

# The review host is started through scripts/restart-review-host.ps1 rather than by
# duplicating its launch logic here. That script carries knowledge this one must not
# re-derive: the AgentCollaboration__* environment overrides without which every
# sub-agent transcript read 404s, the Auth__Webhook__PublicBaseUrl pin without which
# discovery is delivered to a port nobody listens on, and the refusal to kill a host with
# reviews in flight.
#
# It is invoked in a CHILD pwsh, deliberately. That script sets ASPNETCORE_URLS and
# ASPNETCORE_ENVIRONMENT in its own process so the host it launches inherits them; calling
# it in-process would leak ASPNETCORE_URLS=http://localhost:5051 into this process, and the
# two daemons started afterwards would inherit it too and every one of them would try to
# bind 5051 instead of its own port. A child process cannot leak environment upwards.
function Start-ReviewHost {
    param(
        [Parameter(Mandatory)] [string] $ScriptPath,
        [Parameter(Mandatory)] [string] $BinDir,
        [Parameter(Mandatory)] [int]    $Port,
        [Parameter(Mandatory)] [string] $RunDir,
        [Parameter(Mandatory)] [int]    $TimeoutSeconds
    )

    if (-not (Test-Path -LiteralPath $ScriptPath -PathType Leaf)) {
        throw "Review-host launcher not found: '$ScriptPath'."
    }
    $exe = Join-Path $BinDir 'LmStreaming.Sample.exe'
    if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) {
        throw "Review-host binary not found: '$exe'. Publish it first (samples/LmStreaming.Sample/publish-launch.ps1 -DestinationDirectory '$BinDir')."
    }

    $pwshPath = (Get-Process -Id $PID).Path
    if ([string]::IsNullOrEmpty($pwshPath)) { $pwshPath = 'pwsh' }

    $child = Start-Process -FilePath $pwshPath -PassThru -NoNewWindow -ArgumentList @(
        '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass',
        '-File', $ScriptPath,
        '-BinDir', $BinDir,
        '-Port', "$Port",
        '-RunDir', $RunDir
    )

    # Bounded so a wedged launcher cannot outlive the five-minute timer and pile up.
    if (-not $child.WaitForExit($TimeoutSeconds * 1000)) {
        try { $child.Kill($true) } catch { }
        throw "Review-host launcher did not finish within ${TimeoutSeconds}s; killed it."
    }
    if ($child.ExitCode -ne 0) {
        throw "Review-host launcher exited with code $($child.ExitCode); see $RunDir\review-host-$Port.err.log."
    }
}

function Start-Daemon {
    param(
        [Parameter(Mandatory)] [string] $BinDir,
        [Parameter(Mandatory)] [string] $Tenant,
        [Parameter(Mandatory)] [int]    $Port,
        [Parameter(Mandatory)] [string] $RunDir,
        [Parameter(Mandatory)] [int]    $TimeoutSeconds
    )

    $exe = Join-Path $BinDir 'CodeReviewDaemon.Sample.exe'
    if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) {
        throw "Daemon binary not found: '$exe'. Publish it first (scripts/publish-daemon.ps1 -DestinationDirectory '$BinDir')."
    }
    $tenantConfig = Join-Path $BinDir "appsettings.$Tenant.json"
    if (-not (Test-Path -LiteralPath $tenantConfig -PathType Leaf)) {
        throw "Tenant profile not found: '$tenantConfig'. Without it the daemon starts with the BASE settings only - no EnabledRepos, no DatabasePath - and looks perfectly healthy while reviewing nothing."
    }

    # Defensive: if anything upstream left these set, ASPNETCORE_URLS would override the
    # tenant profile's own Urls and put both daemons on the same port.
    $env:ASPNETCORE_URLS = $null
    $env:ASPNETCORE_ENVIRONMENT = $null

    # The daemon resolves DatabasePath/LogFilePath relative to nothing - they are absolute -
    # but its content root must be the deployment directory so it finds its appsettings
    # files and embedded prompt assets.
    $proc = Start-Process -FilePath $exe -WorkingDirectory $BinDir -PassThru -WindowStyle Hidden `
        -ArgumentList @('--review', $Tenant) `
        -RedirectStandardOutput (Join-Path $RunDir "daemon-$Tenant.out.log") `
        -RedirectStandardError (Join-Path $RunDir "daemon-$Tenant.err.log")

    if (-not (Wait-PortListening -Port $Port -TimeoutSeconds $TimeoutSeconds)) {
        if ($proc.HasExited) {
            throw "Daemon '$Tenant' EXITED with code $($proc.ExitCode) without listening on $Port; see $RunDir\daemon-$Tenant.err.log."
        }
        throw "Daemon '$Tenant' (pid $($proc.Id)) did not listen on $Port within ${TimeoutSeconds}s; see $RunDir\daemon-$Tenant.err.log."
    }
    return $proc.Id
}

# ---------------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------------

function Invoke-Main {
    if ([string]::IsNullOrWhiteSpace($script:RestartReviewHostScriptResolved)) {
        $script:RestartReviewHostScriptResolved = Join-Path $PSScriptRoot 'restart-review-host.ps1'
    }

    $services = @(
        [pscustomobject]@{
            Name  = 'review-host'
            Port  = $ReviewHostPort
            Start = { Start-ReviewHost -ScriptPath $script:RestartReviewHostScriptResolved -BinDir $ReviewHostBinDir -Port $ReviewHostPort -RunDir $RunDir -TimeoutSeconds $StartTimeoutSeconds }
        },
        [pscustomobject]@{
            Name  = 'daemon-achieveai'
            Port  = $AchieveAiPort
            Start = { Start-Daemon -BinDir $DaemonBinDir -Tenant 'achieveai' -Port $AchieveAiPort -RunDir $RunDir -TimeoutSeconds $DaemonReadyTimeoutSeconds | Out-Null }
        },
        [pscustomobject]@{
            Name  = 'daemon-mcqdb'
            Port  = $McqdbPort
            Start = { Start-Daemon -BinDir $DaemonBinDir -Tenant 'mcqdb' -Port $McqdbPort -RunDir $RunDir -TimeoutSeconds $DaemonReadyTimeoutSeconds | Out-Null }
        }
    )

    $checked = 0
    $healthy = 0
    $started = 0
    $wouldStart = 0
    $failed = 0

    Write-RunLog -Level INFO -Message ("RUN START dryRun=$($DryRun.IsPresent) reviewHostBin='$ReviewHostBinDir' daemonBin='$DaemonBinDir'")

    foreach ($svc in $services) {
        $checked++
        if (Test-PortListening -Port $svc.Port) {
            $healthy++
            Write-RunLog -Level INFO -Message ("{0}:{1} HEALTHY - already listening, not touched [{2}]" -f $svc.Name, $svc.Port, (Get-PortOwnerDescription -Port $svc.Port))
            continue
        }

        if ($DryRun) {
            # Counted separately from $failed on purpose. A dry run STARTS nothing, so it can
            # never observe a start failure; scoring "down" as "failed" made -DryRun exit 1
            # whenever any service was down, which is the normal case you run it to inspect.
            # That makes the exit code useless exactly when you are reading it, and would make
            # a CI or wrapper check treat a successful inspection as an error.
            Write-RunLog -Level WARN -Message ("{0}:{1} DOWN - would start (dry run; nothing launched)" -f $svc.Name, $svc.Port)
            $wouldStart++
            continue
        }

        Write-RunLog -Level WARN -Message ("{0}:{1} DOWN - starting" -f $svc.Name, $svc.Port)
        try {
            & $svc.Start
            if (Test-PortListening -Port $svc.Port) {
                $started++
                Write-RunLog -Level INFO -Message ("{0}:{1} STARTED [{2}]" -f $svc.Name, $svc.Port, (Get-PortOwnerDescription -Port $svc.Port))
            }
            else {
                $failed++
                Write-RunLog -Level ERROR -Message ("{0}:{1} START REPORTED SUCCESS BUT PORT IS NOT LISTENING" -f $svc.Name, $svc.Port)
            }
        }
        catch {
            $failed++
            Write-RunLog -Level ERROR -Message ("{0}:{1} START FAILED - {2}" -f $svc.Name, $svc.Port, $_.Exception.Message)
        }
    }

    $exitCode = if ($failed -gt 0) { 1 } else { 0 }
    $dryPart = if ($DryRun) { " wouldStart=$wouldStart" } else { "" }
    Write-RunLog -Level INFO -Message ("RUN COMPLETE checked=$checked healthy=$healthy started=$started$dryPart failed=$failed exit=$exitCode")
    return $exitCode
}

# Dot-source guard: ". ./scripts/ensure-services.ps1" defines the functions above for
# testing and starts nothing.
if ($MyInvocation.InvocationName -ne '.') {
    # One run at a time. The scheduled task fires every five minutes; a cold start of all
    # three services can take longer than that, and two overlapping runs would each see the
    # same port down and each launch a service - exactly the duplicate this watchdog exists
    # to avoid. Whoever loses the race is a no-op, not an error: the winner is already
    # doing the work.
    #
    # GLOBAL, not Local. A 'Local\' mutex is scoped to a Windows logon SESSION, and the runs
    # that actually collide are in different sessions: the scheduled task runs in session 0
    # while a hand-run or RDP invocation runs in the interactive session. Under 'Local\' those
    # two each get their own mutex, both acquire immediately, both see the same port down, and
    # both launch a service - the exact duplicate this block claims to prevent. Restoring these
    # services meant running the watchdog by hand while the task kept firing, so this was not a
    # hypothetical overlap.
    #
    # Creating a Global mutex needs SeCreateGlobalPrivilege. If that is denied, fall back to
    # Local and SAY SO rather than failing the run: a narrowly-scoped lock still prevents the
    # common same-session overlap, and a watchdog that refuses to run protects nothing.
    $mutexName = 'Global\LmDotnetTools.EnsureServices'
    try {
        $mutex = New-Object System.Threading.Mutex($false, $mutexName)
    }
    catch [System.UnauthorizedAccessException] {
        Write-RunLog -Level WARN -Message "cannot create $mutexName (SeCreateGlobalPrivilege denied); falling back to a session-scoped lock, which does NOT exclude the scheduled task's session"
        $mutexName = 'Local\LmDotnetTools.EnsureServices'
        $mutex = New-Object System.Threading.Mutex($false, $mutexName)
    }
    $acquired = $false
    try {
        try { $acquired = $mutex.WaitOne([TimeSpan]::FromSeconds(5)) }
        catch [System.Threading.AbandonedMutexException] { $acquired = $true }

        if (-not $acquired) {
            Write-RunLog -Level INFO -Message 'RUN SKIPPED - another ensure-services run holds the lock'
            exit 0
        }

        exit (Invoke-Main)
    }
    finally {
        if ($acquired) { $mutex.ReleaseMutex() }
        $mutex.Dispose()
    }
}
