[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$RepositoryRoot,

    [Parameter(Mandatory = $true)]
    [string[]]$ChangedPath,

    [string[]]$SolutionProject = @()
)

$ErrorActionPreference = "Stop"

function ConvertTo-RepositoryPath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    $repositoryPath = $Path.Replace("\", "/")
    if ($repositoryPath.StartsWith("./", [System.StringComparison]::Ordinal)) {
        return $repositoryPath.Substring(2)
    }

    return $repositoryPath
}

function New-Decision {
    param(
        [Parameter(Mandatory = $true)]
        [ValidateSet("full", "selected")]
        [string]$Mode,

        [Parameter(Mandatory = $true)]
        [string]$Reason,

        [string[]]$SelectedProjects = @()
    )

    return [ordered]@{
        mode = $Mode
        reason = $Reason
        changedPaths = @($normalizedChangedPaths)
        selectedProjects = @($SelectedProjects)
        graph = [ordered]@{
            projectCount = $projects.Count
            referenceCount = $referenceCount
            testProjectCount = $testProjects.Count
            unresolvedReferences = @($unresolvedReferences)
        }
    }
}

$root = (Resolve-Path $RepositoryRoot).Path.TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar)
$normalizedChangedPaths = @($ChangedPath | ForEach-Object { ConvertTo-RepositoryPath $_ } | Sort-Object -Unique)
$normalizedSolutionProjects = @($SolutionProject | ForEach-Object { ConvertTo-RepositoryPath $_ })
$projectPaths = if ($normalizedSolutionProjects.Count -gt 0) {
    @($normalizedSolutionProjects)
}
else {
    @(Get-ChildItem -Path $root -Filter "*.csproj" -File -Recurse | ForEach-Object {
        ConvertTo-RepositoryPath ([System.IO.Path]::GetRelativePath($root, $_.FullName))
    })
}
$projectPaths = @($projectPaths | Sort-Object -Unique)
$projects = @{}
$testProjects = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
$unresolvedReferences = [System.Collections.Generic.List[object]]::new()
$referenceCount = 0

foreach ($relativePath in $projectPaths) {
    $projectFullPath = [System.IO.Path]::GetFullPath($relativePath, $root)
    if (-not (Test-Path $projectFullPath -PathType Leaf)) {
        $unresolvedReferences.Add([ordered]@{ project = $relativePath; reference = $relativePath })
        continue
    }

    $projectFile = Get-Item $projectFullPath
    try {
        [xml]$projectXml = Get-Content $projectFile.FullName -Raw
    }
    catch {
        $decision = New-Decision -Mode "full" -Reason "invalid-project-file"
        $decision | ConvertTo-Json -Depth 6 -Compress
        return
    }

    $isTestProject = (@($projectXml.Project.PropertyGroup.IsTestProject) -contains "true") -or
        (@($projectXml.Project.ItemGroup.PackageReference) | Where-Object { [string]$_.Include -eq "Microsoft.NET.Test.Sdk" }).Count -gt 0
    if ($isTestProject -and $normalizedSolutionProjects.Count -gt 0) {
        $isTestProject = $normalizedSolutionProjects -contains $relativePath
    }
    $projects[$relativePath] = [pscustomobject]@{
        Path = $relativePath
        FullPath = $projectFile.FullName
        Directory = ConvertTo-RepositoryPath ([System.IO.Path]::GetDirectoryName($relativePath))
        Xml = $projectXml
        IsTest = $isTestProject
    }
    if ($isTestProject) {
        [void]$testProjects.Add($relativePath)
    }
}

$reverseReferences = @{}
foreach ($project in $projects.Values) {
    foreach ($reference in @($project.Xml.Project.ItemGroup.ProjectReference)) {
        if ($null -eq $reference -or [string]::IsNullOrWhiteSpace([string]$reference.Include)) {
            continue
        }

        $referenceCount++
        $resolvedFullPath = [System.IO.Path]::GetFullPath([string]$reference.Include, [System.IO.Path]::GetDirectoryName($project.FullPath))
        $resolvedPath = ConvertTo-RepositoryPath ([System.IO.Path]::GetRelativePath($root, $resolvedFullPath))
        if (-not $projects.ContainsKey($resolvedPath)) {
            $unresolvedReferences.Add([ordered]@{ project = $project.Path; reference = ConvertTo-RepositoryPath ([string]$reference.Include) })
            continue
        }

        if (-not $reverseReferences.ContainsKey($resolvedPath)) {
            $reverseReferences[$resolvedPath] = [System.Collections.Generic.List[string]]::new()
        }
        $reverseReferences[$resolvedPath].Add($project.Path)
    }
}

