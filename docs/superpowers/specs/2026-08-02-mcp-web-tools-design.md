# MCP Web Tools Design

## Goal

Expose dependable `web_search` and `web_fetch` tools through the existing Copilot proxy `/mcp` and `/mcp/readonly` endpoints.

For each exact tool name, use GitHub's upstream MCP implementation when GitHub advertises it. Otherwise, expose a local Jina-backed implementation when `JINA_API_KEY` is configured.

This work changes only the MCP endpoint. It does not alter Anthropic Messages translation, OpenAI Responses translation, or native server-tool handling.

## Availability rules

| GitHub advertises name | Jina key configured | Advertised implementation |
|---|---|---|
| Yes | Either | GitHub definition, unchanged |
| No | Yes | Local Jina definition |
| No | No | Tool omitted |

These rules apply independently to `web_search` and `web_fetch`, so mixed catalogs are valid.

The backend selected by a successful `tools/list` response remains authoritative until that client requests a new tool list. There is no mid-call failover because GitHub and Jina may expose different schemas.

If GitHub's `tools/list` request fails, preserve that failure. Do not replace the complete GitHub catalog with a Jina-only catalog.

## Architecture

Add a targeted JSON-RPC composition layer before the existing `ProxyMcp` byte relay.

The layer understands only:

- A single-object POST request whose method is `tools/list`.
- A single-object POST request whose method is `tools/call`.
- MCP DELETE for local snapshot cleanup after forwarding it upstream.

Everything else remains transparent, including `initialize`, client notifications, prompts, resources, unknown methods, GET/SSE, and JSON-RPC batch arrays.

GitHub handles `initialize` and remains the MCP session owner. Existing `Mcp-Session-Id`, `Mcp-Protocol-Version`, `Last-Event-ID`, and `X-MCP-*` forwarding remains unchanged.

### Transport boundary

The composition path supports GitHub `tools/list` responses with `Content-Type: application/json`.

MCP also permits a POST response to use `text/event-stream`. Rewriting an SSE response would require buffering and re-framing the stream, which is outside this feature's narrow scope. If GitHub returns SSE for `tools/list`, pass that response through unchanged and do not advertise local fallbacks from that response. Log a bounded warning without response content or credentials.

Local `tools/call` results are returned as `application/json`. GitHub-owned calls retain GitHub's original response content type and streaming behavior.

JSON-RPC batch arrays pass through unchanged. The current MCP Streamable HTTP protocol requires one JSON-RPC message per POST, so the proxy does not split or recombine batch items.

### Tool listing

For a single-page, successful GitHub `tools/list` JSON response:

1. Preserve every GitHub tool object, including unknown fields and schemas.
2. Determine whether GitHub advertised the exact names `web_search` and `web_fetch`.
3. If Jina is enabled, append a local definition for each missing name.
4. Store the names injected locally, together with the endpoint and relevant GitHub tool-filter header fingerprint.
5. Return the composed JSON-RPC result with the original request ID and applicable upstream response headers.

The design deliberately supports the single-page catalog shape currently observed from GitHub. If a response contains a nonempty `nextCursor`, return it unchanged and do not inject or record local fallbacks. Pagination composition can be added later if GitHub is observed using it.

Each successful composed listing atomically replaces the previous snapshot for that MCP session and endpoint.

### Tool calls

For a single-object `tools/call` request:

- If the requested name was locally injected in the latest matching snapshot, execute the local Jina-backed handler.
- Otherwise, forward the request to GitHub unchanged.
- A GitHub-owned call that fails returns GitHub's error; it does not fall back to Jina.

The relevant `X-MCP-*` tool-filter headers are part of catalog provenance because they can change GitHub's advertised tools. A call may use the latest snapshot when it sends no tool-filter headers. If it sends tool-filter headers, they must match the snapshot fingerprint for local dispatch; otherwise, forward the call to GitHub rather than guessing.

Local fallbacks require an `Mcp-Session-Id` supplied by GitHub. If GitHub does not create a session, preserve its catalog unchanged and do not inject local tools because later calls cannot be routed safely to the schema that client received.

## Snapshot lifecycle

Use a process-local concurrent dictionary keyed by MCP endpoint and `Mcp-Session-Id`. Each snapshot contains only:

- Names injected locally.
- A deterministic fingerprint of relevant `X-MCP-*` tool-filter headers from the listing request.

A new successful composed `tools/list` replaces the snapshot atomically. After forwarding MCP DELETE, remove the matching snapshot regardless of whether GitHub accepts session termination. A GitHub HTTP 404 for a session-bound request also removes the snapshot.

The proxy is loopback-only and single-developer oriented, so no speculative capacity limit, pagination accumulator, notification parser, or eviction policy is added. Process restart clears abandoned snapshots.

## Local Jina tools

