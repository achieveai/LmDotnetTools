[CmdletBinding()]
param(
    [switch]$SkipRestore,

    [string[]]$ChangedPath = @(),

    [string]$ChangedPathFile
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
Set-Location $repoRoot

$timingPath = Join-Path $repoRoot ".logs\ci-phase-timings.ndjson"
$timingDirectory = Split-Path $timingPath -Parent
$timingRunId = [guid]::NewGuid().ToString("N")
New-Item -ItemType Directory -Path $timingDirectory -Force | Out-Null
try {
    Remove-Item $timingPath -Force -ErrorAction Stop
}
catch [System.Management.Automation.ItemNotFoundException] {
    # A first run has no prior timing artifact to remove.
}
catch {
    Write-Warning "Could not reset CI telemetry; prior rows may remain, and rows written by this run carry runId '$timingRunId': $($_.Exception.Message)"
}

$solution = "LmDotnetTools.sln"
if (-not [string]::IsNullOrWhiteSpace($ChangedPathFile)) {
    try {
        $ChangedPath = @(Get-Content -Path $ChangedPathFile -ErrorAction Stop | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    }
    catch {
        Write-Warning "Could not read changed paths for test-impact shadow; full tests remain authoritative: $($_.Exception.Message)"
        $ChangedPath = @()
    }
}

$shadowSelectionPath = Join-Path $repoRoot ".logs\test-impact-shadow.json"
try {
    Remove-Item $shadowSelectionPath -Force -ErrorAction Stop
}
catch [System.Management.Automation.ItemNotFoundException] {
    # A run without prior shadow telemetry has nothing to remove.
}
catch {
    Write-Warning "Could not reset test-impact shadow telemetry; the current full test gate remains authoritative: $($_.Exception.Message)"
}
if ($ChangedPath.Count -gt 0) {
    try {
        $solutionProjects = @(dotnet sln $solution list | Where-Object { $_ -match '\.csproj$' })
        if ($LASTEXITCODE -ne 0 -or $solutionProjects.Count -eq 0) {
            throw "Could not enumerate projects in $solution."
        }
        $shadowSelectionJson = & (Join-Path $PSScriptRoot "select-impacted-tests.ps1") -RepositoryRoot $repoRoot -ChangedPath $ChangedPath -SolutionProject $solutionProjects
        Set-Content -Path $shadowSelectionPath -Value $shadowSelectionJson -Encoding utf8 -ErrorAction Stop
        $shadowSelection = $shadowSelectionJson | ConvertFrom-Json
        Write-Host "Test impact shadow: $($shadowSelection.mode) ($($shadowSelection.reason)); selected $(@($shadowSelection.selectedProjects).Count)/$($shadowSelection.graph.testProjectCount) test projects. Full tests remain authoritative."
    }
    catch {
        Write-Warning "Test impact shadow selection failed; full tests remain authoritative: $($_.Exception.Message)"
    }
}
else {
    Write-Host "Test impact shadow: no changed paths supplied; full tests remain authoritative."
}

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
        [ValidateSet("info", "restore", "format", "build", "test", "pack")]
        [string]$Phase,

        [Parameter(Mandatory = $true)]
        [scriptblock]$Command
    )

    Write-Host "::group::$Name"
    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    $outcome = "passed"
    $exitCode = 0
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
        $exitCode = $LASTEXITCODE
        if ($exitCode -ne 0) {
            throw "Step '$Name' failed with exit code $exitCode."
        }
    }
    catch {
        $outcome = "failed"
        if ($exitCode -eq 0 -and $LASTEXITCODE -ne 0) {
            $exitCode = $LASTEXITCODE
        }
        throw
    }
    finally {
        $stopwatch.Stop()
        $record = [ordered]@{
            runId = $timingRunId
            timestampUtc = [datetime]::UtcNow.ToString("O")
            phase = $Phase
            step = $Name
            outcome = $outcome
            exitCode = if ($outcome -eq "failed" -and $exitCode -eq 0) { $null } else { $exitCode }
            elapsedSeconds = [math]::Round($stopwatch.Elapsed.TotalSeconds, 3)
        }
        try {
            Add-Content -Path $timingPath -Value ($record | ConvertTo-Json -Compress) -Encoding utf8 -ErrorAction Stop
        }
        catch {
            Write-Warning "CI telemetry write failed; the '$Name' outcome remains authoritative: $($_.Exception.Message)"
        }
        Write-Host ("CI timing: {0} [{1}] {2:N3}s ({3})" -f $Name, $Phase, $stopwatch.Elapsed.TotalSeconds, $outcome)
        Write-Host "::endgroup::"
    }
}

Invoke-CiStep "dotnet info" "info" {
    dotnet --info
}

# Deliberately OUTSIDE the -SkipRestore guard. -SkipRestore exists to skip the expensive NuGet
# package restore, but the format gate below invokes CSharpier as a LOCAL tool, so a tree that
# never ran `dotnet tool restore` fails that gate with a missing-command error rather than a
# formatting verdict. Restoring the manifest is two tools and is idempotent, so it costs a
# skipped run almost nothing and keeps `./scripts/ci-test.ps1 -SkipRestore` -- the command
# CONTRIBUTING.md tells contributors to use -- working.
Invoke-CiStep "restore tools" "restore" {
    dotnet tool restore
}

if (-not $SkipRestore) {
    Invoke-CiStep "restore solution" "restore" {
        dotnet restore $solution
    }

    foreach ($project in $extraTestProjects) {
        Invoke-CiStep "restore $project" "restore" {
            dotnet restore $project
        }
    }
}

# Format gate: enforce the repository's canonical CSharpier output over the WHOLE tree before
# building. It checks every tracked .cs file, not just the solution's, because the pre-commit
# hook only ever sees staged files -- a file changed by a merge, a revert, or a --no-verify
# commit reaches main unchecked, and this is the step that catches it. CSharpier is pinned in
# .config/dotnet-tools.json and restored unconditionally above, so the hook, the editor and this
# gate all format identically. Pairs with the centralized TreatWarningsAsErrors flag so the build
# step below catches any analyzer warning as an error.
Invoke-CiStep "csharpier format verify" "format" {
    dotnet csharpier check .
}

Invoke-CiStep "build solution" "build" {
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
Invoke-CiStep "test solution" "test" {
    dotnet test $solution --no-build --verbosity minimal --blame-hang --blame-hang-timeout 4m /p:RunBrowserE2ETests=false
}

foreach ($project in $extraTestProjects) {
    Invoke-CiStep "test $project" "test" {
        dotnet test $project --no-restore --verbosity minimal /p:UseSharedCompilation=false --blame-hang --blame-hang-timeout 4m
    }
}

# Package smoke: pack the publicly shipped Sandbox SDK and assert the nupkg
# carries BOTH target frameworks. Catches packaging regressions (a dropped TFM,
# a broken PackageReadmeFile, a NU5xxx error) on every PR without needing a
# live gateway or Docker — the auth-enforced contract job (sandbox-contract.yml)
# covers the runtime behavior separately.
Invoke-CiStep "pack smoke: Sandbox SDK" "pack" {
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
