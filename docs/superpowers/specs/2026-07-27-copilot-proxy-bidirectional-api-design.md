# Bidirectional API proxy for Copilot models

Date: 2026-07-27
Status: **approved design** (approved 2026-07-27 from device "Kay9")
Component: `samples/CopilotAnthropicProxy.Sample`

## Purpose

> "we just want to use this proxy to make claude, codex, open-code etc to work on top of all the
> models exposed through copilot backend APIs" — the user, verbatim

Point Claude Code, Codex CLI, and opencode at this proxy and let each drive any Copilot model,
regardless of which API dialect the client speaks. GitHub Copilot is the only backend.

This is a **sample**, not production infrastructure. Where a choice exists between a general
mechanism and the smallest thing that demonstrably works, take the smaller one.

### Success criteria

1. `opencode` (OpenAI Chat Completions) completes a streaming tool-calling turn against a Claude
   model and against `gpt-5.4`.
2. Codex CLI (OpenAI Responses) completes a streaming tool-calling turn against any GPT model.
3. Claude Code (Anthropic Messages) completes a streaming tool-calling turn against a
   Responses-only GPT model (`gpt-5.5`, `gpt-5.3-codex`, `gpt-5.6-*`).
4. Claude Code → Claude keeps its **exact** current byte-level behavior. No regression.

## Live-verified facts

Established by `tests/CopilotLive.Tests/CopilotChatCompletionsProbeTests.cs` (4/4 green, raw HTTP
against the real backend, outside the solution so CI never runs it). Full evidence in
`scratchpad/conversation_memories/copilot-bidirectional-api-proxy/07-live-verification.md`.

1. **Claude is servable over Copilot's `/chat/completions`** — 200 OK with streaming, fragmented
   `tool_calls`, `finish_reason: "tool_calls"`, and usage. `max_tokens` is accepted.
2. **`gpt-5.4` is servable over `/chat/completions`, but rejects `max_tokens`** with
   `400 invalid_request_body` — *"Use 'max_completion_tokens' instead."* Claude accepts
   `max_tokens` on the same endpoint, so this is **per-model, not per-endpoint**.
3. **Responses-only models reject `/chat/completions`** with
   `400 unsupported_api_for_model`.
4. **`finish_reason` and usage are present on the wire** for both passthrough dialects.
5. The live catalog holds 40 models and already differs from our captured fixture
   (`claude-opus-5`, `claude-sonnet-5`, `gpt-5.6-{luna,sol,terra}` are new; `claude-sonnet-4.5` is
   gone). ~19 entries advertise **no** endpoints at all, including `text-embedding-*`.

Fact 1 is the load-bearing one: it removes an entire translation layer that the earlier draft
assumed was mandatory.

## Routing

| Client (dialect) | Target model | Copilot endpoint | Work |
|---|---|---|---|
| Claude Code (Anthropic) | Claude | `/v1/messages` | passthrough — **unchanged today** |
| Claude Code (Anthropic) | GPT | `/responses` | **TRANSLATE** |
| opencode (Chat Completions) | Claude | `/chat/completions` | passthrough |
| opencode (Chat Completions) | `gpt-5.4`, `gpt-5-mini` | `/chat/completions` | passthrough + token-field rewrite |
| opencode (Chat Completions) | Responses-only GPT | — | 404 naming the servable alternatives |
| Codex CLI (Responses) | GPT | `/responses` | passthrough |
| Codex CLI (Responses) | Claude | — | deferred (see Out of scope) |

**Exactly one** direction needs real translation.

## Architecture: direct wire-to-wire translation

Translate Anthropic request JSON straight to Responses request JSON, and Responses SSE straight to
Anthropic SSE. **No LmCore `IMessage` middle, no `IAgent` reuse on the translated path.**

### Why not the unified LmCore middle

The earlier draft routed everything through LmCore's message model so that adding a dialect would
be "+1 reader +1 writer". That trade only pays off across many translation pairs. Live evidence
reduced the count to one, and the middle carries four defects verified against source:

