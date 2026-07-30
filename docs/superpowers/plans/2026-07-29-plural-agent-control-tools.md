# Plural Agent Control Tools Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace singular `CheckAgent` with batch `CheckAgents` and add non-destructive batch `WaitForAgents`, reducing polling/tool calls while steering models to wait at least 30 seconds for agents that may take 5–15 minutes.

**Architecture:** Add typed batch snapshot/wait behavior to `SubAgentManager`, composed from its existing ID-or-name resolution and non-destructive completion latch. Keep JSON schema, validation, snake_case serialization, summary counts, and model guidance in `SubAgentToolProvider`; migrate exact tool sets, prompts, tests, docs, and client rendering to plural names while preserving display-only support for historical `CheckAgent` messages.

**Tech Stack:** C#/.NET 8–9, xUnit, FluentAssertions, Moq, `System.Text.Json`, Vue 3/TypeScript, Vitest.

## Global Constraints

- This is a breaking active-tool rename: expose `CheckAgents` and `WaitForAgents`; do not expose or execute `CheckAgent` or a singular wait alias.
- Both tools accept required non-empty `targets: string[]`; each target resolves by canonical ID first, then exact case-sensitive readable name.
- Preserve input order and duplicates; unknown targets return per-entry `status: "not_found"` instead of failing the batch.
- `WaitForAgents.mode` is `all|any`, default `all`.
- `timeout_seconds` defaults to 900 and accepts only integers from 30 through 900 inclusive.
- Wait observation is non-destructive: timeout or observer cancellation must not cancel agents or change completion-relay flags.
- `CheckAgents` response and description remind the model to use `WaitForAgents`, wait at least 30 seconds, and expect 5–15 minute runs.
- Preserve client rendering for historical persisted `CheckAgent` calls.
- Follow RED→GREEN TDD and run the exact failing test before production edits in each task.
- Use `dotnet format whitespace`, not CSharpier, as the repository formatting gate.
- Do not add Co-Authored-By or any AI signature to commits/PRs.
- Do not commit or push unless the user explicitly authorizes it. Commit commands below are checkpoint suggestions to execute only after authorization.
- The approved spec `docs/superpowers/specs/2026-07-29-plural-agent-control-tools-design.md` is currently uncommitted and must be included with the implementation if commits are later authorized.

---

## File Structure

### Core types and behavior

- Modify `src/LmMultiTurn/SubAgents/SubAgentState.cs`
  - Add immutable result records/enums for resolved batch snapshots and wait outcomes, or place them adjacent to `SubAgentStatus` so manager and provider share one typed contract.
- Modify `src/LmMultiTurn/SubAgents/SubAgentManager.cs`
  - Extract typed snapshot construction from `TryPeek`.
  - Add ID-or-name batch resolution/check APIs.
  - Add concurrent, non-destructive `all|any` batch waiting over captured completion latches.
  - Preserve existing singular manager methods for non-tool consumers.
- Modify `src/LmMultiTurn/SubAgents/SubAgentToolProvider.cs`
  - Replace singular descriptor/handler with `CheckAgents`.
  - Add `WaitForAgents` descriptor/handler.
  - Parse/validate arrays, mode, and timeout; serialize typed manager results.
  - Update Agent/SendMessage guidance from polling to plural waiting.

### Core tests

- Create `tests/LmMultiTurn.Tests/SubAgents/SubAgentManagerBatchObservationTests.cs`
  - Manager-level resolution, ordering, duplicate, wait, timeout, cancellation, race, and notification-preservation tests.
- Modify `tests/LmMultiTurn.Tests/SubAgentToolProviderTests.cs`
  - Descriptor schemas, plural handler serialization, guidance, and recoverable validation errors.
- Modify `tests/LmMultiTurn.Tests/SubAgentIntegrationTests.cs`
  - Replace scripted background `CheckAgent` polling with plural check/wait behavior.
- Modify `tests/LmMultiTurn.Tests/SubAgentToolInheritanceExclusionTests.cs`
  - Update exact inherited/excluded tool names.

### Workflow/sample migration

- Modify `src/LmWorkflow/Prompts/ControllerSystemPrompt.cs`
  - Teach workflow controllers to batch checks and prefer `WaitForAgents` with long waits.
- Modify `src/LmWorkflow/README.md`
  - Document the plural observation tools.
- Modify `tests/LmWorkflow.Tests/WorkflowControllerToolRestrictionTests.cs`
  - Assert the restricted controller exposes both plural tools and no singular tool.
