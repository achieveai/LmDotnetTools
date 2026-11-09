#requires -Version 7
# Keeps the Reviewd runner alive: review host, OAuth webhook and the review batch. Scheduled every 5
# minutes; each run starts only what is missing. Liveness is by port (host, webhook) or command line
# (batch). The gateway is Docker-managed; a down gateway is logged, never restarted from here.
# Writes state\status.json for a quick look. Exit 0 when everything is up, 1 otherwise.
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$state = Join-Path $root 'state'
$logs = Join-Path $state 'logs'
New-Item -ItemType Directory -Force $logs | Out-Null
$cfg = Get-Content -Raw (Join-Path $state 'config\runner.json') | ConvertFrom-Json
$log = Join-Path $logs 'ensure.log'
function Log([string] $m) { Add-Content -LiteralPath $log -Value "$([DateTime]::UtcNow.ToString('o')) $m" -Encoding utf8 }
function Listening([int] $port) { [bool](Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue) }
function Start-Reviewd([string] $name, [string[]] $arguments) {
    $all = @('-NoProfile', '-File', (Join-Path $root 'reviewd.ps1')) + $arguments
    # Keep the previous run's logs: a restart is usually what someone wants to diagnose.
    foreach ($stream in 'out', 'err') {
        $file = Join-Path $logs "$name.$stream.log"
        if (Test-Path -LiteralPath $file) { Move-Item -LiteralPath $file -Destination "$file.prev" -Force }
    }
    $p = Start-Process pwsh -WindowStyle Hidden -PassThru -ArgumentList $all `
        -RedirectStandardOutput (Join-Path $logs "$name.out.log") -RedirectStandardError (Join-Path $logs "$name.err.log")
    Log "started $name (pid $($p.Id))"
}
# A stopped or paused runner: touch state\PAUSED to keep this script from starting anything. The pause expires so a
# deploy that dies halfway cannot stop reviews for good: at the UTC time written in the file (e.g.
# 2026-11-06T09:00Z), or 2 hours after the file was last written when it holds no time. For a longer stop, write a
# later time or Disable-ScheduledTask this watchdog. Reviewd Alarm reports any pause older than 1 hour.
$pauseFile = Join-Path $state 'PAUSED'
$paused = Test-Path -LiteralPath $pauseFile
if ($paused) {
    $until = [DateTimeOffset]::MinValue
    $text = (Get-Content -Raw -LiteralPath $pauseFile) ?? ''
    if (-not [DateTimeOffset]::TryParse($text.Trim(), [Globalization.CultureInfo]::InvariantCulture,
            [Globalization.DateTimeStyles]::AssumeUniversal, [ref] $until)) {
        $until = [DateTimeOffset]::new((Get-Item -LiteralPath $pauseFile).LastWriteTimeUtc, [TimeSpan]::Zero).AddHours(2)
    }
    if ([DateTimeOffset]::UtcNow -ge $until) {
        Remove-Item -LiteralPath $pauseFile -Force
        Log "WARN pause expired at $($until.UtcDateTime.ToString('o')); removed state\PAUSED and resuming."
        $paused = $false
    }
}
$status = [ordered]@{ checkedAt = [DateTime]::UtcNow.ToString('o'); paused = $paused }

$status.gateway = Listening $cfg.gatewayPort
if (-not $status.gateway) { Log "gateway :$($cfg.gatewayPort) is DOWN; start it with docker compose in SandboxedOstoolsMcpServer." }

$status.host = Listening $cfg.hostPort
if (-not $status.host -and -not $paused) {
    Log "host :$($cfg.hostPort) down; starting."
    try { & (Join-Path $root 'start-host.ps1') -Port $cfg.hostPort | ForEach-Object { Log "host: $_" }; $status.host = Listening $cfg.hostPort }
    catch { Log "host start FAILED: $($_.Exception.Message)" }
}

$status.webhook = Listening $cfg.webhookPort
if (-not $status.webhook -and -not $paused) {
    Start-Reviewd "webhook-$($cfg.webhookPort)" @('auth', 'serve', '--listen', "http://127.0.0.1:$($cfg.webhookPort)",
        '--callback', "http://localhost:$($cfg.webhookPort)")
}

$selection = Join-Path $state 'selection.json'
$batch = Get-CimInstance Win32_Process -Filter "Name = 'Reviewd.Sample.exe'" |
    Where-Object { $_.CommandLine -match ' review batch ' -and $_.CommandLine -like "*$root*" }
# A batch whose scheduling loop ended stays alive (it keeps serving its OAuth callback), so liveness by command line
# never replaced it: seen 2026-11-03, -04 and -05, hours with no reviews. Start-Reviewd rotates batch.err.log at each
# start, so "Scheduling stopped." there belongs to the running batch. Stop it and its wrapper; it is restarted below.
$batchErr = Join-Path $logs 'batch.err.log'
if ($batch -and -not $paused -and (Test-Path -LiteralPath $batchErr) -and
    (Select-String -LiteralPath $batchErr -Pattern '^Scheduling stopped\.' -Quiet)) {
    $why = (Select-String -LiteralPath $batchErr -Pattern '^Batch stopped' | Select-Object -Last 1).Line
    foreach ($b in @($batch)) {
        $wrapper = Get-CimInstance Win32_Process -Filter "ProcessId = $($b.ParentProcessId)" |
            Where-Object { $_.Name -eq 'pwsh.exe' -and $_.CommandLine -like "*$root\reviewd.ps1*" }
        Stop-Process -Id $b.ProcessId -Force -ErrorAction SilentlyContinue
        if ($wrapper) { Stop-Process -Id $wrapper.ProcessId -Force -ErrorAction SilentlyContinue }
        Log "batch $($b.ProcessId) had stopped scheduling; stopped it to restart. Last error: $why"
    }
    Start-Sleep -Seconds 3
    $batch = $null
}
$status.batch = [bool]$batch
if (-not $batch -and -not $paused) {
    if (-not (Test-Path -LiteralPath $selection)) { Log 'batch not started: no selection yet (discover.ps1 writes it).' }
    elseif (-not ($status.host -and $status.gateway)) { Log 'batch not started: host or gateway is down.' }
    else {
        Start-Reviewd 'batch' @('review', 'batch', '--selection', $selection, '--db', (Join-Path $state 'reviews.db'),
            '--session', $cfg.session, '--workspace', $cfg.workspace, '--provider', $cfg.provider, '--mode', $cfg.mode,
            '--reasoning-effort', $cfg.reasoningEffort, '--max-age-hours', $cfg.maxAgeHours,
            '--listen', "http://127.0.0.1:$($cfg.batchPort)", '--callback', "http://localhost:$($cfg.batchPort)", '--execute')
        $status.batch = 'started'
    }
}

$status | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $state 'status.json') -Encoding utf8NoBOM
exit ([int](-not ($status.gateway -and $status.host -and $status.webhook)))
