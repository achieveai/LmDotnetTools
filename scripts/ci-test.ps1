[CmdletBinding()]
param(
    [switch]$SkipRestore
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
Set-Location $repoRoot

$solution = "LmDotnetTools.sln"
$extraTestProjects = @(
    "tests/LmConfig.Tests/LmConfig.Tests.csproj",
    "tests/McpServer.AspNetCore.Tests/McpServer.AspNetCore.Tests.csproj",
    "tests/Misc.Tests/Misc.Tests.csproj"
)

function Invoke-CiStep {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name,

        [Parameter(Mandatory = $true)]
        [scriptblock]$Command
    )

    Write-Host "::group::$Name"
    try {
        & $Command
    }
    finally {
        Write-Host "::endgroup::"
    }
}

Invoke-CiStep "dotnet info" {
    dotnet --info
}

if (-not $SkipRestore) {
    Invoke-CiStep "restore solution" {
        dotnet restore $solution
    }

    foreach ($project in $extraTestProjects) {
        Invoke-CiStep "restore $project" {
            dotnet restore $project
        }
    }
}

Invoke-CiStep "build solution" {
    dotnet build $solution --no-restore /p:UseSharedCompilation=false
}

Invoke-CiStep "test solution" {
    dotnet test $solution --no-build --verbosity minimal
}

foreach ($project in $extraTestProjects) {
    Invoke-CiStep "test $project" {
        dotnet test $project --no-restore --verbosity minimal /p:UseSharedCompilation=false
    }
}
