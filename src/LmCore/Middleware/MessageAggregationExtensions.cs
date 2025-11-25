using AchieveAi.LmDotnetTools.LmCore.Agents;
using Microsoft.Extensions.Logging;

namespace AchieveAi.LmDotnetTools.LmCore.Middleware;

/// <summary>
///     Extension methods for easily wrapping agents with MessageAggregationMiddleware
/// </summary>
public static class MessageAggregationExtensions
{
    /// <summary>
    ///     Wraps a streaming agent with MessageAggregationMiddleware to enable backward compatibility
    ///     with provider transformations that expect CompositeMessage and ToolsCallAggregateMessage.
    /// </summary>
    /// <param name="agent">The agent to wrap</param>
    /// <param name="logger">Optional logger for the middleware</param>
    /// <returns>A new agent with MessageAggregationMiddleware inserted before the original agent</returns>
    public static IStreamingAgent WithMessageAggregation(
        this IStreamingAgent agent,
        ILogger<MessageAggregationMiddleware>? logger = null
    )
    {
        var middleware = new MessageAggregationMiddleware(logger: logger);
        return new MiddlewareWrappingStreamingAgent(agent, middleware);
    }

    /// <summary>
    ///     Wraps a non-streaming agent with MessageAggregationMiddleware to enable backward compatibility
    ///     with provider transformations that expect CompositeMessage and ToolsCallAggregateMessage.
    /// </summary>
    /// <param name="agent">The agent to wrap</param>
    /// <param name="logger">Optional logger for the middleware</param>
    /// <returns>A new agent with MessageAggregationMiddleware inserted before the original agent</returns>
    public static IAgent WithMessageAggregation(this IAgent agent, ILogger<MessageAggregationMiddleware>? logger = null)
    {
        var middleware = new MessageAggregationMiddleware(logger: logger);
        return new MiddlewareWrappingAgent(agent, middleware);
    }
}
