# MockProviderHost

A standalone, runnable HTTP service that wraps `ScriptedSseResponder`
(`src/LmTestUtils/TestMode/`) behind OpenAI- and Anthropic-compatible endpoints,
so external CLI tools (Claude Agent SDK, Codex, Copilot CLI) can be redirected
at the same scripted scenarios our unit tests use.

This is a **thin transport wrapper**. The host adds no SSE framing; every byte
streamed back to the client originates from `SseStreamHttpContent` /
`AnthropicSseStreamHttpContent`, the same emitters used by the in-process tests.

## Endpoints

| Method | Path                    | Wire                | Description                              |
|--------|-------------------------|---------------------|------------------------------------------|
| GET    | `/healthz`              | text/plain `ok`     | Liveness probe.                          |
| POST   | `/v1/chat/completions`  | OpenAI SSE          | Streaming chat completion.               |
| POST   | `/v1/messages`          | Anthropic SSE       | Streaming Anthropic messages.            |

`Authorization` and `x-api-key` headers are accepted and ignored — the inner
`ScriptedSseResponder` does not authenticate. Conventional response headers
(`openai-version`, `anthropic-version`, request id) are echoed so strict SDKs
don't reject responses.

## Running standalone

```bash
# Default: load the embedded "demo" scenario.
dotnet run --project samples/MockProviderHost -- --port 5099

# Pick a different built-in scenario or a path on disk.
dotnet run --project samples/MockProviderHost -- --port 5099 --scenario demo
dotnet run --project samples/MockProviderHost -- --port 5099 --scenario /path/to/my.json

# LM_MOCK_SCENARIO env var is the same hook (used by LmStreaming.Sample).
LM_MOCK_SCENARIO=demo dotnet run --project samples/MockProviderHost
```

Scenario JSON schema (see `samples/MockProviderHost/scenarios/demo.json` for a full
example). For programmatic scenarios in tests, build a `ScriptedSseResponder` directly via
`ScriptedSseResponder.New()` and pass it to `MockProviderHostBuilder.Build(...)`.

```json
{
  "roles": [
    {
      "key": "demo",
      "match": { "type": "always" },
      "turns": [
        { "messages": [{ "kind": "text", "text": "Hi!" }] }
      ]
    }
  ]
}
```

Match types: `always`, `system_contains` (with `value`), `user_contains` (with `value`),
`tool` (with `name`). Message kinds: `text`, `text_len` (with `wordCount`), `tool_call`
(with `name` and optional `args`).

## Embedded use (the common case)

```csharp
var responder = ScriptedSseResponder.New()
    .ForRole("parent", ctx => ctx.SystemPromptContains("helpful"))
        .Turn(t => t.Text("Hello from the mock"))
    .Build();

var app = MockProviderHostBuilder.Build(
    responder,
    urls: ["http://127.0.0.1:0"]); // ephemeral port for tests

await app.StartAsync();
var baseUrl = app.Urls.First();           // e.g., http://127.0.0.1:51234
// point your CLI at $"{baseUrl}/v1" via OPENAI_BASE_URL / ANTHROPIC_BASE_URL
```

## Authoring scenarios

The instruction-chain script format and `ScriptedSseResponder` builder are
documented inline at `src/LmTestUtils/TestMode/ScriptedSseResponder.cs` and
`src/LmTestUtils/TestMode/InstructionChainParser.cs`.

## Out of scope (today)

These are deliberately not implemented in this issue and may be filed as
follow-ups:

- File-based script loader (`--script <path>`).
- Per-request scenario routing via `X-Mock-Scenario` header.
- Embeddings / rerank / `/v1/responses` / image / audio endpoints.
- Admin API for hot-swapping scripts at runtime.
- Docker image.

## Copilot CLI integration (investigation, issue #14)

Status: **investigation only — yellow light**. The information below was
gathered from the public `@github/copilot` documentation, the in-repo
`CopilotSdkProvider` source, and the Anthropic-side pattern landed in
PR #12. The empirical probe described under "Probe matrix" below has **not**
been executed on a CLI-equipped machine as part of this work item; that is
the next person's job (or the follow-up E2E issue's job). This document
captures the intended probe so the next session does not need to redo the
research.

### Two-layer architecture

`CopilotSdkProvider` runs the Copilot CLI as a child process and speaks
**ACP / JSON-RPC 2.0 over stdio** to it (see
`src/CopilotSdkProvider/Transport/CopilotAcpTransport.cs`). The CLI in turn
calls a model endpoint over HTTPS:

