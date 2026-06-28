# LmStreaming.Sample — Project Context

A full-stack demo of the streaming chat pipeline: an ASP.NET Core backend (`Program.cs`,
Kestrel on `http://localhost:5000`) that bridges `MultiTurnAgentLoop` to the browser over a
WebSocket (`/ws`), plus a Vue 3 + Vite chat client in `ClientApp/` (dev server on
`http://localhost:5173`, proxying `/api` and `/ws` to `:5000`).

## Running locally
```bash
# Backend (Development env pairs with the Vite dev server)
dotnet run --project samples/LmStreaming.Sample        # serves API + /ws on :5000
# Frontend (separate terminal)
npm --prefix samples/LmStreaming.Sample/ClientApp install
npm --prefix samples/LmStreaming.Sample/ClientApp run dev   # http://localhost:5173
```
Provider is chosen in the UI (header dropdown, `GET /api/providers`). Copilot-backed providers
("GPT-5.5 (Copilot)", "Sonnet/Haiku (Copilot)") need a resolvable Copilot/`gh` token, no API key.
Add `?record=1` to the URL to dump the WebSocket stream + LLM request/response to `recordings/`.

## UI / browser testing — READ THIS BEFORE WRITING BROWSER TESTS

There are **two** Playwright surfaces. Keep them aligned; do not invent a third.

1. **Authoritative automated regression — `tests/LmStreaming.Sample.Browser.E2E.Tests/`.**
   Runs the real chat client under headless Chromium against a **scripted SSE backend (no real
   LLM, deterministic, CI-safe)**. Look here FIRST for how to test UI behavior; copy an existing
   scenario rather than starting from scratch.
   - `Infrastructure/BrowserWebAppFactory.cs` — boots the app + scripted backend on an ephemeral port.
   - `Infrastructure/UiHelpers.cs` — the **selector contract** (extension methods over `data-testid`).
   - `Infrastructure/DomAssertions.cs` — await-don't-sleep helpers (`WaitForStreamIdleAsync`, …).
   - `Scenarios/*.cs` — `MultiTurnConversationTests`, `ModeSwitchingTests`, `ClaudeMockProviderTests`,
     `CancellationTests`, `ErrorHandlingTests`, … `dotnet test` runs them all (no exclusions).

2. **Manual / exploratory (Claude MCP Playwright) — `PlaywrightTestingGuide.md`.**
   Ready-to-run MCP scripts for ad-hoc checks. See its "Rationalization & fastest browser control"
   section for the canonical fast patterns and the selector cross-reference.

### Control the browser the FASTEST way possible
- **Select by `data-testid`, never by brittle CSS classes.** The stable contract (see `UiHelpers.cs`):
  `chat-input-textarea`, `send-button`, `stop-button`, `clear-button`, `error-banner`,
  `message-list`, `user-message-group`, `assistant-message-group`, `assistant-text` (the answer
  bubble — equals the `.text-bubble` element), `metadata-pill`, `thinking-pill`, `tool-call-pill`
  (carries `data-tool-name`), `mode-selector-button` / `mode-option-{id}`,
  `provider-selector-button` / `provider-option-{id}`.
- **Bulk-extract DOM state in ONE `browser_evaluate` call** instead of many snapshot/locator
  round-trips. e.g. count answer bubbles (the duplicate-bubble check):
  ```js
  () => [...document.querySelectorAll('[data-testid="assistant-text"]')].map(n => n.textContent.trim())
  ```
- **Wait on state, not time.** Stream finished = stop-button hidden + send-button visible
  (`WaitForStreamIdleAsync`); MCP: `browser_wait_for` on `[data-testid=send-button]` enabled.
  Never `Task.Delay`/fixed sleeps.
- Select a provider: click `provider-selector-button`, then `provider-option-<id>` (e.g.
  `provider-option-gpt-5.5`). A new chat resets the provider; pick it BEFORE the first send (the
  thread locks to whatever provider is active when the first message is sent).

