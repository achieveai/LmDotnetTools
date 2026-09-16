#!/usr/bin/env pwsh
# r1 checker: answers.json vs hidden key (normalised strings), plus a sources.md line per key.
param(
    [Parameter(Mandatory = $true)] [string] $Workspace,
    [Parameter(Mandatory = $true)] [string] $Out
)
$ErrorActionPreference = 'Stop'
$expected = Get-Content (Join-Path $PSScriptRoot 'hidden/expected.json') -Raw | ConvertFrom-Json -AsHashtable
$checks = New-Object System.Collections.Generic.List[object]
function Add-Check([string] $name, [bool] $pass, [string] $detail) { $checks.Add([ordered]@{ name = $name; pass = $pass; detail = $detail }) }
function Normalize([object] $v) { ([string] $v).Trim().ToLowerInvariant() -replace '[\s_-]+', ' ' -replace '(\d)\s*(ms|minutes?|days?|percent|%)$', '$1' }

$answersPath = Join-Path $Workspace 'answers.json'
$answers = $null
if (Test-Path $answersPath) { try { $answers = Get-Content $answersPath -Raw | ConvertFrom-Json -AsHashtable } catch { } }
Add-Check 'answers.json parses' ($null -ne $answers) ''
foreach ($key in ($expected.Keys | Sort-Object)) {
    $got = if ($null -ne $answers -and $answers.ContainsKey($key)) { $answers[$key] } else { $null }
    $ok = ($null -ne $got) -and ((Normalize $got) -eq (Normalize $expected[$key]))
    Add-Check $key $ok "expected '$($expected[$key])', got '$got'"
}
$sources = Join-Path $Workspace 'sources.md'
$sourcesText = if (Test-Path $sources) { Get-Content $sources -Raw } else { '' }
$cited = @($expected.Keys | Where-Object { $sourcesText -match "(?m)^\s*-?\s*``?$([regex]::Escape($_))``?\s*:.*\.md" })
Add-Check 'sources.md cites a page per key' ($cited.Count -eq $expected.Count) "cited $($cited.Count)/$($expected.Count)"

$passed = @($checks | Where-Object { $_.pass }).Count
$total = $checks.Count
$outcome = if ($passed -eq $total) { 'pass' } elseif ($passed -eq 0) { 'fail' } else { 'partial' }
[ordered]@{ task = 'r1'; outcome = $outcome; score = [math]::Round($passed / $total, 4); checks = $checks } | ConvertTo-Json -Depth 5 | Set-Content -Path $Out -Encoding utf8
Write-Host "r1: $outcome ($passed/$total)"
exit 0
