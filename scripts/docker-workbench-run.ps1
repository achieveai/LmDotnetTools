[CmdletBinding()]
param(
    [string]$ImageName = "lmdotnettools-workbench",
    [string]$RepositoryPath = (Join-Path $PSScriptRoot ".."),
    [string]$WorkspacePath = "/workspace",
    [string[]]$Mount = @(),
    [string[]]$EnvVar = @(),
    [switch]$EnableHostDockerAccess,
    [switch]$DisableGeneratedArtifactIsolation,
    [switch]$RunAsRoot,
    [string[]]$ContainerCommand = @("pwsh", "-NoLogo")
)

$ErrorActionPreference = "Stop"

function Resolve-MountArguments {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Spec
    )

    $parts = $Spec -split "=", 2
    if ($parts.Count -ne 2) {
        throw "Mount spec '$Spec' must be in the form '<host-path>=<container-path>'."
    }

    $hostPath = (Resolve-Path $parts[0]).Path
    $containerPath = $parts[1]

    return @("--mount", "type=bind,source=$hostPath,target=$containerPath")
}

function Get-IsolatedDotNetArtifactTargets {
    param(
        [Parameter(Mandatory = $true)]
        [string]$RepoRoot,

        [Parameter(Mandatory = $true)]
        [string]$WorkspaceRoot
    )

    $targets = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    $projectFiles = Get-ChildItem -Path $RepoRoot -Recurse -File -Filter *.csproj

    foreach ($projectFile in $projectFiles) {
        $relativeProjectDirectory = [System.IO.Path]::GetRelativePath($RepoRoot, $projectFile.Directory.FullName)
        $relativeProjectDirectory = $relativeProjectDirectory -replace "\\", "/"

        $containerProjectDirectory = if ([string]::IsNullOrWhiteSpace($relativeProjectDirectory) -or $relativeProjectDirectory -eq ".") {
            $WorkspaceRoot
        }
        else {
            "$WorkspaceRoot/$relativeProjectDirectory"
        }

        [void]$targets.Add("$containerProjectDirectory/bin")
        [void]$targets.Add("$containerProjectDirectory/obj")
    }

    return @($targets)
}

$repoRoot = (Resolve-Path $RepositoryPath).Path

$dockerArgs = @(
    "run",
    "--rm",
    "-it",
    "--workdir",
    $WorkspacePath,
    "--mount",
    "type=bind,source=$repoRoot,target=$WorkspacePath"
)

$isolatedArtifactTargets = @(
    "$WorkspacePath/samples/LmStreaming.Sample/ClientApp/node_modules",
    "$WorkspacePath/samples/LmStreaming.Sample/wwwroot/dist"
)
$isolatedArtifactTargets += Get-IsolatedDotNetArtifactTargets -RepoRoot $repoRoot -WorkspaceRoot $WorkspacePath
$isolatedArtifactTargets = $isolatedArtifactTargets | Sort-Object -Unique

foreach ($envSpec in $EnvVar) {
    $dockerArgs += @("-e", $envSpec)
}

foreach ($mountSpec in $Mount) {
    $dockerArgs += Resolve-MountArguments -Spec $mountSpec
}

if (-not $DisableGeneratedArtifactIsolation) {
    $dockerArgs += @("-e", "WORKBENCH_FIXUP_PATHS=$($isolatedArtifactTargets -join '|')")
    foreach ($target in $isolatedArtifactTargets) {
        $dockerArgs += @("--mount", "type=volume,target=$target")
    }
}

if ($EnableHostDockerAccess) {
    $dockerArgs += @("--mount", "type=bind,source=/var/run/docker.sock,target=/var/run/docker.sock")
}

if ($RunAsRoot) {
    $dockerArgs += @("-e", "WORKBENCH_RUN_AS_ROOT=true", "--user", "root")
}

$dockerArgs += $ImageName
$dockerArgs += $ContainerCommand

Write-Host "Running: docker $($dockerArgs -join ' ')"
& docker @dockerArgs

if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}
