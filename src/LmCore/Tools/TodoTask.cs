namespace AchieveAi.LmDotnetTools.LmCore.Tools;

/// <summary>
///     A single todo entry. Tasks form a two-level hierarchy: a main task may carry
///     <see cref="SubTasks" />, but a subtask may not — see <see cref="TodoManager.AddTask" />,
///     which enforces the limit.
/// </summary>
/// <remarks>
///     Named <c>TodoTask</c> rather than <c>Task</c> so it does not collide with
///     <see cref="System.Threading.Tasks.Task" /> in a namespace that is used from async code.
/// </remarks>
public sealed class TodoTask
{
    /// <summary>Auto-incrementing identifier, unique across every level of the hierarchy.</summary>
    public required int Id { get; init; }

    /// <summary>Human-readable title.</summary>
    public required string Title { get; set; }

    /// <summary>Current lifecycle state. New tasks start at <see cref="TodoTaskStatus.NotStarted" />.</summary>
    public TodoTaskStatus Status { get; set; } = TodoTaskStatus.NotStarted;

    /// <summary>Free-form notes, in the order they were added.</summary>
    public List<string> Notes { get; } = [];

    /// <summary>Child tasks. Always empty for a task that is itself a subtask.</summary>
    public List<TodoTask> SubTasks { get; } = [];
}
