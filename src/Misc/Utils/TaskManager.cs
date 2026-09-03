using System.Collections.Immutable;
using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Models;
using Microsoft.Extensions.Logging;

namespace AchieveAi.LmDotnetTools.Misc.Utils;

/// <summary>
///     Adaptive task management system designed for learning-based problem solving.
///     Core Philosophy:
///     ================
///     Tasks are not a rigid plan but a living hypothesis that evolves with understanding.
///     The ability to modify, add, and remove tasks based on learnings is a feature, not a bug.
///     Key Principles:
///     1. **Cognitive Load Management**: Keep 4-7 tasks at any level to maintain focus
///     2. **Learning Capture**: Notes preserve insights for future tasks
///     3. **Adaptive Planning**: 30-50% plan modification is normal and healthy
///     4. **Hierarchical Breakdown**: Deep nesting for complex problems
///     5. **Continuous Evolution**: Tasks change as understanding deepens
///     Workflow:
///     1. Start with bulk-initialize for known structure
///     2. Add tasks as complexity is discovered
///     3. Capture learnings in notes immediately
///     4. Delete obsolete tasks without hesitation
///     5. Use list-tasks to maintain awareness
///     Success Metrics:
///     - Regular task additions (shows learning)
///     - Frequent note updates (knowledge capture)
///     - Steady completion rate (momentum)
///     - Task deletions (adaptation)
///     - Balanced tree (4-7 siblings per level)
/// </summary>
public class TaskManager : ITodoBoardSource
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum TaskStatus
    {
        NotStarted,
        InProgress,
        Blocked,
        Completed,
        Removed,
    }

    /// <summary>
    ///     How reachable the agent behind an assignee name is, as reported by
    ///     <see cref="AssigneeResolver" />.
    /// </summary>
    public enum AssigneeLiveness
    {
        /// <summary>The name resolved to an agent that is currently addressable.</summary>
        Live,

        /// <summary>The name resolved to a known agent that can no longer be reached.</summary>
        Unreachable,

        /// <summary>The name resolved to no agent at all under the caller's root conversation.</summary>
        Unknown,
    }

    /// <summary>
    ///     What the host's identity layer knows about one assignee name.
    /// </summary>
    /// <param name="AgentId">The canonical agent identifier, or null when nothing resolved.</param>
    /// <param name="CanonicalName">
    ///     The text to store as <see cref="TaskItem.Assignee" />, so the ordinal comparisons that decide
    ///     ownership compare one stable identity rather than whatever the caller happened to type.
    /// </param>
    /// <param name="Liveness">Reachability of the resolved agent.</param>
    /// <param name="Candidates">
    ///     The agents a name matched when it matched more than one. More than one entry means the name
    ///     cannot decide ownership; the board refuses rather than guessing which agent was meant.
    /// </param>
    /// <remarks>
    ///     Deliberately carries no failure-code string: the codes below are this board's contract and
    ///     live with the code that emits them, so the resolver reports facts and <c>TaskManager</c>
    ///     alone maps them to <c>assignee_ambiguous</c> / <c>assignee_unknown</c>.
    /// </remarks>
    public readonly record struct AssigneeResolution(
        string? AgentId,
        string? CanonicalName,
        AssigneeLiveness Liveness,
        IReadOnlyList<string>? Candidates = null
    );

    /// <summary>
    ///     Stable, machine-readable reasons a tool call failed. They are part of these tools'
    ///     contract — a host may count or branch on them — so they follow the lower_snake_case
    ///     convention every other error code in this repository uses.
    /// </summary>
    private const string InvalidArgumentsCode = "invalid_args";
    private const string TaskNotFoundCode = "task_not_found";
    private const string InvalidTaskIdCode = "invalid_task_id";
    private const string InvalidStatusCode = "invalid_status";
    private const string NoteIndexOutOfRangeCode = "note_index_out_of_range";
    private const string InvalidActionCode = "invalid_action";
    private const string TaskNotClaimableCode = "task_not_claimable";
    private const string TaskBlockedCode = "task_blocked";
    private const string TaskAlreadyClaimedCode = "task_already_claimed";
    private const string TaskNotClaimedCode = "task_not_claimed";
    private const string InvalidArtifactPathCode = "invalid_artifact_path";
    private const string BlockCycleCode = "block_cycle";
    private const string TaskHasIncompleteDescendantsCode = "task_has_incomplete_descendants";
    private const string AssigneeAmbiguousCode = "assignee_ambiguous";
    private const string AssigneeUnknownCode = "assignee_unknown";

    /// <summary>
    ///     One nesting level of indentation in <c>bulk-initialize</c>'s result echo. Two spaces per
    ///     level, matching the markdown renderer's <c>level * 2</c> step, so the same tree reads the
    ///     same way in the echo and in <c>list-tasks</c>.
    /// </summary>
    private const string BulkEchoIndent = "  ";

    /// <summary>
    ///     How many rows <c>bulk-initialize</c> echoes before it elides the tail. A large tree can
    ///     mint hundreds of rows and echoing every one back verbatim would spend more context than
    ///     the structural signal is worth; the elision line states its own size, because a cap the
    ///     model cannot see reads to it as a complete list.
    /// </summary>
    private const int MaxBulkEchoRows = 50;

    /// <summary>
    ///     A claim is a lease, not a hard lock (see the design doc's stale-row research): an
    ///     agent that goes quiet without releasing its claim would otherwise wedge the task
    ///     forever. Past this much time since <c>ClaimedAt</c>, a different agent's claim
    ///     attempt is allowed to take the lease over rather than being refused. There is no
    ///     background sweeper — staleness is derived on read, exactly when someone asks.
    /// </summary>
    private static readonly TimeSpan DefaultLeaseStaleAfter = TimeSpan.FromMinutes(15);

    /// <summary>
    ///     Event id for the board-loss warning (#621 Part B). Declared here rather than in a project-wide
    ///     <c>LogEventIds</c> class because <c>Misc</c> has none and one event does not justify minting a
    ///     numbering scheme; the NAME is what a log query greps for, and it is pinned here.
    /// </summary>
    private static readonly EventId TodoBoardIdVanishedEvent = new(6210, "TodoBoardIdVanished");

    private readonly ManagerState _state;
    private readonly object _sync = new();
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _leaseStaleAfter;

    // Thread-safe collections
    public TaskManager()
        : this(new ManagerState(), TimeProvider.System, DefaultLeaseStaleAfter) { }

    public TaskManager(TimeProvider timeProvider)
        : this(new ManagerState(), timeProvider, DefaultLeaseStaleAfter) { }

    public TaskManager(TimeProvider timeProvider, TimeSpan leaseStaleAfter)
        : this(new ManagerState(), timeProvider, leaseStaleAfter) { }

    private TaskManager(ManagerState state, TimeProvider timeProvider, TimeSpan leaseStaleAfter)
    {
        _state = state;
        _timeProvider = timeProvider;
        _leaseStaleAfter = leaseStaleAfter;
    }

    /// <summary>
    ///     Best-effort change hook (#583, PR 2): invoked once per SUCCESSFUL mutating tool call — add,
    ///     bulk-initialize, status update, claim, assign, block, delete, and every note mutation — after
    ///     the internal lock is released. The owning host wires it to publish the live
    ///     <c>conversation_todo</c> frame, exactly as the usage ledger's aggregate-changed callback feeds
    ///     the usage banner.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         One invocation per tool CALL, not per row it touched: a <c>bulk-initialize</c> of 30 tasks is
    ///         one call and therefore one invocation — which is the whole coalescing story, with no timer,
    ///         matching how the usage frame publishes per aggregate change rather than per token.
    ///     </para>
    ///     <para>
    ///         Failed calls (non-null error code) do not fire: the board did not change, and a frame for a
    ///         refusal would repaint the client with a state it already has. Read-only tools never fire.
    ///     </para>
    ///     <para>
    ///         A multicast EVENT, not a settable delegate slot (#583 PR 6, follow-up F-007 from #590): the
    ///         push/persist wiring and the nudge accounting both subscribe, and a settable property let a
    ///         second subscriber silently clobber the first. Subscribers are invoked in subscription order,
    ///         each isolated in its own catch — one subscriber throwing must not starve the others (the
    ///         durable write must survive a broken nudge hook, and vice versa).
    ///     </para>
    /// </remarks>
    public event Action? OnChanged;

    /// <summary>
    ///     Optional logger for this board's own diagnostics: the change hook's last-resort catch, and the
    ///     <c>TodoBoardIdVanished</c> warning. Wired by the same host that wires <see cref="OnChanged" />;
    ///     when null, a throwing subscriber is swallowed silently — which is why subscribers own their own
    ///     guarding and logging first — and the vanish detector is inert.
    /// </summary>
    /// <remarks>
    ///     A settable property rather than a constructor parameter because this type is reached through
    ///     four construction routes (two constructors plus <see cref="FromSnapshot(TodoBoardSnapshot)" />
    ///     and <see cref="DeserializeTasks(string)" />, each with overloads) and the host wires the hook
    ///     after construction anyway.
    /// </remarks>
    public ILogger? Logger { get; set; }

    /// <summary>
    ///     The conversation this board belongs to, stamped by the host right after construction so the
    ///     <c>TodoBoardIdVanished</c> warning can name the thread that lost a row (#621 Part B).
    /// </summary>
    /// <remarks>
    ///     The tool methods are the model-facing surface and cannot carry a host-supplied argument, and
    ///     <see cref="GetTodoBoardSnapshot" /> takes its thread id at call time precisely because the read
    ///     path has one in hand. A detector firing from inside a tool call has neither, so the id has to
    ///     live on the instance. Null at the throwaway construction sites that only enumerate function
    ///     contracts; those wire no logger either, so nothing is logged there regardless.
    /// </remarks>
    public string? ThreadId { get; set; }

    /// <summary>
    ///     Turns an assignee name into a stable agent identity, or reports that it cannot. Null — the
    ///     default — leaves ownership decided by the raw text exactly as before.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         A settable property wired by the host after construction, for the same reason
    ///         <see cref="Logger" /> is: this type is reached through four construction routes and the
    ///         board must keep working with no collaboration layer at all. It is a delegate rather than
    ///         a direct call because <c>Misc</c> is a published package that references only
    ///         LmConfig/LmCore/OpenAIProvider — the agent directory lives in LmMultiTurn, so the
    ///         dependency has to point the other way.
    ///     </para>
    ///     <para>
    ///         The host is responsible for scoping the lookup to the conversation this board belongs to.
    ///         Agent identifiers are ordinals (<c>agent-1</c>, <c>agent-2</c>, …) numbered per root
    ///         conversation, so the same name means different agents in different conversations.
    ///     </para>
    /// </remarks>
    public Func<string, AssigneeResolution>? AssigneeResolver { get; set; }

    /// <summary>
    ///     Fires <see cref="OnChanged" /> when <paramref name="result" /> reports success, passing the
    ///     result through unchanged. Each subscriber is invoked separately and a throwing subscriber is
    ///     swallowed per subscriber: the mutation already succeeded, and a failed UI push must not convert
    ///     a successful tool call into a tool error — nor may it starve the subscribers behind it (a plain
    ///     multicast invoke would abort the rest of the invocation list on the first throw). Swallowed is
    ///     not silent — a downed subscriber can take the live push or the durable write with it, so each
    ///     escape is logged loudly when a logger is wired.
    /// </summary>
    private FunctionResult NotifyIfChanged(FunctionResult result)
    {
        if (result.ErrorCode is null && OnChanged is { } subscribers)
        {
            foreach (var subscriber in subscribers.GetInvocationList())
            {
                try
                {
                    ((Action)subscriber)();
                }
                catch (Exception ex)
                {
                    Logger?.LogError(
                        ex,
                        "An OnChanged subscriber threw; that subscriber's work (live todo-board push, durable save, or nudge accounting) was skipped for this mutation"
                    );
                }
            }
        }

        return result;
    }

    [Function(
        "add-task",
        @"Add tasks dynamically as understanding evolves - adapt your plan based on learnings.

Task breakdown philosophy:
• Keep 4-7 tasks at each level (cognitive load management)
• If more than 7 siblings, consider grouping or abstracting
• Break down tasks when they're too complex to execute directly
• Add tasks as you discover new requirements or dependencies
• It's GOOD to modify the plan - it shows learning and adaptation
• Been assigned a task that is more than one sitting? Break it into sub-items under it
  with parentId — that is what sub-items are for, and it is how the board shows the
  assignee is making progress rather than sitting on one opaque row.

Hierarchy guidelines:
• Level 1: Major phases or components
• Level 2: Concrete deliverables or milestones
• Level 3+: Specific implementation steps
• Deeper nesting for complex subtasks that need isolation
• Nesting is unlimited depth — each level adds one dotted id segment ('1.2.3.4', ...)
• Ids are 1-based: the first main task is '1', its first subtask '1.1'

Assignment inheritance:
• A sub-item added under an assigned task inherits that task's assignee automatically.
• Pass assignee to override the inherited value for that sub-item alone.

Examples:
- Main phase: {""title"": ""Design API""}
- Breakdown: {""title"": ""Define endpoints"", ""parentId"": ""1""}
- Discovered task: {""title"": ""Add rate limiting"", ""parentId"": ""1""}  // Added after learning
- Deep detail: {""title"": ""Validate JWT tokens"", ""parentId"": ""1.2.3""}
- Override inherited assignee: {""title"": ""Review"", ""parentId"": ""1"", ""assignee"": ""rev-a""}"
    )]
    public FunctionResult AddTask(
        [Description("Task title/description")] string title,
        [Description(
            "Parent task ID for nesting (e.g., '1', '1.2', '1.2.3'). Ids are 1-based: the first main task is '1', its first subtask '1.1'. Omit for main task"
        )]
            string? parentId = null,
        [Description(
            "Agent name to assign this task to. Omit on a subtask to inherit the parent task's assignee (if any); omit on a main task to leave it unassigned."
        )]
            string? assignee = null
    )
    {
        return NotifyIfChanged(AddTaskCore(title, parentId, assignee));
    }

    private FunctionResult AddTaskCore(string title, string? parentId, string? assignee)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return FunctionResult.Error(InvalidArgumentsCode, "Error: Title cannot be empty.");
        }

        // An omitted parentId means "make this a main task". A supplied-but-blank one is a
        // malformed call, and silently promoting it to a root task hides the mistake behind
        // a success message.
        if (parentId != null && string.IsNullOrWhiteSpace(parentId))
        {
            return FunctionResult.Error(
                InvalidArgumentsCode,
                "Error: Parent task ID cannot be blank. Omit parentId to add a main task."
            );
        }

        lock (_sync)
        {
            PrivateTaskItem task;
            var now = _timeProvider.GetUtcNow();

            // Adding a root task
            if (parentId == null)
            {
                var taskId = _state.NextId++;
                task = new PrivateTaskItem
                {
                    Id = taskId,
                    DisplayId = taskId.ToString(),
                    Title = title.Trim(),
                    Status = TaskStatus.NotStarted,
                    Assignee = assignee,
                    CreatedAt = now,
                };

                _state.RootTasks.Add(task);
                RecordSeen(task, now);
                return $"Added task {task.DisplayId}: {task.Title}";
            }

            // Parse parent ID and find parent task
            var (parentTask, error) = FindTaskByStringId(parentId);
            if (parentTask == null)
            {
                return error ?? FunctionResult.Error(TaskNotFoundCode, $"Error: Parent task '{parentId}' not found.");
            }

            // Create subtask with hierarchical ID. Assignee inherits from the parent unless
            // explicitly overridden — the mechanism for "lead assigns, assignee breaks it down".
            var subtaskId = parentTask.NextSubTaskId++;
            task = new PrivateTaskItem
            {
                Id = subtaskId,
                DisplayId = $"{parentTask.DisplayId}.{subtaskId}",
                Title = title.Trim(),
                Status = TaskStatus.NotStarted,
                ParentId = parentTask.Id,
                Assignee = assignee ?? parentTask.Assignee,
                CreatedAt = now,
            };

            parentTask.SubTasks.Add(task);
            RecordSeen(task, now);
            return $"Added task {task.DisplayId}: {task.Title}";
        }
    }

    public FunctionResult AddTask(string title, int parentId)
    {
        return AddTask(title, parentId.ToString());
    }

    [Function(
        "bulk-initialize",
        @"Efficiently set up initial task structure - then adapt it as you learn.

This is your starting point - use for:
• Initial problem decomposition based on requirements
• Setting up known phases/milestones at project start
• Importing task structures from templates or previous projects
• Rapid setup when you understand the problem space

Philosophy:
• Start with your best understanding, then evolve
• Initial structure is a hypothesis - expect to modify it
• Better to start with fewer, broader tasks and decompose as needed
• Use clearExisting=true for fresh starts, false to extend

Structure and ids:
• Creates ONE nesting level only: each task plus a flat list of subtask titles.
  For deeper trees, follow up with add-task and parentId — nesting there is
  unlimited depth.
• Ids are assigned 1-based in order: the first task becomes '1', its first
  subtask '1.1'.

After initialization:
• Use add-task to expand as you discover complexity
• Use delete-task to remove tasks that become irrelevant
• Use notes to capture WHY the plan changed
• Expect 30-50% modification from initial plan - this is healthy!

Examples:
- Project start: {""tasks"": [{""task"": ""Research"", ""subTasks"": [""Review docs"", ""Analyze codebase""], ""notes"": [""2-day timebox""]}], ""clearExisting"": true}
- Add phase: {""tasks"": [{""task"": ""Testing"", ""subTasks"": [""Unit tests"", ""Integration tests""]}], ""clearExisting"": false}"
    )]
    public FunctionResult BulkInitialize(
        [Description("List of tasks with their subtasks and notes")] List<BulkTaskItem> tasks,
        [Description("Clear all existing tasks before adding new ones")] bool clearExisting = false
    )
    {
        return NotifyIfChanged(BulkInitializeCore(tasks, clearExisting));
    }

    private FunctionResult BulkInitializeCore(List<BulkTaskItem> tasks, bool clearExisting)
    {
        if (tasks == null || tasks.Count == 0)
        {
            return FunctionResult.Error(InvalidArgumentsCode, "Error: No tasks provided for initialization.");
        }

        lock (_sync)
        {
            // Clear existing tasks if requested
            if (clearExisting)
            {
                _state.RootTasks.Clear();
                _state.NextId = 1;

                // A requested board reset is a deliberate removal of everything on it, and it renumbers
                // from 1 besides: keeping the old ids would report every re-used id as vanished.
                _state.SeenIds.Clear();
            }

            // Every row this call mints, rendered the way add-task renders its own mint —
            // "Task <dotted id>: <title>" — and indented two spaces per nesting level so the shape
            // is readable as well as parseable. Echoing only the main tasks made two calls that
            // differ ONLY in how many children they hung under a task return byte-identical text,
            // which is no signal at all to a model checking the structure it just built (#634 R1).
            var addedLines = new List<string>();
            var mainTaskCount = 0;
            var subTaskCount = 0;
            var errors = new List<string>();
            var now = _timeProvider.GetUtcNow();

            foreach (var bulkItem in tasks)
            {
                if (string.IsNullOrWhiteSpace(bulkItem.Task))
                {
                    // Silent skip for empty tasks (as per requirements for LLM inputs)
                    continue;
                }

                // Add main task
                var mainTaskId = _state.NextId++;
                var mainTask = new PrivateTaskItem
                {
                    Id = mainTaskId,
                    DisplayId = mainTaskId.ToString(),
                    Title = bulkItem.Task.Trim(),
                    Status = TaskStatus.NotStarted,
                    CreatedAt = now,
                };

                _state.RootTasks.Add(mainTask);
                mainTaskCount++;
                addedLines.Add($"{BulkEchoIndent}- Task {mainTask.DisplayId}: {mainTask.Title}");

                // Add notes to main task
                if (bulkItem.Notes != null)
                {
                    foreach (var note in bulkItem.Notes)
                    {
                        if (!string.IsNullOrWhiteSpace(note))
                        {
                            mainTask.Notes.Add(note.Trim());
                        }
                    }
                }

                // Add subtasks
                if (bulkItem.SubTasks != null)
                {
                    foreach (var subTaskTitle in bulkItem.SubTasks)
                    {
                        if (string.IsNullOrWhiteSpace(subTaskTitle))
                        {
                            // Silent skip for empty subtasks (as per requirements)
                            continue;
                        }

                        var subTaskId = mainTask.NextSubTaskId++;
                        var subTask = new PrivateTaskItem
                        {
                            Id = subTaskId,
                            DisplayId = $"{mainTask.DisplayId}.{subTaskId}",
                            Title = subTaskTitle.Trim(),
                            Status = TaskStatus.NotStarted,
                            ParentId = mainTask.Id,
                            Assignee = mainTask.Assignee,
                            CreatedAt = now,
                        };

                        mainTask.SubTasks.Add(subTask);
                        subTaskCount++;
                        addedLines.Add($"{BulkEchoIndent}{BulkEchoIndent}- Task {subTask.DisplayId}: {subTask.Title}");
                    }
                }

                // After the subtasks, so one walk records the whole row it just minted.
                RecordSeen(mainTask, now, includeSubtree: true);
            }

            var result = new StringBuilder();

            if (clearExisting)
            {
                AppendLine(result, "Cleared existing tasks.");
            }

            if (addedLines.Count > 0)
            {
                // The subtask count is stated only when there is one, so a flat initialization keeps
                // the shorter header it always had; when there IS nesting the header carries the
                // totals independently of the listing, which is what survives a truncated tail.
                AppendLine(
                    result,
                    subTaskCount > 0
                        ? $"Added {mainTaskCount} task(s) and {subTaskCount} subtask(s):"
                        : $"Added {mainTaskCount} task(s):"
                );

                foreach (var line in addedLines.Take(MaxBulkEchoRows))
                {
                    AppendLine(result, line);
                }

                // A silent cap reads to the model as "that was everything", so the elision names its
                // own size and points at the tool that shows the rest.
                if (addedLines.Count > MaxBulkEchoRows)
                {
                    AppendLine(
                        result,
                        $"{BulkEchoIndent}... {addedLines.Count - MaxBulkEchoRows} more row(s) not listed "
                            + $"({MaxBulkEchoRows} of {addedLines.Count} shown). Use list-tasks to see them all."
                    );
                }
            }

            if (errors.Count > 0)
            {
                AppendLine(result, "Errors:");
                foreach (var error in errors)
                {
                    AppendLine(result, $"  - {error}");
                }
            }

            return result.ToString().TrimEnd();
        }
    }

    [Function(
        "update-task",
        @"Mark progress to maintain momentum and focus on active work.

Status progression philosophy:
• 'not started' → 'in progress': Commitment to focus. Pass your agent name to claim it —
  this is the same claim claim-task performs, just inline.
• 'in progress' → 'completed': Achievement and learning opportunity. A task must be
  claimed (in progress, with an assignee) before it can be completed — mark complete
  the moment you finish, do not batch it up.
• Any → 'removed': Conscious decision to pivot

Claim discipline:
• One 'in progress' task per assignee. Claiming a second one — via claim-task or by
  passing agent here — releases the first back to 'not started' and the tool result
  says so; you are told, not silently corrected.
• A claim is a lease, not a lock: an untouched claim goes stale after a while and
  another agent's claim attempt can take it over.
• To block or unblock a task, use block-task — this tool refuses a direct 'blocked'
  status so blockedBy always stays in sync with the status.

Before marking complete:
• Add notes about what was learned
• Every descendant must already be 'completed' or 'removed' — this is enforced, not just
  advisory. A descendant left 'not started', 'in progress', or 'blocked' refuses the
  parent's completion; finish or remove it first.
• Consider if follow-up tasks are needed

Status meanings:
• not started: Planned but not begun (the backlog)
• in progress: Actively working — claimed, one per assignee
• completed: Done and learned from (celebrate!)
• removed: No longer needed (adapted plan)

Examples:
- Claim and start: {""taskId"": ""1"", ""status"": ""in progress"", ""agent"": ""rev-a""}
- Finish task: {""taskId"": ""1.3"", ""status"": ""completed""}
- Abandon approach: {""taskId"": ""2.1"", ""status"": ""removed""}"
    )]
    public FunctionResult UpdateTask(
        [Description(
            "Task ID (e.g., '1', '1.2', '1.2.3'). Ids are 1-based: the first main task is '1', its first subtask '1.1'."
        )]
            string taskId,
        [Description("New status: not started|in progress|completed|removed")] string status = "not started",
        [Description(
            "Your agent name. Passing it on an 'in progress' transition claims the task (see claim-task); leave it null to just flip status without touching the claim."
        )]
            string? agent = null
    )
    {
        return NotifyIfChanged(UpdateTaskCore(taskId, status, agent));
    }

    private FunctionResult UpdateTaskCore(string taskId, string status, string? agent)
    {
        lock (_sync)
        {
            // Find target task using string ID
            var (targetTask, error) = FindTaskByStringId(taskId);
            if (targetTask == null)
            {
                return error!.Value;
            }

            // Update status
            if (!TryParseStatus(status, out var newStatus))
            {
                return FunctionResult.Error(
                    InvalidStatusCode,
                    "Error: Invalid status. Use: not started, in progress, completed, removed."
                );
            }

            if (newStatus == TaskStatus.Blocked)
            {
                return FunctionResult.Error(
                    InvalidStatusCode,
                    "Error: Use block-task to set or clear blockedBy. update-task cannot set 'blocked' directly."
                );
            }

            var now = _timeProvider.GetUtcNow();

            if (newStatus == TaskStatus.InProgress && agent != null)
            {
                var claimError = ApplyClaim(targetTask, agent, now, out var claimNote);
                if (claimError != null)
                {
                    return claimError.Value;
                }

                return $"Updated task {targetTask.DisplayId} status to 'in progress'.{claimNote}";
            }

            if (newStatus == TaskStatus.InProgress && RefuseIfBlocked(targetTask) is { } inProgressBlockedError)
            {
                // Legacy status-only transition (agent == null, no lease created) must not
                // bypass the same blockedBy guard ApplyClaim enforces below, or Status and
                // BlockedBy end up disagreeing (Requirement 8.5).
                return inProgressBlockedError;
            }

            if (newStatus == TaskStatus.Completed)
            {
                if (targetTask.Status != TaskStatus.InProgress || targetTask.Assignee == null)
                {
                    return FunctionResult.Error(
                        TaskNotClaimedCode,
                        $"Error: Task {targetTask.DisplayId} must be claimed and in progress before it can be completed. "
                            + "Sequence: claim-task with your agent name, then update-task to 'in progress' "
                            + "(claim-task already sets it), then 'completed'."
                    );
                }

                if (GetIncompleteDescendants(targetTask) is { Count: > 0 } incompleteDescendants)
                {
                    return FunctionResult.Error(
                        TaskHasIncompleteDescendantsCode,
                        $"Error: Task {targetTask.DisplayId} cannot be completed while descendant(s) "
                            + $"{string.Join(", ", incompleteDescendants)} are not started, in progress, or "
                            + "blocked. Complete or remove them first."
                    );
                }

                targetTask.Status = TaskStatus.Completed;
                targetTask.CompletedAt = now;
                var unblocked = AutoUnblockDependentsOf(targetTask.DisplayId);
                var unblockedSuffix =
                    unblocked.Count > 0 ? $" Unblocked: {string.Join(", ", unblocked)}." : string.Empty;

                return $"Updated task {targetTask.DisplayId} status to 'completed'.{unblockedSuffix}";
            }

            // NotStarted or Removed via the plain path, and InProgress with no agent (legacy,
            // status-only transition that leaves the claim fields untouched).
            targetTask.Status = newStatus;
            if (newStatus != TaskStatus.InProgress)
            {
                // The lease is only meaningful while the task is actively in progress.
                targetTask.ClaimedAt = null;
            }

            return $"Updated task {targetTask.DisplayId} status to '{NormalizeStatusText(newStatus)}'.";
        }
    }

    public FunctionResult UpdateTask(int taskId, string status = "not started", string? agent = null)
    {
        return UpdateTask(taskId.ToString(), status, agent);
    }

    public FunctionResult UpdateTask(int taskId, int subtaskId, string status = "not started", string? agent = null)
    {
        return UpdateTask($"{taskId}.{subtaskId}", status, agent);
    }

    [Function(
        "claim-task",
        @"Claim a task by name before you start working on it — the explicit
NotStarted -> InProgress(by name) step the board uses to know who owns what right now.

Why claim, not just update-task:
• Records your identity as the active holder, and stamps when the claim began.
• Enforced one-in-progress-per-assignee: claiming a second task releases your first
  one back to 'not started' — you will see this in the result, it is not silent.
• Refuses tasks with an unresolved blockedBy — resolve the blocker first.
• A claim is a lease. If it goes stale (untouched too long) another agent's claim
  attempt takes it over instead of being refused forever — the result says whose
  stale lease was taken.

Claiming your own already-claimed task just refreshes the lease (use this like a
heartbeat on long work instead of letting it look abandoned).

Examples:
- First claim: {""taskId"": ""3"", ""agent"": ""rev-a""}
- Re-claim / heartbeat: {""taskId"": ""3"", ""agent"": ""rev-a""}  // same agent, refreshes ClaimedAt"
    )]
    public FunctionResult ClaimTask(
        [Description(
            "Task ID (e.g., '1', '1.2', '1.2.3'). Ids are 1-based: the first main task is '1', its first subtask '1.1'."
        )]
            string taskId,
        [Description("Your agent name — recorded as the claim holder")] string agent
    )
    {
        // The claim-refresh path mutates only the lease timestamp — board-visible since #595 put
        // claimedAt on TodoTaskNode, so the hook firing here keeps the persisted lease age (and
        // any staleness rendering built on it) current rather than frozen at the original claim.
        return NotifyIfChanged(ClaimTaskCore(taskId, agent));
    }

    private FunctionResult ClaimTaskCore(string taskId, string agent)
    {
        if (string.IsNullOrWhiteSpace(agent))
        {
            return FunctionResult.Error(InvalidArgumentsCode, "Error: agent cannot be empty.");
        }

        lock (_sync)
        {
            var (task, error) = FindTaskByStringId(taskId);
            if (task == null)
            {
                return error!.Value;
            }

            var now = _timeProvider.GetUtcNow();
            var trimmedAgent = agent.Trim();

            if (
                task.Status == TaskStatus.InProgress
                && string.Equals(task.Assignee, trimmedAgent, StringComparison.Ordinal)
            )
            {
                if (RefuseIfBlocked(task) is { } refreshBlockedError)
                {
                    return refreshBlockedError;
                }

                task.ClaimedAt = now;
                return $"Task {task.DisplayId} claim refreshed by {trimmedAgent}.";
            }

            var claimError = ApplyClaim(task, trimmedAgent, now, out var note);
            if (claimError != null)
            {
                return claimError.Value;
            }

            return $"Task {task.DisplayId} claimed by {trimmedAgent}.{note}";
        }
    }

    [Function(
        "assign-task",
        @"Dispatch a task to another agent by name — the lead's half of assign-then-claim.

Assignment records whose work this is; it does not create a live claim on its own:
• On a task that is not InProgress, this only sets the assignee. The assignee should
  claim-task it when they start (claiming stamps the lease and enforces
  one-in-progress-per-assignee), and break it into sub-items with add-task if it is
  more than one sitting — sub-items created under an assigned task inherit the
  assignee automatically.
• On a task InProgress under someone else, a live (non-stale) lease is refused —
  assignment can queue work, but an active lease belongs to claim-task. Wait for it
  to finish, or for the lease to go stale.
• If that lease has gone stale, the task is handed back to 'not started' for the new
  assignee to claim explicitly — assignment never advances a task into InProgress, so
  it can never leave an assignee holding two active tasks.

Examples:
- Dispatch: {""taskId"": ""3"", ""assignee"": ""rev-a""}
- Reassign after a stale lease: {""taskId"": ""3"", ""assignee"": ""rev-b""}"
    )]
    public FunctionResult AssignTask(
        [Description(
            "Task ID (e.g., '1', '1.2', '1.2.3'). Ids are 1-based: the first main task is '1', its first subtask '1.1'."
        )]
            string taskId,
        [Description("Agent name to assign this task to")] string assignee
    )
    {
        // Assignee is on TodoTaskNode since #595, so this fires a frame the board can actually
        // render differently — and the durable write behind the same hook is what lets the
        // assignment survive an agent recreation.
        return NotifyIfChanged(AssignTaskCore(taskId, assignee));
    }

    private FunctionResult AssignTaskCore(string taskId, string assignee)
    {
        if (string.IsNullOrWhiteSpace(assignee))
        {
            return FunctionResult.Error(InvalidArgumentsCode, "Error: assignee cannot be empty.");
        }

        lock (_sync)
        {
            var (task, error) = FindTaskByStringId(taskId);
            if (task == null)
            {
                return error!.Value;
            }

            if (task.Status is TaskStatus.Completed or TaskStatus.Removed)
            {
                return FunctionResult.Error(
                    TaskNotClaimableCode,
                    $"Error: Task {task.DisplayId} is {NormalizeStatusText(task.Status)} and cannot be (re)assigned."
                );
            }

            // Same choke point as ApplyClaim, and for the same reason: the comparison below decides
            // whether an existing lease is foreign, so both sides of it must be one stable identity.
            if (ResolveAssignee(assignee, out var trimmedAssignee) is { } resolutionError)
            {
                return resolutionError;
            }

            trimmedAssignee = trimmedAssignee.Trim();

            // Assignment is for queued work; an active lease belongs to claim-task (Requirement
            // 8.3). A task InProgress under someone else stays theirs to finish unless their
            // lease has actually gone stale — otherwise assign-task would be a silent way to
            // steal a live claim, or to hand the new assignee a task they can complete without
            // ever claiming it (Requirement 8.8). See F-002.
            if (
                task.Status == TaskStatus.InProgress
                && task.Assignee != null
                && !string.Equals(task.Assignee, trimmedAssignee, StringComparison.Ordinal)
            )
            {
                var now = _timeProvider.GetUtcNow();
                if (!IsLeaseStale(task, now, out var elapsed))
                {
                    return FunctionResult.Error(
                        TaskAlreadyClaimedCode,
                        $"Error: Task {task.DisplayId} is already claimed by {task.Assignee} ({FormatElapsed(elapsed)} ago); its lease is not yet stale. Use claim-task once it goes stale, or wait for {task.Assignee} to finish."
                    );
                }

                // The lease is stale: assignment never advances a task into InProgress
                // (Requirement 8.4 — that would risk the new assignee ending up with two active
                // tasks), so hand it back to 'not started' for them to claim explicitly.
                task.Status = TaskStatus.NotStarted;
                task.ClaimedAt = null;
            }

            task.Assignee = trimmedAssignee;
            return $"Assigned task {task.DisplayId} to {task.Assignee}. "
                + "They should claim it before starting, and break it into sub-items if it is more than one sitting.";
        }
    }

    [Function(
        "block-task",
        @"Set or clear the tasks blocking this one from proceeding — the flat blocked_by
list the board renders as 'blocked by 1, 2'.

• Passing one or more task IDs marks this task Blocked and records them as the reason.
  A blocked task cannot be claimed until every listed blocker is completed.
• Passing an empty list (or omitting blockedBy) clears the block and returns the task
  to 'not started'.
• Completing a blocking task automatically removes it from every dependent's
  blockedBy, and unblocks the dependent (back to 'not started') once none remain —
  you do not need to call this again just to clear a resolved blocker.
• Auto-unblock is one-way: re-opening a completed blocker does NOT re-block its former
  dependents. Call block-task again to re-block them.
• A completed or removed task is refused as a blocker — it can never complete (again)
  to lift the block. Re-open it first (update-task <id> 'not started') if it should
  block again.
• An edge that would close a dependency cycle (a blocks b while b blocks a, directly
  or through intermediates) is refused — no task in the cycle could ever complete to
  unblock the others.

Examples:
- Block on a dependency: {""taskId"": ""3"", ""blockedBy"": [""1""]}
- Block on several: {""taskId"": ""4"", ""blockedBy"": [""1"", ""2""]}
- Clear the block: {""taskId"": ""3"", ""blockedBy"": []}
- Re-block after a premature completion: re-open the blocker, then {""taskId"": ""3"", ""blockedBy"": [""1""]}"
    )]
    public FunctionResult BlockTask(
        [Description(
            "Task ID (e.g., '1', '1.2', '1.2.3'). Ids are 1-based: the first main task is '1', its first subtask '1.1'."
        )]
            string taskId,
        [Description(
            "Task IDs this task is blocked on (e.g., '1', '1.2', '1.2.3'). Ids are 1-based: the first main task is '1', its first subtask '1.1'. Pass an empty list to clear the block."
        )]
            List<string>? blockedBy = null
    )
    {
        return NotifyIfChanged(BlockTaskCore(taskId, blockedBy));
    }

    private FunctionResult BlockTaskCore(string taskId, List<string>? blockedBy)
    {
        lock (_sync)
        {
            var (task, error) = FindTaskByStringId(taskId);
            if (task == null)
            {
                return error!.Value;
            }

            if (task.Status is TaskStatus.Completed or TaskStatus.Removed)
            {
                return FunctionResult.Error(
                    TaskNotClaimableCode,
                    $"Error: Task {task.DisplayId} is {NormalizeStatusText(task.Status)}; blockedBy no longer applies."
                );
            }

            var ids = (blockedBy ?? [])
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id.Trim())
                .Distinct()
                .ToList();

            if (ids.Count == 0)
            {
                task.BlockedBy.Clear();
                if (task.Status == TaskStatus.Blocked)
                {
                    task.Status = TaskStatus.NotStarted;
                }

                return $"Cleared blockedBy on task {task.DisplayId}.";
            }

            // Store canonical DisplayIds, never the raw input strings: FindTaskByStringId
            // resolves "01", "+1", and " 1" all to task 1, but the self-block guard, the cycle
            // DFS, and AutoUnblockDependentsOf all compare ordinal strings — a raw non-canonical
            // id would slip every one of them (self-deadlock accepted, cycle accepted,
            // auto-unblock missed).
            var resolvedIds = new List<string>();
            foreach (var id in ids)
            {
                var (blocker, blockerError) = FindTaskByStringId(id);
                if (blocker == null)
                {
                    return blockerError
                        ?? FunctionResult.Error(TaskNotFoundCode, $"Error: Blocking task '{id}' not found.");
                }

                // A completed or removed task can never complete (again) to lift the block, so
                // listing it would mint a Blocked row whose every claim guard passes —
                // GetUnresolvedBlockers already treats a completed blocker as resolved. This is
                // also the re-block seam (#595, review 587/FU-3): after auto-unblock, re-block on
                // a still-completed blocker is refused with the recipe (re-open it first) instead
                // of silently recording a block with no force.
                if (blocker.Status is TaskStatus.Completed or TaskStatus.Removed)
                {
                    return FunctionResult.Error(
                        InvalidArgumentsCode,
                        $"Error: Task '{id}' is {NormalizeStatusText(blocker.Status)} and cannot block task {task.DisplayId} — a resolved task never completes again to lift the block. Re-open it first (update-task {id} \"not started\") if it should block again."
                    );
                }

                if (!resolvedIds.Contains(blocker.DisplayId))
                {
                    resolvedIds.Add(blocker.DisplayId);
                }
            }

            if (resolvedIds.Contains(task.DisplayId))
            {
                return FunctionResult.Error(
                    InvalidArgumentsCode,
                    $"Error: Task {task.DisplayId} cannot be listed as its own blocker."
                );
            }

            // Reject an edge that closes a cycle (#595, review 587/FU-4): every member of a
            // blockedBy cycle waits on another member, auto-unblock only fires on completion, and
            // completion requires a claim the block refuses — so the deadlock would be silent and
            // permanent, escapable only by someone noticing and clearing an edge by hand.
            if (FindBlockCyclePath(task, resolvedIds) is { } cyclePath)
            {
                return FunctionResult.Error(
                    BlockCycleCode,
                    $"Error: Blocking task {task.DisplayId} on {cyclePath[1]} would create a cycle: {string.Join(" -> ", cyclePath)} (each task waiting on the next). No task in the cycle could ever complete to unblock the others; remove one of the existing blocks instead."
                );
            }

            task.BlockedBy.Clear();
            task.BlockedBy.AddRange(resolvedIds);
            task.Status = TaskStatus.Blocked;
            // Not being actively worked while blocked; a live lease no longer means anything.
            task.ClaimedAt = null;

            return $"Task {task.DisplayId} is now blocked by {string.Join(", ", resolvedIds)}.";
        }
    }

    [Function(
        "attach-artifact",
        @"Attach a file to a task — the shared detail channel other agents read instead of asking.

Attach the file that carries the working detail for this task: the spec you were handed,
the notes you built up, the thing you produced. A task's title is a label; notes are
one-liners; the artifact is where the substance lives. Any agent picking up or reviewing
this task can open it from the board.

Paths are WORKSPACE-RELATIVE, forward-slash only:
• Good: ""docs/spec.md"", ""src/api/registry.ts""
• Refused: absolute paths (""/etc/x"", ""C:/x""), backslashes, and any "".."" segment.
  A host path would silently break after a restart — the workspace mount point moves,
  the file does not.

Attach the SAME path again and nothing changes — the call is idempotent, not an error.

Examples:
- The produced file: {""taskId"": ""2"", ""path"": ""src/renderers/registry.ts""}
- The working spec: {""taskId"": ""3"", ""path"": ""docs/todo-board/spec.md""}"
    )]
    public FunctionResult AttachArtifact(
        [Description(
            "Task ID (e.g., '1', '1.2', '1.2.3'). Ids are 1-based: the first main task is '1', its first subtask '1.1'."
        )]
            string taskId,
        [Description("Workspace-relative file path, forward slashes only (e.g. 'docs/spec.md')")] string path
    )
    {
        return NotifyIfChanged(AttachArtifactCore(taskId, path));
    }

    private FunctionResult AttachArtifactCore(string taskId, string path)
    {
        if (!TryNormalizeWorkspaceRelativePath(path, out var normalized, out var reason))
        {
            return FunctionResult.Error(InvalidArtifactPathCode, $"Error: {reason}");
        }

        lock (_sync)
        {
            var (task, error) = FindTaskByStringId(taskId);
            if (task == null)
            {
                return error!.Value;
            }

            if (task.Artifacts.Contains(normalized, StringComparer.Ordinal))
            {
                return $"Artifact already attached to task {task.DisplayId}: {normalized}";
            }

            task.Artifacts.Add(normalized);
            return $"Attached artifact to task {task.DisplayId}: {normalized}";
        }
    }

    /// <summary>
    ///     Lexically validates and normalizes an artifact path as WORKSPACE-RELATIVE, mirroring the
    ///     sandbox SDK's <c>WorkspaceRelativePath</c> rules (that type is internal to the Sandbox
    ///     assembly, which this project deliberately does not reference). The rules are purely lexical
    ///     and host-independent: reject a NUL byte, any backslash (covers Windows drive/UNC/device
    ///     roots and mixed-separator traversal), a POSIX-absolute path, a drive-letter prefix, and any
    ///     <c>..</c> segment; drop empty and <c>.</c> segments. The board must never carry a host
    ///     path — <c>HostPath</c> is re-derived per session and silently breaks across a restart,
    ///     while a workspace-relative path is exactly what the file-browser preview endpoint accepts.
    /// </summary>
    private static bool TryNormalizeWorkspaceRelativePath(string? path, out string normalized, out string reason)
    {
        normalized = string.Empty;

        if (string.IsNullOrWhiteSpace(path))
        {
            reason = "Artifact path cannot be empty.";
            return false;
        }

        if (path.Contains('\0', StringComparison.Ordinal))
        {
            reason = "Artifact path contains a NUL byte.";
            return false;
        }

        if (path.Contains('\\', StringComparison.Ordinal))
        {
            reason =
                "Artifact path must be workspace-relative with forward slashes only "
                + "(no backslashes, Windows drive/UNC roots, or host paths).";
            return false;
        }

        var trimmed = path.Trim();
        if (trimmed.StartsWith('/'))
        {
            reason = "Artifact path must be workspace-relative, not an absolute path.";
            return false;
        }

        if (trimmed.Length >= 2 && trimmed[1] == ':' && char.IsAsciiLetter(trimmed[0]))
        {
            reason = "Artifact path must be workspace-relative, not a Windows drive path.";
            return false;
        }

        var kept = new List<string>();
        foreach (var segment in trimmed.Split('/'))
        {
            if (segment.Length == 0 || string.Equals(segment, ".", StringComparison.Ordinal))
            {
                continue;
            }

            if (string.Equals(segment, "..", StringComparison.Ordinal))
            {
                reason = "Artifact path contains a '..' segment that escapes the workspace root.";
                return false;
            }

            kept.Add(segment);
        }

        if (kept.Count == 0)
        {
            reason = "Artifact path names the workspace root, not a file.";
            return false;
        }

        normalized = string.Join('/', kept);
        reason = string.Empty;
        return true;
    }

    /// <summary>
    ///     Shared claim logic for <see cref="ClaimTask" /> and <see cref="UpdateTask(string, string, string?)" />
    ///     when it is asked to move a task to <see cref="TaskStatus.InProgress" /> on behalf of a
    ///     named agent. Must be called while holding <see cref="_sync" />.
    /// </summary>
    private FunctionResult? ApplyClaim(PrivateTaskItem task, string rawAgent, DateTimeOffset now, out string note)
    {
        note = string.Empty;

        if (task.Status is TaskStatus.Completed or TaskStatus.Removed)
        {
            return FunctionResult.Error(
                TaskNotClaimableCode,
                $"Error: Task {task.DisplayId} is {NormalizeStatusText(task.Status)} and cannot be claimed."
            );
        }

        if (RefuseIfBlocked(task) is { } blockedError)
        {
            return blockedError;
        }

        // Before the ownership comparison, not after: every string compared below has to be the same
        // stable identity, or a lease held by "agent-3" reads as free to someone who typed the same
        // agent's display name.
        if (ResolveAssignee(rawAgent, out var agent) is { } resolutionError)
        {
            return resolutionError;
        }

        if (
            task.Status == TaskStatus.InProgress
            && task.Assignee != null
            && !string.Equals(task.Assignee, agent, StringComparison.Ordinal)
        )
        {
            if (!IsLeaseStale(task, now, out var elapsed))
            {
                return FunctionResult.Error(
                    TaskAlreadyClaimedCode,
                    $"Error: Task {task.DisplayId} is already claimed by {task.Assignee} ({FormatElapsed(elapsed)} ago); its lease is not yet stale."
                );
            }

            note += $" Took over a stale lease from {task.Assignee} (idle {FormatElapsed(elapsed)}).";
        }

        // One InProgress task per assignee: claiming a second releases the first, and the
        // released task keeps its assignee — it is still that agent's task, just not the
        // active one right now.
        var previous = FindOtherInProgressTaskFor(agent, task);
        if (previous != null)
        {
            previous.Status = TaskStatus.NotStarted;
            previous.ClaimedAt = null;
            note +=
                $" Released task {previous.DisplayId} back to 'not started' ({agent} can only have one active task).";
        }

        task.Status = TaskStatus.InProgress;
        task.Assignee = agent;
        task.ClaimedAt = now;
        task.CreatedAt ??= now;
        return null;
    }

    /// <summary>
    ///     Turns the assignee text a caller typed into the identity the board stores, or refuses it.
    ///     Shared by every path that writes <see cref="PrivateTaskItem.Assignee" /> — <c>claim-task</c>,
    ///     <c>assign-task</c>, and <c>update-task</c> moving a row to in-progress on an agent's behalf —
    ///     so there is one place a name can decide ownership.
    /// </summary>
    /// <remarks>
    ///     With no <see cref="AssigneeResolver" /> wired the text passes through untouched, which is the
    ///     board's behaviour with no collaboration layer at all. With one wired, a name matching more
    ///     than one agent and a name matching none are both refused: the first because the board cannot
    ///     know which agent was meant, the second because agent ids are ordinals numbered per root
    ///     conversation, so an unmatched name is as likely to be another conversation's agent as a typo.
    /// </remarks>
    /// <param name="rawAgent">The text the caller supplied.</param>
    /// <param name="canonical">The identity to store; <paramref name="rawAgent" /> when nothing resolved it.</param>
    /// <returns>The refusal, or null when <paramref name="canonical" /> may be used.</returns>
    private FunctionResult? ResolveAssignee(string rawAgent, out string canonical)
    {
        canonical = rawAgent;
        if (AssigneeResolver is not { } resolve)
        {
            return null;
        }

        var probe = rawAgent.Trim();
        var resolution = resolve(probe);

        if (resolution.Candidates is { Count: > 1 } candidates)
        {
            return FunctionResult.Error(
                AssigneeAmbiguousCode,
                $"Error: '{probe}' names {candidates.Count} agents ({string.Join(", ", candidates)}); "
                    + "the board cannot tell which one owns the work. Pass the agent id instead."
            );
        }

        if (resolution.Liveness == AssigneeLiveness.Unknown)
        {
            return FunctionResult.Error(
                AssigneeUnknownCode,
                $"Error: '{probe}' does not name an agent in this conversation. Agent ids are numbered "
                    + "per conversation, so check the id with the sub-agent listing before assigning."
            );
        }

        canonical = resolution.CanonicalName ?? resolution.AgentId ?? probe;
        return null;
    }

    private PrivateTaskItem? FindOtherInProgressTaskFor(string agent, PrivateTaskItem excluding)
    {
        return GetAllTasksFlat(_state.RootTasks)
            .FirstOrDefault(t =>
                t != excluding
                && t.Status == TaskStatus.InProgress
                && string.Equals(t.Assignee, agent, StringComparison.Ordinal)
            );
    }

    /// <summary>
    ///     Descendants of <paramref name="task" /> (task 22) whose status is neither
    ///     <see cref="TaskStatus.Completed" /> nor <see cref="TaskStatus.Removed" /> — the only two
    ///     terminal statuses. NotStarted, InProgress, and Blocked all mean "not actually finished
    ///     yet", so a parent completing over one of them would silently claim a subtree is done when
    ///     part of it is still open, still being worked, or still waiting on something else. Returns
    ///     dotted display ids so the refusal can name exactly which rows are still outstanding. Must
    ///     be called while holding <see cref="_sync" />.
    /// </summary>
    private static List<string> GetIncompleteDescendants(PrivateTaskItem task)
    {
        return
        [
            .. GetAllTasksFlat(task.SubTasks)
                .Where(t => t.Status is not (TaskStatus.Completed or TaskStatus.Removed))
                .Select(t => t.DisplayId),
        ];
    }

    /// <summary>
    ///     A blocker that no longer exists (deleted after the blockedBy reference was recorded)
    ///     is treated as resolved rather than an unliftable, permanent block.
    /// </summary>
    private List<string> GetUnresolvedBlockers(PrivateTaskItem task)
    {
        var unresolved = new List<string>();
        foreach (var id in task.BlockedBy)
        {
            var (blocker, _) = FindTaskByStringId(id);
            if (blocker != null && blocker.Status != TaskStatus.Completed)
            {
                unresolved.Add(id);
            }
        }

        return unresolved;
    }

    /// <summary>
    ///     Shared blockedBy guard for every route that can move a task into
    ///     <see cref="TaskStatus.InProgress" /> — <see cref="ApplyClaim" />, the legacy agentless
    ///     branch of <see cref="UpdateTask(string, string, string?)" />, and <see cref="ClaimTask" />'s
    ///     same-holder refresh — so <c>Status</c> and <c>BlockedBy</c> can never disagree
    ///     (Requirement 8.1/8.5). Must be called while holding <see cref="_sync" />.
    /// </summary>
    private FunctionResult? RefuseIfBlocked(PrivateTaskItem task)
    {
        if (GetUnresolvedBlockers(task) is { Count: > 0 } unresolved)
        {
            return FunctionResult.Error(
                TaskBlockedCode,
                $"Error: Task {task.DisplayId} is blocked by {string.Join(", ", unresolved)}. Resolve the blocker(s) first."
            );
        }

        return null;
    }

    /// <summary>
    ///     Whether <paramref name="task" />'s current lease is old enough for someone other than
    ///     its current assignee to take over — Requirement 8.3's "older than the staleness
    ///     threshold": a claim exactly <see cref="_leaseStaleAfter" /> old is still live, only one
    ///     that is strictly older counts as stale. Shared by <see cref="ApplyClaim" /> and
    ///     <see cref="AssignTask" /> so both routes into a live claim's territory agree on the
    ///     same boundary.
    /// </summary>
    private bool IsLeaseStale(PrivateTaskItem task, DateTimeOffset now, out TimeSpan elapsed)
    {
        var claimedAt = task.ClaimedAt ?? task.CreatedAt ?? now;
        elapsed = now - claimedAt;
        return elapsed > _leaseStaleAfter;
    }

    /// <summary>
    ///     Looks for a path through the existing blockedBy graph from any of the proposed blockers
    ///     back to <paramref name="task" /> — the path that would turn the proposed edges into a
    ///     cycle (#595, review 587/FU-4). Returns the full cycle as dotted ids, starting and ending
    ///     with <paramref name="task" />'s own id (<c>["2", "1", "2"]</c> reads "2 blocked by 1,
    ///     1 blocked by 2"), or null when no proposed edge closes one. The walk is bounded by a
    ///     visited set shared across the whole call, so a diamond-shaped graph is walked once and a
    ///     pre-existing cycle that does not pass through <paramref name="task" /> cannot loop it.
    ///     Must be called while holding <see cref="_sync" />.
    /// </summary>
    private List<string>? FindBlockCyclePath(PrivateTaskItem task, List<string> proposedBlockerIds)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var path = new List<string> { task.DisplayId };
        foreach (var blockerId in proposedBlockerIds)
        {
            if (TryFindPathBackTo(task.DisplayId, blockerId, visited, path))
            {
                return path;
            }
        }

        return null;
    }

    private bool TryFindPathBackTo(string targetId, string currentId, HashSet<string> visited, List<string> path)
    {
        path.Add(currentId);
        if (string.Equals(currentId, targetId, StringComparison.Ordinal))
        {
            return true;
        }

        if (visited.Add(currentId))
        {
            // A dangling id (blocker deleted after the edge was recorded) simply has no outgoing
            // edges here — consistent with GetUnresolvedBlockers treating it as resolved.
            var (current, _) = FindTaskByStringId(currentId);
            if (current != null)
            {
                foreach (var nextId in current.BlockedBy)
                {
                    if (TryFindPathBackTo(targetId, nextId, visited, path))
                    {
                        return true;
                    }
                }
            }
        }

        path.RemoveAt(path.Count - 1);
        return false;
    }

    private List<string> AutoUnblockDependentsOf(string completedTaskId)
    {
        var unblocked = new List<string>();
        foreach (var candidate in GetAllTasksFlat(_state.RootTasks))
        {
            if (!candidate.BlockedBy.Remove(completedTaskId))
            {
                continue;
            }

            if (candidate.BlockedBy.Count == 0 && candidate.Status == TaskStatus.Blocked)
            {
                candidate.Status = TaskStatus.NotStarted;
                unblocked.Add(candidate.DisplayId);
            }
        }

        return unblocked;
    }

    private static string FormatElapsed(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero)
        {
            elapsed = TimeSpan.Zero;
        }

        if (elapsed.TotalMinutes < 1)
        {
            return $"{(int)elapsed.TotalSeconds}s";
        }

        if (elapsed.TotalHours < 1)
        {
            return $"{(int)elapsed.TotalMinutes}m";
        }

        return $"{(int)elapsed.TotalHours}h {elapsed.Minutes}m";
    }

    [Function(
        "delete-task",
        @"Remove tasks that no longer serve the goal - adaptation is strength, not failure.

When to delete tasks:
• Requirement changed or was misunderstood
• Found a better approach that makes tasks obsolete
• Discovered the task is already completed elsewhere
• Task was based on incorrect assumptions
• Scope reduction or priority shift

This is POSITIVE adaptation showing:
• Learning from new information
• Willingness to change course
• Focus on value over plan adherence
• Agile thinking and flexibility

Before deleting:
• Add a note explaining WHY (learning for future)
• Consider if the task should be modified instead
• Check if subtasks should be preserved under different parent

Examples:
- Obsolete approach: {""taskId"": ""2.3""}  // After finding better solution
- Scope change: {""taskId"": ""4""}  // Entire feature removed
- Already done: {""taskId"": ""1.5""}  // Discovered existing implementation"
    )]
    public FunctionResult DeleteTask(
        [Description(
            "Task ID (e.g., '1', '1.2', '1.2.3'). Ids are 1-based: the first main task is '1', its first subtask '1.1'."
        )]
            string taskId,
        [Description(
            "Subtask ordinal to delete, one level BELOW taskId (1-based: taskId '1.2' + subtaskId 1 deletes '1.2.1'). "
                + "Omit it unless deleting such a child — omitting deletes the taskId task itself with its whole "
                + "subtree, and a dotted taskId already reaches subtasks. Never pass 0 or negative: delete-task is "
                + "destructive, so it refuses a <= 0 subtaskId with an error instead of falling back to the parent."
        )]
            int? subtaskId = null
    )
    {
        return NotifyIfChanged(DeleteTaskCore(taskId, subtaskId));
    }

    private FunctionResult DeleteTaskCore(string taskId, int? subtaskId)
    {
        // No NormalizeSubtaskId here, on purpose (#631): delete-task is destructive, so a <= 0
        // sentinel must NOT fall back to deleting the parent's whole subtree. It flows into the
        // ordinary child lookup below, finds nothing (ordinals are 1-based), and returns the
        // teaching error with the board untouched — the safe pre-#620 behavior plus education.
        lock (_sync)
        {
            var (task, _) = FindTaskByStringId(taskId);

            if (subtaskId.HasValue)
            {
                // Delete subtask
                if (task == null)
                {
                    return FunctionResult.Error(TaskNotFoundCode, $"Error: Parent task {taskId} not found.");
                }

                PrivateTaskItem? subtask;
                lock (task.SubTasks)
                {
                    subtask = task.SubTasks.FirstOrDefault(st => st.Id == subtaskId.Value);
                    if (subtask == null)
                    {
                        ReportVanishedId($"{task.DisplayId}.{subtaskId.Value}", "delete-task subtask lookup");
                        return SubtaskNotFoundError(taskId, subtaskId.Value);
                    }

                    _ = task.SubTasks.Remove(subtask);
                }

                // Deliberate removal, so the ledger forgets it: a later reference to this id is the
                // model's own mistake, not a lost row, and must not be reported as data loss.
                ForgetSubtree(subtask);
                return $"Deleted subtask {subtaskId.Value} from task {taskId}: {subtask.Title}";
            }

            // Delete main task and all subtasks
            if (task == null)
            {
                return FunctionResult.Error(TaskNotFoundCode, $"Error: Task {taskId} not found.");
            }

            _ = RemoveTaskAndSubtasks(task);
            ForgetSubtree(task);
            return $"Deleted task {taskId} and all subtasks: {task.Title}";
        }
    }

    public FunctionResult DeleteTask(int taskId, int? subtaskId = null)
    {
        return DeleteTask(taskId.ToString(), subtaskId);
    }

    [Function(
        "get-task",
        @"Retrieve details to verify prerequisites or next steps.
Use before acting, to confirm status/notes/subtasks.

Ids are 1-based: taskId '1' is the first main task, and subtaskId 3 is the
parent's third subtask (the per-level sibling ordinal).

Examples:
- Task: {""taskId"": 1}
- Subtask: {""taskId"": 1, ""subtaskId"": 3}"
    )]
    public FunctionResult GetTask(
        [Description(
            "Task ID (e.g., '1', '1.2', '1.2.3'). Ids are 1-based: the first main task is '1', its first subtask '1.1'."
        )]
            string taskId,
        [Description(
            "Subtask ordinal one level BELOW taskId (1-based: taskId '1.2' + subtaskId 1 addresses '1.2.1'). "
                + "Omit it unless addressing such a child — a dotted taskId already reaches subtasks. "
                + "Never pass 0; a value <= 0 is treated as omitted."
        )]
            int? subtaskId = null
    )
    {
        lock (_sync)
        {
            var (task, taskRef, error) = FindTaskWithReference(taskId, subtaskId);
            return task == null ? error!.Value : FormatTaskDetails(task, taskRef);
        }
    }

    public FunctionResult GetTask(int taskId, int? subtaskId = null)
    {
        return GetTask(taskId.ToString(), subtaskId);
    }

    [Function(
        "add-note",
        @"Capture learnings, insights, and context that will inform future decisions.

Notes are your memory across tasks - use them to:
• Record WHY decisions were made (not just what)
• Capture constraints, dependencies, or blockers discovered
• Store insights that might help with similar future tasks
• Document assumptions that need validation
• Track technical details that aren't obvious from task titles

Best practices:
• Add notes immediately when you learn something important
• Be specific - 'API returns 429 after 100 requests/min' not 'rate limit exists'
• Include context that your future self will need
• Update notes as understanding evolves

Examples:
- Learning: {""taskId"": ""1"", ""noteText"": ""Database locks occur when batch size > 1000""}
- Constraint: {""taskId"": ""1.2"", ""noteText"": ""Must complete before 3pm due to maintenance window""}
- Insight: {""taskId"": ""2.1"", ""noteText"": ""Similar pattern worked in auth module - see commit abc123""}"
    )]
    public FunctionResult AddNote(
        [Description(
            "Task ID (e.g., '1', '1.2', '1.2.3'). Ids are 1-based: the first main task is '1', its first subtask '1.1'."
        )]
            string taskId,
        [Description(
            "Subtask ordinal one level BELOW taskId (1-based: taskId '1.2' + subtaskId 1 notes '1.2.1'). "
                + "Omit it unless noting such a child — a dotted taskId already reaches subtasks, so to note "
                + "task '1.2' itself pass only taskId. Never pass 0; a value <= 0 is treated as omitted."
        )]
            int? subtaskId = null,
        [Description("Note text to add")] string noteText = ""
    )
    {
        return NotifyIfChanged(AddNoteCore(taskId, subtaskId, noteText));
    }

    private FunctionResult AddNoteCore(string taskId, int? subtaskId, string noteText)
    {
        if (string.IsNullOrWhiteSpace(noteText))
        {
            return FunctionResult.Error(InvalidArgumentsCode, "Error: Note text cannot be empty.");
        }

        lock (_sync)
        {
            var (targetTask, taskRef, error) = FindTaskWithReference(taskId, subtaskId);
            if (targetTask == null)
            {
                return error!.Value;
            }

            lock (targetTask.Notes)
            {
                targetTask.Notes.Add(noteText.Trim());
            }

            return $"Added note to {taskRef}.";
        }
    }

    public FunctionResult AddNote(int taskId, int? subtaskId = null, string noteText = "")
    {
        return AddNote(taskId.ToString(), subtaskId, noteText);
    }

    [Function(
        "edit-note",
        @"Edit an existing note to update information.
Use when you need to correct or update previously added context.

Examples:
- Edit note #2 on task 1: {""taskId"": 1, ""noteIndex"": 2, ""noteText"": ""Updated requirement""}
- Edit note #1 on subtask: {""taskId"": 1, ""subtaskId"": 3, ""noteIndex"": 1, ""noteText"": ""Changed approach""}"
    )]
    public FunctionResult EditNote(
        [Description(
            "Task ID (e.g., '1', '1.2', '1.2.3'). Ids are 1-based: the first main task is '1', its first subtask '1.1'."
        )]
            string taskId,
        [Description(
            "Subtask ordinal one level BELOW taskId (1-based: taskId '1.2' + subtaskId 1 edits a note on '1.2.1'). "
                + "Omit it unless editing such a child's note — a dotted taskId already reaches subtasks. "
                + "Never pass 0; a value <= 0 is treated as omitted."
        )]
            int? subtaskId = null,
        [Description("Note index to edit (1-based: 1 for first note, 2 for second, etc.)")] int noteIndex = 1,
        [Description("New text to replace the existing note")] string noteText = ""
    )
    {
        return NotifyIfChanged(EditNoteCore(taskId, subtaskId, noteIndex, noteText));
    }

    private FunctionResult EditNoteCore(string taskId, int? subtaskId, int noteIndex, string noteText)
    {
        if (string.IsNullOrWhiteSpace(noteText))
        {
            return FunctionResult.Error(InvalidArgumentsCode, "Error: Note text cannot be empty.");
        }

        lock (_sync)
        {
            var (targetTask, taskRef, error) = FindTaskWithReference(taskId, subtaskId);
            if (targetTask == null)
            {
                return error!.Value;
            }

            lock (targetTask.Notes)
            {
                if (noteIndex < 1 || noteIndex > targetTask.Notes.Count)
                {
                    return FunctionResult.Error(
                        NoteIndexOutOfRangeCode,
                        $"Error: Note index {noteIndex} out of range. {taskRef} has {targetTask.Notes.Count} note(s)."
                    );
                }

                targetTask.Notes[noteIndex - 1] = noteText.Trim();
            }

            return $"Updated note #{noteIndex} on {taskRef}.";
        }
    }

    public FunctionResult EditNote(int taskId, int? subtaskId = null, int noteIndex = 1, string noteText = "")
    {
        return EditNote(taskId.ToString(), subtaskId, noteIndex, noteText);
    }

    [Function(
        "delete-note",
        @"Delete a note that is no longer relevant.
Use to remove outdated or incorrect information.

Examples:
- Delete note #1 from task 2: {""taskId"": 2, ""noteIndex"": 1}
- Delete note #3 from subtask: {""taskId"": 1, ""subtaskId"": 2, ""noteIndex"": 3}"
    )]
    public FunctionResult DeleteNote(
        [Description(
            "Task ID (e.g., '1', '1.2', '1.2.3'). Ids are 1-based: the first main task is '1', its first subtask '1.1'."
        )]
            string taskId,
        [Description(
            "Subtask ordinal one level BELOW taskId (1-based: taskId '1.2' + subtaskId 1 deletes a note on '1.2.1'). "
                + "Omit it unless deleting such a child's note — a dotted taskId already reaches subtasks. "
                + "Never pass 0; a value <= 0 is treated as omitted."
        )]
            int? subtaskId = null,
        [Description("Note index to delete (1-based: 1 for first note, 2 for second, etc.)")] int noteIndex = 1
    )
    {
        return NotifyIfChanged(DeleteNoteCore(taskId, subtaskId, noteIndex));
    }

    private FunctionResult DeleteNoteCore(string taskId, int? subtaskId, int noteIndex)
    {
        lock (_sync)
        {
            var (targetTask, taskRef, error) = FindTaskWithReference(taskId, subtaskId);
            if (targetTask == null)
            {
                return error!.Value;
            }

            lock (targetTask.Notes)
            {
                if (noteIndex < 1 || noteIndex > targetTask.Notes.Count)
                {
                    return FunctionResult.Error(
                        NoteIndexOutOfRangeCode,
                        $"Error: Note index {noteIndex} out of range. {taskRef} has {targetTask.Notes.Count} note(s)."
                    );
                }

                var deletedNote = targetTask.Notes[noteIndex - 1];
                targetTask.Notes.RemoveAt(noteIndex - 1);
                return $"Deleted note #{noteIndex} from {taskRef}: \"{deletedNote}\".";
            }
        }
    }

    public FunctionResult DeleteNote(int taskId, int? subtaskId = null, int noteIndex = 1)
    {
        return DeleteNote(taskId.ToString(), subtaskId, noteIndex);
    }

    public FunctionResult ManageNotes(
        string taskId,
        int? subtaskId = null,
        string noteText = "",
        int noteIndex = 1,
        string action = "add"
    )
    {
        return action.Trim().ToLowerInvariant() switch
        {
            "add" => AddNote(taskId, subtaskId, noteText),
            "edit" => Rephrase(
                EditNote(taskId, subtaskId, noteIndex, noteText),
                $"Updated note #{noteIndex} on",
                $"Edited note {noteIndex} on"
            ),
            "delete" => Rephrase(
                DeleteNote(taskId, subtaskId, noteIndex),
                $"Deleted note #{noteIndex} from",
                $"Deleted note {noteIndex} from"
            ),
            _ => FunctionResult.Error(InvalidActionCode, "Error: Invalid action. Use: add, edit, delete."),
        };
    }

    public FunctionResult ManageNotes(
        int taskId,
        int? subtaskId = null,
        string noteText = "",
        int noteIndex = 1,
        string action = "add"
    )
    {
        return ManageNotes(taskId.ToString(), subtaskId, noteText, noteIndex, action);
    }

    [Function(
        "list-notes",
        @"List all notes to recall context for the next step.

Examples:
- Task notes: {""taskId"": 1}
- Subtask notes: {""taskId"": 1, ""subtaskId"": 3}"
    )]
    public FunctionResult ListNotes(
        [Description(
            "Task ID (e.g., '1', '1.2', '1.2.3'). Ids are 1-based: the first main task is '1', its first subtask '1.1'."
        )]
            string taskId,
        [Description(
            "Subtask ordinal one level BELOW taskId (1-based: taskId '1.2' + subtaskId 1 lists notes on '1.2.1'). "
                + "Omit it unless listing such a child's notes — a dotted taskId already reaches subtasks. "
                + "Never pass 0; a value <= 0 is treated as omitted."
        )]
            int? subtaskId = null
    )
    {
        List<string> notesCopy;
        string taskRef;
        string title;
        lock (_sync)
        {
            // Find target task using helper method
            var (targetTask, foundRef, error) = FindTaskWithReference(taskId, subtaskId);
            if (targetTask == null)
            {
                return error!.Value;
            }

            taskRef = foundRef;
            title = targetTask.Title;

            lock (targetTask.Notes)
            {
                if (targetTask.Notes.Count == 0)
                {
                    return $"{taskRef} has no notes.";
                }

                notesCopy = [.. targetTask.Notes];
            }
        }

        var sb = new StringBuilder();
        AppendLine(sb, $"Notes for {taskRef}: {title}");
        for (var i = 0; i < notesCopy.Count; i++)
        {
            AppendLine(sb, $"{i + 1}. {notesCopy[i]}");
        }

        return sb.ToString().TrimEnd();
    }

    public FunctionResult ListNotes(int taskId, int? subtaskId = null)
    {
        return ListNotes(taskId.ToString(), subtaskId);
    }

    [Function(
        "list-tasks",
        @"Review your evolving plan to maintain focus and choose next actions wisely.

Use regularly to:
• Maintain situational awareness of overall progress
• Identify tasks that need updating based on learnings
• Spot imbalances (too many tasks at one level)
• Choose the next task based on dependencies and priority
• Celebrate completed work and learn from it
• See who owns what: an assignee renders as [@name], a blocked task shows what it is
  waiting on, and an in-progress task shows how long it has been claimed

Filtering strategies:
• status='in progress' - Focus on current work (WIP limit)
• status='not started' - Plan next moves
• status='blocked' - See what is stuck and on whom it is waiting
• mainOnly=true - See the big picture without details
• No filter - Full context for major decisions. The default already lists ALL tasks;
  status='all' is accepted and means the same no-filter listing

Healthy patterns:
• 1-3 tasks 'in progress' at once (focus)
• Regular completed tasks (momentum)
• Evolving 'not started' list (adaptation)
• Notes on completed tasks (learning capture)
• No task claimed and idle for long — a stale-looking elapsed time is worth a
  claim-task heartbeat or a hand-off

Examples:
- Next action: {""status"": ""not started"", ""mainOnly"": false}
- WIP check: {""status"": ""in progress""}
- What's stuck: {""status"": ""blocked""}
- Overview: {""mainOnly"": true}"
    )]
    public FunctionResult ListTasks(
        [Description(
            "Filter by status: not started|in progress|blocked|completed|removed. Omit for all tasks (the default already lists all); 'all' is accepted as the same no-filter."
        )]
            string? status = null,
        [Description("Show only main tasks (exclude subtasks)")] bool mainOnly = false
    )
    {
        TaskStatus? filterStatus = null;

        // 'all' is a no-filter sentinel: the default already lists every task, and models pass
        // "all" to say exactly that — refusing it only produces a retry. (#620 F4)
        var wantsAll = string.Equals(status?.Trim(), "all", StringComparison.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(status) && !wantsAll)
        {
            if (!TryParseStatus(status, out var parsedStatus))
            {
                return FunctionResult.Error(
                    InvalidStatusCode,
                    "Error: Invalid status filter. Use: all, not started, in progress, blocked, completed, removed."
                );
            }

            filterStatus = parsedStatus;
        }

        // Snapshotting the root list is not enough: everything below the roots is walked here,
        // and AddTask appends to a nested SubTasks list holding only _sync. The lock therefore
        // has to span the whole traversal, not just the copy.
        lock (_sync)
        {
            var sb = new StringBuilder();
            var now = _timeProvider.GetUtcNow();

            // Count tasks by status for summary
            var allTasks = GetAllTasksFlat(_state.RootTasks);
            var notStartedCount = allTasks.Count(t => t.Status == TaskStatus.NotStarted);
            var inProgressCount = allTasks.Count(t => t.Status == TaskStatus.InProgress);
            var blockedCount = allTasks.Count(t => t.Status == TaskStatus.Blocked);
            var completedCount = allTasks.Count(t => t.Status == TaskStatus.Completed);
            var totalActive = notStartedCount + inProgressCount + blockedCount;

            // Beautiful header with task summary
            AppendLine(sb, "# 📋 Task List");
            if (filterStatus == null && !mainOnly)
            {
                AppendLine(sb);
                AppendLine(
                    sb,
                    $"**Status**: {inProgressCount} in progress | {notStartedCount} pending | {blockedCount} blocked | {completedCount} completed"
                );
                AppendLine(sb, $"**Total**: {totalActive} active tasks");
            }

            AppendLine(sb);

            // Render the body separately so "nothing to show" is decided by what was actually
            // written, not by sniffing the tail of the assembled string.
            var body = new StringBuilder();
            foreach (var task in _state.RootTasks)
            {
                AppendTaskMarkdown(body, task, 0, now, filterStatus, mainOnly);
            }

            // An empty list still gets the header — a bare "No tasks found." gives the model no
            // clue which tool answered it.
            _ =
                body.Length == 0
                    ? sb.Append(
                        _state.RootTasks.Count == 0 ? "No tasks found." : "No tasks match the specified criteria."
                    )
                    : sb.Append(body);

            return sb.ToString().TrimEnd();
        }
    }

    public IList<TaskItem> GetTasks()
    {
        lock (_sync)
        {
            return [.. _state.RootTasks.Select(t => t.ToPublic())];
        }
    }

    /// <summary>
    ///     Captures the board for the conversation read path (<c>GET /todos</c>), together with the id
    ///     ledger's unresolved entries (#621 Part B).
    /// </summary>
    /// <remarks>
    ///     The row projection is <see cref="GetTasks" />'s, inlined under one <see cref="_sync" /> hold
    ///     rather than composed from it: the ledger diff must be taken against the very same rows the
    ///     capture publishes, and two separate lock takes would let a mutation land between them and
    ///     report a row as missing that the published tree still shows.
    /// </remarks>
    public TodoBoardSnapshot GetTodoBoardSnapshot(string threadId)
    {
        List<TodoTaskNode> tasks;
        Dictionary<string, DateTimeOffset> missing;

        lock (_sync)
        {
            tasks = [.. _state.RootTasks.Select(t => t.ToPublic()).Select(ToBoardNode)];

            // Only the ids the ledger still owns and the board can no longer show are worth carrying:
            // for every id that IS present, "last known present" is the capture instant itself, which
            // rehydration re-derives from the rows. See TodoBoardSnapshot.MissingTaskIds. (#621 Part B)
            var present = GetAllTasksFlat(_state.RootTasks).Select(t => t.DisplayId).ToHashSet(StringComparer.Ordinal);
            missing = _state
                .SeenIds.Where(entry => !present.Contains(entry.Key))
                .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);
        }

        return new TodoBoardSnapshot
        {
            ThreadId = threadId,
            CapturedAtUtc = DateTimeOffset.UtcNow,
            Tasks = tasks,
            MissingTaskIds = missing,
        };
    }

    /// <summary>
    ///     Projects one task and its subtree onto the transport-facing board shape.
    /// </summary>
    /// <remarks>
    ///     The status mapping is written out member by member rather than cast across the two enums. A
    ///     cast would work today, since the two enums are structurally identical, and it would also stop
    ///     working silently the day a member is INSERTED into one of them, re-labelling every persisted
    ///     and in-flight row by one position.
    ///     <para>
    ///     A C# switch expression over an enum still requires a catch-all arm for values outside the
    ///     named set (CS8524) even when every named member is listed, so this cannot be a true
    ///     compile-time exhaustiveness check — a future <see cref="TaskStatus" /> member left unmapped
    ///     falls into the discard below rather than failing to build. It throws instead of guessing a
    ///     status, so the gap surfaces as a loud runtime failure on the read path rather than a silently
    ///     wrong board (this is how <see cref="TaskStatus.Blocked" /> was caught: it compiled clean
    ///     against a stale <c>_ =&gt; TodoTaskStatus.NotStarted</c> discard and every Blocked task
    ///     rendered as NotStarted until this method was updated).
    ///     </para>
    /// </remarks>
    private static TodoTaskNode ToBoardNode(TaskItem task)
    {
        return new TodoTaskNode
        {
            Id = task.Id,
            Status = task.Status switch
            {
                TaskStatus.NotStarted => TodoTaskStatus.NotStarted,
                TaskStatus.InProgress => TodoTaskStatus.InProgress,
                TaskStatus.Blocked => TodoTaskStatus.Blocked,
                TaskStatus.Completed => TodoTaskStatus.Completed,
                TaskStatus.Removed => TodoTaskStatus.Removed,
                _ => throw new ArgumentOutOfRangeException(
                    nameof(task),
                    task.Status,
                    $"Unmapped {nameof(TaskStatus)} value; add an explicit arm above rather than falling through."
                ),
            },
            Title = task.Title,
            Notes = [.. task.Notes],
            Artifacts = [.. task.Artifacts],
            BlockedBy = [.. task.BlockedBy],
            Assignee = task.Assignee,
            CreatedAt = task.Times?.CreatedAt,
            ClaimedAt = task.Times?.ClaimedAt,
            CompletedAt = task.Times?.CompletedAt,
            SubTasks = [.. task.SubTasks.Select(ToBoardNode)],
        };
    }

    [Function(
        "search-tasks",
        @"Search by title or get plan statistics to validate completion criteria.

Examples:
- Find 'plan' tasks: {""searchTerm"": ""plan""}
- Completed count: {""countType"": ""completed""}
- Pending count: {""countType"": ""pending""}"
    )]
    public FunctionResult SearchTasks(
        [Description("Search term for title")] string? searchTerm = null,
        [Description("Get counts: total|completed|pending|removed")] string? countType = null
    )
    {
        if (!string.IsNullOrEmpty(countType))
        {
            return GetTaskCounts(countType.ToLowerInvariant());
        }

        if (string.IsNullOrWhiteSpace(searchTerm))
        {
            return FunctionResult.Error(InvalidArgumentsCode, "Error: Provide searchTerm or countType.");
        }

        var matches = new List<(PrivateTaskItem task, string path)>();

        // SearchTaskRecursive descends into every nested SubTasks list, which AddTask appends
        // to under _sync alone — so the lock has to be held for the descent, not just for a
        // snapshot of the roots.
        lock (_sync)
        {
            foreach (var task in _state.RootTasks)
            {
                SearchTaskRecursive(task, searchTerm.Trim(), task.Id.ToString(), matches);
            }

            if (matches.Count == 0)
            {
                return $"No tasks found matching '{searchTerm}'.";
            }

            var sb = new StringBuilder();
            AppendLine(sb, $"Found {matches.Count} task(s) matching '{searchTerm}':");
            foreach (var (task, path) in matches)
            {
                var statusSymbol = GetStatusSymbol(task.Status);
                AppendLine(sb, $"- {statusSymbol} {path}: {task.Title}");
            }

            return sb.ToString().TrimEnd();
        }
    }

    /// <summary>
    ///     Renders the whole tree as markdown. It delegates to <see cref="ListTasks" />, and so
    ///     inherits that method's lock coverage rather than walking the tree itself.
    /// </summary>
    public string GetMarkdown()
    {
        return ListTasks().Text;
    }

    // Helper methods

    /// <summary>
    ///     Rewrites a result's wording while preserving whether it succeeded. Rewriting the text
    ///     alone would drop the error code and turn a reported failure back into a success.
    /// </summary>
    private static FunctionResult Rephrase(FunctionResult result, string oldText, string newText)
    {
        var text = result.Text.Replace(oldText, newText, StringComparison.Ordinal);
        return result.ErrorCode is { } errorCode ? FunctionResult.Error(errorCode, text) : FunctionResult.Ok(text);
    }

    /// <summary>
    ///     Appends a line terminated by a literal LF. <see cref="StringBuilder.AppendLine()" />
    ///     emits <see cref="Environment.NewLine" />, which would make this tool's rendered
    ///     markdown differ between Windows and everything else.
    /// </summary>
    private static void AppendLine(StringBuilder sb, string text)
    {
        _ = sb.Append(text).Append('\n');
    }

    private static void AppendLine(StringBuilder sb)
    {
        _ = sb.Append('\n');
    }

    /// <summary>
    ///     Treats a non-positive subtaskId as omitted. Ordinals are 1-based, so 0 or a negative
    ///     value can never address a real subtask — models pass them as "none" sentinels (65% of
    ///     the failing add-note calls in the #617 corpus), and refusing the sentinel only
    ///     produces retry storms. (#620) Applied only at the read/annotate seam
    ///     (<see cref="FindTaskWithReference" />): delete-task is deliberately carved out,
    ///     because there the same tolerance would turn a 0-based-confusion mistake into silent
    ///     subtree destruction. (#631)
    /// </summary>
    private static int? NormalizeSubtaskId(int? subtaskId)
    {
        return subtaskId <= 0 ? null : subtaskId;
    }

    /// <summary>
    ///     The teaching error for a genuinely wrong positive ordinal: names the id the call
    ///     actually addressed and how to reach the task the model probably meant. Shared by both
    ///     subtask lookup routes — <see cref="FindTaskWithReference" /> and
    ///     <see cref="DeleteTaskCore" /> — so they cannot drift apart. (#620)
    /// </summary>
    private static FunctionResult SubtaskNotFoundError(string taskId, int subtaskId)
    {
        return FunctionResult.Error(
            TaskNotFoundCode,
            $"Error: Task '{taskId}' has no subtask {subtaskId}. subtaskId addresses one level BELOW taskId "
                + $"(that call names '{taskId}.{subtaskId}'). If you meant task '{taskId}' itself, omit subtaskId. "
                + "Ids are 1-based."
        );
    }

    /// <summary>
    ///     Records that <paramref name="task" /> — and, when <paramref name="includeSubtree" /> is set,
    ///     everything under it — was on this board at <paramref name="at" />. Together with
    ///     <see cref="ForgetSubtree" /> this maintains the invariant the vanish detector rests on: the
    ///     ledger holds exactly the ids the board <b>ought</b> to contain, so an id in the ledger that the
    ///     board cannot find is a lost row rather than an id the model invented. (#621 Part B)
    /// </summary>
    /// <remarks>
    ///     Callers hold <see cref="_sync" />. Recording on successful lookups as well as on creation is
    ///     what makes the warning's last-seen instant mean "last time this board could still find it"
    ///     rather than "when it was created".
    /// </remarks>
    private void RecordSeen(PrivateTaskItem task, DateTimeOffset at, bool includeSubtree = false)
    {
        _state.SeenIds[task.DisplayId] = at;

        if (!includeSubtree)
        {
            return;
        }

        lock (task.SubTasks)
        {
            foreach (var child in task.SubTasks)
            {
                RecordSeen(child, at, includeSubtree: true);
            }
        }
    }

    /// <summary>
    ///     Drops <paramref name="task" /> and its whole subtree from the id ledger, because the board was
    ///     asked to remove them. This is the single most important half of the detector: without it every
    ///     post-<c>delete-task</c> reference to a deliberately removed id would be reported as data loss,
    ///     which is the false positive that would make the warning worthless. (#621 Part B)
    /// </summary>
    private void ForgetSubtree(PrivateTaskItem task)
    {
        _ = _state.SeenIds.Remove(task.DisplayId);

        lock (task.SubTasks)
        {
            foreach (var child in task.SubTasks)
            {
                ForgetSubtree(child);
            }
        }
    }

    /// <summary>
    ///     The board-loss detector (#621 Part B). Called at every not-found exit with the CANONICAL dotted
    ///     id the lookup actually addressed; logs <c>TodoBoardIdVanished</c> only when that id is in the
    ///     ledger — that is, when this board held the row and was never told to delete it.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The discrimination line: <b>ledger hit ⇒ state loss, ledger miss ⇒ silence.</b> An id the
    ///         model invented was never allocated here, so it is not in the ledger; an id removed by
    ///         <c>delete-task</c> was taken out of the ledger by <see cref="ForgetSubtree" />. Both stay
    ///         quiet. Only a row this board is still accountable for, and can no longer find, warns.
    ///     </para>
    ///     <para>
    ///         Deliberately NOT self-silencing: the ledger entry survives the report, so a retry storm
    ///         against a vanished id produces one warning per attempt. Each of those attempts really is a
    ///         data-loss symptom, and the alternative — reporting once — would make the log undercount
    ///         exactly the pathology (#617's 48x storm) the watch exists to size.
    ///     </para>
    ///     <para>
    ///         Callers hold <see cref="_sync" />, so the ledger read cannot race a concurrent mutation
    ///         into a spurious report.
    ///     </para>
    /// </remarks>
    /// <param name="canonicalId">
    ///     The dotted id as this board would have spelled it, never the raw model input: lookup accepts
    ///     <c>"01"</c>, <c>"+1"</c> and <c>" 1"</c> for task <c>1</c>, and a ledger keyed by ordinal string
    ///     comparison would miss every one of them.
    /// </param>
    /// <param name="lookup">Which lookup route reported the miss, so the log says where it happened.</param>
    private void ReportVanishedId(string canonicalId, string lookup)
    {
        if (Logger is not { } logger || !_state.SeenIds.TryGetValue(canonicalId, out var lastSeen))
        {
            return;
        }

        logger.LogWarning(
            TodoBoardIdVanishedEvent,
            "TodoBoardIdVanished: task {TaskId} on thread {ThreadId} was on this board at {LastSeenUtc:O} and was never deleted, but {Lookup} reported it not found — the board lost a row it still owns",
            canonicalId,
            ThreadId,
            lastSeen,
            lookup
        );
    }

    /// <summary>
    ///     Finds a task by (possibly dotted) ID and optional subtask ID, returning the task, a
    ///     reference string, and any error. This consolidates the repeated task lookup pattern.
    ///     The task ID accepts the same dotted paths <c>add-task</c> produces, so every task the
    ///     hierarchy can hold is addressable — not just the first two levels.
    /// </summary>
    private (PrivateTaskItem? task, string taskRef, FunctionResult? error) FindTaskWithReference(
        string taskId,
        int? subtaskId
    )
    {
        subtaskId = NormalizeSubtaskId(subtaskId);
        lock (_sync)
        {
            var (task, error) = FindTaskByStringId(taskId);

            if (task == null)
            {
                return (
                    null,
                    string.Empty,
                    error ?? FunctionResult.Error(TaskNotFoundCode, $"Error: Task {taskId} not found.")
                );
            }

            if (subtaskId.HasValue)
            {
                PrivateTaskItem? subtask;
                lock (task.SubTasks)
                {
                    subtask = task.SubTasks.FirstOrDefault(st => st.Id == subtaskId.Value);
                }

                if (subtask == null)
                {
                    ReportVanishedId($"{task.DisplayId}.{subtaskId.Value}", "subtask lookup");
                    return (null, string.Empty, SubtaskNotFoundError(taskId, subtaskId.Value));
                }

                RecordSeen(subtask, _timeProvider.GetUtcNow());
                return (subtask, $"subtask {subtaskId.Value} of task {taskId}", null);
            }

            return (task, $"task {taskId}", null);
        }
    }

    private (PrivateTaskItem? task, FunctionResult? error) FindTaskByStringId(string taskId)
    {
        lock (_sync)
        {
            // Parse hierarchical ID like "1", "1.2", "1.2.3"
            var parts = (taskId ?? string.Empty).Split('.');
            if (parts.Length == 0 || !int.TryParse(parts[0], out var rootId))
            {
                return (null, FunctionResult.Error(InvalidTaskIdCode, $"Error: Invalid task ID format '{taskId}'."));
            }

            // Find root task
            var currentTask = _state.RootTasks.FirstOrDefault(t => t.Id == rootId);
            if (currentTask == null)
            {
                // The ledger is keyed by canonical display id, so the probe uses the PARSED root id
                // rather than parts[0] — " 01" and "1" address the same row and must look up the same.
                ReportVanishedId(rootId.ToString(), "task lookup");
                return (null, FunctionResult.Error(TaskNotFoundCode, $"Error: Task '{parts[0]}' not found."));
            }

            // Navigate through subtask hierarchy
            for (var i = 1; i < parts.Length; i++)
            {
                if (!int.TryParse(parts[i], out var subId))
                {
                    return (
                        null,
                        FunctionResult.Error(
                            InvalidTaskIdCode,
                            $"Error: Invalid subtask ID '{parts[i]}' in '{taskId}'."
                        )
                    );
                }

                PrivateTaskItem? nextTask = null;
                lock (currentTask.SubTasks)
                {
                    nextTask = currentTask.SubTasks.FirstOrDefault(st => st.Id == subId);
                }

                if (nextTask == null)
                {
                    var path = string.Join(".", parts.Take(i + 1));
                    // Canonical form of that same path: the resolved parent's display id plus the parsed
                    // segment. `path` itself is assembled from the raw input and is only for the message,
                    // which stays byte-identical to what it has always been.
                    ReportVanishedId($"{currentTask.DisplayId}.{subId}", "task lookup");
                    return (null, FunctionResult.Error(TaskNotFoundCode, $"Error: Task '{path}' not found."));
                }

                currentTask = nextTask;
            }

            RecordSeen(currentTask, _timeProvider.GetUtcNow());
            return (currentTask, null);
        }
    }

    /// <summary>
    ///     Detaches a task, and with it everything below it, from wherever it is attached —
    ///     the root list or some parent's subtask list. Deleting only from
    ///     <c>RootTasks</c> left every nested task in place.
    /// </summary>
    private bool RemoveTaskAndSubtasks(PrivateTaskItem task)
    {
        lock (_sync)
        {
            return _state.RootTasks.Remove(task) || RemoveFromSubtree(_state.RootTasks, task);
        }
    }

    private static bool RemoveFromSubtree(List<PrivateTaskItem> candidates, PrivateTaskItem target)
    {
        foreach (var candidate in candidates)
        {
            lock (candidate.SubTasks)
            {
                if (candidate.SubTasks.Remove(target))
                {
                    return true;
                }
            }

            if (RemoveFromSubtree(candidate.SubTasks, target))
            {
                return true;
            }
        }

        return false;
    }

    private static List<PrivateTaskItem> GetAllTasksFlat(List<PrivateTaskItem> rootTasks)
    {
        var allTasks = new List<PrivateTaskItem>();

        void AddTaskAndSubtasks(PrivateTaskItem task)
        {
            allTasks.Add(task);
            foreach (var subtask in task.SubTasks)
            {
                AddTaskAndSubtasks(subtask);
            }
        }

        foreach (var task in rootTasks)
        {
            AddTaskAndSubtasks(task);
        }

        return allTasks;
    }

    private static string FormatTaskDetails(PrivateTaskItem task, string header)
    {
        var sb = new StringBuilder();
        AppendLine(sb, $"{string.Concat(header[..1].ToUpper(), header.AsSpan(1))}: {task.Title}");
        AppendLine(sb, $"Status: {NormalizeStatusText(task.Status)}");

        List<string> notesCopy;
        lock (task.Notes)
        {
            notesCopy = [.. task.Notes];
        }

        if (notesCopy.Count > 0)
        {
            AppendLine(sb, $"Notes ({notesCopy.Count}):");
            for (var i = 0; i < notesCopy.Count; i++)
            {
                AppendLine(sb, $"{i + 1}. {notesCopy[i]}");
            }
        }

        List<string> artifactsCopy = [.. task.Artifacts];
        if (artifactsCopy.Count > 0)
        {
            AppendLine(sb, $"Artifacts ({artifactsCopy.Count}):");
            foreach (var artifact in artifactsCopy)
            {
                AppendLine(sb, $"- {artifact}");
            }
        }

        List<PrivateTaskItem> subtasksCopy;
        lock (task.SubTasks)
        {
            subtasksCopy = [.. task.SubTasks];
        }

        if (subtasksCopy.Count > 0)
        {
            AppendLine(sb, $"Subtasks ({subtasksCopy.Count}):");
            foreach (var subtask in subtasksCopy)
            {
                var statusSymbol = GetStatusSymbol(subtask.Status);
                AppendLine(sb, $"  {statusSymbol} {subtask.Id}. {subtask.Title}");
            }
        }

        return sb.ToString().TrimEnd();
    }

    private static void AppendTaskMarkdown(
        StringBuilder sb,
        PrivateTaskItem task,
        int level,
        DateTimeOffset now,
        TaskStatus? filterStatus = null,
        bool mainOnly = false
    )
    {
        if (filterStatus.HasValue && task.Status != filterStatus.Value)
        {
            return;
        }

        var indent = new string(' ', level * 2);
        var statusSymbol = GetStatusSymbol(task.Status);

        // Use hierarchical numbering with proper formatting
        var taskNumber = string.IsNullOrEmpty(task.DisplayId) ? task.Id.ToString() : task.DisplayId;
        var removedSuffix = task.Status == TaskStatus.Removed ? " (removed)" : string.Empty;
        var assigneeSuffix = task.Assignee != null ? $" [@{task.Assignee}]" : string.Empty;
        var statusExtra = task.Status switch
        {
            TaskStatus.Blocked when task.BlockedBy.Count > 0 => $" (blocked by {string.Join(", ", task.BlockedBy)})",
            TaskStatus.InProgress when task.ClaimedAt.HasValue => $" ({FormatElapsed(now - task.ClaimedAt.Value)})",
            _ => string.Empty,
        };

        AppendLine(
            sb,
            $"{indent}{statusSymbol} {taskNumber}. {task.Title}{removedSuffix}{assigneeSuffix}{statusExtra}"
        );

        List<string> notesCopy;
        lock (task.Notes)
        {
            notesCopy = [.. task.Notes];
        }

        if (notesCopy.Count > 0)
        {
            AppendLine(sb, $"{indent}  Notes:");
            for (var i = 0; i < notesCopy.Count; i++)
            {
                AppendLine(sb, $"{indent}  {i + 1}. {notesCopy[i]}");
            }
        }

        // Unlike notes, artifacts render as plain bullets: no tool addresses one by index, so
        // numbering them would imply an edit handle that does not exist.
        List<string> artifactsCopy = [.. task.Artifacts];
        if (artifactsCopy.Count > 0)
        {
            AppendLine(sb, $"{indent}  Artifacts:");
            foreach (var artifact in artifactsCopy)
            {
                AppendLine(sb, $"{indent}  - {artifact}");
            }
        }

        if (!mainOnly)
        {
            List<PrivateTaskItem> subtasksCopy;
            lock (task.SubTasks)
            {
                subtasksCopy = [.. task.SubTasks];
            }

            foreach (var sub in subtasksCopy)
            {
                AppendTaskMarkdown(sb, sub, level + 1, now, filterStatus, mainOnly);
            }
        }
    }

    private static void SearchTaskRecursive(
        PrivateTaskItem task,
        string searchTerm,
        string path,
        List<(PrivateTaskItem, string)> matches
    )
    {
        if (task.Title.Contains(searchTerm, StringComparison.OrdinalIgnoreCase))
        {
            matches.Add((task, path));
        }

        List<PrivateTaskItem> subtasksCopy;
        lock (task.SubTasks)
        {
            subtasksCopy = [.. task.SubTasks];
        }

        for (var i = 0; i < subtasksCopy.Count; i++)
        {
            var subtask = subtasksCopy[i];
            SearchTaskRecursive(subtask, searchTerm, $"{path}.{subtask.Id}", matches);
        }
    }

    public string JsonSerializeTasks()
    {
        // Serialising walks the whole tree; AddTask must not be reshaping it at the same time.
        lock (_sync)
        {
            return JsonSerializer.Serialize(
                _state,
                new JsonSerializerOptions { WriteIndented = false, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }
            );
        }
    }

    public JsonElement JsonSerializeTasksToJsonElements()
    {
        lock (_sync)
        {
            return JsonSerializer.SerializeToElement(
                _state,
                new JsonSerializerOptions { WriteIndented = false, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }
            );
        }
    }

    public static TaskManager DeserializeTasks(JsonElement json)
    {
        return DeserializeTasks(json, TimeProvider.System);
    }

    /// <summary>
    ///     Overload that accepts a <see cref="TimeProvider" /> so callers — chiefly tests —
    ///     can round-trip a persisted board and then control the clock the rehydrated instance
    ///     uses for lease-staleness and elapsed rendering.
    /// </summary>
    public static TaskManager DeserializeTasks(JsonElement json, TimeProvider timeProvider)
    {
        var state = json.Deserialize<ManagerState>();
        return state == null
            ? new TaskManager(timeProvider)
            : new TaskManager(state, timeProvider, DefaultLeaseStaleAfter);
    }

    public static TaskManager DeserializeTasks(string json)
    {
        return DeserializeTasks(json, TimeProvider.System);
    }

    public static TaskManager DeserializeTasks(string json, TimeProvider timeProvider)
    {
        var tasks = JsonSerializer.Deserialize<ManagerState>(json);
        return tasks == null
            ? new TaskManager(timeProvider)
            : new TaskManager(tasks, timeProvider, DefaultLeaseStaleAfter);
    }

    public static TaskManager FromSnapshot(TodoBoardSnapshot snapshot)
    {
        return FromSnapshot(snapshot, TimeProvider.System);
    }

    /// <summary>
    ///     Rehydrates a manager from a persisted board snapshot (#583 PR 2, review F-002), so a pool
    ///     entry recreated after eviction, a provider/mode swap, or a restart starts from the durable
    ///     board instead of empty — where its very first mutation would persist a one-row board over
    ///     the real one (the writer's empty-board guard cannot help once one row exists, and the
    ///     projection's monotonic guard cannot either, because the fresh capture is genuinely newer).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Ids are restored EXACTLY: each row's dotted <c>id</c> becomes its <c>DisplayId</c>, the
    ///         per-level numeric id is the path's last segment, and the id counters advance past the
    ///         highest hydrated value — so ids stay stable across the recreate and never collide with
    ///         rows added afterwards. This is why hydration does not go through <c>BulkInitialize</c>,
    ///         which would renumber from 1, flatten nesting to one level, and reset every status.
    ///     </para>
    ///     <para>
    ///         Everything the board snapshot carries comes back: id, status, title, notes, artifacts,
    ///         <c>blockedBy</c>, <c>assignee</c>, the timestamps, and the subtree. The coordination
    ///         fields all round-trip as of #595: <c>blockedBy</c> (review 590/D-1) so a
    ///         <see cref="TaskStatus.Blocked" /> row keeps its recorded blockers and its enforcement
    ///         across the restart, and <c>assignee</c>/<c>claimedAt</c> (D2) so the agent that
    ///         claimed a row before the recreate can still complete it and a live lease still
    ///         refuses foreign claims until it actually goes stale.
    ///     </para>
    ///     <para>
    ///         Rows from snapshots persisted before those fields existed are normalized to states
    ///         the guards can honestly enforce: a Blocked row with no restorable blockers becomes
    ///         <see cref="TaskStatus.NotStarted" /> rather than rendering a block nothing enforces
    ///         (re-block it with <c>block-task</c> if the dependency still holds); an InProgress row
    ///         with an assignee but no restorable <c>claimedAt</c> has its lease aged from the
    ///         snapshot's capture instant so it can still go stale; and an InProgress row with no
    ///         assignee at all self-heals on the next claim, which adopts it cleanly.
    ///     </para>
    /// </remarks>
    public static TaskManager FromSnapshot(TodoBoardSnapshot snapshot, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(timeProvider);

        var state = new ManagerState();
        var maxRootId = 0;
        foreach (var (node, index) in snapshot.Tasks.Select(static (node, index) => (node, index)))
        {
            var item = FromBoardNode(node, parentId: null, position: index + 1, snapshot.CapturedAtUtc);
            state.RootTasks.Add(item);
            maxRootId = Math.Max(maxRootId, item.Id);
        }

        state.NextId = maxRootId + 1;

        // Rebuild the id ledger (#621 Part B): every hydrated row was demonstrably present when the
        // snapshot was taken, so CapturedAtUtc is its honest last-seen instant, and the ids the previous
        // process had already lost ride along in MissingTaskIds. Without this the recreated board would
        // start with an empty ledger and read every lost row as an id that never existed.
        var rehydrated = new TaskManager(state, timeProvider, DefaultLeaseStaleAfter);
        foreach (var root in state.RootTasks)
        {
            rehydrated.RecordSeen(root, snapshot.CapturedAtUtc, includeSubtree: true);
        }

        foreach (var (id, lastSeen) in snapshot.MissingTaskIds)
        {
            state.SeenIds[id] = lastSeen;
        }

        return rehydrated;
    }

    private static PrivateTaskItem FromBoardNode(
        TodoTaskNode node,
        int? parentId,
        int position,
        DateTimeOffset capturedAtUtc
    )
    {
        // The per-level numeric id is the dotted path's last segment ("1.2.3" -> 3). A segment that
        // does not parse (malformed persisted data) falls back to the row's 1-based position, which
        // keeps hydration total rather than throwing away the whole board for one bad id.
        var lastSegment = node.Id[(node.Id.LastIndexOf('.') + 1)..];
        var id = int.TryParse(lastSegment, out var parsed) && parsed > 0 ? parsed : position;

        var item = new PrivateTaskItem
        {
            Id = id,
            DisplayId = node.Id,
            Title = node.Title,
            Status = FromBoardStatus(node.Status),
            ParentId = parentId,
            // The claim is part of the persisted board too (#595, D2): without it, the very agent
            // that claimed a row before the recreate could no longer complete it (task_not_claimed),
            // and a foreign agent could claim over a lease that was still live. Restoring these
            // fields is hydration, not a transition — no OnChanged can fire here, because the
            // manager is still being constructed and the hook is only wired by the host afterwards.
            Assignee = node.Assignee,
            CreatedAt = node.CreatedAt,
            ClaimedAt = node.ClaimedAt,
            CompletedAt = node.CompletedAt,
        };
        item.Notes.AddRange(node.Notes);
        // Artifacts are part of the persisted board (unlike leases): workspace-relative by the tool
        // boundary's guarantee, so they stay meaningful across the very restart hydration serves.
        item.Artifacts.AddRange(node.Artifacts);
        // blockedBy round-trips too (#595, review 590/D-1): dotted ids stay stable across
        // hydration (DisplayIds are restored exactly), so the references keep pointing at the
        // same rows and RefuseIfBlocked keeps its force after a restart.
        item.BlockedBy.AddRange(node.BlockedBy);

        if (item.Status == TaskStatus.Blocked && item.BlockedBy.Count == 0)
        {
            // A row persisted Blocked by a build that did not round-trip blockedBy (pre-#595)
            // arrives with no restorable blockers: every claim guard would pass while the panel
            // still showed Blocked — exactly the Status/BlockedBy disagreement Requirement 8.5
            // exists to prevent. Downgrade so the hydrated state is honest about what it can
            // enforce; a real block can be re-established with block-task.
            item.Status = TaskStatus.NotStarted;
        }

        if (item.Status == TaskStatus.InProgress && item.Assignee != null && item.ClaimedAt == null)
        {
            // A claimed row from a snapshot persisted before claimedAt round-tripped (or with the
            // field stripped) must not read as freshly claimed forever: IsLeaseStale falls back to
            // "now" when both timestamps are absent, so the lease could never go stale and the row
            // would wedge if its agent is gone. Ageing it from the capture instant is the honest
            // floor — the claim is at least that old.
            item.ClaimedAt = capturedAtUtc;
        }

        var maxSubId = 0;
        foreach (var (subNode, index) in node.SubTasks.Select(static (subNode, index) => (subNode, index)))
        {
            var subItem = FromBoardNode(subNode, parentId: id, position: index + 1, capturedAtUtc);
            item.SubTasks.Add(subItem);
            maxSubId = Math.Max(maxSubId, subItem.Id);
        }

        item.NextSubTaskId = maxSubId + 1;
        return item;
    }

    /// <summary>
    ///     Reverse of <see cref="ToBoardNode" />'s status mapping, under the same discipline: member by
    ///     member, never a cast (a cast re-labels every row the day a member is inserted), throwing on
    ///     an unmapped member so the gap is a loud failure instead of a silently wrong board.
    /// </summary>
    private static TaskStatus FromBoardStatus(TodoTaskStatus status)
    {
        return status switch
        {
            TodoTaskStatus.NotStarted => TaskStatus.NotStarted,
            TodoTaskStatus.InProgress => TaskStatus.InProgress,
            TodoTaskStatus.Blocked => TaskStatus.Blocked,
            TodoTaskStatus.Completed => TaskStatus.Completed,
            TodoTaskStatus.Removed => TaskStatus.Removed,
            _ => throw new ArgumentOutOfRangeException(
                nameof(status),
                status,
                $"Unmapped {nameof(TodoTaskStatus)} value; add an explicit arm above rather than falling through."
            ),
        };
    }

    private string GetTaskCounts(string countType)
    {
        int total;
        int completed;
        int pending;
        int removed;

        // GetAllTasksFlat recurses through nested SubTasks lists that AddTask appends to
        // under _sync alone; a snapshot of the roots leaves that traversal unguarded.
        lock (_sync)
        {
            var allTasks = GetAllTasksFlat(_state.RootTasks);
            total = allTasks.Count;
            completed = allTasks.Count(t => t.Status == TaskStatus.Completed);
            pending = allTasks.Count(t => t.Status is TaskStatus.NotStarted or TaskStatus.InProgress);
            removed = allTasks.Count(t => t.Status == TaskStatus.Removed);
        }

        return countType switch
        {
            "total" => $"Total tasks: {total}",
            "completed" => $"Completed tasks: {completed}",
            "pending" => $"Pending tasks: {pending}",
            "removed" => $"Removed tasks: {removed}",
            _ => $"Task counts - Total: {total}, Completed: {completed}, Pending: {pending}, Removed: {removed}",
        };
    }

    private static string GetStatusSymbol(TaskStatus status)
    {
        return status switch
        {
            TaskStatus.NotStarted => "[ ]",
            TaskStatus.InProgress => "[-]",
            TaskStatus.Blocked => "[!]",
            TaskStatus.Completed => "[x]",
            TaskStatus.Removed => "[~]",
            _ => "[ ]",
        };
    }

    private static bool TryParseStatus(string input, out TaskStatus status)
    {
        var normalized = (input ?? string.Empty).Trim().ToLowerInvariant();
        switch (normalized)
        {
            case "not started":
            case "not_started":
            case "not-started":
            case "notstarted":
            case "todo":
            case "to do":
            case "to-do":
            case "to_do":
            case "pending":
                status = TaskStatus.NotStarted;
                return true;
            case "in progress":
            case "in_progress":
            case "in-progress":
            case "inprogress":
            case "doing":
                status = TaskStatus.InProgress;
                return true;
            case "blocked":
            case "block":
                status = TaskStatus.Blocked;
                return true;
            case "completed":
            case "done":
            case "complete":
                status = TaskStatus.Completed;
                return true;
            case "removed":
            case "deleted":
            case "remove":
            case "delete":
                status = TaskStatus.Removed;
                return true;
            default:
                status = TaskStatus.NotStarted;
                return false;
        }
    }

    private static string NormalizeStatusText(TaskStatus status)
    {
        return status switch
        {
            TaskStatus.NotStarted => "not started",
            TaskStatus.InProgress => "in progress",
            TaskStatus.Blocked => "blocked",
            TaskStatus.Completed => "completed",
            TaskStatus.Removed => "removed",
            _ => "not started",
        };
    }

    public class BulkTaskItem
    {
        public string Task { get; set; } = string.Empty;
        public List<string> SubTasks { get; set; } = [];
        public List<string> Notes { get; set; } = [];
    }

    /// <summary>
    ///     When a task was created, when it was last claimed, and when it was completed. All
    ///     three are optional so a task created before this type existed — or one whose lease
    ///     was never touched — deserializes with the fields it never had simply absent.
    /// </summary>
    public sealed record TaskTimestamps
    {
        [JsonPropertyName("createdAt")]
        public DateTimeOffset? CreatedAt { get; init; }

        [JsonPropertyName("claimedAt")]
        public DateTimeOffset? ClaimedAt { get; init; }

        [JsonPropertyName("completedAt")]
        public DateTimeOffset? CompletedAt { get; init; }
    }

    public record TaskItem
    {
        [JsonPropertyName("id")]
        public required string Id { get; init; } // Changed to string for hierarchical IDs like "1", "1.1", "1.2.1"

        [JsonPropertyName("status")]
        public required TaskStatus Status { get; init; } = TaskStatus.NotStarted;

        [JsonPropertyName("subTasks")]
        public required IList<TaskItem> SubTasks { get; init; } = ImmutableList<TaskItem>.Empty;

        [JsonPropertyName("title")]
        public required string Title { get; init; }

        [JsonPropertyName("notes")]
        public required IList<string> Notes { get; init; } = ImmutableList<string>.Empty;

        // Everything below is additive and optional: a list persisted before this field existed
        // deserializes with it simply absent, and round-trips unchanged.
        [JsonPropertyName("assignee")]
        public string? Assignee { get; init; }

        [JsonPropertyName("blockedBy")]
        public IList<string> BlockedBy { get; init; } = ImmutableList<string>.Empty;

        /// <summary>
        ///     Workspace-relative file paths attached via <c>attach-artifact</c>. Never host paths —
        ///     validated and normalized at the tool boundary. Never null; empty when none.
        /// </summary>
        [JsonPropertyName("artifacts")]
        public IList<string> Artifacts { get; init; } = ImmutableList<string>.Empty;

        [JsonPropertyName("times")]
        public TaskTimestamps? Times { get; init; }
    }

    private sealed record PrivateTaskItem
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("displayId")]
        public string DisplayId { get; set; } = string.Empty;

        [JsonPropertyName("title")]
        public string Title { get; set; } = string.Empty;

        [JsonPropertyName("status")]
        public TaskStatus Status { get; set; } = TaskStatus.NotStarted;

        [JsonPropertyName("notes")]
        public List<string> Notes { get; } = [];

        [JsonPropertyName("parentId")]
        public int? ParentId { get; set; }

        [JsonPropertyName("subTasks")]
        public List<PrivateTaskItem> SubTasks { get; } = [];

        [JsonPropertyName("nextSubTaskId")]
        public int NextSubTaskId { get; set; } = 1;

        // Additive, optional fields — missing on any pre-existing serialized task, and default
        // to null/empty rather than requiring a value, so old-shape JSON keeps loading.
        [JsonPropertyName("assignee")]
        public string? Assignee { get; set; }

        [JsonPropertyName("blockedBy")]
        public List<string> BlockedBy { get; set; } = [];

        [JsonPropertyName("artifacts")]
        public List<string> Artifacts { get; set; } = [];

        [JsonPropertyName("createdAt")]
        public DateTimeOffset? CreatedAt { get; set; }

        [JsonPropertyName("claimedAt")]
        public DateTimeOffset? ClaimedAt { get; set; }

        [JsonPropertyName("completedAt")]
        public DateTimeOffset? CompletedAt { get; set; }

        public TaskItem ToPublic()
        {
            TaskTimestamps? times = null;
            if (CreatedAt is not null || ClaimedAt is not null || CompletedAt is not null)
            {
                times = new TaskTimestamps
                {
                    CreatedAt = CreatedAt,
                    ClaimedAt = ClaimedAt,
                    CompletedAt = CompletedAt,
                };
            }

            return new TaskItem
            {
                Id = string.IsNullOrEmpty(DisplayId) ? Id.ToString() : DisplayId, // Use DisplayId for hierarchical IDs
                Title = Title,
                Status = Status,
                Notes = [.. Notes],
                SubTasks = [.. SubTasks.Select(st => st.ToPublic())],
                Assignee = Assignee,
                BlockedBy = [.. BlockedBy],
                Artifacts = [.. Artifacts],
                Times = times,
            };
        }
    }

    private sealed record ManagerState
    {
        [JsonPropertyName("rootTasks")]
        public List<PrivateTaskItem> RootTasks { get; set; } = [];

        [JsonPropertyName("nextId")]
        public int NextId { get; set; } = 1;

        /// <summary>
        ///     The id ledger behind the <c>TodoBoardIdVanished</c> detector (#621 Part B): every dotted
        ///     id this board has held and has not deliberately deleted, mapped to the instant it was last
        ///     known present. Round-trips with the rest of the state so the JSON persistence route keeps
        ///     the discrimination the detector depends on.
        /// </summary>
        [JsonPropertyName("seenIds")]
        public Dictionary<string, DateTimeOffset> SeenIds { get; set; } = [];
    }
}
