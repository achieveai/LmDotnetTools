namespace AchieveAi.LmDotnetTools.LmCore.Messages;

/// <summary>
/// Payload carried by <see cref="ToolHandlerResult.Resolved"/>. Holds the data a tool
/// handler produced — text, optional multi-modal blocks, error flags. Framework-controlled
/// fields (<c>ToolCallId</c>, <c>IsDeferred</c>, timestamps) are stamped in by the loop
/// via <see cref="ToolCallResultBuilder.FromHandlerResult"/>, not by handlers.
/// </summary>
public readonly record struct ToolHandlerResultPayload(
    string Text,
    IList<ToolResultContentBlock>? ContentBlocks = null,
    bool IsError = false,
    string? ErrorCode = null);

/// <summary>
/// Result of a tool handler invocation. Either a synchronous <see cref="Resolved"/>
/// payload whose value is fed back to the LLM on the next turn, or a <see cref="Deferred"/>
/// signal indicating the loop should pause and wait for an external caller to invoke
/// <c>MultiTurnAgentLoop.ResolveToolCallAsync</c>.
/// </summary>
/// <remarks>
/// Deferred tool execution is only supported when handlers are dispatched by
/// <c>MultiTurnAgentLoop</c>. <c>FunctionCallMiddleware</c>-driven flows have no
/// resolution channel; a <see cref="Deferred"/> in that context yields a placeholder
/// tool result with <c>IsDeferred=true</c> in the wire <c>ToolCallResult</c>, but
/// nothing will ever resolve it.
/// </remarks>
public abstract record ToolHandlerResult
{
    private ToolHandlerResult() { }

    /// <summary>
    /// Tool finished synchronously. The wrapped <see cref="ToolHandlerResultPayload"/>
    /// carries the text, optional multi-modal content blocks, and error flags.
    /// </summary>
    public sealed record Resolved(ToolHandlerResultPayload Payload) : ToolHandlerResult;

    /// <summary>
    /// Tool execution is deferred. The handler has initiated some out-of-process work;
    /// the loop will record the deferral, end the run after the current turn, and wait
    /// for an external caller to invoke <c>MultiTurnAgentLoop.ResolveToolCallAsync</c>.
    /// Carries no fields — host-side correlation should key on the tool call id available
    /// on <c>ToolCallContext</c> at the time the handler ran.
    /// </summary>
    public sealed record Deferred() : ToolHandlerResult;

    /// <summary>
    /// Builds a <see cref="Resolved"/> with a text-only payload. Most common case.
    /// </summary>
    public static Resolved FromText(string text)
        => new(new ToolHandlerResultPayload(text));

    /// <summary>
    /// Builds a <see cref="Resolved"/> with <see cref="ToolHandlerResultPayload.IsError"/>
    /// set and an optional provider-defined error code. Use for tool-side error returns
    /// (e.g., validation failures, expected API errors) so the LLM sees the error flag.
    /// </summary>
    public static Resolved FromError(string text, string? errorCode = null)
        => new(new ToolHandlerResultPayload(text, IsError: true, ErrorCode: errorCode));

    /// <summary>
    /// Builds a <see cref="Resolved"/> carrying multi-modal content blocks (e.g., MCP
    /// image returns). The text is still required — providers without multi-modal
    /// support fall back to it.
    /// </summary>
    public static Resolved FromMultiModal(string text, IList<ToolResultContentBlock> blocks)
        => new(new ToolHandlerResultPayload(text, ContentBlocks: blocks));

    /// <summary>
    /// Convenience accessor for the resolved text. Throws on <see cref="Deferred"/> —
    /// callers that may receive a deferral should pattern-match instead.
    /// </summary>
    public string ResultText => this switch
    {
        Resolved r => r.Payload.Text,
        Deferred => throw new InvalidOperationException(
            "Cannot read ResultText from a deferred ToolHandlerResult — it has not been resolved yet."),
        _ => throw new InvalidOperationException(
            $"Unknown ToolHandlerResult variant '{GetType().Name}'."),
    };
}
