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
dotnet run --project samples/MockProviderHost -- --port 5099
```

The standalone entry point ships with a trivial demo scenario. Real test
scenarios are built via `ScriptedSseResponder.New()` and passed to
`MockProviderHostBuilder.Build(...)` — see `tests/MockProviderHost.Tests` and
`tests/MockProviderHost.E2E.Tests` for examples.

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
