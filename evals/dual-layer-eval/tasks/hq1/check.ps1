#!/usr/bin/env pwsh
# Scores answers.json against hidden/key.json with the suite's shared checker (tools/Score-Answers.ps1).
# Exit 0 when judged, 2 when it could not judge. No LLM.
param(
    [Parameter(Mandatory = $true)] [string] $Workspace,
    [Parameter(Mandatory = $true)] [string] $Out
)
$scorer = Join-Path $PSScriptRoot '../../tools/Score-Answers.ps1'
if (-not (Test-Path -LiteralPath $scorer)) { [Console]::Error.WriteLine("shared checker missing: $scorer"); exit 2 }
& $scorer -Workspace $Workspace -Out $Out -KeyPath (Join-Path $PSScriptRoot 'hidden/key.json')
exit $LASTEXITCODE
