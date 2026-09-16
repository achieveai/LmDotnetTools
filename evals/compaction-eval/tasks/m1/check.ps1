#!/usr/bin/env pwsh
# m1 checker: each worker's config.txt carries the value only the lead knew; the lead's summary lists all three.
param(
    [Parameter(Mandatory = $true)] [string] $Workspace,
    [Parameter(Mandatory = $true)] [string] $Out,
    [string] $Seed = ''
)
$ErrorActionPreference = 'Stop'
$checks = New-Object System.Collections.Generic.List[object]
function Add-Check([string] $name, [bool] $pass, [string] $detail) { $checks.Add([ordered]@{ name = $name; pass = $pass; detail = $detail }) }

# The seed is read from the workspace when not given: summary.md or any config.txt names the build tag.
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
        $ok = $line -eq "$($e.key)=$($e.value)"
        Add-Check "$name config value (asked the lead)" $ok "expected '$($e.key)=$($e.value)', got '$line'"
    } else {
        Add-Check "$name config value (asked the lead)" $false 'config.txt missing'
    }
    $plan = Join-Path $dir 'plan.md'
    Add-Check "$name plan.md has 5 steps" ((Test-Path $plan) -and (([regex]::Matches((Get-Content $plan -Raw), '(?m)^\s*\d+[.)]')).Count -ge 5)) ''
    Add-Check "$name checklist.md" (Test-Path (Join-Path $dir 'checklist.md')) ''
}

$summary = Join-Path $Workspace 'summary.md'
$summaryText = if (Test-Path $summary) { Get-Content $summary -Raw } else { '' }
foreach ($name in $expected.Keys) {
    $e = $expected[$name]
    Add-Check "summary lists $name" ($summaryText -match [regex]::Escape("$($e.key)=$($e.value)")) ''
}

$plan = Join-Path $Workspace 'release-plan.md'
$planText = if (Test-Path $plan) { Get-Content $plan -Raw } else { '' }
$sections = @('Scope', 'Risks', 'Rollout', 'Rollback', 'Communication', 'Sign-off') | Where-Object { $planText -match "(?im)^#+\s*$([regex]::Escape($_))" }
Add-Check 'release-plan.md has 6 sections' ($sections.Count -eq 6) "found $($sections.Count)"
Add-Check 'release-plan.md >= 900 words' (($planText -split '\s+' | Where-Object { $_ }).Count -ge 900) ''

$passed = @($checks | Where-Object { $_.pass }).Count
$total = $checks.Count
$outcome = if ($passed -eq $total) { 'pass' } elseif ($passed -eq 0) { 'fail' } else { 'partial' }
[ordered]@{ task = 'm1'; outcome = $outcome; score = [math]::Round($passed / $total, 4); checks = $checks } | ConvertTo-Json -Depth 5 | Set-Content -Path $Out -Encoding utf8
Write-Host "m1: $outcome ($passed/$total)"
exit 0