- Modify `samples/LmStreaming.Sample/Prompts.yaml`
- Modify `samples/LmStreaming.Sample/PromptExamples.md`
- Modify `samples/CodeReviewDaemon.Sample/Agents/LiveReviewAgentLoopFactory.cs`
  - Update operator/model guidance and comments.
- Modify `tests/LmStreaming.Sample.Browser.E2E.Tests/Scenarios/SubAgentRecursionGuardTests.cs`
- Modify `tests/LmStreaming.Sample.E2E.Tests/Scenarios/SubAgentBackgroundTests.cs`
  - Update exact tool names and plural background semantics.

### Client compatibility

- Modify `samples/LmStreaming.Sample/ClientApp/src/components/tools/registry.ts`
  - Register `checkagents` and `waitforagents` as agent-family tools while retaining `checkagent` for display-only persisted history.
- Modify `samples/LmStreaming.Sample/ClientApp/src/utils/agentColors.ts`
  - Resolve one canonical ID for singular historical calls and only color plural calls when exactly one canonical ID can be determined; do not guess across multiple targets.
- Modify `samples/LmStreaming.Sample/ClientApp/src/components/ToolPill.vue`
  - Update agent-family documentation/comment.
- Modify `samples/LmStreaming.Sample/ClientApp/src/__tests__/utils/toolName.test.ts`
- Modify `samples/LmStreaming.Sample/ClientApp/src/__tests__/utils/agentColors.test.ts`
  - Cover plural names and historical singular rendering.
- Preserve `samples/LmStreaming.Sample/ClientApp/src/__tests__/fixtures/persisted/checkagent.obj.json` unchanged as the backward-display fixture unless its test harness requires only an explanatory `_note` update.

---

### Task 1: Typed Batch Snapshots and Target Resolution

**Files:**
- Modify: `src/LmMultiTurn/SubAgents/SubAgentState.cs:7-29`
- Modify: `src/LmMultiTurn/SubAgents/SubAgentManager.cs:933-969,1371-1424`
- Create: `tests/LmMultiTurn.Tests/SubAgents/SubAgentManagerBatchObservationTests.cs`

**Interfaces:**
- Consumes: existing `_agents`, `_namesToIds`, `SubAgentState`, `SubAgentTurnSummary`, and `TryResolveAgentId` semantics.
- Produces:
  - `SubAgentObservationEntry` with original `Target`, nullable canonical identity, status, existing snapshot fields, and `IsFound`/terminal semantics.
  - `SubAgentObservationBatch` containing ordered `Entries` and summary counts.
  - `SubAgentManager.CheckAgents(IReadOnlyList<string> targets)`.
  - Existing `Peek`, `TryPeek`, and `KnownAgentIds` remain source-compatible.

- [ ] **Step 1: Add failing manager tests for ordered ID/name resolution**

Create `SubAgentManagerBatchObservationTests` using the existing mock setup from `SubAgentManagerObserveCompletionTests`. Add tests equivalent to:

```csharp
[Fact]
public async Task CheckAgents_PreservesInputOrderDuplicatesAndUnknowns()
{
    var manager = CreateManager();
    var first = await SpawnBackgroundAsync(manager, name: "first-reviewer");
    var second = await SpawnBackgroundAsync(manager, name: "second-reviewer");

    var batch = manager.CheckAgents([second, "first-reviewer", "missing", second]);

    batch.Entries.Select(x => x.Target)
        .Should().Equal(second, "first-reviewer", "missing", second);
    batch.Entries.Select(x => x.AgentId)
        .Should().Equal(second, first, null, second);
    batch.Entries[2].Status.Should().Be("not_found");
    batch.Requested.Should().Be(4);
    batch.NotFound.Should().Be(1);
}

[Fact]
public async Task CheckAgents_NameResolutionIsOrdinalCaseSensitive()
{
    var manager = CreateManager();
    await SpawnBackgroundAsync(manager, name: "ReviewOne");

    var batch = manager.CheckAgents(["reviewone"]);

    batch.Entries.Should().ContainSingle(x => x.Status == "not_found");
}
```

Use a controllable async stream so spawned agents can remain `Running` for snapshot assertions.

- [ ] **Step 2: Run the new tests and verify RED**

Run:

```powershell
dotnet test tests/LmMultiTurn.Tests/LmMultiTurn.Tests.csproj --filter "FullyQualifiedName~SubAgentManagerBatchObservationTests" --nologo
```

Expected: compile failure because `CheckAgents`, `SubAgentObservationEntry`, and `SubAgentObservationBatch` do not exist.

- [ ] **Step 3: Add typed immutable observation results**

