namespace AchieveAi.LmDotnetTools.LmMultiTurn;

/// <summary>
/// Snapshot of a deferred tool call that is awaiting external resolution.
/// Returned by <c>MultiTurnAgentLoop.GetDeferredToolCallsAsync</c> so hosts (UIs, webhook
/// receivers, restart-recovery code) can correlate the deferred entry in conversation history
/// with their own state — keyed on <see cref="ToolCallId"/> — and call
/// <c>ResolveToolCallAsync</c> when the answer is known.
/// </summary>
public sealed record DeferredToolCallInfo
{
    /// <summary>
    /// Identifier of the tool call. Use this when calling <c>ResolveToolCallAsync</c>.
    /// </summary>
    public required string ToolCallId { get; init; }

    /// <summary>
    /// Name of the tool function that was invoked.
    /// </summary>
    public required string FunctionName { get; init; }

    /// <summary>
    /// JSON-serialized arguments the LLM passed to the function.
    /// </summary>
    public required string FunctionArgs { get; init; }

    /// <summary>
    /// Unix-ms timestamp recorded when the handler signaled deferral.
    /// </summary>
    public required long DeferredAtUnixMs { get; init; }

    /// <summary>
    /// Identifier of the run during which the tool call was emitted. The run has since ended;
    /// resolving the call will start a new run.
    /// </summary>
    public string? RunId { get; init; }

    /// <summary>
    /// Identifier of the assistant generation (turn) that emitted this tool call.
    /// </summary>
    public string? GenerationId { get; init; }
}
