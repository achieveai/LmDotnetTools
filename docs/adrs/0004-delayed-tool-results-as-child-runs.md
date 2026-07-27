# ADR 0004: Resolve delayed tool results as serialized child runs caused by the real tool result

* Status: Accepted
* Date: 2026-07-27
* Related issues, PRs, or commits: [#227](https://github.com/achieveai/LmDotnetTools/issues/227)

## Context

A tool call may not complete within the run that requested it. `MultiTurnAgentLoop`
already supports this: the call is recorded as deferred, a placeholder result with
`IsDeferred = true` is written to history, and the run finishes. Later,
`ResolveToolCallAsync` (`src/LmMultiTurn/MultiTurnAgentLoop.cs:941`) supplies the real
result, and `TryScheduleAutoResume` arranges for the conversation to continue.

The existing resume mechanism has a shape that does not survive contact with an
observational event contract. `EnqueueResumeSentinel`
(`MultiTurnAgentLoop.cs:1242-1255`) enqueues a `QueuedInput` carrying a `ResumeSentinel`
and a `UserInput` with an **empty message list** and `ParentRunId` set to the deferring
run. `RunLoopAsync` drains it and calls `StartRunAsync`, which mints a brand-new `RunId`
and `GenerationId` — the sentinel's identifiers are correlation-only. So the parent
linkage is already correct, but the *cause* of the resumed run is an empty input rather
than the tool result that actually caused it.

That matters because a subscriber reconstructing the conversation would see a run with no
cause. The two obvious repairs are both wrong. Reporting no cause at all leaves an
unexplained run in the record. Fabricating a synthetic user message to stand in for the
result corrupts the transcript: it asserts the user said something they did not, and any
consumer replaying history would feed that fiction back to the model.

There is a second problem. Several deferred calls from the same turn can resolve at
different times, in any order. If each resolution independently drove a provider
continuation, the model would be called several times for one logical turn, with partial
tool results each time.

## Decision

A delayed tool result emits its `ToolCompleted` event when it resolves, regardless of the
state of the run that requested it.

If the requesting run has already reached a terminal boundary, each resolved result queues
**its own child run**, carrying `ParentRunId` set to the requesting run and `WasForked`
set to `false`. A child run is a continuation, not a fork: `WasForked` reflects caller
intent about provider-context inheritance and nothing about lineage, as its own
documentation states (`src/LmMultiTurn/Messages/RunCompletedMessage.cs:32`).

**The cause of a child run is the real `ToolCallResultMessage`** — never a fabricated user
message, and never an empty input. This is the substantive change to the existing resume
path: the mechanism that mints the run is unchanged, but the cause it carries becomes the
actual result.

**Child runs are serialized, and exactly one of them continues the conversation.** When
several deferred calls from the same turn resolve, each gets its own child run, but a
non-final sibling completes with **zero model turns** and the terminal outcome
`awaiting_sibling_results`. Only the child that clears the last unresolved call in the
batch performs the provider continuation. Every resolution is therefore individually
observable — each has a run with a real cause and a real completion — while the model is
called exactly once, with the complete set of results.

## Consequences

A consumer can now account for every delayed result: it sees the `ToolCompleted` when the
result arrives, a child run causally linked to the requesting run, and an explicit
terminal outcome distinguishing a sibling that deliberately did no work from one that
continued the conversation. The conversation transcript stays truthful — no synthetic user
turn is ever introduced.

This is the one intentional, documented deviation from the "byte-for-byte baseline"
regression bar. The resumed run's causal input changes from an empty input to the real
tool result, which changes what the provider is sent on resume. That is the point of the
change: the previous behavior was to resume with no stated cause. Anything depending on
the old empty-input shape must be updated, and the delayed-result timing change is called
out explicitly in the release notes.

Serialization means the last result in a batch determines when the conversation continues,
so one slow resolution holds the continuation. That is inherent to needing the complete
result set for a single provider call, and it is preferable to the alternative of calling
the model repeatedly with partial results.
