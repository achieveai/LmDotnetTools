# Codex SDK Provider — Manual Testing Guide

## Prerequisites

| Requirement | Check command | Minimum |
|---|---|---|
| .NET SDK | `dotnet --version` | 9.0+ |
| Node.js + npm | `node -v && npm -v` | Node 18+ |
| Codex CLI | `codex --version` | 0.101.0 (configurable via `CODEX_CLI_MIN_VERSION`) |
| Codex API key | `echo $CODEX_API_KEY` | Must be set in environment or `.env` |

## 1. Build and Run Automated Tests

```bash
cd worktrees/codex-sdk-provider

# Build (expect 0 errors, 0 warnings)
dotnet build LmDotnetTools.sln

# Run all tests
dotnet test LmDotnetTools.sln
```

**Key test projects for codex:**
- `tests/CodexSdkProvider.Tests/` — CodexEventParser, CodexVersionChecker, CodexJsonRpcBuilder unit tests
- `tests/LmMultiTurn.Tests/` — CodexAgentLoop integration tests (uses FakeCodexClient)

## 2. Launch the Sample App

```bash
cd samples/LmStreaming.Sample

# Install frontend dependencies (first time only)
cd ClientApp && npm install && cd ..

# Configure environment
cp .env.example .env
# Edit .env and set:
#   LM_PROVIDER_MODE=codex
#   CODEX_API_KEY=<your-key>  (or rely on env var)
#   ASPNETCORE_ENVIRONMENT=Development

# Launch
dotnet run
```

The app listens on `http://localhost:5000` by default. If that port is in use:
```bash
dotnet run --urls "http://localhost:5050"
```

**Startup indicators (console):**
- `LM Provider Mode: codex`
- `Now listening on: http://localhost:<port>`
- MCP port fallback message if default port is occupied (expected)

## 3. Manual Test Scenarios

### 3.1 Basic Text Response

**Steps:**
1. Open the app URL in a browser
2. Click **+ New Chat**
3. Type: `Say exactly: hello from codex`
4. Press Enter or click Send

**Expected:**
- User message appears on the right
- Bot response appears on the left containing "hello from codex"
- Token usage bar appears at the bottom (e.g., "Tokens: 15000 in / 10 out")
- No error banners or disconnection messages

### 3.2 Reasoning / Thinking Display

**Steps:**
1. In the same or new chat, type: `Think step by step about why the sky is blue, then give a one-sentence answer`

**Expected:**
- A collapsible "Thinking:" bubble appears before the response
- Clicking the arrow expands the reasoning content
- Final text response follows the reasoning

### 3.3 MCP Tool Calling (calculate)

**Steps:**
1. Start a new chat
2. Type: `You must use the calculate tool to compute 42 * 17. Return the result.`

**Expected:**
- A tool call card appears showing `calculate: calculate(a: 42, +2 more)` (or similar)
- Clicking the arrow on the tool card shows the full arguments and result
- The final text response contains **714**
- Token usage updates

### 3.4 MCP Tool Calling (get_weather)

**Steps:**
1. Type: `Use get_weather for Berlin. Return the temperature and condition.`

**Expected:**
- A weather card appears with an icon, city name, temperature, and condition
- The final text response references the weather data returned by the tool
- Weather data is mock (random values) — this is expected

### 3.5 Internal Tool Exposure (web_search, command_execution)

When `CODEX_EXPOSE_INTERNAL_TOOLS_AS_TOOL_MESSAGES=true` (default), Codex internal tools are surfaced as tool call/result cards.

**Steps:**
1. Start a new chat
2. Type: `Use web_search to find the official .NET 9 release date`

**Expected:**
- A web_search tool call card appears
- A tool result card follows with the search results
- The agent uses the results to compose a final answer
- If web search is disabled (`CODEX_WEB_SEARCH_MODE=disabled`), the agent may skip the tool or report it's unavailable

### 3.6 Internal Tool Suppression

**Steps:**
1. Stop the app
2. In `.env`, set `CODEX_EXPOSE_INTERNAL_TOOLS_AS_TOOL_MESSAGES=false`
3. Restart and repeat the web_search test from 3.5

**Expected:**
- No tool call/result cards appear for internal tools
- The agent still uses internal tools but they're hidden from the UI
- Only the final text response is visible

### 3.7 Multi-Turn Conversation

**Steps:**
1. Start a new chat
2. Send: `My name is Alice`
3. Wait for response
4. Send: `What is my name?`

**Expected:**
- The second response correctly references "Alice"
- Both messages appear in the conversation sidebar entry
- The thread reuses the same Codex thread (visible in server logs: `codex.persistence.save`)

### 3.8 Conversation Persistence

**Steps:**
1. Complete a conversation (send at least one message and get a response)
2. Note the conversation title in the sidebar
3. Refresh the browser page (F5)

**Expected:**
- The conversation list reloads from the sidebar
- Clicking the previous conversation restores the message history
- Sending a new message in the restored conversation continues seamlessly

