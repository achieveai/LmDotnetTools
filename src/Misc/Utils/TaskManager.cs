using System.Collections.Immutable;
using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Models;

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

    /// <summary>
    ///     A claim is a lease, not a hard lock (see the design doc's stale-row research): an
    ///     agent that goes quiet without releasing its claim would otherwise wedge the task
    ///     forever. Past this much time since <c>ClaimedAt</c>, a different agent's claim
    ///     attempt is allowed to take the lease over rather than being refused. There is no
    ///     background sweeper — staleness is derived on read, exactly when someone asks.
    /// </summary>
    private static readonly TimeSpan DefaultLeaseStaleAfter = TimeSpan.FromMinutes(15);

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
        [Description("Parent task ID for nesting (e.g., '1', '1.2', '1.2.3'). Omit for main task")]
            string? parentId = null,
        [Description(
            "Agent name to assign this task to. Omit on a subtask to inherit the parent task's assignee (if any); omit on a main task to leave it unassigned."
        )]
            string? assignee = null
    )
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
            }

            var addedTasks = new List<string>();
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
                addedTasks.Add($"Task {mainTask.DisplayId}: {mainTask.Title}");

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
                    }
                }
            }

            var result = new StringBuilder();

            if (clearExisting)
            {
                AppendLine(result, "Cleared existing tasks.");
            }

            if (addedTasks.Count > 0)
            {
                AppendLine(result, $"Added {addedTasks.Count} task(s):");
                foreach (var task in addedTasks)
                {
                    AppendLine(result, $"  - {task}");
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
• Verify subtasks are handled
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
        [Description("Task ID (e.g., '1', '1.2', '1.2.3')")] string taskId,
        [Description("New status: not started|in progress|completed|removed")] string status = "not started",
        [Description(
            "Your agent name. Passing it on an 'in progress' transition claims the task (see claim-task); leave it null to just flip status without touching the claim."
        )]
            string? agent = null
    )
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
                        $"Error: Task {targetTask.DisplayId} must be claimed and in progress before it can be completed. Use claim-task (or update-task with an agent) first."
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
        [Description("Task ID (e.g., '1', '1.2', '1.2.3')")] string taskId,
        [Description("Your agent name — recorded as the claim holder")] string agent
    )
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
        [Description("Task ID (e.g., '1', '1.2', '1.2.3')")] string taskId,
        [Description("Agent name to assign this task to")] string assignee
    )
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

            var trimmedAssignee = assignee.Trim();

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

Examples:
- Block on a dependency: {""taskId"": ""3"", ""blockedBy"": [""1""]}
- Block on several: {""taskId"": ""4"", ""blockedBy"": [""1"", ""2""]}
- Clear the block: {""taskId"": ""3"", ""blockedBy"": []}"
    )]
    public FunctionResult BlockTask(
        [Description("Task ID (e.g., '1', '1.2', '1.2.3')")] string taskId,
        [Description("Task IDs this task is blocked on. Pass an empty list to clear the block.")]
            List<string>? blockedBy = null
    )
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

            if (ids.Contains(task.DisplayId))
            {
                return FunctionResult.Error(
                    InvalidArgumentsCode,
                    $"Error: Task {task.DisplayId} cannot be listed as its own blocker."
                );
            }

            foreach (var id in ids)
            {
                var (blocker, blockerError) = FindTaskByStringId(id);
                if (blocker == null)
                {
                    return blockerError
                        ?? FunctionResult.Error(TaskNotFoundCode, $"Error: Blocking task '{id}' not found.");
                }
            }

            task.BlockedBy.Clear();
            task.BlockedBy.AddRange(ids);
            task.Status = TaskStatus.Blocked;
            // Not being actively worked while blocked; a live lease no longer means anything.
            task.ClaimedAt = null;

            return $"Task {task.DisplayId} is now blocked by {string.Join(", ", ids)}.";
        }
    }

    /// <summary>
    ///     Shared claim logic for <see cref="ClaimTask" /> and <see cref="UpdateTask(string, string, string?)" />
    ///     when it is asked to move a task to <see cref="TaskStatus.InProgress" /> on behalf of a
    ///     named agent. Must be called while holding <see cref="_sync" />.
    /// </summary>
    private FunctionResult? ApplyClaim(PrivateTaskItem task, string agent, DateTimeOffset now, out string note)
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
        [Description("Task ID (e.g., '1', '1.2', '1.2.3')")] string taskId,
        [Description("Subtask ID to delete specific subtask")] int? subtaskId = null
    )
    {
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
                        return FunctionResult.Error(
                            TaskNotFoundCode,
                            $"Error: Subtask {subtaskId.Value} not found under task {taskId}."
                        );
                    }

                    _ = task.SubTasks.Remove(subtask);
                }

                return $"Deleted subtask {subtaskId.Value} from task {taskId}: {subtask.Title}";
            }

            // Delete main task and all subtasks
            if (task == null)
            {
                return FunctionResult.Error(TaskNotFoundCode, $"Error: Task {taskId} not found.");
            }

            _ = RemoveTaskAndSubtasks(task);
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

