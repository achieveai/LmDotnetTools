namespace AchieveAi.LmDotnetTools.LmCore.Middleware;

using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Messages;

public class MiddlewareWrappingStreamingAgent : IStreamingAgent
{
    private readonly IStreamingAgent _agent;
    private readonly Func<MiddlewareContext, IAgent, CancellationToken, Task<IEnumerable<IMessage>>>? _middleware;
    private readonly Func<MiddlewareContext, IStreamingAgent, CancellationToken, Task<IAsyncEnumerable<IMessage>>> _streamingMiddleware;

    public MiddlewareWrappingStreamingAgent(
        IStreamingAgent agent,
        IStreamingMiddleware middleware)
    {
        _agent = agent;
        _middleware = middleware.InvokeAsync;
        _streamingMiddleware = middleware.InvokeStreamingAsync;
    }

    public MiddlewareWrappingStreamingAgent(
        IStreamingAgent agent,
        Func<MiddlewareContext, IAgent, CancellationToken, Task<IEnumerable<IMessage>>> middleware,
        Func<MiddlewareContext, IStreamingAgent, CancellationToken, Task<IAsyncEnumerable<IMessage>>> streamingMiddleware)
    {
        _agent = agent;
        _middleware = middleware;
        _streamingMiddleware = streamingMiddleware;
    }

    public Task<IEnumerable<IMessage>> GenerateReplyAsync(
        IEnumerable<IMessage> messages,
        GenerateReplyOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (_middleware is null)
        {
            return _agent.GenerateReplyAsync(messages, options, cancellationToken);
        }

        return _middleware(
                new MiddlewareContext(messages, options),
                _agent,
                cancellationToken);
    }

    public Task<IAsyncEnumerable<IMessage>> GenerateReplyStreamingAsync(
        IEnumerable<IMessage> messages,
        GenerateReplyOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        return _streamingMiddleware(
            new MiddlewareContext(messages, options),
            _agent,
            cancellationToken);
    }
}
