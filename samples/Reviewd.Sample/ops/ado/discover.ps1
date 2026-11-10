#requires -Version 7
# Finds new PR revisions and rewrites state\selection.json; the running batch picks the new file up.
# Scheduled every 2 minutes. Exit 0 when the selection was written, 1 on failure.
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$state = Join-Path $root 'state'
New-Item -ItemType Directory -Force (Join-Path $state 'logs') | Out-Null
$log = Join-Path $state 'logs\discover.log'
$db = Join-Path $state 'reviews.db'
function Log([string] $m) { Add-Content -LiteralPath $log -Value "$([DateTime]::UtcNow.ToString('o')) $m" -Encoding utf8 }
# The task action keeps no stderr, so anything that stops this script must reach the log first.
trap { try { Log "FAILED; selection unchanged: $($_.Exception)`n$($_.ScriptStackTrace)" } catch { }; exit 1 }
$cfg = Get-Content -Raw (Join-Path $state 'config\runner.json') | ConvertFrom-Json
# The batch takes lanes 0-5 and parks on an empty or out-of-range list, while its process still looks alive.
$lanesPerRepo = $cfg.lanesPerRepo
if (-not ($lanesPerRepo -is [long] -or $lanesPerRepo -is [int]) -or $lanesPerRepo -lt 1 -or $lanesPerRepo -gt 6) {
    throw "runner.json lanesPerRepo must be an integer from 1 to 6, not '$lanesPerRepo'."
}

$repos = $cfg.repos -join ','
$check = & (Join-Path $root 'reviewd.ps1') pr check --repos $repos --db $db 2>&1
$checkExit = $LASTEXITCODE
$check | Where-Object { $_ -notmatch '^\{' } | ForEach-Object { Log "check: $_" }
# A repo whose snapshot failed keeps its previous snapshot, so picking still runs on what saved.
if ($checkExit -ne 0) { Log "check exited $checkExit; picking from the snapshots that saved." }

$next = Join-Path $state 'selection.next.json'
$pick = & (Join-Path $root 'reviewd.ps1') pr pick --repos $repos --db $db --max-age-hours $cfg.maxAgeHours --selection-out $next 2>&1
if ($LASTEXITCODE -ne 0) { $pick | ForEach-Object { Log "pick: $_" }; Log 'pick FAILED; selection unchanged.'; exit 1 }
$pick | Where-Object { $_ -notmatch '^\{' } | ForEach-Object { Log "pick: $_" }

# Cap lanes per repo: the gateway is shared with other local work.
$selection = Get-Content -Raw $next | ConvertFrom-Json -AsHashtable
# runner.json excludedLanes parks a lane that an unrecoverable attempt still holds.
foreach ($repo in @($selection['lanes'].Keys)) {
    $skip = @($cfg.excludedLanes.$repo)
    $selection['lanes'][$repo] = @(0..($lanesPerRepo - 1) | Where-Object { $_ -notin $skip })
    if (-not $selection['lanes'][$repo].Count) { throw "runner.json excludedLanes leaves $repo no lane." }
}
$selection | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $next -Encoding utf8NoBOM
$count = @($selection['repositories'].Values | ForEach-Object { $_ }).Count
# One replace-existing rename: the batch watches selection.json, and Move-Item -Force deletes it first,
# so a read in that gap stopped the batch ("Could not find file ... selection.json", 2026-11-03 05:24Z).
# The replace is refused ("Access to the path is denied") while a reader has the file open, so retry briefly.
for ($try = 1; ; $try++) {
    try { [System.IO.File]::Move($next, (Join-Path $state 'selection.json'), $true); break }
    catch { if ($try -ge 20) { throw }; Start-Sleep -Milliseconds 250 }
}
Log "selection written: $count PR(s), $lanesPerRepo lane(s) per repo."
exit 0