Examples:
- Task: {""taskId"": 1}
- Subtask: {""taskId"": 1, ""subtaskId"": 3}"
    )]
    public FunctionResult GetTask(
        [Description("Task ID (e.g., '1', '1.2', '1.2.3')")] string taskId,
        [Description("Subtask ID for specific subtask")] int? subtaskId = null
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
        [Description("Task ID (e.g., '1', '1.2', '1.2.3')")] string taskId,
        [Description("Subtask ID if adding note to subtask (optional)")] int? subtaskId = null,
        [Description("Note text to add")] string noteText = ""
    )
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
        [Description("Task ID (e.g., '1', '1.2', '1.2.3')")] string taskId,
        [Description("Subtask ID if editing subtask note (optional)")] int? subtaskId = null,
        [Description("Note index to edit (1-based: 1 for first note, 2 for second, etc.)")] int noteIndex = 1,
        [Description("New text to replace the existing note")] string noteText = ""
    )
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
        [Description("Task ID (e.g., '1', '1.2', '1.2.3')")] string taskId,
        [Description("Subtask ID if deleting subtask note (optional)")] int? subtaskId = null,
        [Description("Note index to delete (1-based: 1 for first note, 2 for second, etc.)")] int noteIndex = 1
    )
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
        [Description("Task ID (e.g., '1', '1.2', '1.2.3')")] string taskId,
        [Description("Subtask ID for subtask notes")] int? subtaskId = null
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
• No filter - Full context for major decisions

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
        [Description("Filter by status: not started|in progress|blocked|completed|removed")] string? status = null,
        [Description("Show only main tasks (exclude subtasks)")] bool mainOnly = false
    )
    {
        TaskStatus? filterStatus = null;
        if (!string.IsNullOrEmpty(status))
        {
            if (!TryParseStatus(status, out var parsedStatus))
            {
                return FunctionResult.Error(
                    InvalidStatusCode,
                    "Error: Invalid status filter. Use: not started, in progress, blocked, completed, removed."
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
    ///     Captures the board for the conversation read path (<c>GET /todos</c>). Built on
    ///     <see cref="GetTasks" />, so it inherits that method's lock coverage and its display-id rule
    ///     rather than re-deriving either.
    /// </summary>
    public TodoBoardSnapshot GetTodoBoardSnapshot(string threadId)
    {
        return new TodoBoardSnapshot
        {
            ThreadId = threadId,
            CapturedAtUtc = DateTimeOffset.UtcNow,
            Tasks = [.. GetTasks().Select(ToBoardNode)],
        };
    }

    /// <summary>
    ///     Projects one task and its subtree onto the transport-facing board shape.
    /// </summary>
    /// <remarks>
    ///     The status mapping is written out member by member rather than cast across the two enums. They
    ///     are structurally identical today and a cast would work; it would also stop working silently the
    ///     day a member is INSERTED into one of them, re-labelling every persisted and in-flight row by
    ///     one position. Spelling it out makes that day a compile error instead.
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
                TaskStatus.Completed => TodoTaskStatus.Completed,
                TaskStatus.Removed => TodoTaskStatus.Removed,
                _ => TodoTaskStatus.NotStarted,
            },
            Title = task.Title,
            Notes = [.. task.Notes],
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
                    return (
                        null,
                        string.Empty,
                        FunctionResult.Error(
                            TaskNotFoundCode,
                            $"Error: Subtask {subtaskId.Value} not found under task {taskId}."
                        )
                    );
                }

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
                    return (null, FunctionResult.Error(TaskNotFoundCode, $"Error: Task '{path}' not found."));
                }

                currentTask = nextTask;
            }

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
    }
}
