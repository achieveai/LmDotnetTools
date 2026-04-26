# Docker Workbench

This repository now includes a repo-level `Dockerfile` for an **interactive development workbench**
rather than an app-runtime image. The intent is:

- build the image once,
- bind-mount this repository into `/workspace`,
- bind-mount agent CLI config/auth directories from the host as needed,
- run Claude/Copilot/Codex-driven workflows, builds, and tests inside the container.

## What's in the image

The image is designed around the repo's current build/test surface:

- .NET SDK coverage for the repo's `net8.0` / `net9.0` projects
- Node.js 22 + npm
- PowerShell 7
- git
- Docker CLI (for the optional host-Docker-access profile)
- Python 3 + `uv` / `uvx` (for the repo's `.mcp.json`)
- Playwright Chromium system dependencies
- Playwright Chromium browser binaries
- npm-installed agent CLIs:
  - `@anthropic-ai/claude-code@2.1.114` (provides the human-facing `claude` terminal command)
  - `@anthropic-ai/claude-agent-sdk@0.1.55` (kept separately because the current `ClaudeAgentSdkProvider` still expects the older `cli.js` package layout)
  - `@github/copilot`
  - `@openai/codex`

> `@anthropic-ai/claude-agent-sdk` is installed with npm's global prefix set to `/usr/local`,
> which matches the Unix lookup path hard-coded by `ClaudeAgentSdkProvider`.

## Build the image

PowerShell helper:

```powershell
.\scripts\docker-workbench-build.ps1
```

Raw Docker command:

```powershell
docker build -t lmdotnettools-workbench .
```

If you want the container user/group IDs to match a specific host mapping:

```powershell
.\scripts\docker-workbench-build.ps1 -UserUid 1000 -UserGid 1000
```

If you want to experiment with the separate Copilot SDK package in the image as well:

```powershell
.\scripts\docker-workbench-build.ps1 -InstallOptionalCopilotSdk
```

## Run an isolated interactive container

The helper script keeps the repo mounted at `/workspace` and defaults to an interactive `pwsh` shell.
It also isolates generated build artifacts in container-managed volumes so Linux builds do not fight
with Windows-host `node_modules`, `wwwroot/dist`, or project `bin` / `obj` contents. At startup,
the image fixes those isolated volume permissions and then drops to the non-root workbench user
automatically.

```powershell
.\scripts\docker-workbench-run.ps1 `
  -Mount @(
    "$HOME\.claude=/home/dev/.claude",
    "$HOME\.claude.json=/home/dev/.claude.json",
    "$HOME\.codex=/home/dev/.codex"
  )
```

If you want to mount additional config/auth directories, pass more entries in the same `-Mount`
array:

```powershell
.\scripts\docker-workbench-run.ps1 `
  -Mount @(
    "C:\path\to\copilot-config=/home/dev/.config/github-copilot",
    "C:\path\to\extra-cache=/home/dev/.cache/custom-tool"
  )
```

If you need to pass through environment variables:

```powershell
.\scripts\docker-workbench-run.ps1 `
  -EnvVar "ANTHROPIC_API_KEY=$env:ANTHROPIC_API_KEY" `
  -EnvVar "OPENAI_API_KEY=$env:OPENAI_API_KEY"
```

## Optional host-Docker-access profile

The same image can be used with the host Docker daemon mounted in. This is intentionally opt-in
because it effectively grants high-trust host access to processes in the container.

```powershell
.\scripts\docker-workbench-run.ps1 `
  -EnableHostDockerAccess `
  -RunAsRoot `
  -Mount @(
    "$HOME\.claude=/home/dev/.claude",
    "$HOME\.claude.json=/home/dev/.claude.json"
  )
```

Notes:

- `-RunAsRoot` disables the normal privilege drop and leaves the container command running as root.
  Use it when you need direct access to the mounted Docker socket or another root-owned path.
- If you do not need sibling-container workflows, leave host Docker access disabled.

## Validate the mounted repo inside the container

Once you're inside the container, run the verification script from the mounted repo:

```powershell
pwsh scripts/docker-workbench-verify.ps1
```

That script checks:

- .NET / Node / PowerShell / Python / `uv` / Docker CLI availability
- Claude/Copilot/Codex CLI availability
- repo MCP prerequisites (`npx`, `uvx`)
- the repo's core CI flow via `scripts/ci-test.ps1`

If you only want to smoke-test the image/tooling layer first:

```powershell
pwsh scripts/docker-workbench-verify.ps1 -SkipCoreCi
```

You can also launch that verification command directly from the host:

```powershell
.\scripts\docker-workbench-run.ps1 `
  -ContainerCommand @("pwsh", "-NoLogo", "-File", "scripts/docker-workbench-verify.ps1", "-SkipCoreCi")
```

If you intentionally want the raw generated frontend and .NET build artifacts from the mounted repo
to remain visible inside the container, add `-DisableGeneratedArtifactIsolation`. On a Windows host,
leaving isolation enabled is the safer default because .NET timestamp updates against bind-mounted
`obj` files can fail for non-root users.

## Running Claude non-interactively in the workbench

When you want to test the real Claude CLI inside the container, mount both the Claude directory and
the top-level JSON file from the host:

```powershell
.\scripts\docker-workbench-run.ps1 `
  -Mount @(
    "$HOME\.claude=/home/dev/.claude",
    "$HOME\.claude.json=/home/dev/.claude.json"
  )
```

The official `claude` CLI supports one-shot print mode via `-p`. A typical non-interactive
invocation inside the workbench looks like:

```powershell
claude -p --dangerously-skip-permissions "Check the mounted repo, run dotnet build LmDotnetTools.sln, then run dotnet test LmDotnetTools.sln --no-build, and report a concise summary."
```

To include the browser E2E path as well:

```powershell
pwsh scripts/docker-workbench-verify.ps1 -IncludeBrowserE2E
```

That extended mode additionally:

- builds `tests/LmStreaming.Sample.Browser.E2E.Tests` with `BuildClientApp=true`
- installs the repo's Microsoft.Playwright browser assets using the emitted `playwright.ps1`
- runs the browser E2E test project
- refreshes the sample client with in-container `npm ci` / `npm run build` so Linux browser builds do not rely on host-generated frontend artifacts

## Why the image includes Playwright prerequisites

This repo's browser E2E workflow is not hypothetical. CI already runs:

- Ubuntu-based browser tests
- Node-based SPA build
- `pwsh tests/LmStreaming.Sample.Browser.E2E.Tests/bin/Release/net9.0/playwright.ps1 install --with-deps chromium`

Because of that, the workbench image treats Playwright dependencies as first-class prerequisites,
not optional extras.
