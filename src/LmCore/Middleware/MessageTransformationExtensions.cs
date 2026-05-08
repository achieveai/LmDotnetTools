using AchieveAi.LmDotnetTools.LmCore.Agents;
using Microsoft.Extensions.Logging;

namespace AchieveAi.LmDotnetTools.LmCore.Middleware;

/// <summary>
///     Extension methods for easily wrapping agents with MessageTransformationMiddleware
/// </summary>
public static class MessageTransformationExtensions
{
    /// <summary>
    ///     Wraps a streaming agent with MessageTransformationMiddleware for bidirectional message transformation:
    ///     - Downstream (Provider → App): Assigns messageOrderIdx
    ///     - Upstream (App → Provider): Reconstructs aggregates
    ///     This single middleware handles both the new simplified flow and backward compatibility.
    /// </summary>
    /// <param name="agent">The agent to wrap</param>
    /// <param name="logger">Optional logger for the middleware</param>
    /// <returns>A new agent with MessageTransformationMiddleware inserted</returns>
    public static IStreamingAgent WithMessageTransformation(
        this IStreamingAgent agent,
        ILogger<MessageTransformationMiddleware>? logger = null
    )
    {
        var middleware = new MessageTransformationMiddleware(logger: logger);
        return new MiddlewareWrappingStreamingAgent(agent, middleware);
    }

    /// <summary>
    ///     Wraps a non-streaming agent with MessageTransformationMiddleware for bidirectional message transformation:
    ///     - Downstream (Provider → App): Assigns messageOrderIdx
    ///     - Upstream (App → Provider): Reconstructs aggregates
    ///     This single middleware handles both the new simplified flow and backward compatibility.
    /// </summary>
    /// <param name="agent">The agent to wrap</param>
    /// <param name="logger">Optional logger for the middleware</param>
    /// <returns>A new agent with MessageTransformationMiddleware inserted</returns>
    public static IAgent WithMessageTransformation(
        this IAgent agent,
        ILogger<MessageTransformationMiddleware>? logger = null
    )
    {
        var middleware = new MessageTransformationMiddleware(logger: logger);
        return new MiddlewareWrappingAgent(agent, middleware);
    }
}
