# CopilotLive.Tests — manual live suite (NOT run by CI)

Smoke tests that call the **real** GitHub Copilot API
(`https://api.enterprise.githubcopilot.com`) through `CopilotAnthropicAgentFactory` and
`CopilotResponsesAgentFactory`, using whatever GitHub Copilot credential you are already logged in
with.

## Mock-first default

This project is part of `LmDotnetTools.sln`, but live tests **skip unless
`RUN_COPILOT_LIVE_TESTS=1` is explicitly set**. Without that opt-in, the shared fixture does not
resolve credentials or call Copilot. With opt-in, tests also skip if no credential is found.

The regular `CopilotAnthropicProxy.Tests` suite uses a scripted mock upstream. Its
`Thinking_budgets_reach_the_mock_responses_model_with_the_mapped_effort` test checks low, medium,
and high effort through the real proxy pipeline. Mock success proves translation, not live model
compatibility.

## Prerequisites

Be logged in to GitHub Copilot via any one of these (checked in this order):

1. Env var: `GITHUB_COPILOT_TOKEN`, `GH_COPILOT_TOKEN`, `GH_TOKEN`, or `GITHUB_TOKEN`
2. GitHub Copilot CLI / editor login (`github-copilot/apps.json` or `hosts.json`)
3. `gh auth login` (`gh/hosts.json`)

## Run it

```bash
# from the repo root
RUN_COPILOT_LIVE_TESTS=1 dotnet test tests/CopilotLive.Tests

# a single test
RUN_COPILOT_LIVE_TESTS=1 dotnet test tests/CopilotLive.Tests --filter "FullyQualifiedName~Responses_websocket_returns_text"
```

See model ids and replies (xUnit captures `ITestOutputHelper`):

```bash
RUN_COPILOT_LIVE_TESTS=1 dotnet test tests/CopilotLive.Tests --logger "console;verbosity=detailed"
```

## What it covers

| Test | Endpoint / transport |
|------|----------------------|
| `Lists_available_models` | `GET /models` |
| `Messages_non_streaming_returns_assistant_text` | `POST /v1/messages` |
| `Messages_streaming_yields_text` | `POST /v1/messages` (SSE) |
| `Responses_sse_returns_text` | `POST /responses` (SSE) |
| `Responses_websocket_returns_text` | `GET /responses` (WebSocket) |
| `Responses_websocket_multi_turn_chains_previous_response` | WebSocket multi-turn (`previous_response_id`) |

## Model selection

By default the suite reads `GET /models` and picks a cheap model per family (prefers
`haiku`/`sonnet` for Claude, `nano`/`mini` for GPT). Override explicitly with:

```bash
COPILOT_ANTHROPIC_MODEL=claude-sonnet-4.5 COPILOT_OPENAI_MODEL=gpt-4.1 RUN_COPILOT_LIVE_TESTS=1 dotnet test tests/CopilotLive.Tests
```

## Troubleshooting

- **All tests skip** → no credential found. Confirm `gh auth token` prints a token, or that the
  Copilot CLI is logged in (`~/.copilot/config.json` exists).
- **`421 Misdirected Request`** → the resolved token isn't entitled for the enterprise host
  (`api.enterprise.githubcopilot.com`). The token-provider prefers the Copilot CLI's own token for
  this reason; a plain `gh` token may only work against the individual host. Set
  `GITHUB_COPILOT_TOKEN` to a Copilot-entitled token to force it.

> Note: these tests consume real Copilot quota and mimic the Copilot CLI's request headers. The token
> provider reads the Copilot CLI config (`~/.copilot/config.json`, which is JSONC), Copilot editor
> credential files, `gh`'s `hosts.yml`, and finally `gh auth token`.
