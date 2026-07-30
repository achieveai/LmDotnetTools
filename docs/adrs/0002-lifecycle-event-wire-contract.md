# ADR 0002: Publish lifecycle events through a dependency-neutral versioned wire contract

* Status: Accepted
* Date: 2026-07-27
* Related issues, PRs, or commits: [#227](https://github.com/achieveai/LmDotnetTools/issues/227)

## Context

An application hosting a MultiTurn agent cannot currently reconstruct what that agent did.
There is no provider-neutral, correlated record of runs, turns, loaded context, tool
completions, or sandbox creation. Consumers are left with two bad options: scrape the
streaming message fragments — which are intermediate state, not final state, and differ
per provider — or reach into provider-specific internals and couple themselves to them.

Four constraints shaped the answer.

**The contract has more than one kind of consumer.** Trusted in-process subscribers live
inside the host (`LmStreaming.Sample`, `ConversationDaemon.Sample`,
`CodeReviewDaemon.Sample`). Authenticated service-to-service subscribers sit across a
network boundary and receive signed deliveries. A shape that only suits one of them would
force the other to re-model every event.

**`LmCore` must stay dependency-free.** `src/LmCore/AchieveAi.LmDotnetTools.LmCore.csproj`
has zero `ProjectReference` entries, and that is deliberate — it is the package everything
else builds on. Putting wire DTOs there would either pull serialization concerns into the
core or force every consumer of the core to take a dependency on an event vocabulary they
may not want.

**Events cross a version boundary.** A producer and a subscriber are upgraded
independently, so a subscriber will routinely meet an event type, a nested message kind,
or an enum value that did not exist when it was compiled. If an unknown value aborts
deserialization, one new event type in the producer silently breaks every older
subscriber.

**Ordering is per conversation, not global.** Two unrelated threads must not share a
sequence counter — a process-wide counter would leak cross-tenant activity volume, and it
would make a gap in one thread's stream indistinguishable from unrelated traffic in
another's.

Two further facts came out of surveying the codebase before designing this.
`System.Text.Json` source generation is used **nowhere** in the repository today — it is
100% reflection-based with hand-written converters under `src/LmCore/Utils/`. And retries
of a signed delivery must re-send a byte-identical body, or the signature computed over
the original bytes no longer validates.

## Decision

Lifecycle events are defined in a new, **dependency-neutral `LmLifecycle` package** that
references no other project in this repository. `LmCore` keeps the approval *runtime*
types and remains project-reference free; `LmLifecycle` owns the *wire* vocabulary; the
infrastructure layer adapts between them. Lifecycle DTOs deliberately do not implement
`IMessage` and never enter conversation-message persistence — they are a closed wire
vocabulary, not another message kind flowing through the agent loop.

**One source-generated serializer contract owns the encoding.** A single
`JsonSerializerContext` is the sole authority for property names, discriminators, enum
encoding, timestamp format, null handling, and the resulting UTF-8 bytes. Property names
are snake_case and pinned explicitly per property rather than derived from a naming
policy, so the wire format cannot drift as a side effect of renaming a C# member. This is
the first use of source generation in the repository — chosen over the established
reflection-plus-converters approach specifically because the output must be deterministic:
the same value must serialize to the same bytes on every call and on both `net8.0` and
`net9.0`, so that a retry can re-send the identical signed body.

**Discriminators are open strings, and unknown values are preserved rather than
rejected.** An unrecognized event type, nested message kind, tool kind, outcome, or enum
value deserializes into a form that retains the discriminator plus the raw JSON verbatim
and round-trips byte-identically. A subscriber can therefore forward, store, and re-emit
an event it does not understand. There is exactly one exception: an **unknown approval
decision fails closed** and is rejected, because "preserve and ignore" applied to an
authorization decision is indistinguishable from "allow".

**Source identity and delivery identity are separate.** Canonical ordering is per typed
source stream — `thread:{ThreadId}` for agent events, `sandbox:{SessionId}` for sandbox
events — carrying a monotonic `source_sequence` allocated atomically per stream, plus a
`producer_epoch` that changes on producer restart so a counter reset is detectable rather
than silently indistinguishable from a gap. Layered on top, each subscriber gets its own
`delivery_id` and `delivery_sequence`, assigned **after** filtering. Because filtering
happens before delivery numbering, a subscriber sees a contiguous delivery sequence and
can detect loss that is specific to it, without inferring anything about events it was
never entitled to see.

**Identity is assigned once, before fan-out, and reused across retries.** A retry is
therefore a re-send of an identical body, not a new event.

**`parent_run_id` is the nearest cause, not the ultimate origin.** A run that continues its
own thread points at the run before it — including on a sub-agent whose thread some other
agent opened. A spawn therefore appears in `parent_run_id` only on the child's *first* run;
from then on the cross-agent edge is `parent_thread_id` and `spawning_tool_call_id`, which
every event from that child carries for its whole life. This is what lets one field answer
"what caused this run?" uniformly for a resume, a delayed-result continuation, and a spawn,
instead of meaning something different in each case. A subscriber rebuilding the agent tree
groups by `sub_agent_id` and follows `parent_thread_id`; walking `parent_run_id` alone
climbs the child's own history and never leaves the child. Lineage is captured when the
sub-agent is spawned and travels with it, so a restart that rebuilds the child long after
the spawning run ended still reports the run that asked for it rather than whatever the
parent happens to be doing at rebuild time.

**Registration negotiates protocol majors.** Delivery and approval begin only when the
producer and subscriber share a compatible major version; an incompatible peer is refused
at registration rather than failing per-event at runtime.

Within a major version, fields may be **added** and are optional. Tightening a field's
requiredness or nullability requires a new major. Every V1 field's JSON type,
requiredness, nullability, and absent-versus-null-versus-empty semantics are published in
[the field matrix](../lifecycle-event-field-matrix.md) that ships with the package.

## Consequences

Consumers gain a stable, provider-neutral record that is identical whether the underlying
agent is the raw LLM loop, Claude, Codex, or Copilot, and they can correlate it across
threads, runs, turns, tools, sub-agents, and sandbox sessions without touching provider
internals. A subscriber compiled today keeps working against a producer that adds event
types tomorrow.

The cost is a new package to version and a new serialization technique in the repository.
Source generation is a deliberate exception to the existing reflection-based convention
and is confined to this package; the determinism and dual-target byte-symmetry
requirements are enforced by golden-fixture tests asserting exact bytes on both target
frameworks, so a drift in the encoder fails the build rather than corrupting signatures in
production. Preserving unknown payloads means an event can carry data the local type
system cannot describe, which is the intended trade: forward compatibility is worth more
than exhaustive local typing for an observational stream.

This contract is explicitly **not** an authoritative audit log. Delivery is best-effort
and bounded, with no durable outbox, replay, or backfill. Drops are deliberate and
observable as a sequence gap. Anything requiring guaranteed delivery must be built on top
of this rather than assuming it — and the gap-detectable design is what makes that
possible.

Binding events to an owner is handled by a host-resolved key that is never serialized and
never inferred from a thread, run, session, workspace, or tool identifier; see
[ADR 0005](0005-service-to-service-lifecycle-delivery.md).