### 3.9 System Prompt via Chat Modes

**Steps:**
1. Click the **Mode** dropdown in the header
2. Create a new mode with a custom system prompt, e.g.: `You are a pirate. Always respond in pirate speak.`
3. Start a new chat with this mode selected
4. Send: `What is 2+2?`

**Expected:**
- The response uses pirate-themed language
- The system prompt is passed to Codex as `developerInstructions`

### 3.10 Session Recording

**Steps:**
1. Open a new chat with `?record=1` in the WebSocket URL, e.g.:
   `http://localhost:5050/?record=1`
   (The frontend passes this query param to the WebSocket connection)
2. Send a message and wait for a response

**Expected:**
- Recording files created in `samples/LmStreaming.Sample/recordings/`:
  - `<threadId>_<timestamp>.ws.jsonl` — WebSocket message log
  - `<threadId>_<timestamp>.llm.codex.rpc.jsonl` — Codex JSON-RPC trace

## 4. Configuration Reference

All Codex settings are read from environment variables or the `.env` file. See `.env.example` for the full list.

| Variable | Default | Description |
|---|---|---|
| `LM_PROVIDER_MODE` | `test` | Set to `codex` for Codex provider |
| `CODEX_CLI_PATH` | `codex` | Path to the Codex CLI binary |
| `CODEX_CLI_MIN_VERSION` | `0.101.0` | Minimum CLI version (fail-fast if lower) |
| `CODEX_MODEL` | `gpt-5.3-codex` | Model identifier |
| `CODEX_API_KEY` | _(empty)_ | API key (falls back to env var) |
| `CODEX_BASE_URL` | _(empty)_ | Custom API base URL |
| `CODEX_APPROVAL_POLICY` | `on-request` | Tool approval policy |
| `CODEX_SANDBOX_MODE` | `workspace-write` | Sandbox filesystem access |
| `CODEX_NETWORK_ACCESS_ENABLED` | `true` | Allow network access |
| `CODEX_WEB_SEARCH_MODE` | `disabled` | `disabled`, `cached`, or `always` |
| `CODEX_TOOL_BRIDGE_MODE` | `hybrid` | `Mcp`, `Dynamic`, or `Hybrid` tool bridging |
| `CODEX_EXPOSE_INTERNAL_TOOLS_AS_TOOL_MESSAGES` | `true` | Show internal tools in UI |
| `CODEX_EMIT_LEGACY_INTERNAL_TOOL_REASONING_SUMMARIES` | `false` | Emit legacy reasoning summaries |
| `CODEX_RPC_TRACE_ENABLED` | `false` | Enable JSON-RPC trace logging |
| `CODEX_MCP_PORT` | `39200` | MCP server port (auto-fallback if occupied) |
| `CODEX_TURN_COMPLETION_TIMEOUT_MS` | `120000` | Max wait for turn completion |
| `CODEX_APP_SERVER_STARTUP_TIMEOUT_MS` | `30000` | Max wait for app server startup |

## 5. Troubleshooting

### Port already in use
```
Failed to bind to address http://127.0.0.1:5000: address already in use
```
Use `--urls` to pick a different port: `dotnet run --urls "http://localhost:5050"`

### MCP port collision
The app auto-detects port collisions and falls back to a random available port. Check the console log for the effective port:
```
Default CODEX_MCP_PORT 39200 is already in use. Falling back to port <N>.
```

### Codex CLI version too low
```
Codex CLI version X.Y.Z is below minimum required version 0.101.0
```
Upgrade the CLI or lower the minimum: `CODEX_CLI_MIN_VERSION=0.100.0`

### No API key
If `CODEX_API_KEY` is empty in `.env`, the app falls back to the `CODEX_API_KEY` environment variable. Ensure at least one is set.

### Vite dev server errors in console
`WebSocket connection to 'ws://localhost:5173...' failed` — This is the Vite HMR websocket, not related to chat functionality. Safe to ignore.

## 6. Log Analysis

Structured logs are written to `bin/Debug/net9.0/logs/lmstreaming-<date>.jsonl`.

Query with DuckDB:
```sql
-- All codex turn events for a thread
SELECT "@t" as Time, "@l" as Level, SourceContext, "@mt" as Message
FROM read_json('samples/LmStreaming.Sample/bin/Debug/net9.0/logs/lmstreaming-*.jsonl')
WHERE "@mt" LIKE '%codex.turn%'
ORDER BY "@t";

-- Tool execution logs
SELECT "@t" as Time, "@mt" as Message
FROM read_json('samples/LmStreaming.Sample/bin/Debug/net9.0/logs/lmstreaming-*.jsonl')
WHERE SourceContext LIKE '%CodexAgentLoop%'
  AND "@mt" LIKE '%tool%'
ORDER BY "@t";
```

See also: `docs/CodexAppServer-DebugQueries.sql` for more query examples.
