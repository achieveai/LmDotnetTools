using AchieveAi.LmDotnetTools.AgUi.Protocol.Middleware;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using Microsoft.Extensions.DependencyInjection;

namespace AchieveAi.LmDotnetTools.AgUi.Protocol.Configuration;

/// <summary>
///     Provides configuration methods for wiring AG-UI middleware with proper callback integration.
/// </summary>
/// <remarks>
///     This class solves the critical architectural issue of tool callback wiring.
///     The AgUiStreamingMiddleware implements IToolResultCallback, but it needs to be
///     registered with FunctionCallMiddleware to receive tool execution notifications.
///     This class provides extension methods to properly wire these components together.
/// </remarks>
public static class AgUiMiddlewareConfiguration
{
    /// <summary>
    ///     Configures an agent with the AG-UI middleware chain, including proper callback wiring.
    ///     This method wires the AG-UI middleware as a tool result callback to the FunctionCallMiddleware,
    ///     ensuring that tool execution events are captured and published in real-time.
    /// </summary>
    /// <param name="agent">The agent to configure</param>
    /// <param name="services">The service provider containing middleware instances</param>
    /// <param name="includeJsonFragmentMiddleware">
    ///     Whether to include JsonFragmentUpdateMiddleware in the chain (default: true).
    ///     Set to false if you're manually configuring the middleware chain.
    /// </param>
    /// <returns>The agent with AG-UI middleware configured</returns>
    /// <remarks>
    ///     The middleware chain is built in this specific order:
    ///     1. JsonFragmentUpdateMiddleware (optional) - Processes JSON fragments in tool arguments
    ///     2. AgUiStreamingMiddleware - Publishes AG-UI events
    ///     3. FunctionRegistry middleware - Provides function contracts
    ///     4. FunctionCallMiddleware - Executes tool calls (WITH callback to AgUiStreamingMiddleware)
    ///     5. MessageUpdateJoinerMiddleware - Joins message updates
    ///     This order is critical for proper event flow and tool callback integration.
    /// </remarks>
    /// <example>
    ///     <code>
    /// var agent = serviceProvider.GetRequiredService&lt;IStreamingAgent&gt;();
    /// agent = agent.ConfigureWithAgUi(serviceProvider);
    /// </code>
    /// </example>
    public static IStreamingAgent ConfigureWithAgUi(
        this IStreamingAgent agent,
        IServiceProvider services,
        bool includeJsonFragmentMiddleware = true
    )
    {
        ArgumentNullException.ThrowIfNull(agent, nameof(agent));
        ArgumentNullException.ThrowIfNull(services, nameof(services));

        // Get required middleware instances from DI
        var agUiMiddleware = services.GetRequiredService<AgUiStreamingMiddleware>();
        var functionCallMiddleware = services.GetRequiredService<FunctionCallMiddleware>();

        // CRITICAL: Wire the AG-UI middleware as a callback to FunctionCallMiddleware
        // This enables real-time tool execution event capture
        _ = functionCallMiddleware.WithResultCallback(agUiMiddleware);

        // Build the middleware chain in the correct order
        var configuredAgent = agent;

        // Optionally add JSON fragment middleware
        if (includeJsonFragmentMiddleware)
        {
            var jsonFragmentMiddleware = services.GetService<JsonFragmentUpdateMiddleware>();
            if (jsonFragmentMiddleware != null)
            {
                configuredAgent = configuredAgent.WithMiddleware(jsonFragmentMiddleware);
            }
        }

        // Add AG-UI middleware
        configuredAgent = configuredAgent.WithMiddleware(agUiMiddleware);

        // Add function-related middleware
        var functionRegistry = services.GetService<FunctionRegistry>();
        if (functionRegistry != null)
        {
            configuredAgent = configuredAgent.WithMiddleware(functionRegistry.BuildMiddleware());
        }

        configuredAgent = configuredAgent.WithMiddleware(functionCallMiddleware);

        // Add message joiner middleware
        var messageJoiner = services.GetService<MessageUpdateJoinerMiddleware>();
        if (messageJoiner != null)
        {
            configuredAgent = configuredAgent.WithMiddleware(messageJoiner);
        }

        return configuredAgent;
    }