$itemTypes = @("Compile", "Content", "EmbeddedResource", "None")
$itemConsumers = @{}
foreach ($project in $projects.Values) {
    $projectDirectory = [System.IO.Path]::GetDirectoryName($project.FullPath)
    foreach ($itemType in $itemTypes) {
        foreach ($item in @($project.Xml.Project.ItemGroup.$itemType)) {
            if ($null -eq $item -or [string]::IsNullOrWhiteSpace([string]$item.Include)) {
                continue
            }

            $include = [string]$item.Include
            if ($include.Contains("$") -or $include.Contains("*") -or $include.Contains("?")) {
                continue
            }

            $itemFullPath = [System.IO.Path]::GetFullPath($include, $projectDirectory)
            if (-not $itemFullPath.StartsWith("$root$([System.IO.Path]::DirectorySeparatorChar)", [System.StringComparison]::OrdinalIgnoreCase)) {
                continue
            }

            $itemPath = ConvertTo-RepositoryPath ([System.IO.Path]::GetRelativePath($root, $itemFullPath))
            if (-not $itemConsumers.ContainsKey($itemPath)) {
                $itemConsumers[$itemPath] = [System.Collections.Generic.List[string]]::new()
            }
            $itemConsumers[$itemPath].Add($project.Path)
        }
    }
}

if ($unresolvedReferences.Count -gt 0) {
    $decision = New-Decision -Mode "full" -Reason "unresolved-project-reference"
    $decision | ConvertTo-Json -Depth 6 -Compress
    return
}

$infrastructurePrefixes = @(".config/", ".github/", "build/", "eng/", "scripts/")
$infrastructureFiles = @("Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props", "global.json", "LmDotnetTools.sln", "NuGet.config")
if (@($normalizedChangedPaths | Where-Object {
    $path = $_
    ($infrastructureFiles -contains $path) -or @($infrastructurePrefixes | Where-Object { $path.StartsWith($_, [System.StringComparison]::OrdinalIgnoreCase) }).Count -gt 0
}).Count -gt 0) {
    $decision = New-Decision -Mode "full" -Reason "infrastructure-change"
    $decision | ConvertTo-Json -Depth 6 -Compress
    return
}

$ownerPaths = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
foreach ($changed in $normalizedChangedPaths) {
    $owners = @($projects.Values | Where-Object {
        $directory = $_.Directory
        -not [string]::IsNullOrEmpty($directory) -and ($changed -eq $directory -or $changed.StartsWith("$directory/", [System.StringComparison]::OrdinalIgnoreCase))
    } | Sort-Object { $_.Directory.Length } -Descending)

    if ($owners.Count -eq 0) {
        $decision = New-Decision -Mode "full" -Reason "unowned-path"
        $decision | ConvertTo-Json -Depth 6 -Compress
        return
    }

    [void]$ownerPaths.Add($owners[0].Path)
    foreach ($consumer in @($itemConsumers[$changed])) {
        [void]$ownerPaths.Add($consumer)
    }
}

$visited = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
$pending = [System.Collections.Generic.Queue[string]]::new()
foreach ($ownerPath in $ownerPaths) {
    $pending.Enqueue($ownerPath)
}

while ($pending.Count -gt 0) {
    $projectPath = $pending.Dequeue()
    if (-not $visited.Add($projectPath)) {
        continue
    }

    foreach ($dependent in @($reverseReferences[$projectPath])) {
        $pending.Enqueue($dependent)
    }
}

$selectedProjects = @($testProjects | Where-Object { $visited.Contains($_) } | Sort-Object)
if ($selectedProjects.Count -eq 0) {
    $decision = New-Decision -Mode "full" -Reason "empty-test-selection"
    $decision | ConvertTo-Json -Depth 6 -Compress
    return
}

$decision = New-Decision -Mode "selected" -Reason "project-closure" -SelectedProjects $selectedProjects
$decision | ConvertTo-Json -Depth 6 -Compress
