# CopilotAnthropicProxy.Sample

A thin, **loopback-only** reverse proxy that puts every model in your **GitHub Copilot** catalog
behind whichever API dialect your coding agent already speaks. It accepts the **Anthropic Messages
API** *and* the **OpenAI Chat Completions / Responses APIs** on the inbound side, and forwards to
Copilot on the outbound side. A developer with a Copilot entitlement (but no Anthropic or OpenAI
key) can point **Claude Code**, **Codex CLI** or **opencode** at it and drive any model Copilot
exposes.

Most of that is byte-for-byte passthrough. The one place real work happens is **Anthropic Messages
in, for a model that only speaks the Responses API** — Claude Code asking for `gpt-5.3-codex`. There
the proxy translates the request, the reply, and the SSE stream between the two shapes. See
"Known limitations" for what that translation cannot carry.

> [!WARNING]
> **Local development only.** This proxy has **no inbound authentication** but attaches **your**
> Copilot credentials to every outbound call. It binds to loopback (`127.0.0.1` / `[::1]`) only and
> rejects non-loopback remote addresses, foreign `Host` headers, and cross-site requests. **Never**
> bind it to `0.0.0.0`, put it behind a public reverse proxy, or expose it through a tunnel.

## Endpoints

| Method | Path | Purpose |
|---|---|---|
| `POST` | `/v1/messages`, `/messages` | Anthropic Messages. Passthrough for Claude models; translated to `/responses` for Responses-only GPT models. |
| `POST` | `/v1/messages/count_tokens`, `/messages/count_tokens` | Anthropic token counting. Claude models only. |
| `POST` | `/v1/chat/completions`, `/chat/completions` | OpenAI Chat Completions. Passthrough. |
| `POST` | `/v1/responses`, `/responses` | OpenAI Responses. Passthrough. |
| `GET` | `/v1/models` | Model list in a body both dialects can parse. |
| `GET` | `/health` | Liveness. |
| `ALL` | `/mcp`, `/mcp/readonly` | Unchanged MCP passthrough. |

Every `POST` binds both the `/v1`-prefixed and un-prefixed form, because clients disagree about where
the prefix belongs: Claude Code appends `/v1/messages` to a bare host, the Vercel AI SDK appends only
`/messages` to a `…/v1` base, and Codex joins `{base}/responses`.

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
CopilotAnthropicProxy listening on http://127.0.0.1:8788 -> https://api.enterprise.githubcopilot.com (default model: <resolved-opus-id>, 17 available; idle 180s, keep-alive 15s)
```

## Configuring each client

```bash
# Claude Code — any model in the catalog, Claude or GPT
export ANTHROPIC_BASE_URL=http://127.0.0.1:8788
export ANTHROPIC_MODEL=gpt-5.3-codex

# Codex CLI — ~/.codex/config.toml
# base_url = "http://127.0.0.1:8788/v1"

