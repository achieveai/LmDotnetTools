using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AchieveAi.LmDotnetTools.LmCore.Middleware;

/// <summary>
///     Middleware that injects tool/function definitions into the agent's request options.
///     This middleware does NOT execute tools - it only makes them available to the LLM.
///     Use with ToolCallExecutor for explicit tool execution in application code.
/// </summary>
/// <remarks>
///     The middleware always reads the current function set through a factory delegate so a
///     mutating upstream catalog (e.g. mid-session sub-agent template discovery) surfaces in
///     the next call's request options without needing to rebuild the middleware stack.
/// </remarks>
public class ToolCallInjectionMiddleware : IStreamingMiddleware
{
    private readonly Func<IEnumerable<FunctionContract>> _functionsFactory;
    private readonly ILogger<ToolCallInjectionMiddleware> _logger;

    /// <summary>
    ///     Creates a new instance of ToolCallInjectionMiddleware.
    /// </summary>
    /// <param name="functionsFactory">
    ///     Factory returning the current function contracts for each request. Invoked once per
    ///     <see cref="InvokeAsync"/> / <see cref="InvokeStreamingAsync"/> call so a mutable
    ///     upstream source (e.g. <c>MutableSubAgentTemplateSource</c>) is reflected on the
    ///     next call without rebuilding middleware.
    /// </param>
    /// <param name="name">Optional name for this middleware instance.</param>
    /// <param name="logger">Optional logger.</param>
    public ToolCallInjectionMiddleware(
        Func<IEnumerable<FunctionContract>> functionsFactory,
        string? name = null,
        ILogger<ToolCallInjectionMiddleware>? logger = null
    )
    {
        _functionsFactory = functionsFactory ?? throw new ArgumentNullException(nameof(functionsFactory));
        _logger = logger ?? NullLogger<ToolCallInjectionMiddleware>.Instance;
        Name = name ?? nameof(ToolCallInjectionMiddleware);
    }

    /// <summary>
    ///     Convenience overload for callers with a fixed function set. Captures
    ///     <paramref name="functions"/> in a no-op factory.
    /// </summary>
    public ToolCallInjectionMiddleware(
        IEnumerable<FunctionContract> functions,
        string? name = null,
        ILogger<ToolCallInjectionMiddleware>? logger = null
    )
        : this(() => functions, name, logger)
    {
        ArgumentNullException.ThrowIfNull(functions);
    }

    public string? Name { get; }

    public async Task<IEnumerable<IMessage>> InvokeAsync(
        MiddlewareContext context,
        IAgent agent,
        CancellationToken cancellationToken = default
    )
    {
        // Materialize once: the factory may iterate a thread-safe dictionary, and PrepareOptions +
        // the optional Debug log would otherwise re-enumerate it.
        var functions = _functionsFactory().ToList();

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("Injecting {FunctionCount} functions into request options", functions.Count);
        }

        // Clone options and add functions
        var modifiedOptions = PrepareOptions(context.Options, functions);

        // Generate reply with the modified options
        ArgumentNullException.ThrowIfNull(agent);
        var replies = await agent.GenerateReplyAsync(context.Messages, modifiedOptions, cancellationToken);

        return replies;
    }

    public async Task<IAsyncEnumerable<IMessage>> InvokeStreamingAsync(
        MiddlewareContext context,
        IStreamingAgent agent,
        CancellationToken cancellationToken = default
    )
    {
        var functions = _functionsFactory().ToList();

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("Injecting {FunctionCount} functions into streaming request options", functions.Count);
        }

        // Clone options and add functions
        var modifiedOptions = PrepareOptions(context.Options, functions);

        // Get the streaming response from the agent
        ArgumentNullException.ThrowIfNull(agent);
        var streamingResponse = await agent.GenerateReplyStreamingAsync(
            context.Messages,
            modifiedOptions,
            cancellationToken
        );

        return streamingResponse;
    }

    /// <summary>
    ///     Prepares the options by combining middleware functions with context options functions.
    /// </summary>
    private static GenerateReplyOptions PrepareOptions(
        GenerateReplyOptions? contextOptions,
        IEnumerable<FunctionContract> functions)
    {
        var options = contextOptions ?? new GenerateReplyOptions();
        var combinedFunctions = CombineFunctions(functions, options.Functions);
        return options with { Functions = combinedFunctions?.ToArray() };
    }

    /// <summary>
    ///     Combines middleware functions with option functions.
    /// </summary>
    private static IEnumerable<FunctionContract>? CombineFunctions(
        IEnumerable<FunctionContract>? middlewareFunctions,
        IEnumerable<FunctionContract>? optionFunctions)
    {
        return middlewareFunctions == null && optionFunctions == null ? null
            : middlewareFunctions == null ? optionFunctions
            : optionFunctions == null ? middlewareFunctions
            : middlewareFunctions.Concat(optionFunctions);
    }
}
