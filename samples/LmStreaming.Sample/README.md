# LmStreaming.Sample

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
