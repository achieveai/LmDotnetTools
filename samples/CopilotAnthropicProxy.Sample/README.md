# CopilotAnthropicProxy.Sample

A thin, **loopback-only** reverse proxy that speaks the **Anthropic Messages API** on the inbound
side and forwards every request to **GitHub Copilot** on the outbound side. It lets a developer who
has a GitHub Copilot entitlement (but no Anthropic API key) drive **Claude Code** — or the in-house
`AnthropicClient` — on a Copilot-hosted Claude model by pointing `ANTHROPIC_BASE_URL` at this proxy.

What it does, end to end:

1. Accepts `POST /v1/messages` (streaming and non-streaming), `POST /v1/messages/count_tokens`, and
   `GET /v1/models`.
2. Rewrites the JSON `model` field of the request body: if it names a model the proxy knows about
   (see "Choosing the model" below), it passes through unchanged; otherwise it's rewritten to the
   resolved default Copilot Claude (Opus) id. The top-level `context_management` field is stripped
   (Copilot's backend rejects it outright — see "Known request incompatibilities" below). Everything
   else — `system` blocks, `cache_control`, `thinking`, `tools`, and unknown fields — is preserved
   verbatim (raw `JsonNode` swap, never a typed DTO).
3. Drops the inbound `anthropic-beta` header entirely — it is never forwarded (see "Known request
   incompatibilities" below).
4. Attaches Copilot auth + tracking headers using the proven `GithubCopilotProvider` transport
   (`CopilotHttpClientFactory` / `CopilotHeadersHandler` / the CLI credential token provider).
5. Streams the upstream Server-Sent-Events response straight back to the client as **raw bytes**
   (no parsing, no buffering, incremental flush) and passes status codes and rate-limit headers
   through unchanged. If the upstream stream fails mid-flight the proxy does **not** fabricate any
   terminal frames: it returns a `502` when nothing has been sent yet, otherwise it stops and closes
   the (now incomplete) stream — the client detects the truncation from the missing `message_stop`,
   exactly as it would if the upstream connection had dropped directly.
6. Also transparently exposes Copilot's **MCP server** on `/mcp` and `/mcp/readonly` — see
   "Exposing Copilot's MCP server" below.

> [!WARNING]
> **Local development only.** This proxy has **no inbound authentication** but attaches **your**
> Copilot credentials to every outbound call. It binds to loopback (`127.0.0.1` / `[::1]`) only and
> rejects non-loopback remote addresses, foreign `Host` headers, and cross-site requests. **Never**
> bind it to `0.0.0.0`, put it behind a public reverse proxy, or expose it through a tunnel.

## Prerequisites

- .NET SDK 9.0+
- A resolvable GitHub Copilot credential. Any of the following works (checked in this order by
  `CliCredentialCopilotTokenProvider`): `GITHUB_COPILOT_TOKEN` / `GH_TOKEN` env var, the GitHub
  Copilot CLI sign-in (`~/.copilot/config.json`), the macOS login keychain, the Copilot editor
  credential files, or `gh auth login`. The proxy resolves a token **once at startup** and exits
  with a clear message if none is found.

## Run

```bash
dotnet run --project samples/CopilotAnthropicProxy.Sample
```

On startup the proxy logs the resolved default model, how many models are available, and the listen
address, e.g.:

```
CopilotAnthropicProxy listening on http://127.0.0.1:8787 -> https://api.enterprise.githubcopilot.com (default model: <resolved-opus-id>, 7 available)
```

### Choosing the model

The proxy resolves an outbound **model catalog** (a default id plus the full set of available ids)
at startup in one of two modes:

1. **Pinned** — `COPILOT_ANTHROPIC_MODEL` is set. The catalog is just that one id (both default and
   only available entry); Copilot's `/models` endpoint is never queried, and **every** request is
   rewritten to it regardless of what `model` the client sent — this matches the proxy's original
   single-model behavior.
2. **Discovered** — `COPILOT_ANTHROPIC_MODEL` is unset. The proxy queries Copilot `GET /models` and
   keeps every model whose `supported_endpoints` includes `/v1/messages` (the only ones this
   Anthropic-Messages-shaped proxy can forward to — Copilot's GPT/Gemini models are excluded). The
   default is the Claude id containing `opus`; if none is found, the proxy **fails fast** and logs
   the available Claude ids so you can set `COPILOT_ANTHROPIC_MODEL` explicitly. Copilot can expose
   multiple concurrent `opus` ids at once (e.g. `claude-opus-4.6`, `claude-opus-4.7`,
   `claude-opus-4.8`); the proxy picks the one with the numerically highest version suffix — not
   just the first one Copilot happens to list — so the default always tracks the newest opus model.
   In this mode, a request whose `model` field exactly matches (case-insensitively) one of the
   discovered ids is forwarded **unchanged** (normalized to the catalog's casing); anything else
   falls back to the default.

`GET /v1/models` on the proxy returns an Anthropic-shaped list of every id in the catalog (one entry
in pinned mode, every discovered `/v1/messages`-capable id in discovered mode).

## Point a client at the proxy

There are **two** valid base-URL forms depending on how the client builds the request URL.

### Claude Code / Anthropic SDK-style clients

These append `/v1/messages` to the base URL, so use the **bare host** (no `/v1`):

```bash
ANTHROPIC_BASE_URL=http://127.0.0.1:8787 \
ANTHROPIC_API_KEY=dummy \
claude
```

### `samples/LmStreaming.Sample` (in-house `AnthropicClient`)

The in-house `AnthropicClient` appends `/messages` to the configured base URL, so include `/v1`:

```bash
LM_PROVIDER_MODE=anthropic \
ANTHROPIC_API_KEY=dummy \
ANTHROPIC_BASE_URL=http://127.0.0.1:8787/v1 \
ANTHROPIC_MODEL=any-model-id-the-proxy-will-rewrite \
dotnet run --project samples/LmStreaming.Sample
```

`ANTHROPIC_API_KEY` is required by clients but ignored by the proxy (the inbound `x-api-key` /
`Authorization` are **not** forwarded; Copilot auth is attached outbound). The `ANTHROPIC_MODEL`
value is honored when `COPILOT_ANTHROPIC_MODEL` is unset and it matches one of the models the proxy
discovered from Copilot (see "Choosing the model"); otherwise it's rewritten to the resolved default.

## Exposing Copilot's MCP server

The same proxy also transparently exposes GitHub Copilot's **MCP server** (Streamable HTTP
transport) on:

- `GET` / `POST` / `DELETE` `/mcp` — the full read/write toolset
- `GET` / `POST` / `DELETE` `/mcp/readonly` — the read-only toolset

This is a **byte-level reverse proxy**, not an MCP-aware reimplementation: there's no JSON-RPC
parsing and no proxy-side session bookkeeping. Point any MCP Streamable-HTTP client at
`http://127.0.0.1:8787/mcp` (or `/mcp/readonly`) exactly as you would point it at
`https://api.enterprise.githubcopilot.com/mcp` (or `/mcp/readonly`) directly — request/response
bodies, status codes, and headers are relayed verbatim, including SSE responses (same raw-byte
streaming as `/v1/messages`).

**Header policy**: every inbound header is forwarded verbatim **except** `Authorization` (the
proxy attaches its own Copilot bearer token instead, via the same `CopilotHeadersHandler` used for
`/v1/messages`) and a handful of hop-by-hop/framing headers .NET's `HttpClient` must own
(`Host`, `Content-Length`, `Content-Type`, `Connection`, `Transfer-Encoding`, `Keep-Alive`,
`Upgrade`, `TE`, `Trailer`, `Accept-Encoding`). This means `Mcp-Session-Id`,
`Mcp-Protocol-Version`, `Last-Event-ID`, and Copilot's `X-MCP-*` tool-filtering headers
(`X-MCP-Readonly`, `X-MCP-Toolsets`, `X-MCP-Tools`, `X-MCP-Exclude-Tools`, `X-MCP-Features`,
`X-MCP-Lockdown`, `X-MCP-Insiders`, `X-MCP-Host`, and any future ones) all pass through untouched
— the proxy never needs to know Copilot's MCP header vocabulary in advance. Like the rest of the
proxy, no inbound auth is required to reach `/mcp*`; it's covered by the same loopback +
host/cross-site guard described in the warning above.

### Troubleshooting the base URL

| Symptom (request that reaches the proxy) | Cause | Fix |
| --- | --- | --- |
| `404` on `POST /messages` | You used the bare host with a client that appends only `/messages`. | Add `/v1`: `…:8787/v1`. |
| `404` on `POST /v1/v1/messages` | You added `/v1` for a client that already appends `/v1/messages`. | Drop `/v1`: use `…:8787`. |
| `403 permission_error` | Non-loopback `Host`, cross-site request, or a non-loopback `Origin`. | Use `127.0.0.1`/`localhost`; don't proxy from a browser page on another origin. |
| `401 authentication_error` | The proxy could not acquire a Copilot token on the request. | Re-authenticate (`gh auth login` / Copilot CLI) or set `GITHUB_COPILOT_TOKEN`. |

## Environment variables

| Variable | Default | Purpose |
| --- | --- | --- |
| `COPILOT_ANTHROPIC_MODEL` | (discovered from `/models`) | Pins every request to this single Copilot model id and skips discovery entirely. Unset to discover the full `/v1/messages`-capable catalog instead (see "Choosing the model"). |
| `COPILOT_ANTHROPIC_PORT` | `8787` | Loopback listen port. |
| `COPILOT_ANTHROPIC_BASE_URL` | `https://api.enterprise.githubcopilot.com` | Copilot host root (for non-enterprise hosts). |
| `COPILOT_ANTHROPIC_IDLE_TIMEOUT_SECONDS` | `180` | Per-request idle timeout, reset after each streamed upstream read. The total exchange has no deadline, so long generations are not cut off; this only fires when the upstream produces *nothing* for the whole window. |
| `COPILOT_ANTHROPIC_KEEPALIVE_SECONDS` | `15` | While an SSE upstream is silent, emit a downstream SSE keep-alive comment (`:` line) this often so the client's own read timeout does not fire mid-generation. Keep-alives don't reset the idle timeout above. Set `0` to disable. |
| `COPILOT_ANTHROPIC_ENABLE_DEVICE_FLOW` | `false` | When truthy, allow an interactive GitHub device-flow login at startup (composite provider). Off by default — the request path never blocks on device flow. |

## Logs

The proxy logs through Serilog to two sinks:

- **Console** — a readable single-line view for watching the proxy live.
- **File** — canonical structured JSONL (`@t` / `@mt` plus enriched properties, via Serilog's
  `CompactJsonFormatter`) at `logs/copilot-anthropic-proxy-*.jsonl` next to the built binary
  (e.g. `bin/Debug/net9.0/logs/`, git-ignored), rolled daily with 7 files retained. This is the
  same format as `.logs/tests/tests.jsonl`, so the DuckDB queries in the repo root `CLAUDE.md`
  work against it unchanged.

## `web_search` caveat (LmStreaming.Sample validation)

`samples/LmStreaming.Sample` in `anthropic` mode can enable the Anthropic server-side
`AnthropicWebSearchTool`. The GitHub Copilot backend **rejects** that tool shape with HTTP 400
(`"The use of the web search tool is not supported."`). This proxy does **not** special-case it — the
rejection passes straight through as an upstream 400.

To validate against the proxy, run a flow that does **not** enable web search. The clean way to do
that in `LmStreaming.Sample` is to select (or define) a chat **mode with an empty `EnabledTools`
list**: `ModeToolFilter.FilterBuiltInTools` returns `null` for an empty tool set
(`Services/ModeToolFilter.cs`), which strips `AnthropicWebSearchTool` before the request is built.

## Known request incompatibilities (stripped before forwarding)

Copilot's backend rejects two things Claude Code routinely sends. Both are stripped unconditionally
— the client never sees a 400 for either:

- **`anthropic-beta` header.** Copilot's backend rejects the *entire* request if even one value in a
  comma-separated `anthropic-beta` header is one it doesn't recognize (`"unsupported beta header(s):
  <name>"`). Claude Code's beta list changes frequently and routinely includes values ahead of what
  Copilot supports, so the header is dropped entirely rather than allowlisted value-by-value.
- **`context_management` body field.** Copilot's backend rejects the request outright
  (`"context_management: Extra inputs are not permitted"`) if this top-level field is present. It is
  removed from the JSON body (alongside the `model` rewrite) before forwarding.

## Non-goals (intentionally not implemented)

- **No response-body rewriting.** The response body and the SSE `message_start` event carry whatever
  model id was actually sent upstream (the passed-through id, or the resolved default when the
  request's model wasn't recognized) — never rewritten back to the client's requested id. This is
  accepted for raw-passthrough fidelity.
- **No 200K → 1M context fallback / model routing.** Context-length errors pass through unchanged.
- **No refresh-on-401 / token invalidation.** A request-path token failure maps to a local
  `authentication_error`; re-authenticate out of band.
- **No synthetic `count_tokens` estimator.** `count_tokens` is best-effort pass-through; an
  unsupported upstream (404/405) is normalized to an Anthropic `not_found_error`.
- **No inbound auth, TLS, or CORS** beyond the loopback + host/cross-site guard.
- **No MCP session bookkeeping or resumability logic.** The proxy relays `Mcp-Session-Id` and
  `Last-Event-ID` verbatim but never inspects, persists, or validates them — session lifecycle is
  entirely between the client and Copilot.
