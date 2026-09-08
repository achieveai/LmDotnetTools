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

function Set-TestFile {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Root,

        [Parameter(Mandatory = $true)]
        [string]$RelativePath,

        [Parameter(Mandatory = $true)]
        [string]$Content
    )

    $path = Join-Path $Root $RelativePath
    New-Item -ItemType Directory -Path (Split-Path $path -Parent) -Force | Out-Null
    Set-Content -Path $path -Value $Content -Encoding utf8
}

function Add-TestProject {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Root,

        [Parameter(Mandatory = $true)]
        [string]$RelativePath,

        [string[]]$References = @(),

        [switch]$IsTest,

        [switch]$UseTestSdk
    )

    $referenceItems = @(
        foreach ($reference in $References) {
            "    <ProjectReference Include=`"$reference`" />"
        }
    ) -join [Environment]::NewLine

    $testProperty = if ($IsTest) { "    <IsTestProject>true</IsTestProject>" } else { "" }
    $testSdkItem = if ($UseTestSdk) { '    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.0" />' } else { "" }
    $project = @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
$testProperty
  </PropertyGroup>
  <ItemGroup>
$referenceItems
$testSdkItem
  </ItemGroup>
</Project>
"@

    Set-TestFile -Root $Root -RelativePath $RelativePath -Content $project
}

function Invoke-Selector {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Root,

        [Parameter(Mandatory = $true)]
        [string[]]$ChangedPath,

        [string[]]$SolutionProject = @()
    )

    $arguments = @{
        RepositoryRoot = $Root
        ChangedPath = $ChangedPath
    }
    if ($SolutionProject.Count -gt 0) {
        $arguments.SolutionProject = $SolutionProject
    }

    $json = & (Join-Path $PSScriptRoot "select-impacted-tests.ps1") @arguments
    return $json | ConvertFrom-Json
}

$testRoots = [System.Collections.Generic.List[string]]::new()
try {
    $leafRoot = Join-Path ([System.IO.Path]::GetTempPath()) "static-selector-leaf-$([guid]::NewGuid().ToString('N'))"
    $testRoots.Add($leafRoot)
    Add-TestProject -Root $leafRoot -RelativePath "src/Leaf/Leaf.csproj"
    Add-TestProject -Root $leafRoot -RelativePath "tests/Leaf.Tests/Leaf.Tests.csproj" -References "../../src/Leaf/Leaf.csproj" -IsTest
    Set-TestFile -Root $leafRoot -RelativePath "src/Leaf/Leaf.cs" -Content "class Leaf {}"

    $leaf = Invoke-Selector -Root $leafRoot -ChangedPath "src/Leaf/Leaf.cs"
    $dotSegmentLeaf = Invoke-Selector -Root $leafRoot -ChangedPath "src/Other/../Leaf/Leaf.cs"
    Assert-True (
        ($dotSegmentLeaf | ConvertTo-Json -Depth 10 -Compress) -ceq
        ($leaf | ConvertTo-Json -Depth 10 -Compress)
    ) "Equivalent dot-segment and canonical changed paths must produce the same decision."
    $escapedRoot = Invoke-Selector -Root $leafRoot -ChangedPath "../outside.cs"
    Assert-True ($escapedRoot.mode -eq "full" -and $escapedRoot.reason -eq "invalid-changed-path") "A changed path escaping the repository root must fail closed."
    Assert-True ($leaf.mode -eq "selected") "A changed leaf project did not produce selected mode."
    Assert-True ($leaf.reason -eq "project-closure") "A changed leaf project did not report project-closure."
    Assert-True (@($leaf.selectedProjects).Count -eq 1) "A changed leaf project did not select exactly one test project."
    Assert-True ($leaf.selectedProjects[0] -eq "tests/Leaf.Tests/Leaf.Tests.csproj") "A changed leaf project selected the wrong test project."
    Assert-True ($leaf.affectedProjects -is [array]) "Affected projects must remain a JSON array when one project is affected."
    Assert-True (@($leaf.affectedProjects | Where-Object { [string]::IsNullOrEmpty($_) }).Count -eq 0) "A changed file with no direct linked-item consumer must not add an empty affected project."
    Assert-True ($leaf.graph.projectCount -eq 2 -and $leaf.graph.referenceCount -eq 1 -and $leaf.graph.testProjectCount -eq 1) "Leaf graph facts are incorrect."

    $sdkTestRoot = Join-Path ([System.IO.Path]::GetTempPath()) "static-selector-sdk-test-$([guid]::NewGuid().ToString('N'))"
    $testRoots.Add($sdkTestRoot)
    Add-TestProject -Root $sdkTestRoot -RelativePath "src/Library/Library.csproj"
    Add-TestProject -Root $sdkTestRoot -RelativePath "tests/Library.Tests/Library.Tests.csproj" -References "../../src/Library/Library.csproj" -UseTestSdk
    Set-TestFile -Root $sdkTestRoot -RelativePath "src/Library/Library.cs" -Content "class Library {}"

    $sdkTest = Invoke-Selector -Root $sdkTestRoot -ChangedPath "src/Library/Library.cs"
    Assert-True ($sdkTest.mode -eq "selected") "A test project identified by Microsoft.NET.Test.Sdk did not produce selected mode."
    Assert-True ($sdkTest.selectedProjects[0] -eq "tests/Library.Tests/Library.Tests.csproj") "A test project without IsTestProject was omitted."

    $outOfGateRoot = Join-Path ([System.IO.Path]::GetTempPath()) "static-selector-out-of-gate-$([guid]::NewGuid().ToString('N'))"
    $testRoots.Add($outOfGateRoot)
    Add-TestProject -Root $outOfGateRoot -RelativePath "src/Library/Library.csproj"
    Add-TestProject -Root $outOfGateRoot -RelativePath "tests/InGate.Tests/InGate.Tests.csproj" -References "../../src/Library/Library.csproj" -IsTest
    Add-TestProject -Root $outOfGateRoot -RelativePath "tests/OutOfGate.Tests/OutOfGate.Tests.csproj" -References "../../src/Library/Library.csproj" -IsTest
    Set-TestFile -Root $outOfGateRoot -RelativePath "src/Library/Library.cs" -Content "class Library {}"

    $outOfGate = Invoke-Selector -Root $outOfGateRoot -ChangedPath "src/Library/Library.cs" -SolutionProject @("src/Library/Library.csproj", "tests/InGate.Tests/InGate.Tests.csproj")
    Assert-True ($outOfGate.mode -eq "selected") "An out-of-gate test project prevented a provable in-gate selection."
    Assert-True (@($outOfGate.selectedProjects).Count -eq 1 -and $outOfGate.selectedProjects[0] -eq "tests/InGate.Tests/InGate.Tests.csproj") "Shadow selection included a test project outside the authoritative full gate."
    Assert-True ($outOfGate.graph.testProjectCount -eq 1) "Graph facts counted tests outside the authoritative full gate."

    $hubRoot = Join-Path ([System.IO.Path]::GetTempPath()) "static-selector-hub-$([guid]::NewGuid().ToString('N'))"
    $testRoots.Add($hubRoot)
    Add-TestProject -Root $hubRoot -RelativePath "src/Core/Core.csproj"
    Add-TestProject -Root $hubRoot -RelativePath "src/Feature/Feature.csproj" -References "../Core/Core.csproj"
    Add-TestProject -Root $hubRoot -RelativePath "tests/Core.Tests/Core.Tests.csproj" -References "../../src/Core/Core.csproj" -IsTest
    Add-TestProject -Root $hubRoot -RelativePath "tests/Feature.Tests/Feature.Tests.csproj" -References "../../src/Feature/Feature.csproj" -IsTest
    Set-TestFile -Root $hubRoot -RelativePath "src/Core/Core.cs" -Content "class Core {}"

    $hub = Invoke-Selector -Root $hubRoot -ChangedPath "src/Core/Core.cs"
    Assert-True ($hub.mode -eq "selected") "A changed hub project did not produce selected mode."
    Assert-True (@($hub.selectedProjects).Count -eq 2) "A changed hub project did not select both transitive test projects."
    Assert-True (($hub.selectedProjects -join ",") -eq "tests/Core.Tests/Core.Tests.csproj,tests/Feature.Tests/Feature.Tests.csproj") "Hub selection is not deterministic or complete."

    $linkedRoot = Join-Path ([System.IO.Path]::GetTempPath()) "static-selector-linked-$([guid]::NewGuid().ToString('N'))"
    $testRoots.Add($linkedRoot)
    Add-TestProject -Root $linkedRoot -RelativePath "tests/Source.Tests/Source.Tests.csproj" -IsTest
    Add-TestProject -Root $linkedRoot -RelativePath "tests/Consumer.Tests/Consumer.Tests.csproj" -IsTest
    Set-TestFile -Root $linkedRoot -RelativePath "tests/Source.Tests/Shared.cs" -Content "class Shared {}"
    $consumerProjectPath = Join-Path $linkedRoot "tests\Consumer.Tests\Consumer.Tests.csproj"
    [xml]$consumerProject = Get-Content $consumerProjectPath -Raw
    $itemGroup = $consumerProject.CreateElement("ItemGroup")
    $compile = $consumerProject.CreateElement("Compile")
    $compile.SetAttribute("Include", "../Source.Tests/Shared.cs")
    [void]$itemGroup.AppendChild($compile)
    [void]$consumerProject.Project.AppendChild($itemGroup)
    $consumerProject.Save($consumerProjectPath)

    $linked = Invoke-Selector -Root $linkedRoot -ChangedPath "tests/Source.Tests/Shared.cs"
    Assert-True ($linked.mode -eq "selected") "A linked source file did not produce selected mode."
    Assert-True (($linked.selectedProjects -join ",") -eq "tests/Consumer.Tests/Consumer.Tests.csproj,tests/Source.Tests/Source.Tests.csproj") "A linked source consumer was omitted from selection."

    $infrastructure = Invoke-Selector -Root $leafRoot -ChangedPath "scripts/ci-test.ps1"
    Assert-True ($infrastructure.mode -eq "full") "An infrastructure change did not force full mode."
    Assert-True ($infrastructure.reason -eq "infrastructure-change") "An infrastructure change reported the wrong reason."

    foreach ($dotPrefixedInfrastructurePath in @(".github/workflows/ci.yml", ".config/dotnet-tools.json")) {
        $dotPrefixedInfrastructure = Invoke-Selector -Root $leafRoot -ChangedPath $dotPrefixedInfrastructurePath
        Assert-True ($dotPrefixedInfrastructure.mode -eq "full") "A dot-prefixed infrastructure change did not force full mode."
        Assert-True ($dotPrefixedInfrastructure.reason -eq "infrastructure-change") "A dot-prefixed infrastructure change reported the wrong reason."
        Assert-True ($dotPrefixedInfrastructure.changedPaths[0] -eq $dotPrefixedInfrastructurePath) "A dot-prefixed changed path was mangled."
    }

    Set-TestFile -Root $leafRoot -RelativePath "src/Leaf/README.md" -Content "project documentation"
    $projectDocument = Invoke-Selector -Root $leafRoot -ChangedPath "src/Leaf/README.md"
    Assert-True ($projectDocument.mode -eq "selected") "A project-owned non-code file did not select its dependent tests."
    Assert-True ($projectDocument.selectedProjects[0] -eq "tests/Leaf.Tests/Leaf.Tests.csproj") "A project-owned non-code file selected the wrong test project."

    Set-TestFile -Root $leafRoot -RelativePath "README.md" -Content "repository documentation"
    $unowned = Invoke-Selector -Root $leafRoot -ChangedPath "README.md"
    Assert-True ($unowned.mode -eq "full") "An unowned path did not force full mode."
    Assert-True ($unowned.reason -eq "unowned-path") "An unowned path reported the wrong reason."
    Assert-True (@($unowned.changedPaths).Count -eq 1 -and $unowned.changedPaths[0] -eq "README.md") "The decision did not preserve normalized changed paths."

    $brokenRoot = Join-Path ([System.IO.Path]::GetTempPath()) "static-selector-broken-$([guid]::NewGuid().ToString('N'))"
    $testRoots.Add($brokenRoot)
    Add-TestProject -Root $brokenRoot -RelativePath "src/Broken/Broken.csproj" -References "../Missing/Missing.csproj"
    Add-TestProject -Root $brokenRoot -RelativePath "tests/Broken.Tests/Broken.Tests.csproj" -References "../../src/Broken/Broken.csproj" -IsTest
    Set-TestFile -Root $brokenRoot -RelativePath "src/Broken/Broken.cs" -Content "class Broken {}"

    $broken = Invoke-Selector -Root $brokenRoot -ChangedPath "src/Broken/Broken.cs"
    Assert-True ($broken.mode -eq "full") "An unresolved ProjectReference did not force full mode."
    Assert-True ($broken.reason -eq "unresolved-project-reference") "An unresolved ProjectReference reported the wrong reason."
    Assert-True (@($broken.graph.unresolvedReferences).Count -eq 1) "The graph did not report its unresolved ProjectReference."

    $invalidRoot = Join-Path ([System.IO.Path]::GetTempPath()) "static-selector-invalid-$([guid]::NewGuid().ToString('N'))"
    $testRoots.Add($invalidRoot)
    Set-TestFile -Root $invalidRoot -RelativePath "src/Invalid/Invalid.csproj" -Content "<Project><broken>"
    Set-TestFile -Root $invalidRoot -RelativePath "src/Invalid/Invalid.cs" -Content "class Invalid {}"

    $invalid = Invoke-Selector -Root $invalidRoot -ChangedPath "src/Invalid/Invalid.cs"
    Assert-True ($invalid.mode -eq "full") "An invalid project file did not force full mode."
    Assert-True ($invalid.reason -eq "invalid-project-file") "An invalid project file reported the wrong reason."
    Assert-True ($invalid.graph.referenceCount -eq 0) "An invalid graph emitted a null reference count."
    Assert-True (@($invalid.graph.unresolvedReferences).Count -eq 0) "An invalid graph emitted a null unresolved-reference entry."

    Write-Host "PASS: static test selection is deterministic and fails safely."
}
finally {
    foreach ($testRoot in $testRoots) {
        if (Test-Path $testRoot) {
            Remove-Item $testRoot -Recurse -Force
        }
    }
}
