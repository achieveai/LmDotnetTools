[CmdletBinding()]
param(
    [switch]$IncludeBrowserE2E,
    [switch]$SkipCoreCi
)

$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$browserProject = "tests/LmStreaming.Sample.Browser.E2E.Tests/LmStreaming.Sample.Browser.E2E.Tests.csproj"
$playwrightScript = Join-Path $repoRoot "tests/LmStreaming.Sample.Browser.E2E.Tests/bin/Release/net9.0/playwright.ps1"
$clientAppDir = Join-Path $repoRoot "samples/LmStreaming.Sample/ClientApp"

Set-Location $repoRoot

function Invoke-WorkbenchStep {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name,

        [Parameter(Mandatory = $true)]
        [scriptblock]$Command,

        [string]$WorkingDirectory
    )

    Write-Host "==> $Name"

    if ([string]::IsNullOrWhiteSpace($WorkingDirectory)) {
        & $Command
    }
    else {
        Push-Location $WorkingDirectory
        try {
            & $Command
        }
        finally {
            Pop-Location
        }
    }

    if ($LASTEXITCODE -ne 0) {
        throw "Step '$Name' failed with exit code $LASTEXITCODE."
    }
}

Invoke-WorkbenchStep "tool versions" {
    dotnet --list-sdks
    dotnet --list-runtimes
    node --version
    npm --version
    pwsh --version
    python3 --version
    uv --version
    uvx --version
    docker --version
}

Invoke-WorkbenchStep "agent CLI checks" {
    $npmRoot = (& npm root -g).Trim()
    $claudeCliPath = Join-Path $npmRoot "@anthropic-ai/claude-agent-sdk/cli.js"

    if (-not (Test-Path $claudeCliPath)) {
        throw "Claude Agent SDK entrypoint was not found at '$claudeCliPath'."
    }

    $claudeVersion = (& node -e "const path = require('path'); const pkg = path.join(process.argv[1], '@anthropic-ai', 'claude-agent-sdk', 'package.json'); console.log(require(pkg).version);" $npmRoot).Trim()
    if ([string]::IsNullOrWhiteSpace($claudeVersion)) {
        throw "Could not determine Claude Agent SDK package version."
    }

    Write-Host "Claude Agent SDK package version: $claudeVersion"
    claude --version
    copilot --version
    codex --version
}

Invoke-WorkbenchStep "repo MCP prerequisites" {
    npx --version
    uvx --version
}

if (-not $SkipCoreCi) {
    Invoke-WorkbenchStep "core CI validation" {
        pwsh -NoLogo -NoProfile -File (Join-Path $repoRoot "scripts\ci-test.ps1")
    }
}

if ($IncludeBrowserE2E) {
    Invoke-WorkbenchStep "browser client install" -WorkingDirectory $clientAppDir {
        npm ci
    }

    Invoke-WorkbenchStep "browser client build" -WorkingDirectory $clientAppDir {
        npm run build
    }

    Invoke-WorkbenchStep "browser E2E restore" {
        dotnet restore $browserProject
    }

    Invoke-WorkbenchStep "browser E2E build" {
        dotnet build $browserProject --configuration Release --no-restore -p:BuildClientApp=true
    }

    Invoke-WorkbenchStep "Playwright browser install" {
        if (-not (Test-Path $playwrightScript)) {
            throw "Playwright install script was not found at '$playwrightScript'. Build the browser E2E project first."
        }

        pwsh -NoLogo -NoProfile -File $playwrightScript install chromium
    }

    Invoke-WorkbenchStep "browser E2E tests" {
        dotnet test $browserProject --configuration Release --no-build --logger "trx;LogFileName=browser-e2e.trx" --results-directory ".logs/test-results"
    }
}