Add focused records near `SubAgentStatus` in `SubAgentState.cs`. Use repository naming conventions and nullable fields for not-found entries. The concrete shape should include the existing singular fields without serializing JSON inside the manager:

```csharp
public sealed record SubAgentTurnSnapshot(
    string MessageType,
    string? ToolName,
    string? ToolArgsPreview,
    string? TextPreview,
    DateTimeOffset Timestamp);

public sealed record SubAgentObservationEntry
{
    public required string Target { get; init; }
    public string? AgentId { get; init; }
    public string? Name { get; init; }
    public required string Status { get; init; }
    public string? TemplateName { get; init; }
    public string? Task { get; init; }
    public IReadOnlyList<SubAgentTurnSnapshot> RecentTurns { get; init; } = [];
    public string? LastResult { get; init; }
    public bool SendToParentFailed { get; init; }
    public string? SendToParentError { get; init; }
    public bool IsFound => AgentId is not null;
    public bool IsTerminal => Status is "completed" or "error" or "stopped";
}

public sealed record SubAgentObservationBatch
{
    public required IReadOnlyList<SubAgentObservationEntry> Entries { get; init; }
    public int Requested => Entries.Count;
    public int Running => Entries.Count(x => x.Status == "running");
    public int Terminal => Entries.Count(x => x.IsTerminal);
    public int NotFound => Entries.Count(x => x.Status == "not_found");
}
```

If public API review shows these should be internal, expose the manager return type at the lowest visibility compatible with `SubAgentToolProvider` and tests; do not reintroduce anonymous JSON strings.

- [ ] **Step 4: Implement batch resolution and typed snapshot construction**

In `SubAgentManager`:

1. Reuse `TryResolveAgentId` (ID first, then exact name).
2. Add a private `Snapshot(string target, string agentId, SubAgentState state)` that copies the existing last-three-turn semantics.
3. Add `CheckAgents(IReadOnlyList<string> targets)` that returns one entry per input without deduplicating.
4. Keep `TryPeek` behavior by serializing one typed snapshot, so existing non-tool callers do not break during migration.

Do not add a global lock across agents; concurrent dictionaries/state reads already make each entry safe, and the contract is explicitly a best-effort ordered batch view.

- [ ] **Step 5: Run focused manager tests and existing peek tests**

Run:

```powershell
dotnet test tests/LmMultiTurn.Tests/LmMultiTurn.Tests.csproj --filter "FullyQualifiedName~SubAgentManagerBatchObservationTests|FullyQualifiedName~SubAgentToolProviderTests" --nologo
```

Expected: new snapshot tests pass; existing singular provider tests remain unchanged until Task 3.

- [ ] **Step 6: Review checkpoint**

Inspect `git diff --check` and confirm the manager contains no JSON field-name policy beyond preserving the legacy `TryPeek` wrapper.

- [ ] **Step 7: Commit checkpoint only if authorized**

```powershell
git add src/LmMultiTurn/SubAgents/SubAgentState.cs src/LmMultiTurn/SubAgents/SubAgentManager.cs tests/LmMultiTurn.Tests/SubAgents/SubAgentManagerBatchObservationTests.cs
git commit -m "feat(subagents): add typed batch status snapshots"
```

Do not add any co-author/signature trailer.

---

### Task 2: Non-Destructive Batch Waiting

**Files:**
- Modify: `src/LmMultiTurn/SubAgents/SubAgentState.cs`
- Modify: `src/LmMultiTurn/SubAgents/SubAgentManager.cs:1426-1449`
- Test: `tests/LmMultiTurn.Tests/SubAgents/SubAgentManagerBatchObservationTests.cs`

**Interfaces:**
- Consumes: `SubAgentManager.CheckAgents`, current `SubAgentState.Completion.Task`, and existing `ObserveCompletionAsync` non-destructive semantics.
- Produces:
  - `SubAgentWaitMode` (`All`, `Any`).
  - `SubAgentWaitOutcome` (`Completed`, `Partial`, `Timeout`, `NoValidTargets`).
  - `SubAgentWaitResult` with `Batch`, mode, outcome, timeout, and elapsed duration.
  - `SubAgentManager.WaitForAgentsAsync(targets, mode, timeout, cancellationToken)`.

- [ ] **Step 1: Add failing wait-all and wait-any tests**

Use `TaskCompletionSource`-backed async streams to control independent completions:

