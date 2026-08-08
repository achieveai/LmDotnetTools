# Resilient Stream Delivery, Interrupted-Turn Recovery, and Agent Waiting

**Status:** Approved design
**Date:** 2026-08-06

## Problem

Large LmStreaming conversations can produce update fragments faster than a browser subscriber consumes them. The server deliberately drops a subscriber whose bounded channel fills, but the drop currently becomes an ordinary clean WebSocket close. The browser neither reports the loss nor reconnects, so the UI remains stuck in a streaming state. Reconnecting manually can then hit the shared in-flight replay buffer's count or byte cap because that buffer stores raw deltas even though complete messages are sufficient to reconstruct the conversation.

A related provider failure occurs when a streaming HTTP response ends prematurely. Completed messages should be preserved and used as context for an automatic continuation; an attempt that produced only incomplete fragments can be safely abandoned and retried as a new generation.

Finally, Workspace Agent can spawn sub-agents but, with collaboration disabled, exposes only `CheckAgent`, not a blocking agent wait. `WaitWorkflow` is then the closest-looking wait tool and has insufficient guidance and recovery for invalid workflow IDs.

## Goals

1. Keep publisher memory and latency bounded without freezing large-conversation viewers.
2. Make complete messages canonical; deltas remain optional live animation.
3. Automatically resynchronize a browser after subscriber eviction or socket loss.
4. Recover safely from retryable interrupted provider streams without duplicating tool effects.
5. Give Workspace Agent a coherent blocking wait for spawned agents in both collaboration modes.
6. Default collaboration on for Workspace Agent while preserving explicit operator override.

## Non-goals

- Guarantee lossless replay of every historical delta.
- Add unbounded per-subscriber queues or replay storage.
- Retry a partially consumed provider request in place with the same generation.
- Enable collaboration by default for every chat mode.
- Change provider APIs or require provider-side idempotency support.

# 1. Canonical Message Delivery

## 1.1 Message classes

Messages are divided by recovery semantics.

### Expendable live updates

These may be sent to a healthy live subscriber but are never required for reconstruction and never enter reconnect replay:

- `TextUpdateMessage`
- `ReasoningUpdateMessage`
- tool-call argument update fragments
- JSON fragment updates
- other provider fragment/update-only messages

### Canonical and control messages

These form the catch-up bridge and durable conversation state:

- run assignment and run-state controls
- complete text and reasoning messages
- complete tool calls and tool results
- notifications and resolved client-tool messages
- usage aggregates
- run completion/error controls

Persisted complete messages are the primary source of truth. A small bounded canonical/control replay bridge covers messages published after the REST snapshot but before the replacement subscription becomes live.

## 1.2 Live delivery

The existing non-blocking publisher invariant remains:

- a slow subscriber cannot block the provider run or other subscribers;
- each subscriber has a bounded channel;
- channel saturation evicts only that subscriber;
- raw live deltas may be lost for that subscriber.

Subscriber eviction must no longer masquerade as ordinary completion. The server should expose a distinguishable resynchronization outcome for diagnostics, such as a `resync_required` control frame or a dedicated close reason. Client correctness must not depend exclusively on that reason: any socket close while authoritative run state is still active triggers resynchronization.

## 1.3 Automatic single-flight resynchronization

When the active conversation's socket closes before a run-complete signal:

1. Invalidate the old socket epoch so late callbacks cannot mutate current state.
2. Start one single-flight resync for the selected thread.
3. Fetch complete persisted messages through REST.
4. Merge them using existing stable message identity.
5. Fetch authoritative run state.
6. If idle, clear loading state.
7. If active, open a subscribe-only WebSocket at the current live edge.
8. Merge the small canonical/control bridge with the REST snapshot.
9. Reconcile once more after run completion.

The client may show a subtle `Catching up…` state only when recovery takes long enough to be noticeable. A successful recovery does not produce a persistent error banner.

Resync attempts use bounded backoff and are scoped to the thread/selection epoch. Repeated socket loss cannot create parallel connections or an unbounded reconnect loop.

## 1.4 Partial UI repair

An expendable partial bubble is keyed to its generation and message identity. A canonical full message replaces or finalizes that block. When an attempt is abandoned, an explicit control identifies the abandoned generation so the client removes only its unfinalized blocks.

## 1.5 Replay bounds

The replay facility remains bounded but excludes raw update fragments. The existing 10,000-message / 8 MiB raw-event buffer is replaced or specialized into a much smaller canonical/control bridge. Limits remain configurable and continue protecting process memory.

# 2. Interrupted Provider-Stream Recovery

## 2.1 Attempt observation

Each provider attempt tracks, in order:

- update fragments seen,
- canonical messages completed,
- trailing incomplete message kind/generation,
- complete tool calls emitted,
- tool executions dispatched and their terminal results,
- externally visible notifications/client questions,
- recovery count for the logical input.

A transport failure is recoverable only when classified as transient (for example `HttpIOException` with `ResponseEnded`) and cancellation was not requested.

## 2.2 Recovery rule

### No canonical message completed

If only incomplete updates were observed:

1. Mark the failed generation abandoned.
2. Do not persist its fragments.
3. Tell the client to discard its unfinalized blocks.
4. Mint a new generation ID.
5. Retry the original turn once from its original history snapshot.

### One or more canonical messages completed

If any message completed:

1. Preserve every completed canonical message in history.
2. Discard only the incomplete trailing message/fragments.
3. Await every fully emitted/dispatched tool call until it has a terminal result.
4. Never re-execute a completed tool call or human-visible effect.
5. Enqueue an internal continuation sentinel.
6. Start a new internal turn with a new generation ID.
7. Instruct the provider internally to continue from completed history without repeating finished work.

