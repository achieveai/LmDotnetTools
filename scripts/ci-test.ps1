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
    Invoke-CiStep "restore tools" {
        dotnet tool restore
    }

    Invoke-CiStep "restore solution" {
        dotnet restore $solution
    }

    foreach ($project in $extraTestProjects) {
        Invoke-CiStep "restore $project" {
            dotnet restore $project
        }
    }
}

# Format gate: enforce that .editorconfig whitespace rules pass before
# building. Pairs with the centralized TreatWarningsAsErrors flag so the
# build step below catches any analyzer warning as an error.
Invoke-CiStep "format whitespace verify" {
    dotnet format whitespace $solution --verify-no-changes --verbosity diagnostic --no-restore
}

Invoke-CiStep "build solution" {
    dotnet build $solution --no-restore /p:UseSharedCompilation=false
}

# --blame-hang-timeout fires a process dump (and names the hung test) if any
# single test exceeds the timeout. Without it, an infinite hang would just
# eat the workflow's outer timeout-minutes budget and report "cancelled"
# instead of pointing at the offending test.
Invoke-CiStep "test solution" {
    dotnet test $solution --no-build --verbosity minimal --blame-hang --blame-hang-timeout 4m
}

foreach ($project in $extraTestProjects) {
    Invoke-CiStep "test $project" {
        dotnet test $project --no-restore --verbosity minimal /p:UseSharedCompilation=false --blame-hang --blame-hang-timeout 4m
    }
}
