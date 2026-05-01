using System.Collections.Immutable;

namespace AchieveAi.LmDotnetTools.LmCore.Messages;

/// <summary>
/// Result of a tool handler invocation. Either a synchronous (resolved) <see cref="ToolCallResult"/>
/// whose value is fed back to the LLM on the next turn, or a deferred placeholder that the loop
/// records as <see cref="ToolCallResultMessage.IsDeferred"/> and resolves later via
/// <c>MultiTurnAgentLoop.ResolveToolCallAsync</c>.
/// </summary>
/// <remarks>
/// Deferred tool execution is only supported when handlers are dispatched by
/// <c>MultiTurnAgentLoop</c>. <c>FunctionCallMiddleware</c>-driven flows have no
/// resolution channel and treat <see cref="Deferred"/> as an error.
/// </remarks>
public abstract record ToolHandlerResult
{
    private ToolHandlerResult() { }

    /// <summary>
    /// Tool finished synchronously. The wrapped <see cref="ToolCallResult"/> carries the text
    /// payload, optional <see cref="ToolCallResult.ContentBlocks"/> (images / multi-modal),
    /// and any error flags. The loop reads through to those fields when populating
    /// <see cref="ToolCallResultMessage"/>.
    /// </summary>
    public sealed record Resolved(ToolCallResult Result) : ToolHandlerResult;

    /// <summary>
    /// Tool execution is deferred. The handler has initiated some out-of-process
    /// work; the loop will write <paramref name="Placeholder"/> into history with
    /// <see cref="ToolCallResultMessage.IsDeferred"/> set, end the run after the
    /// current turn, and wait for an external caller to invoke
    /// <c>MultiTurnAgentLoop.ResolveToolCallAsync</c>.
    /// </summary>
    /// <param name="Placeholder">Text recorded as the (interim) tool_result. Required
    /// because the LLM protocol mandates a tool_result for every tool_use ID.</param>
    /// <param name="Metadata">Opaque host-supplied metadata round-tripped to
    /// <c>DeferredToolCallInfo</c> (e.g., correlation IDs, expected wait time).</param>
    public sealed record Deferred(
        string Placeholder,
        ImmutableDictionary<string, string>? Metadata = null) : ToolHandlerResult;

    /// <summary>
    /// Convenience wrapper so call sites can write
    /// <c>Task.FromResult(ToolHandlerResult.FromString("..."))</c> without spelling out
    /// the <see cref="ToolCallResult"/> ctor.
    /// </summary>
    public static Resolved FromString(string result)
    {
        return new Resolved(new ToolCallResult(null, result));
    }

    /// <summary>
    /// Extracts the resolved result text. Throws <see cref="InvalidOperationException"/>
    /// if this is a <see cref="Deferred"/> case. Useful in tests and in code paths that
    /// have already gated out the deferred branch.
    /// </summary>
    public string ResultText
    {
        get
        {
            return this switch
            {
                Resolved r => r.Result.Result,
                Deferred => throw new InvalidOperationException(
                    "Cannot extract result text from a deferred ToolHandlerResult — it has not been resolved yet."),
                _ => throw new InvalidOperationException(
                    $"Unknown ToolHandlerResult variant '{GetType().Name}'."),
            };
        }
    }

    /// <summary>
    /// Implicit conversion lets handlers return a bare string and have it auto-wrapped
    /// as <see cref="Resolved"/> with a text-only <see cref="ToolCallResult"/>.
    /// </summary>
    public static implicit operator ToolHandlerResult(string result)
    {
        return new Resolved(new ToolCallResult(null, result));
    }

    /// <summary>
    /// Implicit conversion lets handlers return a <see cref="ToolCallResult"/> directly
    /// — useful when the handler has populated <see cref="ToolCallResult.ContentBlocks"/>
    /// or other fields and wants the loop to forward them verbatim.
    /// </summary>
    public static implicit operator ToolHandlerResult(ToolCallResult result)
    {
        return new Resolved(result);
    }
}
