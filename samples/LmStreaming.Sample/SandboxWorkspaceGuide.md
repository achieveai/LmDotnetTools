# Sandbox Workspace Guide — "Workspace Agent" mode

This guide covers the **Workspace Agent** chat mode in the `LmStreaming.Sample` app. In this
mode the LLM operates inside an isolated sandbox: its file/command tools are executed by an
external Rust MCP gateway (the `SandboxedOstoolsMcpServer` project), scoped to one configured
host directory.

## Overview

The sample can run an LLM that works inside a **sandboxed workspace**. The following tools are
executed by the gateway, not by the host process:

- **File operations** — `Read`, `Write`, `Edit`, `Glob`, `Grep`
- **Shell** — `Bash`, `PowerShell`
- **`Skill`** — runs a documented procedure from a configured skills directory

Every tool runs scoped to a single configured host directory, with **default-deny network
egress**.

### Two provider paths

There are two ways to drive this mode, with different isolation guarantees:

- **Middleware providers (Anthropic / OpenAI) — the true-sandbox showcase.**
  Every tool call goes through the gateway; no native tools compete for the same operations.
  This path requires an API key (`ANTHROPIC_API_KEY` or `OPENAI_API_KEY`).
  **Use this path when you want to demonstrate real, enforced isolation.**

- **Copilot — the no-API-key convenience path.**
  Uses a `gh`/Copilot token instead of an API key. Caveat: the Copilot CLI ships its **own**
  native file/`Bash` tools that run on the host *outside* the gateway, so its isolation is
  best-effort / prompt-steered, **not enforced**. Prefer the middleware path whenever you are
  demonstrating isolation; use Copilot only for a quick, key-free try.

## Prerequisites

- **.NET 9 SDK** (the sample targets `net9.0`).
- **Node 20** (for the Vite client in `ClientApp/`).
- **The gateway, built once.** In `B:\sources\SandboxedOstoolsMcpServer` run:

  ```bash
  cargo build --release -p mcp-gateway -p agent-cli
  ```

  This produces:

  ```text
  target\release\mcp-gateway.exe
  target\release\agent-cli.exe
  ```

### Optional hardening (not required)

The gateway can additionally confine the `PowerShell` tool inside a Windows **AppContainer**.
That is a one-time, admin-only setup via `scripts\install-windows-sandbox.ps1` in the gateway
repo. **You do not need it for a first run:** the sample spawns the gateway with AppContainer
confinement explicitly disabled (`LOCAL_SANDBOX_APPCONTAINER=false`) so the demo works
frictionlessly out of the box.

## Configuration

Workspace Agent mode is configured through the **`SandboxGateway`** section of
`appsettings.json` / `appsettings.Development.json`. The fields map to
`Services/SandboxGatewayOptions.cs`:

| Field | Default | Purpose |
| --- | --- | --- |
| `BaseUrl` | `http://localhost:3000` | Base URL of the gateway the app connects to. |
| `Workspace` | — | Workspace path **relative to `WorkspaceBasePath`**, mounted as the sandbox workspace. |
| `WorkspaceBasePath` | — | Host base directory the gateway resolves the workspace under (becomes `WORKSPACE_BASE_PATH` when the gateway is spawned). |
| `WorkspacePath` | — | **Optional absolute path** to the workspace dir. When set it **overrides** `WorkspaceBasePath` + `Workspace`: the app uses its parent as `WORKSPACE_BASE_PATH` and its folder name as the workspace, and creates it if missing. The simplest way to point the sandbox at any folder (see below). |
| `AppId` | `lmstreaming-sample` | App id sent in the sandbox-create request and, under gateway auth enforcement, as the `X-Sbx-App-Id` bearer header. |
| `AppKey` | — | Base64 per-app secret (`openssl rand -base64 32`). When set it is sent as `X-Sbx-App-Key` on every gateway request so an `AUTH_ENFORCE` gateway can authenticate this app (see [Gateway authentication](#gateway-authentication)). Leave empty for an unenforced gateway. **SECRET — do not commit a real value.** |
| `AutoSpawn` | `true` | If a healthy gateway is already running at `BaseUrl`, adopt it; otherwise spawn `GatewayExePath`. |
| `GatewayExePath` | — | Absolute path to the built `mcp-gateway.exe`, used for auto-spawn. |
| `AgentCliPath` | — | Absolute path to the built `agent-cli.exe` (becomes `LOCAL_AGENT_CLI_PATH` when the gateway is spawned). |
| `SkillsDir` | — | Directory whose `<skill>/SKILL.md` files become the gateway's `Skill` tool (becomes `SKILLS_DIRS`). The repo ships `sandbox-skills/repo-explorer` and `sandbox-skills/workspace-summary`. |
| `PluginsDirs` | — | One or more comma-separated `alias=path` entries pointing at Claude-plugin **marketplace** directories (becomes `PLUGINS_DIRS`). The gateway loads each plugin's skills + `.mcp.json` MCP servers. See [Plugins](#plugins-claude-plugin-marketplaces). |

Example (`appsettings.Development.json`):

```json
{
  "SandboxGateway": {
    "BaseUrl": "http://localhost:3000",
    "Workspace": "demo",
    "WorkspaceBasePath": "B:\\sandbox-workspaces",
    "AppId": "lmstreaming-sample",
    "AutoSpawn": true,
    "GatewayExePath": "B:\\sources\\SandboxedOstoolsMcpServer\\target\\release\\mcp-gateway.exe",
    "AgentCliPath": "B:\\sources\\SandboxedOstoolsMcpServer\\target\\release\\agent-cli.exe",
    "SkillsDir": "B:\\sources\\LmDotnetTools\\samples\\LmStreaming.Sample\\sandbox-skills",
    "PluginsDirs": "claude_plugins=B:\\sources\\claude_plugins"
  }
}
```

With the example above, the mounted workspace is `WorkspaceBasePath` + `Workspace` =
`B:\sandbox-workspaces\demo` (the app **creates the directory if it does not exist**).
Point `Workspace`/`WorkspaceBasePath` at a **dedicated** folder rather than a real source repo —
the local backend runs unsandboxed, so the agent has read/write access to whatever you mount.

### Gateway authentication

Recent gateways enforce **per-app bearer authentication** (gateway ADR 0029). When `AUTH_ENFORCE` is on
(the gateway's default), every request must carry `X-Sbx-App-Id` + `X-Sbx-App-Key`; the gateway binds the
app identity from the credential and scopes each sandbox session to that app. This sample sends those
headers automatically on **all** gateway calls (REST + MCP) whenever `AppKey` is set — set both `AppId` and
`AppKey`, or neither.

To run against an enforcing gateway:

1. Generate a secret: `openssl rand -base64 32`.
2. Register it on the **gateway** under this app's id — e.g. `APP_SECRETS=lmstreaming-sample=<base64>` (or an
   `APP_SECRETS_FILE` JSON `{ "lmstreaming-sample": "<base64>" }`).
3. Set the same value on this app: `SandboxGateway:AppKey` in config, or the `SandboxGateway__AppKey` env var
   (keep the real secret out of committed files).

Leave `AppKey` empty to talk to an `AUTH_ENFORCE=off` gateway — the sample then sends no bearer headers, exactly
as before. (The gateway↔egress-proxy `PROXY_SERVICE_CRED` and its "deploy the proxy first" ordering are
operator concerns configured on the gateway side, not here.)

### Use any folder as the workspace

To point the sandbox at **any** absolute folder at startup, set **`WorkspacePath`** instead of
splitting it into base + leaf:

```json
"SandboxGateway": { "WorkspacePath": "B:\\sandbox-workspaces\\my-project" }
```

The app creates the directory if it doesn't exist, spawns the gateway with the **parent**
(`B:\sandbox-workspaces`) as `WORKSPACE_BASE_PATH`, and uses the **folder name** (`my-project`) as
the workspace. You can set it without editing config too — both bind to the same option:

- env var: `SandboxGateway__WorkspacePath=B:\sandbox-workspaces\my-project`
- command line: `dotnet run --project samples/LmStreaming.Sample -- --SandboxGateway:WorkspacePath="B:\sandbox-workspaces\my-project"`

> **Heads-up — unsandboxed access:** the local backend runs the agent **unsandboxed**, so it gets
> read/write to whatever `WorkspacePath` points at. You *can* point it at an existing source repo,
> but prefer a **dedicated** folder (or a throwaway clone) so the agent can't modify a repo you care
> about — consistent with the dedicated-folder note above.

> **Caveat:** `WorkspacePath` only takes effect when the app **spawns** the gateway. If a gateway is
> already running on `:3000`, the app *adopts* it and keeps that gateway's `WORKSPACE_BASE_PATH` — so
> stop the running gateway first (the workspace must live under whatever base the live gateway uses).

> **Startup is non-fatal.** If the gateway is not configured or not reachable, the app still
> boots and runs all other chat modes. The error only surfaces when **Workspace Agent mode is
> actually used** (i.e. when the first sandbox session is created).

## Skills (`SkillsDir`)

A **skill** is a folder under `SkillsDir` containing a `SKILL.md` with YAML frontmatter
(`name`, `description`) followed by a markdown procedure. The gateway parses each one at startup
and exposes the `Skill` tool; the model invokes it as `Skill { skill: "<name>" }`. The
`description` is what the model matches on, so make it state *when* to use the skill.

The repo ships two:

- **`repo-explorer`** — maps a code repository into a structured JSON manifest.
- **`workspace-summary`** — inventories any workspace (file counts/sizes by extension,
  largest/recent files, top-level layout).

Add your own by creating `<SkillsDir>/<my-skill>/SKILL.md`. Skills are read when the **gateway
starts**, so restart the backend (which respawns the gateway) after adding or editing one.

## Plugins (Claude-plugin marketplaces)

`PluginsDirs` points the gateway at one or more **Claude-plugin marketplace** directories using
comma-separated `alias=path` entries (e.g. `claude_plugins=B:\sources\claude_plugins`). For each
directory the gateway:

1. reads a catalog — `marketplace.json` or `.claude-plugin/marketplace.json` — if present; or
2. otherwise scans immediate subdirectories, treating any that contain
   `.claude-plugin/plugin.json`, `.mcp.json`, `SKILL.md`, or a `skills/` subdir as a plugin.

Each discovered plugin's **skills** (its `skills/<name>/SKILL.md`) and **MCP servers** (its
`.mcp.json`) are surfaced to the sandbox. Multiple marketplaces:
`PluginsDirs = "a=B:\\one,b=B:\\two"`. Startup logs `Discovered plugins in marketplace, alias=…`.

> **`.mcp.json` strictness.** The gateway requires each server entry to declare a `type`. Plugins
> whose `.mcp.json` omits it are loaded **skills-only** with a warning
> (`Failed to parse .mcp.json — using empty mcp_servers`); their skills still work.

### Discovered sub-agent model and reasoning options

Markdown sub-agents may optionally add either or both of these frontmatter fields:

```yaml
---
name: architecture-review
modelintelligence: 4
effort: high
---
```

- **`modelintelligence`** is an integer capability tier from **0 through 6**. Tiers express
  ascending deployment-defined model capability, not a built-in model table. Configure each
  tier as an ordered array of candidate model ids:

  ```json
  "SubAgentIntelligence": {
    "Tiers": {
      "3": ["first-choice-model", "fallback-model"]
    }
  }
  ```

  The first candidate present and routable in the host's discovered Copilot model catalog wins;
  candidates later in the array are ordered fallbacks. An absent map, missing tier, or tier with
  no routable candidate falls through to the parent model and logs a warning. The checked-in
  `appsettings.json` contains empty arrays for tiers 0–6, so it documents the schema without
  selecting a model. Malformed or out-of-range configuration keys are logged as errors and their
  mappings are ignored without preventing application startup.

- **`effort`** accepts **`low`**, **`medium`**, **`high`**, **`extra-high`**, or **`xhigh`**
  (`extra-high` and `xhigh` are equivalent). The request is clamped to the highest selectable effort
  advertised by the resolved model at or below the request. If every advertised selectable
  effort is above the request, it snaps to the model's lowest selectable effort. If the model
  advertises no selectable effort (including a non-adaptive model), or its provider transport
  cannot shape effort, the setting is omitted. LmStreaming uses the provider-specific request
  shape: Anthropic `output_config.effort` or OpenAI Responses `reasoning.effort`.

Model precedence is **explicit `model:` > tier-resolved model > inherited parent model** (a
per-spawn model override, when supplied by a caller, remains highest). An explicit model therefore
disables tier selection. The characteristics-aware factory builds the sub-agent on the resolved
model's routable transport and applies effort only after that model is known.
The 0–6 model-intelligence tier space and the reasoning-effort values are deliberately independent;
there is no ordinal or one-to-one mapping between them.

Invalid or out-of-range `modelintelligence` values and unrecognized `effort` values do not reject
the sub-agent. The shared parser ignores only the invalid field and emits a diagnostic, which the
host logs as a warning.

`CodeReviewDaemon.Sample` deliberately has **no model/effort resolution in this change**. It uses
the shared parser and logs its diagnostics, but does not install the characteristics-aware factory:
`modelintelligence` never selects a daemon model and `effort` never adds reasoning request metadata.
Existing explicit `model:` handling remains unchanged.

## Running

The backend auto-launches the Vite client (via `Vite.AspNetCore`) **and** auto-spawns the gateway
— but **only in the Development environment**, which is also where the gateway/workspace/plugins
config above lives (`appsettings.Development.json`). There is no `launchSettings.json`, so set the
environment explicitly:

1. **Start everything with one command** (API + `/ws` on `http://localhost:5000`, Vite on
   `:5173`, and the auto-spawned gateway on `:3000`):

   ```powershell
   $env:ASPNETCORE_ENVIRONMENT = "Development"
   dotnet run --project samples/LmStreaming.Sample
   ```

   On boot the app adopts a healthy gateway already listening at `BaseUrl`, or spawns
   `GatewayExePath` (local/Windows backend). Open **http://localhost:5000**.

2. **In the UI:**
   - Pick a **provider** — Anthropic or OpenAI for the true-sandbox demo, or Copilot for the
     key-free path.
   - Select the **Workspace Agent** mode.
   - Start chatting (e.g. *"Use the workspace-summary skill to inventory this workspace."*).

## How it works (brief)

- On first use of Workspace Agent mode the app POSTs to
  `{gateway}/api/v1/sandboxes` with a body like:

  ```json
  { "app": { "id": "lmstreaming-sample" }, "workspace": "LmDotnetTools" }
  ```

  and gets back a `session_id`.

- Tool calls are sent to `{gateway}/mcp` (JSON-RPC) with an `X-Session-ID` header. The gateway
  runs each tool inside the sandbox bound to that session.

### Important path model (local/Windows backend)

There is **no `/workspace` path** in the local backend. Instead:

- The **shell tools** (`Bash`, `PowerShell`) **start in the workspace directory**, so relative
  paths work there.
- The **file tools** (`Read`, `Write`, `Edit`, `Glob`, `Grep`) require **absolute host paths**
  under the workspace.

To make the model use the right base path, the app injects the workspace's **absolute host
path** into the system prompt at runtime (the gateway reports it when the session is created).

### Session lifetime

The sandbox session is **app-wide** — shared across all tabs and conversations — and is
**deleted when the app shuts down**.

## Try it (manual test prompts)

Paste these into the chat after selecting a provider and the **Workspace Agent** mode:

1. **List + summarize**
   > List the files in the workspace and summarize the README.

   *Expect:* `Glob`/`Read`/`Bash` tool-call pills and a real file listing of the workspace.

2. **Write + read back**
   > Create `notes.md` containing "hello" and read it back.

   *Expect:* a `Write` followed by a `Read`; the file actually appears in the host workspace
   directory.

3. **Run a skill**
   > Use the repo-explorer skill to map this project.

   *Expect:* a `Skill` tool call that produces a structured JSON manifest (languages, entry
   points, build/test commands, and a one-line purpose per top-level directory).

4. **Isolation — refused egress / path escape** *(middleware path)*
   > Read `C:\Windows\win.ini`.

   *Expect:* the tool refuses with "path is outside the sandbox" — the file is outside the
   mounted workspace.

5. **Shell in the workspace**
   > Run `ls` (or `Get-ChildItem`) and tell me how many files are in the workspace root.

   *Expect:* a `Bash`/`PowerShell` pill; relative paths resolve because the shell starts in the
   workspace directory.

## Troubleshooting

- **Gateway won't start.**
  Ensure `cargo build --release` ran successfully and that `GatewayExePath` / `AgentCliPath`
  point at the built executables. Check the app logs for the spawn error (the gateway's
  stdout/stderr is mirrored into the app log).

- **"Workspace Agent mode supports the OpenAI/Anthropic (and Copilot) providers…"**
  You selected Workspace Agent mode with a provider that is not wired for the sandbox (e.g.
  codex, claude, or a mock provider). Switch to Anthropic, OpenAI, or Copilot.

- **Tools say "path is outside the sandbox" for relative file-tool paths.**
  This is **expected** for the file tools in the local backend. Use the **absolute workspace
  path** that the system prompt gives you as the base for `Read`/`Write`/`Edit`/`Glob`/`Grep`.
  (The shell tools, by contrast, already start in the workspace and accept relative paths.)