```csharp
[Fact]
public async Task WaitForAgentsAsync_AllWaitsForEveryValidAgentConcurrently()
{
    var (manager, first, firstGate, second, secondGate) = await SpawnTwoControlledAgentsAsync();
    var wait = manager.WaitForAgentsAsync(
        [first, second], SubAgentWaitMode.All, TimeSpan.FromSeconds(30), CancellationToken.None);

    firstGate.SetResult("first done");
    await Task.Yield();
    wait.IsCompleted.Should().BeFalse();

    secondGate.SetResult("second done");
    var result = await wait;

    result.Outcome.Should().Be(SubAgentWaitOutcome.Completed);
    result.Batch.Entries.Should().OnlyContain(x => x.IsTerminal);
}

[Fact]
public async Task WaitForAgentsAsync_AnyReturnsAfterFirstTerminalAndReportsPartial()
{
    var (manager, first, firstGate, second, _) = await SpawnTwoControlledAgentsAsync();
    var wait = manager.WaitForAgentsAsync(
        [first, second], SubAgentWaitMode.Any, TimeSpan.FromSeconds(30), CancellationToken.None);

    firstGate.SetResult("done");
    var result = await wait;

    result.Outcome.Should().Be(SubAgentWaitOutcome.Partial);
    result.Batch.Entries.Single(x => x.AgentId == second).Status.Should().Be("running");
}
```

- [ ] **Step 2: Add failing timeout/cancellation/unknown/race tests**

Cover:

- Timeout returns `Timeout` and the controlled agent can subsequently complete normally.
- Caller cancellation throws `OperationCanceledException` and the agent remains running.
- Unknown-only returns `NoValidTargets` immediately.
- Mixed unknown + valid obeys mode semantics.
- Already-terminal agents return without waiting.
- Completion racing timeout is represented by the final terminal snapshot.
- Parent completion notification remains enabled and fires exactly as before.
- Duplicate targets do not create destructive or serial observation behavior.

Use short manager-level test timeouts (for example 50–250 ms) because the public 30-second minimum belongs to the tool boundary, not the manager primitive.

- [ ] **Step 3: Run wait tests and verify RED**

Run:

```powershell
dotnet test tests/LmMultiTurn.Tests/LmMultiTurn.Tests.csproj --filter "FullyQualifiedName~SubAgentManagerBatchObservationTests" --nologo
```

Expected: compile failure for missing wait mode/outcome/result/API.

- [ ] **Step 4: Implement captured-latch concurrent waiting**

In `SubAgentManager.WaitForAgentsAsync`:

1. Resolve and snapshot all targets.
2. Return `NoValidTargets` if none resolve.
3. For each distinct valid non-terminal canonical ID, capture its current `Completion.Task` once. Deduplicate only the internal observation tasks; preserve duplicate response entries.
4. Create a linked timeout CTS separate from caller cancellation.
5. Start all observations before awaiting.
6. For `Any`, await `Task.WhenAny` of captured latches; for `All`, await `Task.WhenAll`.
7. Distinguish caller cancellation from timeout using the original token and timeout CTS state.
8. Re-run `CheckAgents(targets)` before returning.
9. Compute outcome from final snapshots and selected mode.

Do not call `AwaitCompletionAsync`, mutate `state.Cts`, or change `NotifyParentOnCompletion`.

- [ ] **Step 5: Run focused wait and existing trigger tests**

Run:

```powershell
dotnet test tests/LmMultiTurn.Tests/LmMultiTurn.Tests.csproj --filter "FullyQualifiedName~SubAgentManagerBatchObservationTests|FullyQualifiedName~SubAgentManagerObserveCompletionTests" --nologo
dotnet test tests/LmStreaming.Sample.Tests/LmStreaming.Sample.Tests.csproj --filter "FullyQualifiedName~SubAgentCompletionTriggerSourceTests" --nologo
```

Expected: all pass; trigger semantics are unchanged.

- [ ] **Step 6: Commit checkpoint only if authorized**

```powershell
git add src/LmMultiTurn/SubAgents/SubAgentState.cs src/LmMultiTurn/SubAgents/SubAgentManager.cs tests/LmMultiTurn.Tests/SubAgents/SubAgentManagerBatchObservationTests.cs
git commit -m "feat(subagents): wait for agent batches non-destructively"
```

---

### Task 3: Plural Tool Schemas, Handlers, Guidance, and Validation

**Files:**
- Modify: `src/LmMultiTurn/SubAgents/SubAgentToolProvider.cs:10-247,305-423`
- Modify: `tests/LmMultiTurn.Tests/SubAgentToolProviderTests.cs:14-17,85-95,294-307`

**Interfaces:**
- Consumes: `CheckAgents`, `WaitForAgentsAsync`, typed manager records from Tasks 1–2.
- Produces active tools:
  - `CheckAgents({ targets: string[] })`.
  - `WaitForAgents({ targets: string[], mode?: "all"|"any", timeout_seconds?: int })`.