    /// <summary>
    ///     Configures an agent with AG-UI middleware using manual middleware instances.
    ///     This overload is useful when you want to provide specific middleware instances
    ///     rather than resolving them from DI.
    /// </summary>
    /// <param name="agent">The agent to configure</param>
    /// <param name="agUiMiddleware">The AG-UI streaming middleware instance</param>
    /// <param name="functionCallMiddleware">The function call middleware instance</param>
    /// <param name="jsonFragmentMiddleware">Optional JSON fragment middleware instance</param>
    /// <param name="functionRegistry">Optional function registry for tool contracts</param>
    /// <param name="messageJoiner">Optional message update joiner middleware</param>
    /// <returns>The agent with AG-UI middleware configured</returns>
    /// <example>
    ///     <code>
    /// var agent = new MyAgent();
    /// var agUiMiddleware = new AgUiStreamingMiddleware(eventPublisher, converter, logger);
    /// var functionCallMiddleware = new FunctionCallMiddleware(functions, functionMap, logger);
    ///
    /// agent = agent.ConfigureWithAgUi(
    ///     agUiMiddleware,
    ///     functionCallMiddleware,
    ///     jsonFragmentMiddleware,
    ///     functionRegistry,
    ///     messageJoiner);
    /// </code>
    /// </example>
    public static IStreamingAgent ConfigureWithAgUi(
        this IStreamingAgent agent,
        AgUiStreamingMiddleware agUiMiddleware,
        FunctionCallMiddleware functionCallMiddleware,
        JsonFragmentUpdateMiddleware? jsonFragmentMiddleware = null,
        FunctionRegistry? functionRegistry = null,
        MessageUpdateJoinerMiddleware? messageJoiner = null
    )
    {
        ArgumentNullException.ThrowIfNull(agent, nameof(agent));
        ArgumentNullException.ThrowIfNull(agUiMiddleware, nameof(agUiMiddleware));
        ArgumentNullException.ThrowIfNull(functionCallMiddleware, nameof(functionCallMiddleware));

        // CRITICAL: Wire the callback
        _ = functionCallMiddleware.WithResultCallback(agUiMiddleware);

        // Build the middleware chain
        var configuredAgent = agent;

        if (jsonFragmentMiddleware != null)
        {
            configuredAgent = configuredAgent.WithMiddleware(jsonFragmentMiddleware);
        }

        configuredAgent = configuredAgent.WithMiddleware(agUiMiddleware);

        if (functionRegistry != null)
        {
            configuredAgent = configuredAgent.WithMiddleware(functionRegistry.BuildMiddleware());
        }

        configuredAgent = configuredAgent.WithMiddleware(functionCallMiddleware);

        if (messageJoiner != null)
        {
            configuredAgent = configuredAgent.WithMiddleware(messageJoiner);
        }

        return configuredAgent;
    }

    /// <summary>
    ///     Wires the AG-UI middleware as a tool result callback to the function call middleware.
    ///     This is a lower-level method for cases where you need fine-grained control over
    ///     the middleware chain configuration.
    /// </summary>
    /// <param name="functionCallMiddleware">The function call middleware</param>
    /// <param name="agUiMiddleware">The AG-UI middleware to wire as callback</param>
    /// <returns>The function call middleware with callback wired</returns>
    /// <remarks>
    ///     Use this method when you're building a custom middleware chain and need to
    ///     wire the callback manually. For most cases, use ConfigureWithAgUi instead.
    /// </remarks>
    public static FunctionCallMiddleware WithAgUiCallback(
        this FunctionCallMiddleware functionCallMiddleware,
        AgUiStreamingMiddleware agUiMiddleware
    )
    {
        ArgumentNullException.ThrowIfNull(functionCallMiddleware, nameof(functionCallMiddleware));
        ArgumentNullException.ThrowIfNull(agUiMiddleware, nameof(agUiMiddleware));

        return functionCallMiddleware.WithResultCallback(agUiMiddleware);
    }
}
