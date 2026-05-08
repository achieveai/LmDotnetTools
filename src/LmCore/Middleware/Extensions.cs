using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Messages;

namespace AchieveAi.LmDotnetTools.LmCore.Middleware;

public static class Extensions
{
    public static IAgent WithMiddleware(this IAgent agent, IMiddleware middleware)
    {
        return new MiddlewareWrappingAgent(agent, middleware);
    }

    public static IAgent WithMiddleware(
        this IAgent agent,
        Func<MiddlewareContext, IAgent, CancellationToken, Task<IEnumerable<IMessage>>> middleware
    )
    {
        return new MiddlewareWrappingAgent(agent, middleware);
    }

    public static IStreamingAgent WithMiddleware(
        this IStreamingAgent agent,
        Func<MiddlewareContext, IAgent, CancellationToken, Task<IEnumerable<IMessage>>> middleware,
        Func<
            MiddlewareContext,
            IStreamingAgent,
            CancellationToken,
            Task<IAsyncEnumerable<IMessage>>
        > streamingMiddleware
    )
    {
        return new MiddlewareWrappingStreamingAgent(agent, middleware, streamingMiddleware);
    }

    public static IStreamingAgent WithMiddleware(this IStreamingAgent agent, IStreamingMiddleware middleware)
    {
        return new MiddlewareWrappingStreamingAgent(agent, middleware);
    }
}
