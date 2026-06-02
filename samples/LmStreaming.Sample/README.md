# LmStreaming.Sample

A full-stack streaming-chat demo: an ASP.NET Core backend (`/ws` WebSocket + REST API) with an
**auto-launched** Vue 3 + Vite client, driving `MultiTurnAgentLoop` across multiple LLM providers.
It also showcases the **SandboxedOstools MCP gateway** — a *Workspace Agent* mode where the LLM's
file / shell / search tools run inside an isolated sandbox — plus optional GitHub / Azure DevOps
token injection.

## Quick start

```powershell
# ONE command brings up: API + /ws (:5000), the Vite client, and (in Development) the gateway (:3000)
$env:ASPNETCORE_ENVIRONMENT = "Development"
dotnet run --project samples/LmStreaming.Sample
# then open http://localhost:5000
```

> ⚠️ The backend auto-launches the Vite dev server (via `Vite.AspNetCore`) **and** auto-spawns the
> sandbox gateway — **but only in the `Development` environment**, which is also where the gateway,
> workspace, skill, and plugin config lives (`appsettings.Development.json`). There is **no**
> `launchSettings.json`, so `dotnet run` defaults to *Production*; you must set
> `ASPNETCORE_ENVIRONMENT=Development` yourself or neither the SPA nor the gateway will start.

The provider is chosen in the UI (header dropdown). Copilot-backed providers need a resolvable
`gh`/Copilot token (no API key); Anthropic/OpenAI providers need `ANTHROPIC_API_KEY` / `OPENAI_API_KEY`.

## Sandbox Workspace Agent

The **Workspace Agent** chat mode routes every file/shell/search tool call through the Rust
[SandboxedOstools MCP gateway](https://github.com/) (`mcp-gateway.exe`), which confines the LLM to
a configured workspace directory. Two delivery paths:

- **Anthropic / OpenAI (middleware path) — the true-sandbox showcase.** Every tool flows through
  the gateway; nothing runs natively on the host.
- **Copilot (default, no API key) — convenience path.** The Copilot CLI also has *native* host
  tools that bypass the gateway, so its isolation is best-effort / prompt-steered.

All sandbox behavior is configured in the **`SandboxGateway`** section of
`appsettings.Development.json` (bound to `Services/SandboxGatewayOptions.cs`). Full reference:
**[SandboxWorkspaceGuide.md](SandboxWorkspaceGuide.md)**.

### 1. Point at your gateway installation

Build the gateway once, then point the app at the produced executables:

```powershell
# in the gateway repo (e.g. B:\sources\SandboxedOstoolsMcpServer)
cargo build --release    # -> target\release\mcp-gateway.exe  AND  agent-cli.exe
```

```jsonc
"SandboxGateway": {
  "GatewayExePath": "B:\\sources\\SandboxedOstoolsMcpServer\\target\\release\\mcp-gateway.exe",
  "AgentCliPath":   "B:\\sources\\SandboxedOstoolsMcpServer\\target\\release\\agent-cli.exe",
  "BaseUrl":        "http://localhost:3000",
  "AutoSpawn":      true   // adopt a gateway already healthy at BaseUrl, else spawn GatewayExePath
}
```

If a gateway is already running at `BaseUrl` the app **adopts** it; otherwise it **spawns** the exe
above (and kills it on shutdown). Startup is non-fatal — the app still runs other modes if the
gateway is missing; the error surfaces only when Workspace Agent mode is first used.

### 2. Set the workspace directory

The mounted workspace is `WorkspaceBasePath` + `Workspace`. The gateway **creates it if missing**.

```jsonc
"WorkspaceBasePath": "B:\\sandbox-workspaces",
"Workspace": "demo"      // -> mounts B:\sandbox-workspaces\demo
```

> Point this at a **dedicated** folder, not a real source repo — the local/Windows backend runs
> unsandboxed, so the agent gets read/write over whatever you mount. Override per-run without
> editing the file: `$env:SandboxGateway__WorkspaceBasePath="B:\ws"; $env:SandboxGateway__Workspace="demo"`.

### 3. Skills (`SkillsDir`)

A skill is `<SkillsDir>/<name>/SKILL.md` — YAML frontmatter (`name`, `description`) + a markdown
procedure. The gateway exposes them via the `Skill` tool (`Skill { skill: "<name>" }`); the
`description` is what the model matches on. The repo ships **`repo-explorer`** (code-repo manifest)
and **`workspace-summary`** (generic workspace inventory) under `sandbox-skills/`. Add your own by
creating a new `<name>/SKILL.md`. Skills load at gateway start — **restart the backend** after edits.

```jsonc
"SkillsDir": "B:\\sources\\LmDotnetTools\\samples\\LmStreaming.Sample\\sandbox-skills"
```

### 4. Plugin (marketplace) directories — `PluginsDirs`

Load whole **Claude-plugin marketplaces** with comma-separated `alias=path` entries. The gateway
reads each directory's `.claude-plugin/marketplace.json` catalog (or scans subdirs for
`.claude-plugin/plugin.json` / `.mcp.json` / `SKILL.md` / `skills/`) and surfaces each plugin's
**skills** and **`.mcp.json` MCP servers** to the sandbox.

