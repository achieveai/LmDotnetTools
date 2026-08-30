# Feature Specification: Todo Manager

## High-Level Overview

The Todo Manager is an in-memory task management system that provides LLM-accessible functions for creating, managing, and displaying hierarchical todo lists. It supports arbitrarily deep task hierarchies, status tracking, note-taking, and markdown output formatting.

## High Level Requirements

- **In-memory task storage** - No persistence across application restarts
- **Function call integration** - Expose functions via FunctionCallMiddleware with kebab-case names
- **Unbounded hierarchy** - Support tasks nested to any depth
- **Status management** - Track task progress through defined status states
- **Note management** - Add and update notes for any task
- **Markdown output** - Generate human-readable task lists in markdown format
- **Dotted path IDs** - Address any task by its position in the tree (`1`, `1.2`, `1.2.3`)

## Existing Solutions

### Current Codebase Patterns
- **TypeFunctionProvider**: Attribute-based function registration using `[Function()]` and `[Description()]`
- **Custom Function Providers**: Manual function contract creation with full control over parameters
- **FunctionCallMiddleware**: Integration layer that handles JSON parameter parsing and response formatting

### External Research
- Common todo management patterns use hierarchical task structures
- Status enums for lifecycle management (NotStarted, InProgress, Completed, Removed)
- ID-based task operations for efficient lookups
- Recursive operations for handling parent-child relationships

## Current Implementation

`src/Misc/Utils/TaskManager.cs` already implements this feature and has done since before
this specification was written. It is a plain class whose `[Function]`-annotated
methods are surfaced by `TypeFunctionProvider`, and it is wired up per conversation in
`samples/LmStreaming.Sample/Program.cs`. Its tests live in
`tests/Misc.Tests/Utils/TaskManagerTests.cs`.

