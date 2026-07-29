# Daemon Recursive Review Completion Barrier Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Prevent CodeReviewDaemon from judging or posting until every recursive review sub-agent is durably terminal, then produce and deliver one same-thread authoritative synthesis.

**Architecture:** The first parent answer is a provisional, never-posted artifact. A provider-neutral completion source reads a recursive tree: in-process from read-only `SubAgentManager` snapshots, S2S from an LmStreaming recursive endpoint backed by live and persisted state. After two identical all-terminal snapshots, the daemon sends a synthesis turn on the same parent, promotes only that answer, verifies inline delivery, and uses the existing idempotent host summary only if the provider marker is absent.

**Tech Stack:** .NET 9, C# 13, xUnit, FluentAssertions, ASP.NET Core, LmMultiTurn, LmStreaming.Sample, SQLite review artifacts, GitHub/ADO APIs.

## Global Constraints

- Initial parent answer never posts, judges, or becomes authoritative.
- Include every recursive descendant; a late nested spawn resets stability.
- `Completed`, `Error`, `Stopped` terminal; `Running`, `Unknown` nonterminal.
- Require two identical terminal snapshots separated by two seconds.
- 30-minute timeout fails closed, posts nothing, leaves `RetryPending`.
- Child failures are safely inventoried; no raw exception/prompt/secret disclosure.
- Synthesis is a second run on the same parent conversation/thread.
- In-process synthesis attempts inline posting; host summary only when verification finds no marker.
- S2S synthesis is posted host-side because the hosted agent has no PR write credential.
- Preserve current sandbox management through typed `SandboxClient`.
- No AI signatures in commits or PR text.

## Files

**Create**
- `samples/CodeReviewDaemon.Sample/Agents/ReviewSubAgentCompletion.cs` - shared tree DTO, source interface, barrier.
- `tests/CodeReviewDaemon.Sample.Tests/Agents/ReviewSubAgentCompletionBarrierTests.cs` - barrier contract.

**Modify**
- `src/LmMultiTurn/SubAgents/SubAgentManager.cs` and `SubAgentState.cs` - recursive read snapshot and terminal persistence callback.
- `samples/LmStreaming.Sample/Persistence/SubAgentProvenance.cs` - durable terminal keys/projection.
- `samples/LmStreaming.Sample/Controllers/ConversationsController.cs` and `Models/SubAgentSummary.cs` - recursive completion API.
- `samples/CodeReviewDaemon.Sample/Agents/LmStreamingS2SClient.cs` - S2S completion source.
- `samples/CodeReviewDaemon.Sample/Agents/ReviewAgent.cs` and `S2SReviewAgent.cs` - same-thread synthesis drive.
- `samples/CodeReviewDaemon.Sample/Orchestration/DaemonReviewStageExecutor.cs` - provisional/barrier/synthesis/post flow.
- `samples/CodeReviewDaemon.Sample/Persistence/ReviewStore.cs` - artifact state access.
- Corresponding daemon, LmStreaming, and LmMultiTurn tests.

---

### Task 1: Persist exact child terminal status

**Files:**
- Modify: `samples/LmStreaming.Sample/Persistence/SubAgentProvenance.cs`
- Modify: `samples/LmStreaming.Sample/Program.cs`
- Test: `tests/LmStreaming.Sample.Tests/Persistence/NonOwningConversationStoreTests.cs`
- Test: `tests/LmStreaming.Sample.Tests/Persistence/SubAgentProvenanceTests.cs`

**Interfaces:**

```csharp
public const string StatusKey = "sample.subAgentStatus";
public const string TerminalAtKey = "sample.subAgentTerminalAt";
public static ImmutableDictionary<string, object> Build(
    string parentThreadId,
    SubAgentSnapshot? snapshot);
```

- [ ] Add a failing test: `Build` with `Completed`, `Error`, and `Stopped` snapshots writes lower-case status and UTC terminal timestamp; `Running` writes status without terminal timestamp.
- [ ] Run:
  `dotnet test tests/LmStreaming.Sample.Tests/LmStreaming.Sample.Tests.csproj --filter "FullyQualifiedName~SubAgentProvenance"`
  Expected RED: keys/status are absent.
