# Daemon Recursive Review Completion Barrier Implementation Plan

**Status:** Implemented — shipped in `26f4a0f4`. See `samples/CodeReviewDaemon.Sample/Agents/ReviewSubAgentCompletion.cs` and the `recursive=true` descendant-graph endpoint in `ConversationsController.cs`.

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Prevent CodeReviewDaemon from judging or posting until every review descendant visible through its provenance contract is terminal, then produce and deliver one same-thread authoritative synthesis.

**Architecture:** The first parent answer is a provisional, never-posted artifact. One live in-process loop stays open through its direct-child barrier and synthesis; S2S reads a versioned recursive graph assembled from live state and durable provenance. A single persisted 30-minute deadline covers provisional, barrier, and spawn-disabled synthesis. Only the post-barrier answer becomes authoritative; existing idempotency markers/outbox provide verified delivery and fallback.

**Tech Stack:** .NET 9, C# 13, xUnit, FluentAssertions, ASP.NET Core, LmMultiTurn, LmStreaming.Sample, SQLite review artifacts, GitHub/ADO APIs.

## Global Constraints

- Initial parent answer never posts, judges, or becomes authoritative.
- Include every descendant visible through the declared review provenance contract; do not enable nested live delegation in this feature.
- `Completed`, `Error`, `Stopped` terminal; `Running`, `Unknown` nonterminal.
- Require two identical terminal snapshots separated by two seconds; growth, shrinkage, relationship, or status change resets stability.
- One absolute 30-minute deadline covers provisional + barrier + synthesis and survives restart.
- Timeout, snapshot incompatibility, lifecycle/head change, or synthesis roster change fails closed and posts nothing.
- Child failures are safely inventoried; no raw exception/prompt/secret disclosure.
- Synthesis is a second run on the same parent conversation/thread with spawn capability removed.
- In-process synthesis attempts inline posting; host summary only when the canonical summary marker is absent/unverifiable.
- S2S synthesis is posted host-side because the hosted agent has no PR write credential.
- Preserve current sandbox management through typed `SandboxClient`.
- Do not persist live loop handles or a duplicate full-tree checkpoint.
- No AI signatures in commits or PR text.

## Files

**Create**
- `samples/CodeReviewDaemon.Sample/Agents/ReviewSubAgentCompletion.cs` — shared DTO/source contract and barrier.
- `tests/CodeReviewDaemon.Sample.Tests/Agents/ReviewSubAgentCompletionBarrierTests.cs` — barrier contract.

**Modify**
- `src/LmMultiTurn/SubAgents/SubAgentManager.cs` / `SubAgentState.cs` — immutable live roster and active terminal-persistence callback.
- `samples/LmStreaming.Sample/Persistence/SubAgentProvenance.cs` / `NonOwningConversationStore.cs` — durable exact states.
- `samples/LmStreaming.Sample/Controllers/ConversationsController.cs`, `Models/SubAgentSummary.cs`, and `ClientApp/src/api/subAgentsApi.ts` — versioned recursive API.
- `samples/CodeReviewDaemon.Sample/Agents/LmStreamingS2SClient.cs`, `ReviewAgent.cs`, `S2SReviewAgent.cs`, and `S2SReviewAgentLoopFactory.cs` — sources, same-thread resume, shared deadline, synthesis drive.
- `samples/CodeReviewDaemon.Sample/Orchestration/DaemonReviewStageExecutor.cs`, `PrOrchestrator.cs`, and `ReviewPoster.cs` — provisional/barrier/synthesis/delivery flow.
- `samples/CodeReviewDaemon.Sample/Persistence/ReviewStore.cs` — latest-artifact lookup.
- Corresponding daemon, LmStreaming, LmMultiTurn, and client tests.

---

### Task 1: Persist terminal child state at the transition

**Files:**
- Modify: `src/LmMultiTurn/SubAgents/SubAgentManager.cs`
- Modify: `src/LmMultiTurn/SubAgents/SubAgentState.cs`
- Modify: `samples/LmStreaming.Sample/Persistence/SubAgentProvenance.cs`
- Modify: `samples/LmStreaming.Sample/Persistence/NonOwningConversationStore.cs`
- Modify: `samples/LmStreaming.Sample/Program.cs`
- Test: `tests/LmMultiTurn.Tests/SubAgents/SubAgentManagerListAgentsTests.cs`
- Test: `tests/LmStreaming.Sample.Tests/Persistence/NonOwningConversationStoreTests.cs`
- Test: `tests/LmStreaming.Sample.Tests/Persistence/SubAgentProvenanceTests.cs`

