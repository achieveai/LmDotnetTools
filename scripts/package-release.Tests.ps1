$ErrorActionPreference = "Stop"

function Assert-Release {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

function Invoke-FixtureGit {
    param([string[]]$Arguments)
    $value = @(& git -C $fixture @Arguments 2>&1)
    if ($LASTEXITCODE -ne 0) { throw "Fixture Git failed: $($value -join ' ')" }
    return ($value -join "`n").Trim()
}

function Add-FixtureCommit {
    param([string]$Name)
    Set-Content -LiteralPath (Join-Path $fixture "$Name.txt") -Value $Name
    $null = Invoke-FixtureGit @("add", "--", "$Name.txt")
    $null = Invoke-FixtureGit @("commit", "-m", $Name)
    Invoke-FixtureGit @("rev-parse", "HEAD")
}

function Get-VersionStep {
    $lines = [System.IO.File]::ReadAllLines((Join-Path $PSScriptRoot "../.github/workflows/ci.yml"))
    $idLine = [array]::IndexOf($lines, "        id: version")
    $runLine = $idLine + 1
    while ($lines[$runLine] -ne "        run: |") { $runLine++ }
    $body = [System.Collections.Generic.List[string]]::new()
    for ($i = $runLine + 1; $i -lt $lines.Length; $i++) {
        if ($lines[$i].Trim() -and -not $lines[$i].StartsWith("          ")) { break }
        $body.Add($(if ($lines[$i].Length -ge 10) { $lines[$i].Substring(10) } else { "" }))
    }
    [scriptblock]::Create(($body -join "`n"))
}

function Get-ReleaseVersion {
    param([hashtable]$Options = @{})
    $parameters = @{ RepositoryRoot = $fixture; Commit = "HEAD"; EventName = "push"; Ref = "refs/heads/main"; RunNumber = "7" }
    foreach ($key in $Options.Keys) { $parameters[$key] = $Options[$key] }
    & (Join-Path $PSScriptRoot "Get-PackageVersion.ps1") @parameters | ConvertFrom-Json
}

function Assert-ReleaseFailure {
    param([scriptblock]$Action, [string]$Message)
    $caught = $false
    try { & $Action | Out-Null } catch { $caught = $_.Exception.Message -like $Message }
    Assert-Release $caught "Expected failure matching '$Message'."
}

$fixture = Join-Path ([System.IO.Path]::GetTempPath()) "package-release-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $fixture -Force | Out-Null
$environmentNames = @("GITHUB_WORKSPACE", "GITHUB_SHA", "GITHUB_REF", "GITHUB_EVENT_NAME", "GITHUB_RUN_NUMBER", "GITHUB_OUTPUT", "GITHUB_STEP_SUMMARY", "PR_BASE_SHA", "PR_NUMBER")
$savedEnvironment = @{}
foreach ($name in $environmentNames) { $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name) }
try {
    $null = Invoke-FixtureGit @("init", "-b", "main")
    $null = Invoke-FixtureGit @("config", "user.name", "Release fixture")
    $null = Invoke-FixtureGit @("config", "user.email", "release-fixture@example.invalid")
    $anchor = Add-FixtureCommit "published-33"
    Set-Content -LiteralPath (Join-Path $fixture "Directory.Build.props") -Value @"
<Project><PropertyGroup><MajorVersion>1</MajorVersion><MinorVersion>0</MinorVersion><PatchVersion>33</PatchVersion><ReleaseBaseCommit>$anchor</ReleaseBaseCommit></PropertyGroup></Project>
"@
    $first = Add-FixtureCommit "first-main-change"
    New-Item -ItemType Directory -Path (Join-Path $fixture "scripts") -Force | Out-Null
    if (Test-Path (Join-Path $PSScriptRoot "Get-PackageVersion.ps1")) {
        Copy-Item (Join-Path $PSScriptRoot "Get-PackageVersion.ps1") (Join-Path $fixture "scripts/Get-PackageVersion.ps1")
    }
    $env:GITHUB_WORKSPACE = $fixture
    $env:GITHUB_SHA = $first
    $env:GITHUB_REF = "refs/heads/main"
    $env:GITHUB_EVENT_NAME = "push"
    $env:GITHUB_RUN_NUMBER = "7"
    $env:GITHUB_OUTPUT = Join-Path $fixture "outputs.txt"
    $env:GITHUB_STEP_SUMMARY = Join-Path $fixture "summary.md"
    function global:dotnet { $global:LASTEXITCODE = 0; "1.0.33" }
    Push-Location $fixture
    try { & (Get-VersionStep) | Out-Null } finally { Pop-Location; Remove-Item Function:\global:dotnet }
    $outputs = @{}
    foreach ($line in Get-Content -LiteralPath $env:GITHUB_OUTPUT) {
        $key, $value = $line -split "=", 2
        $outputs[$key] = $value
    }
    $workflowVersion = if ($outputs.ContainsKey("version")) { $outputs.version } else { "1.0.33-$($outputs.label)" }
    Assert-Release ($workflowVersion -eq "1.0.34") "First main change must publish stable 1.0.34; workflow chose $workflowVersion."
    $expectedOutputs = [ordered]@{ publish = "True"; major = "1"; minor = "0"; patch = "34"; label = "" }
    foreach ($property in $expectedOutputs.Keys) {
        Assert-Release ($outputs.ContainsKey($property) -and $outputs[$property] -ceq $expectedOutputs[$property]) "Main workflow must export $property=$($expectedOutputs[$property]); got '$($outputs[$property])'."
    }

    Assert-Release ((Get-ReleaseVersion).version -eq "1.0.34") "First main commit must advance one patch."
    Assert-Release ((Get-ReleaseVersion).version -eq "1.0.34") "Retrying the same commit must retain its allocated version."
    $second = Add-FixtureCommit "second-main-change"
    Assert-Release ((Get-ReleaseVersion).version -eq "1.0.35") "Second main commit must advance to 1.0.35."
    $null = Invoke-FixtureGit @("switch", "-c", "feature")
    $null = Add-FixtureCommit "feature-one"
    $null = Add-FixtureCommit "feature-two"
    $preview = Get-ReleaseVersion @{ EventName = "pull_request"; Ref = "refs/pull/9/merge"; PullRequestBase = $second; PullRequestNumber = "9" }
    Assert-Release ($preview.version -eq "1.0.36-pr.9.7" -and -not $preview.publish) "PR preview must count target main, not feature commits, and must not publish."
    $null = Invoke-FixtureGit @("switch", "main")
    $null = Invoke-FixtureGit @("merge", "--no-ff", "feature", "-m", "merge-feature")
    $merged = Invoke-FixtureGit @("rev-parse", "HEAD")
    Assert-Release ((Get-ReleaseVersion).version -eq "1.0.36") "A two-commit feature merge must advance only one main patch."
    $tree = Invoke-FixtureGit @("rev-parse", "HEAD^{tree}")
    $foreignRoot = Invoke-FixtureGit @("commit-tree", $tree, "-m", "unrelated-first-parent")
    $wrongParentMerge = Invoke-FixtureGit @("commit-tree", $tree, "-p", $foreignRoot, "-p", $anchor, "-m", "anchor-only-on-second-parent")
    Assert-ReleaseFailure { Get-ReleaseVersion @{ Commit = $wrongParentMerge } } "*first-parent*"
    $null = Invoke-FixtureGit @("update-ref", "refs/remotes/origin/main", $merged)
    $tagged = Get-ReleaseVersion @{ Ref = "refs/tags/v1.0.36" }
    Assert-Release ($tagged.version -eq "1.0.36" -and $tagged.publish) "A matching tag must reuse the deterministic release version."
    Assert-ReleaseFailure { Get-ReleaseVersion @{ Ref = "refs/tags/v1.0.37" } } "*does not match*"
    $null = Invoke-FixtureGit @("switch", "-c", "unmerged-release")
    $featureOnly = Add-FixtureCommit "unmerged-release"
    $null = Invoke-FixtureGit @("tag", "v1.0.37", $featureOnly)
    $null = Invoke-FixtureGit @("switch", "main")
    Assert-ReleaseFailure { Get-ReleaseVersion @{ Commit = "refs/tags/v1.0.37"; Ref = "refs/tags/v1.0.37" } } "*Release tags must target a commit on main's first-parent history.*"
    Assert-ReleaseFailure { Get-ReleaseVersion @{ EventName = "push"; Ref = "refs/heads/feature" } } "*Only main*"
    $manual = Get-ReleaseVersion @{ EventName = "workflow_dispatch" }
    Assert-Release ($manual.version -eq "1.0.36-ci.7" -and -not $manual.publish) "Manual validation must not publish a stable release."
    $defaultRun = & (Join-Path $PSScriptRoot "Get-PackageVersion.ps1") -RepositoryRoot $fixture -EventName workflow_dispatch | ConvertFrom-Json
    Assert-Release ($defaultRun.version -eq "1.0.36-ci.0" -and -not $defaultRun.publish) "The default run number must remain canonical zero."
    foreach ($invalidRun in @("007", [string][char]0x0667)) {
        Assert-ReleaseFailure { Get-ReleaseVersion @{ EventName = "workflow_dispatch"; RunNumber = $invalidRun } } "*canonical ASCII*"
    }
    Assert-ReleaseFailure { Get-ReleaseVersion @{ EventName = "pull_request"; Ref = "refs/pull/9/merge" } } "*base*"
    $propsPath = Join-Path $fixture "Directory.Build.props"
    $props = Get-Content -LiteralPath $propsPath -Raw
    $foreign = Add-FixtureCommit "foreign-anchor"
    Set-Content -LiteralPath $propsPath -Value ($props.Replace($anchor, $foreign))
    Assert-ReleaseFailure { Get-ReleaseVersion @{ Commit = $merged } } "*first-parent*"
    Set-Content -LiteralPath $propsPath -Value ($props.Replace($anchor, ("0" * 40)))
    Assert-ReleaseFailure { Get-ReleaseVersion } "*first-parent*"
    Set-Content -LiteralPath $propsPath -Value $props
    $shallow = Join-Path $fixture "shallow"
    $null = & git clone --depth 1 "file:///$($fixture.Replace('\', '/'))" $shallow 2>&1
    Assert-Release ($LASTEXITCODE -eq 0) "Shallow fixture clone failed."
    Copy-Item $propsPath (Join-Path $shallow "Directory.Build.props")
    Assert-ReleaseFailure { Get-ReleaseVersion @{ RepositoryRoot = $shallow } } "*shallow*"

    $packages = Join-Path $fixture "packages with spaces"
    $feed = Join-Path $fixture "feed"
    New-Item -ItemType Directory -Path $packages, $feed -Force | Out-Null
    $pushScript = Join-Path $PSScriptRoot "Push-NuGetPackages.ps1"
    Assert-ReleaseFailure { & $pushScript -PackageDirectory $packages -Source $feed -ApiKey "fixture" } "*No NuGet packages*"
    foreach ($name in @("A", "B")) { Set-Content (Join-Path $packages "AchieveAi.LmDotnetTools.$name.1.0.34.nupkg") $name }
    Set-Content (Join-Path $packages "AchieveAi.LmDotnetTools.A.1.0.34.snupkg") "symbol"
    $global:ReleasePushFailure = $false
    function global:dotnet {
        if ($args[0] -ne "nuget" -or $args[1] -ne "push" -or -not (Test-Path -LiteralPath $args[2] -PathType Leaf)) { throw "Push did not receive an existing literal package file." }
        if ($global:ReleasePushFailure) { $global:LASTEXITCODE = 23; return }
        $sourceIndex = [array]::IndexOf($args, "--source")
        $destination = Join-Path $args[$sourceIndex + 1] ([System.IO.Path]::GetFileName($args[2]))
        if (-not (Test-Path $destination)) { Copy-Item -LiteralPath $args[2] -Destination $destination }
        elseif ($args -notcontains "--skip-duplicate") { throw "Retry must skip already published packages." }
        $global:LASTEXITCODE = 0
    }
    & $pushScript -PackageDirectory $packages -Source $feed -ApiKey "fixture"
    & $pushScript -PackageDirectory $packages -Source $feed -ApiKey "fixture"
    Assert-Release (@(Get-ChildItem $feed -File).Count -eq 2) "Push must send both package paths, preserve spaces, and safely retry without separately pushing symbols."
    $global:ReleasePushFailure = $true
    Assert-ReleaseFailure { & $pushScript -PackageDirectory $packages -Source $feed -ApiKey "fixture" } "*exit 23*"
    Write-Host "PASS: package releases advance on main history, retain retry versions, validate previews/tags and push real package paths."
}
finally {
    Remove-Item Function:\global:dotnet -ErrorAction SilentlyContinue
    Remove-Variable ReleasePushFailure -Scope Global -ErrorAction SilentlyContinue
    foreach ($name in $environmentNames) { [Environment]::SetEnvironmentVariable($name, $savedEnvironment[$name]) }
    $tempRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
    $resolvedFixture = [System.IO.Path]::GetFullPath($fixture)
    if (-not $resolvedFixture.StartsWith($tempRoot, [System.StringComparison]::OrdinalIgnoreCase)) { throw "Fixture escaped the temporary directory." }
    if (Test-Path -LiteralPath $resolvedFixture) { Remove-Item -LiteralPath $resolvedFixture -Recurse -Force }
}
$global:LASTEXITCODE = 0
