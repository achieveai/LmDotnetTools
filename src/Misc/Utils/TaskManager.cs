using System.Collections.Immutable;
using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AchieveAi.LmDotnetTools.LmCore.Core;

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
public class TaskManager
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum TaskStatus
    {
        NotStarted,
        InProgress,
        Completed,
        Removed,
    }

    private readonly ManagerState _state;
    private readonly object _sync = new();

    // Thread-safe collections
    public TaskManager()
        : this(new ManagerState()) { }

    private TaskManager(ManagerState state)
    {
        _state = state;
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

Hierarchy guidelines:
• Level 1: Major phases or components
• Level 2: Concrete deliverables or milestones
• Level 3+: Specific implementation steps
• Deeper nesting for complex subtasks that need isolation

Examples:
- Main phase: {""title"": ""Design API""}
- Breakdown: {""title"": ""Define endpoints"", ""parentId"": ""1""}
- Discovered task: {""title"": ""Add rate limiting"", ""parentId"": ""1""}  // Added after learning
- Deep detail: {""title"": ""Validate JWT tokens"", ""parentId"": ""1.2.3""}"
    )]
    public string AddTask(
        [Description("Task title/description")] string title,
        [Description("Parent task ID for nesting (e.g., '1', '1.2', '1.2.3'). Omit for main task")]
            string? parentId = null
    )
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return "Error: Title cannot be empty.";
        }

        // An omitted parentId means "make this a main task". A supplied-but-blank one is a
        // malformed call, and silently promoting it to a root task hides the mistake behind
        // a success message.
        if (parentId != null && string.IsNullOrWhiteSpace(parentId))
        {
            return "Error: Parent task ID cannot be blank. Omit parentId to add a main task.";
        }

        lock (_sync)
        {
            PrivateTaskItem task;

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
                };

                _state.RootTasks.Add(task);
                return $"Added task {task.DisplayId}: {task.Title}";
            }

            // Parse parent ID and find parent task
            var (parentTask, error) = FindTaskByStringId(parentId);
            if (parentTask == null)
            {
                return error ?? $"Error: Parent task '{parentId}' not found.";
            }

            // Create subtask with hierarchical ID
            var subtaskId = parentTask.NextSubTaskId++;
            task = new PrivateTaskItem
            {
                Id = subtaskId,
                DisplayId = $"{parentTask.DisplayId}.{subtaskId}",
                Title = title.Trim(),
                Status = TaskStatus.NotStarted,
                ParentId = parentTask.Id,
            };

            parentTask.SubTasks.Add(task);
            return $"Added task {task.DisplayId}: {task.Title}";
        }
    }

    public string AddTask(string title, int parentId)
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
    public string BulkInitialize(
        [Description("List of tasks with their subtasks and notes")] List<BulkTaskItem> tasks,
        [Description("Clear all existing tasks before adding new ones")] bool clearExisting = false
    )
    {
        if (tasks == null || tasks.Count == 0)
        {
            return "Error: No tasks provided for initialization.";
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
• 'not started' → 'in progress': Commitment to focus
• 'in progress' → 'completed': Achievement and learning opportunity
• Any → 'removed': Conscious decision to pivot

WIP (Work In Progress) Limits:
• Keep only 1-3 tasks 'in progress' simultaneously
• Complete or pause before starting new work
• This prevents context switching and maintains quality

Before marking complete:
• Add notes about what was learned
• Verify subtasks are handled
• Consider if follow-up tasks are needed

Status meanings:
• not started: Planned but not begun (the backlog)
• in progress: Actively working (limit these!)
• completed: Done and learned from (celebrate!)
• removed: No longer needed (adapted plan)

Examples:
- Start work: {""taskId"": ""1"", ""status"": ""in progress""}
- Finish task: {""taskId"": ""1.3"", ""status"": ""completed""}
- Abandon approach: {""taskId"": ""2.1"", ""status"": ""removed""}"
    )]
    public string UpdateTask(
        [Description("Task ID (e.g., '1', '1.2', '1.2.3')")] string taskId,
        [Description("New status: not started|in progress|completed|removed")] string status = "not started"
    )
    {
        lock (_sync)
        {
            // Find target task using string ID
            var (targetTask, error) = FindTaskByStringId(taskId);
            if (targetTask == null)
            {
                return error!;
            }

            // Update status
            if (!TryParseStatus(status, out var newStatus))
            {
                return "Error: Invalid status. Use: not started, in progress, completed, removed.";
            }

            targetTask.Status = newStatus;

            return $"Updated task {targetTask.DisplayId} status to '{NormalizeStatusText(newStatus)}'.";
        }
    }

    public string UpdateTask(int taskId, string status = "not started")
    {
        return UpdateTask(taskId.ToString(), status);
    }

    public string UpdateTask(int taskId, int subtaskId, string status = "not started")
    {
        return UpdateTask($"{taskId}.{subtaskId}", status);
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
    public string DeleteTask(
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
                    return $"Error: Parent task {taskId} not found.";
                }

                PrivateTaskItem? subtask;
                lock (task.SubTasks)
                {
                    subtask = task.SubTasks.FirstOrDefault(st => st.Id == subtaskId.Value);
                    if (subtask == null)
                    {
                        return $"Error: Subtask {subtaskId.Value} not found under task {taskId}.";
                    }

                    _ = task.SubTasks.Remove(subtask);
                }

                return $"Deleted subtask {subtaskId.Value} from task {taskId}: {subtask.Title}";
            }

            // Delete main task and all subtasks
            if (task == null)
            {
                return $"Error: Task {taskId} not found.";
            }

            _ = RemoveTaskAndSubtasks(task);
            return $"Deleted task {taskId} and all subtasks: {task.Title}";
        }
    }

    public string DeleteTask(int taskId, int? subtaskId = null)
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
    public string GetTask(
        [Description("Task ID (e.g., '1', '1.2', '1.2.3')")] string taskId,
        [Description("Subtask ID for specific subtask")] int? subtaskId = null
    )
    {
        lock (_sync)
        {
            var (task, taskRef, error) = FindTaskWithReference(taskId, subtaskId);
            return task == null ? error! : FormatTaskDetails(task, taskRef);
        }
    }

    public string GetTask(int taskId, int? subtaskId = null)
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
    public string AddNote(
        [Description("Task ID (e.g., '1', '1.2', '1.2.3')")] string taskId,
        [Description("Subtask ID if adding note to subtask (optional)")] int? subtaskId = null,
        [Description("Note text to add")] string noteText = ""
    )
    {
        if (string.IsNullOrWhiteSpace(noteText))
        {
            return "Error: Note text cannot be empty.";
        }

        lock (_sync)
        {
            var (targetTask, taskRef, error) = FindTaskWithReference(taskId, subtaskId);
            if (targetTask == null)
            {
                return error!;
            }

            lock (targetTask.Notes)
            {
                targetTask.Notes.Add(noteText.Trim());
            }

            return $"Added note to {taskRef}.";
        }
    }

    public string AddNote(int taskId, int? subtaskId = null, string noteText = "")
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
    public string EditNote(
        [Description("Task ID (e.g., '1', '1.2', '1.2.3')")] string taskId,
        [Description("Subtask ID if editing subtask note (optional)")] int? subtaskId = null,
        [Description("Note index to edit (1-based: 1 for first note, 2 for second, etc.)")] int noteIndex = 1,
        [Description("New text to replace the existing note")] string noteText = ""
    )
    {
        if (string.IsNullOrWhiteSpace(noteText))
        {
            return "Error: Note text cannot be empty.";
        }

        lock (_sync)
        {
            var (targetTask, taskRef, error) = FindTaskWithReference(taskId, subtaskId);
            if (targetTask == null)
            {
                return error!;
            }

            lock (targetTask.Notes)
            {
                if (noteIndex < 1 || noteIndex > targetTask.Notes.Count)
                {
                    return $"Error: Note index {noteIndex} out of range. {taskRef} has {targetTask.Notes.Count} note(s).";
                }

                targetTask.Notes[noteIndex - 1] = noteText.Trim();
            }

            return $"Updated note #{noteIndex} on {taskRef}.";
        }
    }

    public string EditNote(int taskId, int? subtaskId = null, int noteIndex = 1, string noteText = "")
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
    public string DeleteNote(
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
                return error!;
            }

            lock (targetTask.Notes)
            {
                if (noteIndex < 1 || noteIndex > targetTask.Notes.Count)
                {
                    return $"Error: Note index {noteIndex} out of range. {taskRef} has {targetTask.Notes.Count} note(s).";
                }

                var deletedNote = targetTask.Notes[noteIndex - 1];
                targetTask.Notes.RemoveAt(noteIndex - 1);
                return $"Deleted note #{noteIndex} from {taskRef}: \"{deletedNote}\".";
            }
        }
    }

    public string DeleteNote(int taskId, int? subtaskId = null, int noteIndex = 1)
    {
        return DeleteNote(taskId.ToString(), subtaskId, noteIndex);
    }

    public string ManageNotes(
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
            "edit" => EditNote(taskId, subtaskId, noteIndex, noteText)
                .Replace($"Updated note #{noteIndex} on", $"Edited note {noteIndex} on", StringComparison.Ordinal),
            "delete" => DeleteNote(taskId, subtaskId, noteIndex)
                .Replace($"Deleted note #{noteIndex} from", $"Deleted note {noteIndex} from", StringComparison.Ordinal),
            _ => "Error: Invalid action. Use: add, edit, delete.",
        };
    }

    public string ManageNotes(
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
    public string ListNotes(
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
                return error!;
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

    public string ListNotes(int taskId, int? subtaskId = null)
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

Filtering strategies:
• status='in progress' - Focus on current work (WIP limit)
• status='not started' - Plan next moves
• mainOnly=true - See the big picture without details
• No filter - Full context for major decisions

Healthy patterns:
• 1-3 tasks 'in progress' at once (focus)
• Regular completed tasks (momentum)
• Evolving 'not started' list (adaptation)
• Notes on completed tasks (learning capture)

Examples:
- Next action: {""status"": ""not started"", ""mainOnly"": false}
- WIP check: {""status"": ""in progress""}
- Overview: {""mainOnly"": true}"
    )]
    public string ListTasks(
        [Description("Filter by status: not started|in progress|completed|removed")] string? status = null,
        [Description("Show only main tasks (exclude subtasks)")] bool mainOnly = false
    )
    {
        List<PrivateTaskItem> rootTasksCopy;
        lock (_sync)
        {
            rootTasksCopy = [.. _state.RootTasks];
        }

        TaskStatus? filterStatus = null;
        if (!string.IsNullOrEmpty(status))
        {
            if (!TryParseStatus(status, out var parsedStatus))
            {
                return "Error: Invalid status filter. Use: not started, in progress, completed, removed.";
            }

            filterStatus = parsedStatus;
        }

        var sb = new StringBuilder();

        // Count tasks by status for summary
        var allTasks = GetAllTasksFlat(rootTasksCopy);
        var notStartedCount = allTasks.Count(t => t.Status == TaskStatus.NotStarted);
        var inProgressCount = allTasks.Count(t => t.Status == TaskStatus.InProgress);
        var completedCount = allTasks.Count(t => t.Status == TaskStatus.Completed);
        var totalActive = notStartedCount + inProgressCount;

        // Beautiful header with task summary
        AppendLine(sb, "# 📋 Task List");
        if (filterStatus == null && !mainOnly)
        {
            AppendLine(sb);
            AppendLine(
                sb,
                $"**Status**: {inProgressCount} in progress | {notStartedCount} pending | {completedCount} completed"
            );
            AppendLine(sb, $"**Total**: {totalActive} active tasks");
        }

        AppendLine(sb);

        // Render the body separately so "nothing to show" is decided by what was actually
        // written, not by sniffing the tail of the assembled string.
        var body = new StringBuilder();
        foreach (var task in rootTasksCopy)
        {
            AppendTaskMarkdown(body, task, 0, filterStatus, mainOnly);
        }

        // An empty list still gets the header — a bare "No tasks found." gives the model no
        // clue which tool answered it.
        _ = body.Length == 0
            ? sb.Append(rootTasksCopy.Count == 0 ? "No tasks found." : "No tasks match the specified criteria.")
            : sb.Append(body);

        return sb.ToString().TrimEnd();
    }

    public IList<TaskItem> GetTasks()
    {
        lock (_sync)
        {
            return [.. _state.RootTasks.Select(t => t.ToPublic())];
        }
    }

    [Function(
        "search-tasks",
        @"Search by title or get plan statistics to validate completion criteria.

Examples:
- Find 'plan' tasks: {""searchTerm"": ""plan""}
- Completed count: {""countType"": ""completed""}
- Pending count: {""countType"": ""pending""}"
    )]
    public string SearchTasks(
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
            return "Error: Provide searchTerm or countType.";
        }

        var matches = new List<(PrivateTaskItem task, string path)>();

        List<PrivateTaskItem> rootTasksCopy;
        lock (_sync)
        {
            rootTasksCopy = [.. _state.RootTasks];
        }

        foreach (var task in rootTasksCopy)
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

    public string GetMarkdown()
    {
        return ListTasks();
    }

    // Helper methods

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
    private (PrivateTaskItem? task, string taskRef, string? error) FindTaskWithReference(string taskId, int? subtaskId)
    {
        lock (_sync)
        {
            var (task, error) = FindTaskByStringId(taskId);

            if (task == null)
            {
                return (null, string.Empty, error ?? $"Error: Task {taskId} not found.");
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
                    return (null, string.Empty, $"Error: Subtask {subtaskId.Value} not found under task {taskId}.");
                }

                return (subtask, $"subtask {subtaskId.Value} of task {taskId}", null);
            }

            return (task, $"task {taskId}", null);
        }
    }

    private (PrivateTaskItem? task, string? error) FindTaskByStringId(string taskId)
    {
        lock (_sync)
        {
            // Parse hierarchical ID like "1", "1.2", "1.2.3"
            var parts = (taskId ?? string.Empty).Split('.');
            if (parts.Length == 0 || !int.TryParse(parts[0], out var rootId))
            {
                return (null, $"Error: Invalid task ID format '{taskId}'.");
            }

            // Find root task
            var currentTask = _state.RootTasks.FirstOrDefault(t => t.Id == rootId);
            if (currentTask == null)
            {
                return (null, $"Error: Task '{parts[0]}' not found.");
            }

            // Navigate through subtask hierarchy
            for (var i = 1; i < parts.Length; i++)
            {
                if (!int.TryParse(parts[i], out var subId))
                {
                    return (null, $"Error: Invalid subtask ID '{parts[i]}' in '{taskId}'.");
                }

                PrivateTaskItem? nextTask = null;
                lock (currentTask.SubTasks)
                {
                    nextTask = currentTask.SubTasks.FirstOrDefault(st => st.Id == subId);
                }

                if (nextTask == null)
                {
                    var path = string.Join(".", parts.Take(i + 1));
                    return (null, $"Error: Task '{path}' not found.");
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
        AppendLine(
            sb,
            $"{indent}{statusSymbol} {taskNumber}. {task.Title}{(task.Status == TaskStatus.Removed ? " (removed)" : string.Empty)}"
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
                AppendTaskMarkdown(sb, sub, level + 1, filterStatus, mainOnly);
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
        var state = json.Deserialize<ManagerState>();
        return state == null ? new TaskManager() : new TaskManager(state);
    }

    public static TaskManager DeserializeTasks(string json)
    {
        var tasks = JsonSerializer.Deserialize<ManagerState>(json);
        return tasks == null ? new TaskManager() : new TaskManager(tasks);
    }

    private string GetTaskCounts(string countType)
    {
        List<PrivateTaskItem> rootTasksCopy;
        lock (_sync)
        {
            rootTasksCopy = [.. _state.RootTasks];
        }

        var allTasks = GetAllTasksFlat(rootTasksCopy);
        var total = allTasks.Count;
        var completed = allTasks.Count(t => t.Status == TaskStatus.Completed);
        var pending = allTasks.Count(t => t.Status is TaskStatus.NotStarted or TaskStatus.InProgress);
        var removed = allTasks.Count(t => t.Status == TaskStatus.Removed);

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

        public TaskItem ToPublic()
        {
            return new TaskItem
            {
                Id = string.IsNullOrEmpty(DisplayId) ? Id.ToString() : DisplayId, // Use DisplayId for hierarchical IDs
                Title = Title,
                Status = Status,
                Notes = [.. Notes],
                SubTasks = [.. SubTasks.Select(st => st.ToPublic())],
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