**Produces:**

```csharp
public const string StatusKey = "sample.subAgentStatus";
public const string TerminalAtKey = "sample.subAgentTerminalAt";

public static ImmutableDictionary<string, object> Build(
    string parentThreadId,
    SubAgentSnapshot? snapshot);
```

- [ ] Add RED tests for `Completed`, `Error`, `Stopped`, and `Running` projection; terminal values include UTC timestamp, running does not.
- [ ] Add RED test: child reaches terminal, performs no subsequent metadata write, and exact status/timestamp are still persisted.
- [ ] Add RED restart tests: exact terminal values restore; old metadata without exact status projects as `unknown`.
- [ ] Run focused LmMultiTurn/LmStreaming tests; confirm missing keys/transition callback are the failures.
- [ ] Add a narrow terminal-state persistence callback owned by the host integration and invoke it from the manager path that sets terminal state. The manager actively pushes the terminal update; do not rely solely on the child's next `SaveMetadataAsync`.
- [ ] Keep existing metadata-write provenance projection as an idempotent refresh path; no second registry.
- [ ] Run focused tests; expected GREEN.
- [ ] Commit: `feat(streaming): persist sub-agent terminal transitions`.

### Task 2: Expose a versioned persisted descendant graph

**Files:**
- Modify: `samples/LmStreaming.Sample/Models/SubAgentSummary.cs`
- Modify: `samples/LmStreaming.Sample/Controllers/ConversationsController.cs`
- Modify: `samples/LmStreaming.Sample/ClientApp/src/api/subAgentsApi.ts`
- Test: `tests/LmStreaming.Sample.Tests/Controllers/ConversationsControllerSubAgentsTests.cs`

**Produces:**

```csharp
public sealed record SubAgentTreeResponse(
    int SchemaVersion,
    IReadOnlyList<SubAgentSummary> Nodes);
```

Recursive node fields are required in schema v1: `agentId`, `threadId`, `parentThreadId`, `name`, `template`, `status`, `depth`, `terminalAtUtc`, `failureCode`.

HTTP: `GET /api/conversations/{threadId}/subagents?recursive=true` returns `schemaVersion: 1` plus a deterministically ordered node array. The existing no-query endpoint keeps its old array shape.

- [ ] Add RED controller test with persisted root→child→grandchild metadata; assert depth and parent IDs. State explicitly that this tests graph-reader correctness, not live nested spawning.
- [ ] Add RED tests for cycles, terminal ancestors, legacy `persisted`→`unknown`, and required relationship fields.
- [ ] Add RED compatibility test: non-recursive response retains its current fields/shape.
- [ ] Implement one bounded store scan (existing 2,000-thread cap), build a parent→children index once, and traverse from the root with a visited set. Do not repeat the store scan per depth/node.
- [ ] Order recursive nodes by `(depth, parentThreadId, threadId)`.
- [ ] Update TypeScript status with `unknown` and additive recursive fields.
- [ ] Log cycle cuts using opaque IDs only.
- [ ] Run LmStreaming tests/client typecheck; expected GREEN.
- [ ] Commit: `feat(streaming): expose versioned sub-agent descendants`.

### Task 3: Add the provider-neutral shared-deadline barrier

**Files:**
- Create: `samples/CodeReviewDaemon.Sample/Agents/ReviewSubAgentCompletion.cs`
- Create: `tests/CodeReviewDaemon.Sample.Tests/Agents/ReviewSubAgentCompletionBarrierTests.cs`
- Modify: `samples/CodeReviewDaemon.Sample/Configuration/CodeReviewDaemonOptions.cs`

**Produces:**

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
        ReviewRun run,
        string parentThreadId,
        DateTimeOffset deadlineUtc,
        Func<CancellationToken, Task> validateReviewStillCurrent,
        CancellationToken ct);
}

