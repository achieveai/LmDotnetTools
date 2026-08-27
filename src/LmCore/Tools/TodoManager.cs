using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmCore.Models;

namespace AchieveAi.LmDotnetTools.LmCore.Tools;

/// <summary>
///     In-memory hierarchical todo list exposed to an LLM as four kebab-case tools:
///     <c>add-task</c>, <c>update-task</c>, <c>add-task-notes</c> and <c>list-tasks</c>.
///     State lives for the lifetime of the instance only — nothing is persisted.
/// </summary>
/// <remarks>
///     Every operation answers in markdown, including its failures: an unknown id or an
///     unrecognised status produces an error string rather than an exception, so a model that
///     guesses wrong gets a readable correction instead of a fault.
/// </remarks>
public sealed class TodoManager : IFunctionProvider
{
    /// <summary>Tool name for creating a task or subtask.</summary>
    public const string AddTaskToolName = "add-task";

    /// <summary>Tool name for changing a task's status.</summary>
    public const string UpdateTaskToolName = "update-task";

    /// <summary>Tool name for appending a note to a task.</summary>
    public const string AddTaskNotesToolName = "add-task-notes";

    /// <summary>Tool name for rendering the whole list.</summary>
    public const string ListTasksToolName = "list-tasks";

    /// <summary>
    ///     Guards <see cref="_tasks" /> and <see cref="_nextId" />. <c>ToolCallExecutor</c> runs the
    ///     handlers of a single tool-call message one after another, so this is not protecting that
    ///     path; it exists because one instance can be registered once and shared by concurrent
    ///     runs, whose mutations would otherwise interleave.
    /// </summary>
    private readonly object _gate = new();

    private readonly List<TodoTask> _tasks = [];

    private int _nextId = 1;

    /// <summary>
    ///     Snapshot of the main tasks, each carrying its own subtasks. The list is a copy; the
    ///     <see cref="TodoTask" /> instances in it are the live ones.
    /// </summary>
    public IReadOnlyList<TodoTask> Tasks
    {
        get
        {
            lock (_gate)
            {
                return [.. _tasks];
            }
        }
    }

    /// <inheritdoc />
    public string ProviderName => nameof(TodoManager);

    /// <inheritdoc />
    public int Priority => 100;

    /// <inheritdoc />
    public IEnumerable<FunctionDescriptor> GetFunctions()
    {
        yield return Descriptor(BuildAddTaskContract(), HandleAddTaskAsync);
        yield return Descriptor(BuildUpdateTaskContract(), HandleUpdateTaskAsync);
        yield return Descriptor(BuildAddTaskNotesContract(), HandleAddTaskNotesAsync);
        yield return Descriptor(BuildListTasksContract(), HandleListTasksAsync);
    }

    private FunctionDescriptor Descriptor(FunctionContract contract, ToolHandler handler) =>
        new()
        {
            Contract = contract,
            Handler = handler,
            ProviderName = ProviderName,

            // The list is instance state, so a descriptor from this provider cannot be shared
            // across independent conversations without carrying the tasks along with it.
            IsStateful = true,
        };

    // ---------------- Public operations ----------------

    /// <summary>
    ///     Adds a main task, or a subtask when <paramref name="parentId" /> names an existing main
    ///     task. Fails when the parent does not exist or is itself a subtask.
    /// </summary>
    /// <returns>A markdown confirmation, or a markdown error message.</returns>
    public string AddTask(string title, int? parentId = null)
    {
        ArgumentNullException.ThrowIfNull(title);
        return TryAddTask(title, parentId).Markdown;
    }

    /// <summary>
    ///     Sets the status of the task with the given id, searching main tasks and subtasks alike.
    /// </summary>
    /// <returns>A markdown confirmation, or a markdown error message.</returns>
    public string UpdateTask(int taskId, string status)
    {
        ArgumentNullException.ThrowIfNull(status);
        return TryUpdateTask(taskId, status).Markdown;
    }

    /// <summary>
    ///     Appends a note to the task with the given id, searching main tasks and subtasks alike.
    ///     Existing notes are kept.
    /// </summary>
    /// <returns>A markdown confirmation, or a markdown error message.</returns>
    public string AddTaskNotes(int taskId, string note)
    {
        ArgumentNullException.ThrowIfNull(note);
        return TryAddTaskNotes(taskId, note).Markdown;
    }