| # | Defect | Evidence |
|---|---|---|
| 1 | Images are silently dropped on every Responses route | `src/OpenAiResponsesProvider/Agents/MessageMapper.cs:252-255` — bare `default: break;`, no `ImageMessage` case, and `input_image` appears nowhere in the provider |
| 2 | The two providers speak opposite `IMessage` dialects | `AnthropicAgent.cs:262-284` emits updates only (`ToolsCallMessage` branch is an empty body; the `TextMessage` yield is commented out) while `OpenAiResponsesAgent.cs:313,349` emits finalized `ToolsCallMessage` only |
| 3 | LmCore carries no termination reason | zero matches for `StopReason\|FinishReason\|stop_reason\|finish_reason` — so Anthropic `stop_reason` could not be derived |
| 4 | Responses agent defaults to WebSocket with shared mutable turn state | `CopilotResponsesAgentFactory.cs:41` defaults to `Transport.WebSocket`; `CopilotResponsesWebSocketClient.cs:42,45` holds a `SemaphoreSlim` turn gate and a mutable `_previousResponseId` on a shared client |

Wire-to-wire avoids all four rather than fixing them, and it **preserves** information the middle
would destroy: `finish_reason` and `incomplete_details` are right there on the Responses wire, so
Anthropic's `stop_reason` is derivable (defect 3 disappears rather than being worked around).

Rejected alternative recorded so it is not re-litigated: the unified middle would require changing
shared provider code that other consumers depend on, to serve one sample.

## HTTP surface

Inbound paths do not collide, so all dialects share **one base URL** — one value for the user to
configure in each client.

| Route | Behavior |
|---|---|
| `POST /v1/messages` | Claude → passthrough to `/v1/messages`; GPT → translate to `/responses` |
| `POST /v1/messages/count_tokens` | unchanged (Claude only; 400 for GPT — Responses has no equivalent) |
| `POST /v1/chat/completions` | passthrough to `/chat/completions` + token-field rewrite |
| `POST /v1/responses` | passthrough to `/responses` |
| `GET /v1/models` | union-shaped catalog (below) |
| `GET /health` | unchanged |
| `/mcp` | unchanged |

Each `POST` also binds its un-prefixed twin (`/chat/completions`, `/responses`) so a base URL with
or without `/v1` works. Both bindings call the same handler.

### The one real collision: `GET /v1/models`

Anthropic and OpenAI both define `GET /v1/models` with different body shapes. Emit a **union**
object per entry and let each client read the fields it knows:

```json
{
  "object": "list",
  "has_more": false,
  "data": [
    {
      "id": "gpt-5.5",
      "type": "model",
      "object": "model",
      "display_name": "GPT-5.5",
      "owned_by": "OpenAI",
      "created": 1735689600,
      "created_at": "2025-01-01T00:00:00Z"
    }
  ]
}
```

Extra fields are ignored by both clients; every real client keys off `id`. `BuildModelsStub` grows
this shape.

### Catalog

Replace `ParseMessagesCapableModelIds`' `/v1/messages`-only filter with: **include a model if it
advertises at least one endpoint this proxy can reach** (`/v1/messages`, `/chat/completions`, or
`/responses`), and its vendor is Anthropic, OpenAI, or Azure OpenAI.

- Models advertising **no** endpoints stay excluded. This matters: it is what keeps
  `text-embedding-*` from being offered as a chat model.
- Keep the fallback **per response, not per entry** (`Program.cs:502-514`): fall back to id-only
  parsing when *no* entry in the whole response carries `supported_endpoints`. Pinned by
  `ModelResolverTests.cs:68,122`. Since live responses do carry the metadata, the fallback stays
  dormant and the no-endpoint entries are correctly dropped.
- The catalog is fetched at startup and is the single source of truth for routing. **No hard-coded
  model ids** — the live-vs-fixture drift proves any static list is already stale.
- Gemini is excluded by user decision (2026-07-27), even though `/chat/completions` would reach it.

`COPILOT_ANTHROPIC_MODEL` demotes from an unconditional rewrite to a **fallback**: used only when
the client sends no model, or one absent from the catalog. Default port becomes **8788** (8787 is
occupied by a running proxy instance).

### Token-field rewrite

