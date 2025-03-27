namespace AchieveAi.LmDotnetTools.LmCore.Middleware;

using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Messages;

public class MiddlewareWrappingStreamingAgent : IStreamingAgent
{
    private readonly IStreamingAgent _agent;
    private readonly IStreamingMiddleware _middleware;

    public MiddlewareWrappingStreamingAgent(
        IStreamingAgent agent,
        IStreamingMiddleware middleware)
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

    public Task<IAsyncEnumerable<IMessage>> GenerateReplyStreamingAsync(
        IEnumerable<IMessage> messages,
        GenerateReplyOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        return _middleware.InvokeStreamingAsync(
            new MiddlewareContext(messages, options),
            _agent,
            cancellationToken);
    }
}
