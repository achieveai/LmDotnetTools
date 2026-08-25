# LmStreaming Standalone Publish Implementation Plan

**Status:** Implemented — shipped in `22c3e52d` (#290). `samples/LmStreaming.Sample/publish-launch.ps1` now runs the direct build to publish to validate to launch pipeline, with no Vite dev-server path.

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make `samples/LmStreaming.Sample/publish-launch.ps1` directly build, publish, validate, and run a Production artifact from a temporary repository scratchpad directory without starting a Vite development server.

**Architecture:** Replace the existing dual-process development launcher with one production pipeline inside the same script. The script runs `npm ci` and `npm run build`, publishes the ASP.NET Core app with the client MSBuild target disabled, validates the copied Vite bundle, launches the published executable, then proves API and static-asset readiness on the ASP.NET Core port.

**Tech Stack:** PowerShell 7, npm/Vite, .NET SDK/MSBuild, ASP.NET Core static files.

## Global Constraints

- Modify the existing `samples/LmStreaming.Sample/publish-launch.ps1`; do not add a second or versioned launcher.
- The script directly and unconditionally runs `npm ci`, then `npm run build`.
- The script runs `dotnet publish` with `BuildClientApp=false` so MSBuild does not duplicate the client build.
- Generated output lives at repository root `.claude/scratchpad/lmstreaming-standalone-publish/run-<UTC-yyyyMMdd-HHmmss>-<PID>/` and remains untracked.
- The published process receives `ASPNETCORE_ENVIRONMENT=Production` and `DOTNET_ENVIRONMENT=Production`.
- Preserve existing `-Port`, `-Configuration`, `-WebhookBaseUrl`, and `-Force` semantics.
- Preserve the existing backend argument `--SandboxGateway:WorkspaceBasePath=`. Do not invent a new workspace parameter.
- Remove `-VitePort`, `-SkipClientBuild`, Vite port selection, Vite environment variables, Vite process launch, and Vite-specific readiness helpers.
- Validate every JavaScript and CSS file referenced by published `wwwroot/dist/index.html`; do not assume one fixed Vite filename.
- A readiness failure is fatal, not a warning.
- Keep the published application supervised until it exits or the user presses Ctrl+C.
- Do not commit generated artifacts.

## File Structure

- Modify: `samples/LmStreaming.Sample/publish-launch.ps1` — complete standalone pipeline and verification.
- Modify only if launcher usage becomes inaccurate: `samples/LmStreaming.Sample/README.md` — standalone launcher command and behavior.
- Generate: `.claude/scratchpad/lmstreaming-standalone-publish/run-*/` — demonstration publish artifact and run state.
- Read only: `samples/LmStreaming.Sample/LmStreaming.Sample.csproj` — existing opt-in client target.
- Read only: `samples/LmStreaming.Sample/Program.cs` — Production static-file and SPA fallback behavior.

---

### Task 1: Pin the old launcher’s failing standalone contract

**Files:**
- Test through: `samples/LmStreaming.Sample/publish-launch.ps1`
- Record evidence in: `.claude/scratchpad/conversation_memories/lmstreaming-standalone-publish/implementation.md`

**Interfaces:**
- Consumes: current launcher parameters and process flow.
- Produces: RED evidence that the current script starts Vite/Development and does not create a publish artifact.

- [ ] **Step 1: Capture the current static contract**

Run from repository root:

```powershell
$script = Get-Content 'samples/LmStreaming.Sample/publish-launch.ps1' -Raw
[pscustomobject]@{
    StartsVite = $script -match 'npm\s+run\s+dev'
    UsesDevelopment = $script -match "ASPNETCORE_ENVIRONMENT\s*=\s*'Development'"
    CallsPublish = $script -match 'dotnet\s+publish'
    LaunchesSource = $script -match 'dotnet\s+run'
}
```

Expected RED result:

```text
StartsVite      True
UsesDevelopment True
CallsPublish    False
LaunchesSource  True
```

- [ ] **Step 2: Run the existing launcher only long enough to prove its current mode**

Use a free explicit port and capture output. Stop it after it reports its Vite/backend topology.

```powershell
pwsh -NoProfile -File 'samples/LmStreaming.Sample/publish-launch.ps1' -Port 15050 -VitePort 15173 -Force
```

Expected RED: output reports a Vite URL/process or Development flow and does not report a scratchpad publish directory.

- [ ] **Step 3: Record exact RED evidence**

Append the command, exit/termination state, and observed Vite/publish output to `implementation.md`. Do not treat manual termination after the evidence is captured as a launcher failure.

---

### Task 2: Replace development-only inputs and helpers

**Files:**
- Modify: `samples/LmStreaming.Sample/publish-launch.ps1:1-447`

**Interfaces:**
- Consumes: existing `Resolve-InstancePort`, `Wait-HttpReady`, `Stop-OwnedProcessTree`, `Write-RunStateFile`, `-Port`, `-Configuration`, `-WebhookBaseUrl`, and `-Force` behavior.
- Produces: a backend-only launcher foundation with no Vite runtime symbols.

- [ ] **Step 1: Rewrite comment-based help**

Describe one pipeline:

```text
npm ci -> npm run build -> dotnet publish -> validate -> launch published executable
```

Document the repository scratchpad artifact path and retained parameters. Remove `VitePort` and `SkipClientBuild` documentation and examples.

- [ ] **Step 2: Reduce the parameter block**

Use the current defaults for retained parameters:

```powershell
[CmdletBinding()]
param(
    [int] $Port = 5050,
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Debug',
    [string] $WebhookBaseUrl = 'https://lmstreaming.bhakars.internal',
    [switch] $Force
)
```

Do not add a workspace parameter. Preserve `--SandboxGateway:WorkspaceBasePath=` when constructing backend arguments.

- [ ] **Step 3: Remove Vite-only functions and state**

Delete:

```text
Test-ViteProxyReachesBackend
Test-ViteEntryModuleResolves
Test-ClientDependenciesFresh
Vite port resolution
$vite and $vitePortExplicit
VITE_DEV_PORT
VITE_BACKEND_ORIGIN
VITE_AUTO_RUN
npm run dev process creation
Vite process supervision/status/run-state fields
```

Keep backend port auto-selection and `-Force` behavior unchanged.

- [ ] **Step 4: Run a static GREEN contract check**

```powershell
$script = Get-Content 'samples/LmStreaming.Sample/publish-launch.ps1' -Raw
$forbidden = @(
    'VitePort', 'SkipClientBuild', 'npm run dev', 'VITE_DEV_PORT',
    'VITE_BACKEND_ORIGIN', 'VITE_AUTO_RUN', 'Test-ViteProxyReachesBackend',
    'Test-ViteEntryModuleResolves', 'Test-ClientDependenciesFresh', 'dotnet run'
)
$matches = $forbidden | Where-Object { $script.Contains($_) }
if ($matches) { throw "Forbidden launcher paths remain: $($matches -join ', ')" }
```

Expected: no exception and no matches.

---

### Task 3: Add direct build, publish, and artifact-validation helpers

**Files:**
- Modify: `samples/LmStreaming.Sample/publish-launch.ps1`

**Interfaces:**
- Produces:
  - `Invoke-ClientBuild([string] $ClientAppDirectory)` — throws on either npm failure.
  - `New-PublishRunDirectory([string] $RepositoryRoot)` — returns an absolute unique run directory.
  - `Invoke-ApplicationPublish([string] $ProjectFile, [string] $Configuration, [string] $OutputDirectory)` — throws on publish failure.
  - `Get-PublishedAssetReferences([string] $IndexPath)` — returns normalized `/dist/...` JS/CSS references.
  - `Confirm-PublishArtifact([string] $PublishDirectory)` — returns the executable path and asset references; throws on missing data.

- [ ] **Step 1: Add unconditional direct client build**

```powershell
function Invoke-ClientBuild {
    param([Parameter(Mandatory)][string] $ClientAppDirectory)

    & npm ci --prefix $ClientAppDirectory
    if ($LASTEXITCODE -ne 0) {
        throw "npm ci failed with exit code $LASTEXITCODE."
    }

    & npm run build --prefix $ClientAppDirectory
    if ($LASTEXITCODE -ne 0) {
        throw "npm run build failed with exit code $LASTEXITCODE."
    }
}
```

There is deliberately no `node_modules` freshness shortcut.

- [ ] **Step 2: Add repository-root scratchpad directory creation**

Resolve the root through Git using the script/project directory, not the caller’s working directory:

```powershell
function New-PublishRunDirectory {
    param([Parameter(Mandatory)][string] $RepositoryRoot)

    $runName = 'run-{0}-{1}' -f [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss'), $PID
    $directory = Join-Path $RepositoryRoot '.claude' 'scratchpad' 'lmstreaming-standalone-publish' $runName
    [System.IO.Directory]::CreateDirectory($directory) | Out-Null
    return $directory
}
```

If the repository’s PowerShell version does not support multi-child `Join-Path`, use nested `Join-Path` calls while preserving the exact path.

- [ ] **Step 3: Add explicit publish invocation**

```powershell
function Invoke-ApplicationPublish {
    param(
        [Parameter(Mandatory)][string] $ProjectFile,
        [Parameter(Mandatory)][string] $Configuration,
        [Parameter(Mandatory)][string] $OutputDirectory
    )

    & dotnet publish $ProjectFile -c $Configuration -o $OutputDirectory '-p:BuildClientApp=false'
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE."
    }
}
```

Do not add `--no-build`; publish must build the server and create a complete artifact itself.

- [ ] **Step 4: Parse all local JS/CSS references from built HTML**

Use an HTML-reference regex that accepts Vite’s leading `/dist/` path and query strings, rejects remote URLs, normalizes to app-relative paths, and returns every distinct `.js` and `.css` reference. Fail unless at least one JavaScript reference exists. CSS validation applies to every referenced CSS file; no fixed count or filename is assumed.

Representative shape:

```powershell
function Get-PublishedAssetReferences {
    param([Parameter(Mandatory)][string] $IndexPath)

    $html = [System.IO.File]::ReadAllText($IndexPath)
    $matches = [regex]::Matches($html, '(?:src|href)=["''](?<path>/dist/[^"''?#]+\.(?:js|css))(?:[?#][^"'']*)?["'']')
    $assets = @($matches | ForEach-Object { $_.Groups['path'].Value } | Sort-Object -Unique)

    if (-not ($assets | Where-Object { $_ -match '\.js$' })) {
        throw "No JavaScript asset reference was found in $IndexPath."
    }

    return $assets
}
```

- [ ] **Step 5: Validate the full publish artifact before launch**

Check:

```text
LmStreaming.Sample.exe
wwwroot/dist/index.html
every local /dist/*.js or /dist/*.css reference extracted from index.html
```

Convert `/dist/assets/x.js` to `<publish>/wwwroot/dist/assets/x.js` by trimming the leading slash and joining it beneath `wwwroot`. Reject a resolved path that escapes `wwwroot`.

Return:

```powershell
[pscustomobject]@{
    ExecutablePath = $executablePath
    IndexPath = $indexPath
    AssetPaths = $assetReferences
}
```

- [ ] **Step 6: Exercise helper failure paths before integration**

Temporarily call the validator against an empty fresh scratchpad directory from a copied/in-memory helper context, or invoke the script’s validation phase before publish if helpers are structured for dot-sourcing. Expected RED: a focused `LmStreaming.Sample.exe` or `wwwroot/dist/index.html` missing error. Remove any temporary invocation after observing RED.

---

### Task 4: Implement the standalone launch and readiness contract

**Files:**
- Modify: `samples/LmStreaming.Sample/publish-launch.ps1:431-616`

**Interfaces:**
- Consumes: helpers from Task 3 and existing backend port/process helpers.
- Produces: a single supervised published Production process and `.run-state.json` inside its publish directory.

- [ ] **Step 1: Assemble the pipeline in strict order**

```powershell
$repositoryRoot = (& git -C $ProjectDir rev-parse --show-toplevel).Trim()
if ($LASTEXITCODE -ne 0 -or -not $repositoryRoot) {
    throw 'Could not resolve the repository root.'
}

$publishDirectory = New-PublishRunDirectory -RepositoryRoot $repositoryRoot
Invoke-ClientBuild -ClientAppDirectory $ClientAppDir
Invoke-ApplicationPublish -ProjectFile $ProjectFile -Configuration $Configuration -OutputDirectory $publishDirectory
$artifact = Confirm-PublishArtifact -PublishDirectory $publishDirectory
```

The fresh directory is created before npm starts so a failed phase still has a reported inspection location.

- [ ] **Step 2: Launch only the published executable in Production**

Avoid mutating the parent session permanently. Set process-specific environment through `ProcessStartInfo.Environment` (or save and restore parent variables in `finally`). Required child values:

```text
ASPNETCORE_ENVIRONMENT=Production
DOTNET_ENVIRONMENT=Production
```

Required arguments:

```powershell
@(
    '--urls', "http://0.0.0.0:$($backend.Port)",
    '--SandboxGateway:WorkspaceBasePath=',
    "--Auth:Webhook:PublicBaseUrl=$WebhookBaseUrl"
)
```

Set `WorkingDirectory` to `$publishDirectory`. The executable path must equal `$artifact.ExecutablePath` and reside inside `$publishDirectory`.

- [ ] **Step 3: Make readiness checks fatal and concrete**

First poll `http://localhost:<port>/api/providers` until it returns a success status or the fixed timeout expires/process exits. Then request:

```text
/dist/index.html
every JS/CSS reference returned by Get-PublishedAssetReferences
```

For each request, require HTTP 2xx. Throw with the URI and status/error on failure. Do not convert failures to warnings.

- [ ] **Step 4: Write publish-scoped run state**

Write `<publishDirectory>/.run-state.json` after all readiness checks pass. Include:

```text
startedAtUtc
checkout
branch
commit
configuration
publishDirectory
backend.preferredPort
backend.port
backend.portAutoSelected
backend.pid
backend.url
webhookBaseUrl
```

Do not include Vite port, PID, URL, or process fields. State explicitly that the app is running from the published artifact.

- [ ] **Step 5: Report ready state and supervise lifetime**

Print the exact publish directory, ASP.NET Core URL, PID, branch/commit, and webhook base URL. Then call `WaitForExit()` on the published process. In `finally`, stop only the owned published process tree. Keep the artifact directory on success and failure.

---

### Task 5: Demonstrate the complete scratchpad publish flow

**Files:**
- Execute: `samples/LmStreaming.Sample/publish-launch.ps1`
- Inspect: `.claude/scratchpad/lmstreaming-standalone-publish/run-*/`
- Update: `.claude/scratchpad/conversation_memories/lmstreaming-standalone-publish/implementation.md`

**Interfaces:**
- Consumes: completed launcher.
- Produces: exact GREEN evidence for artifact, HTTP behavior, process ancestry, and absence of a launcher-created Vite listener.

- [ ] **Step 1: Start a fresh demonstration run**

Use a free explicit port and `Release`:

```powershell
pwsh -NoProfile -File 'samples/LmStreaming.Sample/publish-launch.ps1' -Port 15050 -Configuration Release -Force
```

Expected phases in order:

```text
npm ci
npm run build
dotnet publish
artifact validation
published executable launch
/api/providers readiness
/dist/index.html readiness
referenced JS/CSS readiness
Ready
```

- [ ] **Step 2: Verify the artifact from another terminal/process**

Resolve the newest run directory by exact name reported by the launcher, not a mutable wildcard assumption. Check:

```powershell
Test-Path "$runDirectory/LmStreaming.Sample.exe"
Test-Path "$runDirectory/wwwroot/dist/index.html"
Get-ChildItem "$runDirectory/wwwroot/dist/assets" -File
Get-Content "$runDirectory/.run-state.json" -Raw | ConvertFrom-Json
```

Expected: executable and HTML are present; referenced JavaScript and any referenced CSS are present; run state points to the same directory and contains no Vite fields.

- [ ] **Step 3: Verify HTTP responses independently**

```powershell
Invoke-WebRequest 'http://localhost:15050/api/providers' -UseBasicParsing
Invoke-WebRequest 'http://localhost:15050/dist/index.html' -UseBasicParsing
```

Extract the references from the returned/published HTML and request each from `http://localhost:15050`. Expected: every request returns 2xx from the ASP.NET Core port.

- [ ] **Step 4: Prove the launcher did not spawn Vite**

Do not assert that the machine has no global Node process. Instead:

1. Inspect the published process command line and parent/child process tree.
2. Confirm the launcher’s only long-lived child is the published `LmStreaming.Sample.exe`.
3. Confirm the launcher output and run state contain no Vite port or Vite PID.
4. Confirm the script has no `npm run dev`, Vite `Start-Process`, or Vite port path after the short-lived `npm ci`/`npm run build` commands have completed.
5. Confirm all browser assets succeed through the ASP.NET Core port without starting another process.

- [ ] **Step 5: Stop cleanly and record evidence**

Press Ctrl+C. Confirm the owned published process exits and the scratchpad artifact remains. Record:

```text
run directory
publish command outcome
published executable path/PID
HTTP status for API, HTML, each referenced asset
process-tree observation
shutdown outcome
```

in `implementation.md`.

---

### Task 6: Run focused regression checks and documentation alignment

**Files:**
- Test: `samples/LmStreaming.Sample/publish-launch.ps1`
- Potentially modify: `samples/LmStreaming.Sample/README.md`

**Interfaces:**
- Consumes: verified standalone launcher.
- Produces: final syntax, help, build, and documentation proof.

- [ ] **Step 1: Parse and help-check the PowerShell script**

```powershell
$errors = $null
[System.Management.Automation.Language.Parser]::ParseFile(
    (Resolve-Path 'samples/LmStreaming.Sample/publish-launch.ps1'),
    [ref] $null,
    [ref] $errors
) | Out-Null
if ($errors.Count) { $errors | Format-List; throw 'PowerShell parse failed.' }

Get-Help 'samples/LmStreaming.Sample/publish-launch.ps1' -Full
```

Expected: zero parser errors; help contains no Vite runtime options.

- [ ] **Step 2: Exercise retained port behavior**

Verify an omitted busy preferred port auto-selects a free backend port, an explicit busy port fails without `-Force`, and `-Force` preserves its documented behavior. Do not involve a second Vite port.

- [ ] **Step 3: Run the focused project build**

```powershell
dotnet build 'samples/LmStreaming.Sample/LmStreaming.Sample.csproj' -c Release -p:BuildClientApp=false
```

Expected: build succeeds. This is a regression check only; the demonstration itself must still be created entirely by `publish-launch.ps1`.

- [ ] **Step 4: Align README only if currently inaccurate**

If the README describes `publish-launch.ps1` as a Vite development launcher, update that existing section to show:

```powershell
./publish-launch.ps1 -Configuration Release
```

State that it runs `npm ci`, builds Vite, publishes under `.claude/scratchpad`, launches the Production executable, and retains the artifact. Do not add an alternate launcher.

- [ ] **Step 5: Review the final diff**

```powershell
git diff --check
git diff -- samples/LmStreaming.Sample/publish-launch.ps1 samples/LmStreaming.Sample/README.md docs/superpowers/specs/2026-08-10-lmstreaming-standalone-publish-design.md docs/superpowers/plans/2026-08-10-lmstreaming-standalone-publish.md
```

Expected: no whitespace errors, no generated publish files in the diff, and no unrelated modifications.