- [ ] Extend provenance to stamp status on every metadata write. Use the live `describeChild` callback already invoked by `NonOwningConversationStore`; no second registry.
- [ ] Make `TryProject` return persisted exact status when present and `unknown` for legacy children instead of the current ambiguous `persisted` marker.
- [ ] Add restart projection assertions for all terminal states and legacy unknown.
- [ ] Run the focused tests; expected GREEN.
- [ ] Commit:
  `git commit -m "feat(streaming): persist sub-agent terminal state"`

### Task 2: Expose recursive completion snapshots

**Files:**
- Modify: `src/LmMultiTurn/SubAgents/SubAgentManager.cs`
- Modify: `samples/LmStreaming.Sample/Models/SubAgentSummary.cs`
- Modify: `samples/LmStreaming.Sample/Controllers/ConversationsController.cs`
- Test: `tests/LmMultiTurn.Tests/SubAgents/SubAgentManagerListAgentsTests.cs`
- Test: `tests/LmStreaming.Sample.Tests/Controllers/ConversationsControllerSubAgentsTests.cs`

**Interfaces:**

```csharp
public sealed record RecursiveSubAgentSnapshot(
    string AgentId, string ThreadId, string ParentThreadId,
    string? Name, string Template, SubAgentStatus Status,
    int Depth, DateTimeOffset? TerminalAtUtc, string? FailureCode);

public IReadOnlyList<RecursiveSubAgentSnapshot> ListAgentTree();
```

HTTP: `GET /api/conversations/{threadId}/subagents?recursive=true` returns the existing flat array shape with `parentThreadId`, `depth`, `terminalAtUtc`, and stable status values.

- [ ] Write a failing LmMultiTurn test with parent -> child -> grandchild; assert both descendants, depth, and parent IDs.
- [ ] Run the test; expected RED: `ListAgentTree` absent.
- [ ] Implement recursive traversal only through live child loops whose `SubAgentManager` exists. Return immutable DTOs; never expose execution handles.
- [ ] Write failing controller tests: live recursive tree, persisted recursive tree after parent leaves pool, and legacy child as `unknown`.
- [ ] Implement controller recursive merge. Traverse persisted provenance by parent thread ID with cycle detection and the existing 2,000-thread scan cap.
- [ ] Run both test projects; expected GREEN.
- [ ] Commit:
  `git commit -m "feat(streaming): expose recursive sub-agent completion"`

### Task 3: Add provider-neutral barrier

**Files:**
- Create: `samples/CodeReviewDaemon.Sample/Agents/ReviewSubAgentCompletion.cs`
- Create: `tests/CodeReviewDaemon.Sample.Tests/Agents/ReviewSubAgentCompletionBarrierTests.cs`
- Modify: `samples/CodeReviewDaemon.Sample/Configuration/CodeReviewDaemonOptions.cs`

**Interfaces:**

```csharp
internal interface IReviewSubAgentCompletionSource
{
    Task<ReviewSubAgentTreeSnapshot> GetSnapshotAsync(
        ReviewRun run, string parentThreadId, CancellationToken ct);
}

internal sealed record ReviewSubAgentTreeSnapshot(
    IReadOnlyList<ReviewSubAgentNode> Nodes);

internal sealed class ReviewSubAgentCompletionBarrier
{
    Task<ReviewSubAgentTreeSnapshot> WaitAsync(
        ReviewRun run, string parentThreadId, CancellationToken ct);
}
```

Options: `ReviewSubAgentBarrierTimeoutMinutes = 30`, `ReviewSubAgentBarrierQuietSeconds = 2`.

- [ ] Test RED: running child blocks; second identical all-terminal snapshot opens; late nested child resets; error/stopped terminal; unknown blocks; empty tree requires two snapshots; timeout throws.
- [ ] Use `TimeProvider` and scripted source, never sleeps.
- [ ] Implement canonical snapshot identity sorted by `(Depth, ParentThreadId, AgentId)` and compare IDs/parents/status only.
- [ ] Poll with 1, 2, 4, then 5 second intervals capped by deadline; quiet-period second read is mandatory.
- [ ] Run tests; expected GREEN.
- [ ] Commit:
  `git commit -m "feat(daemon): wait for recursive review agents"`

### Task 4: Implement in-process and S2S sources

