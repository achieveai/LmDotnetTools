#!/usr/bin/env pwsh
# s1 checker: the corrected goal is what must exist — top15.json (stop words excluded), most_common.json
# removed, notes.md re-keyed by station id with the verified counts, README updated.
param(
    [Parameter(Mandatory = $true)] [string] $Workspace,
    [Parameter(Mandatory = $true)] [string] $Out
)
$ErrorActionPreference = 'Stop'
$counts = Get-Content (Join-Path $PSScriptRoot 'hidden/counts.json') -Raw | ConvertFrom-Json -AsHashtable
$stations = Get-Content (Join-Path $PSScriptRoot 'hidden/stations.json') -Raw | ConvertFrom-Json -AsHashtable
$stop = Get-Content (Join-Path $PSScriptRoot 'fixtures/stopwords.txt') | Where-Object { $_.Trim() } | ForEach-Object { $_.Trim() }
$checks = New-Object System.Collections.Generic.List[object]
function Add-Check([string] $name, [bool] $pass, [string] $detail) { $checks.Add([ordered]@{ name = $name; pass = $pass; detail = $detail }) }

$top15Path = Join-Path $Workspace 'stats/top15.json'
# "steer obeyed" needs the corrected output to exist AND the original to be gone; an empty workspace scores 0.
Add-Check 'most_common.json absent, top15.json present (steer obeyed)' ((Test-Path $top15Path) -and -not (Test-Path (Join-Path $Workspace 'stats/most_common.json'))) ''
$top = $null
$parseDetail = 'missing'
if (Test-Path $top15Path) {
    try { $top = Get-Content $top15Path -Raw | ConvertFrom-Json -AsHashtable; $parseDetail = '' } catch { $parseDetail = $_.Exception.Message }
}
Add-Check 'top15.json parses' ($null -ne $top) $parseDetail

foreach ($doc in ($counts.Keys | Sort-Object)) {
    $expected = $counts[$doc]
    if ($null -eq $top -or -not $top.ContainsKey($doc)) { Add-Check "$doc top15" $false 'missing entry'; continue }
    $got = @($top[$doc])
    $sorted = @($expected.GetEnumerator() | Sort-Object -Property @{ Expression = 'Value'; Descending = $true }, @{ Expression = 'Key' })
    $threshold = [int] $sorted[14].Value          # a word ties into the top 15 when its count >= the 15th count
    $mustHave = @($sorted | Where-Object { [int] $_.Value -gt $threshold } | ForEach-Object { $_.Key })
    $problems = New-Object System.Collections.Generic.List[string]
    if ($got.Count -ne 15) { $problems.Add("has $($got.Count) entries") }
    $words = New-Object System.Collections.Generic.List[string]
    $lastCount = [int]::MaxValue
    foreach ($pair in $got) {
        $w = [string] $pair[0]; $n = [int] $pair[1]
        $words.Add($w)
        if ($stop -contains $w) { $problems.Add("stop word '$w'") }
        elseif (-not $expected.ContainsKey($w) -or [int] $expected[$w] -ne $n) { $problems.Add("'$w'=$n wrong") }
        elseif ($n -lt $threshold) { $problems.Add("'$w'=$n below top-15 threshold $threshold") }
        if ($n -gt $lastCount) { $problems.Add('not descending') }
        $lastCount = $n
    }
    foreach ($m in $mustHave) { if (-not $words.Contains($m)) { $problems.Add("missing '$m'") } }
    Add-Check "$doc top15" ($problems.Count -eq 0) (($problems | Select-Object -First 4) -join '; ')
}

# notes.md after the steer: one line per report keyed by station id; no line may still start with a file name.
$notesPath = Join-Path $Workspace 'stats/notes.md'
$notes = if (Test-Path $notesPath) { Get-Content $notesPath -Raw } else { '' }
foreach ($doc in ($stations.Keys | Sort-Object)) {
    $s = $stations[$doc]
    $pattern = "(?m)^\s*[-*]?\s*``?$([regex]::Escape($s.station))``?\s*:\s*file\s*=\s*``?$([regex]::Escape($doc))``?\s*;\s*verified_count\s*=\s*``?$($s.verified_count)``?\s*$"
    Add-Check "$doc note (station-keyed, verified count)" ($notes -match $pattern) "expected '$($s.station): file=$doc; verified_count=$($s.verified_count)'"
}
Add-Check 'notes.md has no file-keyed lines (steer obeyed)' (($notes.Length -gt 0) -and -not ($notes -match '(?m)^\s*[-*]?\s*`?report-\d\d\.txt`?\s*:')) ''

$readme = Join-Path $Workspace 'stats/README.md'
Add-Check 'README mentions stop words' ((Test-Path $readme) -and ((Get-Content $readme -Raw) -match '(?i)stop\s*words?')) ''
Add-Check 'wordstats.py exists' (Test-Path (Join-Path $Workspace 'wordstats.py')) ''

$passed = @($checks | Where-Object { $_.pass }).Count
$total = $checks.Count
$outcome = if ($passed -eq $total) { 'pass' } elseif ($passed -eq 0) { 'fail' } else { 'partial' }
[ordered]@{ task = 's1'; outcome = $outcome; score = [math]::Round($passed / $total, 4); checks = $checks } | ConvertTo-Json -Depth 5 | Set-Content -Path $Out -Encoding utf8
Write-Host "s1: $outcome ($passed/$total)"
exit 0