    /// <summary>Renders the full list. Identical to <see cref="GetMarkdown" />.</summary>
    public string ListTasks() => GetMarkdown();

    /// <summary>
    ///     Renders every task, subtask and note as markdown under a <c># TODO</c> header.
    /// </summary>
    public string GetMarkdown()
    {
        lock (_gate)
        {
            var lines = new List<string> { "# TODO", string.Empty };

            if (_tasks.Count == 0)
            {
                lines.Add("_No tasks yet._");
            }
            else
            {
                foreach (var task in _tasks)
                {
                    AppendTask(lines, task, depth: 0);
                }
            }

            // "\n" rather than Environment.NewLine: this string is markdown handed to a model and
            // asserted against in tests, so it must not vary between Windows and Linux.
            return string.Join("\n", lines);
        }
    }

    private static void AppendTask(List<string> lines, TodoTask task, int depth)
    {
        var indent = new string(' ', depth * 2);
        lines.Add($"{indent}- [{Marker(task.Status)}] {task.Id}. {task.Title}");

        for (var i = 0; i < task.Notes.Count; i++)
        {
            // Notes sit one level deeper than the bullet that owns them.
            lines.Add($"{indent}  {i + 1}. {task.Notes[i]}");
        }

        foreach (var subTask in task.SubTasks)
        {
            AppendTask(lines, subTask, depth + 1);
        }
    }

    private static char Marker(TodoTaskStatus status) =>
        status switch
        {
            TodoTaskStatus.NotStarted => ' ',
            TodoTaskStatus.InProgress => '-',
            TodoTaskStatus.Completed => 'x',
            TodoTaskStatus.Removed => '~',
            _ => '?',
        };

    // ---------------- Operation implementations ----------------