# opencode — OpenAI-compatible provider
# baseURL: "http://127.0.0.1:8788/v1"
```

`ANTHROPIC_API_KEY` (or `OPENAI_API_KEY`) is required by these clients but ignored by the proxy: the
inbound `x-api-key` / `Authorization` headers are **not** forwarded, and Copilot auth is attached
outbound.

### `samples/LmStreaming.Sample` (in-house `AnthropicClient`)

The in-house `AnthropicClient` appends `/messages` to the configured base URL, so include `/v1`:

```bash
LM_PROVIDER_MODE=anthropic \
ANTHROPIC_API_KEY=dummy \
ANTHROPIC_BASE_URL=http://127.0.0.1:8788/v1 \
ANTHROPIC_MODEL=any-model-id-the-proxy-will-rewrite \
dotnet run --project samples/LmStreaming.Sample
```

## Which models are served

Discovered from Copilot's `/models` at startup — nothing is hard-coded. A model is served when it
advertises at least one of `/v1/messages`, `/chat/completions` or `/responses`, and its vendor is
neither Google nor Microsoft. Models advertising no endpoints (including `text-embedding-*`) are
excluded so an embedding model can never surface as a chat model.

Requests naming an unknown model are mapped onto the same family when one exists
(`claude-3-5-haiku-*` → `claude-haiku-4.5`) and otherwise onto the catalog default. That matters
because Claude Code sends conversation-title, classification and summarisation traffic to this same
base URL under haiku model ids; without family mapping, every one of them would bill against Opus.

`GET /v1/models` returns a dual-dialect union list of every id in the catalog.

> [!NOTE]
> Setting `COPILOT_ANTHROPIC_MODEL` **pins** every request to that one id and skips discovery
> entirely. A pinned catalog carries no endpoint metadata, so the pinned model is treated as an
> Anthropic Messages passthrough — **the translated route is unreachable in pinned mode**. Leave the
> variable unset to drive GPT models through `/v1/messages`.

## Known limitations

These affect **only** the translated route — Anthropic Messages in, for a model that speaks only the
Responses API. Every passthrough route is byte-for-byte and has none of them.

**Request fields**

- **`stop_sequences` is dropped.** The Responses API has no equivalent parameter, and emulating one
  means truncating a stream at a boundary that can straddle SSE chunks. Claude Code uses it only for
  auto-mode classification, which targets the haiku tier — an Anthropic model, hence passthrough.
- **`max_tokens` is raised to 16 when smaller.** Claude Code's first request against a new model is a
  validation probe with `max_tokens: 1`, which the Responses API rejects outright; passed through
  literally, every GPT model would look broken before you got a turn.
- **`temperature` and `top_p` are passed through.** Verified live rather than assumed — OpenAI
  documents its own reasoning models as rejecting a non-default `temperature`, but Copilot accepts
  both and honours them.

**Reasoning**

- **`thinking` blocks are never produced, and reasoning does not carry across turns.** The translated
  request sends no `reasoning` field, and Copilot emits reasoning summaries *only* when one is
  explicitly asked for — so no summary events arrive and no `thinking` block is ever emitted. The
  encrypted payload that would let a GPT model resume its own reasoning through a tool loop is not
  round-tripped either. Answers stay correct; the model re-derives its reasoning each turn, and you
  simply do not see it. (Should a future request start asking for summaries, the `thinking` blocks
  the translators would then emit carry **no `signature`** — Anthropic clients that verify one will
  reject them.)

**Response content**

- **Only `output_text` message parts survive.** Any other part type in a `message` item is dropped,
  including `refusal`. A turn that consists solely of a refusal therefore arrives as `content: []`
  with `stop_reason: "end_turn"` — indistinguishable from a genuinely empty turn, and the refusal
  text is lost.
- **Tool ids are passed through verbatim.** Responses mints `call_…`; Anthropic conventionally uses
  `toolu_…`. The round-trip works because the id we hand out is the id we accept back, but Claude
  Code cannot pattern-match these ids when explaining a tool-pairing failure, so that one error
  message is less specific than usual.
- **Cached input tokens are reported as fresh ones.** Copilot returns the cached prefix as
  `input_tokens_details.cached_tokens` *inside* the total `input_tokens`, and the Anthropic usage
  shape the proxy emits has only `input_tokens` / `output_tokens` — no
  `cache_read_input_tokens` field to put it in. Any client costing a session from these numbers will
  over-report whenever Copilot serves a cached prefix.

**Streaming**

- **A multi-line `data:` event would be silently dropped.** SSE permits an event to span several
  `data:` lines; the stream translator reads one line at a time and does not reassemble them. Not
  observed in practice — every live Copilot Responses stream measured so far uses exactly one
  `data:` line per event — but a change upstream would lose those events with no error.
- **A mid-stream upstream failure looks like a dropped connection.** The proxy never fabricates
  terminal frames, so an upstream `response.failed` and a severed socket both end as a stream with no
  `message_stop`. The reason is logged proxy-side; the client sees only the truncation.
- **An early keep-alive forfeits the error envelope.** If the keep-alive interval elapses before the
  first upstream frame arrives, the ping starts the response body — after which a subsequent upstream
  error can no longer be reported as a clean `502` JSON envelope. This is deliberate parity with the
  raw passthrough route, which has always behaved this way.
- **The idle timeout measures the gap between upstream *lines*, not bytes.** An upstream dribbling a
  single enormous line for longer than `COPILOT_ANTHROPIC_IDLE_TIMEOUT_SECONDS` is treated as idle
  even though bytes are still moving.

**Endpoints**

- **`count_tokens` serves Claude models only.** For a Responses-only model the proxy answers `404`
  with an Anthropic `not_found_error` rather than running a billed generation or inventing a number.
  Clients degrade gracefully — Claude Code logs `countTokens API call failed` and falls back to a
  local estimate, which drives only the `/context` bar and the auto-compact threshold.
- **Prompt caching, batch and files APIs are not proxied.**

## Exposing Copilot's MCP server

The same proxy also transparently exposes GitHub Copilot's **MCP server** (Streamable HTTP
transport) on:

- `GET` / `POST` / `DELETE` `/mcp` — the full read/write toolset
- `GET` / `POST` / `DELETE` `/mcp/readonly` — the read-only toolset

This is a **byte-level reverse proxy**, not an MCP-aware reimplementation: there's no JSON-RPC
parsing and no proxy-side session bookkeeping. Point any MCP Streamable-HTTP client at
`http://127.0.0.1:8788/mcp` (or `/mcp/readonly`) exactly as you would point it at
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

