# CopilotAnthropicProxy.Sample

A thin, **loopback-only** reverse proxy that speaks the **Anthropic Messages API** on the inbound
side and forwards every request to **GitHub Copilot** on the outbound side. It lets a developer who
has a GitHub Copilot entitlement (but no Anthropic API key) drive **Claude Code** — or the in-house
`AnthropicClient` — on a Copilot-hosted Claude model by pointing `ANTHROPIC_BASE_URL` at this proxy.

What it does, end to end:

1. Accepts `POST /v1/messages` (streaming and non-streaming), `POST /v1/messages/count_tokens`, and
   `GET /v1/models`.
2. Rewrites **only** the JSON `model` field of the request body to a configured Copilot Claude (Opus)
   id. Everything else — `system` blocks, `cache_control`, `thinking`, `tools`, betas, and unknown
   fields — is preserved verbatim (raw `JsonNode` swap, never a typed DTO).
3. Attaches Copilot auth + tracking headers using the proven `GithubCopilotProvider` transport
   (`CopilotHttpClientFactory` / `CopilotHeadersHandler` / the CLI credential token provider).
4. Streams the upstream Server-Sent-Events response straight back to the client as **raw bytes**
   (no parsing, no buffering, incremental flush) and passes status codes and rate-limit headers
   through unchanged. If the upstream stream fails mid-flight the proxy does **not** fabricate any
   terminal frames: it returns a `502` when nothing has been sent yet, otherwise it stops and closes
   the (now incomplete) stream — the client detects the truncation from the missing `message_stop`,
   exactly as it would if the upstream connection had dropped directly.

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

On startup the proxy logs the resolved model and the listen address, e.g.:

```
CopilotAnthropicProxy listening on http://127.0.0.1:8787 -> https://api.enterprise.githubcopilot.com (model: <resolved-opus-id>)
```

### Choosing the model

The outbound model id is resolved at startup in this order:

1. `COPILOT_ANTHROPIC_MODEL` if set (wins outright).
2. Otherwise the proxy queries Copilot `GET /models` and picks the Claude id containing `opus`.
3. Otherwise it **fails fast** and logs the available Claude model ids so you can pick one and set
   `COPILOT_ANTHROPIC_MODEL` explicitly.

`GET /v1/models` on the proxy returns an Anthropic-shaped stub advertising the single resolved id.

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
value is also ignored — the proxy always rewrites it to the configured Copilot model.

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
| `COPILOT_ANTHROPIC_MODEL` | (resolved from `/models`) | Outbound Copilot model id. Wins over `/models` resolution. |
| `COPILOT_ANTHROPIC_PORT` | `8787` | Loopback listen port. |
| `COPILOT_ANTHROPIC_BASE_URL` | `https://api.enterprise.githubcopilot.com` | Copilot host root (for non-enterprise hosts). |
| `COPILOT_ANTHROPIC_IDLE_TIMEOUT_SECONDS` | `120` | Per-request idle timeout, reset after each streamed read. The total exchange has no deadline, so long generations are not cut off. |
| `COPILOT_ANTHROPIC_ENABLE_DEVICE_FLOW` | `false` | When truthy, allow an interactive GitHub device-flow login at startup (composite provider). Off by default — the request path never blocks on device flow. |

## `web_search` caveat (LmStreaming.Sample validation)

`samples/LmStreaming.Sample` in `anthropic` mode can enable the Anthropic server-side
`AnthropicWebSearchTool`. The GitHub Copilot backend **rejects** that tool shape with HTTP 400
(`"The use of the web search tool is not supported."`). This proxy does **not** special-case it — the
rejection passes straight through as an upstream 400.

To validate against the proxy, run a flow that does **not** enable web search. The clean way to do
that in `LmStreaming.Sample` is to select (or define) a chat **mode with an empty `EnabledTools`
list**: `ModeToolFilter.FilterBuiltInTools` returns `null` for an empty tool set
(`Services/ModeToolFilter.cs`), which strips `AnthropicWebSearchTool` before the request is built.

## Non-goals (intentionally not implemented)

- **No response-body rewriting.** The response body and the SSE `message_start` event carry the
  rewritten (Opus) model id, not the requested id. This is accepted for raw-passthrough fidelity.
- **No `anthropic-beta` allowlist filter.** Beta values are forwarded as-is; an unknown beta that
  Copilot rejects surfaces as an upstream 400 passthrough.
- **No 200K → 1M context fallback / model routing.** Context-length errors pass through unchanged.
- **No refresh-on-401 / token invalidation.** A request-path token failure maps to a local
  `authentication_error`; re-authenticate out of band.
- **No synthetic `count_tokens` estimator.** `count_tokens` is best-effort pass-through; an
  unsupported upstream (404/405) is normalized to an Anthropic `not_found_error`.
- **No inbound auth, TLS, or CORS** beyond the loopback + host/cross-site guard.