On `/chat/completions` only: if the resolved model's catalog `vendor` is `Anthropic`, keep
`max_tokens`; otherwise rename it to `max_completion_tokens`. Deterministic, offline-testable, and
it rides inside `ProxyModelResolver.TryRewriteModel`, which already parses and rewrites the body.

## Components

New, under `samples/CopilotAnthropicProxy.Sample/Translation/`:

| File | Responsibility | Depends on |
|---|---|---|
| `ModelRoute.cs` | model id → (backend endpoint, passthrough vs. translate, vendor) | catalog |
| `AnthropicToResponsesRequest.cs` | Anthropic request JSON → Responses request JSON. Pure function, no I/O | — |
| `ResponsesToAnthropicSse.cs` | Responses SSE event stream → Anthropic SSE event stream. Owns the block state machine | — |
| `ResponsesToAnthropicJson.cs` | non-streaming Responses response → Anthropic `Message` JSON | — |

Each is independently testable from string in / string out. `ModelRoute` is the only one that
touches configuration.

`Program.cs` changes: register the new routes, swap the catalog filter, demote pinning, extend
`BuildModelsStub`, add the token rewrite, and branch `/v1/messages` on `ModelRoute`.

### Request mapping — Anthropic → Responses

| Anthropic | Responses |
|---|---|
| `model` | `model` |
| `system` (string or block array) | flattened to text, sent as `instructions` (`ResponseCreateRequest.cs:18`; matches how `MessageMapper.cs:63` already folds system text) |
| `messages[]` | `input[]` items |
| content `text` | `{type:"input_text"}` (user) / `{type:"output_text"}` (assistant) |
| content `image` (base64 or url) | `{type:"input_image", image_url:"data:<media_type>;base64,<data>"}` |
| content `tool_use` | `{type:"function_call", call_id, name, arguments}` |
| content `tool_result` | `{type:"function_call_output", call_id, output}` |
| content `thinking` | dropped — see Limitations |
| `tools[].input_schema` | `tools[].parameters` (`{type:"function", name, description, parameters}`) |
| `tool_choice` `auto`/`any`/`tool`/`none` | `auto`/`required`/`{type:"function",name}`/`none` |
| `max_tokens` (required by Anthropic) | `max_output_tokens` (`ResponseCreateRequest.cs:34`) |
| `temperature`, `top_p`, `stream` | identical |
| `stop_sequences` | dropped — no Responses equivalent; see Limitations |

The sample emits this JSON directly rather than through `ResponseCreateRequest`, because the
provider's `ContentItem` has no image representation.

### SSE mapping — Responses → Anthropic

Event names taken from `src/OpenAiResponsesProvider/Models/ResponseEventTypes.cs`.

| Responses event | Anthropic output |
|---|---|
| `response.created` | `message_start` |
| `response.output_item.added` (message) | `content_block_start` `{type:"text"}` |
| `response.output_text.delta` | `content_block_delta` `{type:"text_delta"}` |
| `response.reasoning_summary_text.delta` | `content_block_delta` `{type:"thinking_delta"}` |
| `response.output_item.added` (`function_call`) | `content_block_start` `{type:"tool_use", id, name}` |
| `response.function_call_arguments.delta` | `content_block_delta` `{type:"input_json_delta"}` |
| `response.output_item.done` | `content_block_stop` |
| `response.completed` | `message_delta` (`stop_reason` + cumulative usage), then `message_stop` |
| `response.failed` | terminal SSE `error` event |

The writer owns Anthropic's block discipline: a monotonically increasing `index` per block, open
lazily on the first delta of a kind, close on kind change or stream end, and never leave a block
open at `message_stop`.

**`stop_reason` derivation** (from the wire, which is why the middle was dropped):

| Condition on `response.completed` | `stop_reason` |
|---|---|
| any output item is a `function_call` | `tool_use` |
| `incomplete_details.reason == "max_output_tokens"` | `max_tokens` |
| otherwise | `end_turn` |

