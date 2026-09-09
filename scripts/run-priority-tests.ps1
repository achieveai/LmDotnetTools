#requires -Version 7.0

<#
.SYNOPSIS
Plans priority-based test execution; runs only explicitly approved selected paths.
.DESCRIPTION
Preview is static and never loads test code. ChangedPath or Project scopes selection
through affected consumers, not unchanged dependencies. Priority narrows that scope;
directly changed tests remain included. Approval is separate from classification.
Build/restore are deliberately separate. Full CI does not consume this runner.
#>
[CmdletBinding()]
param(
    [string]$RepositoryRoot = (Join-Path $PSScriptRoot ".."),
    [string]$Solution = "LmDotnetTools.sln",
    [string]$PriorityManifest = "scripts/test-priorities.ndjson",
    [ValidateSet("All", "P0", "P1", "P2", "P3")]
    [string[]]$Priority = @("All"),
    # Ordered inner loop: run each tier as its own plan and stop at the first failure, so a
    # broken baseline is not buried under a longer run. Not a merged selection.
    [ValidateSet("P0", "P1", "P2", "P3")]
    [string[]]$Escalate = @(),
    # Restricts the run to these surface kinds. For a local loop where another modality's
    # dependencies are not provisioned; the excluded kinds are visibly unselected in the plan
    # and still run in full CI. Not a priority decision.
    [ValidateSet("dotnet-project", "vitest-file", "powershell-test", "python-test", "manual-browser-script")]
    [string[]]$Kind = @(),
    [switch]$Fast,
    # Command-line length budget for one --filter expression. Batching keeps each
    # invocation under the platform limit; batches are non-overlapping and their union
    # is the whole selection.
    [ValidateRange(16, 32000)]
    [int]$MaxFilterLength = 24000,
    [string[]]$ChangedPath = @(),
    [string[]]$Project = @(),
    [switch]$Execute,
    [string[]]$ApprovedPath = @(),
    # Approves exactly the surfaces this run already selected, instead of hand-listing them.
    # It is an acknowledgement shortcut, not a prerequisite audit: every approved surface and
    # its outstanding requirements are printed before anything runs.
    [switch]$ApproveSelectedProjects,
    # Set only by the escalation driver below. A tier that legitimately selects nothing - a
    # component with no P0 family, say - must not abort the walk before a later tier with real
    # work. An explicitly requested standalone run with an empty selection still fails, because
    # there the emptiness is the answer to the question the caller asked.
    [switch]$AllowEmptySelection
)

