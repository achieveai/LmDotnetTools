#requires -Version 7
# Runs the Reviewd CLI with this runner's secrets (state\config\cli.env) and settings
# (state\config\appsettings.reviewd.json). Usage: .\reviewd.ps1 pr check --repos owner/repo --db ...
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
foreach ($line in Get-Content -LiteralPath (Join-Path $root 'state\config\cli.env')) {
    if ($line -match '^\s*([A-Za-z0-9_]+)=(.*)$') { [Environment]::SetEnvironmentVariable($matches[1], $matches[2], 'Process') }
}
$env:ASPNETCORE_ENVIRONMENT = 'Production'
$env:DOTNET_ENVIRONMENT = 'Production'
$cliArgs = @($args)
# PowerShell drops a bare -- from a script's arguments; quote it ('--') before exec program arguments.
if ($cliArgs -notcontains '--config') {
    # Insert straight after the command words, so it can never land among exec program arguments.
    $words = 0
    while ($words -lt $cliArgs.Count -and -not "$($cliArgs[$words])".StartsWith('-')) { $words++ }
    $config = @('--config', (Join-Path $root 'state\config\appsettings.reviewd.json'))
    $cliArgs = @($cliArgs[0..($words - 1)]) + $config + @(if ($words -lt $cliArgs.Count) { $cliArgs[$words..($cliArgs.Count - 1)] })
}
& (Join-Path $root 'cli\Reviewd.Sample.exe') @cliArgs
exit $LASTEXITCODE
