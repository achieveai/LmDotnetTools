# CopilotLive.Tests — manual live suite (NOT run by CI)

Smoke tests that call the **real** GitHub Copilot API
(`https://api.enterprise.githubcopilot.com`) through `CopilotAnthropicAgentFactory` and
`CopilotResponsesAgentFactory`, using whatever GitHub Copilot credential you are already logged in
with.

## Why it isn't in CI

This project is deliberately **excluded from `LmDotnetTools.sln`** and **not listed in
`scripts/ci-test.ps1`**, so the CI build never restores, compiles, or runs it. Each test is also a
`[SkippableFact]` that **skips itself** (rather than failing) when no Copilot credential is found —
so even if it were ever added to a run, it stays green without credentials.

## Prerequisites

Be logged in to GitHub Copilot via any one of these (checked in this order):

1. Env var: `GITHUB_COPILOT_TOKEN`, `GH_COPILOT_TOKEN`, `GH_TOKEN`, or `GITHUB_TOKEN`
2. GitHub Copilot CLI / editor login (`github-copilot/apps.json` or `hosts.json`)
3. `gh auth login` (`gh/hosts.json`)

## Run it

```bash
# from the repo root
dotnet test tests/CopilotLive.Tests

# a single test
dotnet test tests/CopilotLive.Tests --filter "FullyQualifiedName~Responses_websocket_returns_text"
```

See model ids and replies (xUnit captures `ITestOutputHelper`):

```bash
dotnet test tests/CopilotLive.Tests --logger "console;verbosity=detailed"
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
COPILOT_ANTHROPIC_MODEL=claude-sonnet-4.5 COPILOT_OPENAI_MODEL=gpt-4.1 dotnet test tests/CopilotLive.Tests
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
