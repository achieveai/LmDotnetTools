# Resilient Stream Delivery and Agent Waiting Implementation Plan

**Status:** Implemented — shipped in `94969b20` (#278). See `src/LmMultiTurn/Delivery/ReplayMessagePolicy.cs`, `Messages/StreamRecoveryMessage.cs`, `Recovery/TurnAttemptState.cs` and `ClientApp/src/composables/streamResync.ts`.

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make large LmStreaming conversations recover automatically from slow-consumer/socket loss using canonical full messages, recover interrupted provider turns without repeating completed work, and give Workspace Agent an unambiguous agent-wait surface.

**Architecture:** Live deltas remain best-effort animation; persisted complete messages plus a small canonical/control bridge are the recovery source of truth. A dropped socket triggers a single-flight REST-first rehydrate and live-edge resubscribe. Provider interruptions either abandon a fragment-only generation and retry once, or preserve completed messages and continue through an internal sentinel. Workspace Agent resolves collaboration mode by explicit override or a mode-specific default.

**Tech Stack:** .NET 9/C#, ASP.NET Core WebSockets, `IAsyncEnumerable<IMessage>`, Vue 3/TypeScript, Vitest, xUnit, Playwright, Serilog JSONL.

## Global Constraints

- Complete messages are canonical; delta/update fragments are never required for catch-up.
- Publisher fan-out must remain non-blocking and memory-bounded.
- Do not add unbounded subscriber queues or replay buffers.
- Every retry/continuation gets a new generation ID.
- Never execute a completed tool effect twice.
- Allow at most one automatic provider-stream recovery per logical input.
- User cancellation never triggers recovery.
- Collaboration defaults on only for Workspace Agent when `AgentCollaboration:Enabled` is unspecified; explicit true/false always wins.
- `WaitAgent` exists only when collaboration is disabled; collaboration mode continues to expose `WaitForAgents` without a singular alias.
- Debug-or-higher logs must not include prompt/message/tool-result content.
- Preserve the existing uncommitted empty-output fix in `src/OpenAiResponsesProvider/Agents/OpenAiResponsesAgent.cs` and `tests/OpenAiResponsesProvider.Tests/OpenAiResponsesAgentRunIdTests.cs`.
- Before each task, verify those protected files still contain the current working-tree change; do not reset, checkout, or overwrite unrelated work.
- Do not commit unless the user explicitly authorizes commits. If authorized, use the suggested commit checkpoint without AI/co-author signatures.

## File Structure

### New files

- `src/LmMultiTurn/Delivery/ReplayMessagePolicy.cs` — classifies canonical/control versus expendable live messages.
- `src/LmMultiTurn/Messages/StreamRecoveryMessage.cs` — content-free control frame identifying subscriber resync or abandoned generation.
- `src/LmMultiTurn/Recovery/TurnAttemptState.cs` — records canonical output, trailing fragments, tool tasks, and recovery count for one provider attempt.
- `src/LmMultiTurn/Messages/InterruptedTurnResume.cs` — typed internal resume metadata carried by `ResumeSentinel`.
- `samples/LmStreaming.Sample/ClientApp/src/composables/streamResync.ts` — single-flight, epoch-guarded REST-first resynchronization helper.

### Modified server/library files

- `src/LmMultiTurn/MultiTurnAgentBase.cs` — canonical-only replay and typed subscriber termination reason.
- `src/LmMultiTurn/MultiTurnAgentLoop.cs` — attempt tracking, safe retry/continuation, and tool-task settlement.
- `src/LmMultiTurn/Messages/ResumeSentinel.cs` — carry interrupted-turn recovery metadata.
- `src/LmMultiTurn/SubAgents/SubAgentToolProvider.cs` — legacy `WaitAgent` descriptor/handler and clearer agent-ID guidance.
- `src/LmWorkflow/Tools/StartWorkflowToolProvider.cs` — `GetWorkflows`, known-ID recovery hints, and stronger workflow-ID guidance.
- `samples/LmStreaming.Sample/Configuration/AgentCollaborationHostOptions.cs` — nullable explicit override and mode-aware resolution.
- `samples/LmStreaming.Sample/Program.cs` — resolve Workspace Agent collaboration default and pass it to the loop.
- `samples/LmStreaming.Sample/WebSocket/ChatWebSocketManager.cs` — emit content-free resync signal/close semantics for primary and sub-agent streams.
- `samples/LmStreaming.Sample/appsettings.json` — remove explicit global false so the mode default can operate; retain limits.

### Modified client files

- `samples/LmStreaming.Sample/ClientApp/src/api/wsClient.ts` — expose close metadata and parse `stream_recovery` control.
- `samples/LmStreaming.Sample/ClientApp/src/composables/useChat.ts` — invoke single-flight resync and discard abandoned partial generations.
- `samples/LmStreaming.Sample/ClientApp/src/components/tools/registry.ts` — recognize `startworkflowagent`.
- `samples/LmStreaming.Sample/ClientApp/src/types/messages.ts` — type stream recovery control if shared message typing requires it.

### Tests

- `tests/LmMultiTurn.Tests/MultiTurnAgentReplayTests.cs`
- `tests/LmMultiTurn.Tests/MultiTurnAgentLoopTests.cs`
- `tests/LmMultiTurn.Tests/SubAgents/SubAgentToolProviderTests.cs` (or the existing nearest provider test file if named differently)
- `tests/LmStreaming.Sample.Tests/WebSocket/ChatWebSocketManagerSubAgentTests.cs`
- `tests/LmStreaming.Sample.Tests/Configuration/AgentCollaborationHostOptionsTests.cs`
- `tests/LmWorkflow.Tests/StartWorkflowToolProviderTests.cs`
- `samples/LmStreaming.Sample/ClientApp/src/__tests__/composables/useChatResume.test.ts`
- `samples/LmStreaming.Sample/ClientApp/src/__tests__/utils/registrySummaries.test.ts`
- `tests/LmStreaming.Sample.Browser.E2E.Tests/Scenarios/StreamingResumeToolPillsTests.cs`

---

### Task 1: Protect Existing Work and Pin Replay Classification

**Files:**
- Create: `src/LmMultiTurn/Delivery/ReplayMessagePolicy.cs`
- Modify: `tests/LmMultiTurn.Tests/MultiTurnAgentReplayTests.cs`
- Protected: `src/OpenAiResponsesProvider/Agents/OpenAiResponsesAgent.cs`
- Protected: `tests/OpenAiResponsesProvider.Tests/OpenAiResponsesAgentRunIdTests.cs`

**Interfaces:**
- Produces: `internal static class ReplayMessagePolicy`
- Produces: `internal static bool IsCanonicalOrControl(IMessage message)`
- Consumes later: `MultiTurnAgentBase.PublishToAllAsync`

- [ ] **Step 1: Record the protected diff**

Run:

```powershell
git diff -- src/OpenAiResponsesProvider/Agents/OpenAiResponsesAgent.cs tests/OpenAiResponsesProvider.Tests/OpenAiResponsesAgentRunIdTests.cs
```

Expected: the empty `ResponseOutputTextDoneEvent` guard and its regression test are present. Save a SHA-256 of both files before edits and compare after every task.

- [ ] **Step 2: Write failing replay-policy tests**

Add a theory in `MultiTurnAgentReplayTests` that asserts:

```csharp
[Theory]
[MemberData(nameof(ReplayClassificationCases))]
public void Replay_policy_classifies_only_canonical_and_control_messages(
    IMessage message,
    bool expected)
{
    ReplayMessagePolicy.IsCanonicalOrControl(message).Should().Be(expected);
}
```

Cases must set `expected: false` for text/reasoning/tool-call/JSON update fragments and `expected: true` for run assignment, complete text/reasoning, complete tool call/result, notify, usage, and run completion.

- [ ] **Step 3: Run RED**

Run:

```powershell
dotnet test tests/LmMultiTurn.Tests/LmMultiTurn.Tests.csproj --filter "FullyQualifiedName~Replay_policy_classifies_only_canonical_and_control_messages" -p:BaseOutputPath=.logs/tb/bin/
```

Expected: FAIL because `ReplayMessagePolicy` does not exist.

- [ ] **Step 4: Implement the explicit type policy**

Use a type-pattern switch with a default of `true` for unknown complete message types, and explicit `false` for every known update/fragment type. Do not use name suffix matching.

```csharp
internal static bool IsCanonicalOrControl(IMessage message) => message switch
{
    TextUpdateMessage => false,
    ReasoningUpdateMessage => false,
    ToolCallUpdateMessage => false,
    ToolsCallUpdateMessage => false,
    JsonFragmentUpdateMessage => false,
    _ => true,
};
```

Use the actual JSON-fragment update type name from `src/LmCore/Messages`; if more update interfaces exist, enumerate them explicitly in the test data and switch.

- [ ] **Step 5: Run GREEN and protected-file hash check**

Run the filtered test, then the full `LmMultiTurn.Tests` project. Confirm protected file hashes are unchanged.

- [ ] **Step 6: Checkpoint**

If commits were authorized:

```powershell
git add src/LmMultiTurn/Delivery/ReplayMessagePolicy.cs tests/LmMultiTurn.Tests/MultiTurnAgentReplayTests.cs
git commit -m "test: define canonical replay message policy"
```

---

### Task 2: Replace Raw Replay with a Canonical/Control Bridge

**Files:**
- Modify: `src/LmMultiTurn/MultiTurnAgentBase.cs:38-64,1287-1458`
- Modify: `tests/LmMultiTurn.Tests/MultiTurnAgentReplayTests.cs`
- Use: `src/LmMultiTurn/Delivery/ReplayMessagePolicy.cs`

**Interfaces:**
- Consumes: `ReplayMessagePolicy.IsCanonicalOrControl(IMessage)`
- Produces: reconnect replay containing canonical/control messages only
- Preserves: live fan-out of all messages

- [ ] **Step 1: Rewrite existing replay expectations as RED**

Change `Subscriber_joining_mid_run_replays_buffered_messages_then_streams_live` so old deltas are absent from replay. Publish assignment, 10,001 text deltas, and one complete `TextMessage`; assert the subscriber first receives assignment and complete text, then receives a new live delta published after subscription.

Add a byte-limit test proving large deltas do not consume bridge bytes and a canonical message still enters the bridge.

- [ ] **Step 2: Run RED**

Expected: old implementation replays deltas and fails ordering/count assertions.

- [ ] **Step 3: Filter bridge insertion only**

In `PublishToAllAsync`, keep fan-out unchanged. Wrap only `_replayBuffer.Add(message)` and byte accounting in:

```csharp
if (_replayRunActive && ReplayMessagePolicy.IsCanonicalOrControl(message))
{
    // existing bounded bridge insertion
}
```

Keep run-assignment open/reset and run-completed clear semantics unchanged.

- [ ] **Step 4: Add bridge truncation observability test**

Prove the warning occurs only when canonical/control entries exceed a deliberately tiny test cap; raw deltas alone never set truncation.

- [ ] **Step 5: Run GREEN**

Run `MultiTurnAgentReplayTests`, then full `LmMultiTurn.Tests`.

- [ ] **Step 6: Checkpoint**

Suggested authorized commit: `fix: replay canonical messages instead of stream deltas`.

---

### Task 3: Signal Subscriber Resynchronization Explicitly

**Files:**
- Create: `src/LmMultiTurn/Messages/StreamRecoveryMessage.cs`
- Modify: `src/LmMultiTurn/MultiTurnAgentBase.cs:1287-1458`
- Modify: `samples/LmStreaming.Sample/WebSocket/ChatWebSocketManager.cs:419-587`
- Modify: `tests/LmMultiTurn.Tests/MultiTurnAgentReplayTests.cs`
- Modify: `tests/LmStreaming.Sample.Tests/WebSocket/ChatWebSocketManagerSubAgentTests.cs`

**Interfaces:**
- Produces: `StreamRecoveryMessage(ThreadId, RunId, GenerationId, StreamRecoveryReason)`
- Produces enum: `StreamRecoveryReason.SlowConsumer`
- WebSocket JSON discriminator: `stream_recovery`
- Client task consumes this control in Task 4.

- [ ] **Step 1: Write RED server tests**

Add a stalled-subscriber test whose enumerable ends with a `StreamRecoveryMessage` instead of silently completing. Add primary/sub-agent pump tests asserting serialization contains only discriminator, reason, thread/run/generation identifiers and no conversation content.

- [ ] **Step 2: Run RED**

Expected: subscriber currently ends cleanly and no recovery control exists.

- [ ] **Step 3: Introduce typed subscriber state**

Replace the dictionary value `Channel<IMessage>` with a focused private subscriber record containing the channel and last run/generation identity. On saturation:

1. remove subscriber from live fan-out;
2. make a reserved terminal recovery control observable even if the bounded channel is full (use a separately completed `TaskCompletionSource<StreamRecoveryMessage>` or a channel with one reserved terminal slot; do not block and do not write the control into a known-full queue);
3. complete the ordinary channel.

`SubscribeAsync` drains replay/live messages and then yields the terminal recovery control exactly once.

- [ ] **Step 4: Serialize and close deliberately**

`PumpMessagesToClientAsync` tracks whether a run-complete/done was seen. On `StreamRecoveryMessage`, send its content-free frame and close with a dedicated reason such as `resync_required`; do not emit `done`. Apply the same semantics to sub-agent streams.

- [ ] **Step 5: Run GREEN and EUII assertions**

Run replay tests and WebSocket manager tests. Assert exception/prompt/tool content cannot appear in the control frame or warning.

- [ ] **Step 6: Checkpoint**

Suggested authorized commit: `fix: signal slow-consumer resynchronization`.

---

### Task 4: Add Client Single-Flight REST-First Resynchronization

**Files:**
- Create: `samples/LmStreaming.Sample/ClientApp/src/composables/streamResync.ts`
- Modify: `samples/LmStreaming.Sample/ClientApp/src/api/wsClient.ts:331-337`
- Modify: `samples/LmStreaming.Sample/ClientApp/src/composables/useChat.ts:824-1002,1152-1242,1333-1465`
- Test: `samples/LmStreaming.Sample/ClientApp/src/__tests__/composables/useChatResume.test.ts`

**Interfaces:**
- Produces: `createStreamResyncCoordinator(deps): StreamResyncCoordinator`
- Produces method: `request(threadId: string, epoch: number, reason: string): Promise<void>`
- Produces method: `invalidate(): void`
- Consumes: existing `loadMessagesFromBackend`, `getRunState`, and subscribe-only `openStreamConnection`

- [ ] **Step 1: Write RED tests for clean and explicit closes**

Extend `useChatResume.test.ts` with:

```ts
it('rehydrates and resubscribes when the socket closes before done', async () => { ... })
it('coalesces repeated close callbacks into one resync', async () => { ... })
it('ignores a late close from an obsolete conversation epoch', async () => { ... })
it('clears loading when the run completed before run-state check', async () => { ... })
```

Simulate a clean `onClose` with no prior `onDone`, and a `stream_recovery` frame. Assert call order: REST load → run-state → new WebSocket.

- [ ] **Step 2: Run RED**

Run:

```powershell
npm --prefix samples/LmStreaming.Sample/ClientApp test -- --run src/__tests__/composables/useChatResume.test.ts
```

Expected: no automatic resync and `isLoading` remains true.

- [ ] **Step 3: Implement a coordinator, not recursive callbacks**

The coordinator stores one in-flight promise keyed by `(threadId, epoch)`, bounded attempt count, and cancellation/invalidated epoch. It calls injected async steps in strict order and clears its in-flight slot in `finally`.

- [ ] **Step 4: Wire socket lifecycle**

Track `doneReceived` per connection. In `onClose`, if the socket belongs to the current epoch, `doneReceived` is false, and the UI/run may still be active, request resync. On an explicit recovery frame, invalidate the socket and request the same single-flight operation.

- [ ] **Step 5: Repair partial generations**

Before REST rehydrate, clear only unfinalized merger accumulators for the disconnected socket epoch. Do not clear persisted/canonical display items. Use the existing stable identity merge when REST messages load.

- [ ] **Step 6: Add bounded retry test**

Make the replacement socket close repeatedly and prove the coordinator stops after the configured attempt count and surfaces one actionable error rather than looping.

- [ ] **Step 7: Run GREEN**

Run all ClientApp Vitest tests and `npm run type-check`.

- [ ] **Step 8: Checkpoint**

Suggested authorized commit: `fix: automatically resync dropped chat streams`.

---

### Task 5: Browser-Prove Large Conversation Recovery

**Files:**
- Modify: `tests/LmStreaming.Sample.Browser.E2E.Tests/Scenarios/StreamingResumeToolPillsTests.cs`
- Modify infrastructure only if required to expose a deliberately tiny subscriber capacity.

**Interfaces:**
- Consumes Tasks 2–4.
- Produces an end-to-end regression for primary and sub-agent views.

- [ ] **Step 1: Add a deterministic slow-consumer scenario**

Configure the test host with a channel capacity of 4 and scripted output containing more updates than capacity plus a final full message. Pause/slow browser consumption through the test seam rather than timing sleeps.

- [ ] **Step 2: Assert RED symptom**

Before the fix, expect the socket to close cleanly, no replacement connection, and loading to remain stuck. Confirm the test fails against current behavior.

- [ ] **Step 3: Assert final behavior**

After Tasks 2–4, assert:

- a replacement socket opens once;
- REST full messages are loaded;
- the final full message is visible exactly once;
- loading ends on completion;
- no error banner remains;
- the primary and sub-agent variants have parity.

- [ ] **Step 4: Run the filtered browser test and complete suite**

Use `-p:BuildClientApp=true` and an isolated `BaseOutputPath` under `.logs/`.

- [ ] **Step 5: Checkpoint**

Suggested authorized commit: `test: cover slow-consumer chat recovery`.

---

### Task 6: Introduce Turn Attempt State for Interrupted Streams

**Files:**
- Create: `src/LmMultiTurn/Recovery/TurnAttemptState.cs`
- Modify: `src/LmMultiTurn/MultiTurnAgentLoop.cs:1000-1060,1345-1468`
- Test: `tests/LmMultiTurn.Tests/MultiTurnAgentLoopTests.cs`

**Interfaces:**
- Produces: `TurnAttemptState.Observe(IMessage)`
- Produces properties: `HasCanonicalMessages`, `HasIncompleteUpdates`, `CompletedMessages`, `PendingToolTasks`, `TrailingGenerationId`
- Produces: `SettleToolTasksAsync(CancellationToken)`

- [ ] **Step 1: Write RED classification tests**

Feed update-only, completed reasoning, completed text, tool call/result, notify, and cancellation sequences into `TurnAttemptState`. Assert canonical versus incomplete state and that tool tasks are terminally accounted.

- [ ] **Step 2: Run RED**

Expected: type does not exist.

- [ ] **Step 3: Implement attempt observation**

Use explicit message type patterns. Completed messages are those returned downstream after join/finalization; update fragments never enter `CompletedMessages`. Track dispatched tool tasks by call ID and retain no prompt/tool-result content in diagnostics.

- [ ] **Step 4: Route `ExecuteTurnAsync` through attempt state**

Replace the local `hasToolCalls`/`pendingToolCalls` bookkeeping with the typed state while preserving existing behavior on normal completion.

- [ ] **Step 5: Run GREEN and full LmMultiTurn tests**

- [ ] **Step 6: Checkpoint**

Suggested authorized commit: `refactor: track provider turn attempt state`.

---

### Task 7: Retry Fragment-Only Attempts Once

**Files:**
- Modify: `src/LmMultiTurn/MultiTurnAgentLoop.cs`
- Create/modify test fixture in: `tests/LmMultiTurn.Tests/MultiTurnAgentLoopTests.cs`
- Use: `src/LmMultiTurn/Recovery/TurnAttemptState.cs`
- Use: `src/LmMultiTurn/Messages/StreamRecoveryMessage.cs`

**Interfaces:**
- Produces internal result: `TurnExecutionResult` with `CompletedNormally`, `RetryableInterruption`, and attempt state.
- Consumes: retryable transport classification (`HttpIOException`/`ResponseEnded`) without broad string-only retry of arbitrary exceptions.

- [ ] **Step 1: Write RED retry test**

Script provider attempt 1 to emit text updates then throw `HttpIOException(HttpRequestError.ResponseEnded)`. Attempt 2 emits a full text message. Assert:

- provider called twice;
- generation IDs differ;
- no partial attempt message is persisted;
- client receives abandon-generation control;
- final text appears once.

- [ ] **Step 2: Add RED ceiling/cancellation tests**

A second interruption must complete with classified error. Cancellation must call provider once and propagate normal cancellation semantics.

- [ ] **Step 3: Implement one bounded retry**

Snapshot original request history before attempt 1. On retryable interruption with zero canonical messages and recovery count zero, publish abandon control, mint a new generation, and invoke the provider again from the original snapshot. Do not add a user message or a run-visible new input.

- [ ] **Step 4: Run GREEN**

Run filtered tests and full `LmMultiTurn.Tests`.

- [ ] **Step 5: Checkpoint**

Suggested authorized commit: `fix: retry fragment-only interrupted turns once`.

---

### Task 8: Preserve Completed Work and Continue Internally

**Files:**
- Create: `src/LmMultiTurn/Messages/InterruptedTurnResume.cs`
- Modify: `src/LmMultiTurn/Messages/ResumeSentinel.cs`
- Modify: `src/LmMultiTurn/MultiTurnAgentLoop.cs:919-1060,1845-2100,2740-2800`
- Test: `tests/LmMultiTurn.Tests/MultiTurnAgentLoopTests.cs`

**Interfaces:**
- Produces: `InterruptedTurnResume(InterruptedRunId, InterruptedGenerationId, RecoveryCount)`
- `ResumeSentinel` gains optional `InterruptedTurnResume? InterruptedTurn`
- Internal provider instruction: fixed content-free framework text, not a visible/persisted user bubble.

- [ ] **Step 1: Write RED completed-reasoning continuation test**

Attempt 1 emits completed reasoning, partial text updates, then `ResponseEnded`. Assert reasoning persists, partial text does not, and attempt 2 begins as an internal continuation with a new generation.

- [ ] **Step 2: Write RED completed-tool test**

Attempt 1 emits a complete tool call, its handler executes once, then transport fails. Assert the tool task is awaited, its result persists once, and the continuation sees that result without re-executing the tool.

- [ ] **Step 3: Write RED visible-effect tests**

Use NotifyClient/AskUserQuestion test doubles to prove completed effects persist once and continuation does not replay them.

- [ ] **Step 4: Extend sentinel and run-loop handling**

Enqueue the interrupted-turn sentinel after `SettleToolTasksAsync`. The next internal turn prepends/attaches a framework continuation instruction to the provider request only; it is not added to `ConversationHistory` or rendered by the client.

- [ ] **Step 5: Enforce one recovery per logical input**

Carry recovery count through retry and continuation. A second interruption completes the run with a stable `stream_interrupted_after_recovery` classification.

- [ ] **Step 6: Run GREEN**

Run focused tests, all deferred-tool tests, and full `LmMultiTurn.Tests`.

- [ ] **Step 7: Checkpoint**

Suggested authorized commit: `fix: continue interrupted turns from completed history`.

---

### Task 9: Default Collaboration On for Workspace Agent Only

**Files:**
- Modify: `samples/LmStreaming.Sample/Configuration/AgentCollaborationHostOptions.cs`
- Modify: `samples/LmStreaming.Sample/Program.cs:1503-1515,1939-1944`
- Modify: `samples/LmStreaming.Sample/appsettings.json:56-65`
- Test: `tests/LmStreaming.Sample.Tests/Configuration/AgentCollaborationHostOptionsTests.cs`
- Add focused Program helper tests in the nearest existing `Program*Tests.cs` file.

**Interfaces:**
- `AgentCollaborationHostOptions.Enabled` becomes `bool?`.
- Produces: `AgentCollaborationOptions? ResolveForMode(bool defaultEnabled)`.
- `ToCollaborationOptions()` may delegate to `ResolveForMode(defaultEnabled: false)` for backward-compatible callers.

- [ ] **Step 1: Write RED configuration matrix**

Assert unspecified + Workspace Agent default yields options; unspecified + default mode yields null; explicit false always yields null; explicit true always yields options.

- [ ] **Step 2: Run RED**

- [ ] **Step 3: Implement nullable override**

```csharp
var enabled = Enabled ?? defaultEnabled;
if (!enabled) return null;
```

Keep all existing limit and visibility validation.

- [ ] **Step 4: Resolve by mode in Program**

Pass `defaultEnabled: isWorkspaceMode` at root collaboration construction. Do not default collaboration on for Workflow Author or ordinary modes. Remove the checked-in explicit `Enabled: false` property while keeping limits and explanatory comment.

- [ ] **Step 5: Run GREEN and sample tests**

- [ ] **Step 6: Checkpoint**

Suggested authorized commit: `feat: default Workspace Agent collaboration on`.

---

### Task 10: Add Legacy `WaitAgent` Only When Collaboration Is Off

**Files:**
- Modify: `src/LmMultiTurn/SubAgents/SubAgentToolProvider.cs:61-100,400-500,1100-1270`
- Test: locate or create `tests/LmMultiTurn.Tests/SubAgents/SubAgentToolProviderTests.cs`

**Interfaces:**
- New constant: `WaitAgentToolName = "WaitAgent"`
- Handler args: `{ "agent_id": string, "timeout_seconds"?: integer }`
- Returns terminal observation/result, or running/timed-out status; error code `unknown_agent` includes known IDs.

- [ ] **Step 1: Write RED surface tests**

No collaboration: exact surface includes `Agent`, `SendMessage`, `CheckAgent`, `WaitAgent`. Collaboration: exact surface contains `WaitForAgents` and excludes `WaitAgent`.

- [ ] **Step 2: Write RED behavior tests**

Cover completion, failure, timeout, unknown ID with valid-ID hints, and cancellation. Use manager observation primitives; do not poll with sleeps.

- [ ] **Step 3: Implement descriptor and handler**

Reuse the existing terminal observation/wait primitive behind `WaitForAgents`/`ObserveCompletionAsync`; do not implement a second polling loop. Description must say: “Use an `agent_id` returned by `Agent`; do not pass workflow IDs.”

- [ ] **Step 4: Run GREEN and full LmMultiTurn tests**

- [ ] **Step 5: Checkpoint**

Suggested authorized commit: `feat: add legacy blocking WaitAgent tool`.

---

### Task 11: Add Workflow Discovery and Corrective ID Guidance

**Files:**
- Modify: `src/LmWorkflow/Tools/StartWorkflowToolProvider.cs:32-90,200-240,350-430`
- Modify: `src/LmWorkflow/WorkflowManager.cs:165-168,745+`
- Test: `tests/LmWorkflow.Tests/StartWorkflowToolProviderTests.cs`

**Interfaces:**
- New tool: `GetWorkflows` with no parameters.
- Uses: existing `WorkflowManager.ListRuns()`.
- Existing tool names remain unchanged.

- [ ] **Step 1: Write RED exact-surface and discovery tests**

Update expected tools to `StartWorkflowAgent`, `GetWorkflows`, `CheckWorkflow`, `WaitWorkflow`. Start two workflows and assert discovery returns their IDs/objectives/statuses.

- [ ] **Step 2: Write RED guidance tests**

Unknown Check/Wait result must contain known IDs. Descriptions must state workflow IDs are unrelated to agent IDs and must be the ID supplied to `StartWorkflowAgent`.

- [ ] **Step 3: Implement discovery and shared unknown-ID formatter**

Add one helper in `StartWorkflowToolProvider` that reads `ListRuns()`, sorts IDs ordinally, and appends a bounded valid-ID hint. Do not silently cap without saying the list is partial; if bounded, include `showing N of M`.

- [ ] **Step 4: Run GREEN and full LmWorkflow tests**

- [ ] **Step 5: Checkpoint**

Suggested authorized commit: `feat: make workflow IDs discoverable`.

---

### Task 12: Fix Workflow Tool Rendering

**Files:**
- Modify: `samples/LmStreaming.Sample/ClientApp/src/components/tools/registry.ts`
- Modify: `samples/LmStreaming.Sample/ClientApp/src/__tests__/utils/registrySummaries.test.ts`

**Interfaces:**
- Normalized `startworkflowagent` maps to the existing flow renderer.
- Historical `startworkflow` remains mapped for persisted fixtures.
- `getworkflows` maps to the flow renderer.

- [ ] **Step 1: Write RED registry test**

Assert `StartWorkflowAgent`, `GetWorkflows`, `CheckWorkflow`, and `WaitWorkflow` all resolve to the flow family and render workflow-oriented summaries.

- [ ] **Step 2: Run RED**

- [ ] **Step 3: Add normalized names to the existing family list**

Do not create a new renderer.

- [ ] **Step 4: Run GREEN, all Vitest tests, and type-check**

- [ ] **Step 5: Checkpoint**

Suggested authorized commit: `fix: render current workflow tool names`.

---

### Task 13: Final Cross-Subsystem Verification

**Files:**
- No production changes unless verification identifies a proven gap.
- Update design/plan only if actual interfaces differ from this plan.

**Interfaces:**
- Verifies Tasks 1–12 as one integrated behavior.

- [ ] **Step 1: Run focused .NET suites with isolated outputs**

```powershell
dotnet test tests/LmMultiTurn.Tests/LmMultiTurn.Tests.csproj -p:BaseOutputPath=.logs/final/lm-multiturn/bin/
dotnet test tests/LmWorkflow.Tests/LmWorkflow.Tests.csproj -p:BaseOutputPath=.logs/final/lm-workflow/bin/
dotnet test tests/LmStreaming.Sample.Tests/LmStreaming.Sample.Tests.csproj -p:BaseOutputPath=.logs/final/sample/bin/
```

Expected: zero failures and zero warnings attributable to changed code.

- [ ] **Step 2: Run client verification**

```powershell
npm --prefix samples/LmStreaming.Sample/ClientApp test -- --run
npm --prefix samples/LmStreaming.Sample/ClientApp run type-check
```

- [ ] **Step 3: Run browser scenarios**

Run filtered large-stream/resume tests first, then the full Browser E2E project with `BuildClientApp=true` and isolated `BaseOutputPath`.

- [ ] **Step 4: Run Release builds**

Build `LmMultiTurn`, `LmWorkflow`, and `LmStreaming.Sample` in Release with binary logs under `.logs/final/`. Do not solution-wide-format.

- [ ] **Step 5: Cold real-app smoke without disturbing existing instances**

Launch an isolated explicit-port instance. Drive:

1. a >10,000-delta response ending in one full message;
2. forced slow-consumer eviction and automatic resync;
3. fragment-only `ResponseEnded` then successful retry;
4. completed-reasoning interruption then internal continuation;
5. Workspace Agent tool list showing collaboration defaults;
6. explicit collaboration-off instance showing `WaitAgent` but not `WaitForAgents`.

Query structured logs with DuckDB for `stream_recovery`, recovery count, generation IDs, and absence of duplicate tool execution.

- [ ] **Step 6: Verify protected work and diff hygiene**

Confirm the two pre-existing empty-output files still contain their original changes, `git diff --check` passes, and no unrelated files changed.

- [ ] **Step 7: Final checkpoint**

Only if the user authorized commits, create the final commit(s) with ordinary project-style messages and no AI/co-author signatures.
