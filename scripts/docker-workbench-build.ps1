[CmdletBinding()]
param(
    [string]$ImageName = "lmdotnettools-workbench",
    [string]$DockerfilePath = "Dockerfile",
    [int]$UserUid = 1001,
    [int]$UserGid = 1001,
    [switch]$InstallOptionalCopilotSdk,
    [switch]$Pull,
    [switch]$NoCache
)

$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$dockerfile = Join-Path $repoRoot $DockerfilePath

if (-not (Test-Path $dockerfile)) {
    throw "Dockerfile '$dockerfile' was not found."
}

$dockerArgs = @(
    "build",
    "--file",
    $dockerfile,
    "--tag",
    $ImageName,
    "--build-arg",
    "USER_UID=$UserUid",
    "--build-arg",
    "USER_GID=$UserGid"
)

if ($InstallOptionalCopilotSdk) {
    $dockerArgs += @("--build-arg", "INSTALL_OPTIONAL_COPILOT_SDK=true")
}

if ($Pull) {
    $dockerArgs += "--pull"
}

if ($NoCache) {
    $dockerArgs += "--no-cache"
}

$dockerArgs += $repoRoot

Write-Host "Running: docker $($dockerArgs -join ' ')"
& docker @dockerArgs

if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}
