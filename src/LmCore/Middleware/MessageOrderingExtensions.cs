using AchieveAi.LmDotnetTools.LmCore.Agents;
using Microsoft.Extensions.Logging;

namespace AchieveAi.LmDotnetTools.LmCore.Middleware;

/// <summary>
///     Extension methods for easily wrapping agents with MessageOrderingMiddleware
/// </summary>
public static class MessageOrderingExtensions
{
    /// <summary>
    ///     Wraps a streaming agent with MessageOrderingMiddleware to assign messageOrderIdx to messages.
    ///     This should typically be the first middleware applied after the provider agent.
    /// </summary>
    /// <param name="agent">The agent to wrap</param>
    /// <param name="logger">Optional logger for the middleware</param>
    /// <returns>A new agent with MessageOrderingMiddleware inserted before the original agent</returns>
    public static IStreamingAgent WithMessageOrdering(
        this IStreamingAgent agent,
        ILogger<MessageOrderingMiddleware>? logger = null
    )
    {
        var middleware = new MessageOrderingMiddleware(logger: logger);
        return new MiddlewareWrappingStreamingAgent(agent, middleware);
    }

    /// <summary>
    ///     Wraps a non-streaming agent with MessageOrderingMiddleware to assign messageOrderIdx to messages.
    ///     This should typically be the first middleware applied after the provider agent.
    /// </summary>
    /// <param name="agent">The agent to wrap</param>
    /// <param name="logger">Optional logger for the middleware</param>
    /// <returns>A new agent with MessageOrderingMiddleware inserted before the original agent</returns>
    public static IAgent WithMessageOrdering(this IAgent agent, ILogger<MessageOrderingMiddleware>? logger = null)
    {
        var middleware = new MessageOrderingMiddleware(logger: logger);
        return new MiddlewareWrappingAgent(agent, middleware);
    }

    /// <summary>
    ///     Wraps a streaming agent with both MessageOrderingMiddleware and MessageAggregationMiddleware
    ///     in the correct order for backward compatibility with existing code.
    ///     Order: Agent → MessageOrdering → MessageAggregation
    /// </summary>
    /// <param name="agent">The agent to wrap</param>
    /// <param name="orderingLogger">Optional logger for the ordering middleware</param>
    /// <param name="aggregationLogger">Optional logger for the aggregation middleware</param>
    /// <returns>A new agent with both middlewares applied in the correct order</returns>
    public static IStreamingAgent WithMessageOrderingAndAggregation(
        this IStreamingAgent agent,
        ILogger<MessageOrderingMiddleware>? orderingLogger = null,
        ILogger<MessageAggregationMiddleware>? aggregationLogger = null
    )
    {
        // Apply ordering first, then aggregation
        return agent.WithMessageOrdering(orderingLogger).WithMessageAggregation(aggregationLogger);
    }

    /// <summary>
    ///     Wraps a non-streaming agent with both MessageOrderingMiddleware and MessageAggregationMiddleware
    ///     in the correct order for backward compatibility with existing code.
    ///     Order: Agent → MessageOrdering → MessageAggregation
    /// </summary>
    /// <param name="agent">The agent to wrap</param>
    /// <param name="orderingLogger">Optional logger for the ordering middleware</param>
    /// <param name="aggregationLogger">Optional logger for the aggregation middleware</param>
    /// <returns>A new agent with both middlewares applied in the correct order</returns>
    public static IAgent WithMessageOrderingAndAggregation(
        this IAgent agent,
        ILogger<MessageOrderingMiddleware>? orderingLogger = null,
        ILogger<MessageAggregationMiddleware>? aggregationLogger = null
    )
    {
        // Apply ordering first, then aggregation
        return agent.WithMessageOrdering(orderingLogger).WithMessageAggregation(aggregationLogger);
    }
}