internal sealed class ReviewBarrierDeadlineException : TimeoutException;
```

Options: `ReviewStageDeadlineMinutes = 30`, `ReviewSubAgentBarrierQuietSeconds = 2`.

- [ ] RED: three running children resolve in different orders; barrier stays closed until all terminal.
- [ ] RED: error/stopped terminal; running/unknown block; mixed terminal foreground + running background blocks.
- [ ] RED: second identical all-terminal snapshot opens; empty tree also needs two snapshots.
- [ ] RED: roster addition, removal, parent change, or status change resets stability.
- [ ] RED: a persisted descendant behind a terminal ancestor remains part of identity and blocks while nonterminal.
- [ ] RED: resumed deadline with 25 minutes elapsed allows only five remaining minutes.
- [ ] RED: lifecycle/head validator failure aborts before barrier opens.
- [ ] Use `TimeProvider` and scripted sources; never sleep in unit tests.
- [ ] Canonicalize snapshots by `(Depth, ParentThreadId, AgentId)` and compare IDs/parents/status.
- [ ] Poll 1, 2, 4, then 5 seconds capped by the supplied absolute deadline; throw `ReviewBarrierDeadlineException` at expiry.
- [ ] Run tests; expected GREEN.
- [ ] Commit: `feat(daemon): wait for review sub-agent settlement`.

### Task 4: Implement in-process and S2S completion sources

**Files:**
- Modify: `src/LmMultiTurn/SubAgents/SubAgentManager.cs`
- Modify: `samples/CodeReviewDaemon.Sample/Agents/LmStreamingS2SClient.cs`
- Modify: `samples/CodeReviewDaemon.Sample/Agents/S2SReviewAgent.cs`
- Modify: `samples/CodeReviewDaemon.Sample/Program.cs`
- Test: `tests/LmMultiTurn.Tests/SubAgents/SubAgentManagerListAgentsTests.cs`
- Test: daemon S2S client/source tests.

- [ ] RED in-process test: immutable source snapshot contains every direct child from the exact live parent manager; no execution handle escapes.
- [ ] RED S2S test: schema-v1 graph maps all fields and sends S2S/app headers without logging values.
- [ ] RED version-skew tests: old flat response, absent/unsupported schema version, and missing required relationship fields fail closed.
- [ ] RED malformed/new status string maps to `Unknown`, not terminal/default or an unhandled JSON enum error.
- [ ] Implement `InProcessReviewSubAgentCompletionSource` from a manager passed directly in the same call stack. Do not add a loop lookup registry to `LiveReviewAgentLoopFactory`.
- [ ] Implement `S2SReviewSubAgentCompletionSource` against the versioned recursive endpoint.
- [ ] Register the mode-appropriate source; unavailable/incompatible snapshots never become empty-success.
- [ ] Document host-first deployment before daemon barrier enablement.
- [ ] Run focused tests; expected GREEN.
- [ ] Commit: `feat(daemon): read review completion state`.

### Task 5: Keep one parent alive and split provisional from synthesis

**Files:**
- Modify: `samples/CodeReviewDaemon.Sample/Agents/ReviewAgent.cs`
- Modify: `samples/CodeReviewDaemon.Sample/Agents/S2SReviewAgent.cs`
- Modify: `samples/CodeReviewDaemon.Sample/Agents/S2SReviewAgentLoopFactory.cs`
- Modify: `samples/CodeReviewDaemon.Sample/Agents/DaemonAgentFactory.cs`
- Modify: `src/LmMultiTurn/SubAgents/SubAgentToolProvider.cs` — turn-scoped spawn suppression while retaining result/message tools.
- Modify: `samples/CodeReviewDaemon.Sample/Orchestration/DaemonReviewStageExecutor.cs`
- Test: `tests/CodeReviewDaemon.Sample.Tests/Scenarios/ReviewAgentTests.cs`
- Test: `tests/CodeReviewDaemon.Sample.Tests/Scenarios/S2SReviewAgentTests.cs`
- Test: executor lifecycle tests.

**Produces:**

```csharp
Task<ReviewAgentResult> CollectProvisionalAsync(
    string input, DateTimeOffset deadlineUtc, CancellationToken ct);

Task<ReviewAgentResult> SynthesizeFinalAsync(
    string synthesisPrompt,
    bool allowInlinePosting,
    DateTimeOffset deadlineUtc,
    CancellationToken ct);
