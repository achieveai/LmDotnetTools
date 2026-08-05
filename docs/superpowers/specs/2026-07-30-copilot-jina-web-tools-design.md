# Copilot and Jina Web Tools Design

**Date:** 2026-07-30

## Goal

Expose general web search to Copilot providers through GitHub Copilot's hosted readonly MCP endpoint, while retaining Jina as the URL-fetch implementation and as the web-search fallback. Keep the change narrow and reuse the existing MCP and web-tool infrastructure.

## Confirmed upstream behavior

Live probing through the existing local `CopilotAnthropicProxy.Sample` established that:

- `POST /mcp/readonly` supports MCP protocol `2025-06-18` and returns a session ID.
- The request header `X-MCP-Tools: web_search` makes `tools/list` expose exactly one tool named `web_search`.
- `web_search` accepts one required string parameter, `query`, and returns an AI-generated web answer with citations and sources.
- The hosted catalog does not expose a `web_fetch` tool.
- The existing `/mcp` and `/mcp/readonly` proxy routes already relay request bodies, response bodies, streaming responses, MCP session/protocol headers, and `X-MCP-*` headers. They do not require reimplementation.

## Scope

### In scope

1. Verify and retain transparent proxying of `/mcp` and `/mcp/readonly` to GitHub Copilot.
2. For all Copilot provider paths, expose Copilot's hosted `web_search` selected with `X-MCP-Tools: web_search`.
3. Configure Jina `WebFetch` from `JINA_API_KEY` loaded from the sample's `.env` file.
4. Use Jina `WebSearch` as the fallback when Copilot hosted MCP search cannot be initialized.
5. Preserve chat-mode `EnabledTools` filtering.
6. Degrade gracefully when Copilot MCP is unavailable.

### Out of scope

- Importing GitHub's code, issue, pull-request, repository, commit, or user search tools.
- Importing the complete Copilot readonly MCP catalog.
- Translating arbitrary Anthropic/OpenAI hosted-tool types into MCP calls.
- Adding an MCP protocol parser to the proxy.
- Adding a new dotenv library or logging API keys.

## Architecture

### Existing proxy

Keep `CopilotAnthropicProxy.Sample`'s `/mcp` and `/mcp/readonly` mappings unchanged. Extend tests only where needed to prove that `X-MCP-Tools: web_search` is forwarded verbatim alongside raw JSON-RPC bodies and MCP session headers.

### Copilot hosted web search

Add a small sample-level registration helper adjacent to `WebToolRegistrationPolicy`. It will:

1. Run only for Copilot-backed providers.
2. Build an authenticated Copilot `HttpClient` through `CopilotHttpClientFactory`, preserving per-request token refresh and required Copilot headers.
3. Create an MCP Streamable-HTTP client targeting `{CopilotBaseUrl}/mcp/readonly`.
4. Set `X-MCP-Tools: web_search`, so discovery returns only the desired hosted tool.
5. Import the discovered tool through the existing `McpClientFunctionProvider`/`FunctionRegistry` path.
6. Return the MCP client as an owned asynchronous resource for conversation-lifetime disposal.
7. Return a success indicator and diagnostic status so the caller can choose the Jina fallback.

The helper remains sample-specific. It does not become a new general provider abstraction.

### Provider coverage

- Dynamically discovered Copilot-backed Anthropic and OpenAI models receive the hosted MCP function in their per-conversation registry.
- The plain `copilot` CLI path receives the same capability through the smallest existing compatible path: prefer its MCP-server configuration when the CLI can carry Copilot authentication and the required header; otherwise expose the imported function through the existing dynamic-tool bridge. The implementation plan must verify this path before choosing it.
- Non-Copilot providers retain current behavior.

### Jina WebSearch and WebFetch

`LmStreaming.Sample` already calls `EnvironmentHelper.LoadEnvIfNeeded(FindEnvFile())` before `WebToolsOptions.FromEnvironment()`. A sample-local `.env` is therefore a supported source for `JINA_API_KEY`; no secret-loading code change is required unless a regression test proves otherwise.

Retain `WebToolRegistrationPolicy` and its provider/mode filtering:

- `WebFetch` is registered through Jina for eligible providers whenever enabled by the selected mode and `JINA_API_KEY` is present.
- For Copilot paths, Jina `WebSearch` is registered only when hosted MCP `web_search` registration failed or was unavailable.
- Existing name-collision checks remain authoritative.
- Direct providers with native web capability retain the current capability policy.

## Data flow

1. The sample loads `.env` without logging secret values.
2. It creates `WebToolsOptions` and a process-level `JinaWebProvider` when `JINA_API_KEY` is available.
3. During Copilot conversation creation, the hosted-search registration helper connects to `/mcp/readonly` with `X-MCP-Tools: web_search`.
4. On success, the sole MCP contract is added to the active function registry and the MCP client is added to conversation-owned resources.
5. `WebToolRegistrationPolicy` adds Jina `WebFetch`; it skips Jina `WebSearch` because hosted search succeeded.
6. On MCP failure, the helper logs a structured warning and returns failure. Conversation creation continues, and `WebToolRegistrationPolicy` adds Jina `WebSearch` and `WebFetch` when enabled and configured.
7. Tool calls execute through the existing middleware and MCP/Jina handlers.

## Error handling

- MCP initialization, discovery, or call failures must not make a Copilot provider unavailable.
- Initialization/discovery failure produces one structured warning without credentials, authorization headers, session IDs, or response bodies that may contain sensitive data.
- A failed MCP client is disposed immediately.
- Successfully created MCP clients are disposed with the conversation agent.
- Missing `JINA_API_KEY` leaves Jina tools disabled using the existing status reporting.
- When both Copilot MCP and Jina are unavailable, the provider remains usable without web tools.

## Testing

### Deterministic tests

1. Proxy tests prove `/mcp` and `/mcp/readonly` forward:
   - raw JSON-RPC bodies;
   - `Mcp-Session-Id` and `Mcp-Protocol-Version`;
   - `X-MCP-Tools: web_search`;
   - streamed responses and statuses.
2. Hosted-search registration tests use a fake MCP upstream to prove:
   - only `web_search` is imported;
   - non-Copilot providers do not connect;
   - mode filtering can suppress the tool;
   - failure returns control for Jina fallback;
   - created clients are disposed.
3. Web-tool policy tests prove:
   - Copilot MCP success yields hosted `web_search` plus Jina `WebFetch`, without duplicate Jina `WebSearch`;
   - MCP failure yields Jina `WebSearch` and `WebFetch` when configured;
   - missing Jina configuration remains non-fatal;
   - non-Copilot behavior is unchanged.
4. Environment-loading coverage proves the sample resolves its local `.env` before constructing web-tool options without reading or printing the key.

### Opt-in live test

Add or extend `tests/CopilotLive.Tests` to initialize the real readonly MCP endpoint with `X-MCP-Tools: web_search` and assert that `tools/list` returns exactly that contract. It remains outside the solution/CI and skips when Copilot credentials are unavailable.

## Success criteria

- Existing proxy routes continue to pass MCP traffic transparently.
- Copilot-backed models can call hosted `web_search` without importing unrelated GitHub tools.
- Jina performs URL fetches and provides search fallback using `JINA_API_KEY` from the sample's `.env`.
- Web-tool availability still respects the selected chat mode.
- Copilot and conversations remain usable when either hosted MCP or Jina is unavailable.
