#requires -Version 7.0

<#
.SYNOPSIS
Produces deterministic NDJSON test-surface inventory without executing project or test code.
.DESCRIPTION
Reads Git-visible paths (tracked plus non-ignored untracked files), solution membership and
literal project XML. Frameworks and test properties are declarations, NOT evaluated MSBuild
state. Imports, discovery, MemberData, test modules and credentials are never loaded.
Every row remains not-run/not-audited. This inventory does not authorize test execution.
#>
[CmdletBinding()]
param(
    [string]$RepositoryRoot = (Join-Path $PSScriptRoot ".."),
    [string]$Solution = "LmDotnetTools.sln",
    [switch]$IncludeDeclarations
)

$ErrorActionPreference = "Stop"
$root = (Resolve-Path -LiteralPath $RepositoryRoot).Path
$gitRoutingVariables = @("GIT_DIR", "GIT_WORK_TREE", "GIT_INDEX_FILE", "GIT_COMMON_DIR")

function Get-RepositoryGitDirectory {
    param([string]$RepositoryPath)
    $dotGit = Join-Path $RepositoryPath ".git"
    if (Test-Path -LiteralPath $dotGit -PathType Container) {
        return (Resolve-Path -LiteralPath $dotGit).Path
    }
    if (-not (Test-Path -LiteralPath $dotGit -PathType Leaf)) {
        throw "Repository Git metadata is missing: $dotGit"
    }
    $pointer = [System.IO.File]::ReadAllText($dotGit).Trim()
    if ($pointer -notmatch '^gitdir:\s*(.+)$') {
        throw "Repository Git metadata pointer is invalid: $dotGit"
    }
    $gitDirectoryPath = $Matches[1]
    if (-not [System.IO.Path]::IsPathRooted($gitDirectoryPath)) {
        $gitDirectoryPath = Join-Path $RepositoryPath $gitDirectoryPath
    }
    $gitDirectory = (Resolve-Path -LiteralPath $gitDirectoryPath).Path
    $worktreePointer = Join-Path $gitDirectory "gitdir"
    if (Test-Path -LiteralPath $worktreePointer -PathType Leaf) {
        $declaredDotGitPath = [System.IO.File]::ReadAllText($worktreePointer).Trim()
        if (-not [System.IO.Path]::IsPathRooted($declaredDotGitPath)) {
            $declaredDotGitPath = Join-Path $gitDirectory $declaredDotGitPath
        }
        $declaredDotGit = (Resolve-Path -LiteralPath $declaredDotGitPath).Path
        $expectedDotGit = (Resolve-Path -LiteralPath $dotGit).Path
        if (-not [string]::Equals($declaredDotGit, $expectedDotGit, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Repository Git metadata does not belong to the requested root: $RepositoryPath"
        }
    }
    return $gitDirectory
}

function Invoke-RepositoryGit {
    param([string[]]$Arguments)
    $previous = @{}
    foreach ($name in $gitRoutingVariables) {
        $item = Get-Item -LiteralPath "Env:$name" -ErrorAction SilentlyContinue
        if ($null -ne $item) { $previous[$name] = $item.Value }
        Remove-Item -LiteralPath "Env:$name" -ErrorAction SilentlyContinue
    }
    try {
        $env:GIT_DIR = Get-RepositoryGitDirectory -RepositoryPath $root
        $env:GIT_WORK_TREE = $root
        & git -C $root -c core.quotepath=false @Arguments
    }
    finally {
        foreach ($name in $gitRoutingVariables) { Remove-Item -LiteralPath "Env:$name" -ErrorAction SilentlyContinue }
        foreach ($name in $previous.Keys) { Set-Item -LiteralPath "Env:$name" -Value $previous[$name] }
    }
}

function Get-GitPaths {
    param([string[]]$GitArguments)
    $paths = @(Invoke-RepositoryGit -Arguments (@("ls-files") + $GitArguments))
    if ($LASTEXITCODE -ne 0) { throw "Git file inventory failed (exit $LASTEXITCODE)." }
    return $paths
}

function Get-Declarations {
    param([xml]$Document, [string]$ElementName)
    foreach ($node in $Document.SelectNodes("//*[local-name()='$ElementName']")) {
        $conditions = [System.Collections.Generic.List[string]]::new()
        for ($ancestor = $node; $ancestor -is [System.Xml.XmlElement]; $ancestor = $ancestor.ParentNode) {
            if ($ancestor.HasAttribute("Condition")) { $conditions.Add($ancestor.GetAttribute("Condition")) }
        }
        [ordered]@{ value = $node.InnerText; condition = $conditions -join " AND " }
    }
}

function Get-OrdinalPaths {
    param([string[]]$Paths)
    $sorted = [System.Collections.Generic.SortedSet[string]]::new([System.StringComparer]::Ordinal)
    foreach ($path in $Paths) { [void]$sorted.Add($path) }
    return @($sorted)
}

function Get-DeclarationSourceHash {
    param([string]$SourceText)
    # A declaration policy follows the source across Windows and Linux. Roslyn preserves the source's
    # original newline tokens in ToFullString(), so hash the canonical LF spelling rather than the checkout's
    # CRLF/LF bytes. Bare CR is normalized too for a deterministic legacy-file result.
    $canonical = $SourceText -replace "`r`n?", "`n"
    return [Convert]::ToHexString(
        [System.Security.Cryptography.SHA256]::HashData([System.Text.Encoding]::UTF8.GetBytes($canonical))
    )
}

$tracked = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
foreach ($path in @(Get-GitPaths -GitArguments @("--cached"))) { [void]$tracked.Add($path) }
$paths = @(Get-OrdinalPaths -Paths (@($tracked) + @(Get-GitPaths -GitArguments @("--others", "--exclude-standard"))))
$gitSpellings = [System.Collections.Generic.Dictionary[string, string]]::new([System.StringComparer]::OrdinalIgnoreCase)
foreach ($path in $paths) {
    if ($gitSpellings.ContainsKey($path)) { throw "Case-colliding Git paths require manual reconciliation: $path" }
    $gitSpellings.Add($path, $path)
}
$solutionPath = Join-Path $root $Solution
if (-not (Test-Path -LiteralPath $solutionPath -PathType Leaf)) { throw "Solution file is missing: $Solution" }
$solutionProjects = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
$solutionDirectory = [System.IO.Path]::GetDirectoryName($solutionPath)
foreach ($line in [System.IO.File]::ReadAllLines($solutionPath)) {
    if ($line -match '^\s*Project\("[^"]+"\)\s*=\s*"[^"]+",\s*"([^"]+\.csproj)"') {
        $fullPath = [System.IO.Path]::GetFullPath($Matches[1].Replace("\", "/"), $solutionDirectory)
        $relative = [System.IO.Path]::GetRelativePath($root, $fullPath).Replace("\", "/")
        if ($relative.StartsWith("../") -or [System.IO.Path]::IsPathRooted($relative)) { throw "Solution project is outside repository: $relative" }
        if ($gitSpellings.ContainsKey($relative)) { $relative = $gitSpellings[$relative] }
        [void]$solutionProjects.Add($relative)
    }
}

$records = [System.Collections.Generic.List[object]]::new()
$projectPaths = @(Get-OrdinalPaths -Paths (@($paths | Where-Object { $_ -like "*.csproj" }) + @($solutionProjects)))
foreach ($path in $projectPaths) {
    $fullPath = Join-Path $root $path
    $status = "present"
    $evidence = [System.Collections.Generic.List[string]]::new()
    $frameworks = @()
    $testDeclarations = @()
    $document = $null
    if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
        $status = "missing"
        $evidence.Add("unresolved-project")
    }
    else {
        try { $document = [xml][System.IO.File]::ReadAllText($fullPath) }
        catch [System.Xml.XmlException] { $status = "invalid-project"; $evidence.Add("invalid-project") }
        catch [System.Management.Automation.PSInvalidCastException] { $status = "invalid-project"; $evidence.Add("invalid-project") }
        if ($null -ne $document) {
            $testDeclarations = @(Get-Declarations -Document $document -ElementName "IsTestProject")
            $frameworks = @((Get-Declarations -Document $document -ElementName "TargetFramework")) + @((Get-Declarations -Document $document -ElementName "TargetFrameworks"))
            if (@($testDeclarations | Where-Object { $_.value.Trim() -ne "false" }).Count -gt 0) { $evidence.Add("test-property-declaration") }
            if (@($document.SelectNodes("//*[local-name()='PackageReference']") | Where-Object { $_.GetAttribute("Include") -eq "Microsoft.NET.Test.Sdk" -or $_.GetAttribute("Update") -eq "Microsoft.NET.Test.Sdk" }).Count -gt 0) {
                $evidence.Add("test-sdk-reference")
            }
        }
    }
    if ($path.StartsWith("tests/", [System.StringComparison]::OrdinalIgnoreCase)) { $evidence.Add("test-directory-candidate") }
    if ([System.IO.Path]::GetFileNameWithoutExtension($path).EndsWith(".Tests", [System.StringComparison]::OrdinalIgnoreCase)) { $evidence.Add("test-name-candidate") }
    $directory = $path.Substring(0, [math]::Max(0, $path.LastIndexOf("/") + 1))
    $sourceFiles = @($paths | Where-Object { $_.StartsWith($directory, [System.StringComparison]::Ordinal) -and $_ -like "*.cs" })
    $candidates = @(Get-OrdinalPaths -Paths @($frameworks | ForEach-Object { $_.value -split ";" } | ForEach-Object { $_.Trim() } | Where-Object { $_ -match '^net[a-zA-Z0-9.\-]+$' }))
    $records.Add([ordered]@{
        schemaVersion = 1
        kind = if ($evidence.Count -gt 0) { "dotnet-project" } else { "dotnet-project-unclassified" }
        path = $path
        tracked = $tracked.Contains($path)
        inSolution = $solutionProjects.Contains($path)
        inventoryStatus = $status
        evaluationStatus = "not-evaluated"
        testEvidence = @($evidence)
        testDeclarations = @($testDeclarations)
        frameworkDeclarations = @($frameworks)
        frameworkCandidates = @($candidates)
        # Directory-prefix candidates only; Compile includes/excludes and overlap are not evaluated.
        sourcePathCandidates = @($sourceFiles)
        discoveredTestCount = $null
        runtimeStatus = "not-run"
        requirements = @("not-audited")
    })
}

foreach ($path in $paths) {
    $kind = if ($path -match '^samples/LmStreaming\.Sample/ClientApp/src/.+\.test\.ts$') { "vitest-file" }
        elseif ($path -match '^samples/LmStreaming\.Sample/playwright-scripts/.+\.mjs$') { "manual-browser-script" }
        elseif ($path -match '\.tests\.ps1$') { "powershell-test" }
        elseif ($path -match '(^|/)(test_[^/]+|[^/]+_test)\.py$') { "python-test" }
        else { $null }
    if ($null -eq $kind) { continue }
    $records.Add([ordered]@{
        schemaVersion = 1
        kind = $kind
        path = $path
        tracked = $tracked.Contains($path)
        inventoryStatus = if (Test-Path -LiteralPath (Join-Path $root $path) -PathType Leaf) { "present" } else { "missing" }
        discoveredTestCount = $null
        runtimeStatus = "not-run"
        requirements = @("not-audited")
    })
}

if ($IncludeDeclarations) {
    # Load only the parser shipped with PowerShell, never a repository assembly.
    Add-Type -Path (Join-Path $PSHOME "Microsoft.CodeAnalysis.dll")
    Add-Type -Path (Join-Path $PSHOME "Microsoft.CodeAnalysis.CSharp.dll")
    $declarationRecords = [System.Collections.Generic.List[object]]::new()
    foreach ($container in @($records)) {
        $gaps = [System.Collections.Generic.SortedSet[string]]::new([System.StringComparer]::Ordinal)
        $container.declarationStatus = "source-only"
        $container.declarationCount = 0
        $container.declarationGaps = @()
        if ($container.kind -eq "dotnet-project") {
            $sources = [System.Collections.Generic.SortedSet[string]]::new([System.StringComparer]::Ordinal)
            foreach ($source in $container.sourcePathCandidates) { [void]$sources.Add($source) }
            if ($container.inventoryStatus -ne "present") { [void]$gaps.Add($container.inventoryStatus) }
            else {
                $projectDocument = [xml][System.IO.File]::ReadAllText((Join-Path $root $container.path))
                foreach ($compile in $projectDocument.SelectNodes("//*[local-name()='Compile']")) {
                    $include = $compile.GetAttribute("Include")
                    if ($compile.HasAttribute("Remove") -or $compile.HasAttribute("Exclude") -or $compile.HasAttribute("Condition") -or $compile.ParentNode.HasAttribute("Condition")) {
                        [void]$gaps.Add("conditional-or-excluded-compile-items")
                    }
                    if (-not $include) { continue }
                    if ($include -match '[\$*?;]') { [void]$gaps.Add("unevaluated-compile-include:$include"); continue }
                    $projectDirectory = [System.IO.Path]::GetDirectoryName((Join-Path $root $container.path))
                    $linkedFullPath = [System.IO.Path]::GetFullPath($include.Replace("\", "/"), $projectDirectory)
                    $linked = [System.IO.Path]::GetRelativePath($root, $linkedFullPath).Replace("\", "/")
                    if ($gitSpellings.ContainsKey($linked)) { [void]$sources.Add($gitSpellings[$linked]) }
                    else { [void]$gaps.Add("unresolved-compile-include:$include") }
                }
                # Static source ownership deliberately does not claim evaluated Compile items.
                [void]$gaps.Add("compile-items-not-evaluated")
            }
            # Build a conservative project-local type index before emitting methods. It is
            # used only to derive inherited xUnit identities when syntax proves a unique,
            # direct, concrete relationship; every other inheritance shape remains a gap.
            $typeDeclarations = [System.Collections.Generic.List[object]]::new()
            $globalTypeAliases = @{}
            $globalTypeImportedNamespaces = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
            foreach ($typeSource in $sources) {
                $typeSourceFullPath = Join-Path $root $typeSource
                if (-not (Test-Path -LiteralPath $typeSourceFullPath -PathType Leaf)) { continue }
                $typeTree = [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText(
                    [System.IO.File]::ReadAllText($typeSourceFullPath)
                )
                $typeRoot = $typeTree.GetRoot()
                foreach ($using in @($typeRoot.DescendantNodes() | Where-Object { $_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.UsingDirectiveSyntax] -and $_.GlobalKeyword.RawKind -ne 0 })) {
                    if ($null -ne $using.Alias) {
                        $globalTypeAliases[$using.Alias.Name.Identifier.ValueText] = $using.Name.ToString().Replace("global::", "")
                    }
                    elseif ($using.StaticKeyword.RawKind -eq 0) {
                        [void]$globalTypeImportedNamespaces.Add($using.Name.ToString().Replace("global::", ""))
                    }
                }
            }
            foreach ($typeSource in $sources) {
                $typeSourceFullPath = Join-Path $root $typeSource
                if (-not (Test-Path -LiteralPath $typeSourceFullPath -PathType Leaf)) { continue }
                $typeTree = [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText(
                    [System.IO.File]::ReadAllText($typeSourceFullPath)
                )
                $typeRoot = $typeTree.GetRoot()
                $typeAliases = @{}
                foreach ($aliasName in $globalTypeAliases.Keys) {
                    $typeAliases[$aliasName] = $globalTypeAliases[$aliasName]
                }
                $typeImportedNamespaces = [System.Collections.Generic.HashSet[string]]::new(
                    $globalTypeImportedNamespaces,
                    [System.StringComparer]::Ordinal
                )
                foreach ($using in @($typeRoot.DescendantNodes() | Where-Object { $_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.UsingDirectiveSyntax] })) {
                    if ($null -ne $using.Alias) {
                        $typeAliases[$using.Alias.Name.Identifier.ValueText] = $using.Name.ToString().Replace("global::", "")
                    }
                    elseif ($using.StaticKeyword.RawKind -eq 0) {
                        [void]$typeImportedNamespaces.Add($using.Name.ToString().Replace("global::", ""))
                    }
                }
                foreach ($typeSyntax in @($typeRoot.DescendantNodes() | Where-Object { $_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.TypeDeclarationSyntax] })) {
                    $typeNamespaceParts = [System.Collections.Generic.List[string]]::new()
                    $typeNameParts = [System.Collections.Generic.List[string]]::new()
                    for ($typeParent = $typeSyntax.Parent; $null -ne $typeParent; $typeParent = $typeParent.Parent) {
                        if ($typeParent -is [Microsoft.CodeAnalysis.CSharp.Syntax.BaseNamespaceDeclarationSyntax]) {
                            $typeNamespaceParts.Insert(0, $typeParent.Name.ToString())
                        }
                        if ($typeParent -is [Microsoft.CodeAnalysis.CSharp.Syntax.TypeDeclarationSyntax]) {
                            $parentArity = if ($null -eq $typeParent.TypeParameterList) { 0 } else { $typeParent.TypeParameterList.Parameters.Count }
                            $typeNameParts.Insert(0, $typeParent.Identifier.ValueText + $(if ($parentArity) { "``$parentArity" } else { "" }))
                        }
                    }
                    $ownArity = if ($null -eq $typeSyntax.TypeParameterList) { 0 } else { $typeSyntax.TypeParameterList.Parameters.Count }
                    $typeNameParts.Add($typeSyntax.Identifier.ValueText + $(if ($ownArity) { "``$ownArity" } else { "" }))
                    $typeNamespace = $typeNamespaceParts -join "."
                    $typeName = $typeNameParts -join "+"
                    $typeDeclarations.Add([pscustomobject]@{
                        namespace = $typeNamespace
                        name = $typeName
                        simpleName = $typeSyntax.Identifier.ValueText
                        fullyQualifiedName = (@($typeNamespace, $typeName) | Where-Object { $_ }) -join "."
                        isAbstract = $typeSyntax.Modifiers.ToString() -match '(^|\s)abstract(\s|$)'
                        isSealed = $typeSyntax.Modifiers.ToString() -match '(^|\s)sealed(\s|$)'
                        isPartial = $typeSyntax.Modifiers.ToString() -match '(^|\s)partial(\s|$)'
                        isGeneric = $ownArity -gt 0
                        isNested = $typeNameParts.Count -gt 1
                        importedNamespaces = @($typeImportedNamespaces)
                        bases = if ($null -eq $typeSyntax.BaseList) {
                            @()
                        }
                        else {
                            @($typeSyntax.BaseList.Types | ForEach-Object {
                                $baseName = $_.Type.ToString().Replace("global::", "")
                                $baseRoot = ($baseName -split '\.')[0]
                                if ($typeAliases.ContainsKey($baseRoot)) {
                                    $baseName = $typeAliases[$baseRoot] + $baseName.Substring($baseRoot.Length)
                                }
                                $baseName
                            })
                        }
                    })
                }
            }
            foreach ($source in $sources) {
                $sourceFullPath = Join-Path $root $source
                if (-not (Test-Path -LiteralPath $sourceFullPath -PathType Leaf)) { [void]$gaps.Add("missing-source:$source"); continue }
                $text = [System.IO.File]::ReadAllText($sourceFullPath)
                $tree = [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText($text)
                $syntax = $tree.GetRoot()
                if (@($tree.GetDiagnostics() | Where-Object Severity -eq "Error").Count -gt 0) { [void]$gaps.Add("syntax-errors:$source") }
                if ($syntax.ContainsDirectives) { [void]$gaps.Add("conditional-source:$source") }
                # Seed from the project-wide global aliases before the file's own, so an
                # attribute aliased in another file still resolves. A file-local alias of the
                # same name shadows the global one, matching C# resolution.
                $aliases = @{}
                foreach ($aliasName in $globalTypeAliases.Keys) {
                    $aliases[$aliasName] = $globalTypeAliases[$aliasName]
                }
                foreach ($using in @($syntax.DescendantNodes() | Where-Object { $_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.UsingDirectiveSyntax] -and $null -ne $_.Alias })) {
                    $aliases[$using.Alias.Name.Identifier.ValueText] = $using.Name.ToString()
                }
                foreach ($type in @($syntax.DescendantNodes() | Where-Object { $_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.TypeDeclarationSyntax] -and $null -ne $_.BaseList })) {
                    [void]$gaps.Add("base-type-resolution-required:$source")
                }
                foreach ($method in @($syntax.DescendantNodes() | Where-Object { $_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax] })) {
                    $attributes = @($method.AttributeLists | ForEach-Object { $_.Attributes } | ForEach-Object {
                        $name = $_.Name.ToString().Replace("global::", "")
                        if ($aliases.ContainsKey($name)) { $name = $aliases[$name] }
                        ($name -split '\.')[-1] -replace 'Attribute$', ''
                    })
                    $testAttributes = @($attributes | Where-Object { $_ -match '^(Fact|Theory|SkippableFact|SkippableTheory|LinuxOnlyFact|WindowsOnlyFact|RequiresFileSymlinkFact|RequiresUnreadableEntryFact|TestMethod|DataTestMethod)$' })
                    if ($testAttributes.Count -eq 0) {
                        if (@($attributes | Where-Object { $_ -match '(Fact|Theory|Test)$' }).Count -gt 0) {
                            [void]$gaps.Add("unresolved-test-attribute:${source}:$($method.Identifier.ValueText)")
                        }
                        continue
                    }
                    $namespaceParts = [System.Collections.Generic.List[string]]::new()
                    $typeParts = [System.Collections.Generic.List[string]]::new()
                    $genericType = $false
                    $declaringTypeSyntax = $null
                    for ($parent = $method.Parent; $null -ne $parent; $parent = $parent.Parent) {
                        if ($parent -is [Microsoft.CodeAnalysis.CSharp.Syntax.BaseNamespaceDeclarationSyntax]) { $namespaceParts.Insert(0, $parent.Name.ToString()) }
                        if ($parent -is [Microsoft.CodeAnalysis.CSharp.Syntax.TypeDeclarationSyntax]) {
                            if ($null -eq $declaringTypeSyntax) { $declaringTypeSyntax = $parent }
                            $typeArity = if ($null -eq $parent.TypeParameterList) { 0 } else { $parent.TypeParameterList.Parameters.Count }
                            $typeParts.Insert(0, $parent.Identifier.ValueText + $(if ($typeArity) { "``$typeArity" } else { "" }))
                            if ($typeArity) { $genericType = $true }
                        }
                    }
                    $typeName = $typeParts -join "+"
                    $namespaceName = $namespaceParts -join "."
                    $fullyQualifiedName = (@($namespaceName, $typeName, $method.Identifier.ValueText) | Where-Object { $_ }) -join "."
                    $signature = (@($method.ParameterList.Parameters | ForEach-Object { ($_.Modifiers.ToString() + " " + $_.Type.ToString()).Trim() }) -join ",")
                    $arity = if ($null -eq $method.TypeParameterList) { 0 } else { $method.TypeParameterList.Parameters.Count }
                    $methodIdentity = $fullyQualifiedName + $(if ($arity) { "``$arity" } else { "" }) + "($signature)"
                    $dataSources = @($method.AttributeLists | ForEach-Object { $_.Attributes } | Where-Object { $_.Name.ToString() -match '(InlineData|MemberData|ClassData|DataRow|DynamicData)(Attribute)?$' } | ForEach-Object { $_.ToString() })
                    $dynamic = @($dataSources | Where-Object { $_ -match '(MemberData|ClassData|DynamicData)' }).Count -gt 0
                    $isTheory = @($testAttributes | Where-Object { $_ -match 'Theory|DataTestMethod' }).Count -gt 0
                    $rowCount = @($attributes | Where-Object { $_ -in @("InlineData", "DataRow") }).Count
                    $parameterization = if ($dynamic -or ($isTheory -and $rowCount -eq 0)) { "dynamic" } elseif ($rowCount -gt 0) { "static" } else { "none" }
                    $declarationGaps = @()
                    $executableFullyQualifiedNames = @($fullyQualifiedName)
                    if ($genericType -or $arity) { $declarationGaps += "generic-test-identity-requires-discovery" }
                    $declaringTypeParts = @(
                        $typeDeclarations |
                        Where-Object {
                            $_.namespace -ceq $namespaceName -and
                            $_.name -ceq $typeName
                        }
                    )
                    $declaringTypeIsPartial = @($declaringTypeParts | Where-Object isPartial).Count -gt 0
                    $declaringTypeIsAbstract = @($declaringTypeParts | Where-Object isAbstract).Count -gt 0
                    if ($declaringTypeIsPartial) {
                        # A syntax part does not carry the modifiers or base list from its siblings.
                        # Without a semantic model, projecting its runtime identity is unsafe.
                        $declarationGaps += "partial-test-identity-requires-discovery"
                        $executableFullyQualifiedNames = @()
                    }
                    elseif ($declaringTypeIsAbstract) {
                        $directDescendants = @(
                            $typeDeclarations |
                            Where-Object {
                                $candidateNamespace = $_.namespace
                                $candidateImports = @($_.importedNamespaces)
                                @(
                                    $_.bases |
                                    Where-Object {
                                        $_ -ceq "$namespaceName.$typeName" -or
                                        ($candidateNamespace -ceq $namespaceName -and $_ -ceq $typeName) -or
                                        ($_ -ceq $typeName -and $candidateImports -ccontains $namespaceName)
                                    }
                                ).Count -eq 1
                            }
                        )
                        $directConcreteDescendants = @(
                            $directDescendants |
                            Where-Object {
                                -not $_.isAbstract -and
                                $_.isSealed -and
                                -not $_.isPartial -and
                                -not $_.isGeneric -and
                                -not $_.isNested -and
                                $_.namespace -ceq $namespaceName
                            }
                        )
                        $unresolvedDescendants = @(
                            $directDescendants |
                            Where-Object {
                                $_.namespace -cne $namespaceName -or
                                $_.isAbstract -or
                                -not $_.isSealed -or
                                $_.isPartial -or
                                $_.isGeneric -or
                                $_.isNested
                            }
                        )
                        if ($directConcreteDescendants.Count -gt 0 -and $unresolvedDescendants.Count -eq 0) {
                            $executableFullyQualifiedNames = @(
                                $directConcreteDescendants |
                                Sort-Object fullyQualifiedName |
                                ForEach-Object { "$($_.fullyQualifiedName).$($method.Identifier.ValueText)" }
                            )
                        }
                        else {
                            $declarationGaps += "abstract-declaring-type-requires-discovery"
                            $executableFullyQualifiedNames = @()
                        }
                    }
                    $record = [ordered]@{
                        schemaVersion = 2; kind = "test-declaration"; containerKind = "dotnet-project"; path = $container.path
                        id = "dotnet|$($container.path)|$methodIdentity"
                        sourcePath = $source; sourceLine = $method.GetLocation().GetLineSpan().StartLinePosition.Line + 1
                        sourceHash = Get-DeclarationSourceHash -SourceText $method.ToFullString()
                        testFramework = if ($testAttributes -contains "TestMethod" -or $testAttributes -contains "DataTestMethod") { "mstest" } else { "xunit" }
                        namespace = $namespaceName; declaringType = $typeName; method = $method.Identifier.ValueText
                        fullyQualifiedName = $fullyQualifiedName; executableFullyQualifiedNames = @($executableFullyQualifiedNames)
                        signature = $signature; attributes = @($attributes)
                        parameterization = $parameterization; dataSources = @($dataSources)
                        skipConditions = @(
                            $method.AttributeLists |
                            ForEach-Object { $_.Attributes } |
                            Where-Object {
                                $attributeText = $_.ToString()
                                $attributeName = ($_.Name.ToString().Replace("global::", "") -split '\.')[-1] -replace 'Attribute$', ''
                                if ($attributeName -match '^(LinuxOnlyFact|WindowsOnlyFact|RequiresFileSymlinkFact|RequiresUnreadableEntryFact)$') {
                                    return $true
                                }
                                return @(
                                    $_.ArgumentList.Arguments |
                                    Where-Object {
                                        $null -ne $_.NameEquals -and
                                        $_.NameEquals.Name.Identifier.ValueText -ceq "Skip" -and
                                        $_.Expression.Kind() -eq
                                            [Microsoft.CodeAnalysis.CSharp.SyntaxKind]::StringLiteralExpression
                                    }
                                ).Count -gt 0
                            } |
                            ForEach-Object { $_.ToString() }
                        )
                        staticRowCount = if ($parameterization -eq "dynamic") { $null } elseif ($rowCount) { $rowCount } else { 1 }
                        frameworkCandidates = @($container.frameworkCandidates)
                        declarationGaps = @($declarationGaps); discoveredTestCount = $null; runtimeStatus = "not-run"; requirements = @("not-audited")
                    }
                    $declarationRecords.Add($record)
                    $container.declarationCount++
                }
            }
        }
        elseif ($container.kind -in @("powershell-test", "manual-browser-script")) {
            if ($container.kind -eq "powershell-test") {
                $scriptTokens = $null; $scriptErrors = $null
                $null = [System.Management.Automation.Language.Parser]::ParseFile((Join-Path $root $container.path), [ref]$scriptTokens, [ref]$scriptErrors)
                if ($scriptErrors.Count -gt 0) { [void]$gaps.Add("script-syntax-errors") }
            }
            $declarationRecords.Add([ordered]@{
                schemaVersion = 2; kind = "test-declaration"; containerKind = $container.kind; path = $container.path
                id = "script|$($container.path)"; sourcePath = $container.path; sourceLine = 1
                testFramework = "script"; parameterization = "atomic"; staticRowCount = $null
                declarationGaps = @(); discoveredTestCount = $null; runtimeStatus = "not-run"; requirements = @("not-audited")
            })
            $container.declarationCount = 1
        }
        elseif ($container.kind -eq "vitest-file") { [void]$gaps.Add("typescript-parser-unavailable") }
        elseif ($container.kind -eq "python-test") { [void]$gaps.Add("python-declarations-not-parsed") }
        else { [void]$gaps.Add("project-test-role-unclassified") }
        $container.declarationGaps = @($gaps)
        if ($gaps.Count -gt 0) { $container.declarationStatus = "partial" }
    }
    $records.AddRange($declarationRecords)
}

$orderedRecords = [System.Collections.Generic.SortedDictionary[string, object]]::new([System.StringComparer]::Ordinal)
foreach ($record in $records) {
    $key = if ($record.kind -eq "test-declaration") { $record.id } else { "$($record.kind)|$($record.path)" }
    if ($orderedRecords.ContainsKey($key)) { throw "Duplicate inventory identity requires reconciliation: $key" }
    $orderedRecords.Add($key, $record)
}
foreach ($record in $orderedRecords.Values) {
    $record | ConvertTo-Json -Depth 10 -Compress
}