**Files:**
- Modify: `samples/CodeReviewDaemon.Sample/Agents/LiveReviewAgentLoopFactory.cs`
- Modify: `samples/CodeReviewDaemon.Sample/Agents/LmStreamingS2SClient.cs`
- Modify: `samples/CodeReviewDaemon.Sample/Agents/S2SReviewAgent.cs`
- Modify: `samples/CodeReviewDaemon.Sample/Program.cs`
- Test: daemon factory/client scenario tests.

- [ ] Add RED in-process test: source reads recursive tree from the exact parent review loop.
- [ ] Add RED S2S client test: recursive endpoint maps every field and sends S2S/app headers.
- [ ] Implement `InProcessReviewSubAgentCompletionSource` using a read-only parent-loop lookup supplied by `LiveReviewAgentLoopFactory`.
- [ ] Implement `S2SReviewSubAgentCompletionSource` calling `api/conversations/{threadId}/subagents?recursive=true`.
- [ ] Register the correct source by review mode. If unavailable, barrier retries and eventually fails closed; no null-object success.
- [ ] Run daemon tests; expected GREEN.
- [ ] Commit:
  `git commit -m "feat(daemon): read review-agent completion trees"`

### Task 5: Split provisional and synthesis turns

**Files:**
- Modify: `samples/CodeReviewDaemon.Sample/Agents/ReviewAgent.cs`
- Modify: `samples/CodeReviewDaemon.Sample/Agents/S2SReviewAgent.cs`
- Modify: `samples/CodeReviewDaemon.Sample/Agents/DaemonAgentFactory.cs`
- Test: `tests/CodeReviewDaemon.Sample.Tests/Scenarios/ReviewAgentTests.cs`
- Test: `tests/CodeReviewDaemon.Sample.Tests/Scenarios/S2SReviewAgentTests.cs`

**Interfaces:**

```csharp
Task<ReviewAgentResult> CollectProvisionalAsync(string input, CancellationToken ct);
Task<ReviewAgentResult> SynthesizeFinalAsync(
    string synthesisPrompt, bool allowInlinePosting, CancellationToken ct);
```

- [ ] RED: provisional sends one turn and never enforcement/post prompt.
- [ ] RED: synthesis sends a second turn on same agent/thread and returns second answer, not first.
- [ ] RED: synthesis error/blank throws.
- [ ] Build synthesis prompt from deterministic safe inventory: completed names/templates and failed/stopped names/templates/status only.
- [ ] Remove old `ReviewAsync(input, postEnforcementPrompt)` behavior after all callers migrate; do not keep parallel APIs.
- [ ] Run tests; expected GREEN.
- [ ] Commit:
  `git commit -m "feat(daemon): synthesize reviews after child settlement"`

### Task 6: Persist barrier and synthesis checkpoints

**Files:**
- Modify: `samples/CodeReviewDaemon.Sample/Orchestration/DaemonReviewStageExecutor.cs`
- Modify: `samples/CodeReviewDaemon.Sample/Persistence/ReviewStore.cs`
- Test: `tests/CodeReviewDaemon.Sample.Tests/Orchestration/DaemonReviewStageExecutorTests.cs`

**Interfaces:**

```csharp
internal const string ProvisionalReviewArtifactKind = "review-provisional";
internal const string SubAgentSnapshotArtifactKind = "review-subagent-snapshot";
internal const string SynthesisRequestArtifactKind = "review-synthesis-request";

internal sealed record ReviewProvisionalPayload(
    string ReviewText, string ParentThreadId, string? ParentRunId,
    DateTimeOffset CreatedAtUtc);
internal sealed record ReviewSubAgentSnapshotPayload(
    IReadOnlyList<ReviewSubAgentNode> Nodes, DateTimeOffset BarrierOpenedAtUtc);
internal sealed record ReviewSynthesisRequestPayload(
    string InputId, string? RunId, string ParentThreadId);
```

- [ ] Write a failing executor test: `Reviewed` persists `review-provisional` but no authoritative `review`, judge artifact, or posting outbox while the completion source reports a running child.
- [ ] Run:
  `dotnet test tests/CodeReviewDaemon.Sample.Tests/CodeReviewDaemon.Sample.Tests.csproj --filter "FullyQualifiedName~Reviewed_waits_for_recursive_children"`
  Expected RED: the existing executor immediately creates `review`.
