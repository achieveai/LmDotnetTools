namespace AchieveAi.LmDotnetTools.LmCore.Middleware;

/// <summary>
/// Per-invocation metadata handed to a tool handler. Carries the call's identity so
/// handlers can correlate deferred work without inventing synthetic IDs.
/// </summary>
public sealed record ToolCallContext
{
    /// <summary>
    /// The tool_call_id assigned by the model for this invocation. Null when the call
    /// path doesn't carry one (e.g. natural tool use without an explicit ID).
    /// </summary>
    public string? ToolCallId { get; init; }
}
