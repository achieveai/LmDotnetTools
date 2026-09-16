#!/usr/bin/env pwsh
# c2 checker: hidden unittest suite against logstat.py (one check per test), incidents.json vs the hidden
# per-day key (one check per day), plus README and the agent's own tests.
param(
    [Parameter(Mandatory = $true)] [string] $Workspace,
    [Parameter(Mandatory = $true)] [string] $Out
)
$ErrorActionPreference = 'Stop'
$checks = New-Object System.Collections.Generic.List[object]
function Add-Check([string] $name, [bool] $pass, [string] $detail) { $checks.Add([ordered]@{ name = $name; pass = $pass; detail = $detail }) }

$testsDir = Join-Path $Workspace 'tests'
New-Item -ItemType Directory -Force -Path $testsDir | Out-Null
Copy-Item (Join-Path $PSScriptRoot 'hidden/test_logstat.py') (Join-Path $testsDir 'test_logstat_hidden.py') -Force
Copy-Item (Join-Path $PSScriptRoot 'hidden/expected.json') (Join-Path $testsDir 'logstat_expected.json') -Force
if (-not (Test-Path (Join-Path $testsDir '__init__.py'))) { Set-Content -Path (Join-Path $testsDir '__init__.py') -Value '' }

$tests = @('test_status_counts', 'test_top_paths', 'test_errors_by_hour', 'test_slowest', 'test_comment_lines_skipped',
    'test_help_exits_zero', 'test_unknown_command_exits_two', 'test_missing_dir_exits_two', 'test_malformed_line_exits_two')
if (-not (Test-Path (Join-Path $Workspace 'logstat.py'))) {
    foreach ($t in $tests) { Add-Check $t $false 'logstat.py missing' }
} else {
    Push-Location $Workspace
    try {
        foreach ($t in $tests) {
            $output = & python -m unittest "tests.test_logstat_hidden.LogStatTests.$t" 2>&1 | Out-String
            $ok = $LASTEXITCODE -eq 0
            $detail = if ($ok) { '' } else { (($output -split "`n" | Where-Object { $_ -match 'Error|Assertion|FAIL' } | Select-Object -First 2) -join ' | ').Trim() }
            Add-Check $t $ok $detail
        }
    } finally { Pop-Location }
}

# incidents.json: one check per day; the ticket id is compared case-insensitively, the path after trimming.
$key = Get-Content (Join-Path $PSScriptRoot 'hidden/incidents.json') -Raw | ConvertFrom-Json -AsHashtable
$incPath = Join-Path $Workspace 'incidents.json'
$inc = $null
if (Test-Path $incPath) { try { $inc = Get-Content $incPath -Raw | ConvertFrom-Json -AsHashtable } catch { } }
Add-Check 'incidents.json parses' ($null -ne $inc) ''
foreach ($day in ($key.Keys | Sort-Object)) {
    $e = $key[$day]
    $got = if ($null -ne $inc -and $inc.ContainsKey($day) -and $inc[$day] -is [System.Collections.IDictionary]) { $inc[$day] } else { $null }
    $gt = if ($null -ne $got -and $got.ContainsKey('ticket')) { ([string] $got['ticket']).Trim().ToUpperInvariant() } else { '' }
    $gp = if ($null -ne $got -and $got.ContainsKey('path')) { ([string] $got['path']).Trim() } else { '' }
    $ok = ($gt -eq $e.ticket) -and ($gp -eq $e.path)
    Add-Check "incident $day" $ok "expected $($e.ticket) $($e.path), got '$gt' '$gp'"
}

Add-Check 'README.md exists' (Test-Path (Join-Path $Workspace 'README.md')) ''
$own = Join-Path $testsDir 'test_logstat.py'
Add-Check 'own tests exist' ((Test-Path $own) -and ((Get-Content $own -Raw) -match 'unittest')) ''

$passed = @($checks | Where-Object { $_.pass }).Count
$total = $checks.Count
$outcome = if ($passed -eq $total) { 'pass' } elseif ($passed -eq 0) { 'fail' } else { 'partial' }
[ordered]@{ task = 'c2'; outcome = $outcome; score = [math]::Round($passed / $total, 4); checks = $checks } | ConvertTo-Json -Depth 5 | Set-Content -Path $Out -Encoding utf8
Write-Host "c2: $outcome ($passed/$total)"
exit 0
