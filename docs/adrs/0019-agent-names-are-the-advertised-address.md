# ADR 0019: Agent names are the advertised address; ordinals stay the stored key

* Status: Accepted
* Date: 2026-09-09
* Related issues, PRs, or commits: spec
  `docs/superpowers/specs/2026-09-09-agent-naming-and-task-identity-design.md`; plan
  `docs/superpowers/plans/2026-09-09-agent-naming-and-task-identity.md`; builds on
  [0009 — hierarchy-wide agent collaboration](0009-hierarchy-wide-agent-collaboration.md); leaves
  [0003 — per-conversation usage collector](0003-per-conversation-usage-collector.md) unchanged

## Context

Every agent in a collaboration has two handles. The ordinal `agent-N` is minted per root
conversation, forms the sub-agent's transcript thread id, keys the directory row, the restart
allocator, the identity-binding schema and the task board's ownership column, and survives a
restart. The name — `reviewer`, `finance-controller` — is what the spawning agent asked for, what the
spawn receipt reports, and what a model naturally uses for a peer.

The system resolved names but never spoke them. Every message a model read at the moment it was
learning how to address a peer taught the ordinal:

* the identity of an agent was never in its own prompt, so an agent did not know what it was called;
* a refused `SendMessage` said `No agent matches 'X'. Call GetAgents for current agent_ids.`;
* an unwaitable `WaitForAgents` target listed nothing at all;
* `CheckAgent` and `WaitAgent` accepted only an id while `SendMessage` accepted a name, so the same
  handle worked in one tool and was "unknown" in the next;
* the board rendered `[@agent-3]` for a row the lead had assigned to `reviewer`.

Production across 1279 conversations shows the cost. Models addressed peers as `lead`, `Revobot`,
`parent`, `manager` and invented `agt_*` ids — role words and product names, never a mistyped
ordinal. `SendMessage → not_found` touched 54 conversations; `WaitForAgents → unknown_agent` 90; the
board refused 19 with `assignee_unknown`, every one a human role name.

Two collision policies coexisted and disagreed. The collaboration directory latched a name asked for
twice as permanently ambiguous, so it resolved to nothing even after one holder left: the board could
only tell the model to "pass the agent id instead" once `finance-controller` named two agents. The
legacy sub-agent map did the opposite and silently reassigned the name to the newcomer, so a caller
mid-conversation with the first agent began addressing the second. Which policy applied depended only
on whether collaboration was switched on.

Honest sizing, so a later reader can weigh this record: correlation errors (`unknown_correlation`,
`missing_correlation`, `correlation_closed`, ≈693 occurrences) dwarf `not_found` (56). Naming fixes
roughly 5 % of `SendMessage` errors by occurrence. It was done anyway because it reaches 54 distinct
conversations, it is the class that produces retry loops, and it is the only class where the model's
failure is reasonable — it asked for the agent by the only handle it had ever been given.

Two sites were changed without production exposure and this record says so: the `CheckAgent`/
`WaitAgent` unknown-target correction and the directory's `ambiguous_name` refusal never fire in the
corpus. Every apparent string match was an agent reading this repository's source through the `Read`
tool. Only `error_code` searches are clean evidence here.

## Decision

**The name is the advertised address. `agent-N` remains the canonical, stored key.** Every surface a
model reads leads with the name and follows with the id in parentheses; every surface a model writes
to accepts either. Nothing persisted changes shape.

* Rejected: making the name canonical. It would rewrite `SubAgentThreadIds`, the ordinal allocator and
  the identity-binding schema, and break every persisted conversation, for a benefit the prompt and
  the error messages deliver without it.

**A collided name is granted a suffix, never latched and never stolen.** A second agent asking for
`reviewer` is granted `reviewer-3` (its own ordinal; then `-3-1`, `-3-2` if that is also taken). The
granted name flows outward: the spawn receipt reports it, the directory row carries it, and the
child's own identity preamble uses it, so all three agree. Claim and bind are one atomic `TryAdd` on
both the directory and the legacy map, because a check-then-act would let concurrent spawns all read
a name as free. A name held by a retired or finished agent stays taken, so a sender learns its target
ended rather than being redirected.

