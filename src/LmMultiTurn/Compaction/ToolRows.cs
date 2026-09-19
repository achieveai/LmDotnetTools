using AchieveAi.LmDotnetTools.LmCore.Messages;

namespace AchieveAi.LmDotnetTools.LmMultiTurn.Compaction;

/// <summary>
///     The two shapes a tool call and a tool result arrive in — one per row, or several batched into one row —
///     read uniformly. Every pre-summary check walks the same history, so they read it the same way.
/// </summary>
internal static class ToolRows
{
    /// <summary>The tool calls a row carries, in order; empty for a row that is not a call.</summary>
    public static IEnumerable<ToolCall> CallsOf(IMessage message) =>
        message switch
        {
            ToolCallMessage single => [single],
            ICanGetToolCalls many => many.GetToolCalls() ?? [],
            _ => [],
        };

    /// <summary>The tool results a row carries, in order; empty for a row that is not a result.</summary>
    public static IEnumerable<ToolCallResult> ResultsOf(IMessage message) =>
        message switch
        {
            ToolCallResultMessage single => [single.ToToolCallResult()],
            ToolsCallResultMessage many => many.ToolCallResults,
            _ => [],
        };
}
