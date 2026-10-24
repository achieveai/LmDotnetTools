#requires -Version 7.0
[CmdletBinding()]
param(
    [string]$PackageDirectory = "out",
    [string]$Source = "https://api.nuget.org/v3/index.json",
    [string]$ApiKey = $env:NUGET_API_KEY
)
$ErrorActionPreference = "Stop"
$packages = @(Get-ChildItem -LiteralPath $PackageDirectory -File -Filter "*.nupkg" | Where-Object Name -notlike "*.symbols.nupkg" | Sort-Object Name)
if ($packages.Count -eq 0) { throw "No NuGet packages were produced in $PackageDirectory." }
if (-not $ApiKey) { throw "NUGET_API_KEY is required to publish packages." }
foreach ($package in $packages) {
    Write-Host "Pushing $($package.Name)"
    & dotnet nuget push $package.FullName --source $Source --api-key $ApiKey --skip-duplicate
    if ($LASTEXITCODE -ne 0) { throw "NuGet push failed for $($package.Name) (exit $LASTEXITCODE)." }
}