* Rejected: *latest wins* (the legacy map's former policy) — silently misroutes messages.
* Rejected: *latch ambiguous forever* (the directory's former policy) — bricks a name for both agents.
* Retained: the tombstone latch. Two DEAD agents that answered to one name leave a sender no way to
  say which it meant and no live agent to suffix against. Persisted rows from before this record can
  carry such a pair; the directory still refuses to guess there.

**The root agent is named from host configuration, defaulting to `MainAgent`**, and every agent is
told who it is. The preamble — *You are `reviewer` (`agent-3`). Other agents address you as
`reviewer`. You report to `MainAgent`.* — is prepended to the system prompt at the one chokepoint
where the loop is constructed. The compaction runtime reads the same stored prompt, so the identity
survives the switch to an envelope view; handing it the caller's raw prompt would make an agent forget
its name on the turn a checkpoint activates. The `primary` alias is unchanged.

**Every unknown-target refusal names the agents the caller could have meant**, through one shared
helper so four sites cannot drift into four vocabularies. The roster differs by tool on purpose:
`SendMessage` offers every LIVE agent in the collaboration except the sender; `WaitForAgents`,
`CheckAgent` and `WaitAgent` offer only the caller's own children, because those are the only agents
they can act on. `CheckAgent` and `WaitAgent` resolve a name like `SendMessage` does.

**Per-agent usage is a view over the one root ledger.** `GetAgents` in detail mode reads each agent's
row from the existing conversation-wide fold; the row gains a per-model breakdown produced by the
same fold the conversation uses, and `UsageRecord.EffectiveModel` is now populated when the provider
reports a model different from the one requested.

* Rejected: giving each agent runtime its own ledger. Six things block it: the dedup key is
  attempt-global and a sub-agent's capture deliberately collides with its parent's relay on that
  key; the revision watermark is per-conversation and N ledgers have no defined complete-prefix
  guarantee; persistence has exactly one home in the root thread metadata; nothing on a record names
  an agent except by parsing the thread id; the execution row had no model dimension; and a sub-agent
  already has two ledgers on its call path kept consistent only by the shared attempt id.
* **This amends nothing in ADR 0003.** The collector stays per-conversation and root-owned. A later
  reader must not read this record as a supersession of it.

**The task board keeps `AgentId` as the ownership key and adds a display name beside it.** The
resolver hands back `DisplayName` alongside the canonical identity; the node persists it as an
additive field and `list-tasks` renders it, falling back to the identifier when no name was offered.
`add-task`, the one write path that stored the caller's text verbatim, now resolves like the other
three.

* The reason recorded at the host wiring — "the canonical identifier, not the display name: the board
  compares ownership ordinally, and an identifier is the only thing guaranteed unique" — is **upheld,
  not reversed**. The change looks like a reversal at a glance because a name now appears in the
  listing; nothing compares it.

Out of scope, deliberately: correlation errors (the larger class, a different problem); transcript
visibility refusals (`not_an_ancestor` — the target resolves, the reader is not permitted); making the
message fabric durable (ADR 0009 decided against it and nothing here revisits it).

## Consequences

* A model reads the same handle in its own prompt, in every spawn receipt, in every refusal and on the
  board, and can write that handle to every tool. The ordinal is still there for the case a name is
  genuinely not enough, but it is no longer what the system teaches.
* Spawn never fails on a name and never redirects one. The price is that a granted name can differ
  from the requested one; callers must read the receipt rather than assume. Every surface that could
  have echoed the request now echoes the grant.
* Two policies became one. The legacy and collaboration surfaces now agree on collisions and on what a
  name is, which removes the class of behaviour that depended on a feature flag.
* The persisted schema is unchanged for identity and additive for the board (`assigneeDisplayName`,
  absent on older snapshots). No migration.
* The tombstone latch is now the only path that can produce `ambiguous_name`, and it is reachable
  only from replayed pre-record rows. It is kept and tested rather than deleted, because deleting the
  refusal would turn a genuinely undecidable case into a guess.
* The refusal sentences list up to 20 agents and announce the cap; a collaboration above that reads a
  truncated but honest list.
* Follow-up tracked outside this record: the remaining 95 % of `SendMessage` errors are correlation
  errors and want their own analysis; the board's `assignee_ambiguous` refusal (2 conversations) is
  now reachable only via tombstones and should be re-measured after this ships.