Reuse the existing implementation in `src/Misc`:

- `JinaWebProvider`
- `WebSearchTool`
- `WebFetchTool`
- `WebToolsOptions`
- `WebInputValidator`
- `WebToolOutput`

Add a project reference from `CopilotAnthropicProxy.Sample` to `Misc`, and update the project comment that currently says the GitHub Copilot provider is its only required reference. Do not introduce another Jina HTTP client or duplicate validation and sanitization logic.

Expose snake_case MCP names:

- `web_search`
- `web_fetch`

Their argument schemas otherwise reflect the existing tool contracts. Invoke the modern `ToolHandler` directly and convert its payload into MCP `CallToolResult` content. Preserve its `IsError` value and pass request cancellation through.

Do not use `LegacyHandlerAdapter`; it loses `IsError` and cancellation. Existing PascalCase wording inside bounded handler error text is acceptable and is not rewritten in this feature.

## Configuration

Use the existing web-tool environment configuration:

- `JINA_API_KEY`
- `WEB_TOOLS_OUTPUT_CAP`
- `WEB_TOOLS_TIMEOUT_MS`
- Other settings already supported by `WebToolsOptions`

Per the requested product rule, local fallback for either tool is enabled only when `JINA_API_KEY` is nonblank and the options validate successfully. Although the underlying Jina fetch implementation can operate keyless, this MCP surface intentionally requires the key for predictable availability and limits.

Invalid Jina configuration produces a bounded startup warning and disables local fallbacks. It does not prevent the proxy from serving GitHub's MCP endpoint.

## Errors and security

- Malformed single-object JSON-RPC requests that target local interception return a JSON-RPC error with the original request ID when available.
- Local execution failures return MCP `CallToolResult` with `isError: true`.
- GitHub JSON-RPC results and errors pass through unchanged for GitHub-owned tools.
- GitHub `tools/list` transport and JSON-RPC errors remain errors.
- HTTP request cancellation flows into the local `ToolHandler` and Jina HTTP operation.
- Error text remains bounded and excludes raw upstream bodies.

The local tools retain existing protections:

- URL scheme and structure validation.
- Blocking loopback, private, link-local, multicast, unspecified, and local-name destinations.
- IPv4-mapped IPv6 handling.
- Header-injection protection for selector inputs.
- Untrusted-content framing.
- Output sanitization and truncation.
- Secret-safe logging and error mapping.

The Jina key must never appear in MCP definitions, results, errors, logs, or GitHub-bound traffic.

## `/mcp/readonly`

`/mcp/readonly` uses the same composition logic and has a separate snapshot namespace from `/mcp`. GitHub's readonly and tool-filter controls remain authoritative and continue upstream unchanged.

The local Jina tools are read-only and may be injected under the same availability rules.

## Tests

Extend focused proxy tests to prove:

1. GitHub `web_search` and `web_fetch` definitions win and remain structurally unchanged.
2. Each absent name is injected from Jina only with a valid key.
3. No key or invalid Jina configuration means no local fallbacks.
4. Mixed catalogs work, such as GitHub search plus Jina fetch.
5. Calls route according to the latest snapshot advertised for that endpoint and session.
6. A subsequent successful list can change ownership and atomically replace the snapshot.
7. Matching, absent, and mismatched `X-MCP-*` filter headers follow the provenance rule.
8. GitHub-owned call failures do not trigger Jina fallback.
9. GitHub `tools/list` failures remain failures.
10. SSE-framed and paginated `tools/list` responses pass through unchanged without recording local fallbacks.
11. JSON-RPC batch arrays pass through unchanged.
12. DELETE and upstream session 404 remove snapshots.
13. Local handler failures preserve `isError: true`, and cancellation reaches the handler.
14. Existing SSRF, output-cap, and secret-hygiene behavior remains enforced through MCP.
15. Unrelated JSON-RPC, headers, GET/SSE, and unsupported methods retain existing behavior.
16. `/mcp/readonly` composes independently without weakening GitHub restrictions.
17. A GitHub session without `Mcp-Session-Id` remains unchanged and receives no local tools.

Use a fake GitHub upstream and fake Jina HTTP handler for focused tests. When real credentials are available, also run an authenticated smoke test that initializes an MCP session, lists tools, and calls each advertised web tool.

## Completion criteria

The feature is complete when an MCP client connected to the existing endpoint:

- Sees at most one definition for each exact web-tool name.
- Sees GitHub's definition whenever GitHub advertises it in a supported, single-page JSON catalog.
- Otherwise sees the Jina definition only when Jina is configured and routing can be tied to a GitHub session.
- Has each call routed consistently with the definition and tool-filter context it received.
- Retains existing transparent behavior outside the targeted interception paths.
- Passes focused security, routing, protocol, and regression tests.