```

- [ ] RED: provisional sends one collect-only turn, regardless of posting configuration.
- [ ] RED: the parent loop's disposal does not occur between provisional and barrier/synthesis; disposal occurs after synthesis/failure.
- [ ] RED: synthesis runs on the same in-process agent and same S2S thread and returns the second answer.
- [ ] RED: `S2SReviewAgentLoopFactory` seeds a persisted existing thread ID; no `ProvisionAsync` call occurs on resume.
- [ ] RED: both S2S turns respect the one supplied absolute deadline rather than creating fresh per-turn windows.
- [ ] RED: synthesis profile cannot call `Agent`; delivered-result reading and in-process provider posting remain available.
- [ ] RED: synthesis generation error/blank throws. Keep provider verification outside this method so verification exceptions remain fallback-eligible.
- [ ] Build deterministic safe inventory from name/template/status/failure-code only.
- [ ] Extend the existing executor `await using` scope to enclose collect → barrier → synthesize. Pass the live manager directly to the in-process source.
- [ ] Remove old `ReviewAsync(input, postEnforcementPrompt)` after all callers migrate; do not keep parallel APIs.
- [ ] Run tests; expected GREEN.
- [ ] Commit: `feat(daemon): synthesize after child settlement`.

### Task 6: Persist minimal resumable checkpoints and retry semantics

**Files:**
- Modify: `samples/CodeReviewDaemon.Sample/Orchestration/DaemonReviewStageExecutor.cs`
- Modify: `samples/CodeReviewDaemon.Sample/Orchestration/PrOrchestrator.cs`
- Modify: `samples/CodeReviewDaemon.Sample/Persistence/ReviewStore.cs`
- Test: `tests/CodeReviewDaemon.Sample.Tests/Scenarios/DaemonReviewStageExecutorTests.cs`
- Test: orchestrator retry tests.

**Artifact kinds:**

```csharp
internal const string ProvisionalReviewArtifactKind = "review-provisional";
internal const string SynthesisRequestArtifactKind = "review-synthesis-request";
```

Reuse `ReviewArtifactPayload` for provisional review text/thread/run correlation, extending it append-compatibly with nullable `ReviewedStartedAtUtc` and `ReviewedDeadlineUtc`. Add an S2S-only synthesis request payload containing input ID, run ID, and parent thread ID. Do not add `review-subagent-snapshot`.

- [ ] Rewrite existing `Reviewed_persists_a_review_artifact_and_skips_optional_arms_by_default`: before barrier/synthesis it now asserts `review-provisional` exists and `review`, judge, and posting outbox do not.
- [ ] RED completion test: after barrier+synthesis, authoritative `review` exists and downstream consumers use it.
- [ ] RED restart test: S2S loads provisional thread/deadline, repeats snapshot stability, and sends synthesis on that same thread.
- [ ] RED accepted-input restart test: existing S2S synthesis input is polled, not resent.
- [ ] RED deadline restart test: original deadline is retained.
- [ ] RED in-process restart policy test: provisional is never promoted/resumed as a live execution; the attempt restarts collect-only.
- [ ] RED failure tests: timeout/snapshot/synthesis failure leaves no authoritative artifact and results in `RetryPending`.
- [ ] Add `ReviewStore.TryGetLatestArtifact`; make the executor's existing `ReadArtifactPayload` delegate to it rather than retaining duplicate lookup logic.
- [ ] Refactor primary review into minimal checkpoints: collect/load provisional; re-query barrier; S2S queue/poll synthesis; persist authoritative review.
- [ ] Extend `RetryGovernor` accounting only for `ReviewBarrierDeadlineException` at `Reviewed`; do not park all transient Reviewed failures. Existing poll interval remains the baseline retry delay.
- [ ] Document rollback procedure for in-flight provisional runs: stop intake and reset to `ContextReady`; never copy provisional to review.
- [ ] Run focused tests; expected GREEN.
- [ ] Commit: `feat(daemon): resume post-barrier review synthesis`.

### Task 7: Reject stale/changed synthesis and reuse canonical delivery

**Files:**
- Modify: `samples/CodeReviewDaemon.Sample/Orchestration/DaemonReviewStageExecutor.cs`
- Modify: `samples/CodeReviewDaemon.Sample/Orchestration/ReviewPoster.cs`
- Test: `tests/CodeReviewDaemon.Sample.Tests/Scenarios/ReviewPosterTests.cs`
- Test: `tests/CodeReviewDaemon.Sample.Tests/Scenarios/DaemonReviewStageExecutorTests.cs`

- [ ] RED: post-synthesis snapshot identical to barrier snapshot allows promotion; any roster/status change rejects the synthesis and posts nothing.
- [ ] RED: PR closes/merges or head changes before synthesis or posting; old-head output is not promoted/posted.
- [ ] RED in-process: canonical summary marker found means no host post; marker absent means one fallback; verification exception also invokes one idempotent fallback; replay remains exactly once.
- [ ] RED S2S: marker verification is never called and `ReviewPoster` receives authoritative synthesis directly.
- [ ] Build the synthesis summary key through existing `IdempotencyKey.Build` using synthesis-specific operation/artifact components. Embed/scan through existing `IdempotencyMarker` and `IReviewCommentPublisher.FindPostedCommentAsync`; add no new publisher method or marker format.
- [ ] Verify the issue-level summary marker only; line-inline comments need not duplicate it.
- [ ] Keep synthesis-generation exceptions fatal; catch only provider-verification failures and route them to fallback.
- [ ] Ensure judge and `CommitPooledNotesAsync` consume only `ReviewArtifactKind`, never provisional.
- [ ] Run tests; expected GREEN.
- [ ] Commit: `feat(daemon): verify authoritative review delivery`.

### Task 8: Prove S2S durability and compatibility end-to-end

**Files:**
- Modify: `samples/LmStreaming.Sample/Controllers/ConversationsController.cs`
- Modify: `samples/LmStreaming.Sample/Persistence/SubAgentProvenance.cs`
- Modify: `samples/CodeReviewDaemon.Sample/Agents/LmStreamingS2SClient.cs`
- Test: `tests/LmStreaming.Sample.Tests/Controllers/ConversationsControllerSubAgentsTests.cs`
- Test: `tests/CodeReviewDaemon.Sample.Tests/Scenarios/LmStreamingS2SClientTests.cs`

- [ ] End-to-end-style test: remove live parent from pool, retain child metadata, request recursive v1, and assert completed/error/stopped plus parent/depth survive.
- [ ] Test root→terminal child→running grandchild provenance: grandchild remains visible and blocks the daemon source.
- [ ] Test deterministic ordering, cycle cut, one bounded scan per endpoint request, and exact auth headers without value logging.
- [ ] Test old non-recursive endpoint compatibility and document deliberate recursive legacy `persisted`→`unknown` mapping.
- [ ] Test daemon-ahead-of-host response fails closed.
- [ ] Run focused and full LmStreaming tests; expected GREEN.
- [ ] Commit: `test(streaming): prove durable review descendant status`.

### Task 9: Full regression and live timing proof

**Files:**
- Modify only files required to repair regressions introduced by Tasks 1–8.
- Update this spec only if verified implementation details differ.
- Runtime evidence: `.run/review-completion-barrier-*` (ignored).

- [ ] Run daemon, LmMultiTurn, and LmStreaming test projects with zero failures.
- [ ] Run full solution with TRX output under `.logs/review-barrier`; no pre-existing failure allowance.
- [ ] Run CSharpier/whitespace verification for changed projects.
- [ ] Launch dedicated review host on 5051 (never production 5050) and isolated daemon DB. Use Terra parent and Sol/Terra-only child resolution.
- [ ] Use multiple concurrent delayed background children completing in different orders; capture provisional, every child terminal, barrier open, synthesis queued, authoritative response, provider post, judge, and completion timestamps.
- [ ] Assert `max(descendantTerminalAt) <= synthesisQueuedAt < authoritativeAnswerAt <= providerPostAt < judgeAt` and total Reviewed duration ≤ configured shared deadline.
- [ ] Repeat with persisted late descendant, errored/stopped child, unknown wire status/version skew, PR lifecycle/head change, and shortened deadline.
- [ ] Keep another PR queued during the delayed fixture and report serial-poller delay as the accepted limitation; do not add poller parallelization in this feature.
- [ ] Verify provider API contains no provisional/stale comment and exactly one authoritative summary.
- [ ] Push and update PR #230 with measured counts, rollout order, and timing evidence.

### Task 10: Re-review the completed implementation

- [ ] Invoke `code-reviewer:pr-review` on final PR head with correctness, concurrency, restart/idempotency, schema compatibility, exception handling, test coverage, over-engineering, and blind-spot lenses.
- [ ] Verify each finding against current code before editing.
- [ ] Implement valid findings one at a time with RED→GREEN tests and separate commits.
- [ ] Re-run Task 9 after any fix.
- [ ] Reply in matching GitHub review threads and resolve only after pushed verification.

---

## Reviewed-plan decisions

- **Removed duplicate marker architecture:** canonical `IdempotencyKey`/`FindPostedCommentAsync` only.
- **Removed duplicate tree checkpoint:** durable child provenance is re-queried after restart.
- **No fabricated in-process InputId:** only S2S queues/polls accepted input IDs; in-process restart reruns collect-only.
- **No loop registry:** one local live loop spans provisional→barrier→synthesis.
- **No hidden nested-delegation enablement:** live source reports current direct roster; S2S recursion is a persisted graph reader.
- **One deadline, not stacked timeouts:** original absolute deadline is persisted and shared by all phases.
- **Synthesis cannot reopen the race:** spawn tool removed and a post-synthesis snapshot verifies stability.
- **Rollout fails closed:** host schema capability first; old flat responses are rejected.
- **Scope held:** serial poller delay is measured/disclosed, not solved by unrelated parallelization.
