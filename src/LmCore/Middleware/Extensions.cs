using AchieveAi.LmDotnetTools.LmCore.Agents;

namespace AchieveAi.LmDotnetTools.LmCore.Middleware;

public static class Extensions
{
    public static IAgent WithMiddleware(this IAgent agent, IMiddleware middleware)
    {
        return new MiddlewareWrappingAgent(agent, middleware);
    }

    public static IStreamingAgent WithMiddleware(this IStreamingAgent agent, IStreamingMiddleware middleware)
    {
        return new MiddlewareWrappingStreamingAgent(agent, middleware);
    }
}
