# ADR 0006: Order a workflow transition against the run observer with a publish-order watermark

* Status: Accepted
* Date: 2026-07-27
* Related issues, PRs, or commits: [#227](https://github.com/achieveai/LmDotnetTools/issues/227)

## Context

`LmWorkflow` learns about the two kinds of work that drive a run through two different
channels, and they are not ordered against each other.

* **Transitions are inline.** `SetCurrentNode` is an ordinary workflow tool, so
  `WorkflowToolProvider.HandleSetCurrentNodeAsync` runs on the controller loop's own thread
  and mutates `WorkflowRuntime` synchronously. Routing into a terminal renders that node's
  `resultTemplate` from the live state channels inside `AdvanceTo`, deliberately *before*
  mutating anything, so a schema failure cannot leave a half-advanced run.
* **Sub-agent results are observed out of band.** They reach the runtime only through
  `WorkflowSession.DriveAndObserveAsync`, which enumerates `loop.ExecuteRunAsync(...)` — a
  bounded channel (capacity 1000, `FullMode = Wait`) that the loop writes to and then runs
  on past. `WorkflowRuntime.ObserveMessage` explicitly "runs on the host's out-of-band
  observer thread (not under `_lock`)".

So the observer lags the loop by up to a channel's worth of messages, and nothing gated a
transition on it catching up. Under load this surfaced as a terminal result composed from
state that was missing a sub-agent write the controller had already collected: the runtime's
own `state.authored` held two entries while the rendered result held one.
`DriveAndObserveAsync`'s doc comment — "each message is observed in publish order (so a
sub-agent result is recorded before any later transition is reached)" — is true of ordering
*among observed messages*, but says nothing about ordering against the loop's own inline tool
execution, which is the ordering that actually matters.

Three obvious barrier predicates were each ruled out by a legitimate case:

* **"the completed node's join policy is satisfied"** — a terminally `Failed` unit never
  validates, so a legitimate `onFailure` route would hang forever.
* **"every unit has settled"** — `Pending` is the initial state after `Compose()`, so a
  controller legitimately routing away from a composed-but-deliberately-unspawned node would
  stall.
* **"no unit is in flight"** — under the very race being closed, the spawn registration may
  itself be unobserved, so units still read `Pending` while their results are already
  published. The predicate is satisfied at exactly the moment it must not be.

Feeding results in synchronously instead was not available either: `MultiTurnAgentLoop`
builds its handlers from `functionRegistry.BuildToolCallComponents` and never routes through
`FunctionCallMiddleware`/`ToolCallExecutor`, so `IToolResultCallback` is not reachable from
the loop at all.

## Decision

A transition waits on **publish order**, not on task status. Before `AdvanceTo`, the
`SetCurrentNode` handler waits until the run observer has processed the message carrying
**its own tool-call id**.

That id is a sound watermark because of two properties the pipeline already guarantees.
`MessagePublishingMiddleware` publishes each message *before* yielding it downstream, so a
tool call reaches subscribers before the loop invokes its handler; and the loop awaits all of
a turn's pending tool calls before starting the next turn, so every earlier turn's results are
published before the routing turn streams at all. The observer consumes the channel FIFO.
Therefore, once the observer has reached the routing call, every message published before it —
including every prior sub-agent result — is already correlated.

Both `ToolCallMessage` and `ToolCallUpdateMessage` ids are recorded as watermarks. Which shape
reaches subscribers depends on the provider: a mocked agent yields the finalized call, while a
real provider publishes only streaming fragments and the finalized message never leaves the
loop. Watermarking one shape only would have made every live transition wait out the timeout.

The barrier is **opt-in and self-limiting**:

* It engages only after `WorkflowSession` declares an ordered observer, so a host driving the
  runtime directly — with no observer at all — never stalls.
* Its bookkeeping is guarded by a dedicated lock, never the runtime's `_lock`, because the
  observer signals waiters from inside `ObserveMessage` while a loop thread may be inside
  `AdvanceTo` holding `_lock`. Waiters complete with `RunContinuationsAsynchronously` and are
  signalled outside the lock, so releasing one can never run the routing handler's
  continuation on the observer thread and re-enter the runtime.
* A watermark that never arrives times out after ten seconds, logs once, and **disables the
  barrier for the remainder of the run**, degrading to the previous behaviour rather than
  taxing every subsequent transition.
* The observed-id history is a bounded FIFO, so it cannot grow across a long-lived controller
  conversation.

Composing the terminal result at completion rather than at transition was rejected: it would
give up `AdvanceTo`'s validate-before-mutate property, which is a stronger guarantee than the
one being repaired.

## Consequences

A terminal's `resultTemplate` now renders from state that includes every result the controller
had already collected, so a workflow's final result no longer depends on how the scheduler
happened to interleave the loop and its observer. The fix is deterministic rather than
probabilistic: the regression tests interleave observations by hand and fail without the
barrier, instead of relying on load to reproduce.

The cost is a new ordering dependency from the workflow tool layer onto the host's observation
contract — a transition is now correct only if the host really is a single ordered consumer of
every published message. `WorkflowSession` is that consumer and declares itself as one; any
future host that wires `ObserveMessage` itself must do the same to get the guarantee, and gets
today's unbarriered behaviour if it does not.

This orders transitions against **observed** results only. It does not make the observer
itself synchronous, and it does not bound how far the observer may lag between transitions.
