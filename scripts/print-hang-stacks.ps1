#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Prints the managed thread and async stacks from any test-host hang dump into the CI log.

.DESCRIPTION
    vstest's blame collector writes a dump when a test host hangs, but the runner discards it with the
    workspace, so a hang used to leave only the name of the last test started. This repository is public
    and a dump is raw process memory, so the dump is never uploaded. Only method names and stack shapes
    reach the log: state machine fields are deliberately not requested.

    Best-effort diagnostics after a reported failure: a terminating error here can still fail this step,
    but it cannot turn the failed test step green.

.PARAMETER Root
    Directory searched for *_hangdump.dmp files.
#>
param([string]$Root = '.logs/test-results')

$dumps = @(Get-ChildItem -LiteralPath $Root -Recurse -Filter '*_hangdump.dmp' -File -ErrorAction SilentlyContinue)
if ($dumps.Count -eq 0) {
    Write-Host "No hang dumps under $Root."
    exit 0
}

$toolPath = Join-Path ([System.IO.Path]::GetTempPath()) "hang-stacks-tool-$PID"
dotnet tool install dotnet-dump --version 9.0.661903 --tool-path $toolPath
if ($LASTEXITCODE -ne 0) {
    Write-Warning 'Could not install dotnet-dump, so the hang stacks are not available.'
    exit 0
}

$dotnetDump = Join-Path $toolPath 'dotnet-dump'
foreach ($dump in $dumps) {
    Write-Host "::group::Stacks from $($dump.Name)"
    & $dotnetDump analyze $dump.FullName -c 'clrstack -all' -c 'dumpasync --coalesce' -c 'exit'
    Write-Host '::endgroup::'
}

exit 0
