[CmdletBinding()]
param(
    [switch]$SkipRestore
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
Set-Location $repoRoot

$solution = "LmDotnetTools.sln"
# Test projects that are NOT in $solution and therefore have to be restored, built and run
# separately. Keep this list as short as the repo allows: a project reachable only from here is
# covered by a list entry rather than by a declaration, and #234 is what that costs -- a sample
# stopped compiling, CI printed `error CS...` on every run including on main, and the job stayed
# green because nothing in the gate built it. tests/McpServer.AspNetCore.Tests used to live here
# and no longer does: it, its sample and the library they exercise are all in the solution now
# (#336), so the solution build covers them and repeating them here would only run them twice.
# tests/LmConfig.Tests and tests/Misc.Tests used to live here and no longer do (#312): both are
# in the solution now, so the solution build and test cover them and repeating them here would
# only run them twice.
$extraTestProjects = @()

function Invoke-CiStep {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name,

        [Parameter(Mandatory = $true)]
        [scriptblock]$Command
    )

    Write-Host "::group::$Name"
    try {
        # $ErrorActionPreference = "Stop" governs PowerShell cmdlet errors ONLY; a native
        # executable that exits non-zero sets $LASTEXITCODE and execution simply continues.
        # Every step below is a `dotnet` invocation, so without this check the script ran to
        # completion and exited 0 over failing builds and failing tests, and GitHub rendered
        # the "Build and test" check green. That was live: PR #285 reported success over 74
        # test failures and four `error CS` compile errors.
        # Reset first so a stale code from an earlier step cannot be misattributed to this one.
        $global:LASTEXITCODE = 0
        & $Command
        if ($LASTEXITCODE -ne 0) {
            throw "Step '$Name' failed with exit code $LASTEXITCODE."
        }
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

# Format gate: enforce the repository's canonical CSharpier output over the WHOLE tree before
# building. It checks every tracked .cs file, not just the solution's, because the pre-commit
# hook only ever sees staged files -- a file changed by a merge, a revert, or a --no-verify
# commit reaches main unchecked, and this is the step that catches it. CSharpier is pinned in
# .config/dotnet-tools.json (restored above), so the hook, the editor and this gate all format
# identically. Pairs with the centralized TreatWarningsAsErrors flag so the build step below
# catches any analyzer warning as an error.
Invoke-CiStep "csharpier format verify" {
    dotnet csharpier check .
}

Invoke-CiStep "build solution" {
    dotnet build $solution --no-restore /p:UseSharedCompilation=false /p:RunBrowserE2ETests=false
}

# The Browser E2E suite is COMPILED above but not executed here: it needs Playwright's
# Chromium and a built client SPA, which this job does not provide. It runs for real in
# .github/workflows/playwright-e2e.yml, which is a separate required check. The property
# must match between build and test because of --no-build.
Write-Host "Browser E2E tests are excluded from this solution-wide run (compiled only); they execute in the playwright-e2e workflow."

# --blame-hang-timeout fires a process dump (and names the hung test) if any
# single test exceeds the timeout. Without it, an infinite hang would just
# eat the workflow's outer timeout-minutes budget and report "cancelled"
# instead of pointing at the offending test.
Invoke-CiStep "test solution" {
    dotnet test $solution --no-build --verbosity minimal --blame-hang --blame-hang-timeout 4m /p:RunBrowserE2ETests=false
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
