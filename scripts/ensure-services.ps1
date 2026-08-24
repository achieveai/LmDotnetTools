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
    # How long to wait before retrying certification of a host that is listening but that the
    # launcher could not certify. The task fires every five minutes and each attempt starts a
    # real conversation and model run on the host, so retrying every tick would be both a
    # restart loop and a bill.
    #
    # Range-checked because zero and negative values do not mean "no backoff" here - they mean
    # the age comparison is never less than the bound, so EVERY tick retries. That is the exact
    # loop this parameter exists to prevent, reachable by a typo. There is no supported
    # retry-every-tick mode; if one is ever wanted it has to be written as one.
    [ValidateRange(1, [int]::MaxValue)]
    [int]    $RecertifyBackoffMinutes = 30,

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

# Returns $null when the review host on $Port is the one this watchdog's launcher certified,
# or a human-readable reason when it is not.
#
# A listening socket is not health. The launcher deliberately LEAVES A HOST RUNNING when it
# fails certification - killing it would only trade a degraded reviewer for no reviewer - and
# it exits nonzero to say so. With port occupancy as the only health signal, that nonzero exit
# was then forgotten: the next tick five minutes later saw the port up, logged HEALTHY, and
# never retried, so a collaboration-off host served indefinitely while this script reported
# everything fine.
function Get-ReviewHostUncertifiedReason {
    param(
        [Parameter(Mandatory)] [int]    $Port,
        [Parameter(Mandatory)] [string] $RunDir,
        [Parameter(Mandatory)] [string] $ExpectedProcessName,
        [Parameter(Mandatory)] [string] $ExpectedPath
    )

    $ownerPid = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue |
        Select-Object -ExpandProperty OwningProcess -Unique |
        Select-Object -First 1
    if ($null -eq $ownerPid) { return "nothing is listening on $Port" }

    $proc = Get-Process -Id $ownerPid -ErrorAction SilentlyContinue
    if ($null -eq $proc) { return "the process holding $Port (pid $ownerPid) has gone" }
    if ($proc.ProcessName -ne $ExpectedProcessName) {
        return "port $Port is held by '$($proc.ProcessName)' (pid $ownerPid), not '$ExpectedProcessName'"
    }

    # Path reads as NULL, and StartTime RAISES, for a process this session cannot open. Both
    # are read defensively and both are required: an owner whose identity cannot be established
    # is uncertified, because the alternative is vouching for a process we cannot identify.
    # Normalised, because this is compared against a path Get-Process resolved fully. The
    # launcher canonicalises the same way; a configured -ReviewHostBinDir carrying a trailing
    # separator or a '..' would otherwise read as a different deployment.
    $ExpectedPath = [System.IO.Path]::GetFullPath($ExpectedPath)

    $ownerPath = $proc.Path
    if ([string]::IsNullOrEmpty($ownerPath)) {
        return "the executable path of pid $ownerPid on $Port cannot be read, so its identity cannot be established"
    }
    if (-not [string]::Equals($ownerPath, $ExpectedPath, [StringComparison]::OrdinalIgnoreCase)) {
        return "port $Port is held by '$ownerPath' (pid $ownerPid), not the deployment at '$ExpectedPath'"
    }
    try { $ownerStarted = $proc.StartTime.ToUniversalTime() }
    catch { return "the start time of pid $ownerPid on $Port cannot be read, so its identity cannot be established" }

    $marker = Join-Path $RunDir "review-host-$Port.certified.json"
    if (-not (Test-Path -LiteralPath $marker -PathType Leaf)) {
        return "no certification marker at '$marker' - this host was not certified by the launcher"
    }

    # EVERY read of the marker is inside this boundary, not just the JSON parse. Under
    # Set-StrictMode a marker of '{}' raises on the missing property and a hostPid of "abc"
    # raises on the cast, and either would escape as a terminating error that aborts the whole
    # watchdog run - skipping the remaining services and the RUN COMPLETE accounting - instead
    # of doing the obvious thing. A marker we cannot make sense of is an uncertified host.
    try {
        $cert = Get-Content -LiteralPath $marker -Raw | ConvertFrom-Json
        if ($null -eq $cert) { throw "empty or null document" }
        foreach ($field in 'hostPid', 'hostPath', 'hostStartedAtUtc') {
            if (-not $cert.PSObject.Properties[$field]) { throw "missing '$field'" }
        }
        $certPid = [int]$cert.hostPid
        $certPath = [string]$cert.hostPath
        $certStarted = ([datetime]::Parse($cert.hostStartedAtUtc)).ToUniversalTime()
    }
    catch {
        return "certification marker '$marker' is unusable ($($_.Exception.Message)) - treating the host as uncertified"
    }

    # Pid, path and start time together. A pid alone is reusable, so a stale marker could
    # otherwise vouch for whatever process Windows next handed that number to.
    if ($certPid -ne [int]$ownerPid) {
        return "certification marker names pid $certPid but pid $ownerPid owns $Port - the certified host was replaced"
    }
    if (-not [string]::Equals($certPath, $ownerPath, [StringComparison]::OrdinalIgnoreCase)) {
        return "certification marker names '$certPath' but pid $ownerPid runs '$ownerPath' - the marker does not describe this host"
    }
    if ([math]::Abs(($certStarted - $ownerStarted).TotalSeconds) -gt 2) {
        return "certification marker names a process started $($certStarted.ToString('o')) but pid $ownerPid started $($ownerStarted.ToString('o')) - the pid was reused"
    }

    return $null
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
        # Kill the TREE, and do not pretend it worked. Two things were wrong with swallowing
        # this: the launcher starts the host as its own child, so killing only the launcher
        # would strand a half-started host with nothing supervising it; and a failed or
        # incomplete kill was still reported as "killed it", leaving a launcher alive that the
        # next tick knows nothing about while the log says it is gone.
        $killError = $null
        try { $child.Kill($true) }
        catch { $killError = $_.Exception.Message }

        $killSuffix = if ($killError) { " (kill reported: $killError)" } else { "" }
        if (-not $child.WaitForExit(30000)) {
            throw "Review-host launcher (pid $($child.Id)) did not finish within ${TimeoutSeconds}s and was STILL RUNNING 30s after being killed$killSuffix; it may still be starting a host. Investigate before the next run."
        }
        throw "Review-host launcher (pid $($child.Id)) did not finish within ${TimeoutSeconds}s; killed it and its tree$killSuffix."
    }
    # $null is UNKNOWN here, not failure, and the difference is the whole bug. Windows
    # PowerShell 5.1 returns $null from ExitCode for a Start-Process -PassThru object even
    # after WaitForExit reports HasExited=True; PowerShell 7 returns the real code. Written as
    # `if ($child.ExitCode -ne 0)`, $null compares unequal to 0, so under 5.1 EVERY successful
    # launch threw - a host that started, certified and wrote its marker was still recorded as
    # START FAILED and made the run exit 1. Reproduced 5/5 under 5.1 and 0/5 under 7.
    #
    # An unknown exit code is not a reason to guess. The caller re-applies the certification
    # predicate immediately after this returns, and that predicate inspects the host itself
    # rather than the messenger, so it is the better authority in exactly this case.
    $exitCode = $child.ExitCode
    if ($null -eq $exitCode) {
        Write-RunLog -Level WARN -Message "review-host launcher exit code is unavailable (this shell does not report it); the certification check below decides the outcome."
    }
    elseif ($exitCode -ne 0) {
        throw "Review-host launcher exited with code $exitCode; see $RunDir\review-host-$Port.err.log."
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
            # Same 5.1 quirk as in Start-ReviewHost: ExitCode can be $null even though the
            # process has exited. Say "unavailable" rather than printing an empty code, which
            # reads as though the daemon exited with no status at all.
            $code = if ($null -eq $proc.ExitCode) { 'unavailable' } else { $proc.ExitCode }
            throw "Daemon '$Tenant' EXITED (code $code) without listening on $Port; see $RunDir\daemon-$Tenant.err.log."
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
            Name    = 'review-host'
            Port    = $ReviewHostPort
            Start   = { Start-ReviewHost -ScriptPath $script:RestartReviewHostScriptResolved -BinDir $ReviewHostBinDir -Port $ReviewHostPort -RunDir $RunDir -TimeoutSeconds $StartTimeoutSeconds }
            Healthy = { Get-ReviewHostUncertifiedReason -Port $ReviewHostPort -RunDir $RunDir -ExpectedProcessName 'LmStreaming.Sample' -ExpectedPath (Join-Path $ReviewHostBinDir 'LmStreaming.Sample.exe') }
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
    $deferred = 0
    $failed = 0

    Write-RunLog -Level INFO -Message ("RUN START dryRun=$($DryRun.IsPresent) reviewHostBin='$ReviewHostBinDir' daemonBin='$DaemonBinDir'")

    foreach ($svc in $services) {
        $checked++

        $portUp = Test-PortListening -Port $svc.Port

        # Only services that declare a Healthy predicate get one; the daemons are judged by
        # their port alone. Guarded through PSObject because under StrictMode reading a
        # property a pscustomobject does not have is an error, not $null.
        $unhealthyReason = $null
        if ($portUp -and $svc.PSObject.Properties['Healthy']) {
            $unhealthyReason = & $svc.Healthy
        }

        if ($portUp -and -not $unhealthyReason) {
            $healthy++
            Write-RunLog -Level INFO -Message ("{0}:{1} HEALTHY - already listening, not touched [{2}]" -f $svc.Name, $svc.Port, (Get-PortOwnerDescription -Port $svc.Port))
            continue
        }

        if ($portUp) {
            # Listening, but not the host we certified. Restarting is the only way to retry
            # certification - but it must not become a five-minute loop against a host that
            # will never certify, because each attempt starts a real conversation and a real
            # model run on it. So back off, and say plainly that we are backing off: a silent
            # deferral would read exactly like the "forgotten" behaviour this replaces.
            #
            # The stamp is written by restart-review-host.ps1 immediately before it launches,
            # NOT here. Only that script knows whether an attempt actually reached a launch:
            # it refuses, before starting anything, when a review is in flight, when a foreign
            # process holds the port, or when the binary is missing. Stamping here would have
            # charged those refusals the full backoff, so a host left uncertified while it
            # finished a review would have waited half an hour to be re-certified - and the
            # in-flight refusal is the one case where the retry should come quickly.
            $stampPath = Join-Path $RunDir ("review-host-{0}.launch-attempt" -f $svc.Port)
            $lastAttempt = $null
            if (Test-Path -LiteralPath $stampPath -PathType Leaf) {
                try { $lastAttempt = ([datetime]::Parse((Get-Content -LiteralPath $stampPath -Raw).Trim())).ToUniversalTime() }
                catch { $lastAttempt = $null }
            }

            if ($null -ne $lastAttempt -and ([datetime]::UtcNow - $lastAttempt).TotalMinutes -lt $RecertifyBackoffMinutes) {
                $deferred++
                Write-RunLog -Level WARN -Message ("{0}:{1} LISTENING BUT UNCERTIFIED - {2}; re-certification DEFERRED (last attempt {3:N1} min ago, backoff {4} min)" -f `
                        $svc.Name, $svc.Port, $unhealthyReason, ([datetime]::UtcNow - $lastAttempt).TotalMinutes, $RecertifyBackoffMinutes)
                continue
            }

            Write-RunLog -Level WARN -Message ("{0}:{1} LISTENING BUT UNCERTIFIED - {2}; restarting to re-certify" -f $svc.Name, $svc.Port, $unhealthyReason)
        }

        if ($DryRun) {
            # Counted separately from $failed on purpose. A dry run STARTS nothing, so it can
            # never observe a start failure; scoring "down" as "failed" made -DryRun exit 1
            # whenever any service was down, which is the normal case you run it to inspect.
            # That makes the exit code useless exactly when you are reading it, and would make
            # a CI or wrapper check treat a successful inspection as an error.
            $what = if ($portUp) { 'would restart to re-certify' } else { 'DOWN - would start' }
            Write-RunLog -Level WARN -Message ("{0}:{1} {2} (dry run; nothing launched)" -f $svc.Name, $svc.Port, $what)
            $wouldStart++
            continue
        }

        if (-not $portUp) {
            Write-RunLog -Level WARN -Message ("{0}:{1} DOWN - starting" -f $svc.Name, $svc.Port)
        }
        try {
            & $svc.Start

            # Re-apply the SAME predicate that judged health above. Accepting a bare listening
            # port here would reintroduce the gap one line later: a launcher can exit 0 having
            # started something this watchdog cannot vouch for.
            $nowUp = Test-PortListening -Port $svc.Port
            $stillUncertified = $null
            if ($nowUp -and $svc.PSObject.Properties['Healthy']) { $stillUncertified = & $svc.Healthy }

            if ($nowUp -and -not $stillUncertified) {
                $started++
                Write-RunLog -Level INFO -Message ("{0}:{1} STARTED [{2}]" -f $svc.Name, $svc.Port, (Get-PortOwnerDescription -Port $svc.Port))
            }
            elseif ($nowUp) {
                $failed++
                Write-RunLog -Level ERROR -Message ("{0}:{1} STARTED BUT NOT CERTIFIED - {2}" -f $svc.Name, $svc.Port, $stillUncertified)
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
    # deferred is reported but does NOT fail the run: nothing was attempted, so there is no
    # failure to report - only a backoff that the WARN line above already spelled out.
    $deferPart = if ($deferred -gt 0) { " deferred=$deferred" } else { "" }
    Write-RunLog -Level INFO -Message ("RUN COMPLETE checked=$checked healthy=$healthy started=$started$dryPart$deferPart failed=$failed exit=$exitCode")
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
