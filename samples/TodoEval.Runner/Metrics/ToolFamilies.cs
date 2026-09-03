namespace TodoEval.Runner.Metrics;

/// <summary>
/// The family a <c>function_name</c> belongs to, per metrics-spec.md "Tool families". Per-tool rows
/// carry it so a reader can segment board work from coordination work without knowing either list.
/// </summary>
internal enum ToolFamily
{
    /// <summary>Any tool that is neither a board tool nor a coordination tool; totals only.</summary>
    Other = 0,

    /// <summary>One of the 15 TaskManager board tools (<see cref="TaskTools.All"/>).</summary>
    Task = 1,

    /// <summary>One of the 7 sub-agent coordination tools (<see cref="CoordinationTools.All"/>).</summary>
    Coordination = 2,
}

/// <summary>
/// The 7 sub-agent coordination tools, mirrored from
/// <c>AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents.SubAgentToolProvider.AllToolNames</c>.
/// </summary>
/// <remarks>
/// This is a MIRROR, not a reference: TodoEval.Runner deliberately carries zero project references
/// (see TodoEval.Runner.csproj) because the eval measures the HOST over its wire and on-disk
/// artifacts, not a client abstraction. Drift is caught instead by
/// <c>CoordinationVocabularyParityTests</c>, which reflects over the real provider and asserts
/// set-equality with this list.
/// </remarks>
internal static class CoordinationTools
{
    public static readonly IReadOnlyList<string> All =
    [
        "Agent",
        "SendMessage",
        "CheckAgent",
        "WaitAgent",
        "CheckAgents",
        "WaitForAgents",
        "GetAgents",
    ];

    private static readonly HashSet<string> Set = new(All, StringComparer.Ordinal);

    public static bool Contains(string toolName) => Set.Contains(toolName);
}

/// <summary>Classifies a tool name into its <see cref="ToolFamily"/> and orders the per-tool rows.</summary>
internal static class ToolFamilies
{
    /// <summary>
    /// Every tool that gets a per-tool row, in the spec's declared order: the 15 task tools first,
    /// then the 7 coordination tools. <c>other</c> tools never get a row — they count in totals only.
    /// </summary>
    public static readonly IReadOnlyList<string> RowOrder = [.. TaskTools.All, .. CoordinationTools.All];

    public static ToolFamily Classify(string toolName) =>
        TaskTools.Contains(toolName) ? ToolFamily.Task
        : CoordinationTools.Contains(toolName) ? ToolFamily.Coordination
        : ToolFamily.Other;

    /// <summary>The lowercase wire spelling used in the score object's <c>family</c> field.</summary>
    public static string Name(ToolFamily family) =>
        family switch
        {
            ToolFamily.Task => "task",
            ToolFamily.Coordination => "coordination",
            _ => "other",
        };
}
