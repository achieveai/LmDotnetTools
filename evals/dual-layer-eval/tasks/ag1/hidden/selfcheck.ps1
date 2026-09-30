#!/usr/bin/env pwsh
# Self-check for ag1: gold scores 1.0, wrong/empty/garbage score 0, requiredFixtures counts match.
& (Join-Path $PSScriptRoot '../../../tools/selfcheck.ps1') -Task 'ag1' @args
exit $LASTEXITCODE