- Removes active `CheckAgent` descriptor and handler.

- [ ] **Step 1: Rewrite descriptor tests first**

Change the expected tool set to:

```csharp
functions.Select(f => f.Contract.Name)
    .Should().BeEquivalentTo(["Agent", "SendMessage", "CheckAgents", "WaitForAgents"]);
```

Assert:

- No function named `CheckAgent` or `WaitForAgent`.
- Both plural descriptors require `targets` with `JsonSchemaObject.Type == "array"`, string `Items`, and `MinItems == 1`.
- Wait mode has enum `all`, `any`.
- Wait timeout has `Minimum == 30`, `Maximum == 900`.
- `CheckAgents` description contains `WaitForAgents`, `at least 30 seconds`, and `5–15 minutes`.
- Agent/SendMessage background guidance says to call `WaitForAgents` rather than poll.

- [ ] **Step 2: Add failing handler result tests**

Test exact JSON fields using `JsonDocument` rather than string matching:

```csharp
[Fact]
public async Task CheckAgents_ReturnsOrderedSnapshotsSummaryAndGuidance()
{
    var handler = GetHandler("CheckAgents");
    var result = await handler(
        JsonSerializer.Serialize(new { targets = new[] { "known-name", "missing" } }),
        new ToolCallContext(), CancellationToken.None);

    var payload = result.Should().BeOfType<ToolHandlerResult.Resolved>().Subject.Payload;
    payload.IsError.Should().BeFalse();
    using var json = JsonDocument.Parse(payload.Text);
    json.RootElement.GetProperty("agents").GetArrayLength().Should().Be(2);
    json.RootElement.GetProperty("summary").GetProperty("not_found").GetInt32().Should().Be(1);
    json.RootElement.GetProperty("guidance").GetString().Should().Contain("5–15 minutes");
}
```

Add `WaitForAgents` serialization tests for default `all`/900, explicit `any`, completed/partial/timeout/no-valid outcomes, and complete snapshot fields.

- [ ] **Step 3: Add failing recoverable validation tests**

For each invalid request, assert `ToolHandlerResult.Resolved.Payload.IsError == true` and a stable error code such as `invalid_agent_targets` or `invalid_agent_wait`:

- Missing `targets`.
- Empty array.
- Blank item.
- Non-string item.
- Invalid mode.
- Timeout 29 and 901.

Also assert 30 and 900 are accepted. Omitted mode/timeout must reach manager as `All` and 900 seconds.

- [ ] **Step 4: Run provider tests and verify RED**

Run:

```powershell
dotnet test tests/LmMultiTurn.Tests/LmMultiTurn.Tests.csproj --filter "FullyQualifiedName~SubAgentToolProviderTests" --nologo
```

Expected: failures because plural descriptors/handlers do not exist and singular remains.

- [ ] **Step 5: Implement descriptors and array parsing**

Update `GetFunctions()` to yield four tools. Build `targets` with:

```csharp
new JsonSchemaObject
{
    Type = new("array"),
    Items = new JsonSchemaObject { Type = new("string") },
    MinItems = 1,
}
```

Build wait mode and timeout schemas with enum/min/max. Since the current schema type has no serialized `default`, state defaults unambiguously in descriptions and apply them in handlers.

Add a strict helper that distinguishes missing/non-array/non-string/blank values and returns recoverable tool errors rather than throwing unhelpful JSON exceptions.

- [ ] **Step 6: Implement stable response serialization**

Serialize valid entries with existing snake_case fields:

```json
{
  "target": "...",
  "agent_id": "...",
  "name": "...",
  "status": "running",
  "template": "...",
  "task": "...",
  "recent_turns": [],
  "last_result": null,
  "send_to_parent_failed": false,
  "send_to_parent_error": null
}
```

Serialize not-found entries with target, nullable canonical identity, and `not_found`. Add summary counts. Add guidance only to `CheckAgents`; describe long waits in both tool descriptions.

- [ ] **Step 7: Update all inline Agent/SendMessage guidance in the provider**

Replace “poll with CheckAgent” language with:

- Background receipt: batch agent IDs and call `WaitForAgents` with at least 30 seconds when completion is required.
- `CheckAgents` is for an immediate status snapshot, not tight polling.
- Agents may take 5–15 minutes.

- [ ] **Step 8: Run provider and manager suites**

```powershell
dotnet test tests/LmMultiTurn.Tests/LmMultiTurn.Tests.csproj --filter "FullyQualifiedName~SubAgentToolProviderTests|FullyQualifiedName~SubAgentManagerBatchObservationTests" --nologo
```

