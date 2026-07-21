# Playwright manual smoke scripts

Single-call Playwright scripts for **manual/exploratory** UI verification of the LmStreaming chat
client. Each script drives a whole flow and returns structured JSON — **run it in ONE call**, do NOT
re-drive the UI with snapshot→act→screenshot loops (that is slow and token-heavy).

## Run

With the app running (dev instance: backend `:5098` + Vite `:5273` — adjust `BASE` in the script if
your ports differ), invoke via the Playwright MCP:

```
browser_run_code_unsafe({ filename: "samples/LmStreaming.Sample/playwright-scripts/provider-switch.mjs" })
```

The result is `{ pass, failures, steps }`. `pass:true` means every assertion held; otherwise `failures`
lists the failing step names and each `steps[].detail` has the observed values.

## Scripts

| Script | Feature under test |
|--------|--------------------|
| `provider-switch.mjs` | Switch a conversation's provider when idle; selector locked (disabled) while streaming; no permanent lock badge; switch persists + recreates the agent. |
| `queue-button.mjs` | Blue **Queue** button replaces red Stop while streaming when the composer has text; clicking Queue clears the box and enqueues the message. |
| `usage-banner.mjs` | Token-usage banner (#196): single turn → Total 150/In 100/Out 50; two turns accumulate to 300; reload restores from the persisted aggregate; a sub-agent delegation folds the descendant's tokens into the persisted aggregate (600) visible via the REST usage endpoint + on reopen. |

## Conventions (so these stay fast + reliable)

- **File format:** a single self-contained `async (page) => { … return { pass, failures, steps } }`
  arrow function. **No trailing `;`** after it — the runner wraps the file as an expression.
- **Assert only DETERMINISTIC, browser-observable state** (DOM/`data-testid`, `/api/*` reads). Exact
  HTTP status codes and timing-sensitive races (e.g. 409-while-streaming) belong in the deterministic
  C# suite (`tests/LmStreaming.Sample.Tests`), not a browser race against the fast mock.
- **Wait on state, not time** (`stop-button` visible = streaming; `send-button` visible = idle).
- **Mock providers only** (`test-anthropic` streams text with a wide window; `claude-mock` completes
  silently) — no real LLM calls.
- **Prompts live in [`../PromptExamples.md`](../PromptExamples.md)** → "Manual UI test prompts
  (conversation UX)". Add new prompts there, not inline.
- **Add a script per recurring manual case** instead of re-driving by hand. Promote any keeper check
  into `tests/LmStreaming.Sample.Browser.E2E.Tests/Scenarios/*.cs` when it should run in CI.

See `../CLAUDE.md` → "UI / browser testing" for the full policy and the `data-testid` selector contract.
