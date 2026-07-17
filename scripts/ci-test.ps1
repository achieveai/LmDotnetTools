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

# Package smoke: pack the publicly shipped Sandbox SDK and assert the nupkg
# carries BOTH target frameworks. Catches packaging regressions (a dropped TFM,
# a broken PackageReadmeFile, a NU5xxx error) on every PR without needing a
# live gateway or Docker — the auth-enforced contract job (sandbox-contract.yml)
# covers the runtime behavior separately.
Invoke-CiStep "pack smoke: Sandbox SDK" {
    $sandboxProject = "src/Sandbox/AchieveAi.LmDotnetTools.Sandbox.csproj"
    $packOut = Join-Path ([System.IO.Path]::GetTempPath()) "sandbox-pack-smoke"
    if (Test-Path $packOut) { Remove-Item $packOut -Recurse -Force }

    dotnet pack $sandboxProject -c Release -o $packOut --no-restore /p:UseSharedCompilation=false
    if ($LASTEXITCODE -ne 0) { throw "dotnet pack failed for $sandboxProject (exit $LASTEXITCODE)." }

    $nupkg = Get-ChildItem $packOut -Filter "AchieveAi.LmDotnetTools.Sandbox.*.nupkg" |
        Where-Object { $_.Name -notlike "*.symbols.nupkg" } |
        Select-Object -First 1
    if (-not $nupkg) { throw "No AchieveAi.LmDotnetTools.Sandbox nupkg produced under $packOut." }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [System.IO.Compression.ZipFile]::OpenRead($nupkg.FullName)
    try {
        $entries = $zip.Entries.FullName
    }
    finally {
        $zip.Dispose()
    }

    foreach ($tfm in @("net8.0", "net9.0")) {
        $dll = "lib/$tfm/AchieveAi.LmDotnetTools.Sandbox.dll"
        if ($entries -notcontains $dll) {
            throw "Sandbox nupkg $($nupkg.Name) is missing $dll. Present lib entries: $(( $entries | Where-Object { $_ -like 'lib/*' } ) -join ', ')"
        }
    }
    Write-Host "Sandbox package verified: $($nupkg.Name) contains net8.0 + net9.0."
}
