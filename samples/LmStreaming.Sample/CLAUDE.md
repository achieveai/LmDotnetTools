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
