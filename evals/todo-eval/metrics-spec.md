# todo-eval metrics specification

Deterministic scoring for the `todo-eval` Testing Mode (#617/#618). No LLM judging: every
metric is computed from the conversation store on disk plus one final board snapshot.
`score.ps1` in this directory is the reference implementation and manual-validation tool;
the #619 harness reimplements this same spec in C# with its own tests. When the two
disagree, this document is the contract.

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
- **`is_error` alone is not trusted**: the store records `is_error: false` on results
  whose text is an error (observed in production data), so the text prefix is the primary
  signal. Error detection is the defensive union: `is_error == true` OR the `Error:` text
  prefix. Today the flag adds nothing (production data shows it always false); it is
  honoured so the oracle stays correct if the store ever starts setting it.

## Definitions

### Error result
A tool result whose result message has `is_error == true`, **or** whose `result` text,
with leading whitespace removed, starts with `Error:` (ordinal comparison,
case-sensitive). A non-string `result` is serialized to compact JSON first and then
tested the same way.

### Task tools
Exactly the 15 TaskManager tools, matched by `function_name` (ordinal, case-sensitive):
`add-task`, `bulk-initialize`, `update-task`, `claim-task`, `assign-task`, `block-task`,
`attach-artifact`, `delete-task`, `get-task`, `add-note`, `edit-note`, `delete-note`,
`list-notes`, `list-tasks`, `search-tasks`.

### Canonicalized arguments
`function_args` parsed as JSON and re-serialized canonically: object keys sorted by
ordinal comparison at every nesting level, no insignificant whitespace, values re-emitted
by the JSON serializer (so `1` and `1.0` differ only if they re-serialize differently;
string escapes are normalized). If `function_args` is absent, empty, or unparseable, the
raw string (empty string for absent) is the canonical form. Two calls are **identical**
when `function_name` and the UTF-8 bytes of the canonical form are equal.

### Per-tool error count / rate
For each of the 15 task tools, across all threads:
- `calls` — number of ToolCallMessages with that `function_name`.
- `errors` — number of those calls whose paired result is an Error result.
- `errorRate` — `errors / calls` (0 when `calls` is 0), rounded to 4 decimal places.

### Retry storm
Within **one thread**, for one call identity (tool + canonical args): walk that thread's
calls of that identity in message order and count the current run of consecutive failing
occurrences; a successful occurrence of the **same identity** resets the run to zero.
Other tool calls interleaved between the occurrences do not break the run — the failing
calls need not be adjacent messages ("consecutive-per-thread, not necessarily adjacent").
Every **maximal** run of length >= 3 is one storm, reported with its thread, tool,
canonical args, and length. `retryStormCount` is the number of storms. Storms are counted
over task tools only.

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

1. A row minted by `bulk-initialize` as a **subtask**, in a transcript recorded before that tool
   echoed its subtask rows (#634 R1). It now lists every row it creates, indented one level deeper
   but in the same `- Task <id>:` shape both ledgers already match, so this gap is closed for
   transcripts recorded after the change and remains open only for the archived corpus.
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

### Total tool calls
Count of ToolCallMessages across all threads — **all** tools, not only task tools.
`taskToolCalls` and `taskToolErrors` are the same totals restricted to the 15 task tools.

### Turns
Per thread: the number of distinct non-null generation ids, taken from the envelope
`generationId` or, when that is null, the inner message's `generationId`/`generation_id`.
`turns` is the sum over all threads (one generation = one model call = one turn);
`primaryTurns` is the same count restricted to threads whose directory name does not start
with `subagent-`.

## Score object

`score.ps1` emits exactly one JSON object:

```json
{
  "schema": "todo-eval/score@1",
  "conversationsDir": "...",
  "threads": 4,
  "subAgentCount": 3,
  "totalToolCalls": 63,
  "taskToolCalls": 58,
  "taskToolErrors": 3,
  "unpairedToolCalls": 0,
  "perTool": { "add-note": { "calls": 14, "errors": 1, "errorRate": 0.0714 }, "...": {} },
  "retryStormCount": 0,
  "retryStorms": [ { "threadId": "...", "tool": "add-note", "count": 4, "args": "{...}" } ],
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

`perTool` always carries all 15 task tools, including zero-call rows, so a reader who
skips the definitions still sees which tools never ran.

## Success thresholds (targets from #621, evaluated post-fix vs the archived baseline)

| Metric | Baseline (investigation, Aug 21-30 store) | Target after API fix |
| --- | --- | --- |
| `add-note` error rate (F1 family) | 65% of all add-note calls failed | **< 5%** |
| Retry storms (3+ identical failing calls) | one 48x storm, several 2-5x | **0** |
| Completion rate | — (established by the baseline sweep) | no worse than baseline |
| Turns to completion | — (established by the baseline sweep) | no worse than baseline |

The thresholds bind the post-fix sweep (#621), not any single smoke run; a smoke run only
has to produce a well-formed score object.
