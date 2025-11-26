using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AchieveAi.LmDotnetTools.LmCore.Middleware;

/// <summary>
/// Middleware that injects tool/function definitions into the agent's request options.
/// This middleware does NOT execute tools - it only makes them available to the LLM.
/// Use with ToolCallExecutor for explicit tool execution in application code.
/// </summary>
public class ToolCallInjectionMiddleware : IStreamingMiddleware
{
    private readonly IEnumerable<FunctionContract> _functions;
    private readonly ILogger<ToolCallInjectionMiddleware> _logger;

    /// <summary>
    /// Creates a new instance of ToolCallInjectionMiddleware
    /// </summary>
    /// <param name="functions">The function contracts to inject into requests</param>
    /// <param name="name">Optional name for this middleware instance</param>
    /// <param name="logger">Optional logger</param>
    public ToolCallInjectionMiddleware(
        IEnumerable<FunctionContract> functions,
        string? name = null,
        ILogger<ToolCallInjectionMiddleware>? logger = null
    )
    {
        _functions = functions ?? throw new ArgumentNullException(nameof(functions));
        _logger = logger ?? NullLogger<ToolCallInjectionMiddleware>.Instance;
        Name = name ?? nameof(ToolCallInjectionMiddleware);
    }

    public string? Name { get; }

    public async Task<IEnumerable<IMessage>> InvokeAsync(
        MiddlewareContext context,
        IAgent agent,
        CancellationToken cancellationToken = default
    )
    {
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug(
                "Injecting {FunctionCount} functions into request options",
                _functions.Count()
            );
        }

        // Clone options and add functions
        var modifiedOptions = PrepareOptions(context.Options);

        // Generate reply with the modified options
        var replies = await agent.GenerateReplyAsync(context.Messages, modifiedOptions, cancellationToken);

        return replies;
    }

    public async Task<IAsyncEnumerable<IMessage>> InvokeStreamingAsync(
        MiddlewareContext context,
        IStreamingAgent agent,
        CancellationToken cancellationToken = default
    )
    {
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug(
                "Injecting {FunctionCount} functions into streaming request options",
                _functions.Count()
            );
        }

        // Clone options and add functions
        var modifiedOptions = PrepareOptions(context.Options);

        // Get the streaming response from the agent
        var streamingResponse = await agent.GenerateReplyStreamingAsync(
            context.Messages,
            modifiedOptions,
            cancellationToken
        );

        return streamingResponse;
    }

    /// <summary>
    /// Prepares the options by combining middleware functions with context options functions
    /// </summary>
    private GenerateReplyOptions PrepareOptions(GenerateReplyOptions? contextOptions)
    {
        var options = contextOptions ?? new GenerateReplyOptions();
        var combinedFunctions = CombineFunctions(options.Functions);
        return options with { Functions = combinedFunctions?.ToArray() };
    }

    /// <summary>
    /// Combines middleware functions with option functions
    /// </summary>
    private IEnumerable<FunctionContract>? CombineFunctions(IEnumerable<FunctionContract>? optionFunctions)
    {
        if (_functions == null && optionFunctions == null)
        {
            return null;
        }

        return _functions == null ? optionFunctions
            : optionFunctions == null ? _functions
            : _functions.Concat(optionFunctions);
    }
}
