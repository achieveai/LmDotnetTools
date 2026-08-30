namespace TodoEval.Runner.Metrics;

/// <summary>
/// The 15 TaskManager board tools, exactly as metrics-spec.md defines them (matched by
/// <c>function_name</c>, ordinal and case-sensitive). Per-tool metrics carry ALL of these —
/// including zero-call rows — so a reader who skips the definitions still sees which tools
/// never ran.
/// </summary>
internal static class TaskTools
{
    public static readonly IReadOnlyList<string> All =
    [
        "add-task",
        "bulk-initialize",
        "update-task",
        "claim-task",
        "assign-task",
        "block-task",
        "attach-artifact",
        "delete-task",
        "get-task",
        "add-note",
        "edit-note",
        "delete-note",
        "list-notes",
        "list-tasks",
        "search-tasks",
    ];

    private static readonly HashSet<string> Set = new(All, StringComparer.Ordinal);

    public static bool Contains(string toolName) => Set.Contains(toolName);
}