```
+-------------------+     ACP/JSON-RPC      +-------------+    HTTPS    +--------------------------+
|  CopilotSdk host  |  <----- stdio ----->  | copilot CLI |  -------->  |  api.githubcopilot.com   |
| (this repo)       |                       | child proc  |             |  (or BYOK endpoint)      |
+-------------------+                       +-------------+             +--------------------------+
```

`MockProviderHost` can only intercept the **HTTPS layer**. Any redirect
strategy therefore has to convince the CLI to point at the host's bound URL
*from inside the child process*, via env vars set on `ProcessStartInfo`.

### Documented BYOK env-var contract

According to the public CLI docs (`copilot help environment`, plus the
`@github/copilot` BYOK section), the CLI exposes the following overrides:

| Env var                          | Purpose                                                 | Example                                  |
|----------------------------------|---------------------------------------------------------|------------------------------------------|
| `COPILOT_PROVIDER_BASE_URL`      | Endpoint base URL                                        | `http://127.0.0.1:51234/v1`              |
| `COPILOT_PROVIDER_TYPE`          | `openai` (default) \| `azure` \| `anthropic`             | `openai`                                 |
| `COPILOT_PROVIDER_API_KEY`       | API key the CLI presents to the endpoint                 | `sk-fake`                                |
| `COPILOT_PROVIDER_BEARER_TOKEN`  | Pre-built bearer token (alternative to `_API_KEY`)       | `bearer-...`                             |
| `COPILOT_PROVIDER_WIRE_API`      | `completions` (Chat Completions) \| `responses`          | `completions`                            |
| `COPILOT_PROVIDER_MODEL_ID`      | Model id sent in the request body                        | `gpt-4o-mini`                            |
| `COPILOT_PROVIDER_WIRE_MODEL`    | Wire-level model id override (when distinct from above)  | `gpt-4o-mini`                            |
| `COPILOT_OFFLINE`                | Assert no egress to `api.githubcopilot.com`              | `1`                                      |

Setting BYOK values is **documented to bypass GitHub OAuth** — i.e., a fake
token against a local mock should work without a real GitHub login. The
probe matrix below is what confirms (or refutes) this on the CLI version
under test.

### Existing-code reconciliation: `COPILOT_BASE_URL` vs `COPILOT_PROVIDER_BASE_URL`

`CopilotAcpTransport.StartAsync` (lines 91-114) currently sets:

- `COPILOT_API_KEY` and `GITHUB_TOKEN` from `CopilotSdkOptions.ApiKey`
- `COPILOT_BASE_URL` (singular, no `_PROVIDER_`) from `CopilotSdkOptions.BaseUrl`

The documented BYOK var is `COPILOT_PROVIDER_BASE_URL`. Two possibilities:

1. The CLI honors **both** (legacy alias + new BYOK suite). In this case the
   existing code path keeps working, but the BYOK suite is needed to also
   force `_TYPE` / `_WIRE_API` / `_API_KEY`. The follow-up issue's
   `CopilotSdkOptions` extension is then **purely additive** (new opt-in
   knobs, existing `BaseUrl` keeps mapping to `COPILOT_BASE_URL`).
2. Only the `_PROVIDER_` variants are honored on current CLI versions. In
   that case, `BaseUrl` → `COPILOT_BASE_URL` is partially dead code today
   and the follow-up issue should migrate to `COPILOT_PROVIDER_BASE_URL`
   (with a deprecation note on the existing `BaseUrl` semantic).

The probe matrix below disambiguates these. **Until the probe runs, do not
remove the existing `COPILOT_BASE_URL` mapping.**

### Wire-format compatibility

| CLI setting                              | MockProviderHost endpoint            | Status                          |
|------------------------------------------|--------------------------------------|---------------------------------|
| `COPILOT_PROVIDER_TYPE=openai`,<br>`COPILOT_PROVIDER_WIRE_API=completions` | `POST /v1/chat/completions` | Already implemented today.      |
| `COPILOT_PROVIDER_TYPE=anthropic`        | `POST /v1/messages`                  | Already implemented today.      |
| `COPILOT_PROVIDER_WIRE_API=responses`    | (none)                               | Out of scope — follow-up issue. |

Newer Copilot model paths (GPT-5 family) use `/v1/responses`. If the probe
finds the CLI defaults to that route on the version under test, the
follow-up E2E issue must add a `/v1/responses` route to
`MockProviderHostBuilder` before E2E coverage is possible.

### Probe matrix (the empirical step)

Run on a developer machine with the Copilot CLI installed at
≥ `0.0.410` (matches `CopilotSdkOptions.CopilotCliMinVersion`).

