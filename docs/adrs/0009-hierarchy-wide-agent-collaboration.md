# ADR 0009: Scope agent collaboration to a root-owned directory, and leave delivery with the owner

* Status: Accepted
* Date: 2026-08-01
* Related issues, PRs, or commits: [#244](https://github.com/achieveai/LmDotnetTools/issues/244)

## Context

An agent hierarchy in this repository is a set of unrelated islands that happen to share a
process. `SubAgentManager` knows the children it spawned and nothing else: no parent, no
siblings, no grandchildren, no workflow controller running beside it. A `WorkflowAgent`
controller is a separately constructed nested root with its own manager, so from the
outside it is not even visibly part of the tree that launched it. And ordinary children
receive no Agent-family tools at all, which is what has been holding recursion to one
level — not a depth check, but the absence of the tool.

#244 asks for the opposite of islands. Any agent under one root must be able to see the
whole hierarchy, address any other member by a stable identity, ask it a question and get
a correlated answer, read its transcript when policy allows, and delegate further work
downward within a configured budget. That is a routing problem, a correlation problem, a
capacity problem, and an authorization problem, and each of them has an obvious wrong
answer.

**The obvious wrong answer for routing** is to promote `SubAgentManager` into the
collaboration coordinator. It already holds the child state, so cross-manager delivery
looks like a matter of letting managers see each other. But that class is a lifecycle
owner: it holds the spawn gate, the defer queue, the concurrency permits, the restart
path, and the per-child locks. Making it root-global couples every nested manager,
workflow controller, transcript decision, message ledger, and capacity rule into the
largest and most restart-sensitive class in `LmMultiTurn`, and it pressures `SubAgentState`
to escape its owner so a remote caller can inspect it. The first cross-manager send would
be the last time the lifecycle invariants were local.

**The obvious wrong answer for correlation and recovery** is to make the message fabric
durable — tables, delivery workers, replay. The issue explicitly scopes collaboration to
one process and explicitly permits unsupported in-flight recovery to fail clearly. Durable
queues would buy replay nobody asked for, at the cost of consistency rules that must be
right before any of the actual behaviour can be proven.

**The obvious wrong answer for structure** is to build the five named services in the
issue — registry, router, ledger, transcript policy, wait broker — as five interface and
implementation pairs with a factory each. The issue names five *responsibilities*. Two of
them are stateful, one is a pure function, and one already exists inside the manager that
owns the children it waits on.

There is also a compatibility force. Hosts exist today. Their tool schemas, their one-level
nesting, their transcript endpoint bytes, and their persisted JSON must not move because a
feature they did not opt into was merged.

## Decision

**One root-owned collaboration bundle answers *where the target is*; the target's existing
owner remains solely responsible for *delivering to it*.**

The collaboration is scoped by the root thread/conversation ID. No second persisted
identifier is minted. A bundle is constructed once per live process hierarchy by the first
live root — an ordinary conversation root, or a resumed workflow root when the original
caller is gone — and every descendant, ordinary or workflow-owned, receives that same
reference and never constructs a competing one.

The bundle holds exactly two stateful sealed types and one pure evaluator.
`AgentCollaborationDirectory` owns identity: canonical IDs, name aliases with an explicit
ambiguity marker rather than silent reassignment, the precomputed ancestor path, structural
and delegation depth, status, the root capacity leases, and the bounded per-target FIFO of
message IDs. `AgentMessageLedger` owns meaning: trusted message IDs, Question and
DelegateTask open/close state, accepted/delivered/failed transitions, idempotency, and a
narrow content-free Question-admitted event. The transcript policy is a static method over
trusted directory data — an ancestor-path containment check against a root-configured mode
— because that is all it ever needs to be.

The directory is not the transport. An entry carries a narrow internal endpoint capability,
implemented by the loop or manager that owns the target, split into a write facet
(`DeliverAsync`) and a read facet (status and transcript projection) only because that split
prevents a reader from acquiring the ability to inject. Delivery therefore still happens
inside the target's lifecycle lock, through its existing continuation and restart rules,
using the existing `IMultiTurnAgent.TrySendAsync` admission that already records accepted
input before enqueue and rolls back when the channel is full. No router service, endpoint
factory, or global wait service is introduced, and no code outside a manager ever sees a
`SubAgentState`.

The two state owners are deliberately non-overlapping. A target inbox holds message *IDs*
in FIFO order and nothing else; the ledger is the sole source of every message's status.
Ledger retention is bounded — open entries are never evicted, closed ones are pruned by a
configurable age and count cap against an injected `TimeProvider`, so the retention window
is testable without sleeping.

Capacity is a lease. The root-wide total-agent lease is acquired **first and
non-blockingly**, before any per-manager gate, queue, or lock, on both ordinary and workflow
spawn paths — one acquisition order, no exceptions, so a queued agent cannot exceed the
collaboration cap and a lock-ordering inversion cannot exist. The lease follows the agent
instance: a restart reuses it, and it is released exactly once when the agent leaves
admitted state, *before* a potentially slow Stop/Dispose completes, so slow teardown cannot
freeze collaboration capacity.

An agent message is a first-class `IMessage` in `LmCore`, not a formatted string.
`AgentMessage` reuses the `NotifyMessage` envelope discipline — immutable computed text,
escaped attributes, closing-tag sanitization — so content cannot close its own envelope or
forge a reply instruction. Its converter discriminator and property inference must be
registered **before** the generic `text` inference branch, or persisted envelopes silently
rehydrate as `TextMessage`. Older readers that lack the discriminator degrade to generic
text rather than throwing, which is what makes reverting this work safe.

Two boundaries are drawn on purpose. `LmWorkflow` adapts its controllers and delegates into
contracts defined in `LmMultiTurn`; `LmMultiTurn` never references `LmWorkflow`. And a
workflow controller is a visible hierarchy node but a zero-cost delegation hop — a
controller started by a caller at delegation depth `d` is itself at `d`, and its ordinary
delegate is at `d + 1` — so orchestration structure appears in `GetAgents` without spending
delegation budget.

Finally, the whole feature is gated on the presence of `AgentCollaborationOptions`. Absent
options reproduce today's behaviour exactly: legacy tool schemas, one-level nesting,
per-manager limits only, byte-compatible transcript output, and no collaboration writes.

## Consequences

Lifecycle correctness stays where it was proven. Restart, continuation, cancellation, and
disposal are unchanged code paths reached through a new address, so the risk this feature
adds is concentrated in resolution and correlation rather than smeared across the machinery
that was already hard to get right.

The cost is composition plumbing. Every agent root, nested manager, and workflow controller
construction site must now thread a bundle reference and an immutable per-agent identity
context, and every lifecycle boundary must register, update, or remove a directory entry and
release a lease. That plumbing is wide and shallow, which is the trade being made: many
small touch points instead of one large coupled class.

Two state owners with disjoint responsibilities means neither can be consulted alone. A
caller asking "did my message arrive" reads the ledger; a caller asking "what is queued for
this target" reads the inbox. Keeping message status out of the inbox is what prevents the
two from drifting into disagreement, and it is worth the extra indirection.

Per-target inbox capacity is shared across senders in v1. There is no per-sender fairness
quota, so one sender can fill a target's inbox and every subsequent sender — including that
one — receives an explicit recoverable backpressure result before a message ID is minted.
This is a deliberate simplification: a bound plus an observable rejection is enough to stop
unbounded growth, and fairness quotas are not introduced until a real workload proves they
are needed.

Acceptance is not delivery, and the gap is now visible rather than silent. Once a message is
accepted it may still fail to be delivered — most sharply on the restart path, whose
existing five-second admission gate can throw after acceptance. That failure is recorded as
a terminal `DeliveryFailed` state in the ledger before the pump item completes; it is never
routed through a fire-and-forget fault observer, because an unobserved task fault is
indistinguishable from a message that vanished.

In-flight collaboration state is memory-only. A process failure with open Questions or
DelegateTasks reports a clear interrupted/lost result. It does not replay, misroute, or
duplicate — which is the boundary #244 explicitly permits, and stating it here is what keeps
a future reader from mistaking the in-memory ledger for an oversight.

Contact and read permission are separate axes, and that separation is load-bearing. Every
member of a collaboration can address every other member; only the transcript policy decides
who may *read* one. Because the same policy result must serve the tool, the REST projection,
and the UI, the raw agent-thread endpoints must be brought under it too — a caller who can
derive `subagent-{agentId}` from `GetAgents` must not be able to read past the policy with a
direct GET, or write past sender identity, correlation, inbox bounds, and envelope safety
with a direct POST.

Role and description become collaboration-visible directory metadata, which makes them a
disclosure surface. They are bounded (role 1–80, description 1–200 Unicode scalar values),
validated once at spawn and reused verbatim on restart, never used as a routing key, and
never written to logs — logs carry identifiers, types, outcomes, and content lengths only.

Because ADRs here are append-only, an implementation that diverges architecturally from this
record gets a superseding ADR rather than an edit to this one.
