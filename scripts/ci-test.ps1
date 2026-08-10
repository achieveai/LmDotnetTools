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

# NUL gate. Runs before restore because it is the cheapest step here and its failure should not
# wait out a build.
#
# A single raw NUL byte in a file git treats as text has two consequences, and neither announces
# itself:
#   1. git renders every diff of that file as `Bin <n> -> <m> bytes`. The change becomes
#      unreviewable as text. This is not hypothetical - Workspace/ReviewSlot.cs grew ~10 KB landing
#      the untrusted-PR mount gate and shipped as `Bin 3366 -> 13085 bytes`, so not one line of a
#      security seam could be read in review.
#   2. `grep` silently declines to match it. Our shell shims ugrep -I, so the file returns rc=0 with
#      no output - indistinguishable from a genuine zero. A `-l` sweep still lists the file while a
#      follow-up `-n` sweep finds nothing, which reads as "declared but never implemented."
#
# `*.cs text` in .gitattributes fixes (1) permanently by removing git's content-sniffing step. It
# cannot fix (2): only the absence of the byte does that, and nothing else enforces it. Hence this.
#
# The discriminator is git's OWN `check-attr`, not a second extension list that would drift from
# .gitattributes: a path declared binary there is skipped, and everything else - declared text, or
# left to `* text=auto` - must be NUL-free. A genuinely binary file that .gitattributes has not
# declared therefore trips this gate, which is the correct outcome: undeclared means git is
# sniffing, and sniffing is the hazard.
Invoke-CiStep "no NUL bytes in tracked text files" {
    # quotepath=false keeps non-ASCII paths usable rather than C-escaped.
    $tracked = @(& git -c core.quotepath=false ls-files)
    if ($LASTEXITCODE -ne 0) { throw "git ls-files failed (exit $LASTEXITCODE)." }

    # One process for every path; check-attr answers in the order it is asked. Matching on the
    # line's suffix rather than splitting on ':' keeps paths containing a colon unambiguous.
    $attrs = @($tracked | & git check-attr text --stdin)
    if ($LASTEXITCODE -ne 0) { throw "git check-attr failed (exit $LASTEXITCODE)." }
    if ($attrs.Count -ne $tracked.Count) {
        throw "git check-attr returned $($attrs.Count) verdicts for $($tracked.Count) paths; refusing to scan on a misaligned mapping."
    }

    $offenders = @()
    $scanned = 0
    $skippedBinary = 0
    $skippedMissing = @()

    for ($i = 0; $i -lt $tracked.Count; $i++) {
        if ($attrs[$i].EndsWith(": text: unset")) { $skippedBinary++; continue }

        $path = $tracked[$i]
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { $skippedMissing += $path; continue }

        $scanned++
        $bytes = [System.IO.File]::ReadAllBytes($path)
        if ([Array]::IndexOf($bytes, [byte]0) -lt 0) { continue }

        # Report line numbers so the fix is a targeted edit rather than a hunt.
        $lineNumbers = [System.Collections.Generic.List[int]]::new()
        $line = 1
        foreach ($b in $bytes) {
            if ($b -eq 0) { $null = $lineNumbers.Add($line) }
            elseif ($b -eq 10) { $line++ }
        }
        $offenders += "  $path - $($lineNumbers.Count) NUL byte(s) at line(s) $(($lineNumbers | Select-Object -Unique) -join ', ')"
    }

    if ($offenders.Count -gt 0) {
        throw @"
Raw NUL bytes found in $($offenders.Count) tracked text file(s):
$($offenders -join "`n")

In C# source this is almost always a sentinel written as a literal byte instead of the escape.
Write `"\0…"` rather than embedding the byte - in a NON-verbatim literal the compiled string is
byte-identical, so this is a source-only change. If the file is genuinely binary, declare it in
.gitattributes (e.g. `*.ext binary`) instead of leaving it to `* text=auto`.
"@
    }

    # Report the skips too: "0 missing" is what makes this a total scan rather than a hopeful one.
    if ($skippedMissing.Count -gt 0) {
        Write-Host "NUL gate: $($skippedMissing.Count) tracked path(s) absent from the working tree, not scanned: $($skippedMissing -join ', ')"
    }
    Write-Host "NUL gate: clean. $scanned text file(s) scanned, $skippedBinary declared binary and skipped, $($skippedMissing.Count) missing."
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