## Troubleshooting the base URL

| Symptom (request that reaches the proxy) | Cause | Fix |
| --- | --- | --- |
| `404` on `POST /messages` | You used the bare host with a client that appends only `/messages`. | Add `/v1`: `…:8788/v1`. |
| `404` on `POST /v1/v1/messages` | You added `/v1` for a client that already appends `/v1/messages`. | Drop `/v1`: use `…:8788`. |
| `404 not_found_error` on `count_tokens` | The model is served by translation to Responses, which has no token-counting endpoint. | Expected; the client falls back to a local estimate. |
| A GPT model answers as Claude | `COPILOT_ANTHROPIC_MODEL` is pinned, so every request is rewritten to it. | Unset it to use the discovered catalog. |
| `403 permission_error` | Non-loopback `Host`, cross-site request, or a non-loopback `Origin`. | Use `127.0.0.1`/`localhost`; don't proxy from a browser page on another origin. |
| `401 authentication_error` | The proxy could not acquire a Copilot token on the request. | Re-authenticate (`gh auth login` / Copilot CLI) or set `GITHUB_COPILOT_TOKEN`. |

## Environment variables

| Variable | Default | Purpose |
| --- | --- | --- |
| `COPILOT_ANTHROPIC_MODEL` | (discovered from `/models`) | Pins every request to this single Copilot model id and skips discovery entirely. Leave unset to serve the full catalog — see the note under "Which models are served". |
| `COPILOT_ANTHROPIC_PORT` | `8788` | Loopback listen port. |
| `COPILOT_ANTHROPIC_BASE_URL` | `https://api.enterprise.githubcopilot.com` | Copilot host root (for non-enterprise hosts). |
| `COPILOT_ANTHROPIC_IDLE_TIMEOUT_SECONDS` | `180` | Per-request idle timeout, reset after each streamed upstream read. The total exchange has no deadline, so long generations are not cut off; this only fires when the upstream produces *nothing* for the whole window. |
| `COPILOT_ANTHROPIC_KEEPALIVE_SECONDS` | `15` | While an SSE upstream is silent, emit a downstream SSE keep-alive this often so the client's own read timeout does not fire mid-generation. Keep-alives don't reset the idle timeout above. Set `0` to disable. |
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
(`"The use of the web search tool is not supported."`). On the passthrough route this rejection
passes straight through as an upstream 400; on the translated route the tool is dropped before the
request is sent (only tools carrying an `input_schema` are mapped), so the request succeeds without
web search.

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

The translated route avoids both by construction: it builds a new Responses body from an explicit
allowlist rather than patching the inbound one, so `betas`, `cache_control`, `metadata` and server
tools are dropped without needing to be enumerated.

## Non-goals (intentionally not implemented)

- **No response-body rewriting on the passthrough routes.** The response body and the SSE
  `message_start` event carry whatever model id was actually sent upstream — never rewritten back to
  the client's requested id. This is accepted for raw-passthrough fidelity.
- **No 200K → 1M context fallback / model routing.** Context-length errors pass through unchanged.
- **No refresh-on-401 / token invalidation.** A request-path token failure maps to a local
  `authentication_error`; re-authenticate out of band.
- **No synthetic `count_tokens` estimator.** `count_tokens` is best-effort pass-through for Claude
  models; an unsupported upstream (404/405) is normalized to an Anthropic `not_found_error`.
- **No inbound auth, TLS, or CORS** beyond the loopback + host/cross-site guard.
- **No MCP session bookkeeping or resumability logic.** The proxy relays `Mcp-Session-Id` and
  `Last-Event-ID` verbatim but never inspects, persists, or validates them — session lifecycle is
  entirely between the client and Copilot.

## Live smoke tests

`tests/CopilotLive.Tests/` is outside `LmDotnetTools.sln`, so CI never runs it. Those tests hit the
real Copilot backend and are run by hand; they cover the things a fixture cannot prove — the live
spelling of every Responses SSE event the stream translator switches on, the shape of
`incomplete_details` on a truncated reply, and whether reasoning summaries arrive unrequested.

```bash
dotnet test tests/CopilotLive.Tests/CopilotLive.Tests.csproj
```

They skip cleanly with an explanatory message when no Copilot credential is present.
