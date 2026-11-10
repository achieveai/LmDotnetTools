#requires -Version 7
# Starts the Reviewd review host (host\LmStreaming.Sample.exe) if its port is free. Config comes from
# state\config\host.env; runtime folders under host\ are junctions into state\host.
param([int] $Port = 5310, [int] $GatewayPort = 3000)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$exe = Join-Path $root 'host\LmStreaming.Sample.exe'
$logs = Join-Path $root 'state\logs'
New-Item -ItemType Directory -Force $logs | Out-Null
if (Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue) {
    "Port $Port already listening; not starting another host."
    exit 0
}
$env:LMSTREAMING_ENV_FILE = Join-Path $root 'state\config\host.env'
# Never inherit a caller's URL or environment; host.env sets both.
$env:ASPNETCORE_URLS = $null
$env:ASPNETCORE_ENVIRONMENT = $null
$p = Start-Process -FilePath $exe -WorkingDirectory (Join-Path $root 'host') -PassThru -WindowStyle Hidden `
    -RedirectStandardOutput (Join-Path $logs "host-$Port.out.log") `
    -RedirectStandardError (Join-Path $logs "host-$Port.err.log")
$deadline = (Get-Date).AddSeconds(180)
$lastProbe = 'no probe answered'
while ((Get-Date) -lt $deadline) {
    if ($p.HasExited) { throw "Host exited with code $($p.ExitCode); see $logs\host-$Port.err.log." }
    try {
        $code = (Invoke-WebRequest "http://127.0.0.1:$Port/" -UseBasicParsing -TimeoutSec 5).StatusCode
        $lastProbe = "HTTP $code"
        if ($code -eq 200) {
            "Host up on $Port (pid $($p.Id))."
            # A cold gateway catalog takes minutes; the host's catalog call gives up after ~10 s and every unfreeze
            # then 503s, which holds re-reviews (2026-10-18). Build the catalog before the batch needs it.
            $t0 = Get-Date
            try {
                $null = Invoke-WebRequest "http://127.0.0.1:$GatewayPort/api/v1/marketplaces/preview?marketplaces=gb-plugins,sandbox-plugins" `
                    -UseBasicParsing -TimeoutSec 600
                "Gateway catalog warm in $([int]((Get-Date) - $t0).TotalSeconds)s."
            }
            catch { "Gateway catalog warm-up failed: $($_.Exception)`n$($_.ScriptStackTrace)" }
            exit 0
        }
    }
    catch { $lastProbe = "$($_.Exception.GetType().Name): $($_.Exception.Message)" }
    Start-Sleep -Seconds 2
}
throw "Host (pid $($p.Id)) did not answer 200 on $Port within 180s. Last probe: $lastProbe"
