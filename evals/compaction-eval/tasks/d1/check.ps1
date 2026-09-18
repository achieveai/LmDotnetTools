#!/usr/bin/env pwsh
# d1 checker: answers.json vs hidden/expected.json, plus the required scripts and report.
param(
    [Parameter(Mandatory = $true)] [string] $Workspace,
    [Parameter(Mandatory = $true)] [string] $Out
)
$ErrorActionPreference = 'Stop'
$expected = Get-Content (Join-Path $PSScriptRoot 'hidden/expected.json') -Raw | ConvertFrom-Json -AsHashtable
$checks = New-Object System.Collections.Generic.List[object]

function Add-Check([string] $name, [bool] $pass, [string] $detail) {
    $checks.Add([ordered]@{ name = $name; pass = $pass; detail = $detail })
}

$answersPath = Join-Path $Workspace 'answers.json'
$answers = $null
if (Test-Path $answersPath) {
    try { $answers = Get-Content $answersPath -Raw | ConvertFrom-Json -AsHashtable } catch { Add-Check 'answers.json parses' $false $_.Exception.Message }
} else {
    Add-Check 'answers.json exists' $false 'missing'
}

foreach ($key in $expected.Keys) {
    $spec = $expected[$key]
    if ($null -eq $answers -or -not $answers.ContainsKey($key)) { Add-Check $key $false 'missing key'; continue }
    $got = $answers[$key]
    if ($spec.ContainsKey('tolerance')) {
        $num = 0.0
        $ok = [double]::TryParse([string] $got, [ref] $num) -and [math]::Abs($num - [double] $spec.value) -le [double] $spec.tolerance
        Add-Check $key $ok "expected $($spec.value) ± $($spec.tolerance), got $got"
    } else {
        $ok = [string] $got -eq [string] $spec.value
        Add-Check $key $ok "expected $($spec.value), got $got"
    }
}

foreach ($f in 'scripts/diamonds.py', 'scripts/penguins.py', 'scripts/flights.py', 'scripts/tips.py', 'scripts/combine.py', 'report.md', 'notes.md') {
    Add-Check "file $f" (Test-Path (Join-Path $Workspace $f)) ''
}

$passed = @($checks | Where-Object { $_.pass }).Count
$total = $checks.Count
$outcome = if ($passed -eq $total) { 'pass' } elseif ($passed -eq 0) { 'fail' } else { 'partial' }
$score = [ordered]@{ task = 'd1'; outcome = $outcome; score = [math]::Round($passed / $total, 4); checks = $checks }
$score | ConvertTo-Json -Depth 5 | Set-Content -Path $Out -Encoding utf8
Write-Host "d1: $outcome ($passed/$total)"
exit 0