1. `copilot --version` → record version.
2. `copilot help environment` → snapshot env-var list. Confirm `COPILOT_PROVIDER_*`
   names match the table above; flag any drift in this doc.
3. Stand up `EphemeralHostFixture` with an OpenAI-shaped responder
   (see `tests/MockProviderHost.E2E.Tests/Scenarios/CopilotSdkAgainstMockTests.cs`
   for the scaffold).
4. Spawn the CLI directly (not via `CopilotAcpTransport` — the transport
   does not yet wire BYOK options) with the env matrix below, and record
   whether `responder.RemainingTurns["parent"]` reaches `0` (the
   ground-truth signal that the CLI hit `/v1/chat/completions`):

   | Run | Env vars set                                                                                                                                         | Expected                                                                                                                                |
   |-----|------------------------------------------------------------------------------------------------------------------------------------------------------|-----------------------------------------------------------------------------------------------------------------------------------------|
   | A   | `COPILOT_PROVIDER_BASE_URL`, `COPILOT_PROVIDER_TYPE=openai`, `COPILOT_PROVIDER_API_KEY=fake`, `COPILOT_PROVIDER_WIRE_API=completions`, `COPILOT_OFFLINE=1` | CLI reaches mock without GitHub OAuth; `RemainingTurns["parent"] == 0`. Verdict: **GREEN** for follow-up E2E.                            |
   | B   | `COPILOT_BASE_URL` only (legacy var the transport already sets), no BYOK suite                                                                       | If responder still drains, the legacy var is honored (case 1 above). If not, the existing code path is dead and case 2 applies.         |
   | C   | No env overrides                                                                                                                                     | CLI prompts for OAuth or fails with a missing-token error — confirms BYOK is the bypass path.                                           |
   | D   | BYOK suite **without** `COPILOT_OFFLINE=1`                                                                                                           | Same as A; `OFFLINE=1` is for asserting no egress, not for enabling the bypass.                                                          |

5. If the CLI defaults to `/v1/responses` instead of `/v1/chat/completions`,
   record the request the responder received and add a follow-up note for
   the `/v1/responses` route work.

### Verdict and follow-up shopping list

Verdict will be one of:

- **GREEN** — Run A succeeds. Follow-up E2E issue can land the
  `CopilotSdkOptions` BYOK extension and a real (non-skipped) E2E test.
- **YELLOW** — Run A succeeds with caveats (e.g., requires a specific
  `WIRE_MODEL`, or only works for `WIRE_API=responses`). Follow-up E2E
  issue must add the missing endpoint(s) first.
- **RED** — BYOK does not bypass GitHub OAuth on the CLI version under
  test. Follow-up E2E is blocked on either an upstream CLI fix or a
  different redirect strategy (e.g., HTTPS proxy with a trusted root).

Concrete next steps for the follow-up E2E issue (regardless of verdict
shade, all are **additive**):

- Extend `CopilotSdkOptions` with nullable BYOK knobs:
  `ProviderBaseUrl`, `ProviderType`, `ProviderApiKey`, `ProviderBearerToken`,
  `ProviderWireApi`, `ProviderModelId`, `ProviderWireModel`. All default
  `null` (untouched today).
- Mirror PR #12's `ApplyMockHostOverrides` shape on `CopilotAcpTransport.StartAsync`:
  copy each non-null option onto `psi.Environment` under the matching
  `COPILOT_PROVIDER_*` key.
- Reconcile the existing `BaseUrl` → `COPILOT_BASE_URL` mapping based on
  Run B: keep as alias (case 1) or migrate to `COPILOT_PROVIDER_BASE_URL`
  with a deprecation note (case 2).
- Decide whether `MockProviderHostBuilder` needs a `/v1/responses` route
  (only if Run A or D recorded that path).
- Replace the `[SkippableFact]` skip gate in `CopilotSdkAgainstMockTests`
  with a CLI-presence-only gate (drop `LMDOTNET_RUN_COPILOT_E2E=1`) once
  the contract is stable.

### Scaffold test

A skipped scaffold mirroring the Claude pattern lives at
`tests/MockProviderHost.E2E.Tests/Scenarios/CopilotSdkAgainstMockTests.cs`,
gated by `LMDOTNET_RUN_COPILOT_E2E=1` plus a CLI-presence probe in
`tests/MockProviderHost.E2E.Tests/Infrastructure/CopilotCliPrerequisites.cs`.
The test is intentionally not wired through `CopilotAcpTransport` — it
spawns `copilot` directly and sets BYOK env vars on the child, so the
scaffold can be turned on without first landing the transport-side
options work.