$ErrorActionPreference = "Stop"
$root = (Resolve-Path -LiteralPath $RepositoryRoot).Path
function ConvertTo-ScopedRepositoryPath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    if ([string]::IsNullOrWhiteSpace($Path) -or [System.IO.Path]::IsPathRooted($Path)) {
        throw "Invalid repository-relative scope path: $Path"
    }
    $fullPath = [System.IO.Path]::GetFullPath($Path, $root)
    $rootPrefix = "$root$([System.IO.Path]::DirectorySeparatorChar)"
    if (-not $fullPath.StartsWith($rootPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Repository-relative scope path escapes the repository root: $Path"
    }
    return [System.IO.Path]::GetRelativePath($root, $fullPath).Replace("\", "/")
}
# Escalation drives this same script once per tier rather than adding a second execution
# path: every tier still passes through the identical policy, preflight, filter and TRX
# reconciliation gates. A tier that throws stops the walk, so a later tier cannot mask it.
if ($Escalate.Count -gt 0) {
    if (@($Priority).Count -ne 1 -or $Priority[0] -cne "All") {
        throw "Use -Escalate or -Priority, not both: -Escalate already names the tiers to run, in order."
    }
    if (@($Escalate | Sort-Object -Unique).Count -ne $Escalate.Count) {
        throw "Each escalation tier may appear only once: $($Escalate -join ', ')."
    }
    $forwarded = @{
        RepositoryRoot = $root
        Solution = $Solution
        PriorityManifest = $PriorityManifest
        MaxFilterLength = $MaxFilterLength
        ChangedPath = $ChangedPath
        Project = $Project
        ApprovedPath = $ApprovedPath
        Kind = $Kind
    }
    foreach ($name in @("Fast", "Execute", "ApproveSelectedProjects")) {
        if ($PSBoundParameters.ContainsKey($name) -and $PSBoundParameters[$name]) { $forwarded[$name] = $true }
    }
    $tierPreviews = [System.Collections.Generic.List[string]]::new()
    # An empty tier is bypassed, not fatal; a tier that FAILS still stops the walk, so a broken
    # baseline is never masked by a later green tier.
    if ($Execute) { $forwarded["AllowEmptySelection"] = $true }
    foreach ($tier in $Escalate) {
        if ($Execute) { Write-Host "Priority escalation: running $tier." }
        $tierOutput = & $PSCommandPath @forwarded -Priority $tier
        if (-not $Execute) { $tierPreviews.Add((($tierOutput | Out-String)).Trim()) }
    }
    if (-not $Execute) { "[" + ($tierPreviews -join ",`n") + "]" }
    return
}
$scoped = $Fast -or $ChangedPath.Count -gt 0 -or $Project.Count -gt 0
$ChangedPath = @($ChangedPath | ForEach-Object { ConvertTo-ScopedRepositoryPath $_ })
$Project = @($Project | ForEach-Object { ConvertTo-ScopedRepositoryPath $_ })
$inventory = @(& (Join-Path $PSScriptRoot "Get-TestInventory.ps1") -RepositoryRoot $root -Solution $Solution | ForEach-Object { $_ | ConvertFrom-Json })
$surfaces = @($inventory | Where-Object kind -ne "dotnet-project-unclassified")
foreach ($projectPath in $Project) {
    if (@($inventory | Where-Object { $_.kind -like "dotnet-project*" -and $_.path -ceq $projectPath }).Count -ne 1) {
        throw "Unknown project scope: $projectPath"
    }
}
$scopePaths = @($ChangedPath) + @($Project)
$policyPath = [System.IO.Path]::GetFullPath($PriorityManifest, $root)
$policyStatus = "present"
$policyErrors = [System.Collections.Generic.List[string]]::new()
$rules = [System.Collections.Generic.Dictionary[string, object]]::new([System.StringComparer]::Ordinal)
$caseRules = [System.Collections.Generic.Dictionary[string, object]]::new([System.StringComparer]::Ordinal)
$declarations = @()
function Test-NonNegativeInteger {
    param([object]$Value, [switch]$AllowNull)
    if ($null -eq $Value) { return $AllowNull.IsPresent }
    return ($Value -is [byte] -or $Value -is [sbyte] -or
        $Value -is [int16] -or $Value -is [uint16] -or
        $Value -is [int32] -or $Value -is [uint32] -or
        $Value -is [int64] -or $Value -is [uint64]) -and $Value -ge 0
}
try {
    if (-not (Test-Path -LiteralPath $policyPath -PathType Leaf)) { $policyStatus = "missing" }
    else {
        foreach ($line in [System.IO.File]::ReadAllLines($policyPath)) {
            if ([string]::IsNullOrWhiteSpace($line)) { continue }
            $rule = $line | ConvertFrom-Json
            if ($rule.kind -eq "test-declaration") {
                $hasSourceHash = $null -ne $rule.PSObject.Properties["sourceHash"]
                $validEvidence = $rule.evidence -is [array] -and @($rule.evidence).Count -gt 0 -and
                    @($rule.evidence | Where-Object { $_ -isnot [string] -or [string]::IsNullOrWhiteSpace($_) }).Count -eq 0
                $validRequirements = $rule.requirements -is [array] -and @($rule.requirements).Count -gt 0 -and
                    @($rule.requirements | Where-Object { $_ -isnot [string] -or [string]::IsNullOrWhiteSpace($_) }).Count -eq 0 -and
                    @($rule.requirements | Where-Object { $_ -ceq "not-audited" }).Count -gt 0
                $validUncertainties = $null -ne $rule.PSObject.Properties["uncertainties"] -and $rule.uncertainties -is [array] -and
                    @($rule.uncertainties | Where-Object { $_ -isnot [string] -or [string]::IsNullOrWhiteSpace($_) }).Count -eq 0
                $validExemption = $null -eq $rule.PSObject.Properties["p1FloorExemption"] -or $rule.p1FloorExemption -eq "vacuous"
                $validCoverageEvidence = @("measured", "no-coverage-capture", "not-in-capture") -ccontains $rule.coverageEvidence
                $hasCoverageBasis = $null -ne $rule.PSObject.Properties["coverageBasis"]
                $validCoverageBasis = -not $hasCoverageBasis
                if ($rule.coverageEvidence -ceq "measured") {
                    $basis = $rule.coverageBasis
                    $basisFields = @($basis.PSObject.Properties.Name)
                    $validCoverageBasis = $hasCoverageBasis -and $null -ne $basis -and
                        $basisFields.Count -eq 5 -and
                        @("greedyRank", "marginalNewSpans", "exclusiveSpans", "addsNothingNew", "inP0Prefix" |
                            Where-Object { $basisFields -cnotcontains $_ }).Count -eq 0 -and
                        (Test-NonNegativeInteger $basis.greedyRank -AllowNull) -and
                        (Test-NonNegativeInteger $basis.marginalNewSpans) -and
                        (Test-NonNegativeInteger $basis.exclusiveSpans) -and
                        $basis.addsNothingNew -is [bool] -and $basis.inP0Prefix -is [bool]
                }
                if ([string]::IsNullOrWhiteSpace($rule.id) -or [string]::IsNullOrWhiteSpace($rule.path) -or -not $hasSourceHash -or
                    $rule.priority -notin @("P0", "P1", "P2", "P3") -or [string]::IsNullOrWhiteSpace($rule.rationale) -or
                    $rule.reviewState -ne "reviewed" -or -not $validEvidence -or -not $validRequirements -or
                    [string]::IsNullOrWhiteSpace($rule.component) -or -not $validUncertainties -or -not $validExemption -or
                    -not $validCoverageEvidence -or -not $validCoverageBasis) {
                    throw "Invalid declaration priority rule."
                }
                $caseRules.Add($rule.id, $rule)
                continue
            }
            $validRequirements = $rule.requirements -is [array] -and @($rule.requirements).Count -gt 0 -and
                @($rule.requirements | Where-Object { $_ -isnot [string] -or [string]::IsNullOrWhiteSpace($_) }).Count -eq 0
            if ($rule.kind -notin @("dotnet-project", "vitest-file", "manual-browser-script", "powershell-test", "python-test") -or
                $rule.priority -notin @("P0", "P1", "P2", "P3") -or [string]::IsNullOrWhiteSpace($rule.path) -or
                [string]::IsNullOrWhiteSpace($rule.rationale) -or -not $validRequirements) { throw "Invalid priority rule." }
            $rules.Add("$($rule.kind)|$($rule.path)", $rule)
        }
        $caseInventory = @(& (Join-Path $PSScriptRoot "Get-TestInventory.ps1") -RepositoryRoot $root -Solution $Solution -IncludeDeclarations | ForEach-Object { $_ | ConvertFrom-Json })
        $declarations = @($caseInventory | Where-Object kind -eq "test-declaration")
        $byId = [System.Collections.Generic.Dictionary[string, object]]::new([System.StringComparer]::Ordinal)
        foreach ($declaration in $declarations) { $byId.Add($declaration.id, $declaration) }
        foreach ($rule in $caseRules.Values) {
            $declaration = $null
            if (-not $byId.TryGetValue($rule.id, [ref]$declaration) -or $declaration.path -cne $rule.path -or $declaration.sourceHash -cne $rule.sourceHash) {
                $policyStatus = "stale"
                $policyErrors.Add("Unmatched or changed declaration policy: $($rule.id)")
            }
        }
        foreach ($declaration in $declarations) {
            if (-not $caseRules.ContainsKey($declaration.id)) {
                $policyStatus = "stale"
                $policyErrors.Add("Missing declaration priority rule: $($declaration.id)")
            }
        }
        foreach ($rule in $rules.Values) {
            if ($rule.path -ne "*" -and @($surfaces | Where-Object { $_.kind -ceq $rule.kind -and $_.path -ceq $rule.path }).Count -eq 0) {
                $policyStatus = "stale"
                $policyErrors.Add("Unmatched policy: $($rule.kind)|$($rule.path)")
            }
        }
    }
}
catch { $policyStatus = "invalid"; $policyErrors.Add($_.Exception.Message); $rules.Clear(); $caseRules.Clear() }

$impact = $null
if ($scoped) {
    if ($scopePaths.Count -eq 0) { $impact = [pscustomobject]@{ mode = "full"; reason = "no-change-input"; selectedProjects = @() } }
    else {
        try {
            # Supply every inventoried project: avoid recursive filesystem enumeration and retain external consumers.
            $projects = @($inventory | Where-Object { $_.kind -like "dotnet-project*" } | ForEach-Object path)
            $knownTests = @($surfaces | Where-Object kind -eq "dotnet-project" | ForEach-Object path)
            $impact = & (Join-Path $PSScriptRoot "select-impacted-tests.ps1") -RepositoryRoot $root -ChangedPath $scopePaths -SolutionProject $projects -KnownTestProject $knownTests | ConvertFrom-Json
            # A client-only fixture may have no .NET host project. Its ownership is still known.
            if ($impact.reason -eq "unowned-path" -and @($scopePaths | Where-Object { -not $_.StartsWith("samples/LmStreaming.Sample/ClientApp/", [System.StringComparison]::Ordinal) }).Count -eq 0) {
                $impact = [pscustomobject]@{ mode = "selected"; reason = "client-component"; selectedProjects = @(); affectedProjects = @() }
            }
            if ($impact.mode -notin @("full", "selected")) { throw "Invalid impact result." }
        }
        catch { $impact = [pscustomobject]@{ mode = "full"; reason = "impact-error"; selectedProjects = @() } }
    }
}
$tests = @(
    foreach ($surface in $surfaces) {
        $rule = $null
        $source = "exact"
        if (-not $rules.TryGetValue("$($surface.kind)|$($surface.path)", [ref]$rule)) {
            $source = "kind-default"
            if (-not $rules.TryGetValue("$($surface.kind)|*", [ref]$rule)) { $source = "unassigned" }
        }
        $tier = if ($null -eq $rule) { "unassigned" } else { $rule.priority }
        $reason = "not-requested"
        $selected = $false
        $directChange = $ChangedPath -contains $surface.path
        if ($surface.kind -eq "dotnet-project") {
            $directChange = $directChange -or @($surface.sourcePathCandidates | Where-Object { $ChangedPath -contains $_ }).Count -gt 0
        }
        $inScope = -not $scoped -or $impact.mode -eq "full" -or $directChange
        if ($scoped -and -not $inScope) {
            if ($surface.kind -eq "dotnet-project") { $inScope = $impact.selectedProjects -contains $surface.path }
            elseif ($surface.kind -in @("vitest-file", "manual-browser-script")) {
                $inScope = @($ChangedPath | Where-Object { $_.StartsWith("samples/LmStreaming.Sample/", [System.StringComparison]::Ordinal) }).Count -gt 0 -or
                    @($impact.affectedProjects | Where-Object { $_ -like "samples/LmStreaming.Sample/*" -or $_ -like "src/LmStreaming.AspNetCore/*" }).Count -gt 0
            }
        }
        # Kind is a modality restriction, not a scope or priority decision, so it is recorded
        # under its own reason rather than being reported as an unaffected component.
        $kindExcluded = $Kind.Count -gt 0 -and $Kind -notcontains $surface.kind
        if ($kindExcluded) { $inScope = $false }
        if ($kindExcluded) { $reason = "outside-requested-kinds" }
        elseif (-not $inScope) { $reason = "outside-affected-components" }
        elseif ($policyStatus -ne "present") { $selected = $true; $reason = "policy-$policyStatus-scope-fallback" }
        elseif ($tier -eq "unassigned") { $selected = $true; $reason = "unassigned-fallback" }
        elseif ($directChange) { $selected = $true; $reason = "directly-changed-tests" }
        elseif ($Priority -contains "All" -or $Priority -contains $tier) {
            $selected = $true
            $reason = if ($scoped) { $impact.reason } else { "requested-priority" }
        }
        $caseTests = @(
            foreach ($declaration in @($declarations | Where-Object path -CEQ $surface.path)) {
                $caseRule = $null
                $reviewed = $caseRules.TryGetValue($declaration.id, [ref]$caseRule)
                $caseTier = if ($reviewed) { $caseRule.priority } else { "unassigned" }
                # A changed declaration source keeps that family. A changed test project file
                # can alter discovery/execution for every family it owns, so retain them all.
                $caseDirect = $ChangedPath -contains $declaration.sourcePath -or $ChangedPath -contains $surface.path
                $caseSelected = $inScope -and ($policyStatus -ne "present" -or -not $reviewed -or $caseDirect -or $Priority -contains "All" -or $Priority -contains $caseTier)
                [ordered]@{
                    id = $declaration.id; method = $declaration.method; fullyQualifiedName = $declaration.fullyQualifiedName
                    executableFullyQualifiedNames = [object[]]@(
                        if ($null -ne $declaration.PSObject.Properties["executableFullyQualifiedNames"]) {
                            $declaration.executableFullyQualifiedNames
                        }
                        else {
                            $declaration.fullyQualifiedName
                        }
                    )
                    sourcePath = $declaration.sourcePath; sourceLine = $declaration.sourceLine
                    priority = $caseTier; selected = $caseSelected
                    reviewState = if ($reviewed) { "reviewed" } else { "unassigned" }
                    rationale = if ($reviewed) { $caseRule.rationale } else { "Unreviewed declaration: retain within affected scope." }
                    requirements = [object[]]@(if ($reviewed) { $caseRule.requirements } else { "not-audited" })
                    parameterization = $declaration.parameterization; staticRowCount = $declaration.staticRowCount
                    skipConditions = [object[]]@($declaration.skipConditions)
                    declarationGaps = [object[]]@($declaration.declarationGaps); runtimeStatus = "not-run"
                }
            }
        )
        if ($caseTests.Count -gt 0) {
            $selected = @($caseTests | Where-Object selected).Count -gt 0
            if ($kindExcluded) {
                $reason = "outside-requested-kinds"
            }
            elseif (-not $inScope) {
                $reason = "outside-affected-components"
            }
            elseif ($selected -and $reason -eq "not-requested") {
                $reason = "declaration-priorities"
            }
        }
        # Exact method-family filter. `FullyQualifiedName=<declaration FQN>` selects every
        # parameter row of one family and nothing else on all three adapter generations in
        # this repository; `~` bleeds into siblings sharing a prefix and is never generated.
        # Clauses come from the inventory, never from manifest-authored strings.
        $selectedCases = @($caseTests | Where-Object selected)
        $surfaceInventory = @(
            $caseInventory |
            Where-Object {
                $_.kind -ceq $surface.kind -and
                $_.path -ceq $surface.path
            }
        )
        $surfaceDeclarationGaps = @(
            $surfaceInventory |
            ForEach-Object { @($_.declarationGaps) }
        )
        $ambiguousExecutableNames = [System.Collections.Generic.List[string]]::new()
        $executableNameCounts = [System.Collections.Generic.Dictionary[string, int]]::new([System.StringComparer]::OrdinalIgnoreCase)
        foreach ($caseTest in $caseTests) {
            foreach ($executableName in $caseTest.executableFullyQualifiedNames) {
                if ($executableNameCounts.ContainsKey($executableName)) { $executableNameCounts[$executableName]++ }
                else { $executableNameCounts.Add($executableName, 1) }
            }
        }
        $selectedExecutableNames = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
        foreach ($caseTest in $selectedCases) {
            foreach ($executableName in $caseTest.executableFullyQualifiedNames) {
                [void]$selectedExecutableNames.Add($executableName)
            }
        }
        foreach ($entry in $executableNameCounts.GetEnumerator()) {
            if ($entry.Value -gt 1 -and $selectedExecutableNames.Contains($entry.Key)) {
                $ambiguousExecutableNames.Add($entry.Key)
            }
        }
        $filter = $null
        $unresolvedTestAttributes = @(
            $surfaceDeclarationGaps |
            Where-Object { $_ -like "unresolved-test-attribute:*" }
        )
        $selectedInheritedDeclarations = @($selectedCases | Where-Object { $_.declarationGaps -contains "abstract-declaring-type-requires-discovery" })
        $selectedPartialDeclarations = @($selectedCases | Where-Object { $_.declarationGaps -contains "partial-test-identity-requires-discovery" })
        $selectedGenericDeclarations = @($selectedCases | Where-Object { $_.declarationGaps -contains "generic-test-identity-requires-discovery" })
        if ($surface.kind -eq "dotnet-project" -and $caseTests.Count -gt 0 -and $selectedCases.Count -lt $caseTests.Count -and $unresolvedTestAttributes.Count -gt 0) {
            # A test-shaped custom attribute may hide declarations from static inventory.
            # Until that attribute is reviewed, no subset can claim complete selection.
            $filter = [ordered]@{
                mode = "unsupported-unresolved-test-attribute"
                gaps = @($unresolvedTestAttributes)
                clauses = @(); expression = $null; expectedCount = $selectedCases.Count; batches = @()
            }
        }
        elseif ($surface.kind -eq "dotnet-project" -and $caseTests.Count -gt 0 -and $selectedCases.Count -lt $caseTests.Count -and $selectedInheritedDeclarations.Count -gt 0) {
            # xUnit reports inherited base methods under concrete derived classes. Static source
            # inspection does not know that executable identity, so the base FQN is not a safe filter.
            $filter = [ordered]@{
                mode = "unsupported-inherited-executable-identity"
                declarations = @($selectedInheritedDeclarations | ForEach-Object id)
                clauses = @(); expression = $null; expectedCount = $selectedCases.Count; batches = @()
            }
        }
        elseif ($surface.kind -eq "dotnet-project" -and $caseTests.Count -gt 0 -and $selectedCases.Count -lt $caseTests.Count -and $selectedPartialDeclarations.Count -gt 0) {
            # Modifiers and base lists can be split across partial declarations. Static syntax
            # cannot safely infer the adapter identity without evaluating a semantic model.
            $filter = [ordered]@{
                mode = "unsupported-partial-executable-identity"
                declarations = @($selectedPartialDeclarations | ForEach-Object id)
                clauses = @(); expression = $null; expectedCount = $selectedCases.Count; batches = @()
            }
        }
        elseif ($surface.kind -eq "dotnet-project" -and $caseTests.Count -gt 0 -and $selectedCases.Count -lt $caseTests.Count -and $selectedGenericDeclarations.Count -gt 0) {
            # Generic declaring types and methods need runtime discovery before their adapter
            # identity is known. Do not emit a source-shaped filter as if it were executable.
            $filter = [ordered]@{
                mode = "unsupported-generic-executable-identity"
                declarations = @($selectedGenericDeclarations | ForEach-Object id)
                clauses = @(); expression = $null; expectedCount = $selectedCases.Count; batches = @()
            }
        }
        elseif ($surface.kind -eq "dotnet-project" -and $caseTests.Count -gt 0 -and $selectedCases.Count -lt $caseTests.Count -and $ambiguousExecutableNames.Count -gt 0) {
            # VSTest's FullyQualifiedName exposes class + method, not the source signature.
            # Overloads therefore cannot be selected or reconciled independently. Refuse the
            # subset instead of presenting a filter that silently combines method families.
            $filter = [ordered]@{
                mode = "unsupported-ambiguous-executable-identity"
                ambiguousFullyQualifiedNames = @($ambiguousExecutableNames | Sort-Object)
                clauses = @(); expression = $null; expectedCount = $selectedCases.Count; batches = @()
            }
        }
        elseif ($surface.kind -eq "dotnet-project" -and $caseTests.Count -gt 0 -and $selectedCases.Count -lt $caseTests.Count) {
            $clauses = @(
                $selectedCases |
                ForEach-Object { $_.executableFullyQualifiedNames } |
                ForEach-Object { "FullyQualifiedName=$_" }
            )
            # Pack clauses greedily in inventory order. Deterministic, and every clause lands
            # in exactly one batch: a duplicate would run a family twice, and a dropped one
            # would vanish behind an exit code of 0.
            $batches = [System.Collections.Generic.List[object]]::new()
            $current = [System.Collections.Generic.List[string]]::new()
            foreach ($clause in $clauses) {
                if ($clause.Length -gt $MaxFilterLength) {
                    $filter = [ordered]@{
                        mode = "unsupported-filter-clause-too-long"
                        clauseLength = $clause.Length; maxFilterLength = $MaxFilterLength
                        clauses = @(); expression = $null; expectedCount = $selectedCases.Count; batches = @()
                    }
                    break
                }
                $projected = if ($current.Count -eq 0) { $clause.Length } else { $current.Count + ($current | Measure-Object Length -Sum).Sum + $clause.Length }
                if ($current.Count -gt 0 -and $projected -gt $MaxFilterLength) {
                    $batches.Add([ordered]@{ clauses = @($current); expression = ($current -join "|"); expectedCount = $current.Count })
                    $current = [System.Collections.Generic.List[string]]::new()
                }
                $current.Add($clause)
            }
            if ($null -eq $filter) {
                if ($current.Count -gt 0) { $batches.Add([ordered]@{ clauses = @($current); expression = ($current -join "|"); expectedCount = $current.Count }) }
                $filter = [ordered]@{
                    mode = "subset"
                    clauses = @($clauses)
                    expression = ($clauses -join "|")
                    # A filter matching nothing prints "No test matches" and exits 0, which is
                    # byte-identical to a passing run. The runner asserts against this count.
                    expectedCount = $clauses.Count
                    batches = @($batches)
                }
            }
        }
        elseif ($surface.kind -eq "dotnet-project") {
            $filter = [ordered]@{ mode = "none"; clauses = @(); expression = $null; expectedCount = $caseTests.Count; batches = @() }
        }
        [ordered]@{
            kind = $surface.kind
            path = $surface.path
            priority = $tier
            prioritySource = $source
            declarations = @($caseTests)
            filter = $filter
            declarationGaps = [object[]]@(if ($caseInventory.Count -gt 0) { $surfaceDeclarationGaps } else { "declarations-not-inventoried" })
            declarationStatus = if ($caseTests.Count -eq 0) { "not-inventoried" } elseif (@($caseTests | Where-Object reviewState -eq "unassigned").Count -gt 0) { "partially-reviewed" } else { "known-declarations-reviewed" }
            rationale = if ($null -eq $rule) { "No rule: include conservatively until classified." } else { $rule.rationale }
            requirements = [object[]]@(if ($null -eq $rule) { "not-audited" } else { $rule.requirements })
            inventoryStatus = $surface.inventoryStatus
            selected = $selected
            selectionReason = $reason
            runtimeStatus = "not-run"
        }
    }
)
$selectedTests = @($tests | Where-Object selected)
$plan = [ordered]@{
    schemaVersion = 1
    mode = if ($Fast) { "fast" } else { "priority" }
    requestedPriorities = @($Priority)
    policyStatus = $policyStatus
    policyErrors = @($policyErrors)
    impact = $impact
    selectedCount = $selectedTests.Count
    declarationSummary = [ordered]@{ known = $declarations.Count; reviewed = $caseRules.Count; runtimeCount = $null; status = "incomplete-static-only" }
    tests = @($tests)
    unclassifiedProjects = @($inventory | Where-Object kind -eq "dotnet-project-unclassified" | ForEach-Object path)
    ciPolicy = "unchanged-full-and-existing-prerequisite-gates"
}
if (-not $Execute) { $plan | ConvertTo-Json -Depth 15; return }

# Preflight the entire selection before executing anything. No partial passing lane.
if ($policyStatus -ne "present") { throw "Execution blocked: reconcile $policyStatus priority policy first." }
if ($selectedTests.Count -eq 0) {
    if (-not $AllowEmptySelection) { throw "Execution blocked: selection is empty." }
    Write-Host "Priority escalation: $($Priority -join ',') selects nothing here; skipping."
    return
}
$approved = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
foreach ($path in $ApprovedPath) { [void]$approved.Add(($path.Replace("\", "/") -replace '^\./', '')) }
if ($ApproveSelectedProjects) {
    # Approve the selection this run computed, and nothing wider: an unselected surface is
    # still refused below. The requirements are printed because they remain unaudited.
    foreach ($test in $selectedTests) {
        Write-Host "Approving selected surface: $($test.path) [$($test.requirements -join ', ')]"
        [void]$approved.Add($test.path)
    }
}
foreach ($test in $selectedTests) {
    if (-not $approved.Contains($test.path)) { throw "Execution authorization required after prerequisite review: $($test.path) [$($test.requirements -join ', ')]. Use ApprovedPath only for reviewed, provisioned paths." }
    if ($test.inventoryStatus -ne "present") { throw "Execution blocked: $($test.path) is $($test.inventoryStatus)." }
    if ($test.kind -notin @("dotnet-project", "powershell-test", "vitest-file", "python-test")) { throw "Execution blocked: manual scenario needs its documented procedure: $($test.path)." }
    # A subset must carry a usable filter. There is no unfiltered fallback: running the
    # whole project instead would silently execute families the selection excluded.
    if ($test.declarations.Count -gt 0 -and @($test.declarations | Where-Object selected).Count -ne $test.declarations.Count) {
        if ($null -eq $test.filter -or $test.filter.mode -ne "subset" -or [string]::IsNullOrWhiteSpace($test.filter.expression)) {
            throw "Execution blocked: no usable declaration filter for $($test.path). No unfiltered fallback is permitted."
        }
        if ($test.filter.expectedCount -lt 1) { throw "Execution blocked: empty selection for $($test.path). A zero-match filter exits 0 and would report success." }
        if ($test.filter.expression -match '~') { throw "Execution blocked: contains-matching filter for $($test.path) would select sibling families." }
    }
}
foreach ($test in $selectedTests) {
    $fullPath = Join-Path $root $test.path
    switch ($test.kind) {
        "dotnet-project" {
            if ($null -ne $test.filter -and $test.filter.mode -eq "subset") {
                # A zero-match filter exits 0, byte-identical to a passing run. Reconcile
                # every batch independently from every TRX it emits. This proves only that
                # each selected method family was reported, not that every dynamic data row ran.
                $stamp = [System.DateTime]::UtcNow.ToString("yyyyMMdd-HHmmss")
                $resultsRoot = Join-Path $root ".logs/test-results/priority-$stamp-$([System.Guid]::NewGuid().ToString('n').Substring(0, 8))"
                $batchIndex = 0
                foreach ($batch in $test.filter.batches) {
                    $batchIndex++
                    $batchDirectory = Join-Path $resultsRoot "batch-$batchIndex"
                    if (Test-Path -LiteralPath $batchDirectory) {
                        throw "Execution blocked: non-empty batch result path already exists: $batchDirectory"
                    }
                    New-Item -ItemType Directory -Path $batchDirectory | Out-Null
                    & dotnet test $fullPath --no-build --no-restore --verbosity minimal --blame-hang --blame-hang-timeout 4m --filter $batch.expression --results-directory $batchDirectory --logger "trx"
                    if ($LASTEXITCODE -ne 0) { throw "Test command failed with exit $LASTEXITCODE for $($test.path)." }

                    $trxFiles = @(Get-ChildItem -LiteralPath $batchDirectory -Filter "*.trx" -File -Recurse | Sort-Object FullName)
                    if ($trxFiles.Count -eq 0) {
                        throw "Execution blocked: $($test.path) batch $batchIndex wrote no TRX, so its run cannot be distinguished from a zero-match filter."
                    }

                    $selectedNames = @($batch.clauses | ForEach-Object { ($_ -split "=", 2)[1] })
                    $selectedByMethod = @{}
                    $declaredSkipFamilies = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
                    foreach ($selectedCase in @($test.declarations | Where-Object selected)) {
                        foreach ($selectedExecutableName in $selectedCase.executableFullyQualifiedNames) {
                            if ($selectedNames -ccontains $selectedExecutableName -and @($selectedCase.skipConditions).Count -gt 0) {
                                [void]$declaredSkipFamilies.Add($selectedExecutableName)
                            }
                        }
                    }
                    foreach ($selectedName in $selectedNames) {
                        $methodName = ($selectedName -split '\.')[-1]
                        if (-not $selectedByMethod.ContainsKey($methodName)) { $selectedByMethod[$methodName] = @() }
                        $selectedByMethod[$methodName] = @($selectedByMethod[$methodName]) + $selectedName
                    }
                    $observedFamilies = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
                    $unexpectedFamilies = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)

                    foreach ($trxFile in $trxFiles) {
                        try { [xml]$trx = Get-Content -LiteralPath $trxFile.FullName -Raw }
                        catch { throw "Execution blocked: malformed TRX for $($test.path) batch $batchIndex at $($trxFile.FullName): $($_.Exception.Message)" }
                        $namespace = [System.Xml.XmlNamespaceManager]::new($trx.NameTable)
                        $namespace.AddNamespace("trx", "http://microsoft.com/schemas/VisualStudio/TeamTest/2010")
                        $resultsNode = $trx.SelectSingleNode("/trx:TestRun/trx:Results", $namespace)
                        if ($null -eq $resultsNode) {
                            throw "Execution blocked: TRX for $($test.path) batch $batchIndex has no Results node: $($trxFile.FullName)"
                        }
                        $definitions = @{}
                        foreach ($definition in @($trx.SelectNodes("/trx:TestRun/trx:TestDefinitions/trx:UnitTest", $namespace))) {
                            $testMethod = $definition.SelectSingleNode("trx:TestMethod", $namespace)
                            if ($null -ne $testMethod -and -not [string]::IsNullOrWhiteSpace($definition.id) -and
                                -not [string]::IsNullOrWhiteSpace($testMethod.className) -and -not [string]::IsNullOrWhiteSpace($testMethod.name)) {
                                $definitionId = [string]$definition.id
                                $definitionFamily = "$($testMethod.className).$($testMethod.name)"
                                if ($definitions.ContainsKey($definitionId) -and $definitions[$definitionId] -cne $definitionFamily) {
                                    throw "Execution blocked: conflicting TestDefinitions for testId '$definitionId' in $($trxFile.FullName): '$($definitions[$definitionId])' and '$definitionFamily'."
                                }
                                $definitions[$definitionId] = $definitionFamily
                            }
                        }
                        foreach ($result in @($resultsNode.SelectNodes("trx:UnitTestResult", $namespace))) {
                            $outcome = [string]$result.outcome
                            if ($outcome -notin @("Passed", "Failed", "Timeout", "Aborted", "NotExecuted")) {
                                throw "Execution blocked: unsupported TRX outcome '$outcome' for $($test.path) batch ${batchIndex}: $($result.testName)"
                            }

                            $family = $null
                            if (-not [string]::IsNullOrWhiteSpace($result.testId)) {
                                $resultId = [string]$result.testId
                                if (-not $definitions.ContainsKey($resultId)) {
                                    throw "Execution blocked: result testId '$resultId' has no usable TestDefinition for $($test.path) batch ${batchIndex}: $($result.testName)"
                                }
                                $family = $definitions[$resultId]
                            }
                            else {
                                $displayName = ([string]$result.testName -split "\(")[0].Trim()
                                $exactMatches = @($selectedNames | Where-Object { $_ -ceq $displayName })
                                if ($exactMatches.Count -eq 1) { $family = $exactMatches[0] }
                                elseif ($displayName -notmatch '\.') {
                                    if ($selectedByMethod.ContainsKey($displayName)) {
                                        $bareMatches = @($selectedByMethod[$displayName])
                                        if ($bareMatches.Count -eq 1) { $family = $bareMatches[0] }
                                        elseif ($bareMatches.Count -gt 1) {
                                            throw "Execution blocked: ambiguous bare result '$displayName' for $($test.path) batch $batchIndex; it maps to $($bareMatches -join ', ')."
                                        }
                                        else { $family = $displayName }
                                    }
                                    else { $family = $displayName }
                                }
                                else { $family = $displayName }
                            }
                            if ($outcome -eq "NotExecuted" -and -not $declaredSkipFamilies.Contains($family)) {
                                throw "Execution blocked: selected result was NotExecuted without an explicit inventory skip condition for $($test.path) batch ${batchIndex}: $($result.testName)"
                            }
                            if ($selectedNames -ccontains $family) { [void]$observedFamilies.Add($family) }
                            else { [void]$unexpectedFamilies.Add($family) }
                        }
                    }

                    $missing = @($selectedNames | Where-Object { -not $observedFamilies.Contains($_) })
                    $unexpected = @($unexpectedFamilies | Sort-Object)
                    if ($missing.Count -gt 0 -or $unexpected.Count -gt 0) {
                        throw "Executed selection does not reconcile for $($test.path) batch $batchIndex. Selected but never reported: $($missing -join ', '). Reported but not selected: $($unexpected -join ', '). A zero-match filter exits 0, so this is not a pass."
                    }
                }
            }
            else { & dotnet test $fullPath --no-build --no-restore --verbosity minimal --blame-hang --blame-hang-timeout 4m }
        }
        "powershell-test" { & pwsh -NoProfile -File $fullPath }
        "python-test" { & python -m pytest $fullPath }
        "vitest-file" {
            $clientRoot = Join-Path $root "samples/LmStreaming.Sample/ClientApp"
            $relative = [System.IO.Path]::GetRelativePath($clientRoot, $fullPath).Replace("\", "/")
            & npm --prefix $clientRoot exec --no -- vitest run --root $clientRoot $relative
        }
    }
    if ($LASTEXITCODE -ne 0) { throw "Test command failed with exit $LASTEXITCODE for $($test.path)." }
}