Expected: pass.

- [ ] **Step 9: Commit checkpoint only if authorized**

```powershell
git add src/LmMultiTurn/SubAgents/SubAgentToolProvider.cs tests/LmMultiTurn.Tests/SubAgentToolProviderTests.cs
git commit -m "feat(subagents): expose plural check and wait tools"
```

---

### Task 4: Core Integration, Workflow Restrictions, and Prompt Migration

**Files:**
- Modify: `src/LmMultiTurn/MultiTurnAgentLoop.cs:178-207`
- Modify: `src/LmMultiTurn/SubAgents/SubAgentOptions.cs:31-40`
- Modify: `src/LmMultiTurn/SubAgents/SubAgentState.cs:512`
- Modify: `tests/LmMultiTurn.Tests/SubAgentIntegrationTests.cs:14-296`
- Modify: `tests/LmMultiTurn.Tests/SubAgentToolInheritanceExclusionTests.cs:80-214`
- Modify: `src/LmWorkflow/Prompts/ControllerSystemPrompt.cs:25-83`
- Modify: `src/LmWorkflow/README.md:40-50`
- Modify: `tests/LmWorkflow.Tests/WorkflowControllerToolRestrictionTests.cs:25-52`
- Modify: `samples/LmStreaming.Sample/Prompts.yaml:110-125`
- Modify: `samples/LmStreaming.Sample/PromptExamples.md:145-160`
- Modify: `samples/CodeReviewDaemon.Sample/Agents/LiveReviewAgentLoopFactory.cs:200-212`
- Modify: `tests/LmStreaming.Sample.Browser.E2E.Tests/Scenarios/SubAgentRecursionGuardTests.cs`
- Modify: `tests/LmStreaming.Sample.E2E.Tests/Scenarios/SubAgentBackgroundTests.cs`

**Interfaces:**
- Consumes: plural descriptors and handlers from Task 3.
- Produces: every active prompt/exact tool list uses plural names and steers models to batch/wait.

- [ ] **Step 1: Update exact-tool-set tests before production comments/prompts**

Change expected orchestration tool families from:

```csharp
["Agent", "SendMessage", "CheckAgent"]
```

to:

```csharp
["Agent", "SendMessage", "CheckAgents", "WaitForAgents"]
```

In workflow restriction tests, expected controller tools become:

```csharp
[
    "GetWorkflow", "SetCurrentNode", "SetState", "SetNotes",
    "Agent", "SendMessage", "CheckAgents", "WaitForAgents"
]
```

Assert inherited delegate snapshots exclude both plural observation tools.

- [ ] **Step 2: Rewrite the scripted integration flow**

In `SubAgentIntegrationTests`, make the scripted parent:

1. Spawn a background `Agent`.
2. Call `CheckAgents` once with `targets: [agentId]` to prove immediate status works.
3. Call `WaitForAgents` with `targets: [agentId]`, `mode: "all"`, `timeout_seconds: 30` to obtain completion.

Assert both plural tool calls and results exist, singular `CheckAgent` does not, and the wait result contains the completed agent snapshot.

- [ ] **Step 3: Run integration/restriction tests and verify RED**

```powershell
dotnet test tests/LmMultiTurn.Tests/LmMultiTurn.Tests.csproj --filter "FullyQualifiedName~SubAgentIntegrationTests|FullyQualifiedName~SubAgentToolInheritanceExclusionTests" --nologo
dotnet test tests/LmWorkflow.Tests/LmWorkflow.Tests.csproj --filter "FullyQualifiedName~WorkflowControllerToolRestrictionTests" --nologo
```

Expected: exact-name assertions fail until migration edits are complete.

- [ ] **Step 4: Migrate core comments and structural tool guidance**

Update `MultiTurnAgentLoop`, `SubAgentOptions`, and `SubAgentState` comments so future code review does not reintroduce singular names. Do not change recursion behavior beyond the descriptor family now containing four tools.

- [ ] **Step 5: Migrate workflow controller instructions**

In `ControllerSystemPrompt`:

- List `CheckAgents(targets)` and `WaitForAgents(targets, mode?, timeout_seconds?)`.
- Tell controllers to gather background IDs and use one plural wait call.
- Tell them not to tight-poll.
- State that agents may take 5–15 minutes and waits must be at least 30 seconds.
- Keep the existing exact workflow spawn-name gate instructions unchanged.

- [ ] **Step 6: Migrate sample/daemon prompts and docs**

