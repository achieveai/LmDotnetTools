# Deferred Tool Execution — Design

Sequel to [motivation.md](motivation.md), which argues *why* the primitive should be "deferred tool
result" rather than "approval". This document is the as-built design: what exists in the tree today,
where each claim lives, and which test pins it.

> **motivation.md is stale in two places.** Its "Design implications" section says restart durability
> is "the hardest open question" and that the loop "cannot solve it generically". A well-defined
> slice of it *has* since been solved — see [Restart durability](#restart-durability) — and the
> boundary of that slice is stated there precisely. Its "History as the single source of truth …
> not in a parallel registry" bullet is also superseded: pending state now lives in three places with
> three different jobs (see [Two registries](#two-registries-and-what-each-one-is-authoritative-for)).
> What remains accurate is the placeholder argument, the single-resolution-API argument, and the
> out-of-scope note on cross-process resolution.

## What it is

A tool handler may return **without a result**. The loop writes a placeholder `tool_result` into
history, ends the run, and waits for something outside the loop — a human, a webhook, a worker, a
timer — to supply the real answer. When it arrives, the placeholder is replaced in place and the
conversation continues in a **child run** caused by that result.

```csharp
// Handler side — src/LmCore/Middleware/ToolHandler.cs, src/LmCore/Messages/ToolHandlerResult.cs
delegate Task<ToolHandlerResult> ToolHandler(string argsJson, ToolCallContext context, CancellationToken ct);

return new ToolHandlerResult.Deferred();        // no fields: correlate on context.ToolCallId
return ToolHandlerResult.FromText("done");      // the ordinary synchronous case

// Host side — src/LmMultiTurn/MultiTurnAgentLoop.cs
Task<IReadOnlyList<DeferredToolCallInfo>> GetDeferredToolCallsAsync(CancellationToken ct = default);

Task ResolveToolCallAsync(                       // throws on rejection
    string toolCallId, string result, bool isError = false,
    IList<ToolResultContentBlock>? contentBlocks = null, CancellationToken ct = default);

Task<ResolveToolCallOutcome> TryResolveToolCallAsync(  // reports instead of throwing
    string toolCallId, string result, bool isError = false,
    IList<ToolResultContentBlock>? contentBlocks = null, CancellationToken ct = default);

enum ResolveToolCallOutcome { Resolved, Duplicate, NotFound, Conflict, StoreFailed, Cancelled }
```

Two in-box tools use it: `AskUserQuestionToolProvider` (`src/LmMultiTurn/ClientTools/`) and
`WaitToolProvider` (`src/LmMultiTurn/Triggers/`) — the latter is the whole basis of the
[wait/trigger primitive](../wait-trigger-primitive/README.md).

## Design

### The placeholder contract

Every `tool_use` id in an assistant message must have a matching `tool_result` before the next
inference; neither Anthropic nor OpenAI defines a partially-answered state. So a deferral cannot mean
"omit the result" — it means "write a result that is marked not-yet-real".

`ToolCallResultBuilder.FromHandlerResult` (`src/LmCore/Messages/ToolCallResultBuilder.cs`) is the
single mapping point from handler shape to wire shape. A `Deferred` becomes a `ToolCallResult` with
empty `Result` text, `IsDeferred = true`, and `DeferredAt` stamped. `IsDeferred`, `DeferredAt` and
`ResolvedAt` exist on both `ToolCallResult` and `ToolCallResultMessage`
(`src/LmCore/Messages/ToolCall.cs`), so the marker rides the wire shape and the history shape alike.

The empty text is not a valid request payload, so the loop must never send one.
`MultiTurnAgentLoop.ExecuteTurnAsync` enforces that as a hard precondition: before every provider
request it scans the materialized message list and throws `InvalidOperationException` on the first
`IsDeferred` result it finds, in a `ToolCallResultMessage` or inside a `ToolsCallResultMessage`. The
scan is skipped when `DelayedResultCoordinator.IsEmpty`, so the cost is paid only while something is
actually outstanding; the scan itself is the belt-and-braces check against history rebuilt by a path
that never reached the coordinator.

**Why the throw rather than a wait.** A partially-filled tool-result set is not a state the
conversation can be *in*. Making the request is the bug, not the provider's rejection of it — so the
loop refuses locally, where the stack trace names the unresolved call.

> **Deferral is only resolvable under `MultiTurnAgentLoop`.** `FunctionCallMiddleware`
> (`src/LmCore/Middleware/FunctionCallMiddleware.cs`) routes through the same builder and therefore
> emits `IsDeferred = true` entries inside its `ToolsCallAggregateMessage`, but it owns no resolution
> channel and does not wait — its own class remarks say callers "must inspect the aggregate for
> deferred entries and implement their own resolution policy". `ToolHandlerResult`'s remarks put it
> plainly: in that context "nothing will ever resolve it". Treat a `Deferred()` from a
> middleware-dispatched handler as unsupported unless the caller has built its own loop.

### Two registries, and what each one is authoritative for

Three things hold deferral state, and conflating them is the mistake this design exists to avoid.

| | Where | Authoritative for |
|---|---|---|
| **History** | `ToolCallResultMessage.IsDeferred` in the loop's message list, persisted by `IConversationStore` | What the provider will be sent. It has the final say in every conflict. |
| **In-memory registry** | `DelayedResultCoordinator` (internal, `src/LmMultiTurn/DelayedResultCoordinator.cs`) | Whether a call is outstanding *right now*; commit ordering; which resolution owns the continuation; child-run minting. |
| **Durable rows** | `IRunLifecycleStore` (`src/LmMultiTurn/Persistence/IRunLifecycleStore.cs`) — `run_lifecycle` and `run_deferred_calls` | What a *successor process* can know: that a call deferred, that a resolution committed and with what fingerprint, which child run was named, and which runs started. |

`DelayedResultCoordinator` is the live state machine. Every member takes one lock, because the loop
thread (reserving, parking, draining) and arbitrary caller threads (resolving) both reach it:

- `TryReserve(entry, parked)` / `Release(id)` — a seat is taken when the call **defers**, not when it
  resolves. A resolution arrives on a webhook or UI thread with the result already in hand; turning
  it away for want of capacity would strand the run forever. Reserving on the loop's own thread makes
  a failure an ordinary run error instead.
- `TryPark(runId, generationId, out unresolved)` — marks the turn's outstanding calls parked and
  reports whether the run must end. Marking and deciding happen together under the lock, which closes
  the race against a resolution landing at that instant.
- `TryBeginResolve` / `AbortResolve` / `CompleteResolve` — a claim/commit pair. The claim
  deliberately leaves the entry in the map, so a turn that ends mid-commit still parks correctly.
- `MintChildRunIfParked` — mints the child-run id at a moment when refusing the whole resolution is
  still free.
- `TryDequeueCause` / `RecoverCauses` — the queue of committed results waiting for their child run.

**Why separate at all.** The durable rows answer a question about a *process boundary*; the
coordinator answers a question about *this instant*. Merging them would put a store round-trip inside
the lock that decides ordering, and would make a store outage indistinguishable from "nothing is
outstanding". Keeping history above both is what makes every disagreement resolvable: the code
comments in `ResolveClaimedAsync` and `RecoverOwedContinuationsAsync` both say it — history has the
last word.

The loop never touches `IRunLifecycleStore` directly. `RunTurnLifecycleFinalizer`
(`src/LmMultiTurn/Lifecycle/RunTurnLifecycleFinalizer.cs`) wraps it and fixes the failure policy per
call, which is itself load-bearing:

| Wrapper member | On store failure | With no store configured |
|---|---|---|
| `RecordDeferredToolCallAsync` | **swallowed** and logged — observation must not break a conversation that works without it | no-op |
| `TryResolveDeferredToolCallAsync` | **propagates** — its answer decides whether a resolution is applied | returns `Resolved` |
| `AttachDeferredChildRunAsync` | **propagates** | returns the caller's own id |
| `ListRunLifecycleAsync` | **swallowed**, returns empty | returns empty |
| `IsRunStartDurable(runId)` | — | returns `true`: no store means no recovery, so nothing can double-run |

### Resolution

`ResolveToolCallAsync` (throwing) and `TryResolveToolCallAsync` (reporting) are one implementation,
`ResolveToolCallInternalAsync`, so the two surfaces cannot drift on what counts as an error.

**The outcome taxonomy exists for the webhook receiver.** An exception says something went wrong; it
does not say whether redelivering would help. `ResolveToolCallOutcome`
(`src/LmMultiTurn/ResolveToolCallOutcome.cs`) splits that decision:

| Outcome | Meaning | Redeliver? |
|---|---|---|
| `Resolved` | this attempt resolved the call | no — done |
| `Duplicate` | already resolved with identical content | no — this *is* a successful retry |
| `NotFound` | no such deferred or resolved call on this thread | no — permanent |
| `Conflict` | already resolved (or being resolved) with **different** content; the first stands | no — permanent, and nothing was overwritten |
| `StoreFailed` | the durable store refused the write; the call is untouched | **yes**, unchanged |
| `Cancelled` | the caller's token cancelled before commit; the call is untouched | **yes**, unchanged |

**Idempotency is fingerprint-based.** `ComputeResolutionFingerprint` is
`SHA-256("1\n" or "0\n" + result)` hex-encoded — the error flag and the result text, nothing else.
The fingerprint is held on the in-flight claim (`DeferredEntry.ResolvingFingerprint`) so a second
delivery arriving *mid-commit* can be classified without waiting, and it is written to the durable
row (`DeferredToolCallRecord.ResolutionFingerprint`) so a delivery arriving after a restart can be
classified too. The store never interprets it; it compares ordinally.

**The durable write goes first, then history.** `ResolveClaimedAsync` commits in this order:

1. `Lifecycle.TryResolveDeferredToolCallAsync` — the durable row, carrying the child-run id when the
   claim already had one.
2. `AttachChildRunBeforeHistoryAsync` — settles the child run durably in the two cases the write
   above could not name it (the run parked *during* that write; or the write found the resolution
   already `Duplicate`, in which case the standing id is **adopted** rather than a second one minted).
3. `UpdateToolResultByCallId` → `ApplyResolution` — the in-place history mutation.
4. `ReplacePersistedAsync` and `PublishToAllAsync`.
5. `Lifecycle.ToolCompletedAsync`, then `CompleteResolve`, then `ScheduleLoopWake`.

**Why that order.** Everything before step 3 can be refused for free: history is untouched, the claim
goes back, and the caller may deliver the same result again. A store failure *after* history was
mutated would leave a resolution the process believes happened and the store does not — which no
retry can repair. Steps 1 and 2 are therefore the last points at which "no" is a safe answer, and
both of them say `StoreFailed` rather than throwing something opaque.

`ToolCompleted` is emitted before the cause is committed, so a subscriber always sees the tool finish
before the run carrying its result starts.

Two recovery paths hang off this. `ResolveUnclaimedAsync` handles a placeholder that sits in history
with no reservation covering it — only a host that injected deferred history itself can produce
that — by adopting it pre-parked and running the normal path, rather than letting the result strand.
And a durable `Duplicate` does **not** short-circuit: it means an earlier attempt committed the row
and died before touching history, and the live claim proves history is still unresolved, so finishing
the job is exactly right.

### Child-run continuation

[ADR 0004](../../adrs/0004-delayed-tool-results-as-child-runs.md) is the governing decision. Two
rules come out of it, both implemented in `RunDelayedChildAsync`:

**The cause of a child run is the real `ToolCallResultMessage`** — never a fabricated user message,
never an empty input. The result is already in history (resolution filled the placeholder in place),
so the child carries it by reference for attribution and must not append it again.

**Exactly one child run per batch continues the conversation.** `CompleteResolve` sets
`IsContinuationOwner` from a global test — `_entries.Count == 0`, "nothing is outstanding any more" —
evaluated at commit time, so when several results race, the last to commit owns the continuation. It
is global rather than per-run because the provider request carries the whole history: one unresolved
placeholder anywhere in it invalidates the request regardless of which run left it there.

A non-owning sibling **completes with zero model turns and outcome
`awaiting_sibling_results`** (`LifecycleRunOutcomes.AwaitingSiblingResults`,
`src/LmLifecycle/LifecycleVocabulary.cs`). *This is the contract, not a dropped response.* A
subscriber that sees a completed run with no model output there is seeing a deliberate no-op: the
result was delivered and observed, and the model call was withheld because the tool-result set was
still incomplete. Every resolution is individually observable; the model is called once, with the
full set.

One further gate: the child does nothing irreversible until `Lifecycle.IsRunStartDurable` confirms
the run row landed. The row is the only thing that tells the next process this continuation has
begun. Without it, the child completes as an error and the durable state is left exactly as recovery
wants to find it — the resolution names this child, and no row names it.

Pinned by `tests/LmMultiTurn.Tests/DelayedResultChildRunTests.cs`:
`EachResolvedResultGetsItsOwnChildRun_AndOnlyTheLastTalksToTheProvider`,
`ResolutionOrderDoesNotDecideTheOwner_TheLastOneOutstandingDoes`,
`TheChildCarriesTheRealToolResult_AndTheProviderSeesItExactlyOnce`,
`NoSiblingResultIsAppendedTwice_WhenSeveralResolve`,
`ToolCompletedForTheOriginatingRun_PrecedesTheChildRunsEvents`.

### Restart durability

The slice that is solved: **one process dies and a successor process, reading the same store, picks
up the same thread.** Not concurrent replicas — see [Known gaps](#known-gaps-and-follow-ups).

#### The recovery sequence, in order

`MultiTurnAgentBase.RecoverAsync` (`src/LmMultiTurn/MultiTurnAgentBase.cs`):

1. `LoadMetadataAsync`, `LoadMessagesAsync`, `RestoreHistory`. The placeholder round-trips because
   the whole message is serialized as JSON, and its persisted id is deterministic —
   `MessagePersistenceConverter.BuildToolResultPersistedId` → `tcr:{threadId}:{toolCallId}` — which
   is what lets `ReplaceMessageAsync` later rewrite the row in place without an in-memory index.
2. `OnHistoryRestoredAsync` (overridden in `MultiTurnAgentLoop`):
   1. index every `ToolCallMessage` by id, to recover function name and args;
   2. for each `IsDeferred` result, `_delayed.TryReserve(entry, parked: true)` — **parked on
      arrival**, because the run that requested it belonged to a process that no longer exists, so
      its result can only return as a child run;
   3. `LoadSuppressedRunMarkerAsync` — restore the no-spawn guarantee the parked run made;
   4. `RestoreRecoveryBudgetAsync` — restore the stream-recovery budget the park had spent;
   5. `TryPark` on the most recent deferring run/generation;
   6. `RecoverOwedContinuationsAsync`;
   7. Wait reconciliation.
3. `OnThreadRecoveredAsync` → `TriggerRuntime.RestoreNotifyWaitsAsync`. Runs even for threads with
   zero persisted messages, because `notify_waits` rows are keyed by thread in their own table.
4. Later, on the start path: `ReconcileRunLedgerAsync`, then
   `Lifecycle.ReconcileInterruptedRunsAsync`, which terminalizes every run
   `ListNonTerminalRunsAsync` returns as `interrupted`. Ordering against step 2.6 is immaterial:
   `RecoverOwedContinuationsAsync` builds its "already started" set from *all* run rows regardless of
   phase.

`RecoverOwedContinuationsAsync` is the centre of it. It asks for exactly one thing: a durable
deferred-call record that **is resolved**, **names a child run**, and for which **no run row with
that id exists**. That triple is precisely a continuation this thread is owed. Reusing the recorded
child-run id rather than minting a fresh one is what makes a second crash safe — the run row is the
"begun" marker, and reusing the id means the marker can only be written once. History is then
consulted for the resolved result; a record naming a result history does not hold is logged and
skipped. `DelayedResultCoordinator.RecoverCauses` re-queues them oldest-first and re-decides
ownership under the same lock and by the same rule: at most one recovered cause continues, and only
when nothing is still outstanding.

#### What survives

| # | Guarantee | Pinned by |
|---|---|---|
| G1 | An unresolved deferral is rebuilt from persisted history and reappears in `GetDeferredToolCallsAsync` | `DeferredToolExecutionTests.OnHistoryRestoredAsync_RebuildsDeferredRegistry_FromPersistedHistory` |
| G2 | A deferral left by a dead process resolves normally in the successor | `DelayedResultChildRunTests.ADeferralRestoredFromAnotherProcess_ResolvesNormally` |
| G3 | A continuation committed durably but never run resumes **exactly once** after a restart | `DelayedResultDurableContinuationTests.AContinuationCommittedButNeverRun_ResumesExactlyOnceAfterARestart` |
| G4 | …and is not recovered a second time by a second restart | `DelayedResultDurableContinuationTests.ARecoveredContinuationIsNotRecoveredASecondTime` |
| G5 | A child run whose start could not be durably recorded is **not run**, and stays recoverable | `DelayedResultDurableContinuationTests.AChildRunThatCannotBeRecordedAsStarted_IsNotRunAndIsRecoveredExactlyOnce` |
| G6 | A resolution racing its own run's parking still names its child run durably | `DelayedResultDurableContinuationTests.AResolutionThatRacesParking_DurablyNamesTheChildRunItCauses` |
| G7 | A child run another process already named is **adopted**, not duplicated | `DelayedResultDurableContinuationTests.WhenAnotherProcessAlreadyNamedTheChild_ThisOneAdoptsItRatherThanStartingASecond` |
| G8 | A redelivery of the same result is a `Duplicate`; one with different content is a `Conflict` that changes nothing — across **all three** stores | `RunLifecycleStoreTestsBase.TryResolveDeferredToolCall_SameFingerprintTwice_IsADuplicate`, `..._DifferentFingerprint_IsAConflictAndChangesNothing` |
| G9 | Concurrent resolutions of one call commit exactly once, durably and in-process | `RunLifecycleStoreTestsBase.TryResolveDeferredToolCall_ConcurrentCallers_ExactlyOneResolves`; `DelayedResultChildRunTests.ConcurrentDeliveriesOfOneResult_CommitExactlyOnce` |
| G10 | Concurrent child-run naming converges on one child | `RunLifecycleStoreTestsBase.AttachDeferredChildRun_ConcurrentCallers_AllAgreeOnOneChild` |
| G11 | The parked run's **spawn-suppression** guarantee is kept by the recovered continuation | `DelayedResultDurableContinuationTests.ARecoveredOwedContinuation_RetainsItsPersistedSpawnSuppression` |
| G12 | The parked run's **stream-recovery budget** is not refunded by the crash | `InterruptedDeferralRecoveryTests.RecoveryBudgetSurvivesARestart_SoAResumedInputStillGetsNoSecondRecovery`, `...RecoveryBudgetIsSpentOncePerLogicalInput_EvenAcrossAParkAndResume` |
| G13 | An interrupted turn holding an unresolved deferral parks rather than continuing, and asks only once | `InterruptedDeferralRecoveryTests.InterruptionWithAnUnresolvedDeferral_ParksInsteadOfContinuing_AndAsksOnlyOnce` |
| G14 | A restored `Wait` is never left hanging: restorable sources re-arm, non-restorable resolve `trigger_lost_on_restart`, and a host with triggers disabled resolves `trigger_disabled` — one failure not stranding the rest | `DeferredToolExecutionTests.OnHistoryRestoredAsync_ResolvesRestoredWait_AsTriggerDisabled_WhenNoTriggerRuntime`, `...OnHistoryRestoredAsync_IsolatesTriggerDisabledFailures_SoOneConflictDoesNotStrandOthers` |

The store conformance suite is one abstract class,
`tests/LmMultiTurn.Tests/Persistence/RunLifecycleStoreTests.cs` →
`RunLifecycleStoreTestsBase`, run against all three implementations via
`InMemoryRunLifecycleStoreTests`, `FileRunLifecycleStoreTests`, `SqliteRunLifecycleStoreTests`.
The SQLite shape is in `src/LmMultiTurn/Persistence/Sqlite/SqliteSchemaInitializer.cs`:
`run_deferred_calls` is a **table**, keyed `PRIMARY KEY (thread_id, tool_call_id)`, precisely so
resolving is one conditional single-row UPDATE rather than a read-modify-write on a JSON column.
`AttachDeferredChildRunAsync` is the same shape, guarded `resolved_at IS NOT NULL AND child_run_id IS
NULL` — put-if-absent that reports the standing winner.

#### What does not survive

- **Handler-initiated outbound work.** An HTTP request already in flight, a queue publish, a job
  submission — none of it is tracked anywhere. `DeferredToolCallRecord` holds the tool name, ordinal,
  timestamps, fingerprint and child run, and nothing about what the handler did. The handler owns its
  own idempotency and its own recovery; `GetDeferredToolCallsAsync` on startup is the metadata the
  loop offers to support that.
- **The in-flight claim.** `DeferredEntry.Resolving` and `ResolvingFingerprint` are memory-only. A
  crash mid-commit is repaired by the durable `Duplicate` path, not by restoring the claim.
- **Ordinals and the pending-cause queue.** Both are rebuilt, not restored. `RecoverCauses` assigns
  fresh ordinals and re-decides ownership from the recovered set.
- **A resolution recorded with no child run.** Deliberately *not* recovered. That record says the
  result was folded into a run that was still going, so its continuation was that run's own next
  turn — recovering it here would be indistinguishable from resuming any interrupted run, and would
  take a turn nobody asked for.
- **Function arguments, if the originating `ToolCallMessage` is gone.** The durable record carries no
  args. `OnHistoryRestoredAsync` recovers them from the indexed `ToolCallMessage`, falling back to
  `"{}"`; `DeferredAtUnixMs` falls back to `0`. Silently.
- **Anything at all under `InMemoryConversationStore`**, which is a store for tests and development
  by construction.
- **Continuation recovery without a lifecycle store.** `RecoverOwedContinuationsAsync` returns
  immediately when `ListRunLifecycleAsync` is empty — and that call is best-effort, so a store that
  *cannot be read* at restart degrades to "recover from history alone" with only a log line.

### A deferred call's lifecycle

```
        handler returns ToolHandlerResult.Deferred()
                          │
                          ▼
  ┌───────────────────────────────────────────────┐
  │  Reserved            _delayed.TryReserve      │  placeholder appended to history,
  │  (requesting run still executing)             │  run_deferred_calls row written
  └───────┬───────────────────────────────┬───────┘
          │ turn ends, still outstanding  │ resolves before the turn ends
          │ (TryPark)                     │
          ▼                               ▼
  ┌───────────────────┐          ┌──────────────────────────┐
  │  Parked           │          │  Folded into the live run│  no child run; the run's
  │  run has ended    │          │  (ChildRunId == null)    │  own next turn continues
  └───┬───────────┬───┘          └──────────────────────────┘
      │           │
      │           └────── process dies ──────┐
      │                                      ▼
      │                        ┌──────────────────────────────────┐
      │                        │  Restored (parked on arrival)    │
      │                        │  OnHistoryRestoredAsync          │
      │                        └───────────────┬──────────────────┘
      │                                        │
      └────────────────┬───────────────────────┘
                       ▼  Resolve/TryResolveToolCallAsync
             ┌──────────────────────┐
             │  Claimed             │──► StoreFailed / Cancelled ──► back to Parked (retry safe)
             │  TryBeginResolve     │──► Duplicate  (same fingerprint)
             └──────────┬───────────┘──► Conflict   (different content; first stands)
                        │ durable row ✓  →  child run named ✓  →  history rewritten ✓
                        ▼
             ┌──────────────────────┐
             │  Resolved            │  placeholder replaced in place; ToolCompleted emitted
             └──────────┬───────────┘
                        ▼  CompleteResolve queues a cause
             ┌──────────────────────────────────────────────┐
             │  Child run (cause = the real tool result)    │
             ├──────────────────────────────────────────────┤
             │  IsContinuationOwner  → calls the provider   │
             │  otherwise            → completes with       │
             │                         awaiting_sibling_results (0 turns)
             └──────────────────────────────────────────────┘
                        │
                        └── process dies before the child run's row lands
                                       │
                                       ▼
                        ┌──────────────────────────────────────────┐
                        │  Owed continuation                       │
                        │  RecoverOwedContinuationsAsync finds a   │
                        │  resolved record naming a child run with │
                        │  no run row → RecoverCauses re-queues it │
                        │  under the SAME id (so never twice)      │
                        └──────────────────────────────────────────┘
```

## Known gaps and follow-ups

Each of these is concrete enough to file as-is.

1. **`AttachDeferredChildRunAsync` is optional, so continuation-survives-restart is opt-in per
   store, not an interface guarantee.** The interface default throws `NotSupportedException`
   (`src/LmMultiTurn/Persistence/IRunLifecycleStore.cs`). All three in-box stores implement it, but a
   third-party `IRunLifecycleStore` compiles without it, and both call sites
   (`AttachChildRunBeforeHistoryAsync`, `ReportLateChildRunAsync`) deliberately swallow the throw and
   log: the continuation runs in this process but is unrecoverable across a restart. Pinned as
   *tolerated* by `DelayedResultDurableContinuationTests.AStoreFromBeforeChildRunNamingExisted_*`.
   Options: promote it to a required member on the next interface version, or surface the
   degradation as a startup capability check rather than a per-resolution warning.
2. **Handler-initiated outbound work is never tracked.** Nothing durable records that a handler fired
   an HTTP request or published to a queue. On restart the loop knows a call is outstanding and
   nothing about what is in flight for it. The handler owns its own idempotency. If the framework
   should help here, the shape would be an opaque handler-supplied blob on `DeferredToolCallRecord`,
   returned by `GetDeferredToolCallsAsync`.
3. **`FunctionCallMiddleware`-dispatched deferrals are unresolvable by construction.** They produce a
   permanent `IsDeferred = true` entry with no resolution channel. Today this is documented in
   remarks only. It should probably fail loudly — either the middleware rejects a `Deferred()` return
   outright, or `FunctionRegistry` refuses to register a deferring handler on a middleware-only path.
4. **A conflicting redelivery is refused, never merged.** `Conflict` means the first resolution
   stands and the second is discarded with nothing recorded about it beyond the caller's return
   value. There is no dead-letter, no event, and no way for an operator to see that a webhook
   delivered a contradicting answer. Refusing is right; the silence is the gap.
5. **No cross-replica or concurrent-process resolution.** The model is strictly
   *one process, succeeded by another*. The durable layer would serialize two writers correctly —
   both `TryResolveDeferredToolCallAsync` and `AttachDeferredChildRunAsync` are conditional
   single-row updates — but nothing in `src/LmMultiTurn/Persistence` leases a thread to one process
   (`IConversationOwnershipStore` is tenancy, not a lease). Two live loops on one thread would each
   hold their own history and their own `DelayedResultCoordinator`, and the `IsRunStartDurable` /
   "already started" fences only stop a *successor*, not a *peer*. motivation.md still lists this as
   out of scope, correctly.
6. **The durable record carries no function arguments.** After a restart,
   `DeferredToolCallInfo.FunctionArgs` degrades to `"{}"` and `DeferredAtUnixMs` to `0` when the
   originating `ToolCallMessage` is absent from restored history — silently, with no warning logged.
   A host correlating on args rather than on `ToolCallId` would see an empty object and no signal
   that it was a degradation.
7. **The resolution fingerprint ignores content blocks.** `ComputeResolutionFingerprint` hashes only
   the error flag and the result text. Two deliveries that differ *only* in
   `IList<ToolResultContentBlock>` are therefore a `Duplicate`, and the second delivery's blocks are
   never published to subscribers. Correct for history (which is text-only by design), arguably wrong
   for the subscriber stream.
