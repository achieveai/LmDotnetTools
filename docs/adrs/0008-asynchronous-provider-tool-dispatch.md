# ADR 0008: Dispatch provider tool requests off the stdio read loop, bounded and refusing

* Status: Accepted
* Date: 2026-07-27
* Related issues, PRs, or commits: [#227](https://github.com/achieveai/LmDotnetTools/issues/227)

## Context

`CodexAppServerTransport` and `CopilotAcpTransport` both speak stdio JSON-RPC to a CLI
they launched, and both read it the same way: one sequential loop over stdout lines.
That stream is multiplexed. It carries the CLI's notifications, its responses to
requests we made, and — the part that matters here — the requests it makes of *us*:
`session/request_permission`, `fs/read_text_file`, dynamic tool invocations.

Both transports awaited the handler for those inbound requests **inline**, on the read
loop. The next line was not read until the current handler returned. That is fine while
handlers are cheap, and it was fine before #227, because nothing a handler did could
take longer than a file read.

The approval gate breaks it. A tool call parked waiting on a human decision holds the
read loop, and the read loop is the only thing that can deliver what would unpark it:
the approval decision, the cancellation that would abandon the wait, the rest of the
turn's events, and any second tool call. The failure is not "slow" — it is closed. The
run cannot fail cleanly, because the signal that would fail it arrives through the
stream the handler is blocking. It can only time out.

Two obvious repairs are both wrong. Fire-and-forget on every inbound request removes
the stall and replaces it with unbounded growth: a peer that floods requests, or a
session where every tool call parks on an approval, accumulates in-flight handlers with
nothing to stop it. Queueing at capacity is the same failure wearing a bound — the
queue drains only when handlers finish, and handlers waiting on people may not.

## Decision

**Run inbound requests off the read loop, through a bounded dispatcher that refuses
rather than queues at capacity.**

One shared primitive, `BoundedServerRequestDispatcher` in `src/LmCore/Transport/`,
serves both transports. The read loop hands it a handler and moves to the next line
immediately. The dispatcher yields before invoking the handler, so a handler that
blocks its thread rather than awaiting still cannot reach back and stall the reader.

At capacity `TryDispatch` returns false and **the transport answers the request
itself** with JSON-RPC error `-32000`. Refusal lives with the caller because the caller
is the only party that knows how to correlate a refusal back to the peer — the
dispatcher deliberately knows nothing about ids, JSON, or the outbound stream. The same
path answers requests that arrive after the transport has stopped; the two cases are
distinguished in the log by `transport_stopped` versus `dispatcher_saturated` rather
than by different behaviour on the wire.

Capacity is `MaxConcurrentServerRequests = 8` per transport. It is a backstop against a
misbehaving peer, not a throughput knob: a session with eight tool calls simultaneously
parked at approval gates is already pathological, and a bounded refusal is a better
answer to it than an unbounded wait.

Responses therefore complete **out of order**, which is what the JSON-RPC id is for. No
new correlation machinery was needed. Outbound writes were already serialized in both
transports by a `SemaphoreSlim` inside `WriteJsonLineAsync`, so concurrent handlers
finishing at once interleave at line granularity, not byte granularity.

Shutdown cancels in flight. `StopAsync` disposes the dispatcher *after* the read loops
have finished — so nothing new can be dispatched — and *before* the streams go away, so
a handler still writing a response is cancelled rather than left to discover a disposed
stdin. Disposal waits five seconds for cancelled handlers to unwind; a handler that
ignores its token does not get to hold shutdown open forever.

## Consequences

An approval gate is now survivable. A parked tool call blocks neither the turn's
remaining events nor a second tool call nor its own cancellation, and stopping a
transport with a request still parked completes instead of hanging.

That claim is pinned at the transport level, not just at the dispatcher's unit
boundary. `CodexAppServerTransportDispatchTests` and `CopilotAcpTransportDispatchTests`
drive each real transport over a fake CLI, park a request, and assert that a later
notification and a later request are both observed while the first handler is still
parked. Both were verified red against the previous inline dispatch and green after —
reverting either dispatch site to `await HandleServerRequestAsync(...)` fails the
matching test with a timeout. The duplication between the two test classes is
deliberate: the transports share the hazard, so pinning it in both is what stops them
drifting apart on the property that matters.

The two transports still each own their read loop, their id correlation, and their
write lock. Only the dispatch discipline is shared. That is the smallest thing both
needed, and it keeps `LmCore` free of any knowledge of either CLI's protocol.

Refusal is visible to the peer as an error, not as silence, so a CLI that oversubscribes
gets a definite answer for the call it made rather than a request that never resolves.
Whether a given CLI retries a `-32000` is its business; from our side the correlation is
closed either way.

A bound on concurrency is not a bound on duration. What limits how long a single parked
handler can hold its slot is the approval layer, not the transport:
`ToolApprovalOptions.MaxApprovalWait` (five minutes by default, validated finite and
positive) always applies. `ToolInvocationRequest.OperationDeadline` can tighten that
further and wins when it is sooner, but no production caller sets it today — neither
`MultiTurnAgentLoop` nor `ToolCallExecutor` populates it, and there is no turn-deadline
concept for them to populate it from. It is an opt-in hook for a host that has its own
deadline, and it is worth stating plainly rather than leaving a reader to infer that
something enforces it.
