using AchieveAi.LmDotnetTools.LmCore.Messages;

namespace AchieveAi.LmDotnetTools.LmCore.Middleware;

/// <summary>
/// Adapters that bridge the new <see cref="ToolHandlerResult"/> handler shape to/from the
/// legacy <c>Func&lt;string, Task&lt;string&gt;&gt;</c> and
/// <c>Func&lt;string, Task&lt;ToolCallResult&gt;&gt;</c> shapes used by external integrations
/// (MCP server adapters, external SDK tool bridges) that cannot support deferred tool execution.
/// </summary>
/// <remarks>
/// Deferred tool execution requires an out-of-band resolution channel (see
/// <c>MultiTurnAgentLoop.ResolveToolCallAsync</c>). Components that lack one — external SDK
/// bridges, <c>FunctionCallMiddleware</c>-driven flows — should adapt new-shape handlers via
/// these helpers. Deferred returns surface as <see cref="NotSupportedException"/> at invocation
/// time.
/// </remarks>
public static class LegacyHandlerAdapter
{
    /// <summary>
    /// Wraps a single new-shape <see cref="ToolHandlerResult"/> handler into the legacy
    /// <c>Func&lt;string, Task&lt;string&gt;&gt;</c> shape. Throws
    /// <see cref="NotSupportedException"/> at invocation time on
    /// <see cref="ToolHandlerResult.Deferred"/>.
    /// </summary>
    public static Func<string, Task<string>> ToLegacyHandler(
        Func<string, Task<ToolHandlerResult>> handler,
        string toolKey = "<unknown>")
    {
        ArgumentNullException.ThrowIfNull(handler);
        return async args =>
        {
            var result = await handler(args);
            return result switch
            {
                ToolHandlerResult.Resolved r => r.Result.Result,
                ToolHandlerResult.Deferred => throw new NotSupportedException(
                    $"Tool '{toolKey}' returned a deferred result. Deferred tool execution is "
                    + "only supported when handlers are dispatched by MultiTurnAgentLoop."
                ),
                _ => throw new InvalidOperationException(
                    $"Unknown ToolHandlerResult variant '{result.GetType().Name}' for tool '{toolKey}'."
                ),
            };
        };
    }

    /// <summary>
    /// Wraps a handler dictionary of the new <see cref="ToolHandlerResult"/> shape into the
    /// legacy <c>Func&lt;string, Task&lt;string&gt;&gt;</c> shape. Each wrapped handler unwraps
    /// <see cref="ToolHandlerResult.Resolved"/> and throws <see cref="NotSupportedException"/>
    /// on <see cref="ToolHandlerResult.Deferred"/>.
    /// </summary>
    public static IDictionary<string, Func<string, Task<string>>> WrapToLegacyHandlers(
        IDictionary<string, Func<string, Task<ToolHandlerResult>>> source,
        IEqualityComparer<string>? keyComparer = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        var wrapped = new Dictionary<string, Func<string, Task<string>>>(
            source.Count,
            keyComparer ?? StringComparer.Ordinal);

        foreach (var kvp in source)
        {
            wrapped[kvp.Key] = ToLegacyHandler(kvp.Value, kvp.Key);
        }

        return wrapped;
    }

    /// <summary>
    /// Wraps a single legacy <c>Func&lt;string, Task&lt;string&gt;&gt;</c> handler into the new
    /// <see cref="ToolHandlerResult"/> shape. The wrapped handler always returns
    /// <see cref="ToolHandlerResult.Resolved"/> with a text-only <see cref="ToolCallResult"/>;
    /// legacy handlers cannot signal deferral.
    /// </summary>
    public static Func<string, Task<ToolHandlerResult>> ToNewHandler(
        Func<string, Task<string>> legacy)
    {
        ArgumentNullException.ThrowIfNull(legacy);
        return async args => new ToolHandlerResult.Resolved(new ToolCallResult(null, await legacy(args)));
    }

    /// <summary>
    /// Wraps a single legacy <c>Func&lt;string, Task&lt;ToolCallResult&gt;&gt;</c> handler into
    /// the new <see cref="ToolHandlerResult"/> shape. Useful for multi-modal handlers where the
    /// legacy callsite produces a <see cref="ToolCallResult"/> with content blocks; the wrapped
    /// handler forwards the entire <see cref="ToolCallResult"/> verbatim.
    /// </summary>
    public static Func<string, Task<ToolHandlerResult>> ToNewHandler(
        Func<string, Task<ToolCallResult>> legacy)
    {
        ArgumentNullException.ThrowIfNull(legacy);
        return async args => new ToolHandlerResult.Resolved(await legacy(args));
    }

    /// <summary>
    /// Wraps a handler dictionary of the legacy <c>Func&lt;string, Task&lt;string&gt;&gt;</c>
    /// shape into the new <see cref="ToolHandlerResult"/> shape. Each wrapped handler always
    /// resolves synchronously with a text-only <see cref="ToolCallResult"/>.
    /// </summary>
    public static IDictionary<string, Func<string, Task<ToolHandlerResult>>> WrapToNewHandlers(
        IDictionary<string, Func<string, Task<string>>> source,
        IEqualityComparer<string>? keyComparer = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        var wrapped = new Dictionary<string, Func<string, Task<ToolHandlerResult>>>(
            source.Count,
            keyComparer ?? StringComparer.Ordinal);

        foreach (var kvp in source)
        {
            wrapped[kvp.Key] = ToNewHandler(kvp.Value);
        }

        return wrapped;
    }
}