## Message pipeline gotcha (why "duplicate bubble" regressions exist)
Providers stream text as `TextUpdateMessage` deltas **and then** a finalizing `TextMessage`
(same `generationId`). Two layers must treat that as ONE logical message:
- `MessageTransformationMiddleware` gives the finalizing message the **same `messageOrderIdx`** as
  its deltas (the client merge key is `kind-runId-generationId-messageOrderIdx`).
- `MessageUpdateJoinerMiddleware` must **not** also emit a synthesized "built" copy beside the
  provider's finalizing message (else history/persistence/reload store it twice).
Regression coverage (all CI-runnable): `LmCore.Tests/Middleware/MessageTransformationMiddlewareTests`,
`LmCore.Tests/Middleware/MessageUpdateJoinerMiddlewareTests`, and
`OpenAiResponsesProvider.Tests/OpenAiResponsesAgentTests` (drives the real mock OpenAI `/responses`
SSE stream through the pipeline). Add new duplicate-render checks at these levels first; a browser
scenario in `Browser.E2E.Tests` is the optional top-of-pyramid.

## Message identity across turns (why multi-turn thinking/text "collapses to the top")
The merge key `kind-runId-generationId-messageOrderIdx` has **TWO** client consumers, and BOTH
assume it uniquely identifies one logical message:
1. `useChat.getMergeKey` — the display/dedup key (collision ⇒ later turns overwrite the first block,
   pinned to first-insert position = the top).
2. `useMessageMerger` — the streaming accumulator key (collision ⇒ a later turn's `*_update` deltas
   **concatenate** onto the earlier turn's growing string; `finalize()` only runs per-run, never
   per-turn).

**Server-side root fix (shipped):** `MultiTurnAgentLoop.ExecuteRunTurnsAsync` now mints a **per-turn
(per-generation) `generationId`** — turn 1 reuses the run's id (so `run_assignment`'s advertised id
matches the first turn and single-turn runs are unchanged), turns 2+ get a fresh `Guid`. Each turn's
messages still share one id (so a turn's `tool_call` + `tool_call_result` group together — the
#105/H1 requirement) while turns stay distinct, so `(generationId, messageOrderIdx)` no longer
collides across turns. Pillbox grouping is arrival-order based on the client (not `generationId`), so
this doesn't change grouping; `run_assignment.generationId` / `run_completed.generationId` are not
load-bearing on the client (only logged / used to stamp error messages). Coverage:
`LmMultiTurn.Tests` → `ExecuteRunAsync_MultiTurn_AssignsDistinctGenerationIdPerTurn`. Backstory: prior
to this, `generationId` was **run-scoped** (constant across all turns — #105/H1 over-corrected from
per-message to per-run), while `messageOrderIdx` **resets every turn**, so turn N and N+1 collided;
tool calls survived (key carries `tool_call_id`), reasoning/text collapsed.

**Client defense (kept — defense-in-depth + backward compat):** a per-message **content turn epoch**
in `useChat` (bumps when text/reasoning resumes after an intervening tool call), threaded into BOTH
consumers — the merge key (`…-t{seq}`) and the merger accumulator key (`genId::t{seq}`). Do **not**
remove it: conversations **already persisted** before the server fix still carry the run-scoped
collision on disk, so the rehydration path relies on the epoch to render reloaded multi-turn
thinking/text distinctly. Regression coverage:
`ClientApp/src/__tests__/composables/useChat.test.ts` (multi-turn reasoning, text interleaving,
**merge-key invariant guard**, **rehydration multi-turn identity**) and `useMessageMerger.test.ts`
(per-turn `turnSeq` separation).

**Rules when touching message identity (these caused the regression once already):**
- Changing the scope of an identity field (e.g. `generationId`) requires auditing **every** consumer
  — there are two here, not one.
- Any fix that changes message identity MUST ship a **multi-turn (2+ turns) end-to-end client** test
  (`useChat`/`displayItems`). Unit/single-turn green ≠ correct; the bug is emergent across turns.
- "Consistent across the whole run" is a **two-field** claim: pinning `generationId` silently
  requires `messageOrderIdx` to be run-unique. State and test both halves.
