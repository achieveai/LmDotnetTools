namespace AchieveAi.LmDotnetTools.LmCore.Middleware;

/// <summary>
/// Per-invocation context handed to a tool handler. Carries the call's identity and the
/// host's cancellation signal so handlers can correlate deferred work without inventing
/// synthetic IDs and can react to cancellation without closing over an outer
/// <see cref="System.Threading.CancellationToken"/>.
/// </summary>
public sealed record ToolCallContext
{
    /// <summary>
    /// The tool_call_id assigned by the model for this invocation. Null when the call
    /// path doesn't carry one (e.g. natural tool use without an explicit ID).
    /// </summary>
    public string? ToolCallId { get; init; }

    /// <summary>Host cancellation signal threaded by the middleware.</summary>
    public CancellationToken CancellationToken { get; init; }
}
