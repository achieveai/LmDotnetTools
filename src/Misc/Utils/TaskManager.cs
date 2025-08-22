using System.Collections.Concurrent;
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

  public class BulkTaskItem
  {
    public string Task { get; set; } = string.Empty;
    public List<string> SubTasks { get; set; } = new();
    public List<string> Notes { get; set; } = new();
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

  // Thread-safe collections
  private readonly List<TaskItem> _rootTasks = new();
  private readonly ConcurrentDictionary<int, TaskItem> _tasksById = new();
  private readonly ReaderWriterLockSlim _rootTasksLock = new();
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
      Id = Interlocked.Increment(ref _nextId),
      Title = title.Trim(),
      Status = TaskStatus.NotStarted,
      ParentId = parentId
    };

    if (parentId is null)
    {
      // Add as root task
      _rootTasksLock.EnterWriteLock();
      try
      {
        _rootTasks.Add(task);
      }
      finally
      {
        _rootTasksLock.ExitWriteLock();
      }

      _tasksById[task.Id] = task;
      return $"Added task {task.Id}: {task.Title}";
    }

    // Add as subtask
    if (!_tasksById.TryGetValue(parentId.Value, out var parent))
    {
      return $"Error: Parent task {parentId.Value} not found.";
    }

    if (parent.ParentId is not null)
    {
      return $"Error: Only two levels supported. Task {parent.Id} is already a subtask.";
    }

    lock (parent.SubTasks)
    {
      parent.SubTasks.Add(task);
    }

    _tasksById[task.Id] = task;
    return $"Added subtask {task.Id} under task {parent.Id}: {task.Title}";
  }

  [Function("bulk-initialize", @"Initialize multiple tasks with subtasks and notes in one operation.
Use for setting up complex task hierarchies from structured data.

Examples:
- Initialize with clearing: {""tasks"": [{""task"": ""Design API"", ""subTasks"": [""Define endpoints"", ""Create schemas""], ""notes"": [""RESTful design""]}], ""clearExisting"": true}
- Append to existing: {""tasks"": [{""task"": ""Implementation"", ""subTasks"": [""Code review""], ""notes"": []}], ""clearExisting"": false}")]
  public string BulkInitialize(
      [Description("List of tasks with their subtasks and notes")] List<BulkTaskItem> tasks,
      [Description("Clear all existing tasks before adding new ones")] bool clearExisting = false)
  {
    if (tasks == null || tasks.Count == 0)
    {
      return "Error: No tasks provided for initialization.";
    }

    // Clear existing tasks if requested
    if (clearExisting)
    {
      _rootTasksLock.EnterWriteLock();
      try
      {
        _rootTasks.Clear();
      }
      finally
      {
        _rootTasksLock.ExitWriteLock();
      }

      _tasksById.Clear();
      Interlocked.Exchange(ref _nextId, 1);
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
      var mainTask = new TaskItem
      {
        Id = Interlocked.Increment(ref _nextId),
        Title = bulkItem.Task.Trim(),
        Status = TaskStatus.NotStarted
      };

      _rootTasksLock.EnterWriteLock();
      try
      {
        _rootTasks.Add(mainTask);
      }
      finally
      {
        _rootTasksLock.ExitWriteLock();
      }

      _tasksById[mainTask.Id] = mainTask;
      addedTasks.Add($"Task {mainTask.Id}: {mainTask.Title}");

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

          var subTask = new TaskItem
          {
            Id = Interlocked.Increment(ref _nextId),
            Title = subTaskTitle.Trim(),
            Status = TaskStatus.NotStarted,
            ParentId = mainTask.Id
          };

          mainTask.SubTasks.Add(subTask);
          _tasksById[subTask.Id] = subTask;
        }
      }
    }

    var result = new StringBuilder();

    if (clearExisting)
    {
      result.AppendLine("Cleared existing tasks.");
    }

    if (addedTasks.Count > 0)
    {
      result.AppendLine($"Added {addedTasks.Count} task(s):");
      foreach (var task in addedTasks)
      {
        result.AppendLine($"  - {task}");
      }
    }

    if (errors.Count > 0)
    {
      result.AppendLine("Errors:");
      foreach (var error in errors)
      {
        result.AppendLine($"  - {error}");
      }
    }

    return result.ToString().TrimEnd();
  }

  [Function("update-task", @"Update task or subtask status to advance plan execution.
Use after completing a step or changing task state.

Examples:
- Set task 1 to in-progress: {""taskId"": 1, ""status"": ""in progress""}
- Complete subtask 3 under task 1: {""taskId"": 1, ""subtaskId"": 3, ""status"": ""completed""}")]
  public string UpdateTask(
      [Description("Task ID")] int taskId,
      [Description("Subtask ID if updating subtask")] int? subtaskId = null,
      [Description("New status: not started|in progress|completed|removed")] string status = "not started")
  {
    // Find target task using helper method
    var (targetTask, taskRef, error) = FindTaskWithReference(taskId, subtaskId);
    if (targetTask == null)
      return error!;

    // Update status
    if (!TryParseStatus(status, out var newStatus))
      return "Error: Invalid status. Use: not started, in progress, completed, removed.";

    targetTask.Status = newStatus;

    return $"Updated {taskRef} status to '{NormalizeStatusText(newStatus)}'.";
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

      TaskItem? subtask = null;
      lock (parentTask.SubTasks)
      {
        subtask = parentTask.SubTasks.FirstOrDefault(st => st.Id == subtaskId.Value);
        if (subtask == null)
          return $"Error: Subtask {subtaskId.Value} not found under task {taskId}.";

        parentTask.SubTasks.Remove(subtask);
      }

      _tasksById.TryRemove(subtask.Id, out _);
      return $"Deleted subtask {subtaskId.Value} from task {taskId}: {subtask.Title}";
    }
    else
    {
      // Delete main task and all subtasks
      if (!_tasksById.TryGetValue(taskId, out var task))
        return $"Error: Task {taskId} not found.";

      _rootTasksLock.EnterWriteLock();
      try
      {
        _rootTasks.Remove(task);
      }
      finally
      {
        _rootTasksLock.ExitWriteLock();
      }

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
    var (task, taskRef, error) = FindTaskWithReference(taskId, subtaskId);
    if (task == null)
      return error!;

    return FormatTaskDetails(task, taskRef);
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
    // Find target task using helper method
    var (targetTask, taskRef, error) = FindTaskWithReference(taskId, subtaskId);
    if (targetTask == null)
      return error!;

    switch (action.ToLowerInvariant())
    {
      case "add":
        if (string.IsNullOrWhiteSpace(noteText))
          return "Error: Note text required for add action.";
        lock (targetTask.Notes)
        {
          targetTask.Notes.Add(noteText.Trim());
        }
        return $"Added note to {taskRef}.";

      case "edit":
        if (!noteIndex.HasValue || string.IsNullOrWhiteSpace(noteText))
          return "Error: Note index and new text required for edit action.";
        lock (targetTask.Notes)
        {
          if (noteIndex.Value < 1 || noteIndex.Value > targetTask.Notes.Count)
            return $"Error: Note index {noteIndex.Value} out of range (1-{targetTask.Notes.Count}).";
          targetTask.Notes[noteIndex.Value - 1] = noteText.Trim();
        }
        return $"Edited note {noteIndex.Value} on {taskRef}.";

      case "delete":
        if (!noteIndex.HasValue)
          return "Error: Note index required for delete action.";
        lock (targetTask.Notes)
        {
          if (noteIndex.Value < 1 || noteIndex.Value > targetTask.Notes.Count)
            return $"Error: Note index {noteIndex.Value} out of range (1-{targetTask.Notes.Count}).";
          targetTask.Notes.RemoveAt(noteIndex.Value - 1);
        }
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
    // Find target task using helper method
    var (targetTask, taskRef, error) = FindTaskWithReference(taskId, subtaskId);
    if (targetTask == null)
      return error!;

    List<string> notesCopy;
    lock (targetTask.Notes)
    {
      if (targetTask.Notes.Count == 0)
        return $"{taskRef} has no notes.";
      notesCopy = new List<string>(targetTask.Notes);
    }

    var sb = new StringBuilder();
    sb.AppendLine($"Notes for {taskRef}: {targetTask.Title}");
    for (int i = 0; i < notesCopy.Count; i++)
    {
      sb.AppendLine($"{i + 1}. {notesCopy[i]}");
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
    List<TaskItem> rootTasksCopy;
    _rootTasksLock.EnterReadLock();
    try
    {
      if (_rootTasks.Count == 0)
        return "No tasks found.";
      rootTasksCopy = new List<TaskItem>(_rootTasks);
    }
    finally
    {
      _rootTasksLock.ExitReadLock();
    }

    TaskStatus? filterStatus = null;
    if (!string.IsNullOrEmpty(status))
    {
      if (!TryParseStatus(status, out var parsedStatus))
        return "Error: Invalid status filter. Use: not started, in progress, completed, removed.";
      filterStatus = parsedStatus;
    }

    var sb = new StringBuilder();
    sb.AppendLine("# TODO");

    foreach (var task in rootTasksCopy)
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

    List<TaskItem> rootTasksCopy;
    _rootTasksLock.EnterReadLock();
    try
    {
      rootTasksCopy = new List<TaskItem>(_rootTasks);
    }
    finally
    {
      _rootTasksLock.ExitReadLock();
    }

    foreach (var task in rootTasksCopy)
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

  // Helper methods

  /// <summary>
  /// Finds a task by ID and optional subtask ID, returning the task, a reference string, and any error.
  /// This consolidates the repeated task lookup pattern.
  /// </summary>
  private (TaskItem? task, string taskRef, string? error) FindTaskWithReference(int taskId, int? subtaskId)
  {
    if (subtaskId.HasValue)
    {
      // Find subtask
      if (!_tasksById.TryGetValue(taskId, out var parentTask))
        return (null, string.Empty, $"Error: Parent task {taskId} not found.");

      TaskItem? subtask;
      lock (parentTask.SubTasks)
      {
        subtask = parentTask.SubTasks.FirstOrDefault(st => st.Id == subtaskId.Value);
      }

      if (subtask == null)
        return (null, string.Empty, $"Error: Subtask {subtaskId.Value} not found under task {taskId}.");

      return (subtask, $"subtask {subtaskId.Value} of task {taskId}", null);
    }
    else
    {
      // Find main task
      if (!_tasksById.TryGetValue(taskId, out var task))
        return (null, string.Empty, $"Error: Task {taskId} not found.");

      return (task, $"task {taskId}", null);
    }
  }

  private void RemoveTaskAndSubtasks(TaskItem task)
  {
    _tasksById.TryRemove(task.Id, out _);
    foreach (var subtask in task.SubTasks)
    {
      _tasksById.TryRemove(subtask.Id, out _);
    }
  }

  private string FormatTaskDetails(TaskItem task, string header)
  {
    var sb = new StringBuilder();
    sb.AppendLine($"{header.Substring(0, 1).ToUpper() + header.Substring(1)}: {task.Title}");
    sb.AppendLine($"Status: {NormalizeStatusText(task.Status)}");

    List<string> notesCopy;
    lock (task.Notes)
    {
      notesCopy = new List<string>(task.Notes);
    }

    if (notesCopy.Count > 0)
    {
      sb.AppendLine($"Notes ({notesCopy.Count}):");
      for (int i = 0; i < notesCopy.Count; i++)
      {
        sb.AppendLine($"  {i + 1}. {notesCopy[i]}");
      }
    }

    List<TaskItem> subtasksCopy;
    lock (task.SubTasks)
    {
      subtasksCopy = new List<TaskItem>(task.SubTasks);
    }

    if (subtasksCopy.Count > 0)
    {
      sb.AppendLine($"Subtasks ({subtasksCopy.Count}):");
      foreach (var subtask in subtasksCopy)
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

    List<string> notesCopy;
    lock (task.Notes)
    {
      notesCopy = new List<string>(task.Notes);
    }

    if (notesCopy.Count > 0)
    {
      sb.AppendLine($"{indent}  Notes:");
      for (int i = 0; i < notesCopy.Count; i++)
      {
        sb.AppendLine($"{indent}  - {i + 1} {notesCopy[i]}");
      }
    }

    if (!mainOnly)
    {
      List<TaskItem> subtasksCopy;
      lock (task.SubTasks)
      {
        subtasksCopy = new List<TaskItem>(task.SubTasks);
      }

      foreach (var sub in subtasksCopy)
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

    List<TaskItem> subtasksCopy;
    lock (task.SubTasks)
    {
      subtasksCopy = new List<TaskItem>(task.SubTasks);
    }

    for (int i = 0; i < subtasksCopy.Count; i++)
    {
      var subtask = subtasksCopy[i];
      SearchTaskRecursive(subtask, searchTerm, $"{path}.{subtask.Id}", matches);
    }
  }

  private string GetTaskCounts(string countType)
  {
    var allTasks = _tasksById.Values.ToList();
    var total = allTasks.Count;
    var completed = allTasks.Count(t => t.Status == TaskStatus.Completed);
    var pending = allTasks.Count(t => t.Status == TaskStatus.NotStarted || t.Status == TaskStatus.InProgress);
    var removed = allTasks.Count(t => t.Status == TaskStatus.Removed);

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

  /// <summary>
  /// Gets the current task context for middleware integration
  /// </summary>
  /// <returns>String representation of pending tasks (not started or in progress)</returns>
  public string GetCurrentTaskContext()
  {
    var pendingTasks = new List<string>();

    _rootTasksLock.EnterReadLock();
    try
    {
      foreach (var task in _rootTasks)
      {
        if (task.Status == TaskStatus.NotStarted || task.Status == TaskStatus.InProgress)
        {
          pendingTasks.Add($"Task {task.Id}: {task.Title} ({NormalizeStatusText(task.Status)})");

          // Add pending subtasks
          lock (task.SubTasks)
          {
            foreach (var subtask in task.SubTasks)
            {
              if (subtask.Status == TaskStatus.NotStarted || subtask.Status == TaskStatus.InProgress)
              {
                pendingTasks.Add($"  Subtask {subtask.Id}: {subtask.Title} ({NormalizeStatusText(subtask.Status)})");
              }
            }
          }
        }
      }
    }
    finally
    {
      _rootTasksLock.ExitReadLock();
    }

    return pendingTasks.Count > 0 ? string.Join("\n", pendingTasks) : string.Empty;
  }
}