    private OperationResult TryAddTask(string title, int? parentId)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return OperationResult.Failure("**Error:** `title` is required and cannot be empty.");
        }

        lock (_gate)
        {
            if (parentId is not { } parent)
            {
                var task = new TodoTask { Id = _nextId++, Title = title };
                _tasks.Add(task);
                return OperationResult.Success($"Added task **{task.Id}**: {task.Title}");
            }

            var parentTask = _tasks.FirstOrDefault(t => t.Id == parent);
            if (parentTask == null)
            {
                // Distinguish "no such task" from "that task cannot be a parent" — a model that
                // aimed at a subtask needs to know the id was found but rejected.
                var existsAsSubTask = _tasks.SelectMany(t => t.SubTasks).Any(t => t.Id == parent);
                return existsAsSubTask
                    ? OperationResult.Failure(
                        $"**Error:** Task **{parent}** is already a subtask. "
                            + "The list supports two levels only, so a subtask cannot have children."
                    )
                    : OperationResult.Failure($"**Error:** No task found with id **{parent}**.");
            }

            var subTask = new TodoTask { Id = _nextId++, Title = title };
            parentTask.SubTasks.Add(subTask);
            return OperationResult.Success($"Added subtask **{subTask.Id}** under task **{parent}**: {subTask.Title}");
        }
    }

    private OperationResult TryUpdateTask(int taskId, string status)
    {
        if (!TryParseStatus(status, out var parsed))
        {
            return OperationResult.Failure(
                $"**Error:** Unknown status `{status}`. "
                    + "Use one of `not started`, `in progress`, `completed`, `removed`."
            );
        }

        lock (_gate)
        {
            var task = Find(taskId);
            if (task == null)
            {
                return OperationResult.Failure($"**Error:** No task found with id **{taskId}**.");
            }

            task.Status = parsed;
            return OperationResult.Success($"Updated task **{taskId}** to **{status.Trim()}**.");
        }
    }

    private OperationResult TryAddTaskNotes(int taskId, string note)
    {
        if (string.IsNullOrWhiteSpace(note))
        {
            return OperationResult.Failure("**Error:** `note` is required and cannot be empty.");
        }

        lock (_gate)
        {
            var task = Find(taskId);
            if (task == null)
            {
                return OperationResult.Failure($"**Error:** No task found with id **{taskId}**.");
            }

            task.Notes.Add(note);
            return OperationResult.Success($"Added note to task **{taskId}**.");
        }
    }

    /// <summary>Finds a task at either level. Callers must already hold <see cref="_gate" />.</summary>
    private TodoTask? Find(int taskId)
    {
        foreach (var task in _tasks)
        {
            if (task.Id == taskId)
            {
                return task;
            }

            var match = task.SubTasks.FirstOrDefault(t => t.Id == taskId);
            if (match != null)
            {
                return match;
            }
        }

        return null;
    }

    /// <summary>
    ///     Accepts the four spec statuses case-insensitively, ignoring spaces, hyphens and
    ///     underscores — so <c>not started</c>, <c>Not_Started</c> and <c>NotStarted</c> all parse.
    ///     Anything else is rejected.
    /// </summary>
    private static bool TryParseStatus(string? status, out TodoTaskStatus parsed)
    {
        parsed = TodoTaskStatus.NotStarted;
        if (status == null)
        {
            return false;
        }

        var builder = new StringBuilder(status.Length);
        foreach (var character in status)
        {
            if (character is ' ' or '-' or '_' or '\t')
            {
                continue;
            }

            _ = builder.Append(char.ToLowerInvariant(character));
        }

        var normalized = builder.ToString();

        switch (normalized)
        {
            case "notstarted":
                parsed = TodoTaskStatus.NotStarted;
                return true;
            case "inprogress":
                parsed = TodoTaskStatus.InProgress;
                return true;
            case "completed":
                parsed = TodoTaskStatus.Completed;
                return true;
            case "removed":
                parsed = TodoTaskStatus.Removed;
                return true;
            default:
                return false;
        }
    }

    // ---------------- Contracts ----------------

    private static JsonSchemaObject StringSchema() => new() { Type = new("string") };

    private static JsonSchemaObject IntegerSchema() => new() { Type = new("integer") };

    private static FunctionContract BuildAddTaskContract() =>
        new()
        {
            Name = AddTaskToolName,
            Description =
                "Add a task to the todo list. Omit parent_id for a top-level task, or pass the id of "
                + "an existing top-level task to add a subtask under it. The list is two levels deep, "
                + "so a subtask cannot itself have children.",
            Parameters =
            [
                new FunctionParameterContract
                {
                    Name = "title",
                    Description = "Short description of the work.",
                    ParameterType = StringSchema(),
                    IsRequired = true,
                },
                new FunctionParameterContract
                {
                    Name = "parent_id",
                    Description = "Id of the top-level task to nest under. Omit to create a top-level task.",
                    ParameterType = IntegerSchema(),
                    IsRequired = false,
                },
            ],
        };

    private static FunctionContract BuildUpdateTaskContract() =>
        new()
        {
            Name = UpdateTaskToolName,
            Description = "Set the status of a task or subtask, found by id.",
            Parameters =
            [
                new FunctionParameterContract
                {
                    Name = "task_id",
                    Description = "Id of the task or subtask to update.",
                    ParameterType = IntegerSchema(),
                    IsRequired = true,
                },
                new FunctionParameterContract
                {
                    Name = "status",
                    Description = "One of: not started, in progress, completed, removed.",
                    ParameterType = StringSchema(),
                    IsRequired = true,
                },
            ],
        };

    private static FunctionContract BuildAddTaskNotesContract() =>
        new()
        {
            Name = AddTaskNotesToolName,
            Description = "Append a note to a task or subtask, found by id. Existing notes are kept.",
            Parameters =
            [
                new FunctionParameterContract
                {
                    Name = "task_id",
                    Description = "Id of the task or subtask to annotate.",
                    ParameterType = IntegerSchema(),
                    IsRequired = true,
                },
                new FunctionParameterContract
                {
                    Name = "note",
                    Description = "The note to append.",
                    ParameterType = StringSchema(),
                    IsRequired = true,
                },
            ],
        };

    private static FunctionContract BuildListTasksContract() =>
        new()
        {
            Name = ListTasksToolName,
            Description = "Render the full todo list as markdown, including subtasks and notes.",
            Parameters = [],
        };

    // ---------------- Handlers ----------------

    private Task<ToolHandlerResult> HandleAddTaskAsync(string argsJson, ToolCallContext context, CancellationToken ct)
    {
        if (!TryParseArgs(argsJson, out var root, out var parseError))
        {
            return Task.FromResult<ToolHandlerResult>(ToolHandlerResult.FromError(parseError, "invalid_args"));
        }

        using (root)
        {
            var title = ReadString(root.RootElement, "title");
            var parentId = ReadInt(root.RootElement, "parent_id");
            return Task.FromResult<ToolHandlerResult>(ToResult(TryAddTask(title ?? string.Empty, parentId)));
        }
    }

    private Task<ToolHandlerResult> HandleUpdateTaskAsync(string argsJson, ToolCallContext context, CancellationToken ct)
    {
        if (!TryParseArgs(argsJson, out var root, out var parseError))
        {
            return Task.FromResult<ToolHandlerResult>(ToolHandlerResult.FromError(parseError, "invalid_args"));
        }

        using (root)
        {
            var taskId = ReadInt(root.RootElement, "task_id");
            var status = ReadString(root.RootElement, "status");
            if (taskId is not { } id)
            {
                return Task.FromResult<ToolHandlerResult>(
                    ToolHandlerResult.FromError("**Error:** `task_id` is required.", "invalid_args")
                );
            }

            return Task.FromResult<ToolHandlerResult>(ToResult(TryUpdateTask(id, status ?? string.Empty)));
        }
    }

    private Task<ToolHandlerResult> HandleAddTaskNotesAsync(
        string argsJson,
        ToolCallContext context,
        CancellationToken ct
    )
    {
        if (!TryParseArgs(argsJson, out var root, out var parseError))
        {
            return Task.FromResult<ToolHandlerResult>(ToolHandlerResult.FromError(parseError, "invalid_args"));
        }

        using (root)
        {
            var taskId = ReadInt(root.RootElement, "task_id");
            var note = ReadString(root.RootElement, "note");
            if (taskId is not { } id)
            {
                return Task.FromResult<ToolHandlerResult>(
                    ToolHandlerResult.FromError("**Error:** `task_id` is required.", "invalid_args")
                );
            }

            return Task.FromResult<ToolHandlerResult>(ToResult(TryAddTaskNotes(id, note ?? string.Empty)));
        }
    }

    private Task<ToolHandlerResult> HandleListTasksAsync(string argsJson, ToolCallContext context, CancellationToken ct)
    {
        // list-tasks takes no parameters, so its arguments are not inspected at all.
        return Task.FromResult<ToolHandlerResult>(ToolHandlerResult.FromText(GetMarkdown()));
    }

    private static ToolHandlerResult ToResult(OperationResult result) =>
        result.Succeeded
            ? ToolHandlerResult.FromText(result.Markdown)
            : ToolHandlerResult.FromError(result.Markdown, "todo_operation_failed");

    private static bool TryParseArgs(
        string? argsJson,
        [NotNullWhen(true)] out JsonDocument? document,
        [NotNullWhen(false)] out string? error
    )
    {
        document = null;
        error = null;

        if (string.IsNullOrWhiteSpace(argsJson))
        {
            // An absent payload is an empty argument set, not a malformed one.
            document = JsonDocument.Parse("{}");
            return true;
        }

        try
        {
            document = JsonDocument.Parse(argsJson);
        }
        catch (JsonException)
        {
            error = "**Error:** Arguments are not valid JSON.";
            return false;
        }

        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            document.Dispose();
            document = null;
            error = "**Error:** Arguments must be a JSON object.";
            return false;
        }

        return true;
    }

    private static string? ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;

    private static int? ReadInt(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var element))
        {
            return null;
        }

        return element.ValueKind switch
        {
            JsonValueKind.Number when element.TryGetInt32(out var number) => number,

            // Models routinely quote numeric arguments, so a numeric string is accepted here.
            JsonValueKind.String when int.TryParse(element.GetString(), out var parsed) => parsed,
            _ => null,
        };
    }

    private readonly record struct OperationResult(bool Succeeded, string Markdown)
    {
        public static OperationResult Success(string markdown) => new(true, markdown);

        public static OperationResult Failure(string markdown) => new(false, markdown);
    }
}
