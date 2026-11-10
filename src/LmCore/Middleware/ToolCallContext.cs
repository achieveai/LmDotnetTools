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

    /// <summary>
    /// True when the caller will save output too large to keep in the conversation, so a tool
    /// that would otherwise drop the end of its own output can return all of it. False leaves the
    /// tool's own limits as the only protection.
    /// </summary>
    public bool CanSaveLargeOutput { get; init; }
}