Completed reasoning, text, tool interactions, notifications, and resolved client questions are all treated as finished work and become context for the continuation.

## 2.3 Internal continuation

Extend the existing internal `ResumeSentinel` mechanism rather than adding a visible user message. The sentinel records:

- interrupted run and generation,
- recovery reason,
- whether this is a retry of an empty attempt or continuation after completed output,
- logical recovery count.

It is operational metadata and does not render as a user bubble. The provider receives an internal continuation instruction derived from the sentinel.

## 2.4 Safety and limits

- One automatic recovery transition is allowed per logical user input.
- A second retryable interruption completes with a classified stream-interruption error.
- User cancellation never triggers recovery.
- The loop must cancel/await or otherwise terminally account for all per-attempt tool tasks before recovery begins; no orphaned background execution may race the continuation.
- Every replacement attempt or continuation gets a new generation ID.

# 3. Workspace Agent Collaboration and Waiting Tools

## 3.1 Mode-specific collaboration default

Collaboration configuration becomes an explicit override rather than a single global boolean default:

- explicit `true`: collaboration enabled;
- explicit `false`: collaboration disabled;
- unspecified: enabled for Workspace Agent, disabled for other ordinary modes.

Workspace Agent therefore defaults to:

- `Agent`
- `SendMessage`
- `GetAgents`
- `CheckAgents`
- `WaitForAgents`

Other ordinary modes retain legacy behavior unless explicitly enabled. Workflow controllers retain their intentionally restricted collaboration/controller surface. Operators may explicitly disable collaboration for Workspace Agent.

## 3.2 Legacy fallback

When collaboration is explicitly disabled, Workspace Agent receives:

- `Agent`
- `SendMessage`
- `CheckAgent`
- `WaitAgent`

`WaitAgent(agent_id, timeout_seconds)` blocks for one direct sub-agent and returns a terminal result/error or a bounded running/timed-out status. It accepts IDs returned by `Agent` and is not registered when collaboration is enabled.

When collaboration is enabled, no `WaitAgent` alias is added; `WaitForAgents` is the only blocking agent-wait tool.

## 3.3 Workflow-tool clarity

- `WaitWorkflow` and `CheckWorkflow` explicitly state that they accept only the `workflowId` supplied to `StartWorkflowAgent` and never accept an `agent_id`.
- `WaitAgent`/`WaitForAgents` explicitly state that they accept IDs returned by `Agent`, not workflow IDs.
- Add a read-only `GetWorkflows` over the existing workflow run listing.
- Unknown workflow results include known workflow IDs and corrective guidance.
- Keep caller-chosen workflow IDs for compatibility, but make that contract explicit.
- Register the current `StartWorkflowAgent` name in the client's workflow renderer while retaining the historical alias where needed.

# 4. Error Handling and Observability

Structured logs and control frames must distinguish:

- subscriber eviction / resync required,
- ordinary run completion,
- network socket closure during an active run,
- provider attempt abandoned before canonical output,
- provider continuation after canonical output,
- recovery exhausted,
- tool tasks awaited during recovery,
- workflow ID versus agent ID validation failures.

No Debug-or-higher log includes prompt, message, or tool-result content. Correlation uses thread, run, generation, subscriber, and recovery-attempt identifiers.

# 5. Test Strategy

## 5.1 Canonical delivery and resync

- A stalled primary subscriber is evicted without blocking the publisher and the browser automatically resynchronizes.
- A stalled sub-agent subscriber has identical recovery semantics.
- More than 10,000 deltas do not enter catch-up replay.
- Complete messages remain recoverable and finalize/replace partial UI blocks.
- The REST-snapshot/subscription-edge race produces neither gaps nor duplicates.
- A late callback from an obsolete socket cannot mutate a newly selected conversation.
- Repeated drops produce bounded reconnect attempts and never leave `isLoading` permanently true.
- Run completion during resync ends loading without opening an unnecessary socket.

## 5.2 Interrupted streams

- Updates only followed by `ResponseEnded` abandons the generation and retries the original turn once.
- Completed reasoning followed by partial text and `ResponseEnded` preserves reasoning and starts an internal continuation turn.
- Completed text followed by interruption is preserved and continued in the next turn.
- Completed tool calls are awaited; their effects execute once.
- Partial tool-call fragments are discarded and never executed.
- Notification and client-question effects are not repeated.
- A second interruption produces a classified terminal error.
- Cancellation produces no retry or continuation.

## 5.3 Tool exposure

- Workspace Agent with unspecified collaboration config gets collaboration tools by default.
- Explicit collaboration disable gives legacy `CheckAgent` plus `WaitAgent`.
- `WaitAgent` is absent when collaboration is enabled.
- Other ordinary modes retain their existing default.
- `WaitWorkflow` rejects agent IDs with corrective text.
- Unknown workflow output lists valid workflow IDs.
- `GetWorkflows` recovers a lost ID.
- `StartWorkflowAgent` uses the workflow renderer in the client.

# 6. Delivery Order

1. Canonical-only replay classification and server resync signaling.
2. Client automatic REST-first resynchronization with socket epochs and bounded reconnect.
3. Interrupted-stream attempt tracking and recovery sentinel.
4. Mode-specific collaboration default and legacy `WaitAgent`.
5. Workflow discovery/guidance and client renderer correction.

Each phase has its own RED→GREEN tests and may land independently without weakening the existing bounded-memory or non-blocking-publisher invariants.
