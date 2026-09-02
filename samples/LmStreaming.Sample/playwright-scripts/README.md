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
| `subagent-tabs.mjs` | Sub-agent center-pane tabs: `main` + one colored tab per background-spawned sub-agent; distinct colors; selecting a tab shows that child's transcript; parent Agent-call pills tinted to match. |
| `workflow-agent-run-and-view.mjs` | **WorkflowAgent end-to-end (REAL provider).** Launches a workflow, then asserts: a `kind:"workflow"` run tab + ≥1 nested delegate tab appear; the delegate inherited + used domain tools (its transcript has a successful `Read` — transparency); the **workflow AGENT conversation is viewable after completion** (`/messages` for the `workflow-{id}` thread is non-empty with `GetWorkflow`/`SetCurrentNode`/`Agent` orchestration + the ⚙ tab renders it, not "unavailable"); usage rolled up. |
| `mode-capability-tools.mjs` | **Modes editor manages the capability tools, and a CLONE actually gets them.** Asserts `/api/tools` lists the sandbox/sub-agent/workflow groups (sandbox listed LIVE from the gateway, so plugin tools appear); a copy of Workspace Agent opens pre-ticked with `sandbox:*`/`subagents:*` and warns about the per-conversation sandbox cost; narrowing it to `sandbox:Read` saves, round-trips on reopen, and keeps the other families and the mode's `web_search`; then RUNS the clone on a mock provider and reads `tools_list` back — `Read`/`Agent`/`StartWorkflowAgent` present, `Bash`/`PowerShell` gone. |
| `ask-question-notify-client.mjs` | Browser-hosted client tools (#246): `AskUserQuestion` answer/Skip resolve the deferred call and resume the parked run; a pending question survives a `?threadId=` reload and can still be answered; `NotifyClient` renders its own notification pill WHILE the run is still streaming (not only after completion) without enqueuing an extra user turn or forking an extra assistant run, and the notification rehydrates after a reload. |
| `subagent-ordinal-ids.mjs` | Two-phase manual validation of ordinal sub-agent ids (#705): `agent-1`, `agent-2` assigned in spawn order, scoped thread ids `subagent-{scope}-agent-N`, and the counter continues at `agent-3` after a backend restart. Phase 2 is gated by the `EXISTING_THREAD` constant. |
| `context-cost-panel.mjs` | **Context / cost / compaction panel (#685).** One row per framework-owned agent + a descendant-wide total (`GET /{threadId}/context` is authoritative; live `context_pressure` frames enrich). Asserts: a `test-anthropic` turn shows `N% of 200,000 tokens (measured|estimated)` with a `role=meter`, real token/cost values and a total row; a parent + 2 background sub-agents shows 3 rows whose capacity/cost/total equal the endpoint after a `?threadId=` reload (no provisional rows); the `test` provider (no window, no usage row) reads `Unknown window` / `No usage recorded` — never `0%` / `$0.0000`; the panel renders no prompt text; keyboard Tab/ArrowDown/Enter reach and open a row; at 390px the cells stack with no sideways scroll. Screenshots to `.logs/manual/pw685-*.png`. BASE `:5685` — adjust to your host. |

### Real-provider workflow scripts (exception to "mock only")

`workflow-agent-run-and-view.mjs` and other WorkflowAgent checks **cannot use the mock providers** — the
workflow tools are only wired in **Workspace Agent mode**, which requires a real provider + a live sandbox
gateway + a selected workspace. These scripts provision a `workspace-agent` conversation bound to
`gpt-5.6-luna` + the **LmDotnetTools** workspace (via `POST /api/conversations` — the race-free pattern),
then drive a real LLM. They are therefore **slow (minutes)** and mildly non-deterministic (the model may
retry the delegate a few times — all are surfaced, `pass` still holds). Swap `PROVIDER_ID` if
`gpt-5.6-luna` isn't available. **Durability across restart is NOT scriptable** (a script can't restart the
server): run the script, then manually stop+start the server, then re-`GET /{threadId}/subagents` — the
tabs must still return (they replay from `conversations/workflow-index/{threadId}.json`).

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