An earlier revision of this document opened by asserting that no implementation existed.
That was wrong, and it cost a duplicate implementation (PR #511's `TodoManager`) before
anyone noticed. Everything below is therefore written as a description of `TaskManager`,
not as a design for something new. Where the original acceptance criteria disagreed with
the shipped behaviour, the criterion has been amended and the amendment is called out.

## Detailed Requirements

### Requirement 1: Task Model and Storage
- **User Story**: As a user, I need a way to represent tasks with hierarchical relationships and metadata so that I can organize my work effectively.

#### Acceptance Criteria:
1. **Task Structure**: WHEN creating the task model THEN it SHALL include properties for ID (string — the dotted path its parent chain produces), Title (string), Status (enum), Notes (List<string>), and SubTasks (List<TaskItem>)
   - *Amended.* This criterion previously said `ID (int)`, which contradicted criterion 3: an int cannot name `1.2.3`. `TaskItem.Id` is the dotted path string, and every tool takes and returns that path. An int sibling ordinal is still kept internally, but it is not the addressable ID.
2. **Status Enum**: WHEN defining task status THEN it SHALL support "NotStarted", "InProgress", "Completed", and "Removed" states
3. **Hierarchy Depth**: WHEN adding subtasks THEN the system SHALL impose no depth limit — a task may be nested arbitrarily deep, and every task SHALL be addressable by the dotted path its parent chain produces (`1`, `1.2`, `1.2.3`, ...). All ids are 1-based: the first main task is `1`, its first subtask `1.1`, and the per-level `subtaskId` sibling ordinals the tools accept start at 1.
   - *Amended.* This criterion previously required a maximum depth of 2 levels. The shipped implementation nests without limit, the tool descriptions advertise depth-3 examples, and the cap was never enforced. The cap is withdrawn rather than retrofitted.
   - *Practical serializer bound.* The in-memory model is unlimited-depth, but the persisted/pushed snapshot passes through System.Text.Json with its default `MaxDepth` of 64, so ~60 task levels is the effective bound on boards that survive persistence (see the comment at the `Serialize` call in `ConversationTodoProjection.SaveAsync`).
4. **In-Memory Storage**: WHEN storing tasks THEN they SHALL be kept in memory only with no persistence
5. **Instance Scope**: WHEN a host serves several conversations THEN each SHALL get its own `TaskManager`; the type holds mutable state and MUST NOT be registered as a shared singleton

### Requirement 2: Add Task Function
- **User Story**: As a user, I need to add new tasks and subtasks so that I can build my todo list.

#### Acceptance Criteria:
1. **Function Exposure**: WHEN registering functions THEN "add-task" SHALL be available to LLM with parameters for `title` and optional `parentId`
   - *Amended.* The parameter is `parentId`, not `parent_id`. The tool schema is generated from the C# parameter names verbatim, so the snake_case spellings this document used throughout named parameters that do not exist.
2. **Main Task Creation**: WHEN calling add-task without `parentId` THEN it SHALL create a new main task with auto-incremented ID and "NotStarted" status
3. **Subtask Creation**: WHEN calling add-task with a valid `parentId` THEN it SHALL create a subtask under the specified parent, at any depth
4. **Error Handling**: WHEN calling add-task with an invalid or blank `parentId` THEN it SHALL return an error message in markdown format. A blank `parentId` SHALL NOT be treated as an omitted one
5. **Response Format**: WHEN task is successfully created THEN it SHALL return confirmation message with task ID in markdown format

### Requirement 3: Update Task Status Function
- **User Story**: As a user, I need to update task status so that I can track progress on my work.

#### Acceptance Criteria:
1. **Function Exposure**: WHEN registering functions THEN "update-task" SHALL be available with parameters for `taskId` and `status`
   - *Amended.* The parameter is `taskId`, not `task_id` — see the note on Requirement 2.1.
2. **Status Validation**: WHEN updating status THEN it SHALL accept "not started", "in progress", "completed", or "removed", along with the hyphenated and underscored spellings a model is apt to emit ("not-started", "in-progress", "to-do", ...)
   - *Amended.* "update-task" SHALL refuse a direct "blocked" target and direct the caller to "block-task" instead — see Requirement 8.1. Setting `in progress` with an `agent` name now goes through the claim/lease path in Requirement 8.3, and setting `completed` now requires the task to be `InProgress` with a non-null `assignee` — see Requirement 8.8.
3. **Task Lookup**: WHEN updating task status THEN it SHALL find tasks by dotted path across all hierarchy levels
4. **Error Handling**: WHEN `taskId` is invalid THEN it SHALL return error message in markdown format
5. **Response Format**: WHEN status is successfully updated THEN it SHALL return confirmation message in markdown format

### Requirement 4: Add Task Notes Function
- **User Story**: As a user, I need to add notes to tasks so that I can capture additional context and details.

#### Acceptance Criteria:
1. **Function Exposure**: WHEN registering functions THEN "add-note" SHALL be available with parameters for `taskId` and `noteText`, alongside "edit-note", "delete-note" and "list-notes"
   - *Amended.* The tool is named `add-note`, not `add-task-notes`, and its parameters are `taskId` and `noteText`, not `task_id` and `note` — see the note on Requirement 2.1.
2. **Note Appending**: WHEN adding notes THEN it SHALL append to the existing notes list for the task
3. **Task Lookup**: WHEN adding notes THEN it SHALL find tasks by dotted path across all hierarchy levels, not only the first two
4. **Error Handling**: WHEN `taskId` is invalid THEN it SHALL return error message in markdown format
5. **Response Format**: WHEN note is successfully added THEN it SHALL return confirmation message in markdown format

### Requirement 5: List Tasks Function
- **User Story**: As a user, I need to view all my tasks so that I can see what work needs to be done.

#### Acceptance Criteria:
1. **Function Exposure**: WHEN registering functions THEN "list-tasks" SHALL be available with optional `status` and `mainOnly` parameters
   - *Amended.* This criterion previously said "with no parameters". The shipped tool takes two optional filters.
2. **Hierarchical Display**: WHEN listing tasks THEN it SHALL show main tasks with indented subtasks, two spaces per level
3. **Status Indicators**: WHEN displaying tasks THEN it SHALL use `[ ]` for not started, `[-]` for in progress, `[x]` for completed and `[~]` for removed, with a trailing ` (removed)` on a removed task
   - *Amended.* The removed marker is stated explicitly. It was previously `[d]`; `[~]` was chosen because it reads as "struck through" rather than as an abbreviation, and nothing else in the format uses `~`.
4. **Notes Display**: WHEN tasks have notes THEN it SHALL display them indented under the task with numbering
5. **Artifacts Display**: WHEN tasks have attached artifacts (Requirement 9) THEN it SHALL display them after the notes block, indented under the task, as an `Artifacts:` label followed by one unnumbered `- <path>` bullet per artifact — unnumbered because, unlike notes, no tool addresses an artifact by index
   - *Added with Requirement 9.* Renumbers the criteria that follow.
6. **Markdown Format**: WHEN returning task list THEN it SHALL emit the format shown in "Worked Example" below
   - *Amended.* The original criterion pointed at a "provided example" that this document never contained. The example below is the format the implementation actually produces, and `tests/Misc.Tests/Utils/TaskManagerTests.cs` pins it byte for byte.
7. **Header On Empty**: WHEN there are no tasks, or none match the filter, THEN it SHALL still emit the header, followed by "No tasks found." or "No tasks match the specified criteria." A bare sentence with no header gives the model no clue which tool answered it.
8. **Line Endings**: WHEN rendering THEN lines SHALL be separated by `\n` regardless of host platform, so the output is identical on Windows and elsewhere

### Worked Example

Numbering is the dotted path, not a bullet list. There is no `- ` prefix. The status
summary block appears only for an unfiltered, non-`mainOnly` listing.

```text
# 📋 Task List

**Status**: 1 in progress | 2 pending | 0 blocked | 1 completed
**Total**: 3 active tasks

[-] 1. Design API
  Notes:
  1. Rate limit is 100/min
  2. Auth via JWT
  [x] 1.1. Define endpoints [@tester]
    [ ] 1.1.1. Validate JWT
  [~] 1.2. Draft schema (removed)
[ ] 2. Ship it
```

Note that the counts describe *active* work: a `[~]` removed task is excluded from
"**Total**: N active tasks" and from the pending count. The blocked count is now named
explicitly rather than folded silently into "pending", and task 1.1 carries a `[@tester]`
tag because completing it required claiming it first (Requirement 8.8) and `assignee` is
durable ownership that survives the `Completed` transition rather than being cleared — see
Requirement 8 for the coordination fields (`Blocked`, `assignee`, `blockedBy`, elapsed-time)
that this document was amended to cover, and the row-suffix rendering (`[@assignee]`,
`(Nm)`, `(blocked by ...)`) they add. A task with attached artifacts (Requirement 9)
additionally renders an `Artifacts:` block of `- <path>` bullets directly after its notes,
at the same indent — `ListTasks_RendersArtifactsAsPlainBulletsUnderTheTask` pins that
rendering byte for byte.

### Requirement 6: Markdown Generation Method
- **User Story**: As a developer, I need a method to generate markdown representation so that I can get formatted output programmatically.

#### Acceptance Criteria:
1. **Method Availability**: WHEN implementing TaskManager THEN it SHALL provide a GetMarkdown() method
2. **Format Consistency**: WHEN generating markdown THEN it SHALL match the same format as list-tasks function
3. **Complete Output**: WHEN calling GetMarkdown THEN it SHALL include all tasks, subtasks, and notes
4. **Header Inclusion**: WHEN generating markdown THEN it SHALL include the `# 📋 Task List` header
   - *Amended.* The header is `# 📋 Task List`, not `# TODO`.

### Requirement 7: Function Provider Integration
- **User Story**: As a developer, I need the TodoManager to integrate with the function call system so that LLMs can use it.

#### Acceptance Criteria:
1. **Provider Implementation**: WHEN creating TaskManager THEN it SHALL be a plain class whose `[Function]`-annotated methods are surfaced by `TypeFunctionProvider`; it SHALL NOT implement `IFunctionProvider` itself
   - *Amended.* Hand-rolling `IFunctionProvider` would duplicate the reflection the framework already does.
2. **Function Registration**: WHEN getting functions THEN `TypeFunctionProvider` SHALL return FunctionDescriptor objects for all fifteen operations: add-task, bulk-initialize, update-task, delete-task, get-task, add-note, edit-note, delete-note, list-notes, list-tasks, search-tasks, claim-task, assign-task, block-task, attach-artifact
   - *Amended.* The original eleven operations are unchanged; `claim-task`, `assign-task`, and `block-task` were added for the coordination fields in Requirement 8, and `attach-artifact` for the file artifacts in Requirement 9.
3. **Parameter Mapping**: WHEN functions are called THEN arguments SHALL bind even when the model's JSON types differ from the declared ones — a quoted number onto a numeric parameter, an unquoted number onto a string parameter — and a parameter the model omitted SHALL take its declared C# default rather than the type's zero value
4. **Error Handling**: WHEN operations fail THEN it SHALL return descriptive error messages instead of throwing exceptions, and SHOULD mark the tool result as an error so the model and the host can tell a failure from a successful answer whose text happens to start with "Error"
   - *Amended.* Both halves are shipped. Every failure returns a message rather than throwing, and all fifteen tools return `FunctionResult`, so a domain failure reaches the model with `IsError = true` and a lower_snake_case error code (`task_not_found`, `invalid_args`, `invalid_task_id`, `invalid_status`, `note_index_out_of_range`, `task_not_claimable`, `task_blocked`, `task_already_claimed`, `task_not_claimed`, `invalid_artifact_path`, `block_cycle`) while a success carries no code. The text on the wire is unchanged — only `Text` is serialized — so the contract still advertises `string`.
5. **Statefulness**: WHEN a provider is built around a live instance THEN its descriptors SHALL be marked `IsStateful`, so hosts that only accept stateless tools exclude it rather than sharing one conversation's list with another

### Requirement 8: Coordination Fields (Assignee, Claim/Lease, Blocked)

- **User Story**: As an orchestrator handing work to other agents, I need to assign tasks,
  claim them by name, block on dependencies, and see when a claim has gone stale, so that
  multiple agents can coordinate through one task list without stepping on each other.

#### Acceptance Criteria:
1. **Blocked Status**: WHEN a task cannot proceed THEN "block-task" SHALL set its status to
   `Blocked` and record the blocking task IDs in `blockedBy`; "update-task" SHALL refuse a
   direct `blocked` status target and point the caller at `block-task` instead, so `Status`
   and `BlockedBy` never disagree
   - *Amended (#595).* "block-task" SHALL refuse, atomically (no partial write), an edge
     that would close a dependency cycle — direct (`a<->b`), transitive
     (`a->b->c->a`), or a self-block — returning `block_cycle` (self-block:
     `invalid_args`) with the cycle path named, because every member of a `blockedBy`
     cycle waits on another member and auto-unblock only fires on completion, so the
     deadlock would be silent and permanent. It SHALL also refuse a `Completed` or
     `Removed` task as a blocker (`invalid_args`): a resolved task never completes
     (again) to lift the block, so accepting it would mint a `Blocked` row that every
     claim guard passes — the caller is pointed at re-opening the blocker first.
2. **Assignee**: WHEN a task is created, assigned via "assign-task", or claimed via
   "claim-task" THEN it SHALL carry an `assignee` (agent name); a sub-task created under an
   assigned parent SHALL inherit the parent's assignee unless the caller overrides it
   - *Amended.* "assign-task" refuses to reassign a task whose current `InProgress` claim is
     still live (not stale by Requirement 8.3's threshold), returning `task_already_claimed`
     — the same rule "claim-task" enforces, so assignment can never silently steal a live
     lease. Reassigning over a *stale* claim succeeds but resets the task to `NotStarted`
     (clearing `claimedAt`) rather than leaving it `InProgress` under the new name, so
     assignment alone can never violate Requirement 8.4's one-in-progress-per-assignee
     invariant and the new assignee must claim the task explicitly before completing it.
3. **Claim Is A Lease, Not A Lock**: WHEN "claim-task" is called with an agent name THEN it
   SHALL move `NotStarted -> InProgress` and stamp `claimedAt`; a second agent's claim
   attempt SHALL be refused *unless* the existing claim is older than the staleness
   threshold (15 minutes by default), in which case the lease is taken over. Staleness is
   derived from `claimedAt` on read — there is no background sweeper
4. **One In-Progress Task Per Assignee**: WHEN an agent claims a new task THEN any other
   task already `InProgress` under that same assignee SHALL be released back to
   `NotStarted` (its assignee kept, `claimedAt` cleared) rather than left running in
   parallel
5. **Blocked Tasks Are Not Claimable**: WHEN a task is `Blocked` THEN every route that can
   move it to `InProgress` — "claim-task", "update-task" (with or without an `agent`), and
   "claim-task"'s same-holder lease refresh — SHALL refuse it until every ID in `blockedBy`
   is resolved
   - *Amended.* The original wording scoped this guard to "claim-task" alone; a plain
     `update-task <id> "in progress"` with no `agent` moved the status without going through
     "claim-task" at all, so it could bypass the block. The guard is now a single shared
     check (`RefuseIfBlocked`) applied by every route, so `Status` and `BlockedBy` can never
     disagree.
6. **Auto-Unblock On Completion**: WHEN a task completes THEN every other task that named it
   in `blockedBy` SHALL have that ID removed, and SHALL return to `NotStarted` once its
   `blockedBy` list is empty
   - *Amended (#595).* Auto-unblock is one-way: re-opening a completed blocker does NOT
     re-block its former dependents. The re-block path is explicit — re-open the blocker
     (`update-task <id> "not started"`), then call "block-task" again — and the
     re-established block carries the same enforcement as the original. "block-task"'s
     description documents both halves.
7. **Timestamps**: WHEN a task is created, claimed, or completed THEN `createdAt`,
   `claimedAt`, and `completedAt` SHALL be stamped from an injectable clock
   (`TimeProvider`), so tests can assert on them deterministically
8. **Completion Requires An Active Claim**: WHEN "update-task" is asked to mark a task
   `completed` THEN it SHALL require the task to currently be `InProgress` with a non-null
   `assignee`, and SHALL return a descriptive error (not a silent no-op) otherwise
9. **Backward Compatibility**: WHEN loading a persisted task tree written before these
   fields existed THEN `assignee`, `blockedBy`, and the timestamp fields SHALL default to
   absent/empty rather than failing to deserialize
10. **Coordination State Survives Restart** *(added by #595, reviews 590/D-1 and D2)*: WHEN a
    board snapshot is serialized THEN `blockedBy`, `assignee`, and the timestamps
    (`createdAt`, `claimedAt`, `completedAt`) SHALL round-trip with it — including through
    `FromSnapshot` rehydration, like `artifacts` (Requirement 9.5) — so after a restart a
    `Blocked` row keeps refusing claims, the agent that claimed a row can still complete it,
    and a live lease still refuses foreign claims until it goes stale by Requirement 8.3's
    threshold. Rows hydrated from snapshots persisted before these fields existed SHALL be
    normalized to states the guards can honestly enforce: a `Blocked` row with no restorable
    blockers becomes `NotStarted` (rather than rendering a block nothing enforces); an
    `InProgress` row with an `assignee` but no restorable `claimedAt` has its lease aged
    from the snapshot's `CapturedAtUtc` (so it can still go stale rather than reading as
    freshly claimed forever); and an `InProgress` row with no `assignee` at all self-heals
    on the next claim. Restoring these fields is hydration, not a transition — it SHALL NOT
    fire the change hook

### Requirement 9: File Artifacts (attach-artifact)

- **User Story**: As an agent producing files while working a task, I need to attach them to
  the task so that the board can surface them as chips and any other agent picking up or
  reviewing the task can read the substance instead of asking for it.

#### Acceptance Criteria:
1. **Attach Function**: WHEN "attach-artifact" is called with a `taskId` and a `path` THEN
   the path SHALL be recorded on that task's `artifacts` list and rendered by "list-tasks"
   (Requirement 5.5) and "get-task"
2. **Workspace-Relative Only**: WHEN validating the path THEN it SHALL be accepted only as a
   workspace-relative, forward-slash path — a NUL byte, any backslash (covering Windows
   drive/UNC/device roots and mixed-separator traversal), a POSIX-absolute path, a
   drive-letter prefix, or any `..` segment SHALL be refused with `invalid_artifact_path`.
   A host path must never reach the stored list or the wire: it silently breaks after a
   restart, and the workspace-relative form is exactly what the file-browser preview
   endpoint accepts
3. **Normalization**: WHEN a path passes validation THEN it SHALL be stored normalized —
   empty and `.` segments dropped, segments joined with `/` — and a path that normalizes to
   the workspace root (not a file) SHALL be refused
4. **Idempotence**: WHEN the same (normalized) path is attached to the same task twice THEN
   the second call SHALL succeed without duplicating the entry, and its result text SHALL
   say the artifact was already attached
5. **Persistence**: WHEN a task tree or board snapshot is serialized THEN `artifacts` SHALL
   round-trip with it — including through `FromSnapshot` rehydration, a discipline the
   coordination fields joined in Requirement 8.10 — and a tree persisted before this field
   existed SHALL load with it empty

### Requirement 10: Nudges — The Board Talks Back (#583 PR 6)

- **User Story**: As an orchestrator, I want the board to notify an assignee when work is
  dispatched to it, and to nudge an agent whose claimed work shows no board progress — with a
  hard budget so the system can never become a perpetual motion machine of agents poking each
  other.

#### Acceptance Criteria:
1. **Assignment Notice (N1)**: WHEN a task's assignee CHANGES to a new value outside a claim
   THEN the new assignee's conversation SHALL receive a `todo-nudge` notification naming the
   task. It SHALL NOT fire for a claim (the agent acted), for a new row born with an
   assignee (sub-items inherit their creator's assignment), for an unrelated board change
   while an assignment merely persists, or for assignments hydrated from a persisted
   snapshot on recreate/restart (hydration is baseline, not a transition). N1 is on by
   default and is NOT budgeted — it is keyed to an explicit `assign-task` call and cannot
   loop on its own
2. **Stalled-Agent Nudges (N2–N4)**: run-end (N2), idle-turns (N3), and breakdown (N4)
   nudges SHALL each default OFF and SHALL share one budget: at most 2 nudges per agent per
   idle period; the second nudge escalates (asks for a reason and says it is the last); at
   the budget the agent is marked stalled (surfaced via `StalledAgents`) and nothing further
   is delivered
3. **Budget Reset Contract**: the budget SHALL reset ONLY on a REAL board change — a change
   to what the board says (rows, statuses, titles, notes, assignees, blockedBy,
   artifacts). Time
   passing, a claim-refresh heartbeat (which touches only `claimedAt`), and the agent merely
   replying SHALL NOT reset it
4. **Never-Nudge Conditions**: no stall nudge SHALL fire when the tier is disabled, when the
   run ended errored or cancelled, when everything the agent owns is terminal, or when
   everything left is `Blocked` (a correct stop, not a stall)
5. **Root-Conversation Opt-In**: a nudge whose target resolves to the ROOT conversation
   SHALL be dropped unless `TodoNudges:NudgeRootConversation` is explicitly true
6. **Tolerant Configuration**: a missing, empty, or malformed `TodoNudges` section SHALL
   read as the defaults (feature-off for N2–N4) and SHALL never throw
7. **Delivery Channel**: nudges SHALL be injected as `NotifyMessage`s of kind `todo-nudge`
   through the target conversation's normal input channel, so the client renders them as
   notification pills, never as fabricated user messages; the service's own board-change
   bookkeeping SHALL never emit frames or messages by itself
8. **Multicast Change Hook**: `TaskManager.OnChanged` SHALL be a real multicast event —
   subscribing the nudge bookkeeping SHALL NOT displace the live-frame publisher or the
   durable writer, and one subscriber throwing SHALL NOT starve the subscribers behind it