Update active guidance in `Prompts.yaml`, `PromptExamples.md`, LmWorkflow README, and daemon comments. Ensure examples show arrays even for one target and show `timeout_seconds: 900` for long review tasks.

- [ ] **Step 7: Update deterministic E2E assertions**

Update recursion-guard and background tests to recognize plural active tools. Do not add a paid/live LLM test; use scripted tool calls and exact registered tool names.

- [ ] **Step 8: Run all migrated .NET tests**

```powershell
dotnet test tests/LmMultiTurn.Tests/LmMultiTurn.Tests.csproj --nologo
dotnet test tests/LmWorkflow.Tests/LmWorkflow.Tests.csproj --filter "FullyQualifiedName~WorkflowControllerToolRestrictionTests" --nologo
dotnet test tests/LmStreaming.Sample.E2E.Tests/LmStreaming.Sample.E2E.Tests.csproj --filter "FullyQualifiedName~SubAgentBackgroundTests" --nologo
dotnet test tests/LmStreaming.Sample.Browser.E2E.Tests/LmStreaming.Sample.Browser.E2E.Tests.csproj --filter "FullyQualifiedName~SubAgentRecursionGuardTests" --nologo
```

Expected: pass.

- [ ] **Step 9: Commit checkpoint only if authorized**

```powershell
git add src/LmMultiTurn src/LmWorkflow tests/LmMultiTurn.Tests tests/LmWorkflow.Tests samples/LmStreaming.Sample/Prompts.yaml samples/LmStreaming.Sample/PromptExamples.md samples/CodeReviewDaemon.Sample/Agents/LiveReviewAgentLoopFactory.cs tests/LmStreaming.Sample.E2E.Tests tests/LmStreaming.Sample.Browser.E2E.Tests
git commit -m "refactor(subagents): migrate orchestration to plural controls"
```

---

### Task 5: Client Plural Rendering and Historical Singular Compatibility

**Files:**
- Modify: `samples/LmStreaming.Sample/ClientApp/src/components/tools/registry.ts:202-225`
- Modify: `samples/LmStreaming.Sample/ClientApp/src/utils/agentColors.ts:39-74`
- Modify: `samples/LmStreaming.Sample/ClientApp/src/components/ToolPill.vue:47-54`
- Modify: `samples/LmStreaming.Sample/ClientApp/src/__tests__/utils/toolName.test.ts:6-29`
- Modify: `samples/LmStreaming.Sample/ClientApp/src/__tests__/utils/agentColors.test.ts`
- Preserve/test: `samples/LmStreaming.Sample/ClientApp/src/__tests__/fixtures/persisted/checkagent.obj.json`

**Interfaces:**
- Consumes: wire names `CheckAgents` and `WaitForAgents`, request `targets`, response `agents`.
- Produces: agent-family icons/summaries for plural calls while historical `CheckAgent` remains renderable.

- [ ] **Step 1: Add failing renderer tests**

Extend name-resolution cases:

```ts
['CheckAgents', 'agent'],
['WaitForAgents', 'agent'],
['CheckAgent', 'agent'], // persisted-history display only
```

Retain the historical fixture test. This tests display compatibility, not server execution.

- [ ] **Step 2: Add failing color-resolution tests**

Specify deterministic behavior:

- Historical singular `{ agent_id: "a1" }` still resolves `a1`.
- Plural `{ targets: ["a1"] }` can resolve `a1` only when it is already a canonical ID; readable names cannot be safely mapped client-side.
- Plural calls with multiple targets return `null` for one-pill coloring rather than guessing.
- A plural result containing exactly one `agents[0].agent_id` can resolve that ID.

If the current helper deliberately supports only scalar fields, use the conservative rule: plural calls are uncolored unless exactly one canonical ID is discoverable from args/result.

- [ ] **Step 3: Run client tests and verify RED**

```powershell
npm --prefix samples/LmStreaming.Sample/ClientApp test -- --run src/__tests__/utils/toolName.test.ts src/__tests__/utils/agentColors.test.ts
```

Expected: plural renderer/color cases fail.

- [ ] **Step 4: Register plural and historical names**

Update registry:

```ts
register(
  ['agent', 'sendmessage', 'checkagents', 'waitforagents', 'checkagent'],
  agentRenderer
);
```

Keep `checkagent` with an inline comment that it is display-only compatibility for persisted history.

- [ ] **Step 5: Implement conservative plural ID extraction**

Extend `resolveAgentIdFromCall` to inspect:

1. Existing scalar `agent_id`/`agentId`.
2. A one-element `targets` array only when its element matches an already canonical agent ID format accepted by the client; otherwise defer to result.
3. Parsed result `agent_id` as before.
4. Parsed result `agents` only when it contains exactly one non-empty canonical `agent_id`.

