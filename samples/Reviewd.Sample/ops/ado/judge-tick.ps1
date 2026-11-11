#requires -Version 7
# Windows port of samples/Reviewd.Sample/ops/judge-tick.sh for this install: judge merged PRs' verified reviews
# (judge batch --execute within the daily cap), then, when judge.json "close" is true, file each completed final
# judgment into the review store (judge close --execute). Settings: state\judge\judge.json.
# Stop gracefully: create state\judge\STOPPED (running judges finish, none start, nothing closes). Resume: delete it.
# Any batch failure latches STOPPED with the reason, like the Linux tick.
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot; $state = Join-Path $root 'state'; $jh = Join-Path $state 'judge'
$stop = Join-Path $jh 'STOPPED'; $log = Join-Path $jh 'judge-tick.log'; $pendingFailed = $false
function Log([string] $m) { Add-Content -LiteralPath $log -Value "$([DateTime]::UtcNow.ToString('o')) $m" -Encoding utf8 }
# The task action keeps no stderr, so anything that stops this script must reach the log first.
trap { try { Log "FAILED: $($_.Exception)`n$($_.ScriptStackTrace)" } catch { }; exit 1 }
$cfg = Get-Content -Raw (Join-Path $jh 'judge.json') | ConvertFrom-Json
$run = Get-Content -Raw (Join-Path $state 'config\runner.json') | ConvertFrom-Json
$db = Join-Path $state 'reviews.db'
"ado`t$db`t$($run.session)`t$($run.workspace)" | Set-Content (Join-Path $jh 'targets.tsv') -Encoding utf8NoBOM
Set-Location $state
$batchArgs = @('judge', 'batch', '--targets', (Join-Path $jh 'targets.tsv'), '--stop-file', $stop,
    '--provider', $cfg.provider, '--reasoning-effort', $cfg.effort, '--max-usd', $cfg.maxUsd,
    '--max-usd-per-day', $cfg.maxUsdPerDay, '--avg-usd-target', $cfg.avgUsdTarget, '--max-age-days', $cfg.maxAgeDays)
if ($cfg.execute) { $batchArgs += '--execute' }
Log "tick start (execute=$($cfg.execute), close=$($cfg.close))"
& (Join-Path $root 'reviewd.ps1') @batchArgs > (Join-Path $jh 'summary.md.new') 2>> (Join-Path $jh 'judge-batch.err.log')
$rc = $LASTEXITCODE
Move-Item -Force (Join-Path $jh 'summary.md.new') (Join-Path $jh 'summary.md')
$status = (Get-Content (Join-Path $jh 'summary.md') | Where-Object { $_ -match '^\*\*Status:' } | Select-Object -First 1)
Log "batch exit $rc; $status"
if ($rc -ne 0 -and -not (Test-Path $stop)) {
    Set-Content $stop "$([DateTime]::UtcNow.ToString('o')): judge batch exited $rc ($status). See summary.md and judge-batch.err.log. Delete this file to resume." -Encoding utf8NoBOM
    Log 'latched STOPPED'
}
if ($cfg.close -and -not (Test-Path $stop)) {
    $closed = Join-Path $jh 'closed.tsv'; $failed = Join-Path $jh 'close-failed.tsv'
    # A failed read must not look like "nothing to close": keep its stderr, log it, close nothing this tick.
    $pending = @(& python3 -I (Join-Path $root 'judge-pending-closes.py') $db $jh 2>> (Join-Path $jh 'pending-closes.err.log'))
    if ($LASTEXITCODE -ne 0) {
        Log "pending-close read FAILED (exit $LASTEXITCODE); see pending-closes.err.log. No closes this tick."
        $pending = @(); $pendingFailed = $true
    }
    $n = 0
    foreach ($line in $pending) {
        if ((Test-Path $stop) -or $n -ge $cfg.closeLimit) { break }
        $repo, $pr, $j = $line -split "`t"
        $out = & (Join-Path $root 'reviewd.ps1') judge close --repo $repo --pr $pr --db $db --store $cfg.store --judgment $j --execute 2>&1
        $crc = $LASTEXITCODE; $n++
        $out | ForEach-Object { "$_" } | Add-Content (Join-Path $jh 'close.log') -Encoding utf8
        if ($crc -eq 0) { "$repo`t$pr`t$j`t$([DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mmZ'))" | Add-Content $closed -Encoding utf8; Log "closed $repo !$pr judgment $j" }
        elseif ($crc -eq 6) { Log "close $repo !${pr}: main kept moving; retry next tick" }
        else { $why = (@($out | ForEach-Object { "$_" } | Where-Object { $_ }) | Select-Object -Last 1); "$repo`t$pr`t$j`texit ${crc}: $why" | Add-Content $failed -Encoding utf8; Log "close FAILED $repo !$pr exit $crc" }
    }
}
Log 'tick end'
exit $(if ($rc -ne 0) { $rc } elseif ($pendingFailed) { 1 } else { 0 })
