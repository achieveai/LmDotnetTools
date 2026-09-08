$ErrorActionPreference = "Stop"

function Assert-True {
    param(
        [Parameter(Mandatory = $true)]
        [bool]$Condition,

        [Parameter(Mandatory = $true)]
        [string]$Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

function New-FakePackage {
    param(
        [Parameter(Mandatory = $true)]
        [string]$OutputDirectory
    )

    New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
    $packagePath = Join-Path $OutputDirectory "AchieveAi.LmDotnetTools.Sandbox.1.0.0.nupkg"
    $stream = [System.IO.File]::Open($packagePath, [System.IO.FileMode]::Create)
    try {
        $archive = [System.IO.Compression.ZipArchive]::new(
            $stream,
            [System.IO.Compression.ZipArchiveMode]::Create,
            $false
        )
        try {
            foreach ($tfm in @("net8.0", "net9.0")) {
                $entry = $archive.CreateEntry("lib/$tfm/AchieveAi.LmDotnetTools.Sandbox.dll")
                $entryStream = $entry.Open()
                $entryStream.Dispose()
            }
        }
        finally {
            $archive.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }
}

function Invoke-TelemetryScenario {
    param(
        [string]$FailVerb,
        [switch]$LockTimingFile,
        [switch]$BreakPackVerification,
        [string]$ChangedPath = "src/LmCore/Messages/IMessage.cs",
        [switch]$OmitSelector,
        [switch]$UseChangedPathFile,
        [switch]$EmptyChangedPathFile
    )

    $testRoot = Join-Path ([System.IO.Path]::GetTempPath()) "ci-test-telemetry-$([guid]::NewGuid().ToString('N'))"
    $scriptsDirectory = Join-Path $testRoot "scripts"
    New-Item -ItemType Directory -Path $scriptsDirectory -Force | Out-Null
    Copy-Item (Join-Path $PSScriptRoot "ci-test.ps1") (Join-Path $scriptsDirectory "ci-test.ps1")
    if (-not $OmitSelector) {
        Copy-Item (Join-Path $PSScriptRoot "select-impacted-tests.ps1") (Join-Path $scriptsDirectory "select-impacted-tests.ps1")
    }

    $sourceDirectory = Join-Path $testRoot "src\LmCore\Messages"
    $testDirectory = Join-Path $testRoot "tests\LmCore.Tests"
    New-Item -ItemType Directory -Path $sourceDirectory, $testDirectory -Force | Out-Null
    Set-Content -Path (Join-Path $testRoot "src\LmCore\LmCore.csproj") -Value '<Project Sdk="Microsoft.NET.Sdk" />' -Encoding utf8
    Set-Content -Path (Join-Path $testRoot "tests\LmCore.Tests\LmCore.Tests.csproj") -Encoding utf8 -Value @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><IsTestProject>true</IsTestProject></PropertyGroup>
  <ItemGroup><ProjectReference Include="../../src/LmCore/LmCore.csproj" /></ItemGroup>
</Project>
'@
    Set-Content -Path (Join-Path $sourceDirectory "IMessage.cs") -Value "interface IMessage {}" -Encoding utf8
    Set-Content -Path (Join-Path $testRoot "README.md") -Value "repository documentation" -Encoding utf8

    $timingPath = Join-Path $testRoot ".logs\ci-phase-timings.ndjson"
    $timingLock = $null
    if ($LockTimingFile) {
        New-Item -ItemType Directory -Path (Split-Path $timingPath -Parent) -Force | Out-Null
        Set-Content -Path $timingPath -Value '{"runId":"stale"}' -Encoding utf8
        $timingLock = [System.IO.File]::Open(
            $timingPath,
            [System.IO.FileMode]::Open,
            [System.IO.FileAccess]::ReadWrite,
            [System.IO.FileShare]::None
        )
    }

    $global:FakeDotnetFailVerb = $FailVerb
    $global:FakeDotnetBreakPack = [bool]$BreakPackVerification
    $global:FakeDotnetInvocations = [System.Collections.Generic.List[string]]::new()
    function global:dotnet {
        $global:FakeDotnetInvocations.Add(($args -join " "))
        $verb = if ($args.Count -eq 0) { "" } else { [string]$args[0] }
        if ($verb -eq "sln") {
            @(
                "Project(s)"
                "----------"
                "src/LmCore/LmCore.csproj"
                "tests/LmCore.Tests/LmCore.Tests.csproj"
            )
        }

        if ($verb -eq "pack" -and -not $global:FakeDotnetBreakPack) {
            $outputIndex = [array]::IndexOf($args, "-o")
            if ($outputIndex -lt 0) {
                throw "Fake dotnet expected a pack output directory."
            }

            New-FakePackage -OutputDirectory ([string]$args[$outputIndex + 1])
        }

        if ($verb -eq $global:FakeDotnetFailVerb) {
            $global:LASTEXITCODE = 23
            return
        }

        $global:LASTEXITCODE = 0
    }

    $changedPathFile = Join-Path $testRoot "changed-paths.txt"
    if ($UseChangedPathFile -and -not $EmptyChangedPathFile) {
        Set-Content -Path $changedPathFile -Value $ChangedPath -Encoding utf8
    }
    elseif ($UseChangedPathFile) {
        New-Item -ItemType File -Path $changedPathFile | Out-Null
        $staleShadowPath = Join-Path $testRoot ".logs\test-impact-shadow.json"
        New-Item -ItemType Directory -Path (Split-Path $staleShadowPath -Parent) -Force | Out-Null
        Set-Content -Path $staleShadowPath -Value '{"mode":"selected","reason":"stale"}' -Encoding utf8
    }

    $startingLocation = Get-Location
    $warningPath = Join-Path $testRoot "warnings.txt"
    $caught = $null
    $invocations = @()
    try {
        if ($UseChangedPathFile) {
            & (Join-Path $scriptsDirectory "ci-test.ps1") -SkipRestore -ChangedPathFile $changedPathFile 3> $warningPath
        }
        else {
            & (Join-Path $scriptsDirectory "ci-test.ps1") -SkipRestore -ChangedPath $ChangedPath 3> $warningPath
        }
    }
    catch {
        $caught = $_
    }
    finally {
        $invocations = @($global:FakeDotnetInvocations)
        Set-Location $startingLocation
        if ($null -ne $timingLock) {
            $timingLock.Dispose()
        }
        Remove-Item Function:\global:dotnet -ErrorAction SilentlyContinue
        Remove-Variable FakeDotnetFailVerb -Scope Global -ErrorAction SilentlyContinue
        Remove-Variable FakeDotnetBreakPack -Scope Global -ErrorAction SilentlyContinue
        Remove-Variable FakeDotnetInvocations -Scope Global -ErrorAction SilentlyContinue
    }

    $records = if ($LockTimingFile) {
        @()
    }
    else {
        Assert-True (Test-Path $timingPath) "The CI script did not create $timingPath."
        @(Get-Content $timingPath | ForEach-Object { $_ | ConvertFrom-Json })
    }

    $selectionPath = Join-Path $testRoot ".logs\test-impact-shadow.json"
    return [pscustomobject]@{
        Root = $testRoot
        Records = $records
        Error = $caught
        Warnings = if (Test-Path $warningPath) { Get-Content $warningPath -Raw } else { "" }
        Selection = if (Test-Path $selectionPath) { Get-Content $selectionPath -Raw | ConvertFrom-Json } else { $null }
        Invocations = @($invocations)
    }
}

$success = $null
$fullShadow = $null
$selectorFailure = $null
$changedPathFile = $null
$emptyChangedPathFile = $null
$failure = $null
$powerShellFailure = $null
$lockedSuccess = $null
$lockedFailure = $null
try {
    $success = Invoke-TelemetryScenario
    Assert-True ($null -eq $success.Error) "The successful fake run threw: $($success.Error)"

    $expectedPhases = @("info", "restore", "format", "build", "test", "pack")
    $actualPhases = @($success.Records.phase | Select-Object -Unique)
    foreach ($phase in $expectedPhases) {
        Assert-True ($actualPhases -contains $phase) "The successful run omitted the '$phase' timing phase."
    }

    Assert-True (@($success.Records | Where-Object outcome -ne "passed").Count -eq 0) "A successful step was not recorded as passed."
    Assert-True (@($success.Records | Where-Object { $_.elapsedSeconds -lt 0 }).Count -eq 0) "A timing record contained a negative duration."
    $successRunIds = @($success.Records.runId | Select-Object -Unique)
    Assert-True ($successRunIds.Count -eq 1) "One invocation did not use exactly one telemetry run id."
    Assert-True ($successRunIds[0] -match '^[0-9a-f]{32}$') "The telemetry run id is not a 32-character GUID."
    Assert-True ($null -ne $success.Selection) "The CI script did not emit a shadow selection artifact."
    Assert-True ($success.Selection.mode -eq "selected") "The CI shadow selector did not preserve selected mode."
    Assert-True ($success.Selection.selectedProjects[0] -eq "tests/LmCore.Tests/LmCore.Tests.csproj") "The CI shadow selector emitted the wrong project."
    $testInvocations = @($success.Invocations | Where-Object { $_ -like "test *" })
    Assert-True ($testInvocations.Count -eq 1) "The CI script did not execute exactly one authoritative test command."
    Assert-True ($testInvocations[0] -eq "test LmDotnetTools.sln --no-build --verbosity minimal --blame-hang --blame-hang-timeout 4m /p:RunBrowserE2ETests=false") "Shadow selection changed the authoritative full test command."

    $fullShadow = Invoke-TelemetryScenario -ChangedPath "README.md"
    Assert-True ($null -eq $fullShadow.Error) "An unowned shadow path failed otherwise successful CI commands."
    Assert-True ($fullShadow.Selection.mode -eq "full" -and $fullShadow.Selection.reason -eq "unowned-path") "An unowned path was not recorded as a full shadow decision."

    $selectorFailure = Invoke-TelemetryScenario -OmitSelector
    Assert-True ($null -eq $selectorFailure.Error) "A shadow selector failure failed otherwise successful CI commands."
    Assert-True ($null -eq $selectorFailure.Selection) "A missing selector unexpectedly emitted a decision artifact."
    Assert-True ($selectorFailure.Warnings -match "shadow selection failed") "A shadow selector failure was not reported."
    Assert-True (@($selectorFailure.Invocations | Where-Object { $_ -like "test *" }).Count -eq 1) "A shadow selector failure skipped the authoritative full test command."

    $changedPathFile = Invoke-TelemetryScenario -UseChangedPathFile
    Assert-True ($null -eq $changedPathFile.Error) "A changed-path file failed otherwise successful CI commands."
    Assert-True ($changedPathFile.Selection.mode -eq "selected") "A changed-path file did not drive shadow selection."

    $emptyChangedPathFile = Invoke-TelemetryScenario -UseChangedPathFile -EmptyChangedPathFile
    Assert-True ($null -eq $emptyChangedPathFile.Error) "An empty changed-path file failed the authoritative CI commands."
    Assert-True ($null -eq $emptyChangedPathFile.Selection) "An empty changed-path file emitted a misleading shadow decision."
    Assert-True (@($emptyChangedPathFile.Invocations | Where-Object { $_ -like "test *" }).Count -eq 1) "An empty changed-path file skipped the authoritative full test command."

    $failure = Invoke-TelemetryScenario -FailVerb "test"
    Assert-True ($null -ne $failure.Error) "The failing fake test command did not fail the CI script."
    Assert-True ($failure.Error.Exception.Message -match "exit code 23") "The CI script did not preserve native exit code 23."

    $failedTest = @($failure.Records | Where-Object { $_.phase -eq "test" -and $_.outcome -eq "failed" })
    Assert-True ($failedTest.Count -eq 1) "The failed test phase was not emitted exactly once."
    Assert-True ($failedTest[0].exitCode -eq 23) "The failed test timing record did not contain exit code 23."

    $powerShellFailure = Invoke-TelemetryScenario -BreakPackVerification
    Assert-True ($null -ne $powerShellFailure.Error) "The broken package verification did not fail the CI script."
    $failedPack = @($powerShellFailure.Records | Where-Object { $_.phase -eq "pack" -and $_.outcome -eq "failed" })
    Assert-True ($failedPack.Count -eq 1) "The PowerShell pack failure was not emitted exactly once."
    Assert-True ($null -eq $failedPack[0].exitCode) "A PowerShell failure did not record a null native exit code."

    $lockedSuccess = Invoke-TelemetryScenario -LockTimingFile
    Assert-True ($null -eq $lockedSuccess.Error) "A telemetry write failure failed otherwise successful CI commands: $($lockedSuccess.Error)"
    Assert-True ($lockedSuccess.Warnings -match "prior rows may remain") "A failed telemetry reset was not reported."
    Assert-True ($lockedSuccess.Warnings -match "CI telemetry write failed") "A failed telemetry append was not reported."

    $lockedFailure = Invoke-TelemetryScenario -FailVerb "test" -LockTimingFile
    Assert-True ($null -ne $lockedFailure.Error) "The locked-file failure scenario did not fail the CI script."
    Assert-True ($lockedFailure.Error.Exception.Message -match "exit code 23") "A telemetry write failure replaced native exit code 23."
    Assert-True ($lockedFailure.Warnings -match "CI telemetry write failed") "Telemetry loss during a command failure was not reported."

    Write-Host "PASS: CI phase telemetry records success and failure without hiding native exit codes."
}
finally {
    foreach ($scenario in @($success, $fullShadow, $selectorFailure, $changedPathFile, $emptyChangedPathFile, $failure, $powerShellFailure, $lockedSuccess, $lockedFailure)) {
        if ($null -ne $scenario -and (Test-Path $scenario.Root)) {
            Remove-Item $scenario.Root -Recurse -Force
        }
    }
}

$global:LASTEXITCODE = 0
