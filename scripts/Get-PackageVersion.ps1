#requires -Version 7.0
[CmdletBinding()]
param(
    [string]$RepositoryRoot = (Join-Path $PSScriptRoot ".."),
    [string]$Commit = "HEAD",
    [ValidateSet("push", "pull_request", "workflow_dispatch")]
    [string]$EventName = "workflow_dispatch",
    [string]$Ref = "refs/heads/main",
    [string]$PullRequestBase,
    [string]$PullRequestNumber,
    [string]$RunNumber = "0"
)
$ErrorActionPreference = "Stop"
$root = (Resolve-Path -LiteralPath $RepositoryRoot).Path
function Invoke-VersionGit {
    param([string[]]$Arguments)
    $output = @(& git -C $root @Arguments 2>&1)
    if ($LASTEXITCODE -ne 0) { throw "Cannot determine package version: $($output -join ' ')" }
    return $output
}
if ((Invoke-VersionGit @("rev-parse", "--is-shallow-repository")) -eq "true") {
    throw "Cannot allocate a release from shallow history. Checkout with fetch-depth: 0."
}
$props = [xml][System.IO.File]::ReadAllText((Join-Path $root "Directory.Build.props"))
$major = [string]$props.SelectSingleNode("//MajorVersion").InnerText
$minor = [string]$props.SelectSingleNode("//MinorVersion").InnerText
$basePatch = [string]$props.SelectSingleNode("//PatchVersion").InnerText
$anchor = [string]$props.SelectSingleNode("//ReleaseBaseCommit").InnerText
if ($major -notmatch '^\d+$' -or $minor -notmatch '^\d+$' -or $basePatch -notmatch '^\d+$' -or $anchor -notmatch '^[0-9a-f]{40}$') {
    throw "Directory.Build.props must declare a numeric release baseline and full ReleaseBaseCommit."
}
$revision = $Commit
$extraCommit = 0
if ($EventName -eq "pull_request") {
    if (-not $PullRequestBase -or $PullRequestNumber -notmatch '^[1-9]\d*$') { throw "A PR preview requires its target base SHA and PR number." }
    $revision = $PullRequestBase
    $extraCommit = 1
}
$sha = [string](Invoke-VersionGit @("rev-parse", "--verify", "--end-of-options", "$revision^{commit}"))
$history = @(Invoke-VersionGit @("rev-list", "--first-parent", $sha))
$distance = [array]::IndexOf($history, $anchor)
if ($distance -lt 0) { throw "ReleaseBaseCommit must exist on the revision's first-parent history." }
$patch = [int]$basePatch + $distance + $extraCommit
$stableVersion = "$major.$minor.$patch"
$publish = $EventName -eq "push"
$label = ""
if ($Ref.StartsWith("refs/tags/v", [System.StringComparison]::Ordinal)) {
    if ($Ref.Substring("refs/tags/v".Length) -ne $stableVersion) { throw "Tag $Ref does not match computed version $stableVersion." }
    $mainHistory = @(Invoke-VersionGit @("rev-list", "--first-parent", "refs/remotes/origin/main"))
    if ($mainHistory -notcontains $sha) { throw "Release tags must target a commit on main's first-parent history." }
}
elseif ($EventName -eq "pull_request") { $label = "pr.$PullRequestNumber.$RunNumber" }
elseif ($Ref -ne "refs/heads/main" -and $publish) { throw "Only main or a matching main release tag may publish packages." }
elseif (-not $publish) { $label = "ci.$RunNumber" }
if ($label -and $RunNumber -notmatch '^\d+$') { throw "Preview package run number must be numeric." }
[ordered]@{
    version = "$stableVersion$(if ($label) { "-$label" })"
    major = [int]$major; minor = [int]$minor; patch = $patch; label = $label
    publish = $publish; baseCommit = $anchor; distance = $distance; commit = $sha
} | ConvertTo-Json -Compress
