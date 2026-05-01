[CmdletBinding()]
param(
    [string]$ImageName = "lmdotnettools-workbench",
    [string]$DockerfilePath = "Dockerfile",
    [int]$UserUid = 1001,
    [int]$UserGid = 1001,
    [switch]$InstallOptionalCopilotSdk,
    [switch]$Pull,
    [switch]$NoCache,
    # RevoBot cred-helper templates source. The Dockerfile COPYs from
    # .credhelper-staging/ (git-ignored), so we sync the canonical templates
    # from revobot into that directory before each build.
    [string]$RevobotTemplatesPath = "b:/sources/revobot/templates"
)

$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$dockerfile = Join-Path $repoRoot $DockerfilePath

if (-not (Test-Path $dockerfile)) {
    throw "Dockerfile '$dockerfile' was not found."
}

# Stage RevoBot cred-helper templates into the build context.
$stagingDir = Join-Path $repoRoot ".credhelper-staging"
$requiredTemplates = @("git-credential-revobot", "revobot-entrypoint")
if (-not (Test-Path $RevobotTemplatesPath)) {
    throw "RevobotTemplatesPath '$RevobotTemplatesPath' does not exist. Pass -RevobotTemplatesPath to point at the revobot/templates directory."
}
# Clean any previous stage so renamed/removed upstream templates don't
# linger in the build context and silently get baked into the image.
if (Test-Path $stagingDir) {
    Remove-Item -Recurse -Force $stagingDir -ErrorAction SilentlyContinue
}
New-Item -ItemType Directory -Force -Path $stagingDir | Out-Null
foreach ($name in $requiredTemplates) {
    $src = Join-Path $RevobotTemplatesPath $name
    if (-not (Test-Path $src)) {
        throw "Required cred-helper template '$src' is missing."
    }
    Copy-Item -Force $src (Join-Path $stagingDir $name)
}
Write-Host "Staged cred-helper templates from $RevobotTemplatesPath -> $stagingDir"

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
