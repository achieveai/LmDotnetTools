namespace AchieveAi.LmDotnetTools.LmCore.Tools;

/// <summary>
///     Lifecycle states a <see cref="TodoTask" /> can be in.
/// </summary>
public enum TodoTaskStatus
{
    /// <summary>Task has been created but no work has begun.</summary>
    NotStarted,

    /// <summary>Work on the task is underway.</summary>
    InProgress,

    /// <summary>Task is finished.</summary>
    Completed,

    /// <summary>
    ///     Task is no longer being pursued. Removed tasks are retained rather than deleted so the
    ///     rendered list keeps a record of what was dropped.
    /// </summary>
    Removed,
}
