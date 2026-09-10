# Agent naming and task identity

Date: 2026-09-09
Status: Draft, awaiting review

## Summary

Agents in a collaboration have human-meaningful names today. Nothing addresses them by name reliably,
nothing tells an agent its own name, and every corrective message the system emits teaches the model
to use the ordinal `agent-N` instead.

This design makes the name the **advertised address** and leaves the ordinal as the **stored key**.
It also closes four identity holes in the task board and exposes per-agent token usage.

**One principle: names are what the system says; ordinals are what it stores.**

## Problem

### What already exists

Three of the five asks are already implemented, then undermined elsewhere.

- `AgentCollaborationDirectory.Resolve()` already tries **id → name → tombstone**
  (`src/LmMultiTurn/Collaboration/AgentCollaborationDirectory.cs:451-477`). Names work.
- `SubAgentManager.TryResolveAgentId()` accepts id-or-name on **both** the collaboration and legacy
  paths (`src/LmMultiTurn/SubAgents/SubAgentManager.cs:1723-1762`).
- `TaskManager.AssigneeResolver` is wired to that same directory
  (`samples/LmStreaming.Sample/Services/TodoBoardIdentityWiring.cs:37-45`), with ambiguity and
  liveness handling.
- `AgentExecutionRef` (#681) already defines the per-agent usage grouping rule
  (`src/LmCore/Models/AgentExecutionRef.cs:15-21,39-43,52-56`), and `FoldByExecution` already ships.

### What production shows

Mined read-only from `B:\published\LmStreaming.Sample\conversations` — 1279 conversations, 1.2 GB.
Counts are distinct conversations unless marked occ.

Models address by **role word**, never by a mistyped ordinal. Of 61 conversations carrying a real
`No agent matches '<x>'` result:

| target | convs |
|---|---|
| `lead` | 15 |
| `Revobot` (the product name) | 13 |
| invented `agt_*` ids | 10 |
| `thread-*` ids | 6 |
| `parent` | 3 |
| `main`, `manager`, `main-session`, `finance-coordinator`, `agent-4d311d92` | 1-2 each |

Other classes:

- `SendMessage → not_found` — **54 convs** (56 occ.)
- `WaitForAgents → unknown_agent` — **90 convs** (107 occ.), three competing identifier shapes
  guessed: human name, hex conversation suffix, ordinal, thread id. One agent waited on **its own
  directory suffix** and was told no such sub-agent exists
  (`subagent-194d982d43ba-7fac4c99`).
- Board `assignee_unknown` — **19 convs**. **Every** rejected probe is a human role name
  (`reviewer`, `logging-reviewer`, `exception-reviewer`, `Revobot` ×6, …). Several come from agents
  whose own conversation directory is named `…-agent-5`, `…-agent-1`, `…-agent-10`.
- Board `assignee_ambiguous` — **2 convs**. The collision this design prevents is already live:
  > `Error: 'finance-controller' names 2 agents (agent-1, agent-2); the board cannot tell which one owns the work. Pass the agent id instead.`
- Retry loops with ≥2 distinct rejected identifiers — **16 convs**.

Two facts that frame the whole design:

- `sample.subAgentName` is present in **1056 / 1279** metadata files (e.g. `pr532-rereview-verifier`).
  **The names already exist and are never the address.**
- `PerAgent` usage appears in **0 / 1279** metadata files. `PerModel` in 1265.
  `ParentExecutionId` is non-null in only **113** — parent→child roll-up is essentially never
  populated.

### Honest sizing

Correlation errors dominate `SendMessage` failures by occurrence:
`unknown_correlation` 397 + `missing_correlation` 184 + `correlation_closed` 112 + others ≈ **693**,
against `not_found` **56**. **Naming fixes roughly 5 % of SendMessage errors by occurrence.**

It is still worth doing: it touches 54 distinct conversations, it is the class that produces retry
loops, and it is the only class where the model's failure is *reasonable* — it asked for the agent by
the only handle it was ever given.

`GetAgentTranscript → not_an_ancestor` (113 convs, 144 occ.) is a **visibility** refusal, not a
naming one. It must be excluded from any identity metric or it will flatter the result.

### Caveat on absence claims

`Read` tool results put this repository's own source into the transcripts, so any search for an error
*string* is contaminated by agents reading the code. Only `error_code` searches are clean. Every
"never fired" claim below rests on an `error_code` search or on inspecting the matched lines.

## Decisions taken

| # | Decision | Rationale |
|---|---|---|
| D-a | Root agent name comes from **chat-mode / host config**, defaulting to **`MainAgent`**, and is part of the system prompt | No UI work; every conversation of a mode shares a stable, speakable name |
| D-b | Name collisions **auto-suffix with the ordinal** (`reviewer` → `reviewer-3`) | Spawn never fails; every live name addresses exactly one agent |
| D-c | **`agent-N` stays canonical.** The name is the advertised address | Thread ids, the restart allocator, persisted directory rows and board ownership all key on the ordinal. Changing it breaks every persisted conversation |
| D-d | Per-agent usage is a **view over the existing root ledger**, not relocated ownership | ADR 0003 stays intact; avoids six concrete blockers listed below |

### Why not relocate usage ownership (D-d evidence)

Six things block giving each agent runtime its own ledger:

1. **The dedup key is attempt-global.** `ProviderAttemptId = $"{ownerExecutionId}:{attemptKey}"`
   (`src/LmMultiTurn/UsageAccounting/UsageRecordMapper.cs:51`). A sub-agent's own capture and the
   parent's relay of the same provider call deliberately collide on one key
   (`src/LmMultiTurn/SubAgents/SubAgentManager.cs:5172-5175`).
2. **The revision watermark is per-conversation.** `FoldedRevision` is the proven gap-free prefix
   (`src/LmMultiTurn/UsageAccounting/UsageLedger.cs:77-88`). N ledgers means N revision spaces with
   no defined complete-prefix guarantee across them.
3. **Persistence has exactly one home** — the root `ThreadMetadata` bag
   (`src/LmMultiTurn/Compaction/ConversationContextReport.cs:191-197`).
4. Nothing on `UsageRecord` names an agent; the id is only recoverable by parsing the thread id.
5. `ExecutionUsageRow` has no model dimension (`src/LmCore/Models/ConversationUsageAggregate.cs:77-129`).
6. A sub-agent already has **two** ledgers on its call path — its own and its parent's — kept
   consistent only by the shared `ProviderAttemptId` (`src/LmMultiTurn/MultiTurnAgentLoop.cs:434`).

## Non-goals

- Correlation errors (`unknown_correlation`, `missing_correlation`, `correlation_closed`). They are
  the larger class but a different problem.
- Transcript visibility (`not_an_ancestor`). The target resolves fine; the reader is not permitted.
- Making the message fabric durable. ADR 0009 decided against it; nothing here revisits that.
- Changing `agent-N` to a name-based canonical id (rejected as D-c).
- Adding a provider field to usage. It is net-new and not asked for.

## Design

### 1. Identity preamble — an agent knows who it is

**Defects closed: D5, D6.**

Today a child loop receives `systemPrompt: template.SystemPrompt` verbatim
(`src/LmMultiTurn/SubAgents/SubAgentManager.cs:3501`). `MultiTurnAgentLoop` holds `Collaboration`
but injects nothing. The only path to self-knowledge is the `your_agent_id` field of a `GetAgents`
result (`src/LmMultiTurn/SubAgents/SubAgentToolProvider.cs:1728`). An agent that never calls
`GetAgents` cannot tell a peer how to reach it, and cannot recognise its own name in an inbound
message.

**Change.** `MultiTurnAgentLoop` prepends an identity block to the system prompt whenever
`Collaboration` is non-null. One implementation serves root and child.

```
You are `reviewer` (agent-3). You report to `MainAgent`.
Other agents address you by the name `reviewer`.
```

**Placement.** In `MultiTurnAgentLoop`, not the host's `SubAgentPrompt` seam
(`samples/LmStreaming.Sample/Program.cs:3872-3890`). That seam folds a *static* fragment into
template prompts at options-build time; identity is per-agent. Identity is a library concern.

**Root name.** `AgentCollaborationSetup.CreateRoot` keeps its `name` parameter but the default
becomes `MainAgent` instead of `root` (`src/LmMultiTurn/Collaboration/AgentCollaborationSetup.cs:84`).
`ChatMode` gains an optional `RootAgentName`; the host passes it instead of the hard-coded
`"conversation"` (`samples/LmStreaming.Sample/Program.cs:3478`). Blank or absent ⇒ `MainAgent`.

The config property keeps the word *root* because it names **which** agent it configures — the root
of the hierarchy — while `MainAgent` is the default **value** the model actually reads. The existing
code symbol `AgentExecutionRef.RootAgentId` (`src/LmCore/Models/AgentExecutionRef.cs:24`) is an
attribution sentinel, not a display name, and is left untouched.

The `primary` alias is kept as a second name for the root — it is already bound
(`AgentCollaborationDirectory.cs:343-346`) and already advertised in tool descriptions.

### 2. Unique names by construction

**Defects closed: D9.**

Two contradictory collision policies exist in one process, chosen by whether collaboration is on:

- **Legacy** (`SubAgentManager.cs:936-956`): **latest wins.** A second agent claiming a live name
  silently steals it, with only a `LogWarning` reading
  *"SendMessage by this name will now address the new agent."*
- **Collaboration** (`AgentCollaborationDirectory.BindName`, `:643-658`): **latch ambiguous
  forever.** The name resolves to nothing from then on, even after one of the two agents leaves.

Neither is safe once names are the advertised address: one silently misroutes, the other permanently
bricks a name.

**Change.** Both are replaced by auto-suffixing. On a name already bound to a live agent, append the
new agent's ordinal: `reviewer` → `reviewer-3`.

This reuses the existing convention. `DeriveReadableName(templateName, ordinal)` already produces
`{last-segment-of-template}-{ordinal}` (`SubAgentManager.cs:1363-1372`), e.g. `general-purpose-3`.
So:

- auto-derived names are already collision-free — the ordinal makes them unique by construction;
- **only model-supplied names ever collide**;
- the suffixed name has the same shape the model already reads back;
- no new numbering scheme is introduced.

The spawn receipt returns the name **actually granted**, so the caller learns the suffix
(`SerializeSpawnReceipt`, `SubAgentManager.cs:742`). The `Agent` tool's `name` description changes
from "unique handle" to state that a taken name will be suffixed.

`IsAmbiguous` latching remains for **tombstones** (`_invalidatedByName`) — two dead agents that
answered to one name still leave a sender no way to say which it meant. It becomes unreachable for
live agents.

### 3. Speak names in every message the model reads

**Defects closed: D3 (retargeted), D11.**

The evidence corrected an earlier reading here. `DescribeUnknownAgent`
(`SubAgentToolProvider.cs:2438-2454`) — which lists ids only — **never fired in the corpus**; both
matches were agents reading this repository's source, with line numbers visible in the match.
Directory-level `ambiguous_name` never fired either. Fixing them alone would change nothing
observable.

The messages that **do** fire:

| Message | Fired | Location |
|---|---|---|
| `No agent matches '{target}'. Call GetAgents for current agent_ids.` | 61 convs | `SubAgentToolProvider.cs:1469` |
| `You have no sub-agent matching: {…}. WaitForAgents only covers agents you spawned yourself.` | 90 convs | `SubAgentToolProvider.cs:1858` |
| `'{probe}' does not name an agent in this conversation. … check the id with the sub-agent listing` | 19 convs | `src/Misc/Utils/TaskManager.cs:1295` |
| `'{probe}' names {n} agents ({ids}); … Pass the agent id instead.` | 2 convs | `TaskManager.cs:1287` |

**Change.** All four name the live agents as `name (agent-N)` pairs rather than bare ids, and stop
instructing the model to "pass the agent id". `DescribeUnknownAgent` and the directory codes get the
same treatment for consistency, flagged in the commit as having no current production exposure.

Observability rows gain names too: `BuildOutboundObligations` emits `to_agent_id` and no name
(`SubAgentToolProvider.cs:1579-1608`); it gains `to_agent_name`.

### 4. One addressing vocabulary

**Defects closed: D4.**

`TryResolveAgentId` accepts id-or-name on both paths, but the schemas disagree:

| Tool | Param | Says |
|---|---|---|
| `SendMessage` (collab) | `target` | "agent_id … or by name" ✓ |
| `SendMessage` (legacy) | `target` | "id … or the name you assigned it" ✓ |
| `CheckAgents` | `agent_ids` | "ids, exact unique names, or aliases" ✓ |
| `CheckAgent` | `agent_id` | **"The id of the sub-agent to check"** ✗ |
| `WaitAgent` | `agent_id` | **"The id of the sub-agent to wait for"** + *"Use an `agent_id` returned by `Agent`"* ✗ |

**Change.** Descriptions only. Every addressing parameter states id-or-name identically, and every
one says a name is preferred. Wire parameter names (`target`, `agent_id`, `agent_ids`) are left
alone — renaming them breaks replayed tool calls from persisted conversations for no behavioural
gain.

### 5. Task board speaks names, keys on ids

**Defects closed: D1, D2, D14, D15.**

`ResolveAssignee` (`TaskManager.cs:1271-1317`) is the choke point. Its doc comment claims it is
*"shared by every path that writes `PrivateTaskItem.Assignee`"* (`:1257-1259`). **That sentence is
false.** Four write paths exist and one skips it:

| Tool | Resolves? | Location |
|---|---|---|
| `assign-task` | yes | `:876` |
| `claim-task` | yes — except the refresh branch, which compares raw text **before** resolving | `:1214`, raw compare at `:792-804` |
| `update-task` (status `in progress`) | yes | `:666-675` |
| **`add-task`** | **no — raw text written verbatim** | `:352` (root), `:378` (subtask, and sub-items inherit it) |

Consequence: `add-task {assignee:"reviewer"}` stores the literal `reviewer` while `claim-task
{agent:"reviewer"}` resolves to and compares `agent-3`. Comparisons are ordinal (`:794`, `:891`,
`:1222`, `:1325`), so **the assigned agent can never claim its own task.**

**Changes.**

1. `AddTaskCore` routes through `ResolveAssignee`. Inherited sub-item assignees are already
   canonical, so only the explicit argument needs resolving.
2. `claim-task`'s refresh branch resolves before comparing.
3. The false doc comment is corrected to enumerate the paths.
4. `AssigneeResolution` gains a `DisplayName`. Ownership still compares `AgentId` — the reason given
   at `TodoBoardIdentityWiring.cs:76-83` (an identifier is the only thing guaranteed unique within
   the conversation) is correct and is **not** being reversed. `list-tasks` renders the display name.
5. `TaskAssignmentProbe` (`src/LmMultiTurn/SubAgents/SubAgentOptions.cs:186`) is invoked with the
   spawn **name** and compared case-insensitively against `task.Assignee`
   (`samples/LmStreaming.Sample/Program.cs:3842-3860`) — which holds `agent-N` once the resolver is
   attached. **It can never match.** It is re-pointed at the resolver so name and id both work.
   Impact is bounded: it only gates a `LogWarning` about a sub-agent lacking task tools
   (`SubAgentManager.cs:3890-3898`).

`AssigneeResolver` stays null by default (`TaskManager.cs:237`) — `Misc` is a published leaf that
cannot reference `LmMultiTurn`, so the delegate seam is correct. The null-resolver behaviour (raw
text is the ownership key) is documented rather than changed.

**Open item for review:** `TaskManager` is also constructed at
`samples/LmStreaming.Sample/Services/ToolCatalog.cs:70` and `Services/ModeSubAgentRequiredTools.cs:47`.
Both appear to be throwaway instances used only to enumerate tool schemas, but their lifetimes were
not traced. If either reaches a live conversation, that board has no resolver. **This must be
confirmed during implementation.**

### 6. `GetAgents` gains a detail mode, and usage becomes visible

**Defects closed: D7, D8, D12, D13.**

`HandleGetAgentsToolAsync` (`SubAgentToolProvider.cs:1702-1783`) always emits 16 fields per agent —
roughly 340 bytes per row, capped at `MaxTotalAgents` (default 32), so about 11 KB on every call.
There is no usage field.

**Change.** A `detail` parameter, default `normal`.

- **`normal`** — `name`, `description`, `parent_name`, `status`, `is_you`. Enough to decide whom to
  contact.
- **`detailed`** — adds `agent_id`, `structural_depth`, `delegation_depth`, `agent_type`,
  `transcript_readable`, `aliases`, and `usage`.

`usage` is a **view** over the existing root ledger, computed through
`AgentExecutionRef.ExecutionIdOf` and `FoldByExecution`. No second ledger; ADR 0003 is untouched.

Two prerequisites, both real defects, both required for the usage view to mean what it says:

- **`UsageRecord.EffectiveModel` is never written.** Grepping all of `src/` finds no assignment; both
  mapper call sites write only `RequestedModel` (`UsageRecordMapper.cs:60`). `EffectiveModelId`
  therefore always falls back to the requested model. ADR 0003's claim that the model *actually used*
  is captured is **not delivered today**. The mapper must stamp it from the resolved model.
- **`ExecutionUsageRow` has no model dimension** (`ConversationUsageAggregate.cs:77-129`) — it sums
  across models per execution. It gains a per-model breakdown. This is the row to extend, not
  `ModelUsageRow`.

`ModelUsageRow` and the conversation-wide `PerModel` fold are unchanged.

## Defect closure map

| # | Defect | Closed by |
|---|---|---|
| D1 | `add-task` bypasses the assignee resolver | §5 |
| D2 | Board stores and displays `agent-3`, never `reviewer` | §5 |
| D3 | Corrective messages teach `agent-N` | §3 |
| D4 | `CheckAgent`/`WaitAgent` schemas say "the id" | §4 |
| D5 | Root cannot be named | §1 |
| D6 | An agent is never told its own identity | §1 |
| D7 | `GetAgents` has one, expensive verbosity | §6 |
| D8 | Usage carries no agent identity | §6 |
| D9 | Two opposite collision policies | §2 |
| D10 | (observation — auto-derived names are already unique) | informs §2 |
| D11 | Obligation rows print ids, no names | §3 |
| D12 | `UsageRecord.EffectiveModel` never written | §6 |
| D13 | `ExecutionUsageRow` has no model dimension | §6 |
| D14 | `TaskAssignmentProbe` can never match | §5 |
| D15 | `claim-task` refresh branch compares raw text | §5 |

## Testing

TDD throughout: every behavioural change lands as a red test first.

**Existing suites that must stay green** (they encode the semantics being changed, so several will
need deliberate updating rather than accidental breaking):

- `tests/Misc.Tests/Utils/TaskManagerAssigneeResolutionTests.cs` — the identity contract
- `tests/Misc.Tests/Utils/TaskManagerTests.cs`, `…FromSnapshotTests.cs`, `…ToolSurfaceTests.cs`
- `tests/LmStreaming.Sample.Tests/Services/TodoBoardIdentityWiringTests.cs` — restart, tombstone,
  cross-root scoping
- `tests/LmMultiTurn.Tests/Collaboration/AgentCollaborationDirectoryTests.cs`,
  `DirectoryInvalidatedRoutingTests.cs`, `CollaborationIdentityWiringTests.cs`
- `tests/LmMultiTurn.Tests/UsageAccounting/*`, `tests/LmCore.Tests/Models/ExecutionUsageFoldTests.cs`
- `tests/LmMultiTurn.Tests/SubAgentRequiredToolsTests.cs:816-828` — the probe

**New coverage, one per claim:**

| Claim | Test |
|---|---|
| A spawned agent's system prompt names it and its parent | `MultiTurnAgentLoop` collaboration test asserting the prompt text |
| Root defaults to `MainAgent` when config supplies nothing | `AgentCollaborationSetup` test |
| Second `reviewer` becomes `reviewer-{ordinal}` and the receipt says so | `SubAgentManager` spawn test |
| A suffixed name resolves to exactly one agent; the original still resolves to the first | directory test |
| `add-task` with an unknown assignee is refused | `TaskManager` test — **red before the fix** |
| Assign by name then claim by name succeeds end to end | `TaskManager` + wiring test — the D1 regression |
| `list-tasks` shows the display name while ownership compares the id | wiring test |
| `GetAgents` `normal` omits `agent_id`; `detailed` includes it and a usage block | tool-provider test |
| `EffectiveModel` is populated when the resolved model differs from the requested one | `UsageRecordMapperTests` — **red before the fix** |
| A per-agent usage row carries a per-model breakdown | `ExecutionUsageFoldTests` |

**Mutation checks** for the two tests whose subject is an absence:

- The `add-task` refusal test must fail if `ResolveAssignee` is removed from `AddTaskCore` — place
  the mutation at the call site, not the declaration.
- The `EffectiveModel` test must fail if the stamp is reverted to `RequestedModel`.

**Repository gates:**

- `dotnet csharpier format .` — CSharpier is the formatting authority; the pre-commit hook and CI
  both check it.
- New test method families must be classified in `scripts/test-priorities.ndjson`, or the manifest
  drifts and reddens main on merge.
- `./scripts/run-priority-tests.ps1 -Escalate P0,P1,P2 -Kind dotnet-project` over the changed paths,
  after building. `scripts/ci-test.ps1` stays authoritative.

## Sequencing

Each phase is independently shippable and independently reviewable.

1. **Identity preamble + root name** (§1). Highest value per line: it is the only change that gives
   the model the handle it has been guessing at.
2. **Unique names + error messages that speak them** (§2, §3). Makes name addressing trustworthy.
3. **Task board** (§5). Closes the create-path hole, which is a correctness bug independent of
   naming.
4. **`GetAgents` detail mode + usage prerequisites** (§6). Largest surface; benefits from the naming
   work already being in.

§4 (descriptions) rides along with whichever phase touches the file first.

## Risks and residual uncertainty

- **The win is ~5 % of SendMessage errors by occurrence.** Stated in the problem section and not
  hidden. If the goal were to reduce total tool-call failures, correlation handling is the larger
  target. This work is justified on retry loops and on the model's failure being reasonable, not on
  volume.
- **Prompt-length cost.** The identity preamble is added to every agent's system prompt on every
  turn. It should be a single short line, and its token cost should be measured, not assumed.
- **Changed error text may shift model behaviour in ways the corpus cannot predict.** The corpus
  records behaviour under the old text only.
- **`GetAgents` `normal` omits `agent_id`.** A model that has learnt to copy the id will not find one
  until it asks for `detailed`. This is intended, but it is a behaviour change for existing
  conversations replaying old patterns.
- **`ToolCatalog` / `ModeSubAgentRequiredTools` board instances are untraced** (§5 open item). If one
  reaches a live conversation, that board has no resolver and this design's guarantees do not hold
  there.
- **Suffixing changes what a name means mid-conversation.** An agent told it is `reviewer` keeps that
  name; a *later* `reviewer` becomes `reviewer-4`. A model that assumed it could re-derive a peer's
  name from its role will be wrong. The spawn receipt is the only authority.

## ADR — a deliverable, not a note

This work changes a documented invariant. `AgentDirectoryEntry.Name` is currently documented as
*"May collide with another agent's, so it is not an addressing key"*
(`src/LmMultiTurn/Collaboration/AgentDirectoryEntry.cs:46`), and `AgentId` as
*"the only safe addressing key"* (`:40-41`). After §2 the name **is** an addressing key. A future
reader who finds those comments deleted with no record of why would have to re-derive this whole
argument.

**An ADR is therefore part of the work, written in Phase 1** (§ Sequencing), titled
*Agent names are the advertised address; ordinals are the stored key*.

It records, with the reasoning and the rejected alternatives:

| Decision | What the ADR must capture |
|---|---|
| D-c | The name is the **advertised address**; `agent-N` stays the **stored key**. Rejected: making the name canonical — it would rewrite `SubAgentThreadIds`, the ordinal allocator, the identity-binding schema, and break every persisted conversation. |
| D-b | Collisions **auto-suffix with the ordinal**. Rejected: *latest wins* (silently misroutes — `SubAgentManager.cs:936-956`) and *latch ambiguous forever* (permanently bricks a name — `AgentCollaborationDirectory.cs:643-658`). Both exist today and are replaced. Records that ambiguity latching survives for tombstones and why. |
| D-a | The root agent is named from host config, defaulting to `MainAgent`, and every agent is told its own identity in its system prompt. |
| D-d | Per-agent usage is a **view** over the one root ledger. Rejected: relocating ownership to each agent runtime — with the six blockers enumerated in this spec. This one **amends nothing** in ADR 0003; the ADR must say so explicitly, so a later reader does not read it as a supersession. |
| — | The board keeps `AgentId` as the ownership key and adds a display name beside it. The reason at `TodoBoardIdentityWiring.cs:76-83` is **upheld, not reversed** — worth stating, because §5 looks like a reversal at a glance. |

It must also carry the honest sizing (~5 % of `SendMessage` errors by occurrence) and the reason the
work was done anyway. An ADR that records only the upside is the kind a later reader cannot trust.

**Numbering.** Choose the number immediately before committing, by checking the highest existing
`docs/adrs/` entry on `main` — not now. Parallel ADR numbering collides **without producing a merge
conflict**, so a number reserved at design time is a number two branches can both take. `0018` is the
highest present in this worktree; that is a starting point to re-verify, not an answer.

**Related ADRs to cross-reference:** 0003 (per-conversation usage collector — untouched),
0009 (hierarchy-wide agent collaboration — the message fabric stays non-durable).
