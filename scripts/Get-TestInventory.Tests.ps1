#requires -Version 7.0

$ErrorActionPreference = "Stop"

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

function Set-FixtureFile {
    param([string]$Path, [string]$Content)
    $fullPath = Join-Path $fixture $Path
    New-Item -ItemType Directory -Path (Split-Path $fullPath -Parent) -Force | Out-Null
    [System.IO.File]::WriteAllText($fullPath, $Content)
}

function Get-Inventory {
    $lines = @(& (Join-Path $PSScriptRoot "Get-TestInventory.ps1") -RepositoryRoot $fixture)
    return @($lines | ForEach-Object { $_ | ConvertFrom-Json })
}

foreach ($scriptName in @("Get-TestInventory.ps1", "Get-TestInventory.Tests.ps1")) {
    $tokens = $null
    $parseErrors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot $scriptName), [ref]$tokens, [ref]$parseErrors)
    Assert-True ($parseErrors.Count -eq 0) "$scriptName must parse without errors."
    Assert-True ($ast.ScriptRequirements.RequiredPSVersion -eq [version]"7.0") "$scriptName must explicitly require PowerShell 7.0."
}

$fixture = Join-Path ([System.IO.Path]::GetTempPath()) "test-inventory-$([guid]::NewGuid().ToString('N'))"
function Invoke-FixtureGit {
    param([string[]]$Arguments)
    $routing = @("GIT_DIR", "GIT_WORK_TREE", "GIT_INDEX_FILE", "GIT_COMMON_DIR")
    $previous = @{}
    foreach ($name in $routing) {
        $item = Get-Item -LiteralPath "Env:$name" -ErrorAction SilentlyContinue
        if ($null -ne $item) { $previous[$name] = $item.Value }
        Remove-Item -LiteralPath "Env:$name" -ErrorAction SilentlyContinue
    }
    try { & git -C $fixture @Arguments }
    finally {
        foreach ($name in $routing) { Remove-Item -LiteralPath "Env:$name" -ErrorAction SilentlyContinue }
        foreach ($name in $previous.Keys) { Set-Item -LiteralPath "Env:$name" -Value $previous[$name] }
    }
}
New-Item -ItemType Directory -Path $fixture | Out-Null
try {
    Invoke-FixtureGit -Arguments @("init", "--quiet")
    if ($LASTEXITCODE -ne 0) { throw "Could not initialize inventory fixture." }
    Set-FixtureFile ".gitignore" "ignored/`n"
    Set-FixtureFile "tests/Core/Core.csproj" '<Project><PropertyGroup><TargetFramework>net9.0</TargetFramework><IsTestProject>true</IsTestProject></PropertyGroup></Project>'
    Set-FixtureFile "tests/Core/Checks.cs" 'class Checks { /* Static inventory must not pretend to discover cases. */ }'
    Set-FixtureFile "tests/Utilities/Utilities.csproj" '<Project><PropertyGroup><TargetFrameworks>net8.0;net9.0</TargetFrameworks></PropertyGroup><ItemGroup><PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.0" /></ItemGroup></Project>'
    Set-FixtureFile "tests/Browser/Browser.csproj" '<Project><PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup><PropertyGroup Condition="&apos;$(RunBrowserE2ETests)&apos; != &apos;false&apos;"><IsTestProject>true</IsTestProject></PropertyGroup></Project>'
    Set-FixtureFile "tests/Inherited/Inherited.csproj" '<Project><Import Project="../../custom.targets" /></Project>'
    Set-FixtureFile "src/Library/Library.csproj" '<Project><PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup></Project>'
    Set-FixtureFile "src/TestHelper/TestHelper.csproj" '<Project><PropertyGroup><TargetFramework>net9.0</TargetFramework><IsTestProject>false</IsTestProject></PropertyGroup></Project>'
    Set-FixtureFile "tools/External.Tests.csproj" '<Project><PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup></Project>'
    Set-FixtureFile "RootTests.csproj" '<Project><PropertyGroup><IsTestProject>true</IsTestProject></PropertyGroup></Project>'
    Set-FixtureFile "LmDotnetTools.sln" @'
Microsoft Visual Studio Solution File, Format Version 12.00
Project("{FAKE}") = "Core", "TESTS\CORE\CORE.csproj", "{CORE}"
EndProject
Project("{FAKE}") = "Utilities", "tests\Utilities\Utilities.csproj", "{UTILITIES}"
EndProject
Project("{FAKE}") = "Browser", "tests\Browser\Browser.csproj", "{BROWSER}"
EndProject
Project("{FAKE}") = "Missing", "tests\Missing\Missing.csproj", "{MISSING}"
EndProject
'@
    Set-FixtureFile "samples/LmStreaming.Sample/ClientApp/src/nested/example.test.ts" 'throw new Error("Do not execute during static inventory");'
    Set-FixtureFile "samples/LmStreaming.Sample/playwright-scripts/manual.mjs" 'throw new Error("Do not execute");'
    Set-FixtureFile "scripts/check.Tests.ps1" 'throw "Do not execute"'
    Set-FixtureFile "evals/sample/tests/score.tests.ps1" 'throw "Do not execute"'
    Set-FixtureFile "tests/python/test_protocol.py" 'raise Exception("Do not execute")'
    Set-FixtureFile "ignored/Hidden.csproj" '<Project><PropertyGroup><IsTestProject>true</IsTestProject></PropertyGroup></Project>'
    Invoke-FixtureGit -Arguments @("-c", "core.autocrlf=false", "add", "--", ".")
    if ($LASTEXITCODE -ne 0) { throw "Could not stage inventory fixture." }
    Set-FixtureFile "tests/New/New.csproj" '<Project><PropertyGroup><TargetFramework>$(InheritedFramework)</TargetFramework><IsTestProject>true</IsTestProject></PropertyGroup></Project>'
    Set-FixtureFile "tests/Unknown/Unknown.csproj" '<Project><broken>'

    $rows = @(Get-Inventory)
    $projects = @($rows | Where-Object kind -eq "dotnet-project")
    Assert-True ($projects.Count -eq 9) "Inventory must retain root, externally named, present and missing test candidates."
    Assert-True (@($rows | Where-Object kind -eq "dotnet-project-unclassified").Count -eq 2) "Unclassified libraries must have auditable records, not silently disappear."
    Assert-True (@($projects | Where-Object path -eq "tools/External.Tests.csproj").Count -eq 1) "A test-named project outside tests must remain a candidate."
    $rootProject = $projects | Where-Object path -eq "RootTests.csproj"
    Assert-True ($rootProject.sourcePathCandidates -contains "tests/Core/Checks.cs") "Root project directory candidates include descendants but do not claim evaluated ownership."
    Assert-True (@($projects | Where-Object path -eq "src/Library/Library.csproj").Count -eq 0) "Ordinary libraries must not be called test projects."
    Assert-True (@($projects | Where-Object path -like "ignored/*").Count -eq 0) "Ignored untracked projects must not leak into inventory."
    $core = $projects | Where-Object path -eq "tests/Core/Core.csproj"
    Assert-True (@($core).Count -eq 1 -and $core.path -ceq "tests/Core/Core.csproj") "Case-mismatched solution paths must use one Git-spelled identity."
    Assert-True ($core.inSolution -and $core.tracked) "Track solution membership and Git provenance."
    $library = $rows | Where-Object path -eq "src/Library/Library.csproj"
    Assert-True ($library.kind -eq "dotnet-project-unclassified" -and $library.testEvidence.Count -eq 0) "Libraries need explicit unclassified records."
    Assert-True ($core.sourcePathCandidates.Count -eq 1) "Inventory source files without inventing test cases."
    Assert-True ($null -eq $core.discoveredTestCount -and $core.runtimeStatus -eq "not-run") "Static inventory must not claim discovery or success."
    Assert-True ($core.requirements -contains "not-audited") "No static declaration certifies offline execution."
    $utilities = $projects | Where-Object path -eq "tests/Utilities/Utilities.csproj"
    Assert-True (($utilities.frameworkCandidates -join ",") -eq "net8.0,net9.0") "Retain all declared framework candidates."
    Assert-True ($utilities.testEvidence -contains "test-sdk-reference") "SDK-only test utilities must be included."
    $browser = $projects | Where-Object path -eq "tests/Browser/Browser.csproj"
    Assert-True ($browser.testDeclarations[0].condition -match "RunBrowserE2ETests") "Preserve conditional IsTestProject rather than evaluating it."
    Assert-True ($browser.evaluationStatus -eq "not-evaluated") "XML inspection must not imply MSBuild evaluation."
    $new = $projects | Where-Object path -eq "tests/New/New.csproj"
    Assert-True (-not $new.tracked -and -not $new.inSolution) "New untracked suites must be visible."
    Assert-True ($new.frameworkCandidates.Count -eq 0 -and $new.frameworkDeclarations[0].value -eq '$(InheritedFramework)') "Do not turn an MSBuild expression into a runnable framework."
    $inherited = $projects | Where-Object path -eq "tests/Inherited/Inherited.csproj"
    Assert-True ($inherited.testEvidence -contains "test-directory-candidate") "Imported test state must remain an explicit candidate."
    $missing = $projects | Where-Object path -eq "tests/Missing/Missing.csproj"
    Assert-True ($missing.inventoryStatus -eq "missing" -and $missing.inSolution) "Missing solution projects must remain explicit."
    $invalid = $projects | Where-Object path -eq "tests/Unknown/Unknown.csproj"
    Assert-True ($invalid.inventoryStatus -eq "invalid-project") "Malformed projects must not disappear."
    Assert-True (@($rows | Where-Object kind -eq "vitest-file").Count -eq 1) "Include client test-file identities."
    Assert-True (@($rows | Where-Object kind -eq "manual-browser-script").Count -eq 1) "Account for manual scripts without running them."
    Assert-True (@($rows | Where-Object kind -eq "powershell-test").Count -eq 2) "Include case-insensitive standalone PowerShell test scripts."
    Assert-True (@($rows | Where-Object kind -eq "python-test").Count -eq 1) "Include Python test files without importing them."
    $first = $rows | ConvertTo-Json -Depth 12 -Compress
    $second = (Get-Inventory) | ConvertTo-Json -Depth 12 -Compress
    Assert-True ($first -ceq $second) "Inventory must be deterministic for unchanged inputs."

    Set-FixtureFile "tests/ab/ab.csproj" '<Project />'
    Set-FixtureFile "tests/a-b/a-b.csproj" '<Project />'
    $originalCulture = [System.Globalization.CultureInfo]::CurrentCulture
    try {
        [System.Globalization.CultureInfo]::CurrentCulture = [System.Globalization.CultureInfo]::GetCultureInfo("en-US")
        $english = @(Get-Inventory)
        [System.Globalization.CultureInfo]::CurrentCulture = [System.Globalization.CultureInfo]::GetCultureInfo("tr-TR")
        $turkish = @(Get-Inventory)
        Assert-True (($english | ConvertTo-Json -Depth 12 -Compress) -ceq ($turkish | ConvertTo-Json -Depth 12 -Compress)) "Inventory ordering must not depend on process culture."
        $orderedPaths = @($english | Where-Object kind -eq "dotnet-project" | ForEach-Object path)
        Assert-True ([array]::IndexOf($orderedPaths, "tests/a-b/a-b.csproj") -lt [array]::IndexOf($orderedPaths, "tests/ab/ab.csproj")) "Path order must be ordinal, including punctuation."
    }
    finally { [System.Globalization.CultureInfo]::CurrentCulture = $originalCulture }

    Set-FixtureFile "tests/Core/Checks.cs" @'
using Alias = Xunit.FactAttribute;
namespace Contracts;
public partial class Checks : UnknownBase {
    static Checks() { throw new System.Exception("Never load test code"); }
    // [Fact] void NotATest() {}
    [Alias] public void Baseline() {}
    [MyFact] public void HiddenCustom() {}
    [Xunit.Theory, Xunit.InlineData(1), Xunit.InlineData(2)] public void Rows(int value) {}
    [Xunit.Theory, Xunit.MemberData(nameof(Values))] public void Dynamic(int value) {}
    [WindowsOnlyFact(Skip = "Fixture skip")] public void Platform() {}
    [Xunit.Fact(Skip = null)] public void NullSkip() {}
    [GlobalAlias] public void GloballyAliasedAttribute() {}
    [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod] public void Storage() {}
    public class Nested { [Xunit.Fact] public void Baseline() {} }
    public class Generic<T> { public class Inner<TFirst, TSecond> { [Xunit.Fact] public void GenericBaseline() {} } }
    public abstract class InheritedBase { [Xunit.Fact] public void InheritedBaseline() {} }
    public abstract class IntermediateInherited : InheritedBase {}
    public sealed class ConcreteInherited : IntermediateInherited {}
}
public abstract class DirectBase { [Xunit.Fact] public void DirectInheritedBaseline() {} }
public sealed class DirectOne : DirectBase {}
public sealed class DirectTwo : DirectBase {}
public abstract partial class PartialBase {}
public partial class PartialBase { [Xunit.Fact] public void PartialInheritedBaseline() {} }
public sealed class PartialConcrete : PartialBase {}
'@
    Set-FixtureFile "tests/Core/CrossNamespace.cs" 'namespace Other; public sealed class CrossNamespaceDirect : global::Contracts.DirectBase {}'
    Set-FixtureFile "tests/Core/AliasedNamespace.cs" 'using DirectAlias = Contracts.DirectBase; namespace Another; public sealed class AliasedNamespaceDirect : DirectAlias {}'
    Set-FixtureFile "tests/Core/ImportedNamespace.cs" 'using Contracts; namespace Imported; public sealed class ImportedNamespaceDirect : DirectBase {}'
    Set-FixtureFile "tests/Core/GlobalUsings.cs" 'global using Contracts; global using GlobalDirectAlias = Contracts.DirectBase; global using GlobalAlias = Xunit.FactAttribute;'
    Set-FixtureFile "tests/Core/GlobalAliasedNamespace.cs" 'namespace GlobalAliased; public sealed class GlobalAliasedNamespaceDirect : GlobalDirectAlias {} namespace GlobalImported; public sealed class GlobalImportedNamespaceDirect : DirectBase {}'
    Set-FixtureFile "shared/Linked.cs" 'namespace Contracts; public class Linked { [Xunit.Fact] public void Shared() {} }'
    Set-FixtureFile "tests/Core/Core.csproj" '<Project><PropertyGroup><TargetFramework>net9.0</TargetFramework><IsTestProject>true</IsTestProject></PropertyGroup><ItemGroup><Compile Include="../../shared/Linked.cs" /></ItemGroup></Project>'
    $declarationLines = @(& (Join-Path $PSScriptRoot "Get-TestInventory.ps1") -RepositoryRoot $fixture -IncludeDeclarations)
    $declarations = @($declarationLines | ForEach-Object { $_ | ConvertFrom-Json })
    $coreCases = @($declarations | Where-Object { $_.kind -eq "test-declaration" -and $_.path -eq "tests/Core/Core.csproj" })
    Assert-True ($coreCases.Count -eq 13) "AST must retain recognized aliases and attributes, nested and generic types, inherited-source uncertainty, mixed frameworks and linked methods; unresolved custom attributes are not invented as tests."
    # A test attribute aliased in ANOTHER file must still resolve. Without project-wide global
    # aliases it matches no known attribute and no unresolved-attribute gap, so the family
    # vanishes from inventory, manifest comparison and selection with nothing to notice it.
    Assert-True (
        @($coreCases | Where-Object method -eq "GloballyAliasedAttribute").Count -eq 1
    ) "A recognized test attribute reached through a project-wide global using alias must still be inventoried."
    $baseline = $coreCases | Where-Object fullyQualifiedName -eq "Contracts.Checks.Baseline"
    Assert-True ($baseline.id -ceq 'dotnet|tests/Core/Core.csproj|Contracts.Checks.Baseline()') "Declaration identity must be stable, container-scoped and independent of source lines."
    $genericBaseline = $coreCases | Where-Object method -eq "GenericBaseline"
    Assert-True ($genericBaseline.declaringType -ceq 'Checks+Generic`1+Inner`2') "Every generic declaring-type segment must retain its own arity."
    Assert-True ($genericBaseline.id -ceq 'dotnet|tests/Core/Core.csproj|Contracts.Checks+Generic`1+Inner`2.GenericBaseline()') "Generic declaring-type arity must prevent stable identity collisions."
    $inheritedBaseline = $coreCases | Where-Object method -eq "InheritedBaseline"
    Assert-True ($inheritedBaseline.declarationGaps -contains "abstract-declaring-type-requires-discovery") "An abstract base without a proven concrete descendant must remain non-executable."
    Assert-True (@($inheritedBaseline.executableFullyQualifiedNames).Count -eq 0) "An unresolved abstract base must not invent an executable FQN."
    $directInheritedBaseline = $coreCases | Where-Object method -eq "DirectInheritedBaseline"
    Assert-True ($directInheritedBaseline.declarationGaps -contains "abstract-declaring-type-requires-discovery") "A cross-namespace descendant must prevent a same-namespace subset from being projected as complete."
    Assert-True (@($directInheritedBaseline.executableFullyQualifiedNames).Count -eq 0) "A cross-namespace descendant must fail closed instead of silently omitting its executable family."
    $partialInheritedBaseline = $coreCases | Where-Object method -eq "PartialInheritedBaseline"
    Assert-True ($partialInheritedBaseline.declarationGaps -contains "partial-test-identity-requires-discovery") "A method-bearing partial declaration must not hide abstract modifiers from another part."
    Assert-True (@($partialInheritedBaseline.executableFullyQualifiedNames).Count -eq 0) "A partial test declaring type must fail closed instead of emitting its source FQN."
    $rowsCase = $coreCases | Where-Object method -eq "Rows"
    Assert-True ($rowsCase.staticRowCount -eq 2 -and $rowsCase.parameterization -eq "static") "Literal InlineData rows belong to one method family, not separate invented cases."
    $dynamicCase = $coreCases | Where-Object method -eq "Dynamic"
    Assert-True ($null -eq $dynamicCase.staticRowCount -and $dynamicCase.parameterization -eq "dynamic") "MemberData remains unevaluated with unknown row count."
    Assert-True (($coreCases | Where-Object method -eq "Storage").testFramework -eq "mstest") "Test framework is separate from target framework."
    Assert-True (($coreCases | Where-Object method -eq "Platform").skipConditions -contains 'WindowsOnlyFact(Skip = "Fixture skip")') "Platform and static skip declarations must remain visible without evaluating them."
    Assert-True (@(($coreCases | Where-Object method -eq "NullSkip").skipConditions).Count -eq 0) "An explicitly null static Skip value must not authorize a NotExecuted result."
    Assert-True (($coreCases | Where-Object method -eq "Shared").sourcePath -eq "shared/Linked.cs") "Literal linked sources are attributed to their owning project."
    Assert-True (@($coreCases | Where-Object { $_.runtimeStatus -ne "not-run" -or $null -ne $_.discoveredTestCount }).Count -eq 0) "Source parsing never claims runtime discovery."
    $coreDeclarationContainer = $declarations | Where-Object { $_.kind -eq "dotnet-project" -and $_.path -eq "tests/Core/Core.csproj" }
    Assert-True ($coreDeclarationContainer.declarationGaps -contains "base-type-resolution-required:tests/Core/Checks.cs") "Unresolved base classes cannot silently hide inherited cases."
    Assert-True ($coreDeclarationContainer.declarationGaps -contains "unresolved-test-attribute:tests/Core/Checks.cs:HiddenCustom") "An unrecognized test-shaped custom attribute must block a declaration-completeness claim."
    Assert-True (($declarations | Where-Object kind -eq "vitest-file").declarationGaps -contains "typescript-parser-unavailable") "Missing client parser must remain visible, never zero-case completeness."
    $scriptCase = $declarations | Where-Object { $_.kind -eq "test-declaration" -and $_.path -eq "scripts/check.Tests.ps1" }
    Assert-True ($scriptCase.testFramework -eq "script" -and $scriptCase.parameterization -eq "atomic") "Standalone assertion scripts stay atomic and are parsed, not invoked."
    $declarationRepeat = @(& (Join-Path $PSScriptRoot "Get-TestInventory.ps1") -RepositoryRoot $fixture -IncludeDeclarations)
    Assert-True (($declarationLines -join "`n") -ceq ($declarationRepeat -join "`n")) "Declaration output is ordinal and deterministic."
    $baselineHash = ($coreCases | Where-Object fullyQualifiedName -eq "Contracts.Checks.Baseline").sourceHash
    $checksPath = Join-Path $fixture "tests/Core/Checks.cs"
    [System.IO.File]::WriteAllText($checksPath, ([System.IO.File]::ReadAllText($checksPath)).Replace("`n", "`r`n"))
    $crlfDeclarations = @(& (Join-Path $PSScriptRoot "Get-TestInventory.ps1") -RepositoryRoot $fixture -IncludeDeclarations | ForEach-Object { $_ | ConvertFrom-Json })
    $crlfHash = (
        $crlfDeclarations |
        Where-Object {
            $_.kind -eq "test-declaration" -and
            $_.path -eq "tests/Core/Core.csproj" -and
            $_.fullyQualifiedName -eq "Contracts.Checks.Baseline"
        }
    ).sourceHash
    Assert-True ($crlfHash -ceq $baselineHash) "Declaration source hashes must be identical for LF and CRLF checkouts."

    Remove-Item -LiteralPath (Join-Path $fixture "tests/Core/Core.csproj") -Force
    $deleted = Get-Inventory | Where-Object path -eq "tests/Core/Core.csproj"
    Assert-True ($deleted.inventoryStatus -eq "missing" -and $deleted.tracked) "Deleted tracked projects must remain visible."
    Write-Host 'PASS: static inventory preserves test surfaces, uncertainty and deterministic identities without executing tests.'
}
finally {
    Remove-Item -LiteralPath $fixture -Recurse -Force
}
