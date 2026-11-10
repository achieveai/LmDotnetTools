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
# The CLI reads its own options only before --, so a child program's --config after it is not ours.
$separator = [Array]::IndexOf([object[]] $cliArgs, '--')
$own = if ($separator -lt 0) { $cliArgs.Count } else { $separator }
if (-not ($cliArgs | Select-Object -First $own | Where-Object { $_ -eq '--config' })) {
    # Insert straight after the command words, so it can never land among exec program arguments.
    $words = 0
    while ($words -lt $own -and -not "$($cliArgs[$words])".StartsWith('-')) { $words++ }
    $config = @('--config', (Join-Path $root 'state\config\appsettings.reviewd.json'))
    $cliArgs = @($cliArgs | Select-Object -First $words) + $config + @($cliArgs | Select-Object -Skip $words)
}
& (Join-Path $root 'cli\Reviewd.Sample.exe') @cliArgs
exit $LASTEXITCODE
