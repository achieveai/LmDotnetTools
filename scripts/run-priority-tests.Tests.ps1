#requires -Version 7.0

$ErrorActionPreference = "Stop"
$runner = Join-Path $PSScriptRoot "run-priority-tests.ps1"
function Assert-True { param([bool]$Condition, [string]$Message) if (-not $Condition) { throw $Message } }
$fixture = Join-Path ([System.IO.Path]::GetTempPath()) "priority-tests-$([guid]::NewGuid().ToString('N'))"
function Set-FixtureFile {
    param([string]$Path, [string]$Content)
    $full = Join-Path $fixture $Path
    New-Item -ItemType Directory -Path (Split-Path $full -Parent) -Force | Out-Null
    [System.IO.File]::WriteAllText($full, $Content)
}
function Get-Plan {
    param([hashtable]$Options = @{})
    & $runner -RepositoryRoot $fixture @Options | ConvertFrom-Json
}
function New-TrxDocument {
    param(
        [object[]]$Results,
        [object[]]$Definitions,
        [switch]$WithoutResults,
        [switch]$WithoutDefinitions
    )
    $resultXml = @(
        foreach ($result in $Results) {
            $id = if ($result.ContainsKey("Id")) { " testId=`"$($result.Id)`"" } else { "" }
            "<UnitTestResult$id testName=`"$([System.Security.SecurityElement]::Escape($result.Name))`" outcome=`"$($result.Outcome)`" />"
        }
    ) -join ""
    $definitionRows = if ($PSBoundParameters.ContainsKey("Definitions")) { $Definitions } else { $Results }
    $definitionXml = @(
        foreach ($result in $definitionRows) {
            if ($result.ContainsKey("Id") -and $result.ContainsKey("Class") -and $result.ContainsKey("Method")) {
                "<UnitTest id=`"$($result.Id)`"><TestMethod className=`"$($result.Class)`" name=`"$($result.Method)`" /></UnitTest>"
            }
        }
    ) -join ""
    $resultsNode = if ($WithoutResults) { "" } else { "<Results>$resultXml</Results>" }
    $definitionsNode = if ($WithoutDefinitions) { "" } else { "<TestDefinitions>$definitionXml</TestDefinitions>" }
    return "<?xml version=`"1.0`" encoding=`"UTF-8`"?><TestRun xmlns=`"http://microsoft.com/schemas/VisualStudio/TeamTest/2010`">$resultsNode$definitionsNode</TestRun>"
}
function Set-TrxPlans {
    param([object[]]$Plans)
    $global:PriorityTrxPlans = [System.Collections.Generic.List[object]]::new()
    foreach ($plan in $Plans) { $global:PriorityTrxPlans.Add($plan) }
    $global:PriorityTrxPlanIndex = 0
}
New-Item -ItemType Directory -Path $fixture | Out-Null
try {
    git -C $fixture init --quiet
    if ($LASTEXITCODE -ne 0) { throw "Fixture git init failed." }
    Set-FixtureFile "src/Core/Core.csproj" '<Project><PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup></Project>'
    $solution = "Microsoft Visual Studio Solution File, Format Version 12.00`n"
    $solution += 'Project("{FAKE}") = "Core", "src/Core/Core.csproj", "{CORE}"' + "`nEndProject`n"
    $policy = @()
    foreach ($tier in 0..3) {
        $path = "tests/Tier$tier/Tier$tier.csproj"
        Set-FixtureFile $path '<Project><PropertyGroup><TargetFrameworks>net8.0;net9.0</TargetFrameworks><IsTestProject>true</IsTestProject></PropertyGroup><ItemGroup><ProjectReference Include="../../src/Core/Core.csproj" /></ItemGroup></Project>'
        Set-FixtureFile "tests/Tier$tier/Tests.cs" 'class Tests {}'
        $solution += "Project(`"{FAKE}`") = `"Tier$tier`", `"$path`", `"{T$tier}`"`nEndProject`n"
        $policy += @{ kind = "dotnet-project"; path = $path; priority = "P$tier"; rationale = "Fixture tier $tier"; requirements = @("not-audited") } | ConvertTo-Json -Compress
    }
    Set-FixtureFile "LmDotnetTools.sln" $solution
    Set-FixtureFile "scripts/test-priorities.ndjson" ($policy -join "`n")
    $all = Get-Plan
    Assert-True ($all.tests.Count -eq 4 -and $all.selectedCount -eq 4) "All priorities must retain every test suite."
    Assert-True ($all.unclassifiedProjects.Count -eq 1) "Unclassified project uncertainty must remain visible."
    Assert-True (@($all.tests | Where-Object runtimeStatus -ne "not-run").Count -eq 0) "Preview must never claim execution."
    foreach ($tier in 0..3) {
        $plan = Get-Plan @{ Priority = @("P$tier") }
        Assert-True ($plan.selectedCount -eq 1 -and ($plan.tests | Where-Object selected).priority -eq "P$tier") "Exact priority selection must work for P$tier."
    }
    Set-FixtureFile "tests/Tier2/Tier2.csproj" '<Project><Import Project="../../test-settings.props" /></Project>'
    $inherited = Get-Plan @{ Fast = $true; ChangedPath = @("tests/Tier3/Tests.cs") }
    Assert-True (-not ($inherited.tests | Where-Object path -eq "tests/Tier2/Tier2.csproj").selected) "Unrelated imported test metadata must not force dependency tests into a scoped run."
    $changedInherited = Get-Plan @{ Fast = $true; ChangedPath = @("tests/Tier2/Tests.cs") }
    Assert-True ($changedInherited.impact.mode -eq "selected" -and $changedInherited.selectedCount -eq 1 -and ($changedInherited.tests | Where-Object selected).path -eq "tests/Tier2/Tier2.csproj") "An actually changed imported test candidate must remain selected through known ownership, not full fallback."
    Set-FixtureFile "tests/Tier2/Tier2.csproj" '<Project><PropertyGroup><TargetFrameworks>net8.0;net9.0</TargetFrameworks><IsTestProject>true</IsTestProject></PropertyGroup><ItemGroup><ProjectReference Include="../../src/Core/Core.csproj" /></ItemGroup></Project>'
    $combined = Get-Plan @{ Priority = @("P0", "P2", "P2") }
    Assert-True ($combined.selectedCount -eq 2) "Repeated/multiple priorities must form a set."
    $fast = Get-Plan @{ Fast = $true; ChangedPath = @("tests/Tier3/Tests.cs") }
    Assert-True ($fast.selectedCount -eq 1 -and -not ($fast.tests | Where-Object priority -eq "P0").selected) "Scoped fast mode includes affected P3 but excludes unrelated global P0."
    $scopedTier = Get-Plan @{ Fast = $true; Priority = @("P1"); ChangedPath = @("tests/Tier3/Tests.cs") }
    Assert-True ($scopedTier.selectedCount -eq 1 -and ($scopedTier.tests | Where-Object selected).priority -eq "P3") "Directly edited tests survive an explicit priority subset."
    $coreTier = Get-Plan @{ Priority = @("P1"); ChangedPath = @("src/Core/Thing.cs") }
    Assert-True ($coreTier.selectedCount -eq 1 -and ($coreTier.tests | Where-Object selected).priority -eq "P1") "ChangedPath scope and priority selection must compose without Fast."
    $projectScope = Get-Plan @{ Project = @("tests/Tier3/Tier3.csproj"); Priority = @("P3") }
    $dotSegmentProjectScope = Get-Plan @{ Project = @("tests/Tier2/../Tier3/Tier3.csproj"); Priority = @("P3") }
    Assert-True (
        $dotSegmentProjectScope.selectedCount -eq $projectScope.selectedCount -and
        (($dotSegmentProjectScope.tests | Where-Object selected).path -join ",") -ceq
        (($projectScope.tests | Where-Object selected).path -join ",")
    ) "Equivalent dot-segment and canonical project scopes must produce the same selected project set."
    $escapedProjectScope = $false
    try { Get-Plan @{ Project = @("../outside.csproj") } | Out-Null }
    catch { $escapedProjectScope = $_.Exception.Message -like '*escapes the repository root*' }
    Assert-True $escapedProjectScope "A project scope escaping the repository root must be rejected."
    Assert-True ($projectScope.selectedCount -eq 1 -and ($projectScope.tests | Where-Object selected).priority -eq "P3") "Explicit project scope must exclude unrelated P0 and dependency projects."
    $projectWrongTier = Get-Plan @{ Project = @("tests/Tier3/Tier3.csproj"); Priority = @("P1") }
    Assert-True ($projectWrongTier.selectedCount -eq 0) "An explicit project is a scope, not a directly edited test override."
    Assert-True (@($fast.tests | Where-Object { $_.selected -and $_.priority -eq "P3" }).Count -eq 1) "Affected lower-cadence suites must not vanish."
    Assert-True (($fast.tests | Where-Object selected).selectionReason -eq "directly-changed-tests") "Declaration planning must preserve the specific direct-change reason."
    $closure = Get-Plan @{ Fast = $true; ChangedPath = @("src/Core/Thing.cs") }
    Assert-True ($closure.selectedCount -eq 4 -and $closure.impact.mode -eq "selected" -and $closure.impact.reason -eq "project-closure") "Changed library selects every consumer priority through the graph, not full fallback."
    $unknownChange = Get-Plan @{ Fast = $true; ChangedPath = @("unknown.txt") }
    Assert-True ($unknownChange.selectedCount -eq 4 -and $unknownChange.impact.mode -eq "full" -and $unknownChange.impact.reason -eq "unowned-path") "Unknown impact must expand through the unowned-path fallback."
    Set-FixtureFile "tests/New/New.csproj" '<Project><PropertyGroup><IsTestProject>true</IsTestProject></PropertyGroup></Project>'
    $unknown = Get-Plan @{ Priority = @("P0") }
    Assert-True ($unknown.selectedCount -eq 2) "Unassigned new test suite must join any priority selection."
    $new = $unknown.tests | Where-Object path -eq "tests/New/New.csproj"
    Assert-True ($new.priority -eq "unassigned" -and $new.selected -and $new.requirements -contains "not-audited") "Unknown is selected, not implicitly safe."
    Set-FixtureFile "scripts/test-priorities.ndjson" (($policy + '{"kind":"dotnet-project","path":"tests/Deleted/Deleted.csproj","priority":"P2","rationale":"stale","requirements":["not-audited"]}') -join "`n")
    $stale = Get-Plan @{ Priority = @("P0") }
    Assert-True ($stale.policyStatus -eq "stale" -and $stale.selectedCount -eq 5) "Stale classification must expand, not narrow."
    Set-FixtureFile "scripts/test-priorities.ndjson" 'not json'
    $invalid = Get-Plan @{ Priority = @("P0") }
    Assert-True ($invalid.policyStatus -eq "invalid" -and $invalid.selectedCount -eq 5) "Invalid policy fails open for selection, closed for execution."
    Set-FixtureFile "scripts/test-priorities.ndjson" ($policy -join "`n")
    $invalidContainerRule = $policy[0] | ConvertFrom-Json
    $invalidContainerRule.PSObject.Properties.Remove("requirements")
    Set-FixtureFile "scripts/test-priorities.ndjson" ((@($invalidContainerRule | ConvertTo-Json -Compress) + @($policy | Select-Object -Skip 1)) -join "`n")
    Assert-True ((Get-Plan).policyStatus -eq "invalid") "A container rule must explicitly carry requirements."
    $invalidContainerRule = $policy[0] | ConvertFrom-Json
    $invalidContainerRule.requirements = "not-audited"
    Set-FixtureFile "scripts/test-priorities.ndjson" ((@($invalidContainerRule | ConvertTo-Json -Compress) + @($policy | Select-Object -Skip 1)) -join "`n")
    Assert-True ((Get-Plan).policyStatus -eq "invalid") "Container requirements must be a nonempty string array, not a scalar."
    Set-FixtureFile "scripts/test-priorities.ndjson" ($policy -join "`n")
    $global:PriorityTestInvocations = [System.Collections.Generic.List[object]]::new()
    function global:dotnet { $global:PriorityTestInvocations.Add(@($args)); $global:LASTEXITCODE = 0 }
    $blocked = $false
    try { & $runner -RepositoryRoot $fixture -Priority P1 -Execute | Out-Null } catch { $blocked = $_.Exception.Message -like '*authorization*' }
    Assert-True ($blocked -and $global:PriorityTestInvocations.Count -eq 0) "Preflight must block entire run before any unapproved command executes."
    $policy += @{ kind = "dotnet-project"; path = "tests/New/New.csproj"; priority = "P2"; rationale = "New fixture"; requirements = @("not-audited") } | ConvertTo-Json -Compress
    Set-FixtureFile "scripts/test-priorities.ndjson" ($policy -join "`n")
    & $runner -RepositoryRoot $fixture -Priority P0 -Execute -ApprovedPath "tests/Tier0/Tier0.csproj" | Out-Null
    Assert-True ($global:PriorityTestInvocations.Count -eq 1) "Approved selected project must execute once, not once per framework or duplicate tier."
    $arguments = $global:PriorityTestInvocations[0]
    Assert-True ($arguments[0] -eq "test" -and $arguments -contains "--no-build" -and $arguments -contains "--no-restore" -and $arguments -notcontains "--filter") "Execution must preserve all theory rows and TFMs without implicit restore/build."
    function global:dotnet { $global:LASTEXITCODE = 23 }
    $failed = $false
    try { & $runner -RepositoryRoot $fixture -Priority P0 -Execute -ApprovedPath "tests/Tier0/Tier0.csproj" | Out-Null } catch { $failed = $_.Exception.Message -like '*23*' }
    Assert-True $failed "Native test failure must propagate."
    $global:LASTEXITCODE = 0
    $cases = @(
        @{ kind = "vitest-file"; path = "samples/LmStreaming.Sample/ClientApp/src/example.test.ts"; priority = "P1"; rationale = "Client fixture"; requirements = @("not-audited") },
        @{ kind = "powershell-test"; path = "scripts/example.Tests.ps1"; priority = "P1"; rationale = "Script fixture"; requirements = @("not-audited") },
        @{ kind = "python-test"; path = "tests/test_example.py"; priority = "P1"; rationale = "Python fixture"; requirements = @("not-audited") },
        @{ kind = "manual-browser-script"; path = "samples/LmStreaming.Sample/playwright-scripts/example.mjs"; priority = "P3"; rationale = "Manual fixture"; requirements = @("manual-scenario") }
    )
    foreach ($case in $cases) {
        Set-FixtureFile $case.path 'DO NOT EXECUTE: fixture payload'
        $policy += $case | ConvertTo-Json -Compress
    }
    $atomicRules = @(& (Join-Path $PSScriptRoot "Get-TestInventory.ps1") -RepositoryRoot $fixture -IncludeDeclarations | ForEach-Object { $_ | ConvertFrom-Json } | Where-Object kind -eq "test-declaration" | ForEach-Object {
        $containerRule = @($cases | Where-Object path -eq $_.path)[0]
        @{ kind = "test-declaration"; id = $_.id; path = $_.path; sourceHash = $_.sourceHash; priority = $containerRule.priority; rationale = "Fixture atomic script contract"; evidence = @("$($_.sourcePath):1"); reviewState = "reviewed"; requirements = @("not-audited"); component = "fixture scripts"; uncertainties = @(); coverageEvidence = "not-in-capture" } | ConvertTo-Json -Compress
    })
    $policy += $atomicRules
    Set-FixtureFile "scripts/test-priorities.ndjson" ($policy -join "`n")
    $all = Get-Plan
    Assert-True ($all.tests.Count -eq 9 -and $all.selectedCount -eq 9) "All includes .NET and every non-.NET surface."
    $fast = Get-Plan @{ Fast = $true; ChangedPath = @("tests/Tier3/Tests.cs") }
    Assert-True (@($fast.tests | Where-Object { $_.kind -ne "dotnet-project" -and $_.selected }).Count -eq 0) "Unrelated client and scripts must not join an isolated test-project change."
    $clientChange = Get-Plan @{ Fast = $true; ChangedPath = @("samples/LmStreaming.Sample/ClientApp/src/example.test.ts") }
    Assert-True (($clientChange.tests | Where-Object kind -eq "vitest-file").selected -and @($clientChange.tests | Where-Object { $_.kind -eq "dotnet-project" -and $_.selected }).Count -eq 0) "Known client component scope cannot fall back into unrelated core projects."
    $global:PriorityTestInvocations.Clear()
    function global:dotnet { $global:PriorityTestInvocations.Add(@("dotnet") + @($args)); $global:LASTEXITCODE = 0 }
    function global:pwsh { $global:PriorityTestInvocations.Add(@("pwsh") + @($args)); $global:LASTEXITCODE = 0 }
    function global:python { $global:PriorityTestInvocations.Add(@("python") + @($args)); $global:LASTEXITCODE = 0 }
    function global:npm { $global:PriorityTestInvocations.Add(@("npm") + @($args)); $global:LASTEXITCODE = 0 }
    $paths = @(($all.tests | Where-Object priority -eq "P1").path)
    & $runner -RepositoryRoot $fixture -Priority P1 -Execute -ApprovedPath $paths | Out-Null
    Assert-True ($global:PriorityTestInvocations.Count -eq 4) "Approved mixed P1 executes one command per selected surface."
    $pythonArgs = @($global:PriorityTestInvocations | Where-Object { $_[0] -eq "python" })[0]
    Assert-True ($pythonArgs -contains "pytest") "Python must use pytest rather than execute module imports directly."
    $npmArgs = @($global:PriorityTestInvocations | Where-Object { $_[0] -eq "npm" })[0]
    # PowerShell consumes -- when binding a function mock; native invocation retains it.
    foreach ($arg in @("--prefix", "exec", "--no", "vitest", "run", "--root", "src/example.test.ts")) {
        Assert-True ($npmArgs -contains $arg) "Vitest dispatch must contain $arg to use local dependencies and run without watching."
    }
    $pwshArgs = @($global:PriorityTestInvocations | Where-Object { $_[0] -eq "pwsh" })[0]
    Assert-True ($pwshArgs -contains "-NoProfile" -and $pwshArgs -contains "-File") "PowerShell dispatch must be a profile-free file invocation."
    # Kind narrows which surfaces a run covers when another modality's dependencies are not
    # provisioned. It must exclude the other modality outright, not merely reorder it.
    $dotnetOnly = Get-Plan @{ Priority = @("P1"); Kind = @("dotnet-project") }
    Assert-True (
        @($dotnetOnly.tests | Where-Object { $_.selected -and $_.kind -ne "dotnet-project" }).Count -eq 0 -and
        @($dotnetOnly.tests | Where-Object { $_.selected -and $_.kind -eq "dotnet-project" }).Count -gt 0
    ) "Kind must restrict the selection to the requested surface kinds while keeping them."
    Assert-True (
        @($all.tests | Where-Object { $_.selected -and $_.kind -eq "vitest-file" }).Count -gt 0
    ) "The unfiltered fixture must still select a client surface, or the Kind assertion proves nothing."
    $global:PriorityTestInvocations.Clear()
    & $runner -RepositoryRoot $fixture -Priority P1 -Kind dotnet-project -Execute -ApproveSelectedProjects | Out-Null
    Assert-True (
        @($global:PriorityTestInvocations | Where-Object { $_[0] -ceq "npm" }).Count -eq 0 -and
        @($global:PriorityTestInvocations | Where-Object { $_[0] -ceq "dotnet" }).Count -gt 0
    ) "A kind-restricted run must never dispatch an excluded modality's command."
    $global:PriorityTestInvocations.Clear()
    $blocked = $false
    try { & $runner -RepositoryRoot $fixture -Priority P3 -Execute -ApprovedPath @(($all.tests | Where-Object priority -eq "P3").path) | Out-Null } catch { $blocked = $_.Exception.Message -like '*manual scenario*' }
    Assert-True ($blocked -and $global:PriorityTestInvocations.Count -eq 0) "Manual scenario blocks all selected commands before execution."
    $repositoryPlan = & $runner -RepositoryRoot (Join-Path $PSScriptRoot "..") | ConvertFrom-Json
    Assert-True ($repositoryPlan.policyStatus -eq "present") "Checked-in priority manifest must match the actual inventory."
    Assert-True (@($repositoryPlan.tests | Where-Object priority -eq "unassigned").Count -eq 0) "Every current test surface must have an explicit or kind-default assignment."
    Assert-True (@($repositoryPlan.tests | Where-Object priority -eq "P0").Count -gt 0) "Component baseline assignments must not become empty."
    Assert-True ($repositoryPlan.declarationSummary.known -eq 9524 -and $repositoryPlan.declarationSummary.reviewed -eq 9524) "Every known .NET/script declaration must have exactly one reviewed policy row."
    foreach ($repositoryTier in @("P0", "P1")) {
        $tierPlan = & $runner -RepositoryRoot (Join-Path $PSScriptRoot "..") -Priority $repositoryTier | ConvertFrom-Json
        $unsupportedTierSubsets = @(
            $tierPlan.tests |
            Where-Object {
                $_.selected -and
                $_.kind -eq "dotnet-project" -and
                $_.declarations.Count -gt 0 -and
                @($_.declarations | Where-Object selected).Count -lt $_.declarations.Count -and
                $_.filter.mode -ne "subset"
            }
        )
        Assert-True ($unsupportedTierSubsets.Count -eq 0) "The current $repositoryTier selection must not contain a dotnet subset that execution preflight rejects."
    }
    $repositoryDeclarations = @($repositoryPlan.tests | ForEach-Object { @($_.declarations) })
    foreach ($tier in @(@("P0", 277), @("P1", 7250), @("P2", 1870), @("P3", 127))) {
        Assert-True (@($repositoryDeclarations | Where-Object priority -eq $tier[0]).Count -eq $tier[1]) "Checked-in declaration count for $($tier[0]) must match the reviewed corpus."
    }
    Assert-True (@($repositoryDeclarations | Where-Object reviewState -ne "reviewed").Count -eq 0) "The checked-in declaration policy cannot contain unreviewed families."
    $manifestRows = @([System.IO.File]::ReadAllLines((Join-Path $PSScriptRoot "test-priorities.ndjson")) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | ForEach-Object { $_ | ConvertFrom-Json })
    Assert-True (@($manifestRows | Where-Object kind -ne "test-declaration").Count -eq 39) "Manifest integration must preserve all 39 container/default rows."
    Assert-True (@($manifestRows | Where-Object { $_.kind -eq "test-declaration" -and $_.p1FloorExemption -eq "vacuous" }).Count -eq 5) "Manifest integration must preserve the five reviewed vacuity exemptions."
    # Measured rows only ever leave this corpus when the declaration itself is deleted upstream;
    # nothing in this tooling may downgrade a measured row to an unmeasured one.
    Assert-True (@($manifestRows | Where-Object { $_.kind -eq "test-declaration" -and $_.coverageEvidence -eq "measured" }).Count -eq 8953) "Manifest integration must preserve every measured coverage classification whose declaration still exists."
    Assert-True (@($manifestRows | Where-Object { $_.kind -eq "test-declaration" -and $_.coverageEvidence -eq "no-coverage-capture" }).Count -eq 104) "Manifest integration must preserve approved projects without coverage capture."
    Assert-True (@($manifestRows | Where-Object { $_.kind -eq "test-declaration" -and $_.coverageEvidence -eq "not-in-capture" }).Count -eq 467) "Manifest integration must preserve declarations absent from the frozen capture."
    Assert-True (@($manifestRows | Where-Object { $_.kind -eq "test-declaration" -and $_.path -like "samples/LmStreaming.Sample/ClientApp/*" }).Count -eq 0) "Client tests remain whole-suite and must not acquire declaration rows in this phase."
    foreach ($changed in @("samples/LmStreaming.Sample/Program.cs", "src/LmStreaming.AspNetCore/SelectionProbe.cs")) {
        $scopedPlan = & $runner -RepositoryRoot (Join-Path $PSScriptRoot "..") -Fast -ChangedPath $changed | ConvertFrom-Json
        Assert-True ($scopedPlan.impact.mode -eq "selected" -and $scopedPlan.impact.reason -eq "project-closure") "High-level source changes must use consumer closure, not a full fallback."
        Assert-True (@($scopedPlan.tests | Where-Object { $_.selected -and $_.path -like "tests/LmCore.Tests/*" }).Count -eq 0) "High-level changes must exclude unrelated LmCore tests."
        Assert-True (@($scopedPlan.tests | Where-Object { $_.selected -and $_.path -like "tests/LmStreaming.Sample.Tests/*" }).Count -eq 1) "High-level changes must still include the Sample test consumer."
    }
    Set-FixtureFile "tests/Tier0/Tests.cs" 'namespace Cases; public class Tests { [Xunit.Fact] public void Critical() {} [Xunit.Theory, Xunit.InlineData(1), Xunit.InlineData(2)] public void Regression(int value) {} }'
    $sourceRows = @(& (Join-Path $PSScriptRoot "Get-TestInventory.ps1") -RepositoryRoot $fixture -IncludeDeclarations | ForEach-Object { $_ | ConvertFrom-Json })
    $caseRules = @($sourceRows | Where-Object { $_.kind -eq "test-declaration" -and $_.path -eq "tests/Tier0/Tier0.csproj" } | ForEach-Object {
        @{ kind = "test-declaration"; id = $_.id; path = $_.path; sourceHash = $_.sourceHash; priority = $(if ($_.method -eq "Critical") { "P0" } else { "P2" }); rationale = "Fixture assertion contract"; evidence = @("tests/Tier0/Tests.cs:1"); reviewState = "reviewed"; requirements = @("not-audited"); component = "fixture tier zero"; uncertainties = @(); coverageEvidence = "not-in-capture" } | ConvertTo-Json -Compress
    })
    Set-FixtureFile "scripts/test-priorities.ndjson" (($policy + $caseRules) -join "`n")
    $casePlan = Get-Plan @{ Project = @("tests/Tier0/Tier0.csproj"); Priority = @("P0") }
    Assert-True ($casePlan.policyStatus -eq "present") "Declaration policies must coexist with container records in the existing manifest."
    Assert-True (
        @($casePlan.tests | Where-Object { $_.requirements -isnot [array] }).Count -eq 0
    ) "Every surface requirements field must remain a JSON array."
    Assert-True (
        @($casePlan.tests | ForEach-Object { @($_.declarations) } | ForEach-Object { $_ } | Where-Object { $_.requirements -isnot [array] -or $_.declarationGaps -isnot [array] -or $_.skipConditions -isnot [array] }).Count -eq 0
    ) "Every declaration requirements, declarationGaps and skipConditions field must remain a JSON array."
    Assert-True (
        @($casePlan.tests | Where-Object { $_.declarationGaps -isnot [array] }).Count -eq 0
    ) "Every surface declarationGaps field must remain a JSON array."
    $invalidCaseRule = $caseRules[0] | ConvertFrom-Json
    $invalidCaseRule.component = ""
    Set-FixtureFile "scripts/test-priorities.ndjson" (($policy + @($invalidCaseRule | ConvertTo-Json -Compress) + @($caseRules | Select-Object -Skip 1)) -join "`n")
    Assert-True ((Get-Plan).policyStatus -eq "invalid") "A declaration rule without a component must make the policy invalid."
    $invalidCaseRule = $caseRules[0] | ConvertFrom-Json
    $invalidCaseRule.PSObject.Properties.Remove("sourceHash")
    Set-FixtureFile "scripts/test-priorities.ndjson" (($policy + @($invalidCaseRule | ConvertTo-Json -Compress) + @($caseRules | Select-Object -Skip 1)) -join "`n")
    Assert-True ((Get-Plan).policyStatus -eq "invalid") "A declaration rule must explicitly carry sourceHash, including null for atomic scripts."
    foreach ($field in @("evidence", "requirements")) {
        $invalidCaseRule = $caseRules[0] | ConvertFrom-Json
        $invalidCaseRule.$field = "not-an-array"
        Set-FixtureFile "scripts/test-priorities.ndjson" (($policy + @($invalidCaseRule | ConvertTo-Json -Compress) + @($caseRules | Select-Object -Skip 1)) -join "`n")
        Assert-True ((Get-Plan).policyStatus -eq "invalid") "Declaration $field must be an array, not a scalar string."
    }
    $invalidCaseRule = $caseRules[0] | ConvertFrom-Json
    $invalidCaseRule.requirements = @("offline")
    Set-FixtureFile "scripts/test-priorities.ndjson" (($policy + @($invalidCaseRule | ConvertTo-Json -Compress) + @($caseRules | Select-Object -Skip 1)) -join "`n")
    Assert-True ((Get-Plan).policyStatus -eq "invalid") "Declaration requirements must retain not-audited."
    $invalidCaseRule = $caseRules[0] | ConvertFrom-Json
    $invalidCaseRule.uncertainties = "not-an-array"
    Set-FixtureFile "scripts/test-priorities.ndjson" (($policy + @($invalidCaseRule | ConvertTo-Json -Compress) + @($caseRules | Select-Object -Skip 1)) -join "`n")
    Assert-True ((Get-Plan).policyStatus -eq "invalid") "Declaration uncertainties must be an array."
    $invalidCaseRule = $caseRules[0] | ConvertFrom-Json
    $invalidCaseRule | Add-Member -NotePropertyName p1FloorExemption -NotePropertyValue "unsupported"
    Set-FixtureFile "scripts/test-priorities.ndjson" (($policy + @($invalidCaseRule | ConvertTo-Json -Compress) + @($caseRules | Select-Object -Skip 1)) -join "`n")
    Assert-True ((Get-Plan).policyStatus -eq "invalid") "Only the reviewed vacuous floor exemption is valid."
    $invalidCaseRule = $caseRules[0] | ConvertFrom-Json
    $invalidCaseRule.coverageEvidence = "unknown"
    Set-FixtureFile "scripts/test-priorities.ndjson" (($policy + @($invalidCaseRule | ConvertTo-Json -Compress) + @($caseRules | Select-Object -Skip 1)) -join "`n")
    Assert-True ((Get-Plan).policyStatus -eq "invalid") "Coverage evidence must use the durable three-state vocabulary."
    $invalidCaseRule = $caseRules[0] | ConvertFrom-Json
    $invalidCaseRule | Add-Member -NotePropertyName coverageBasis -NotePropertyValue ([pscustomobject]@{ greedyRank = 1; marginalNewSpans = 2; exclusiveSpans = 1; addsNothingNew = $false; inP0Prefix = $true })
    Set-FixtureFile "scripts/test-priorities.ndjson" (($policy + @($invalidCaseRule | ConvertTo-Json -Compress) + @($caseRules | Select-Object -Skip 1)) -join "`n")
    Assert-True ((Get-Plan).policyStatus -eq "invalid") "An unmeasured declaration must not carry a coverage basis."
    $invalidCaseRule = $caseRules[0] | ConvertFrom-Json
    $invalidCaseRule.coverageEvidence = "measured"
    Set-FixtureFile "scripts/test-priorities.ndjson" (($policy + @($invalidCaseRule | ConvertTo-Json -Compress) + @($caseRules | Select-Object -Skip 1)) -join "`n")
    Assert-True ((Get-Plan).policyStatus -eq "invalid") "Measured coverage evidence must carry its compact basis."
    $invalidCaseRule | Add-Member -NotePropertyName coverageBasis -NotePropertyValue ([pscustomobject]@{ greedyRank = $null; marginalNewSpans = -1; exclusiveSpans = 0; addsNothingNew = $true; inP0Prefix = $false })
    Set-FixtureFile "scripts/test-priorities.ndjson" (($policy + @($invalidCaseRule | ConvertTo-Json -Compress) + @($caseRules | Select-Object -Skip 1)) -join "`n")
    Assert-True ((Get-Plan).policyStatus -eq "invalid") "Measured coverage counts must be nonnegative integers."
    $invalidCaseRule.coverageBasis.marginalNewSpans = 0
    $invalidCaseRule.coverageBasis.addsNothingNew = "true"
    Set-FixtureFile "scripts/test-priorities.ndjson" (($policy + @($invalidCaseRule | ConvertTo-Json -Compress) + @($caseRules | Select-Object -Skip 1)) -join "`n")
    Assert-True ((Get-Plan).policyStatus -eq "invalid") "Measured coverage flags must be booleans."
    $invalidCaseRule.coverageBasis.addsNothingNew = $true
    Set-FixtureFile "scripts/test-priorities.ndjson" (($policy + @($invalidCaseRule | ConvertTo-Json -Compress) + @($caseRules | Select-Object -Skip 1)) -join "`n")
    Assert-True ((Get-Plan).policyStatus -eq "present") "A complete typed measured coverage basis must remain valid."
    Set-FixtureFile "scripts/test-priorities.ndjson" (($policy + $caseRules) -join "`n")
    Assert-True ($casePlan.declarationSummary.known -ge 2 -and $casePlan.declarationSummary.reviewed -eq $casePlan.declarationSummary.known) "Report complete declaration coverage separately from container counts."
    $tier0 = $casePlan.tests | Where-Object path -eq "tests/Tier0/Tier0.csproj"
    Assert-True ($tier0.declarations.Count -eq 2 -and @($tier0.declarations | Where-Object selected).Count -eq 1) "Priority selection must distinguish individual methods in the same project."
    Assert-True (($tier0.declarations | Where-Object selected).method -eq "Critical") "P0 must not select an explicitly P2 theory family."
    Assert-True (($tier0.declarations | Where-Object method -eq "Regression").staticRowCount -eq 2) "Unselected theory rows remain accounted for."
    $changedProject = Get-Plan @{ ChangedPath = @("tests/Tier0/Tier0.csproj"); Priority = @("P2") }
    $changedProjectTier0 = $changedProject.tests | Where-Object path -eq "tests/Tier0/Tier0.csproj"
    Assert-True (
        @($changedProjectTier0.declarations | Where-Object selected).Count -eq
        $changedProjectTier0.declarations.Count
    ) "A directly changed test project file must retain every owned family regardless of an explicit priority subset."
    $missingRulePolicy = @($policy + @($caseRules | Select-Object -First 1))
    Set-FixtureFile "scripts/test-priorities.ndjson" ($missingRulePolicy -join "`n")
    $missingRule = Get-Plan @{ Project = @("tests/Tier0/Tier0.csproj"); Priority = @("P0") }
    Assert-True ($missingRule.policyStatus -eq "stale" -and @($missingRule.policyErrors | Where-Object { $_ -like "Missing declaration priority rule:*" }).Count -eq 1) "One missing declaration rule must make the policy stale instead of silently selecting an unassigned family."
    Set-FixtureFile "scripts/test-priorities.ndjson" ($policy -join "`n")
    $missingAllRules = Get-Plan @{ Project = @("tests/Tier0/Tier0.csproj"); Priority = @("P0") }
    Assert-True ($missingAllRules.policyStatus -eq "stale" -and @($missingAllRules.policyErrors | Where-Object { $_ -like "Missing declaration priority rule:*" }).Count -eq 2) "Removing every declaration rule must not silently restore whole-container policy."
    Set-FixtureFile "scripts/test-priorities.ndjson" (($policy + $caseRules) -join "`n")
    Set-FixtureFile "tests/Tier0/Tests.cs" 'namespace Cases; public class Tests { [Xunit.Fact] public void Critical() {} [Xunit.Fact] public void Critical(int value) {} [Xunit.Theory, Xunit.InlineData(1), Xunit.InlineData(2)] public void Regression(int value) {} }'
    $overloadRows = @(& (Join-Path $PSScriptRoot "Get-TestInventory.ps1") -RepositoryRoot $fixture -IncludeDeclarations | ForEach-Object { $_ | ConvertFrom-Json })
    $overloadRules = @($overloadRows | Where-Object { $_.kind -eq "test-declaration" -and $_.path -eq "tests/Tier0/Tier0.csproj" } | ForEach-Object {
        @{ kind = "test-declaration"; id = $_.id; path = $_.path; sourceHash = $_.sourceHash; priority = $(if ($_.method -eq "Regression") { "P0" } else { "P2" }); rationale = "Fixture overload contract"; evidence = @("tests/Tier0/Tests.cs:1"); reviewState = "reviewed"; requirements = @("not-audited"); component = "fixture overload"; uncertainties = @(); coverageEvidence = "not-in-capture" } | ConvertTo-Json -Compress
    })
    Set-FixtureFile "scripts/test-priorities.ndjson" (($policy + $overloadRules) -join "`n")
    $overloadPlan = Get-Plan @{ Project = @("tests/Tier0/Tier0.csproj"); Priority = @("P0") }
    $overloadTier0 = $overloadPlan.tests | Where-Object path -eq "tests/Tier0/Tier0.csproj"
    Assert-True (@($overloadTier0.declarations | Where-Object method -eq "Critical").Count -eq 2) "Stable declaration IDs must retain both overloaded source methods."
    Assert-True ($overloadTier0.filter.mode -eq "subset" -and $overloadTier0.filter.expression -eq "FullyQualifiedName=Cases.Tests.Regression") "An overload pair wholly outside the selection must not block an otherwise unambiguous subset."
    $mixedOverloadRules = @($overloadRows | Where-Object { $_.kind -eq "test-declaration" -and $_.path -eq "tests/Tier0/Tier0.csproj" } | ForEach-Object {
        @{ kind = "test-declaration"; id = $_.id; path = $_.path; sourceHash = $_.sourceHash; priority = $(if ($_.method -eq "Critical" -and -not $_.signature) { "P0" } else { "P2" }); rationale = "Fixture overload contract"; evidence = @("tests/Tier0/Tests.cs:1"); reviewState = "reviewed"; requirements = @("not-audited"); component = "fixture overload"; uncertainties = @(); coverageEvidence = "not-in-capture" } | ConvertTo-Json -Compress
    })
    Set-FixtureFile "scripts/test-priorities.ndjson" (($policy + $mixedOverloadRules) -join "`n")
    $mixedOverloadPlan = Get-Plan @{ Project = @("tests/Tier0/Tier0.csproj"); Priority = @("P0") }
    $mixedOverloadTier0 = $mixedOverloadPlan.tests | Where-Object path -eq "tests/Tier0/Tier0.csproj"
    Assert-True ($mixedOverloadTier0.filter.mode -eq "unsupported-ambiguous-executable-identity" -and $null -eq $mixedOverloadTier0.filter.expression) "Selecting only one overload must fail closed instead of emitting a class-and-method filter that selects both."
    $global:PriorityTestInvocations.Clear()
    $overloadBlocked = $false
    try { & $runner -RepositoryRoot $fixture -Project "tests/Tier0/Tier0.csproj" -Priority P0 -Execute -ApprovedPath "tests/Tier0/Tier0.csproj" | Out-Null }
    catch { $overloadBlocked = $_.Exception.Message -like '*no usable declaration filter*' }
    Assert-True ($overloadBlocked -and $global:PriorityTestInvocations.Count -eq 0) "Unsupported overload filtering must block before any test command starts."
    Set-FixtureFile "tests/Tier0/Tests.cs" 'namespace Cases; public abstract class BaseTests { [Xunit.Fact] public void Critical() {} } public abstract class IntermediateTests : BaseTests {} public sealed class ConcreteTests : IntermediateTests {} public class OtherTests { [Xunit.Fact] public void Regression() {} }'
    $inheritedRows = @(& (Join-Path $PSScriptRoot "Get-TestInventory.ps1") -RepositoryRoot $fixture -IncludeDeclarations | ForEach-Object { $_ | ConvertFrom-Json })
    $inheritedRules = @($inheritedRows | Where-Object { $_.kind -eq "test-declaration" -and $_.path -eq "tests/Tier0/Tier0.csproj" } | ForEach-Object {
        @{ kind = "test-declaration"; id = $_.id; path = $_.path; sourceHash = $_.sourceHash; priority = $(if ($_.method -eq "Critical") { "P0" } else { "P2" }); rationale = "Fixture inherited contract"; evidence = @("tests/Tier0/Tests.cs:1"); reviewState = "reviewed"; requirements = @("not-audited"); component = "fixture inheritance"; uncertainties = @(); coverageEvidence = "not-in-capture" } | ConvertTo-Json -Compress
    })
    Set-FixtureFile "scripts/test-priorities.ndjson" (($policy + $inheritedRules) -join "`n")
    $inheritedPlan = Get-Plan @{ Project = @("tests/Tier0/Tier0.csproj"); Priority = @("P0") }
    $inheritedTier0 = $inheritedPlan.tests | Where-Object path -eq "tests/Tier0/Tier0.csproj"
    Assert-True (($inheritedTier0.declarations | Where-Object method -eq "Critical").declarationGaps -contains "abstract-declaring-type-requires-discovery") "An abstract-base source family must expose that its concrete executable identity needs discovery."
    Assert-True ($inheritedTier0.filter.mode -eq "unsupported-inherited-executable-identity" -and $null -eq $inheritedTier0.filter.expression) "An inherited family subset must not emit the abstract base FQN as an executable filter."
    $global:PriorityTestInvocations.Clear()
    $inheritedBlocked = $false
    try { & $runner -RepositoryRoot $fixture -Project "tests/Tier0/Tier0.csproj" -Priority P0 -Execute -ApprovedPath "tests/Tier0/Tier0.csproj" | Out-Null }
    catch { $inheritedBlocked = $_.Exception.Message -like '*no usable declaration filter*' }
    Assert-True ($inheritedBlocked -and $global:PriorityTestInvocations.Count -eq 0) "Inherited source identity uncertainty must block before any test command starts."
    Set-FixtureFile "tests/Tier0/Tests.cs" 'namespace Cases; public abstract class BaseTests { [Xunit.Fact] public void Critical() {} } public sealed class FirstTests : BaseTests {} public sealed class SecondTests : BaseTests {} public class OtherTests { [Xunit.Fact] public void Regression() {} }'
    $resolvedInheritedRows = @(& (Join-Path $PSScriptRoot "Get-TestInventory.ps1") -RepositoryRoot $fixture -IncludeDeclarations | ForEach-Object { $_ | ConvertFrom-Json })
    $resolvedInheritedRules = @($resolvedInheritedRows | Where-Object { $_.kind -eq "test-declaration" -and $_.path -eq "tests/Tier0/Tier0.csproj" } | ForEach-Object {
        @{ kind = "test-declaration"; id = $_.id; path = $_.path; sourceHash = $_.sourceHash; priority = $(if ($_.method -eq "Critical") { "P0" } else { "P2" }); rationale = "Fixture resolved inheritance contract"; evidence = @("tests/Tier0/Tests.cs:1"); reviewState = "reviewed"; requirements = @("not-audited"); component = "fixture resolved inheritance"; uncertainties = @(); coverageEvidence = "not-in-capture" } | ConvertTo-Json -Compress
    })
    Set-FixtureFile "scripts/test-priorities.ndjson" (($policy + $resolvedInheritedRules) -join "`n")
    $resolvedInheritedPlan = Get-Plan @{ Project = @("tests/Tier0/Tier0.csproj"); Priority = @("P0") }
    $resolvedInheritedTier0 = $resolvedInheritedPlan.tests | Where-Object path -eq "tests/Tier0/Tier0.csproj"
    Assert-True (
        $resolvedInheritedTier0.filter.mode -eq "subset" -and
        ($resolvedInheritedTier0.filter.clauses -join ",") -eq
        "FullyQualifiedName=Cases.FirstTests.Critical,FullyQualifiedName=Cases.SecondTests.Critical" -and
        $resolvedInheritedTier0.filter.expectedCount -eq 2
    ) "A statically proven concrete inheritance set must emit one exact filter clause per runtime family."
    Set-FixtureFile "tests/Tier0/Tests.cs" 'namespace Cases; public abstract partial class PartialBase {} public partial class PartialBase { [Xunit.Fact] public void Critical() {} } public sealed class Concrete : PartialBase {} public class OtherTests { [Xunit.Fact] public void Regression() {} }'
    $partialRows = @(& (Join-Path $PSScriptRoot "Get-TestInventory.ps1") -RepositoryRoot $fixture -IncludeDeclarations | ForEach-Object { $_ | ConvertFrom-Json })
    $partialRules = @($partialRows | Where-Object { $_.kind -eq "test-declaration" -and $_.path -eq "tests/Tier0/Tier0.csproj" } | ForEach-Object {
        @{ kind = "test-declaration"; id = $_.id; path = $_.path; sourceHash = $_.sourceHash; priority = $(if ($_.method -eq "Critical") { "P0" } else { "P2" }); rationale = "Fixture partial contract"; evidence = @("tests/Tier0/Tests.cs:1"); reviewState = "reviewed"; requirements = @("not-audited"); component = "fixture partial"; uncertainties = @(); coverageEvidence = "not-in-capture" } | ConvertTo-Json -Compress
    })
    Set-FixtureFile "scripts/test-priorities.ndjson" (($policy + $partialRules) -join "`n")
    $partialPlan = Get-Plan @{ Project = @("tests/Tier0/Tier0.csproj"); Priority = @("P0") }
    $partialTier0 = $partialPlan.tests | Where-Object path -eq "tests/Tier0/Tier0.csproj"
    Assert-True (($partialTier0.declarations | Where-Object method -eq "Critical").declarationGaps -contains "partial-test-identity-requires-discovery") "A partial source family must expose that its executable identity needs discovery."
    Assert-True ($partialTier0.filter.mode -eq "unsupported-partial-executable-identity" -and $null -eq $partialTier0.filter.expression) "A partial family subset must not emit an unverified source FQN as an executable filter."
    $global:PriorityTestInvocations.Clear()
    $partialBlocked = $false
    try { & $runner -RepositoryRoot $fixture -Project "tests/Tier0/Tier0.csproj" -Priority P0 -Execute -ApprovedPath "tests/Tier0/Tier0.csproj" | Out-Null }
    catch { $partialBlocked = $_.Exception.Message -like '*no usable declaration filter*' }
    Assert-True ($partialBlocked -and $global:PriorityTestInvocations.Count -eq 0) "Partial source identity uncertainty must block before any test command starts."
    Set-FixtureFile "tests/Tier0/Tests.cs" 'namespace Cases; public class GenericTests<T> { [Xunit.Fact] public void Critical() {} } public class OtherTests { [Xunit.Fact] public void Regression() {} }'
    $genericRows = @(& (Join-Path $PSScriptRoot "Get-TestInventory.ps1") -RepositoryRoot $fixture -IncludeDeclarations | ForEach-Object { $_ | ConvertFrom-Json })
    $genericRules = @($genericRows | Where-Object { $_.kind -eq "test-declaration" -and $_.path -eq "tests/Tier0/Tier0.csproj" } | ForEach-Object {
        @{ kind = "test-declaration"; id = $_.id; path = $_.path; sourceHash = $_.sourceHash; priority = $(if ($_.method -eq "Critical") { "P0" } else { "P2" }); rationale = "Fixture generic contract"; evidence = @("tests/Tier0/Tests.cs:1"); reviewState = "reviewed"; requirements = @("not-audited"); component = "fixture generic"; uncertainties = @(); coverageEvidence = "not-in-capture" } | ConvertTo-Json -Compress
    })
    Set-FixtureFile "scripts/test-priorities.ndjson" (($policy + $genericRules) -join "`n")
    $genericPlan = Get-Plan @{ Project = @("tests/Tier0/Tier0.csproj"); Priority = @("P0") }
    $genericTier0 = $genericPlan.tests | Where-Object path -eq "tests/Tier0/Tier0.csproj"
    Assert-True (($genericTier0.declarations | Where-Object method -eq "Critical").declarationGaps -contains "generic-test-identity-requires-discovery") "A generic source family must expose that its executable identity needs discovery."
    Assert-True ($genericTier0.filter.mode -eq "unsupported-generic-executable-identity" -and $null -eq $genericTier0.filter.expression) "A generic family subset must not emit an unverified source FQN as an executable filter."
    $global:PriorityTestInvocations.Clear()
    $genericBlocked = $false
    try { & $runner -RepositoryRoot $fixture -Project "tests/Tier0/Tier0.csproj" -Priority P0 -Execute -ApprovedPath "tests/Tier0/Tier0.csproj" | Out-Null }
    catch { $genericBlocked = $_.Exception.Message -like '*no usable declaration filter*' }
    Assert-True ($genericBlocked -and $global:PriorityTestInvocations.Count -eq 0) "Generic source identity uncertainty must block before any test command starts."
    Set-FixtureFile "tests/Tier0/Tests.cs" 'namespace Cases; public class Tests { [MyFact] public void Hidden() {} [Xunit.Fact] public void Known() {} [Xunit.Fact] public void Regression() {} }'
    $customRows = @(& (Join-Path $PSScriptRoot "Get-TestInventory.ps1") -RepositoryRoot $fixture -IncludeDeclarations | ForEach-Object { $_ | ConvertFrom-Json })
    $customRules = @($customRows | Where-Object { $_.kind -eq "test-declaration" -and $_.path -eq "tests/Tier0/Tier0.csproj" } | ForEach-Object {
        @{ kind = "test-declaration"; id = $_.id; path = $_.path; sourceHash = $_.sourceHash; priority = $(if ($_.method -eq "Known") { "P0" } else { "P2" }); rationale = "Fixture custom attribute contract"; evidence = @("tests/Tier0/Tests.cs:1"); reviewState = "reviewed"; requirements = @("not-audited"); component = "fixture custom attribute"; uncertainties = @(); coverageEvidence = "not-in-capture" } | ConvertTo-Json -Compress
    })
    Set-FixtureFile "scripts/test-priorities.ndjson" (($policy + $customRules) -join "`n")
    $customPlan = Get-Plan @{ Project = @("tests/Tier0/Tier0.csproj"); Priority = @("P0") }
    $customTier0 = $customPlan.tests | Where-Object path -eq "tests/Tier0/Tier0.csproj"
    Assert-True ($customTier0.declarationGaps -contains "unresolved-test-attribute:tests/Tier0/Tests.cs:Hidden") "An unresolved test-shaped custom attribute must remain visible in the plan."
    Assert-True ($customTier0.filter.mode -eq "unsupported-unresolved-test-attribute" -and $null -eq $customTier0.filter.expression) "A project with an unresolved test-shaped attribute must not claim a complete executable subset."
    $global:PriorityTestInvocations.Clear()
    $customBlocked = $false
    try { & $runner -RepositoryRoot $fixture -Project "tests/Tier0/Tier0.csproj" -Priority P0 -Execute -ApprovedPath "tests/Tier0/Tier0.csproj" | Out-Null }
    catch { $customBlocked = $_.Exception.Message -like '*no usable declaration filter*' }
    Assert-True ($customBlocked -and $global:PriorityTestInvocations.Count -eq 0) "Unknown custom test identity must block before any test command starts."
    Set-FixtureFile "tests/Tier0/Tests.cs" 'namespace Cases; public class Tests { [Xunit.Fact] public void Critical() {} [Xunit.Theory, Xunit.InlineData(1), Xunit.InlineData(2)] public void Regression(int value) {} }'
    Set-FixtureFile "scripts/test-priorities.ndjson" (($policy + $caseRules) -join "`n")
    # The exact-family filter is now proven offline against both adapter generations
    # (see conversation_memories/coverage-based-test-selection/filter-proof.md), so a
    # supported non-overloaded subset executes filtered. The unfiltered fallback stays forbidden.
    Assert-True ($null -ne $tier0.filter -and $tier0.filter.mode -eq "subset") "A partially selected project must preview the filter it will run."
    Assert-True ($tier0.filter.expectedCount -eq 1) "The filter must carry the count to assert against, because a zero-match filter exits 0."
    Assert-True ($tier0.filter.clauses.Count -eq 1 -and $tier0.filter.clauses[0] -eq "FullyQualifiedName=Cases.Tests.Critical") "Filter clauses must be exact declaration FQNs computed from the inventory."
    Assert-True ($tier0.filter.expression -notmatch '~') "Contains-matching bleeds into sibling methods and must never be generated."
    # A subset run reconciles the families the adapter reported. The fake consumes one
    # report plan per invocation so wrong-batch and multi-TRX behavior are observable.
    function global:dotnet {
        $argv = @($args)
        $global:PriorityTestInvocations.Add($argv)
        $directoryIndex = [array]::IndexOf($argv, "--results-directory")
        if ($directoryIndex -ge 0) {
            $directory = $argv[$directoryIndex + 1]
            New-Item -ItemType Directory -Path $directory -Force | Out-Null
            $plan = @($global:PriorityTrxPlans)[$global:PriorityTrxPlanIndex]
            $global:PriorityTrxPlanIndex++
            if ($null -ne $plan) {
                $reports = if ($plan.ContainsKey("Reports")) { @($plan.Reports) } else { @($plan) }
                $fileIndex = 0
                foreach ($report in $reports) {
                    $fileIndex++
                    $relativePath = if ($report.ContainsKey("Path")) { $report.Path } else { "result-$fileIndex.trx" }
                    $trxPath = Join-Path $directory $relativePath
                    New-Item -ItemType Directory -Path (Split-Path $trxPath -Parent) -Force | Out-Null
                    $content = if ($report.ContainsKey("Content")) {
                        $report.Content
                    }
                    elseif ($report.ContainsKey("Definitions")) {
                        New-TrxDocument -Results @($report.Results) -Definitions @($report.Definitions)
                    }
                    else {
                        New-TrxDocument -Results @($report.Results)
                    }
                    [System.IO.File]::WriteAllText($trxPath, $content)
                }
            }
        }
        # An opt-in failing filter lets a fixture distinguish "the next tier never started"
        # from "the next tier ran and happened to pass".
        $filterIndex = [array]::IndexOf($argv, "--filter")
        $filterValue = if ($filterIndex -ge 0) { [string]$argv[$filterIndex + 1] } else { "" }
        if (-not [string]::IsNullOrEmpty($global:PriorityFailingFilter) -and $filterValue.Contains($global:PriorityFailingFilter)) {
            $global:LASTEXITCODE = 1
            return
        }
        $global:LASTEXITCODE = 0
    }
    Set-TrxPlans @(@(@{ Results = @(@{ Id = "critical"; Name = "Critical"; Outcome = "Passed"; Class = "Cases.Tests"; Method = "Critical" }) }))
    $global:PriorityTestInvocations.Clear()
    & $runner -RepositoryRoot $fixture -Project "tests/Tier0/Tier0.csproj" -Priority P0 -Execute -ApprovedPath "./tests/Tier0/Tier0.csproj" | Out-Null
    Assert-True ($global:PriorityTestInvocations.Count -eq 1) "An approved method subset must execute exactly once, including an equivalent repository-relative path with a leading ./ prefix."
    $subsetArgs = $global:PriorityTestInvocations[0]
    Assert-True ($subsetArgs -contains "--filter") "A method subset must never execute as an unfiltered project."
    $filterValue = $subsetArgs[[array]::IndexOf($subsetArgs, "--filter") + 1]
    Assert-True ($filterValue -eq "FullyQualifiedName=Cases.Tests.Critical") "The executed filter must select exactly the reviewed P0 family."
    Assert-True ($filterValue -notmatch '~' -and $filterValue -notmatch 'Regression') "The excluded P2 theory family must not appear in the filter."
    Set-FixtureFile "tests/Tier0/Tests.cs" 'namespace Cases; public class Tests { [Xunit.Fact] public void Critical() { throw new System.Exception(); } [Xunit.Theory, Xunit.InlineData(1), Xunit.InlineData(2)] public void Regression(int value) {} }'
    $changedBody = Get-Plan @{ Project = @("tests/Tier0/Tier0.csproj"); Priority = @("P0") }
    Assert-True ($changedBody.policyStatus -eq "stale") "Changed assertions must invalidate the old reviewed declaration policy."
    Set-FixtureFile "tests/Tier0/Tests.cs" 'namespace Cases; public class ExceptionallyLongDeclaringTypeNameForFilterLimit { [Xunit.Fact] public void Critical() {} [Xunit.Fact] public void Regression() {} }'
    $longRows = @(& (Join-Path $PSScriptRoot "Get-TestInventory.ps1") -RepositoryRoot $fixture -IncludeDeclarations | ForEach-Object { $_ | ConvertFrom-Json })
    $longRules = @($longRows | Where-Object { $_.kind -eq "test-declaration" -and $_.path -eq "tests/Tier0/Tier0.csproj" } | ForEach-Object {
        @{ kind = "test-declaration"; id = $_.id; path = $_.path; sourceHash = $_.sourceHash; priority = $(if ($_.method -eq "Critical") { "P0" } else { "P2" }); rationale = "Fixture filter-length contract"; evidence = @("tests/Tier0/Tests.cs:1"); reviewState = "reviewed"; requirements = @("not-audited"); component = "fixture filter length"; uncertainties = @(); coverageEvidence = "not-in-capture" } | ConvertTo-Json -Compress
    })
    Set-FixtureFile "scripts/test-priorities.ndjson" (($policy + $longRules) -join "`n")
    $longPlan = Get-Plan @{ Project = @("tests/Tier0/Tier0.csproj"); Priority = @("P0"); MaxFilterLength = 40 }
    $longTier0 = $longPlan.tests | Where-Object path -eq "tests/Tier0/Tier0.csproj"
    Assert-True ($longTier0.filter.mode -eq "unsupported-filter-clause-too-long" -and $longTier0.filter.clauseLength -gt 40) "One clause longer than the command budget must be refused rather than emitted unchanged."
    $global:PriorityTestInvocations.Clear()
    $longBlocked = $false
    try { & $runner -RepositoryRoot $fixture -Project "tests/Tier0/Tier0.csproj" -Priority P0 -MaxFilterLength 40 -Execute -ApprovedPath "tests/Tier0/Tier0.csproj" | Out-Null }
    catch { $longBlocked = $_.Exception.Message -like '*no usable declaration filter*' }
    Assert-True ($longBlocked -and $global:PriorityTestInvocations.Count -eq 0) "An individually oversized filter clause must block before any test command starts."
    # A large P0 selection can exceed the command-line length limit. Batching must be
    # deterministic and non-overlapping: a duplicated family would run twice and a dropped
    # one would vanish silently, because a zero-match filter exits 0.
    Set-FixtureFile "tests/Tier0/Tests.cs" 'namespace Cases; public class Tests { [Xunit.Fact] public void Critical() {} [LinuxOnlyFact("fixture platform condition")] public void Second() {} [Xunit.Theory, Xunit.InlineData(1), Xunit.InlineData(2)] public void Regression(int value) {} }'
    $batchRows = @(& (Join-Path $PSScriptRoot "Get-TestInventory.ps1") -RepositoryRoot $fixture -IncludeDeclarations | ForEach-Object { $_ | ConvertFrom-Json })
    $batchRules = @($batchRows | Where-Object { $_.kind -eq "test-declaration" -and $_.path -eq "tests/Tier0/Tier0.csproj" } | ForEach-Object {
        @{ kind = "test-declaration"; id = $_.id; path = $_.path; sourceHash = $_.sourceHash; priority = $(if ($_.method -eq "Regression") { "P2" } else { "P0" }); rationale = "Fixture batching contract"; evidence = @("tests/Tier0/Tests.cs:1"); reviewState = "reviewed"; requirements = @("not-audited"); component = "fixture batching"; uncertainties = @(); coverageEvidence = "not-in-capture" } | ConvertTo-Json -Compress
    })
    Set-FixtureFile "scripts/test-priorities.ndjson" (($policy + $batchRules) -join "`n")
    $unbatched = Get-Plan @{ Project = @("tests/Tier0/Tier0.csproj"); Priority = @("P0") }
    $unbatchedTier0 = $unbatched.tests | Where-Object path -eq "tests/Tier0/Tier0.csproj"
    Assert-True ($unbatchedTier0.filter.expectedCount -eq 2 -and $unbatchedTier0.filter.batches.Count -eq 1) "A filter within the length budget must stay a single batch."
    $batched = Get-Plan @{ Project = @("tests/Tier0/Tier0.csproj"); Priority = @("P0"); MaxFilterLength = 40 }
    $batchTier0 = $batched.tests | Where-Object path -eq "tests/Tier0/Tier0.csproj"
    Assert-True ($batchTier0.filter.batches.Count -eq 2) "An over-length filter must split into batches."
    $batchClauses = @($batchTier0.filter.batches | ForEach-Object { $_.clauses })
    Assert-True ($batchClauses.Count -eq 2 -and @($batchClauses | Sort-Object -Unique).Count -eq 2) "Batches must be non-overlapping."
    Assert-True (@(Compare-Object $batchClauses $batchTier0.filter.clauses).Count -eq 0) "Batch union must equal the whole selection: a dropped family exits 0 and looks green."
    Assert-True ((@($batchTier0.filter.batches | ForEach-Object { $_.expectedCount }) | Measure-Object -Sum).Sum -eq $batchTier0.filter.expectedCount) "Per-batch expected counts must sum to the selection."
    Set-TrxPlans @(
        @(@{ Results = @(@{ Id = "critical"; Name = "Critical"; Outcome = "Passed"; Class = "Cases.Tests"; Method = "Critical" }) }),
        @(@{ Results = @(@{ Id = "second"; Name = "Second"; Outcome = "Passed"; Class = "Cases.Tests"; Method = "Second" }) })
    )
    $global:PriorityTestInvocations.Clear()
    & $runner -RepositoryRoot $fixture -Project "tests/Tier0/Tier0.csproj" -Priority P0 -MaxFilterLength 40 -Execute -ApprovedPath "tests/Tier0/Tier0.csproj" | Out-Null
    Assert-True ($global:PriorityTestInvocations.Count -eq 2) "Each batch must execute as its own filtered command."
    $executedFilters = @($global:PriorityTestInvocations | ForEach-Object { $_[[array]::IndexOf($_, "--filter") + 1] })
    Assert-True (@($executedFilters | Sort-Object -Unique).Count -eq 2) "Executed batches must not repeat a filter."
    Assert-True (@($executedFilters | Where-Object { $_ -match 'Regression' }).Count -eq 0) "Batching must never widen the selection to an excluded family."

    # A zero-match filter exits 0, byte-identical to a passing run, so the exit code alone
    # cannot tell "ran the selection" from "ran nothing". The static expectedCount guard
    # only proves we INTENDED to run something. These check the families the adapter
    # actually reported back, which is the only evidence the selection executed.
    Set-TrxPlans @(@(@{ Results = @(
        @{ Id = "critical"; Name = "Critical"; Outcome = "Passed"; Class = "Cases.Tests"; Method = "Critical" },
        @{ Id = "second"; Name = "Second"; Outcome = "Passed"; Class = "Cases.Tests"; Method = "Second" }
    ) }))
    $global:PriorityTestInvocations.Clear()
    & $runner -RepositoryRoot $fixture -Project "tests/Tier0/Tier0.csproj" -Priority P0 -Execute -ApprovedPath "tests/Tier0/Tier0.csproj" | Out-Null
    $countedArgs = $global:PriorityTestInvocations[0]
    Assert-True ($countedArgs -contains "--results-directory" -and @($countedArgs | Where-Object { $_ -like "trx*" }).Count -eq 1) "Asserting the executed selection needs a structured result, not a parsed console summary."

    # The filter matched nothing, or matched a renamed family: the run still exits 0.
    Set-TrxPlans @(@(@{ Results = @(@{ Id = "critical"; Name = "Critical"; Outcome = "Passed"; Class = "Cases.Tests"; Method = "Critical" }) }))
    $missed = $false
    try { & $runner -RepositoryRoot $fixture -Project "tests/Tier0/Tier0.csproj" -Priority P0 -Execute -ApprovedPath "tests/Tier0/Tier0.csproj" | Out-Null }
    catch { $missed = $_.Exception.Message -like '*Selected but never reported*' }
    Assert-True $missed "A selected family that never reported a result must fail the run, because a zero-match filter exits 0 and looks green."

    # The opposite direction: the adapter ran a family the selection excluded.
    Set-TrxPlans @(@(@{ Results = @(
        @{ Id = "critical"; Name = "Critical"; Outcome = "Passed"; Class = "Cases.Tests"; Method = "Critical" },
        @{ Id = "second"; Name = "Second"; Outcome = "Passed"; Class = "Cases.Tests"; Method = "Second" },
        @{ Id = "regression"; Name = "Regression(value: 1)"; Outcome = "Passed"; Class = "Cases.Tests"; Method = "Regression" }
    ) }))
    $bled = $false
    try { & $runner -RepositoryRoot $fixture -Project "tests/Tier0/Tier0.csproj" -Priority P0 -Execute -ApprovedPath "tests/Tier0/Tier0.csproj" | Out-Null }
    catch { $bled = $_.Exception.Message -like '*Reported but not selected*' }
    Assert-True $bled "A family outside the selection reporting a result must fail the run: the filter widened."

    # Theory-style display suffixes do not change the definition's exact family identity.
    Set-TrxPlans @(@(@{ Results = @(
        @{ Id = "critical"; Name = "Critical"; Outcome = "Passed"; Class = "Cases.Tests"; Method = "Critical" },
        @{ Id = "second-1"; Name = "Second(value: 1)"; Outcome = "Passed"; Class = "Cases.Tests"; Method = "Second" },
        @{ Id = "second-2"; Name = "Second(value: 2)"; Outcome = "Passed"; Class = "Cases.Tests"; Method = "Second" }
    ) }))
    $rowsOk = $true
    try { & $runner -RepositoryRoot $fixture -Project "tests/Tier0/Tier0.csproj" -Priority P0 -Execute -ApprovedPath "tests/Tier0/Tier0.csproj" | Out-Null }
    catch { $rowsOk = $false }
    Assert-True $rowsOk "Parameterized display suffixes must reconcile through the exact test definition family."

    # A compatibility fallback is permitted only when a bare result identifies one selected family.
    $bareXml = New-TrxDocument -Results @(
        @{ Name = "Critical"; Outcome = "Passed" },
        @{ Name = "Second"; Outcome = "Passed" }
    ) -WithoutDefinitions
    Set-TrxPlans @(@(@{ Content = $bareXml }))
    $bareOk = $true
    try { & $runner -RepositoryRoot $fixture -Project "tests/Tier0/Tier0.csproj" -Priority P0 -Execute -ApprovedPath "tests/Tier0/Tier0.csproj" | Out-Null }
    catch { $bareOk = $false }
    Assert-True $bareOk "An unambiguous genuinely bare method name from an older adapter must reconcile to its selected declaration."

    $dottedXml = New-TrxDocument -Results @(
        @{ Name = "Critical"; Outcome = "Passed" },
        @{ Name = "Other.Type.Second"; Outcome = "Passed" }
    ) -WithoutDefinitions
    Set-TrxPlans @(@(@{ Content = $dottedXml }))
    $dottedLookalike = $false
    try { & $runner -RepositoryRoot $fixture -Project "tests/Tier0/Tier0.csproj" -Priority P0 -Execute -ApprovedPath "tests/Tier0/Tier0.csproj" | Out-Null }
    catch { $dottedLookalike = $_.Exception.Message -like '*Reported but not selected*' }
    Assert-True $dottedLookalike "A definitionless dotted display identity must not be reduced to a bare selected method name."

    # Definitions take precedence over a lookalike display name.
    Set-TrxPlans @(@(@{ Results = @(
        @{ Id = "critical"; Name = "Critical"; Outcome = "Passed"; Class = "Cases.Tests"; Method = "Critical" },
        @{ Id = "second"; Name = "Second"; Outcome = "Passed"; Class = "Cases.Tests"; Method = "Second" },
        @{ Id = "lookalike"; Name = "Second"; Outcome = "Passed"; Class = "Other.Suite"; Method = "Second" }
    ) }))
    $lookalike = $false
    try { & $runner -RepositoryRoot $fixture -Project "tests/Tier0/Tier0.csproj" -Priority P0 -Execute -ApprovedPath "tests/Tier0/Tier0.csproj" | Out-Null }
    catch { $lookalike = $_.Exception.Message -like '*Reported but not selected*' }
    Assert-True $lookalike "Definition identity must not absorb another class with the same method name."

    # A present testId is authoritative. Missing its definition is structural corruption,
    # not permission to reinterpret a display name.
    $danglingXml = New-TrxDocument -Results @(
        @{ Id = "critical"; Name = "Critical"; Outcome = "Passed" },
        @{ Id = "second"; Name = "Second"; Outcome = "Passed" }
    ) -WithoutDefinitions
    Set-TrxPlans @(@(@{ Content = $danglingXml }))
    $dangling = $false
    try { & $runner -RepositoryRoot $fixture -Project "tests/Tier0/Tier0.csproj" -Priority P0 -Execute -ApprovedPath "tests/Tier0/Tier0.csproj" | Out-Null }
    catch { $dangling = $_.Exception.Message -like '*testId*has no usable TestDefinition*' }
    Assert-True $dangling "A result testId without a usable definition must fail instead of falling back to its display name."

    $duplicateDefinitionXml = New-TrxDocument -Results @(
        @{ Id = "critical"; Name = "Critical"; Outcome = "Passed" },
        @{ Id = "second"; Name = "Second"; Outcome = "Passed" }
    ) -Definitions @(
        @{ Id = "critical"; Class = "Cases.Tests"; Method = "Critical" },
        @{ Id = "critical"; Class = "Other.Suite"; Method = "Critical" },
        @{ Id = "second"; Class = "Cases.Tests"; Method = "Second" }
    )
    Set-TrxPlans @(@(@{ Content = $duplicateDefinitionXml }))
    $duplicateDefinition = $false
    try { & $runner -RepositoryRoot $fixture -Project "tests/Tier0/Tier0.csproj" -Priority P0 -Execute -ApprovedPath "tests/Tier0/Tier0.csproj" | Out-Null }
    catch { $duplicateDefinition = $_.Exception.Message -like '*conflicting TestDefinitions*' }
    Assert-True $duplicateDefinition "Conflicting definitions for one testId must fail instead of using the last definition."

    # Reconciliation is per batch. Reporting both families in batch 1 cannot satisfy batch 2.
    Set-TrxPlans @(
        @(@{ Results = @(
            @{ Id = "critical"; Name = "Critical"; Outcome = "Passed"; Class = "Cases.Tests"; Method = "Critical" },
            @{ Id = "second"; Name = "Second"; Outcome = "Passed"; Class = "Cases.Tests"; Method = "Second" }
        ) }),
        @(@{ Results = @() })
    )
    $wrongBatch = $false
    try { & $runner -RepositoryRoot $fixture -Project "tests/Tier0/Tier0.csproj" -Priority P0 -MaxFilterLength 40 -Execute -ApprovedPath "tests/Tier0/Tier0.csproj" | Out-Null }
    catch {
        $wrongBatch =
            $_.Exception.Message -like '*batch 1*' -and
            $_.Exception.Message -like '*Reported but not selected*'
    }
    Assert-True $wrongBatch "A family emitted only in another batch must fail that batch's reported-but-not-selected reconciliation."

    # All recursively emitted TRX files contribute to one batch.
    Set-TrxPlans @(@{ Reports = @(
        @{ Path = "net8.0/results.trx"; Results = @(@{ Id = "critical"; Name = "Critical"; Outcome = "Passed"; Class = "Cases.Tests"; Method = "Critical" }) },
        @{ Path = "net9.0/results.trx"; Results = @(@{ Id = "second"; Name = "Second"; Outcome = "Passed"; Class = "Cases.Tests"; Method = "Second" }) }
    ) })
    $multiTrx = $true
    $multiTrxError = $null
    try { & $runner -RepositoryRoot $fixture -Project "tests/Tier0/Tier0.csproj" -Priority P0 -Execute -ApprovedPath "tests/Tier0/Tier0.csproj" | Out-Null }
    catch { $multiTrx = $false; $multiTrxError = $_.Exception.Message }
    Assert-True $multiTrx "Every framework-scoped TRX emitted for a batch must be consumed. Error: $multiTrxError"

    Set-TrxPlans @(@(@{ Content = '<not-xml' }))
    $malformed = $false
    try { & $runner -RepositoryRoot $fixture -Project "tests/Tier0/Tier0.csproj" -Priority P0 -Execute -ApprovedPath "tests/Tier0/Tier0.csproj" | Out-Null }
    catch { $malformed = $_.Exception.Message -like '*malformed TRX*' }
    Assert-True $malformed "Malformed TRX must fail with an explicit reconciliation error."

    Set-TrxPlans @(@())
    $missingTrx = $false
    try { & $runner -RepositoryRoot $fixture -Project "tests/Tier0/Tier0.csproj" -Priority P0 -Execute -ApprovedPath "tests/Tier0/Tier0.csproj" | Out-Null }
    catch { $missingTrx = $_.Exception.Message -like '*wrote no TRX*' }
    Assert-True $missingTrx "A successful native exit with no TRX must fail closed."

    $noResultsXml = New-TrxDocument -Results @() -WithoutResults
    Set-TrxPlans @(@(@{ Content = $noResultsXml }))
    $missingResults = $false
    try { & $runner -RepositoryRoot $fixture -Project "tests/Tier0/Tier0.csproj" -Priority P0 -Execute -ApprovedPath "tests/Tier0/Tier0.csproj" | Out-Null }
    catch { $missingResults = $_.Exception.Message -like '*has no Results*' }
    Assert-True $missingResults "A TRX without a Results node must fail explicitly."

    # A result proves reported execution only for this explicit terminal-outcome allowlist.
    foreach ($reportedOutcome in @("Failed", "Timeout", "Aborted")) {
        Set-TrxPlans @(@(@{ Results = @(
            @{ Id = "critical"; Name = "Critical"; Outcome = $reportedOutcome; Class = "Cases.Tests"; Method = "Critical" },
            @{ Id = "second"; Name = "Second"; Outcome = "Passed"; Class = "Cases.Tests"; Method = "Second" }
        ) }))
        $reportedOutcomeAccepted = $true
        try { & $runner -RepositoryRoot $fixture -Project "tests/Tier0/Tier0.csproj" -Priority P0 -Execute -ApprovedPath "tests/Tier0/Tier0.csproj" | Out-Null }
        catch { $reportedOutcomeAccepted = $false }
        Assert-True $reportedOutcomeAccepted "$reportedOutcome must count as a reported execution outcome."
    }

    Set-TrxPlans @(@(@{ Results = @(
        @{ Id = "critical"; Name = "Critical"; Outcome = "Passed"; Class = "Cases.Tests"; Method = "Critical" },
        @{ Id = "second"; Name = "Second"; Outcome = "NotExecuted"; Class = "Cases.Tests"; Method = "Second" }
    ) }))
    $declaredNotExecutedAccepted = $true
    try { & $runner -RepositoryRoot $fixture -Project "tests/Tier0/Tier0.csproj" -Priority P0 -Execute -ApprovedPath "tests/Tier0/Tier0.csproj" | Out-Null }
    catch { $declaredNotExecutedAccepted = $false }
    Assert-True $declaredNotExecutedAccepted "NotExecuted must count as an expected reported skip only for a family with an explicit inventory skip condition."

    Set-FixtureFile "tests/Tier0/Tests.cs" 'namespace Cases; public class Tests { [Xunit.Fact] public void Critical() {} [Xunit.Fact] public void Second() {} [Xunit.Theory, Xunit.InlineData(1), Xunit.InlineData(2)] public void Regression(int value) {} }'
    $undeclaredRows = @(& (Join-Path $PSScriptRoot "Get-TestInventory.ps1") -RepositoryRoot $fixture -IncludeDeclarations | ForEach-Object { $_ | ConvertFrom-Json })
    $undeclaredRules = @($undeclaredRows | Where-Object { $_.kind -eq "test-declaration" -and $_.path -eq "tests/Tier0/Tier0.csproj" } | ForEach-Object {
        @{ kind = "test-declaration"; id = $_.id; path = $_.path; sourceHash = $_.sourceHash; priority = $(if ($_.method -eq "Regression") { "P2" } else { "P0" }); rationale = "Fixture undeclared skip contract"; evidence = @("tests/Tier0/Tests.cs:1"); reviewState = "reviewed"; requirements = @("not-audited"); component = "fixture batching"; uncertainties = @(); coverageEvidence = "not-in-capture" } | ConvertTo-Json -Compress
    })
    Set-FixtureFile "scripts/test-priorities.ndjson" (($policy + $undeclaredRules) -join "`n")
    Set-TrxPlans @(@(@{ Results = @(
        @{ Id = "critical"; Name = "Critical"; Outcome = "Passed"; Class = "Cases.Tests"; Method = "Critical" },
        @{ Id = "second"; Name = "Second"; Outcome = "NotExecuted"; Class = "Cases.Tests"; Method = "Second" }
    ) }))
    $notExecuted = $false
    try { & $runner -RepositoryRoot $fixture -Project "tests/Tier0/Tier0.csproj" -Priority P0 -Execute -ApprovedPath "tests/Tier0/Tier0.csproj" | Out-Null }
    catch { $notExecuted = $_.Exception.Message -like '*NotExecuted*' }
    Assert-True $notExecuted "NotExecuted without an explicit inventory skip condition must remain fail-closed."
    Set-FixtureFile "tests/Tier0/Tests.cs" 'namespace Cases; public class Tests { [Xunit.Fact] public void Critical() {} [LinuxOnlyFact("fixture platform condition")] public void Second() {} [Xunit.Theory, Xunit.InlineData(1), Xunit.InlineData(2)] public void Regression(int value) {} }'
    Set-FixtureFile "scripts/test-priorities.ndjson" (($policy + $batchRules) -join "`n")

    Set-TrxPlans @(@(@{ Results = @(
        @{ Id = "critical"; Name = "Critical"; Outcome = "Passed"; Class = "Cases.Tests"; Method = "Critical" },
        @{ Id = "second"; Name = "Second"; Outcome = "Inconclusive"; Class = "Cases.Tests"; Method = "Second" }
    ) }))
    $unsupportedOutcome = $false
    try { & $runner -RepositoryRoot $fixture -Project "tests/Tier0/Tier0.csproj" -Priority P0 -Execute -ApprovedPath "tests/Tier0/Tier0.csproj" | Out-Null }
    catch { $unsupportedOutcome = $_.Exception.Message -like "*unsupported TRX outcome 'Inconclusive'*" }
    Assert-True $unsupportedOutcome "An outcome outside the explicit allowlist must fail closed."

    # Two selected classes can share a bare method; fallback must reject the ambiguity.
    Set-FixtureFile "tests/Tier0/Tests.cs" 'namespace Cases; public class First { [Xunit.Fact] public void Same() {} } public class Second { [Xunit.Fact] public void Same() {} } public class Excluded { [Xunit.Fact] public void Other() {} }'
    $ambiguousRows = @(& (Join-Path $PSScriptRoot "Get-TestInventory.ps1") -RepositoryRoot $fixture -IncludeDeclarations | ForEach-Object { $_ | ConvertFrom-Json })
    $ambiguousRules = @($ambiguousRows | Where-Object { $_.kind -eq "test-declaration" -and $_.path -eq "tests/Tier0/Tier0.csproj" } | ForEach-Object {
        @{ kind = "test-declaration"; id = $_.id; path = $_.path; sourceHash = $_.sourceHash; priority = $(if ($_.method -eq "Same") { "P0" } else { "P2" }); rationale = "Fixture ambiguity contract"; evidence = @("tests/Tier0/Tests.cs:1"); reviewState = "reviewed"; requirements = @("not-audited"); component = "fixture ambiguity"; uncertainties = @(); coverageEvidence = "not-in-capture" } | ConvertTo-Json -Compress
    })
    Set-FixtureFile "scripts/test-priorities.ndjson" (($policy + $ambiguousRules) -join "`n")
    $ambiguousXml = New-TrxDocument -Results @(@{ Name = "Same"; Outcome = "Passed" }) -WithoutDefinitions
    Set-TrxPlans @(@(@{ Content = $ambiguousXml }))
    $ambiguous = $false
    try { & $runner -RepositoryRoot $fixture -Project "tests/Tier0/Tier0.csproj" -Priority P0 -Execute -ApprovedPath "tests/Tier0/Tier0.csproj" | Out-Null }
    catch { $ambiguous = $_.Exception.Message -like '*ambiguous bare result*' }
    Assert-True $ambiguous "A bare method shared by two selected classes must fail closed."

    # Tiered inner loop: approve exactly what the preview selected, then walk tiers in order.
    Set-FixtureFile "tests/Tier0/Tests.cs" 'namespace Cases; public class Tests { [Xunit.Fact] public void Critical() {} [Xunit.Theory, Xunit.InlineData(1), Xunit.InlineData(2)] public void Regression(int value) {} }'
    Set-FixtureFile "scripts/test-priorities.ndjson" (($policy + $caseRules) -join "`n")
    $global:PriorityFailingFilter = $null
    $global:PriorityTestInvocations.Clear()
    Set-TrxPlans @(@(@{ Results = @(@{ Id = "critical"; Name = "Critical"; Outcome = "Passed"; Class = "Cases.Tests"; Method = "Critical" }) }))
    & $runner -RepositoryRoot $fixture -Project "tests/Tier0/Tier0.csproj" -Priority P0 -Execute -ApproveSelectedProjects | Out-Null
    Assert-True ($global:PriorityTestInvocations.Count -eq 1) "ApproveSelectedProjects must satisfy the approval gate for exactly the selected surfaces without a hand-listed ApprovedPath."

    # Approving the selection is not approving everything: an unselected surface stays out.
    $global:PriorityTestInvocations.Clear()
    Set-TrxPlans @(
        @(@{ Results = @(@{ Id = "critical"; Name = "Critical"; Outcome = "Passed"; Class = "Cases.Tests"; Method = "Critical" }) }),
        @(@{ Results = @(@{ Id = "regression"; Name = "Regression"; Outcome = "Passed"; Class = "Cases.Tests"; Method = "Regression" }) })
    )
    & $runner -RepositoryRoot $fixture -Project "tests/Tier0/Tier0.csproj" -Escalate P0, P2 -Execute -ApproveSelectedProjects | Out-Null
    Assert-True ($global:PriorityTestInvocations.Count -eq 2) "Escalation must run one command per requested tier."
    $escalationFilters = @($global:PriorityTestInvocations | ForEach-Object { $_[[array]::IndexOf($_, "--filter") + 1] })
    Assert-True (
        $escalationFilters[0] -ceq "FullyQualifiedName=Cases.Tests.Critical" -and
        $escalationFilters[1] -ceq "FullyQualifiedName=Cases.Tests.Regression"
    ) "Escalation must run the requested tiers in the requested order, not as one merged selection."

    # The distinguishing case: a failing earlier tier must prevent the later tier entirely.
    $global:PriorityFailingFilter = "Cases.Tests.Critical"
    $global:PriorityTestInvocations.Clear()
    Set-TrxPlans @(
        @(@{ Results = @(@{ Id = "critical"; Name = "Critical"; Outcome = "Passed"; Class = "Cases.Tests"; Method = "Critical" }) }),
        @(@{ Results = @(@{ Id = "regression"; Name = "Regression"; Outcome = "Passed"; Class = "Cases.Tests"; Method = "Regression" }) })
    )
    $escalationStopped = $false
    try { & $runner -RepositoryRoot $fixture -Project "tests/Tier0/Tier0.csproj" -Escalate P0, P2 -Execute -ApproveSelectedProjects | Out-Null }
    catch { $escalationStopped = $_.Exception.Message -like '*failed with exit 1*' }
    Assert-True ($escalationStopped -and $global:PriorityTestInvocations.Count -eq 1) "A failing tier must stop the escalation before the next tier starts."
    $global:PriorityFailingFilter = $null

    # A tier with no work HERE is not a failure. Tier0 declares only P0 and P2 families, so P1
    # selects nothing; the documented P0,P1,P2 loop would be unusable on any component without a
    # family in an early tier if that emptiness aborted the walk.
    $global:PriorityTestInvocations.Clear()
    Set-TrxPlans @(@(@{ Results = @(@{ Id = "regression"; Name = "Regression"; Outcome = "Passed"; Class = "Cases.Tests"; Method = "Regression" }) }))
    & $runner -RepositoryRoot $fixture -Project "tests/Tier0/Tier0.csproj" -Escalate P1, P2 -Execute -ApproveSelectedProjects | Out-Null
    Assert-True ($global:PriorityTestInvocations.Count -eq 1) "An empty early tier must be bypassed instead of aborting the escalation."
    $bypassFilter = $global:PriorityTestInvocations[0][[array]::IndexOf($global:PriorityTestInvocations[0], "--filter") + 1]
    Assert-True ($bypassFilter -ceq "FullyQualifiedName=Cases.Tests.Regression") "The tier after an empty one must still run its own selection."

    # The bypass belongs to escalation only. Asking for one empty tier directly is still an error,
    # because there the emptiness is the answer to the question the caller asked.
    $standaloneEmpty = $false
    try { & $runner -RepositoryRoot $fixture -Project "tests/Tier0/Tier0.csproj" -Priority P1 -Execute -ApproveSelectedProjects | Out-Null }
    catch { $standaloneEmpty = $_.Exception.Message -like '*selection is empty*' }
    Assert-True $standaloneEmpty "A standalone run whose selection is empty must still fail."

    $escalationPreview = @(& $runner -RepositoryRoot $fixture -Project "tests/Tier0/Tier0.csproj" -Escalate P0, P2 | ConvertFrom-Json)
    Assert-True (
        $escalationPreview.Count -eq 2 -and
        ($escalationPreview[0].requestedPriorities -contains "P0") -and
        ($escalationPreview[1].requestedPriorities -contains "P2")
    ) "An escalation preview must return one ordered plan per tier without executing anything."

    $conflicting = $false
    try { & $runner -RepositoryRoot $fixture -Escalate P0 -Priority P1 | Out-Null }
    catch { $conflicting = $_.Exception.Message -like '*not both*' }
    Assert-True $conflicting "Escalate and Priority must not silently disagree about which tiers to run."

    $duplicateTiers = $false
    try { & $runner -RepositoryRoot $fixture -Escalate P0, P0 | Out-Null }
    catch { $duplicateTiers = $_.Exception.Message -like '*once*' }
    Assert-True $duplicateTiers "A repeated tier must be rejected rather than silently run twice."

    Write-Output "PASS: component scopes, case previews, priority composition, consumer closure, policy fallback, execution preflight, exact-family filters, filter batching, executed-selection reconciliation, tier escalation and native failures."
}
finally {
    foreach ($name in @("dotnet", "pwsh", "python", "npm")) { Remove-Item "Function:\$name" -ErrorAction SilentlyContinue }
    foreach ($name in @("PriorityTestInvocations", "PriorityTrxPlans", "PriorityTrxPlanIndex", "PriorityFailingFilter")) {
        Remove-Variable $name -Scope Global -ErrorAction SilentlyContinue
    }
    Remove-Item -LiteralPath $fixture -Recurse -Force
}
