using System.ComponentModel;
using System.Text;
using AchieveAi.LmDotnetTools.LmCore.Agents;

namespace AchieveAi.LmDotnetTools.Misc.Utils;

public class TaskManager
{
  private enum TaskStatus
  {
    NotStarted,
    InProgress,
    Completed,
    Removed
  }

  private sealed class TaskItem
  {
    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public TaskStatus Status { get; set; } = TaskStatus.NotStarted;
    public List<string> Notes { get; } = new();
    public int? ParentId { get; set; }
    public List<TaskItem> SubTasks { get; } = new();
  }

  private readonly List<TaskItem> _rootTasks = new();
  private readonly Dictionary<int, TaskItem> _tasksById = new();
  private int _nextId = 1;

  [Function("add-task", @"Create a main task or a subtask to track plan steps.
Use when converting plan items into executable tasks.

Examples:
- Main task: {""title"": ""Draft project plan""}
- Subtask under task 1: {""title"": ""Outline sections"", ""parentId"": 1}

Notes:
- Only two levels are supported (task → subtask).")]
  public string AddTask(
      [Description("Task title")] string title,
      [Description("Parent task ID for subtask")] int? parentId = null)
  {
    if (string.IsNullOrWhiteSpace(title))
    {
      return "Error: Title cannot be empty.";
    }

    var task = new TaskItem
    {
      Id = _nextId++,
      Title = title.Trim(),
      Status = TaskStatus.NotStarted,
      ParentId = parentId
    };

    if (parentId is null)
    {
      _rootTasks.Add(task);
      _tasksById[task.Id] = task;
      return $"Added task {task.Id}: {task.Title}";
    }

    if (!_tasksById.TryGetValue(parentId.Value, out var parent))
    {
      return $"Error: Parent task {parentId.Value} not found.";
    }

    if (parent.ParentId is not null)
    {
      return $"Error: Only two levels supported. Task {parent.Id} is already a subtask.";
    }

    parent.SubTasks.Add(task);
    _tasksById[task.Id] = task;
    return $"Added subtask {task.Id} under task {parent.Id}: {task.Title}";
  }

  [Function("update-task", @"Update task or subtask to advance plan execution.
Use after completing a step or changing scope/title.

Examples:
- Set task 1 to in-progress: {""taskId"": 1, ""status"": ""in progress""}
- Complete subtask 3 under task 1: {""taskId"": 1, ""subtaskId"": 3, ""status"": ""completed""}
- Rename task 2 and mark done: {""taskId"": 2, ""title"": ""Finalize project plan"", ""status"": ""completed""}")]
  public string UpdateTask(
      [Description("Task ID")] int taskId,
      [Description("Subtask ID if updating subtask")] int? subtaskId = null,
      [Description("New status: not started|in progress|completed|removed")] string? status = null,
      [Description("New title")] string? title = null)
  {
    // Find target task
    TaskItem? targetTask = null;
    string taskRef;

    if (subtaskId.HasValue)
    {
      // Update subtask
      if (!_tasksById.TryGetValue(taskId, out var parentTask))
        return $"Error: Parent task {taskId} not found.";

      targetTask = parentTask.SubTasks.FirstOrDefault(st => st.Id == subtaskId.Value);
      if (targetTask == null)
        return $"Error: Subtask {subtaskId.Value} not found under task {taskId}.";
      taskRef = $"subtask {subtaskId.Value} of task {taskId}";
    }
    else
    {
      // Update main task
      if (!_tasksById.TryGetValue(taskId, out targetTask))
        return $"Error: Task {taskId} not found.";
      taskRef = $"task {taskId}";
    }

    var updates = new List<string>();

    // Update status if provided
    if (!string.IsNullOrEmpty(status))
    {
      if (TryParseStatus(status, out var newStatus))
      {
        targetTask.Status = newStatus;
        updates.Add($"status to '{NormalizeStatusText(newStatus)}'");
      }
      else
      {
        return "Error: Invalid status. Use: not started, in progress, completed, removed.";
      }
    }

    // Update title if provided
    if (!string.IsNullOrEmpty(title))
    {
      targetTask.Title = title.Trim();
      updates.Add($"title to '{targetTask.Title}'");
    }

    if (updates.Count == 0)
      return "Error: No updates specified. Provide status and/or title.";

    return $"Updated {taskRef}: {string.Join(", ", updates)}.";
  }

  [Function("delete-task", @"Delete a task or a specific subtask when the plan changes.
Use to remove obsolete/mistaken items.

Examples:
- Delete subtask 2 under task 1: {""taskId"": 1, ""subtaskId"": 2}
- Delete task 3 and its subtasks: {""taskId"": 3}")]
  public string DeleteTask(
      [Description("Task ID")] int taskId,
      [Description("Subtask ID to delete specific subtask")] int? subtaskId = null)
  {
    if (subtaskId.HasValue)
    {
      // Delete subtask
      if (!_tasksById.TryGetValue(taskId, out var parentTask))
        return $"Error: Parent task {taskId} not found.";

      var subtask = parentTask.SubTasks.FirstOrDefault(st => st.Id == subtaskId.Value);
      if (subtask == null)
        return $"Error: Subtask {subtaskId.Value} not found under task {taskId}.";

      parentTask.SubTasks.Remove(subtask);
      _tasksById.Remove(subtask.Id);
      return $"Deleted subtask {subtaskId.Value} from task {taskId}: {subtask.Title}";
    }
    else
    {
      // Delete main task and all subtasks
      if (!_tasksById.TryGetValue(taskId, out var task))
        return $"Error: Task {taskId} not found.";

      _rootTasks.Remove(task);
      RemoveTaskAndSubtasks(task);
      return $"Deleted task {taskId} and all subtasks: {task.Title}";
    }
  }

  [Function("get-task", @"Retrieve details to verify prerequisites or next steps.
Use before acting, to confirm status/notes/subtasks.

Examples:
- Task: {""taskId"": 1}
- Subtask: {""taskId"": 1, ""subtaskId"": 3}")]
  public string GetTask(
      [Description("Task ID")] int taskId,
      [Description("Subtask ID for specific subtask")] int? subtaskId = null)
  {
    if (subtaskId.HasValue)
    {
      // Get subtask details
      if (!_tasksById.TryGetValue(taskId, out var parentTask))
        return $"Error: Parent task {taskId} not found.";

      var subtask = parentTask.SubTasks.FirstOrDefault(st => st.Id == subtaskId.Value);
      if (subtask == null)
        return $"Error: Subtask {subtaskId.Value} not found under task {taskId}.";

      return FormatTaskDetails(subtask, $"Subtask {subtaskId.Value} of Task {taskId}");
    }
    else
    {
      // Get main task details
      if (!_tasksById.TryGetValue(taskId, out var task))
        return $"Error: Task {taskId} not found.";

      return FormatTaskDetails(task, $"Task {taskId}");
    }
  }

  [Function("manage-notes", @"Add, edit, or delete notes to capture reasoning state.
Use to persist decisions, constraints, or context between steps.

Examples:
- Add note to task 1: {""taskId"": 1, ""action"": ""add"", ""noteText"": ""Scope agreed""}
- Edit note #2 on subtask 3 of task 1:
  {""taskId"": 1, ""subtaskId"": 3, ""action"": ""edit"", ""noteIndex"": 2, ""noteText"": ""Updated detail""}
- Delete note #1 on task 2: {""taskId"": 2, ""action"": ""delete"", ""noteIndex"": 1}")]
  public string ManageNotes(
      [Description("Task ID")] int taskId,
      [Description("Subtask ID if managing subtask notes")] int? subtaskId = null,
      [Description("Note text to add, or new text to replace")] string? noteText = null,
      [Description("Note index (1-based) to edit/delete")] int? noteIndex = null,
      [Description("Action: add|edit|delete")] string action = "add")
  {
    // Find target task
    TaskItem? targetTask = null;
    string taskRef;

    if (subtaskId.HasValue)
    {
      if (!_tasksById.TryGetValue(taskId, out var parentTask))
        return $"Error: Parent task {taskId} not found.";

      targetTask = parentTask.SubTasks.FirstOrDefault(st => st.Id == subtaskId.Value);
      if (targetTask == null)
        return $"Error: Subtask {subtaskId.Value} not found under task {taskId}.";
      taskRef = $"subtask {subtaskId.Value} of task {taskId}";
    }
    else
    {
      if (!_tasksById.TryGetValue(taskId, out targetTask))
        return $"Error: Task {taskId} not found.";
      taskRef = $"task {taskId}";
    }

    switch (action.ToLowerInvariant())
    {
      case "add":
        if (string.IsNullOrWhiteSpace(noteText))
          return "Error: Note text required for add action.";
        targetTask.Notes.Add(noteText.Trim());
        return $"Added note to {taskRef}.";

      case "edit":
        if (!noteIndex.HasValue || string.IsNullOrWhiteSpace(noteText))
          return "Error: Note index and new text required for edit action.";
        if (noteIndex.Value < 1 || noteIndex.Value > targetTask.Notes.Count)
          return $"Error: Note index {noteIndex.Value} out of range (1-{targetTask.Notes.Count}).";
        targetTask.Notes[noteIndex.Value - 1] = noteText.Trim();
        return $"Edited note {noteIndex.Value} on {taskRef}.";

      case "delete":
        if (!noteIndex.HasValue)
          return "Error: Note index required for delete action.";
        if (noteIndex.Value < 1 || noteIndex.Value > targetTask.Notes.Count)
          return $"Error: Note index {noteIndex.Value} out of range (1-{targetTask.Notes.Count}).";
        targetTask.Notes.RemoveAt(noteIndex.Value - 1);
        return $"Deleted note {noteIndex.Value} from {taskRef}.";

      default:
        return "Error: Invalid action. Use: add, edit, delete.";
    }
  }

  [Function("list-notes", @"List all notes to recall context for the next step.

Examples:
- Task notes: {""taskId"": 1}
- Subtask notes: {""taskId"": 1, ""subtaskId"": 3}")]
  public string ListNotes(
      [Description("Task ID")] int taskId,
      [Description("Subtask ID for subtask notes")] int? subtaskId = null)
  {
    // Find target task
    TaskItem? targetTask = null;
    string taskRef;

    if (subtaskId.HasValue)
    {
      if (!_tasksById.TryGetValue(taskId, out var parentTask))
        return $"Error: Parent task {taskId} not found.";

      targetTask = parentTask.SubTasks.FirstOrDefault(st => st.Id == subtaskId.Value);
      if (targetTask == null)
        return $"Error: Subtask {subtaskId.Value} not found under task {taskId}.";
      taskRef = $"Subtask {subtaskId.Value} of Task {taskId}";
    }
    else
    {
      if (!_tasksById.TryGetValue(taskId, out targetTask))
        return $"Error: Task {taskId} not found.";
      taskRef = $"Task {taskId}";
    }

    if (targetTask.Notes.Count == 0)
      return $"{taskRef} has no notes.";

    var sb = new StringBuilder();
    sb.AppendLine($"Notes for {taskRef}: {targetTask.Title}");
    for (int i = 0; i < targetTask.Notes.Count; i++)
    {
      sb.AppendLine($"{i + 1}. {targetTask.Notes[i]}");
    }
    return sb.ToString().TrimEnd();
  }

  [Function("list-tasks", @"List the plan with optional filters to choose the next action.

Examples:
- All tasks: {}
- Only in-progress: {""status"": ""in progress""}
- Only main tasks: {""mainOnly"": true}
- Completed main tasks: {""status"": ""completed"", ""mainOnly"": true}")]
  public string ListTasks(
      [Description("Filter by status: not started|in progress|completed|removed")] string? status = null,
      [Description("Show only main tasks (exclude subtasks)")] bool mainOnly = false)
  {
    if (_rootTasks.Count == 0)
      return "No tasks found.";

    TaskStatus? filterStatus = null;
    if (!string.IsNullOrEmpty(status))
    {
      if (!TryParseStatus(status, out var parsedStatus))
        return "Error: Invalid status filter. Use: not started, in progress, completed, removed.";
      filterStatus = parsedStatus;
    }

    var sb = new StringBuilder();
    sb.AppendLine("# TODO");

    foreach (var task in _rootTasks)
    {
      AppendTaskMarkdown(sb, task, level: 0, filterStatus, mainOnly);
    }

    var result = sb.ToString().TrimEnd();
    return result == "# TODO" ? "No tasks match the specified criteria." : result;
  }

  [Function("search-tasks", @"Search by title or get plan statistics to validate completion criteria.

Examples:
- Find 'plan' tasks: {""searchTerm"": ""plan""}
- Completed count: {""countType"": ""completed""}
- Pending count: {""countType"": ""pending""}")]
  public string SearchTasks(
      [Description("Search term for title")] string? searchTerm = null,
      [Description("Get counts: total|completed|pending|removed")] string? countType = null)
  {
    if (!string.IsNullOrEmpty(countType))
    {
      return GetTaskCounts(countType.ToLowerInvariant());
    }

    if (string.IsNullOrWhiteSpace(searchTerm))
      return "Error: Provide searchTerm or countType.";

    var matches = new List<(TaskItem task, string path)>();
    foreach (var task in _rootTasks)
    {
      SearchTaskRecursive(task, searchTerm.Trim(), task.Id.ToString(), matches);
    }

    if (matches.Count == 0)
      return $"No tasks found matching '{searchTerm}'.";

    var sb = new StringBuilder();
    sb.AppendLine($"Found {matches.Count} task(s) matching '{searchTerm}':");
    foreach (var (task, path) in matches)
    {
      var statusSymbol = GetStatusSymbol(task.Status);
      sb.AppendLine($"- {statusSymbol} {path}: {task.Title}");
    }
    return sb.ToString().TrimEnd();
  }

  public string GetMarkdown()
  {
    return ListTasks();
  }

  /// <summary>
  /// Gets a compact summary of current task state for forwarding to next tool calls.
  /// Only includes active (not completed/removed) tasks.
  /// </summary>
  public string GetCurrentTaskContext()
  {
    if (_rootTasks.Count == 0)
      return "";

    var activeTasks = _rootTasks.Where(t => t.Status != TaskStatus.Completed && t.Status != TaskStatus.Removed).ToList();
    if (!activeTasks.Any())
      return "";

    var sb = new StringBuilder();
    sb.AppendLine("Current Tasks:");

    foreach (var task in activeTasks)
    {
      var statusSymbol = GetStatusSymbol(task.Status);
      sb.AppendLine($"{statusSymbol} Task {task.Id}: {task.Title}");

      // Include active subtasks
      var activeSubtasks = task.SubTasks.Where(st => st.Status != TaskStatus.Completed && st.Status != TaskStatus.Removed).ToList();
      foreach (var subtask in activeSubtasks)
      {
        var subtaskStatusSymbol = GetStatusSymbol(subtask.Status);
        sb.AppendLine($"  {subtaskStatusSymbol} {subtask.Id}: {subtask.Title}");
      }
    }

    return sb.ToString().TrimEnd();
  }

  // Helper methods
  private void RemoveTaskAndSubtasks(TaskItem task)
  {
    _tasksById.Remove(task.Id);
    foreach (var subtask in task.SubTasks)
    {
      _tasksById.Remove(subtask.Id);
    }
  }

  private string FormatTaskDetails(TaskItem task, string header)
  {
    var sb = new StringBuilder();
    sb.AppendLine($"{header}: {task.Title}");
    sb.AppendLine($"Status: {NormalizeStatusText(task.Status)}");

    if (task.Notes.Count > 0)
    {
      sb.AppendLine($"Notes ({task.Notes.Count}):");
      for (int i = 0; i < task.Notes.Count; i++)
      {
        sb.AppendLine($"  {i + 1}. {task.Notes[i]}");
      }
    }

    if (task.SubTasks.Count > 0)
    {
      sb.AppendLine($"Subtasks ({task.SubTasks.Count}):");
      foreach (var subtask in task.SubTasks)
      {
        var statusSymbol = GetStatusSymbol(subtask.Status);
        sb.AppendLine($"  {statusSymbol} {subtask.Id}. {subtask.Title}");
      }
    }

    return sb.ToString().TrimEnd();
  }

  private void AppendTaskMarkdown(StringBuilder sb, TaskItem task, int level, TaskStatus? filterStatus = null, bool mainOnly = false)
  {
    if (filterStatus.HasValue && task.Status != filterStatus.Value)
      return;

    var indent = new string(' ', level * 2);
    var statusSymbol = GetStatusSymbol(task.Status);
    sb.AppendLine($"{indent}- {statusSymbol} {task.Id}. {task.Title}{(task.Status == TaskStatus.Removed ? " (removed)" : string.Empty)}");

    if (task.Notes.Count > 0)
    {
      sb.AppendLine($"{indent}  Notes:");
      for (int i = 0; i < task.Notes.Count; i++)
      {
        sb.AppendLine($"{indent}  - {i + 1} {task.Notes[i]}");
      }
    }

    if (!mainOnly)
    {
      foreach (var sub in task.SubTasks)
      {
        AppendTaskMarkdown(sb, sub, level + 1, filterStatus, mainOnly);
      }
    }
  }

  private void SearchTaskRecursive(TaskItem task, string searchTerm, string path, List<(TaskItem, string)> matches)
  {
    if (task.Title.Contains(searchTerm, StringComparison.OrdinalIgnoreCase))
    {
      matches.Add((task, path));
    }

    for (int i = 0; i < task.SubTasks.Count; i++)
    {
      var subtask = task.SubTasks[i];
      SearchTaskRecursive(subtask, searchTerm, $"{path}.{subtask.Id}", matches);
    }
  }

  private string GetTaskCounts(string countType)
  {
    var total = _tasksById.Count;
    var completed = _tasksById.Values.Count(t => t.Status == TaskStatus.Completed);
    var pending = _tasksById.Values.Count(t => t.Status == TaskStatus.NotStarted || t.Status == TaskStatus.InProgress);
    var removed = _tasksById.Values.Count(t => t.Status == TaskStatus.Removed);

    return countType switch
    {
      "total" => $"Total tasks: {total}",
      "completed" => $"Completed tasks: {completed}",
      "pending" => $"Pending tasks: {pending}",
      "removed" => $"Removed tasks: {removed}",
      _ => $"Task counts - Total: {total}, Completed: {completed}, Pending: {pending}, Removed: {removed}"
    };
  }

  private static string GetStatusSymbol(TaskStatus status) => status switch
  {
    TaskStatus.NotStarted => "[ ]",
    TaskStatus.InProgress => "[-]",
    TaskStatus.Completed => "[x]",
    TaskStatus.Removed => "[d]",
    _ => "[ ]"
  };

  private static bool TryParseStatus(string input, out TaskStatus status)
  {
    var normalized = (input ?? string.Empty).Trim().ToLowerInvariant();
    switch (normalized)
    {
      case "not started":
      case "not_started":
      case "notstarted":
      case "todo":
      case "to do":
      case "pending":
        status = TaskStatus.NotStarted; return true;
      case "in progress":
      case "in_progress":
      case "inprogress":
      case "doing":
        status = TaskStatus.InProgress; return true;
      case "completed":
      case "done":
      case "complete":
        status = TaskStatus.Completed; return true;
      case "removed":
      case "deleted":
      case "remove":
      case "delete":
        status = TaskStatus.Removed; return true;
      default:
        status = TaskStatus.NotStarted; return false;
    }
  }

  private static string NormalizeStatusText(TaskStatus status) => status switch
  {
    TaskStatus.NotStarted => "not started",
    TaskStatus.InProgress => "in progress",
    TaskStatus.Completed => "completed",
    TaskStatus.Removed => "removed",
    _ => "not started"
  };
}
