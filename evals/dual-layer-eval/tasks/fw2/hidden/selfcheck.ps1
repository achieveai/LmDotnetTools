#!/usr/bin/env pwsh
# Self-check for fw2: gold scores 1.0, wrong/empty/garbage score 0, trap-only answers score as predicted, requiredFixtures counts match.
& (Join-Path $PSScriptRoot '../../../tools/selfcheck.ps1') -Task 'fw2' @args
exit $LASTEXITCODE
