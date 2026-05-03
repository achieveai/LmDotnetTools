namespace AchieveAi.LmDotnetTools.LmCore.Messages;

/// <summary>
/// Single mapping point from a handler-side <see cref="ToolHandlerResult"/> to the wire-shape
/// <see cref="ToolCallResult"/>. Stamps in framework-controlled fields (tool call id, tool
/// name, deferral marker, timestamps) that handlers should not — and now cannot — set
/// themselves.
/// </summary>
/// <remarks>
/// Both <c>FunctionCallMiddleware</c> and <c>MultiTurnAgentLoop</c> route through here so
/// the two paths can't drift. Handlers signal "deferred" with an empty
/// <see cref="ToolHandlerResult.Deferred"/> record; the builder produces a
/// <see cref="ToolCallResult"/> with empty <c>Result</c> text and <c>IsDeferred=true</c> —
/// host loops are responsible for refusing to send any provider request while such a
/// result is unresolved in history (see <c>MultiTurnAgentLoop.ExecuteTurnAsync</c>).
/// </remarks>
public static class ToolCallResultBuilder
{
    /// <summary>
    /// Maps a handler return into a wire-shape <see cref="ToolCallResult"/>.
    /// </summary>
    /// <param name="result">The handler's return value.</param>
    /// <param name="toolCallId">Tool call id from <c>ToolCallContext.ToolCallId</c> (or the
    /// originating <c>ToolCallMessage</c>). May be null on call paths that don't carry one.</param>
    /// <param name="toolName">Function name. Optional but recommended — used by providers
    /// that surface <c>tool_name</c> separately from <c>tool_call_id</c>.</param>
    /// <param name="executionTarget">Distinguishes local function tools from provider-executed
    /// server tools. Defaults to <see cref="ExecutionTarget.LocalFunction"/>.</param>
    public static ToolCallResult FromHandlerResult(
        ToolHandlerResult result,
        string? toolCallId,
        string? toolName = null,
        ExecutionTarget executionTarget = ExecutionTarget.LocalFunction)
    {
        return result switch
        {
            ToolHandlerResult.Resolved r => new ToolCallResult(
                toolCallId,
                r.Payload.Text,
                r.Payload.ContentBlocks,
                executionTarget)
            {
                ToolName = toolName,
                IsError = r.Payload.IsError,
                ErrorCode = r.Payload.ErrorCode,
            },
            ToolHandlerResult.Deferred => new ToolCallResult(
                toolCallId,
                string.Empty,
                executionTarget)
            {
                ToolName = toolName,
                IsDeferred = true,
                DeferredAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            },
            _ => throw new InvalidOperationException(
                $"Unknown ToolHandlerResult variant '{result.GetType().Name}'."),
        };
    }
}