- [ ] Add `ReviewStore.TryGetLatestArtifact(reviewRunId, artifactKind)` under the existing store gate; return null when absent and preserve append-only artifact history.
- [ ] Refactor `RunPrimaryReviewAsync` into four resumable checkpoints: collect/load provisional; wait/load terminal snapshot; queue/poll synthesis; persist authoritative `review`.
- [ ] If `review-synthesis-request` exists after restart, call a new `SynthesizeFinalByInputAsync` polling seam instead of sending a duplicate input.
- [ ] Add restart tests after each checkpoint: provisional saved, barrier opened, synthesis queued, authoritative saved. Each test constructs a fresh executor over the same `ReviewStore`.
- [ ] Assert timeout/snapshot/synthesis failure leaves no authoritative artifact and bubbles to `PrOrchestrator`, which marks `RetryPending`.
- [ ] Run focused daemon tests; expected GREEN.
- [ ] Commit:
  `git commit -m "feat(daemon): persist review completion checkpoints"`

### Task 7: Verify inline delivery and apply fallback

**Files:**
- Modify: `samples/CodeReviewDaemon.Sample/Orchestration/IReviewCommentPublisher.cs`
- Modify: `samples/CodeReviewDaemon.Sample/Orchestration/DaemonReviewStageExecutor.cs`
- Modify: `samples/CodeReviewDaemon.Sample/Orchestration/ReviewPoster.cs`
- Test: `tests/CodeReviewDaemon.Sample.Tests/Scenarios/ReviewPosterTests.cs`
- Test: `tests/CodeReviewDaemon.Sample.Tests/Scenarios/DaemonReviewStageExecutorTests.cs`

**Interfaces:**

```csharp
Task<PostedComment?> FindAuthoritativeReviewAsync(
    ReviewCommentTarget target,
    string headSha,
    string authoritativeReviewMarker,
    CancellationToken cancellationToken);
```

The marker is head-scoped and synthesis-specific:
`review-synthesis:v1:{provider}:{normalizedRepo}:{prId}:{headSha}:{variantId}`.

- [ ] Write failing tests: marker found means no host post; marker absent means one host post; provider verification exception also invokes the idempotent host fallback; replay/restart remains exactly once.
- [ ] Run the two scenario test classes; expected RED because the publisher has no synthesis verification seam.
- [ ] Add the marker to the in-process synthesis/post prompt. Require the agent's posted summary to carry it; line comments need not duplicate it.
- [ ] Implement provider scans for the authoritative marker using existing bounded comment/thread APIs. Never infer posting from model text such as “already posted.”
- [ ] In `Posted`, verify in-process delivery first. If found, transition/adopt the existing outbox response without posting a summary; otherwise pass the authoritative synthesis to `ReviewPoster`.
- [ ] On S2S skip inline verification and call `ReviewPoster` directly, because the hosted agent intentionally has no provider credential.
- [ ] Ensure judge and `CommitPooledNotesAsync` read only `ReviewArtifactKind`, never `review-provisional`.
- [ ] Run tests; expected GREEN.
- [ ] Commit:
  `git commit -m "feat(daemon): verify post-barrier review delivery"`

### Task 8: Close the S2S durable-status loop

**Files:**
- Modify: `samples/LmStreaming.Sample/Controllers/ConversationsController.cs`
- Modify: `samples/LmStreaming.Sample/Persistence/SubAgentProvenance.cs`
- Modify: `samples/CodeReviewDaemon.Sample/Agents/LmStreamingS2SClient.cs`
- Test: `tests/LmStreaming.Sample.Tests/Controllers/ConversationsControllerSubAgentsTests.cs`
- Test: `tests/CodeReviewDaemon.Sample.Tests/Scenarios/LmStreamingS2SClientTests.cs`

- [ ] Write a failing end-to-end-style controller test: remove the live parent from the pool, retain child metadata, request `?recursive=true`, and assert exact completed/error/stopped states and parent/depth survive.
- [ ] Write a failing client test for a mixed recursive tree and verify all three auth headers are attached without logging values.
- [ ] Run focused tests; expected RED until Tasks 1–2 production changes are wired end-to-end.
- [ ] Make recursive response ordering deterministic by `(depth, parentThreadId, threadId)` and reject/cut cycles with a warning containing only opaque IDs.
- [ ] Keep the old non-recursive response backward compatible: existing fields and status spellings remain accepted; new fields are additive.
- [ ] Run focused and full `LmStreaming.Sample.Tests`; expected GREEN.
- [ ] Commit:
  `git commit -m "test(streaming): prove durable recursive completion status"`

