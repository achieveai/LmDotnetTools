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

# A prose answer may carry the qualifier its own source sentence carries: `09-oncall.md` says "the primary
# on-call for the owning team", and two runs answered exactly that for a key whose expected value is
# "primary on-call". Scoring those wrong measured the checker, not the model. So a non-numeric expected
# value also matches when the answer is that value followed by more words. This is deliberately one-sided
# and it does cost strictness: an answer that states the right role and then contradicts itself now passes.
# Numeric values keep exact equality, or "48" would match "480".
function Matches-Expected([object] $got, [object] $want) {
    $g = Normalize $got
    $w = Normalize $want
    if ($g -eq $w) { return $true }
    if ($w -match '^[\d.]+$') { return $false }
    return $g -match ('^' + [regex]::Escape($w) + ' ')
}

$answersPath = Join-Path $Workspace 'answers.json'
$answers = $null
if (Test-Path $answersPath) { try { $answers = Get-Content $answersPath -Raw | ConvertFrom-Json -AsHashtable } catch { } }
Add-Check 'answers.json parses' ($null -ne $answers) ''
foreach ($key in ($expected.Keys | Sort-Object)) {
    $got = if ($null -ne $answers -and $answers.ContainsKey($key)) { $answers[$key] } else { $null }
    $ok = ($null -ne $got) -and (Matches-Expected $got $expected[$key])
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