Caveat: `incomplete_details` is **not** parsed anywhere in `src/OpenAiResponsesProvider`, so unlike
the event names above it is not grounded in our code — it comes from the Responses API contract.
P1 must confirm its exact shape against a live truncated response before relying on it. Until
confirmed, an unrecognized terminal shape falls back to `end_turn`, which is the safe default: a
wrong `max_tokens` would make a client believe output was truncated when it was not.

Anthropic requires `usage` in `message_delta` to be **cumulative**; the Responses `usage` object on
`response.completed` is already a total, so it maps directly.

## Error handling

Errors are shaped in the **inbound** dialect: Anthropic `{type:"error",error:{type,message}}` via
the existing `WriteAnthropicErrorAsync`, or OpenAI `{error:{message,type,param,code}}`.

| Condition | Status |
|---|---|
| Unknown model | 404 |
| Model not servable on the requested dialect | 404, message naming servable alternatives |
| Token acquisition failure | 401 |
| Upstream transport failure | 502 |
| Idle timeout | 504 |
| Client abort | no body; cancellation propagates |

Once response headers are committed the status can no longer change, so a mid-stream failure emits
a terminal SSE `error` event (Anthropic) or an error chunk followed by `[DONE]` (OpenAI).

The translated path does not flow through `ProxyHttp.ForwardAsync`, so it must **re-apply**
keep-alive and the reset-per-read idle-timeout CTS itself. This is a known trap: those behaviors
live in `ForwardAsync` today and would otherwise be silently lost on the new branch.

## Testing

1. **Request-mapper unit tests** (pure JSON → JSON): tools, `tool_choice` in all four forms,
   images, system-as-string and system-as-blocks, a multi-turn `tool_use`/`tool_result` loop,
   `max_tokens` → `max_output_tokens`.
2. **SSE-writer unit tests**: scripted Responses event sequences → exact expected Anthropic event
   and JSON assertions. Covers interleaved text/tool blocks, index monotonicity, all three
   `stop_reason` branches, and mid-stream `response.failed`.
3. **Integration tests** through the existing `ProxyWebAppFactory` + fake upstream — one per route.
   Reuse `GatedStream` to prove incremental flush and `CancellationObservingStream` to prove
   cancellation propagates.
4. **Live smoke** in `CopilotLive.Tests` beside the existing probe: one real turn per client shape.
   Opt-in, outside the solution, never in CI.

Tiers 1 and 2 carry the weight — a protocol translator's bugs are almost all in field mapping and
stream framing, and both tiers run offline in milliseconds.

## Delivery

**P0 — passthrough and catalog. No translation code.**
New `/v1/chat/completions` and `/v1/responses` routes, catalog filter widened, models union,
token-field rewrite, pinning demoted, port 8788. Delivers success criteria 1 and 2, and preserves
4. Low risk: it adds routes beside the existing one and reuses `ForwardAsync` wholesale.

**P1 — the translator.** `AnthropicToResponsesRequest` + `ResponsesToAnthropicJson` (non-streaming
first, easier to assert), then `ResponsesToAnthropicSse`. Delivers success criterion 3.

**P2 — docs and live smoke.** README base-URL forms per client, the security note, limitations.

Each phase is independently shippable and independently tested, per the user's "make sure each step
is well tested before taking bet."

## Limitations (documented, not solved)

- **Thinking signatures do not round-trip.** Anthropic `thinking` blocks carry a `signature` with
  no Responses equivalent, so a signed thinking block from a prior turn cannot be replayed to a GPT
  model. Inbound `thinking` blocks are dropped on the translated path. Responses reasoning summaries
  *are* surfaced outbound as `thinking_delta`, without a signature.
- **`stop_sequences` is dropped** on the translated path; the Responses API has no equivalent.
- **`count_tokens` is Claude-only** and returns 400 for GPT models.
- **Gemini is out of scope** by user decision, though `/chat/completions` would reach it.

## Out of scope

- Inbound `/responses` → Claude models (Codex CLI driving Claude). Deferred, and cheap when wanted:
  it is a Responses ↔ Chat Completions mapping between two OpenAI-family dialects, and Claude is
  live-proven on `/chat/completions`.
- Prompt caching, batch, and files APIs.
- Any change to `src/` providers. This work is confined to the sample plus its tests.
