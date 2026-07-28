# ADR 0007: Observe turns and context at the seam where the fact settles

* Status: Accepted
* Date: 2026-07-27
* Related issues, PRs, or commits: [#227](https://github.com/achieveai/LmDotnetTools/issues/227)

## Context

Two of the lifecycle events in #227 describe things that are in motion for most of
their lifetime, and the obvious place to report each of them is the place where the
report would be wrong.

**A turn.** The four loops disagree about what a turn is. `MultiTurnAgentLoop` mints a
`GenerationId` per model round trip, so its turns are visible from the outside. The
three CLI loops — `ClaudeAgentLoop`, `CodexAgentLoop`, `CopilotAgentLoop` — run a whole
provider-side agentic loop behind a single `GenerationId`, and what they surface is a
stream of deltas: partial text, tool-call fragments, arguments that arrive a few
characters at a time. A turn's message count, tool-call count, and outcome are all
unsettled until the loop stops producing. Worse, a turn can end four ways that do not
pass through the loop's own completion path at all — provider error, caller
cancellation, host interruption, and hitting the turn ceiling.

So there is a real temptation to emit progress: report the turn as it develops and
correct it later. That produces an event stream where a subscriber cannot tell a
finished turn from an in-flight one without waiting for a quiet period that has no
defined length, and where the same turn appears several times with different counts.
The alternative temptation — wiring final-state reporting separately into each loop —
gives four implementations of "the turn is over now," and the four failure paths above
mean each one has at least five places to get it right.

**Context.** Discovered context files (`CLAUDE.md`, `AGENTS.md`) reach a model by two
unrelated routes. At boot the rendered block is concatenated into the system-prompt
string. Mid-session it is wrapped in a `NotifyMessage` and injected. The natural place
to report it is the discovery: that is where the path, the file, and the truncation
decision are all in hand as typed values, and where carrying provenance alongside the
block costs nothing.

But discovery is not delivery. A mid-session discovery can be queued behind a run in
progress and never dispatched; a run can be cancelled between discovery and dispatch;
the same file can be rediscovered with new content; and an inventory of what the
gateway found is not the same as what any request carried. Reporting at discovery
means the event says the model received something it may not have. And the boot route
makes carrying provenance awkward in exactly the way that invites drift: the block
becomes a bare string with nowhere to hang metadata, so the metadata has to be
remembered somewhere else and hoped to still be true at dispatch.

## Decision

**Report each fact from the one seam where it has stopped changing.**

For turns, that seam is `RunTurnLifecycleFinalizer`. Every loop routes through it, and
it publishes `turn_completed` **once per accepted `GenerationId`, at final state
only** — never a partial, never a correction. The finalizer owns the in-flight table,
so whichever path ends a turn first owns ending it, and the run's terminal sweep closes
any turn the loop did not. All five ending paths therefore produce exactly one event
with a stable outcome, and a subscriber may treat the arrival of `turn_completed` as
the turn being over. Because the counts are taken at that moment, a turn is described
the same way whichever loop produced it, despite the loops disagreeing about what a
turn is.

For context, that seam is the provider request itself. `context_loaded` is published
from the **immutable request snapshot, immediately before dispatch**, and its
provenance is *recovered by reading the request* rather than carried alongside it.
`RenderedContextBlock` owns the `<context-discovery>` tag grammar in both directions —
writing it when a file is rendered, reading it back when a request goes out — so boot
and mid-session rendering stay byte-identical by construction, and the phase is read
off the message role rather than remembered: a block in a system message is a boot
seed, anything else is a mid-session delivery.

Each source is announced **once per agent instance**, keyed on
`{discovery_kind}:{normalized_path}`. A boot seed rides in every subsequent request, so
first delivery is the only interesting moment; re-announcing it each turn would claim
new context arrived once per model round trip forever. When one request first carries
several sources, `rendered_hash` covers those blocks concatenated in request order with
no separator, because a separator would be bytes the model was never sent.

## Consequences

A subscriber gets a stream it can trust without a settling heuristic. One
`turn_completed` per turn, at final state, counted the same way across all four loops.
One `context_loaded` per source, describing bytes a model actually received — so
context that is queued, cancelled, superseded, rediscovered, or merely inventoried
produces no event at all, which is the correct answer rather than a missing one.

Reading provenance back out of the request costs a scan of the outgoing request text.
It is gated on `RunTurnLifecycleFinalizer.PublishesEvents` rather than `IsEnabled`, so
a host that only persists lifecycle rows never pays for a scan whose result nobody
receives, and a host with lifecycle off pays nothing.

Recovery-by-scanning also bounds what an event can say. The tag carries a path and a
truncation flag and nothing else, so a scanned block is attributed to the
`context_file` discovery kind — the only kind that renders this tag. Adding a second
kind means extending the grammar, not adding a parallel reporting path, which is the
constraint that keeps the two directions from diverging.

The "once per source" ledger is in-memory and per agent instance. A conversation
reloaded in a new process reports its context again, which is honest — that process
did hand the model the context — and subscribers that care about the difference dedup
on `rendered_hash`.

Finally, giving up progress reporting means a subscriber cannot watch a long CLI turn
develop. That is deliberate: a partial turn is not a fact about the conversation, and
`message_completed` already covers "something arrived" for consumers that need
liveness.