```jsonc
"PluginsDirs": "claude_plugins=B:\\sources\\claude_plugins"
// multiple: "a=B:\\one,b=B:\\two"
```

Startup logs `Discovered plugins in marketplace, alias=claude_plugins, plugins=N`. Plugins whose
`.mcp.json` omits the required `type` field load **skills-only** (warning, non-fatal).

### 5. Try it

In the UI: pick **Anthropic/OpenAI** (true sandbox) or **Copilot** (key-free), select **Workspace
Agent** mode, then e.g.:

- *"Use the workspace-summary skill to inventory this workspace."*
- *"Create notes.md with today's plan and read it back."* (the file appears in your mounted workspace)

Verify the stack is up:

```powershell
Invoke-RestMethod http://localhost:5000/api/chat-modes | Select-Object -First 5   # app ready
Invoke-RestMethod http://127.0.0.1:3000/health                                    # gateway ready
```

> **Path model (local/Windows backend):** there is **no** `/workspace` mount. Shell tools (`Bash`,
> `PowerShell`) start *in* the workspace (relative paths are fine); file tools (`Read`, `Write`,
> `Edit`, `Glob`, `Grep`) need **absolute** host paths, and the gateway enforces
> **read-before-write** (a path must be `Read` — even a missing one — before `Write`/`Edit`). The
> Workspace Agent system prompt already instructs the model accordingly.

### GitHub / Azure DevOps token injection (optional)

Workspace egress to `api.github.com` / `dev.azure.com` can be authenticated by injecting
refreshed OAuth tokens the sandbox never sees. See **[AuthProviderGuide.md](AuthProviderGuide.md)**.

## Session Recording

In development, open the app with `?record=1` (or `?record=true`) to enable server-side recording for that WebSocket session:

- WebSocket stream messages are written as JSONL to:
  - `samples/LmStreaming.Sample/recordings/<threadId>_<timestamp>.ws.jsonl`
- LLM provider request/response dumps are written to:
  - `samples/LmStreaming.Sample/recordings/<threadId>_<timestamp>.llm.request.txt`
  - `samples/LmStreaming.Sample/recordings/<threadId>_<timestamp>.llm.response.txt`
- Codex App Server JSON-RPC traces (when provider mode is `codex`) are written to:
  - `samples/LmStreaming.Sample/recordings/<threadId>_<timestamp>.llm.codex.rpc.jsonl`

This works for multi-turn runs and records provider calls for whichever provider mode is active (`openai`, `anthropic`, `test-anthropic`, `codex`).

## Mock-backed CLI providers (`*-mock`)

The sample boots an in-process [MockProviderHost](../MockProviderHost/) on an ephemeral
loopback port at startup so the chat client can demo all three CLI agents end-to-end without
upstream API keys:

| Provider id     | CLI required | Notes                                          |
|-----------------|--------------|------------------------------------------------|
| `claude-mock`   | `claude`     | Points the Claude Agent SDK at the mock host. |
| `codex-mock`    | (none)       | Codex spawns its own app-server stdio child.  |
| `copilot-mock`  | `copilot`    | Disables Copilot's model-allowlist probe.     |

Availability gating combines the existing CLI-on-PATH probe with the runtime mock-host
status — if the mock host fails to bind a port at startup, all three `*-mock` entries report
unavailable in `GET /api/providers` and disappear from the dropdown.

### Customising the scripted scenario

The mock host loads a JSON scenario document at startup. The default scenario is the
embedded `demo` scenario shipped with `MockProviderHost.csproj`; override it via:

```bash
# built-in name
LM_MOCK_SCENARIO=demo dotnet run --project samples/LmStreaming.Sample

# custom file path
LM_MOCK_SCENARIO=/path/to/my-scenario.json dotnet run --project samples/LmStreaming.Sample
```

Schema (see `samples/MockProviderHost/scenarios/demo.json` for a full example):

```json
{
  "roles": [
    {
      "key": "demo",
      "match": { "type": "always" },
      "turns": [
        { "messages": [{ "kind": "text", "text": "Hi!" }] },
        { "thinking": 64, "messages": [
            { "kind": "text", "text": "..." },
            { "kind": "tool_call", "name": "echo", "args": { "msg": "hi" } }
        ] }
      ]
    }
  ]
}
```

Match types: `always`, `system_contains` (with `value`), `user_contains` (with `value`),
`tool` (with `name`). Message kinds: `text`, `text_len` (with `wordCount`), `tool_call`
(with `name` and optional `args`).
