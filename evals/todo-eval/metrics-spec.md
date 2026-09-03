# todo-eval metrics specification

Deterministic scoring for the `todo-eval` Testing Mode (#617/#618). No LLM judging: every
metric is computed from the conversation store on disk plus one final board snapshot.
`score.ps1` in this directory is the reference implementation and manual-validation tool;
the #619 harness reimplements this same spec in C# with its own tests. When the two
disagree, this document is the contract.

**Revision `todo-eval/metrics-spec@3`, emitting `todo-eval/score@2`.** Revision 2 added the
#670 evidence layer: the coordination tool family and its refusal codes, wait outcomes,
token usage, sub-agent spawn timings and host startup work, the three fingerprints that
say whether two sweeps may be compared at all, and the redacted transcript forms a
committed archive carries. Revision 3 (#677) changes no measurement: it turns the success
thresholds below from prose into the machine-readable gate list the comparer evaluates, and
states when two sweeps may be compared at all. The score payload shape is unchanged, so the
emitted schema stays `todo-eval/score@2`. Every one of these is read from the store on disk;
nothing here needs a live host.

## Inputs

1. **Conversations directory** — the host's `conversations/` content-root directory for an
   isolated run. Every immediate subdirectory containing a `messages.json` is a thread and
   is scored: the primary `thread-*` directory and every `subagent-*` directory alike.
2. **Board snapshot (JSON file)** — the final board for the primary thread, in either form:
   - a raw `TodoBoardSnapshot` — the payload of `GET /api/conversations/{threadId}/todos`,
     or the `todo.board` value itself: `{ "Tasks": [ ... ] }` (property casing ignored); or
   - a thread `metadata.json`, in which case the snapshot is parsed out of
     `properties["todo.board"]` (a JSON **string**).
3. **Fixture** — `expected-board.json` (this directory) unless overridden.

## Conversation-store fields keyed on

Verified against a live conversation store (deployed LmStreaming.Sample, Aug 2026):

- `messages.json` is a JSON array of envelopes. Envelope fields used: `messageType`,
  `generationId`, `role` (used to select assistant `TextMessage`s for the
  fabricated-compliance heuristic; present on every envelope in the production store),
  and `messageJson` (a **string** holding the inner message JSON). Array order
  is the thread's message order; `messageOrderIdx` is per-generation and is NOT used for
  cross-message ordering.
- `messageType == "ToolCallMessage"` → inner fields used: `function_name`,
  `function_args` (a JSON string), `tool_call_id`, `generation_id`.
- `messageType == "ToolCallResultMessage"` → inner fields used: `tool_call_id`,
  `result`, and `generation_id` when present.
- A call is paired to its result by `tool_call_id` **within the same thread**. A call with
  no result in the same thread counts as a call, never as an error; the count of such
  unpaired calls is reported as `unpairedToolCalls`.
- **`is_error` alone is not trusted for the task family**: the store records
  `is_error: false` on results whose text is an error (observed in production data), so the
  text prefix is the primary signal there. Error detection is the defensive union:
  `is_error == true` OR the `Error:` text prefix. For the 15 task tools the flag adds
  nothing today (production data shows it always false); it is honoured so the oracle stays
  correct if the store ever starts setting it.
- **A coordination refusal is shaped differently** and would be scored as a SUCCESS by the
  rule above: it carries `is_error: true` and an `error_code`, and its text is a plain
  sentence with no `Error:` prefix (`"No agent named 'agent-9' is registered."`). The
  presence of an error code is therefore a third disjunct **for the coordination family
  only** — see *Error result*. Restricting it to that family is deliberate: it leaves the
  task tools' historical counts, and their comparability with the archived baseline,
  untouched.
- `messageType == "ToolCallResultMessage"` may also carry `error_code` (a string) beside
  `result`; that is what `ToolHandlerResult.FromError(text, code)` persists.

## Definitions

### Error result
A tool result whose result message has `is_error == true`, **or** whose `result` text,
with leading whitespace removed, starts with `Error:` (ordinal comparison,
case-sensitive). A non-string `result` is serialized to compact JSON first and then
tested the same way. **For the coordination family only**, a third disjunct applies: the
result has an *error code* (below). No disjunct is family-specific in the other direction —
a task result with `is_error: true` is still an error.

### Task tools
Exactly the 15 TaskManager tools, matched by `function_name` (ordinal, case-sensitive):
`add-task`, `bulk-initialize`, `update-task`, `claim-task`, `assign-task`, `block-task`,
`attach-artifact`, `delete-task`, `get-task`, `add-note`, `edit-note`, `delete-note`,
`list-notes`, `list-tasks`, `search-tasks`.

### Coordination tools
Exactly the 7 sub-agent tools (`SubAgentToolProvider.AllToolNames`), matched the same way:
`Agent`, `SendMessage`, `CheckAgent`, `WaitAgent`, `CheckAgents`, `WaitForAgents`,
`GetAgents`. The union of two surfaces — the supervisor-only one
(`Agent`/`SendMessage`/`CheckAgent`/`WaitAgent`) and the collaboration one, which swaps
`WaitAgent` for `CheckAgents`/`WaitForAgents`/`GetAgents` — so **no single conversation is
offered all seven**. Both scorers emit a row for every one of them anyway; the zeros are
how a reader tells "never called" from "never available".

### Tool families
Every `function_name` classifies as exactly one of:

| family | membership | counted in |
| --- | --- | --- |
| `task` | the 15 task tools | `taskToolCalls` / `taskToolErrors`, board bookkeeping |
| `coordination` | the 7 coordination tools | `coordinationToolCalls` / `coordinationToolErrors` |
| `other` | anything else (`web-search`, …) | `totalToolCalls` only; **no** per-tool row |

The two vocabularies are disjoint, and classification is ordinal and case-sensitive:
`checkagents` is `other`, because it is not a tool this host has.

Board bookkeeping — the vanished-id ledger and the block-recorded/cleared flags — reads
the `task` family alone. Retry storms read `task` and `coordination` alike.

### Error code
The stable string a failure is classified by, resolved in this order:

1. the result message's own `error_code`;
2. else a `code` property, when the `result` text itself parses to a JSON object;
3. else none.

A **coordination** failure with no code is reported as `unclassified` rather than omitted,
so a reader can never confuse "no refusals" with "refusals we failed to classify". A
**task** failure with no code contributes no row at all: the board tools report errors as
`Error:` text, and blanketing them as `unclassified` would bury the coordination taxonomy
under a hundred meaningless rows. `errorCodes` is reported per tool and rolled up
run-wide, with ordinal-sorted keys.

### Canonicalized arguments
`function_args` parsed as JSON and re-serialized canonically: object keys sorted by
ordinal comparison at every nesting level, no insignificant whitespace, values re-emitted
by the JSON serializer (so `1` and `1.0` differ only if they re-serialize differently;
string escapes are normalized). If `function_args` is absent, empty, or unparseable, the
raw string (empty string for absent) is the canonical form. Two calls are **identical**
when `function_name` and the UTF-8 bytes of the canonical form are equal.

### Per-tool error count / rate
For each of the 15 task tools **and each of the 7 coordination tools** — 22 rows, in that
order — across all threads:
- `calls` — number of ToolCallMessages with that `function_name`.
- `errors` — number of those calls whose paired result is an Error result.
- `errorRate` — `errors / calls` (0 when `calls` is 0), rounded to 4 decimal places.
- `family` — `task` or `coordination`.
- `errorCodes` — that tool's error-code tally (see *Error code*), ordinal-sorted.

### Retry storm
Within **one thread**, for one call identity (tool + canonical args): walk that thread's
calls of that identity in message order and count the current run of consecutive failing
occurrences; a successful occurrence of the **same identity** resets the run to zero.
Other tool calls interleaved between the occurrences do not break the run — the failing
calls need not be adjacent messages ("consecutive-per-thread, not necessarily adjacent").
Every **maximal** run of length >= 3 is one storm, reported with its thread, tool,
argument **digest**, and length. `retryStormCount` is the number of storms.

Storms are counted over the `task` **and** `coordination` families. `other` tools are not
walked at all.

**Polling exemption.** A run is extended only by a *failing* occurrence, so repeated
*successful* calls of one identity can never form a storm however many there are. That is
not incidental: `CheckAgent`/`CheckAgents`/`WaitAgent`/`WaitForAgents` are polls, and a
supervisor calling one twenty times while it waits is doing its job. Only a repeated
**refusal** is thrash. An unpaired call (no result) is neither failure nor success and
leaves the run untouched.

**Reported arguments are a digest, never the arguments.** `args` is
`{"__argsSha256":"<hex>"}` — the lowercase hex SHA-256 of the UTF-8 bytes of the
*canonical* form. `runs.jsonl` is a committed artifact, so echoing the model's arguments
into it would publish exactly what a redacted archive removes; and taking the digest over
the canonical bytes is what keeps two spec-identical calls identical. Arguments that are
already a digest pass through unchanged, so a redacted archive reports the same storm as
the raw store it was taken from.

### Board id vanished (#621 Part B)

A `task_not_found` whose id **this thread minted and never deleted** is a lost board row, not a
model typo. The transcript-side mirror of the server's `TodoBoardIdVanished` detector:

Per thread, in message order, maintain a **ledger** of canonical dotted ids:

- **Canonical id** — every dotted segment parsed as an integer and re-emitted (`" 01"` → `1`,
  `"+1.02"` → `1.2`), because `FindTaskByStringId` resolves all those spellings to the same row.
  An id with a non-integer segment has no canonical form and is skipped: that is an
  invalid-id error, not a not-found.
- **Add** on a successful `add-task` result matching `^Added task (<id>):`.
- **Add** on each `^\s*-\s*Task (<id>):` line of a successful `bulk-initialize` result; the same
  result clears the whole ledger first when it contains `Cleared existing tasks.`, because
  `clearExisting` is a requested reset that also renumbers from 1.
- **Remove**, together with every dotted descendant, on a successful `delete-task` result
  matching `^Deleted task (<id>) and all subtasks:` or
  `^Deleted subtask (<n>) from task (<id>):` (the latter removes `<id>.<n>`).

A task-tool call whose paired result is an Error result naming a **ledger id** is one vanish
event, reported with its thread, canonical task id, and tool. The id is read from the first
matching not-found wording — quoted forms tested before the bare ones, since the bare pattern
would otherwise capture the quotes and lose the event:

| Error text | Id |
| --- | --- |
| `Error: Task '<id>' has no subtask <n>.` | `<id>.<n>` |
| `Error: Task '<id>' not found.` | `<id>` |
| `Error: Parent task '<id>' not found.` | `<id>` |
| `Error: Blocking task '<id>' not found.` | `<id>` |
| `Error: Task <id> not found.` | `<id>` |
| `Error: Parent task <id> not found.` | `<id>` |

`boardIdVanished.count` is the number of events across all threads.

**This count is a lower bound, and both implementations say so in the emitted `note`.** The
authoritative signal is the server-side Warning; the eval mirrors it because the eval scores
archived transcripts (the #617 baseline corpus predates the detector and has no logs at all)
and because neither this oracle nor the C# harness reads structured logs. Three known gaps,
all one-directional — they undercount, never overcount:

1. A row minted by `bulk-initialize` as a **subtask** whose id that tool's result text never
   names. #634 R1 **narrowed** this gap; it did not close it. The tool now echoes the subtask rows
   it creates, indented one level deeper but in the same `- Task <id>:` shape both ledgers already
   match, so those ids do enter the ledger. What is still invisible is every subtask row in a
   transcript recorded before the change, plus the tail past the echo's 50-row cap on any board
   large enough to hit it — and that tail is invisible on new transcripts, not just archived ones.
   The direction is unchanged: still a one-directional undercount, never an overcount.
2. A row minted in a process whose transcript is not in this conversations directory.
3. A thread that starts from a rehydrated board — every id predating the transcript is unknown
   to the ledger. The server detector has no such gap: its ledger's unresolved entries ride the
   persisted snapshot as `TodoBoardSnapshot.MissingTaskIds`.

So a run reporting `boardIdVanished.count == 0` has **not** shown the board is intact; grep the
host's structured logs for the event name `TodoBoardIdVanished` as well.

### Block recorded / cleared
- `blockRecorded` — at least one **successful** (non-error) `block-task` call anywhere in
  the run whose canonical args contain a non-empty `blockedBy` array.
- `blockExplicitlyCleared` — at least one successful `block-task` call whose args omit
  `blockedBy` or pass it empty. Reported for information; not required, because completing
  every blocker auto-clears the block by design.
- `blockCleared` — `blockRecorded` AND the final board has zero tasks (any depth) with
  status `Blocked`. Clearing by explicit call and clearing by blocker completion both
  satisfy this.

### Completion
`true` iff the final board matches every check the fixture enables, AND the fixture's
conversation requirements hold. With `expected-board.json` as committed:

1. `topLevelTaskCount`: the board has exactly 3 top-level tasks.
2. `subtaskCountsSorted`: the per-top-level-task direct-child counts, sorted ascending,
   equal `[3, 3, 4]`.
3. `level3`: at least 1 depth-2 task has at least 2 direct children (the checklist item
   the task text orders broken down — a level `bulk-initialize` cannot create).
4. `allTasksCompleted`: every task at every depth has status `Completed` (status compared
   case-insensitively; the store serializes enum names).
5. `minNotesPerSubtask`: every task at depth >= 2 (subtasks and deeper) has at least 1
   note. Top-level workstream tasks are exempt.
6. `maxBlockedTasks`: zero tasks at any depth have status `Blocked`.
7. `requireBlockRecorded` / `requireBlockCleared`: as defined above.

Every failed check contributes a human-readable string to `completionFailures`.

### Run validity (precondition, not a score)

Production finding (Aug 2026): sub-agents spawned from agent-definition templates with a
restricted frontmatter `tools:` list received ZERO task tools in their schema
(`SubAgentTemplateMapper` maps frontmatter tools verbatim to `SubAgentTemplate.EnabledTools`;
null = inherit all parent tools, a list = only those), so they never emitted a task call —
and one fabricated compliance in prose. Such a run measures the template plumbing, not the
TaskManager API, so it is **invalid**, never a low score.

**Zero threads is always invalid.** A conversations directory with no scoreable thread
(no immediate subdirectory containing a `messages.json`) means the harness was pointed at
the wrong place — there is nothing the run could have measured. `validity.valid` is false
with the reason `no conversation threads found - harness misconfiguration` in
`validity.reasons`; such a run must never enter a sweep as a valid failed run.

**Zero sub-agent threads is VALID.** Spawning no sub-agents is model behavior under test,
not harness breakage, so `subAgentThreads == 0` does not invalidate the run. It is
surfaced prominently as top-level `subAgentCount` in the score summary (duplicating
`validity.subAgentThreads`) so sweep tables can segment no-sub-agent runs without digging
into the validity block.

Three gates:

1. **Mode-level** — the mode's enabled tools must include all 15 task tools (`mode.json` in
   this directory does).
2. **Template-level** — the sub-agent template each spawn resolves to must be inherit-all
   (`EnabledTools = null`; the built-in `general-purpose`/`researcher` templates are) or
   explicitly include the 15 names. Never run this eval with reviewer-style restricted
   templates. Verified operationally from the spawn log line
   `Sub-agent {AgentId} (template {Template}) inherited ... parent tool(s)`
   (`SubAgentManager.cs`), which names the inherited toolset.
3. **Oracle-side observable proxy** — the conversation store does not carry the tool
   schema, so the oracle uses the eval-specific invariant instead: every sub-agent in this
   task owns a workstream and MUST touch the board, so a `subagent-*` thread with **zero
   task-tool calls** marks the run invalid. `validity.valid` is false and the offending
   thread ids are listed in `validity.subAgentsWithoutTaskToolCalls`. Completion and the
   other metrics are still reported, but a sweep must discard invalid runs.

**Fabricated compliance** — a sub-agent thread with zero task-tool calls whose assistant
`TextMessage` text still claims board actions is additionally listed in
`validity.fabricatedComplianceSuspects`. Deterministic heuristic: any assistant text in
that thread matching regex `(?i)(claim|complet|marked)` AND `(?i)(task|todo|board)`. The
flag is only ever raised on tool-less threads, so a truthful report of real board work can
never be flagged. The regex CAN flag honest denials on a tool-less thread — e.g. "I could
not complete any task on the board" matches both patterns — so a listed thread is a
**triage pointer** into the transcript, not a verdict of fabrication.

### Wait outcomes
For each `WaitAgent` / `WaitForAgents` call, one outcome string, tallied run-wide as
`waitOutcomes`:

- the error code, when the result is an Error result (`unclassified` when it has none);
- else the result's `status` property, when the result parses to a JSON object carrying one
  (`running`, `timeout`, `question_received`, …);
- else `ok`.

This is what separates "the supervisor waited and the wait timed out" from "the supervisor
waited for an agent that does not exist" — two facts that a bare error count merges.

### Open obligations
`openObligations.lastObserved` is the `openObligations` number carried by the **last**
coordination result that had one; `resultsCarryingField` counts how many results carried it
at all. When `resultsCarryingField` is `0` the report carries a `note` saying so, because
this build does not emit the field yet (#673): **the zero means NOT REPORTED, not "none
were open"**. Both scorers compute the counter for real rather than hardcoding zero, so the
number starts moving on its own the day the host begins emitting it.

### Usage
Read from `metadata.json` → `properties["usage.records"]`, a JSON **string** holding the
array of `UsageRecord`s the host persisted. The host serializes it with default options, so
property names are PascalCase and `ExecutionKind` is the enum's **number**
(`0` Primary, `1` SubAgent, `2` WorkflowController, `3` WorkflowTask, `4` Continuation);
camelCase names and string enum values are accepted too, so a later serializer change
cannot silently blank the block into "this run cost nothing".

- Records are collected from the root thread's bag **and** every sub-agent's, then
  **deduped by `ProviderAttemptId`**: the same attempt is relayed into more than one bag by
  design, and counting it twice would double the run. The number of duplicates dropped is
  reported as `duplicateAttemptIds`.
- A record with neither `ProviderAttemptId` nor `LogicalCallId` is dropped: the dedupe key
  is that id, so admitting the record risks double-counting the very tokens this exists to
  attribute.
- The **owning agent** is `ParentExecutionId` when present, else `RootConversationId`, else
  `(unknown)`. For a sub-agent record `ParentExecutionId` holds the *child's* id.
- Rollups: `totals`, `byExecutionKind`, `byAgent`.
- **Turn attribution is a heuristic.** `UsageRecord` carries no generation id, so a record
  is joined to a turn by stripping the owner prefix from `ProviderAttemptId` (everything
  after the first `:`) and matching the remainder against that thread's generation ids.
  Records whose attempt key is synthetic (`derived:…`) can never join and land in
  `unattributedTurnTokens`. Both notes are emitted in the score object, next to the numbers
  they qualify.
- `kindsNotEmitted` names the execution kinds this build cannot produce
  (`WorkflowController`, `WorkflowTask`, `Continuation`), so their zeros are not read as
  measurements.
- **Tokens are not attributable to a tool family.** One turn's prompt carries every tool's
  schema at once; no per-family split exists in the data and none is invented.

### Spawn timings and coordination startup work
Two stamps written by `SubAgentInstrumentationProjection` when a host opts into
`SubAgentOptions.Instrumentation`, both JSON strings in the thread `metadata.json` properties
bag and both therefore readable offline from an archived store. The keys are library-owned
(`subagents.*`), like `usage.records`, because the library writes them; the host only asks to
be measured.

- `subagents.spawnTimings` — an array, one entry per sub-agent construction, with `AgentId`,
  `Template`, `ToolRegistryMs`, `ContextFanOutMs`, `TotalMs`, `InheritedToolCount`,
  `ToolCatalogBytes` and `Reconstructed`.
- `subagents.startupWork` — one roll-up object: `Spawns`, `Reconstructions`,
  `SpawnToolRegistryMs`, `SpawnContextFanOutMs`, `SpawnTotalMs`, `TemplateCatalogBuilds`,
  `TemplateCatalogBytes`, `DirectoryListings`, `DirectoryListingEntries`,
  `DirectoryListingBytes`.

**Both keys are run-wide, not per-thread, and a reader MUST take them from one thread only.**
The sink is a single object shared by the root loop and every collaborating child (it is
deliberately not cleared by `ForChildLoop`, so a grandchild's construction still counts), and
each loop stamps *the whole shared snapshot* onto its own thread as it saves metadata. So an
n-thread run archives n copies of the same measurement, each a prefix of the final one, and
the copies differ only in how late that thread last wrote. Concatenating `spawnTimings` across
threads multiplies every spawn by the number of threads that outlived it; summing `startupWork`
does the same to every counter.

The rule is therefore: **select the single richest stamp and report it verbatim** — never merge,
concatenate or sum across threads. Richest means the greatest
`Spawns + TemplateCatalogBuilds + DirectoryListings`; ties keep the first in the reader's
existing thread order, and a thread carrying neither key is skipped. Both values come from that
same stamp, so `spawnTimings` and `startupWork` always describe one consistent observation:
taking the timings from one thread and the roll-up from another can report more array entries
than `Spawns`. `spawnTimings` is `[]` and `startupWork` is `null` when no thread carries either.

Because the two keys are written together in one atomic property-bag update, a thread carrying
one and not the other cannot occur in a real store; a fixture that encodes that split is
describing a state production cannot reach.

`Reconstructed` separates a rebuilt finished agent from a fresh spawn: both pay the same
construction cost, so a run's re-construction share is invisible without the flag.
`ToolCatalogBytes` and the two `*Bytes` counters are the numbers a threshold can be set on -
milliseconds move with the hardware, and a fast box hides a catalog that keeps growing.

A malformed or older-shaped stamp costs the run its timings, never its score.

The Runner additionally records the wall-clock cost of *its own* host bring-up
(`hostPublishMs`, `hostReadyMs`) in `sweep-manifest.json`, not here: those are measured by
the harness, not derivable from the store.

### Redacted input forms
A committed archive is redacted (#669 shared decision 14) and **must score identically to
the raw store it replaces**. Both scorers therefore accept, wherever the raw form appears:

- `function_args` as `{"__argsSha256":"<hex>"}` — canonicalization treats it like any other
  object, so the call identity, and hence every storm, survives intact.
- assistant prose (`text`/`reasoning`/`thinking`) as
  `{"length":N,"claimVerbMatch":bool,"claimNounMatch":bool}` — the fabricated-compliance
  heuristic's *verdict*, which is the only thing the score reads prose for.
- `properties["sample.subAgentTask"]` / `["sample.subAgentName"]` in the same object form.

Tool **results** are deliberately NOT redacted: the vanished-id ledger is derived from
nothing but their text. They are deterministic server output over a fixed corpus.

### Fingerprints
Three hashes that say whether two sweeps may be compared at all. All are lowercase hex
SHA-256, and **none of them includes any build identity** — rebuilding a scorer must never
invalidate an archived baseline; only a change to a measurement-defining constant may.

- `taskCorpusHash` — WHAT the model was asked to do: `task.md`, `mode.json` and
  `expected-board.json`, in that order. Each file contributes
  `<name>\n<byteCount>\n<bytes>\n`; the name and the length are what stop two files'
  contents from sliding into each other and hashing the same. CR bytes are stripped first,
  so a CRLF checkout hashes like an LF one. A **missing** file contributes a byte count of
  `-1` and no bytes, which stays distinguishable from a genuinely empty one. Frozen at run
  time and recorded as `ranUnder`; a later comparison that finds a different corpus hash is
  comparing two different tasks.
- `specHash` — the measurement contract's identity: `<specVersion>\n<schema>`.
- `evaluatorHash` — every constant that can move a measured NUMBER:
  `<specHash>\n<task tools, ordinal-sorted, comma-joined>\n<coordination tools, same>\n<storm threshold>\n__argsSha256`.

`--extract-only` recomputes the set as `extractedUnder`; a difference between it and the
archived `ranUnder` is the signal that the archive was scored by a different evaluator than
the one that produced it.

### Total tool calls
Count of ToolCallMessages across all threads — **all** tools, not only task tools.
`taskToolCalls` and `taskToolErrors` are the same totals restricted to the 15 task tools;
`coordinationToolCalls` and `coordinationToolErrors` to the 7 coordination tools. The two
never overlap, and their sum is `totalToolCalls` minus the `other` family.

### Turns
Per thread: the number of distinct non-null generation ids, taken from the envelope
`generationId` or, when that is null, the inner message's `generationId`/`generation_id`.
`turns` is the sum over all threads (one generation = one model call = one turn);
`primaryTurns` is the same count restricted to threads whose directory name does not start
with `subagent-`.

## Score object

`score.ps1` emits exactly one JSON object. **Every value below is an illustrative
placeholder chosen to show the shape** — none of them is a recorded measurement, so no
threshold may be set from a number on this page. Real figures live in an archived run's
`summary.md` and `runs.jsonl`.

```json
{
  "schema": "todo-eval/score@2",
  "conversationsDir": "...",
  "threads": 4,
  "subAgentCount": 3,
  "totalToolCalls": 63,
  "taskToolCalls": 58,
  "taskToolErrors": 3,
  "coordinationToolCalls": 4,
  "coordinationToolErrors": 1,
  "unpairedToolCalls": 0,
  "perTool": {
    "add-note": { "calls": 14, "errors": 1, "errorRate": 0.0714, "family": "task", "errorCodes": {} },
    "WaitForAgents": { "calls": 3, "errors": 1, "errorRate": 0.3333, "family": "coordination",
                       "errorCodes": { "unknown_agent": 1 } },
    "...": {}
  },
  "errorCodes": { "unknown_agent": 1 },
  "waitOutcomes": { "timeout": 1, "unknown_agent": 1 },
  "openObligations": { "lastObserved": 0, "resultsCarryingField": 0, "note": "... NOT REPORTED ..." },
  "usage": {
    "totals": { "records": 12, "inputTokens": 0, "outputTokens": 0, "cacheReadTokens": 0,
                "cacheWriteTokens": 0, "reasoningTokens": 0, "totalTokens": 0 },
    "duplicateAttemptIds": 1,
    "byExecutionKind": { "Primary": { "...": 0 }, "SubAgent": { "...": 0 } },
    "byAgent": { "thread-...": { "...": 0 } },
    "kindsNotEmitted": [ "WorkflowController", "WorkflowTask", "Continuation" ],
    "attributedTurnTokens": 0,
    "unattributedTurnTokens": 0,
    "notes": [ "Turn attribution is a HEURISTIC ...", "Tokens are NOT attributable to a tool family ..." ]
  },
  "spawnTimings": [ { "AgentId": "agent-1", "Template": "general-purpose",
                      "ToolRegistryMs": 37, "ContextFanOutMs": 12, "TotalMs": 61,
                      "InheritedToolCount": 21, "ToolCatalogBytes": 18432,
                      "Reconstructed": false } ],
  "startupWork": { "Spawns": 3, "Reconstructions": 1, "SpawnToolRegistryMs": 91,
                   "SpawnContextFanOutMs": 30, "SpawnTotalMs": 174,
                   "TemplateCatalogBuilds": 11, "TemplateCatalogBytes": 47300,
                   "DirectoryListings": 1, "DirectoryListingEntries": 2,
                   "DirectoryListingBytes": 412 },
  "fingerprints": { "taskCorpusHash": "...", "specHash": "...", "evaluatorHash": "...",
                    "specVersion": "todo-eval/metrics-spec@3" },
  "retryStormCount": 0,
  "retryStorms": [ { "threadId": "...", "tool": "add-note", "count": 4,
                     "args": "{\"__argsSha256\":\"...\"}" } ],
  "boardIdVanished": {
    "count": 0,
    "events": [ { "threadId": "...", "taskId": "1.2", "tool": "get-task" } ],
    "note": "Transcript-derived LOWER BOUND ... also grep the host structured logs for TodoBoardIdVanished"
  },
  "blockRecorded": true,
  "blockExplicitlyCleared": true,
  "blockCleared": true,
  "completion": true,
  "completionFailures": [],
  "turns": 41,
  "primaryTurns": 12,
  "validity": {
    "valid": true,
    "reasons": [],
    "subAgentThreads": 3,
    "subAgentsWithoutTaskToolCalls": [],
    "fabricatedComplianceSuspects": []
  }
}
```

`perTool` always carries all 22 rows — 15 task tools then 7 coordination tools, including
zero-call rows — so a reader who skips the definitions still sees which tools never ran.

## Comparing two sweeps

A before/after comparison is only meaningful between two sweeps that were asked the same
thing and whose numbers were produced by the same evaluator. The comparer therefore either
**refuses** and publishes its reason, or compares and publishes every row below. It never
publishes a partial number set.

### When a comparison refuses

Checked in this order; the first match is the reported cause, so the reason names the most
specific difference rather than a downstream hash it also moved.

| # | Refusal | Reads | Fires when |
| --- | --- | --- | --- |
| 1 | `ManifestMissing` | — | Either directory lacks `sweep-manifest.json` or `runs.jsonl`. An archive from before the fingerprint manifest cannot say what produced its numbers. |
| 2 | `CorpusHashDiffers` | `ranUnder.taskCorpusHash` | The two sweeps were asked different things: `task.md`, `mode.json` or `expected-board.json` moved between the runs. |
| 3 | `SpecVersionDiffers` | `extractedUnder.specVersion` | The two sets of numbers were extracted under different revisions of this document. |
| 4 | `EvaluatorHashDiffers` | `extractedUnder.evaluatorHash` | Same revision, but a measurement-defining constant differs between the two extractions. |
| 5 | `CoverageBelowMinimum` | both sweeps | Completed runs / total runs is below **50%** in either sweep. |
| 6 | `FaultRateAboveMaximum` | both sweeps | Harness faults (`harness_error`, `timeout`, `interrupted`) exceed **25%** of runs in either sweep. |

Rows 5 and 6 are **comparability bounds, not quality targets**: they say a sweep is too thin
or too broken to characterise itself. "Did completion improve" is a gate, below, and is
never double-counted here. Both bounds are engineering pins, not values derived from
evidence.

Ordering 3 before 4 is load-bearing. A revision bump moves `specVersion`, `specHash` **and**
`evaluatorHash` together, so checking the evaluator hash first would mask every bump behind
it and leave `SpecVersionDiffers` permanently unreachable. `EvaluatorHashDiffers` stays
reachable on its own: the tool vocabularies and the retry-storm threshold feed the evaluator
hash without touching the revision.

**Task type** needs no refusal of its own — `mode.json` is one of the three corpus files, so
a different task type is already a different `taskCorpusHash`.

### `ranUnder` versus `extractedUnder`

The manifest records the fingerprint triple twice: `ranUnder` is frozen when the sweep runs,
`extractedUnder` is recomputed on every `--extract-only`. The refusals read them
asymmetrically, and the asymmetry is the point:

- The **corpus** refusal reads `ranUnder`, because that is the only recording of what the
  model actually faced. `extractedUnder.taskCorpusHash` is recomputed from the corpus on
  disk *now*, so two sweeps re-extracted on one checkout always share it and a corpus
  refusal reading it could never fire.
- The **spec** and **evaluator** refusals read `extractedUnder`, because that is what says
  whether the two sets of numbers were produced by the same evaluator.
- A `ranUnder` spec or evaluator difference is published as **contract drift and never
  refuses**. Both archives are re-scored by today's identical evaluator, which is exactly
  what lets this document reach a new revision without forcing a baseline re-run.

Re-extracting an archive requires its `conversations/` directory. An archive that has none —
`results/baseline-2026-08-30` and `results/postfix-2026-08-30` are both pre-#670 and have
none — cannot be re-extracted in place, and running `--extract-only` against it would
overwrite its `runs.jsonl` with zeros.

## Success thresholds (evaluated post-fix vs the archived baseline)

Each row is one gate the comparer evaluates from the same rolled-up figures the before/after
table prints, so the verdict and the numbers beneath it cannot disagree. Baseline-derived is
the rule; only the two figures #621 states as numbers are absolute, and a worse baseline
never raises an absolute ceiling.

| Gate id | Direction | Threshold | Not measurable when |
| --- | --- | --- | --- |
| `task-tool-error-rate` | at most | baseline | Either sweep made no board tool call |
| `coordination-tool-error-rate` | at most | baseline | Either sweep made no coordination call |
| `board-id-vanished` | at most | baseline | — |
| `completion-rate` | at least | baseline | No `expected-board.json` was supplied, so completion was never judged |
| `average-turns` | at most | baseline | Either sweep has no runs |
| `unknown-agent-waits` | at most | baseline | The baseline recorded no wait call at all |
| `tool-calls-per-successful-run` | at most | baseline | Either sweep has no valid completed run |
| `input-tokens-per-successful-run` | at most | baseline | No usage record was persisted with the comparable runs |
| `tool-catalog-bytes-per-spawn` | at most | baseline | No spawn timing was stamped |
| `add-note-error-rate` | at most | **0.05** (#621) | The candidate made no `add-note` call |
| `retry-storms` | at most | **0** (#621) | — |
| `open-obligations` | at most | **0** | Neither sweep emitted an obligation-reporting result |

Baseline-derived gates compare against the archived sweep's own figure, so "no worse than
baseline" is a number, not a judgement. `#621`'s original prose targets map onto this table
as: `add-note` error rate **at most 5%** (65% in the Aug 21-30 investigation store), retry
storms **0** (one 48x storm and several 2-5x in the same store), and completion rate and
turns **no worse than the baseline sweep**.

Three rules keep the verdict honest:

1. **A rate over zero calls is undefined, not 0%.** Every gate whose denominator is empty,
   whose baseline never recorded the signal, or whose evidence was never emitted reports
   `NotMeasurable` — printed as UNPROVEN. It is never a pass, never sets "all gates passed",
   and never fails the run; it is republished under "Contrary evidence" instead.
2. **A pass by a hair is contrary evidence.** A baseline-derived gate whose actual sits
   within 5% of its own threshold is flagged, so a reader is never told a metric improved
   when it merely failed to get measurably worse. Meeting an absolute target is never
   flagged, and a zero threshold has no proportional margin to sit inside.
3. **Every metric that moved the wrong way is republished**, gated or not, so a summary
   cannot report only the metrics that improved.

The thresholds bind a post-fix sweep compared against an archived baseline, not any single
smoke run; a smoke run only has to produce a well-formed score object.