Never pick the first of multiple targets; a batch pill represents the batch, not one agent.

- [ ] **Step 6: Update comments and run tests**

Update `ToolPill.vue` and `agentColors.ts` comments to list plural active tools and historical singular compatibility.

Run:

```powershell
npm --prefix samples/LmStreaming.Sample/ClientApp test -- --run src/__tests__/utils/toolName.test.ts src/__tests__/utils/agentColors.test.ts
```

Expected: pass.

- [ ] **Step 7: Commit checkpoint only if authorized**

```powershell
git add samples/LmStreaming.Sample/ClientApp/src/components/tools/registry.ts samples/LmStreaming.Sample/ClientApp/src/utils/agentColors.ts samples/LmStreaming.Sample/ClientApp/src/components/ToolPill.vue samples/LmStreaming.Sample/ClientApp/src/__tests__/utils
git commit -m "fix(sample): render plural agent control tools"
```

---

### Task 6: Repository-Wide Migration Audit and Verification

**Files:**
- Modify any remaining active singular references found by the audit, excluding the approved historical fixture and explicit migration/design documentation.
- Include approved spec: `docs/superpowers/specs/2026-07-29-plural-agent-control-tools-design.md`
- Include plan: `docs/superpowers/plans/2026-07-29-plural-agent-control-tools.md`

**Interfaces:**
- Consumes: completed Tasks 1–5.
- Produces: zero active server/prompt/test references to singular execution and a fully verified solution.

- [ ] **Step 1: Audit remaining singular references**

Run:

```powershell
rg -n "CheckAgent|WaitForAgent" src samples tests docs --glob "!docs/superpowers/specs/2026-07-29-plural-agent-control-tools-design.md" --glob "!docs/superpowers/plans/2026-07-29-plural-agent-control-tools.md"
```

Classify every match:

- Migrate active server code, prompts, tests, and comments.
- Keep only intentional historical-client fixture/registry compatibility, with an explanatory comment/test.
- Confirm no active singular descriptor or handler remains.

- [ ] **Step 2: Run focused core tests**

```powershell
dotnet test tests/LmMultiTurn.Tests/LmMultiTurn.Tests.csproj --nologo
dotnet test tests/LmWorkflow.Tests/LmWorkflow.Tests.csproj --filter "FullyQualifiedName~WorkflowControllerToolRestrictionTests" --nologo
```

Expected: pass.

- [ ] **Step 3: Run relevant sample tests**

```powershell
dotnet test tests/LmStreaming.Sample.Tests/LmStreaming.Sample.Tests.csproj --filter "FullyQualifiedName~SubAgentCompletionTriggerSourceTests" --nologo
dotnet test tests/LmStreaming.Sample.E2E.Tests/LmStreaming.Sample.E2E.Tests.csproj --filter "FullyQualifiedName~SubAgentBackgroundTests" --nologo
dotnet test tests/LmStreaming.Sample.Browser.E2E.Tests/LmStreaming.Sample.Browser.E2E.Tests.csproj --filter "FullyQualifiedName~SubAgentRecursionGuardTests" --nologo
npm --prefix samples/LmStreaming.Sample/ClientApp test -- --run src/__tests__/utils/toolName.test.ts src/__tests__/utils/agentColors.test.ts
```

Expected: pass.

- [ ] **Step 4: Run the full solution build**

```powershell
dotnet build LmDotnetTools.sln -bl:.logs/build.binlog
```

Expected: build succeeds with no new warnings/errors.

- [ ] **Step 5: Run formatting verification**

```powershell
dotnet format whitespace LmDotnetTools.sln --verify-no-changes
```

Expected: exit code 0.

- [ ] **Step 6: Inspect final diff and status**

```powershell
git diff --check
git status --short
git diff --stat
```

Confirm:

- No unrelated generated logs/conversations/recordings are included.
- The approved spec and plan are present.
- No source-controlled historical fixture was deleted.
- No AI signature appears in documentation or proposed commit messages.

- [ ] **Step 7: Final commit only if authorized**

If the user authorizes commits and earlier checkpoints were not committed separately:

```powershell
git add src/LmMultiTurn src/LmWorkflow samples/LmStreaming.Sample samples/CodeReviewDaemon.Sample tests docs/superpowers/specs/2026-07-29-plural-agent-control-tools-design.md docs/superpowers/plans/2026-07-29-plural-agent-control-tools.md
git commit -m "feat(subagents): batch agent checks and waits"
```

Do not push unless the user separately requests it.
