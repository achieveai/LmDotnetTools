#!/usr/bin/env pwsh
# m1 checker: each worker's config.txt carries the value only the lead knew; gates.md names the active gate per
# spec; the release plan has its six sections and cites every gate; summary.md lists all three values and the
# workers' handoff codes (not the void ones).
param(
    [Parameter(Mandatory = $true)] [string] $Workspace,
    [Parameter(Mandatory = $true)] [string] $Out,
    [string] $Seed = ''
)
$ErrorActionPreference = 'Stop'
$keys = Get-Content (Join-Path $PSScriptRoot 'hidden/keys.json') -Raw | ConvertFrom-Json -AsHashtable
$checks = New-Object System.Collections.Generic.List[object]
function Add-Check([string] $name, [bool] $pass, [string] $detail) { $checks.Add([ordered]@{ name = $name; pass = $pass; detail = $detail }) }
function Read-Text([string] $path) { if (Test-Path $path) { Get-Content $path -Raw } else { '' } }

# The seed is read from the workspace when not given: the pipeline config.txt names the build tag.
if (-not $Seed) {
    $tagFile = Join-Path $Workspace 'workstreams/pipeline/config.txt'
    if (Test-Path $tagFile) { $m = [regex]::Match((Get-Content $tagFile -Raw), 'r-([A-Za-z0-9]+)-7'); if ($m.Success) { $Seed = $m.Groups[1].Value } }
}
$expected = [ordered]@{
    gateway  = @{ key = 'port'; value = '8443' }
    storage  = @{ key = 'region'; value = 'eu-west-2' }
    pipeline = @{ key = 'build_tag'; value = "r-$Seed-7" }
}

foreach ($name in $expected.Keys) {
    $e = $expected[$name]
    $dir = Join-Path $Workspace "workstreams/$name"
    $cfg = Join-Path $dir 'config.txt'
    if (Test-Path $cfg) {
        $line = ((Get-Content $cfg) | Where-Object { $_.Trim() } | Select-Object -First 1).Trim()
        Add-Check "$name config value (asked the lead)" ($line -eq "$($e.key)=$($e.value)") "expected '$($e.key)=$($e.value)', got '$line'"
    } else {
        Add-Check "$name config value (asked the lead)" $false 'config.txt missing'
    }
    $plan = Join-Path $dir 'plan.md'
    Add-Check "$name plan.md has 5 steps" ((Test-Path $plan) -and (([regex]::Matches((Read-Text $plan), '(?m)^\s*\d+[.)]')).Count -ge 5)) ''
    $report = Read-Text (Join-Path $dir 'report.md')
    $reportWords = ($report -split '\s+' | Where-Object { $_ }).Count
    Add-Check "$name report.md >= 400 words" ($reportWords -ge 400) "found $reportWords"
}

# gates.md: one line per spec naming the active gate; a line that names a retired gate for that file fails.
$gatesText = Read-Text (Join-Path $Workspace 'gates.md')
foreach ($file in ($keys.gates.Keys | Sort-Object)) {
    $g = $keys.gates[$file]
    $line = [regex]::Match($gatesText, "(?m)^\s*[-*]?\s*``?$([regex]::Escape($file))``?\s*:(.*)$")
    $ok = $false
    $detail = "expected $($g.gate)"
    if ($line.Success) {
        $rest = $line.Groups[1].Value
        $names = $rest -match "\b$([regex]::Escape($g.gate))\b"
        $retiredHit = @($g.retired | Where-Object { $rest -match "\b$([regex]::Escape($_))\b" })
        $ok = $names -and ($retiredHit.Count -eq 0)
        if (-not $ok) { $detail += ", line: " + $rest.Trim().Substring(0, [math]::Min(60, $rest.Trim().Length)) }
    } else { $detail += ', no line for this file' }
    Add-Check "gates.md $file" $ok $detail
}

$summaryText = Read-Text (Join-Path $Workspace 'summary.md')
foreach ($name in $expected.Keys) {
    $e = $expected[$name]
    Add-Check "summary lists $name value" ($summaryText -match [regex]::Escape("$($e.key)=$($e.value)")) ''
    $h = $keys.handoffs[$name]
    $ok = ($summaryText -match [regex]::Escape($h.code)) -and -not ($summaryText -match [regex]::Escape($h.void))
    Add-Check "summary carries $name handoff code (not the void one)" $ok "expected $($h.code)"
}

$planText = Read-Text (Join-Path $Workspace 'release-plan.md')
$sections = @('Scope', 'Risks', 'Rollout', 'Rollback', 'Communication', 'Sign-off') | Where-Object { $planText -match "(?im)^#+\s*$([regex]::Escape($_))" }
Add-Check 'release-plan.md has 6 sections' ($sections.Count -eq 6) "found $($sections.Count)"
Add-Check 'release-plan.md >= 900 words' (($planText -split '\s+' | Where-Object { $_ }).Count -ge 900) ''
$cited = @($keys.gates.Values | ForEach-Object { $_.gate } | Where-Object { $planText -match "\b$([regex]::Escape($_))\b" })
Add-Check 'release-plan.md cites all 8 active gates' ($cited.Count -eq 8) "cited $($cited.Count)/8"

$passed = @($checks | Where-Object { $_.pass }).Count
$total = $checks.Count
$outcome = if ($passed -eq $total) { 'pass' } elseif ($passed -eq 0) { 'fail' } else { 'partial' }
[ordered]@{ task = 'm1'; outcome = $outcome; score = [math]::Round($passed / $total, 4); checks = $checks } | ConvertTo-Json -Depth 5 | Set-Content -Path $Out -Encoding utf8
Write-Host "m1: $outcome ($passed/$total)"
exit 0