### Task 9: Full regression and live timing proof

**Files:**
- Modify only files needed to repair regressions introduced by Tasks 1–8.
- Update: `docs/superpowers/specs/2026-07-29-daemon-recursive-review-completion-barrier-design.md` only if verified implementation details differ.
- Runtime evidence: `.run/review-completion-barrier-*` (ignored, never committed).

- [ ] Run daemon tests:
  `dotnet test tests/CodeReviewDaemon.Sample.Tests/CodeReviewDaemon.Sample.Tests.csproj --nologo`
  Expected: zero failures.
- [ ] Run LmMultiTurn tests:
  `dotnet test tests/LmMultiTurn.Tests/LmMultiTurn.Tests.csproj --nologo`
  Expected: zero failures.
- [ ] Run LmStreaming tests:
  `dotnet test tests/LmStreaming.Sample.Tests/LmStreaming.Sample.Tests.csproj --nologo`
  Expected: zero failures except existing platform skips.
- [ ] Run full solution:
  `dotnet test LmDotnetTools.sln --no-build --logger "trx;LogFileName=results.trx" --results-directory .logs/review-barrier`
  Expected: every project passes; intentional skips only.
- [ ] Run whitespace checks for all four changed source/test projects with `dotnet format whitespace ... --verify-no-changes --no-restore`.
- [ ] Launch the dedicated review host on 5051 and an isolated daemon database targeting one fixture PR. Use Terra for the parent and Sol/Terra-only child model resolution.
- [ ] Drive a deterministic delayed-child fixture: one child completes after the provisional answer. Capture timestamps for child terminal state, barrier-open event, synthesis input, authoritative answer, provider post, judge, and run completion.
- [ ] Assert from logs/provider API:
  `max(descendantTerminalAt) <= synthesisQueuedAt < authoritativeAnswerAt <= providerPostAt < judgeAt`.
- [ ] Repeat with a late nested child and with one errored child; verify stability reset and safe failure disclosure.
- [ ] Repeat with an intentionally nonterminal child and a shortened test-only barrier timeout; verify no provider comment and `RetryPending`.
- [ ] Commit any regression fixes:
  `git commit -m "test(daemon): verify recursive review completion barrier"`
- [ ] Push and update PR #230 with measured counts and live timing evidence.

### Task 10: Re-review the completed implementation

**Files:** none unless findings require fixes.

- [ ] Invoke `code-reviewer:pr-review` on the final PR head with explicit lenses: correctness, concurrency, restart/idempotency, schema compatibility, exception handling, test coverage, over-engineering, and blind spots.
- [ ] Verify each finding against current code before editing.
- [ ] Implement valid findings one at a time with RED→GREEN tests and separate commits.
- [ ] Re-run Task 9 verification after any fix.
- [ ] Reply in the matching GitHub review thread and resolve only after the fix is pushed and verified.

---

## Plan self-review

- **Spec coverage:** Tasks 1–2 durable recursive status; Tasks 3–4 unified barrier sources; Task 5 same-thread synthesis; Task 6 restart checkpoints; Task 7 verified posting/fallback; Task 8 S2S compatibility; Task 9 timeout/errors/live timing; Task 10 requested adversarial review.
- **Scope:** one subsystem; lifecycle webhooks remain optional and non-authoritative.
- **Type consistency:** `ReviewSubAgentTreeSnapshot`, `ReviewSubAgentNode`, `IReviewSubAgentCompletionSource`, and artifact names are introduced before executor use.
- **Security:** inventories exclude prompts/raw errors; existing S2S and sandbox credentials remain header-only/environment-only.
- **No duplicate architecture:** reuses `SubAgentManager`, `SubAgentProvenance`, `ReviewStore`, `ReviewPoster`, and provider backstop scans rather than adding a second registry/outbox.
- **Failure direction:** unknown/running descendants, unavailable snapshots, blank synthesis, and timeout all fail closed before posting.
