#!/usr/bin/env pwsh
# c1 checker: hidden unittest file copied into the workspace, one check per test, plus README presence.
param(
    [Parameter(Mandatory = $true)] [string] $Workspace,
    [Parameter(Mandatory = $true)] [string] $Out
)
$ErrorActionPreference = 'Stop'
$checks = New-Object System.Collections.Generic.List[object]
function Add-Check([string] $name, [bool] $pass, [string] $detail) {
    $checks.Add([ordered]@{ name = $name; pass = $pass; detail = $detail })
}

$testsDir = Join-Path $Workspace 'tests'
New-Item -ItemType Directory -Force -Path $testsDir | Out-Null
Copy-Item (Join-Path $PSScriptRoot 'hidden/test_csvagg.py') (Join-Path $testsDir 'test_csvagg_hidden.py') -Force

$names = 'test_group_sum_count_first_seen_order', 'test_avg_skips_non_numeric_and_rounds', 'test_avg_of_no_numeric_values_is_null', 'test_where_min_max', 'test_sort_desc_top', 'test_errors_exit_2_with_empty_stdout', 'test_help_exits_zero'
if (-not (Test-Path (Join-Path $Workspace 'csvagg.py'))) {
    foreach ($n in $names) { Add-Check $n $false 'csvagg.py missing' }
} else {
    foreach ($n in $names) {
        $proc = Start-Process -FilePath 'python' -ArgumentList @('-m', 'unittest', "tests.test_csvagg_hidden.CsvAggTests.$n") -WorkingDirectory $Workspace -Wait -PassThru -NoNewWindow -RedirectStandardError (Join-Path $env:TEMP "c1-$n.err")
        $err = Get-Content (Join-Path $env:TEMP "c1-$n.err") -Raw -ErrorAction SilentlyContinue
        $ok = $proc.ExitCode -eq 0
        $detail = if ($ok) { 'ok' } else { ($err -split "`n" | Where-Object { $_ -match 'Error|assert|Assertion' } | Select-Object -First 2) -join ' | ' }
        Add-Check $n $ok $detail
    }
}
Add-Check 'file README.md' (Test-Path (Join-Path $Workspace 'README.md')) ''
Add-Check 'own tests written' ((Get-ChildItem $testsDir -Filter 'test_*.py' | Where-Object { $_.Name -ne 'test_csvagg_hidden.py' }).Count -gt 0) ''

$passed = @($checks | Where-Object { $_.pass }).Count
$total = $checks.Count
$outcome = if ($passed -eq $total) { 'pass' } elseif ($passed -eq 0) { 'fail' } else { 'partial' }
[ordered]@{ task = 'c1'; outcome = $outcome; score = [math]::Round($passed / $total, 4); checks = $checks } | ConvertTo-Json -Depth 5 | Set-Content -Path $Out -Encoding utf8
Write-Host "c1: $outcome ($passed/$total)"
exit 0
