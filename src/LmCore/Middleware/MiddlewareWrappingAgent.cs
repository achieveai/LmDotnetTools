namespace AchieveAi.LmDotnetTools.LmCore.Middleware;

using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Messages;

public class MiddlewareWrappingAgent : IAgent
{
    private readonly IAgent _agent;
    private readonly IMiddleware _middleware;

    public MiddlewareWrappingAgent(
        IAgent agent,
        IMiddleware middleware)
    {
        _agent = agent;
        _middleware = middleware;
    }

    public Task<IMessage> GenerateReplyAsync(
        IEnumerable<IMessage> messages,
        GenerateReplyOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        return _middleware.InvokeAsync(
            new MiddlewareContext(messages, options),
            _agent,
            cancellationToken);
    }
}
