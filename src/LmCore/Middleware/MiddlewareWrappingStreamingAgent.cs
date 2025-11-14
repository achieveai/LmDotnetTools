namespace AchieveAi.LmDotnetTools.LmCore.Middleware;

using System.Diagnostics;
using System.Runtime.CompilerServices;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

public class MiddlewareWrappingStreamingAgent : IStreamingAgent
{
    private readonly IStreamingAgent _agent;
    private readonly Func<MiddlewareContext, IAgent, CancellationToken, Task<IEnumerable<IMessage>>>? _middleware;
    private readonly Func<
        MiddlewareContext,
        IStreamingAgent,
        CancellationToken,
        Task<IAsyncEnumerable<IMessage>>
    > _streamingMiddleware;
    private readonly string _middlewareName;
    private readonly string _creationStackTrace;
    private static ILogger<MiddlewareWrappingStreamingAgent> _logger =
        NullLogger<MiddlewareWrappingStreamingAgent>.Instance;

    /// <summary>
    /// Sets the logger to use for all MiddlewareWrappingStreamingAgent instances.
    /// This should be called once at application startup if logging is desired.
    /// </summary>
    public static void SetLogger(ILogger<MiddlewareWrappingStreamingAgent> logger)
    {
        _logger = logger ?? NullLogger<MiddlewareWrappingStreamingAgent>.Instance;
    }

    public MiddlewareWrappingStreamingAgent(IStreamingAgent agent, IStreamingMiddleware middleware)
    {
        _agent = agent;
        _middleware = middleware.InvokeAsync;
        _streamingMiddleware = middleware.InvokeStreamingAsync;
        _middlewareName = middleware.Name ?? middleware.GetType().Name;
        _creationStackTrace = Environment.StackTrace;

        if (_logger.IsEnabled(LogLevel.Debug))
            _logger.LogDebug("MiddlewareWrappingStreamingAgent created: Middleware={MiddlewareName}", _middlewareName);
    }

    public MiddlewareWrappingStreamingAgent(
        IStreamingAgent agent,
        Func<MiddlewareContext, IAgent, CancellationToken, Task<IEnumerable<IMessage>>> middleware,
        Func<
            MiddlewareContext,
            IStreamingAgent,
            CancellationToken,
            Task<IAsyncEnumerable<IMessage>>
        > streamingMiddleware
    )
    {
        _agent = agent;
        _middleware = middleware;
        _streamingMiddleware = streamingMiddleware;
        _middlewareName = "CustomMiddleware";
        _creationStackTrace = Environment.StackTrace;

        if (_logger.IsEnabled(LogLevel.Debug))
            _logger.LogDebug("MiddlewareWrappingStreamingAgent created: Middleware={MiddlewareName}", _middlewareName);
    }

    public Task<IEnumerable<IMessage>> GenerateReplyAsync(
        IEnumerable<IMessage> messages,
        GenerateReplyOptions? options = null,
        CancellationToken cancellationToken = default
    )
    {
        return _middleware is null
            ? _agent.GenerateReplyAsync(messages, options, cancellationToken)
            : _middleware(new MiddlewareContext(messages, options), _agent, cancellationToken);
    }

    public async Task<IAsyncEnumerable<IMessage>> GenerateReplyStreamingAsync(
        IEnumerable<IMessage> messages,
        GenerateReplyOptions? options = null,
        CancellationToken cancellationToken = default
    )
    {
        var correlationId = Guid.NewGuid().ToString("N")[..8];
        _logger.LogDebug(
            "[{CorrelationId}] Middleware '{MiddlewareName}' starting streaming invocation",
            correlationId,
            _middlewareName
        );

        var messageStream = await _streamingMiddleware(
            new MiddlewareContext(messages, options),
            _agent,
            cancellationToken
        );

        return MonitoredStreamWrapper(messageStream, correlationId, cancellationToken);
    }

    private async IAsyncEnumerable<IMessage> MonitoredStreamWrapper(
        IAsyncEnumerable<IMessage> sourceStream,
        string correlationId,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        var startTime = DateTime.UtcNow;
        var lastYieldTime = DateTime.UtcNow;
        var messageCount = 0;
        var activeYieldCount = 0;
        var timeoutThreshold = TimeSpan.FromSeconds(30);

        // Start monitoring task
        using var monitorCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var monitorTask = Task.Run(
            async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(2), monitorCts.Token); // Initial delay

                while (!monitorCts.Token.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(5), monitorCts.Token);

                        var timeSinceLastYield = DateTime.UtcNow - lastYieldTime;
                        var currentActiveYields = Interlocked.CompareExchange(ref activeYieldCount, 0, 0);

                        if (currentActiveYields > 0 && timeSinceLastYield > timeoutThreshold)
                        {
                            _logger.LogError(
                                "[{CorrelationId}] MIDDLEWARE STUCK: '{MiddlewareName}' has been yielding for {Duration:F1}s\n"
                                    + "Messages yielded so far: {MessageCount}\n"
                                    + "Creation stack trace:\n{StackTrace}",
                                correlationId,
                                _middlewareName,
                                timeSinceLastYield.TotalSeconds,
                                messageCount,
                                _creationStackTrace
                            );
                        }
                        else if (timeSinceLastYield > timeoutThreshold.Multiply(0.5))
                        {
                            _logger.LogWarning(
                                "[{CorrelationId}] Middleware '{MiddlewareName}' slow: No yield for {Duration:F1}s, ActiveYields={ActiveYields}",
                                correlationId,
                                _middlewareName,
                                timeSinceLastYield.TotalSeconds,
                                currentActiveYields
                            );
                        }
                        else
                        {
                            _logger.LogTrace(
                                "[{CorrelationId}] Middleware '{MiddlewareName}' healthy: LastYield={Duration:F1}s ago, Messages={MessageCount}",
                                correlationId,
                                _middlewareName,
                                timeSinceLastYield.TotalSeconds,
                                messageCount
                            );
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(
                            ex,
                            "[{CorrelationId}] Monitor task error for middleware '{MiddlewareName}'",
                            correlationId,
                            _middlewareName
                        );
                    }
                }
            },
            monitorCts.Token
        );

        try
        {
            await foreach (var message in sourceStream.WithCancellation(cancellationToken))
            {
                Interlocked.Increment(ref activeYieldCount);
                var yieldStartTime = DateTime.UtcNow;

                try
                {
                    _logger.LogTrace(
                        "[{CorrelationId}] Middleware '{MiddlewareName}' yielding message #{MessageNum}",
                        correlationId,
                        _middlewareName,
                        messageCount + 1
                    );

                    yield return message;

                    messageCount++;
                    lastYieldTime = DateTime.UtcNow;
                    var yieldDuration = (lastYieldTime - yieldStartTime).TotalMilliseconds;

                    if (yieldDuration > 1000)
                    {
                        _logger.LogWarning(
                            "[{CorrelationId}] Middleware '{MiddlewareName}' slow yield: Message #{MessageNum} took {Duration}ms",
                            correlationId,
                            _middlewareName,
                            messageCount,
                            yieldDuration
                        );
                    }
                }
                finally
                {
                    Interlocked.Decrement(ref activeYieldCount);
                }
            }

            _logger.LogDebug(
                "[{CorrelationId}] Middleware '{MiddlewareName}' completed: TotalMessages={MessageCount}, Duration={Duration}ms",
                correlationId,
                _middlewareName,
                messageCount,
                (DateTime.UtcNow - startTime).TotalMilliseconds
            );
        }
        finally
        {
            monitorCts.Cancel();
            try
            {
                await monitorTask.ConfigureAwait(false);
            }
            catch { }
        }
    }
}
