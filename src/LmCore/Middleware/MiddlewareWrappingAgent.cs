using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;

namespace AchieveAi.LmDotnetTools.LmCore.Middleware;

public class MiddlewareWrappingAgent : IAgent
{
    private readonly IAgent _agent;
    private readonly Func<MiddlewareContext, IAgent, CancellationToken, Task<IEnumerable<IMessage>>> _middleware;

    public MiddlewareWrappingAgent(IAgent agent, IMiddleware middleware)
    {
        _agent = agent;
        _middleware = middleware.InvokeAsync;
    }

    public MiddlewareWrappingAgent(
        IAgent agent,
        Func<MiddlewareContext, IAgent, CancellationToken, Task<IEnumerable<IMessage>>> middleware
    )
    {
        _agent = agent;
        _middleware = middleware;
    }

    public Task<IEnumerable<IMessage>> GenerateReplyAsync(
        IEnumerable<IMessage> messages,
        GenerateReplyOptions? options = null,
        CancellationToken cancellationToken = default
    )
    {
        return _middleware(new MiddlewareContext(messages, options), _agent, cancellationToken);
    }
}
