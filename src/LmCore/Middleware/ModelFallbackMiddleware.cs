using System.Collections.Immutable;
using System.Net;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AchieveAi.LmDotnetTools.LmCore.Middleware;

/// <summary>
///     One model to try when the one before it fails: the agent that serves it, its model id, and an
///     optional reshape of the request for it (built-in tools, reasoning options) beyond the model id.
/// </summary>
/// <param name="ModelId">The model this target runs; it replaces the request's model id.</param>
/// <param name="Agent">The agent that serves <paramref name="ModelId" />.</param>
/// <param name="ShapeOptions">
///     Reshapes the request for this target after its model id is set. Null sends the request as is. It runs
///     for every target before the primary is called, so it must not throw.
/// </param>
public sealed record FallbackTarget(
    string ModelId,
    IStreamingAgent Agent,
    Func<GenerateReplyOptions, GenerateReplyOptions>? ShapeOptions = null
);

/// <summary>
///     Middleware that tries another model when one fails: either a chain of <see cref="FallbackTarget" />s
///     after the wrapped agent, or the agents a model name maps to, then a default.
/// </summary>
/// <remarks>
///     <para>
///         Two shapes. The model-map constructor picks agents by the request's model id and falls back
///         on any failure. The chain constructor runs the wrapped agent first, then each
///         <see cref="FallbackTarget" /> in order, with the request's model id replaced by the target's,
///         and by default falls back only on a provider outage (<see cref="IsProviderOutage" />).
///     </para>
///     <para>
///         Both shapes never fall back on the caller's own cancellation, and never after a reply has
///         started: a streamed reply is read up to its first message before an agent counts as working,
///         because provider streams are lazy and send their request on that first read. A failure after
///         the first message reaches the caller as is, since output from one model cannot be
///         continued by another.
///     </para>
/// </remarks>
public class ModelFallbackMiddleware : IStreamingMiddleware
{
    private readonly IAgent? _defaultAgent;
    private readonly IReadOnlyList<FallbackTarget>? _fallbacks;
    private readonly ILogger _logger;
    private readonly Dictionary<string, IAgent[]>? _modelAgentMap;
    private readonly Func<Exception, bool> _shouldFallBack;
    private readonly bool _tryDefaultLast;

    /// <summary>
    ///     Initializes a new instance of the <see cref="ModelFallbackMiddleware" /> class.
    /// </summary>
    /// <param name="modelAgentMap">A dictionary mapping model names to arrays of agents to try in order.</param>
    /// <param name="defaultAgent">The default agent to use if no mapping is found for the model.</param>
    /// <param name="tryDefaultLast">If true, the default agent will be tried as a last resort after all mapped agents fail.</param>
    /// <param name="name">Optional name for the middleware.</param>
    public ModelFallbackMiddleware(
        Dictionary<string, IAgent[]> modelAgentMap,
        IAgent defaultAgent,
        bool tryDefaultLast = true,
        string? name = null
    )
    {
        _modelAgentMap = modelAgentMap ?? throw new ArgumentNullException(nameof(modelAgentMap));
        _defaultAgent = defaultAgent ?? throw new ArgumentNullException(nameof(defaultAgent));
        _tryDefaultLast = tryDefaultLast;
        _shouldFallBack = static _ => true;
        _logger = NullLogger.Instance;
        Name = name ?? nameof(ModelFallbackMiddleware);
    }

    /// <summary>
    ///     Runs the wrapped agent first and, when it fails before replying, each of
    ///     <paramref name="fallbacks" /> in order.
    /// </summary>
    /// <param name="fallbacks">The models to try after the wrapped agent, in order.</param>
    /// <param name="logger">Receives one warning per fallback hop: from-model, to-model and reason.</param>
    /// <param name="shouldFallBack">
    ///     Which failures move on to the next model. Defaults to <see cref="IsProviderOutage" />. The
    ///     caller's own cancellation never falls back, whatever this returns.
    /// </param>
    /// <param name="name">Optional name for the middleware.</param>
    public ModelFallbackMiddleware(
        IReadOnlyList<FallbackTarget> fallbacks,
        ILogger? logger = null,
        Func<Exception, bool>? shouldFallBack = null,
        string? name = null
    )
    {
        ArgumentNullException.ThrowIfNull(fallbacks);
        _fallbacks = fallbacks;
        _shouldFallBack = shouldFallBack ?? IsProviderOutage;
        _logger = logger ?? NullLogger.Instance;
        Name = name ?? nameof(ModelFallbackMiddleware);
    }

    /// <summary>
    ///     Gets the name of the middleware.
    /// </summary>
    public string? Name { get; }

    /// <summary>
    ///     The chain constructor's default trigger: true when the provider could not serve the request
    ///     right now, so another model may.
    /// </summary>
    /// <remarks>
    ///     Falls back on: HTTP 408, 429 and every 5xx; a transport failure with no status (connection
    ///     refused or reset, DNS); any <see cref="IOException" />, which includes
    ///     <see cref="HttpIOException" /> and is how a response body that breaks while it is read surfaces; a timeout
    ///     (<see cref="TimeoutException" />, or any <see cref="OperationCanceledException" />, which is how an
    ///     <see cref="HttpClient" /> timeout surfaces). The caller's own cancellation never reaches this
    ///     rule: the chain stops on it first. Does not fall back on any other 4xx (a bad request, auth or a
    ///     missing model fails the same way on the next model, or should not be retried), or on any other
    ///     exception (a bug or a malformed response). By the time this runs, the provider's own retry
    ///     policy has already been spent.
    /// </remarks>
    public static bool IsProviderOutage(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception switch
        {
            HttpRequestException { StatusCode: { } status } => status
                is HttpStatusCode.RequestTimeout
                    or HttpStatusCode.TooManyRequests
                || (int)status >= 500,
            HttpRequestException => true,
            IOException => true,
            TimeoutException => true,
            OperationCanceledException => true,
            _ => false,
        };
    }

    /// <summary>
    ///     Tries each agent the constructor's shape selects, in order, until one replies.
    /// </summary>
    public async Task<IEnumerable<IMessage>> InvokeAsync(
        MiddlewareContext context,
        IAgent agent,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(agent);
        var attempts = BuildAttempts(context, agent);
        Exception? earlier = null;
        for (var i = 0; ; i++)
        {
            var attempt = attempts[i];
            try
            {
                var replies = await attempt.Agent.GenerateReplyAsync(
                    context.Messages,
                    attempt.Options,
                    cancellationToken
                );
                return attempt.FallbackModelId is { } ranModel
                    ? [.. replies.Select(reply => CreditUsage(reply, ranModel))]
                    : replies;
            }
            catch (Exception ex) when (CanFallBack(ex, i, attempts.Count, cancellationToken))
            {
                LogHop(attempt, attempts[i + 1], ex);
                earlier = ex;
            }
            catch (Exception ex) when (ReportsEarlierFailure(attempt, earlier, ex, cancellationToken))
            {
                ExceptionDispatchInfo.Capture(earlier!).Throw();
                throw;
            }
        }
    }

    /// <summary>
    ///     Tries each agent the constructor's shape selects, in order, until one starts a reply.
    /// </summary>
    /// <remarks>
    ///     The returned stream is already open: its first message has been read. Disposing its enumerator
    ///     releases the provider stream even before the first read, and a token passed to the enumeration
    ///     (<c>WithCancellation</c>) cancels the provider's reads just as <paramref name="cancellationToken" />
    ///     does.
    /// </remarks>
    public async Task<IAsyncEnumerable<IMessage>> InvokeStreamingAsync(
        MiddlewareContext context,
        IStreamingAgent agent,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(agent);
        var attempts = BuildAttempts(context, agent);

        // Every provider call and read runs on this token, so the enumeration's own token can be linked to
        // it once the caller starts reading.
        var reads = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            Exception? earlier = null;
            for (var i = 0; ; i++)
            {
                var attempt = attempts[i];
                IAsyncEnumerator<IMessage>? reply = null;
                try
                {
                    var stream = attempt.Agent is IStreamingAgent streamingAgent
                        ? await streamingAgent.GenerateReplyStreamingAsync(
                            context.Messages,
                            attempt.Options,
                            reads.Token
                        )
                        : ToAsyncEnumerableInternal(
                            await attempt.Agent.GenerateReplyAsync(context.Messages, attempt.Options, reads.Token),
                            reads.Token
                        );

                    // The request goes out on this first read, so this is where an outage shows.
                    reply = stream.GetAsyncEnumerator(reads.Token);
                    var started = await reply.MoveNextAsync();
                    return new StartedReply(reply, started, attempt.FallbackModelId, reads);
                }
                catch (Exception ex) when (CanFallBack(ex, i, attempts.Count, cancellationToken))
                {
                    await DisposeQuietlyAsync(reply);

                    // The caller may have stopped the turn while the failed attempt was being cleaned up.
                    cancellationToken.ThrowIfCancellationRequested();
                    LogHop(attempt, attempts[i + 1], ex);
                    earlier = ex;
                }
                catch (Exception ex) when (ReportsEarlierFailure(attempt, earlier, ex, cancellationToken))
                {
                    await DisposeQuietlyAsync(reply);
                    ExceptionDispatchInfo.Capture(earlier!).Throw();
                    throw;
                }
                catch
                {
                    await DisposeQuietlyAsync(reply);
                    throw;
                }
            }
        }
        catch
        {
            reads.Dispose();
            throw;
        }
    }

    /// <summary>The agents to try, in order, each with the request it gets.</summary>
    private List<Attempt> BuildAttempts(MiddlewareContext context, IAgent agent)
    {
        if (_fallbacks is not null)
        {
            var attempts = new List<Attempt>(_fallbacks.Count + 1)
            {
                new(agent, context.Options, context.Options?.ModelId, FallbackModelId: null),
            };
            foreach (var target in _fallbacks)
            {
                var options = (context.Options ?? new GenerateReplyOptions()) with { ModelId = target.ModelId };
                attempts.Add(
                    new Attempt(
                        target.Agent,
                        target.ShapeOptions is null ? options : target.ShapeOptions(options),
                        target.ModelId,
                        target.ModelId
                    )
                );
            }

            return attempts;
        }

        var modelId = context.Options?.ModelId;
        if (modelId == null)
        {
            return [new Attempt(agent, context.Options, null, null)];
        }

        if (_modelAgentMap!.TryGetValue(modelId, out var mapped) && mapped.Length > 0)
        {
            var attempts = mapped.Select(a => new Attempt(a, context.Options, modelId, null)).ToList();
            if (_tryDefaultLast)
            {
                // The default is the last resort: if it fails too, the mapped agent's failure is the one reported.
                attempts.Add(new Attempt(_defaultAgent!, context.Options, modelId, null, ReportsEarlierFailure: true));
            }

            return attempts;
        }

        return [new Attempt(_defaultAgent!, context.Options, modelId, null)];
    }

    private bool CanFallBack(Exception exception, int index, int count, CancellationToken cancellationToken) =>
        index < count - 1 && !IsCallerCancellation(exception, cancellationToken) && _shouldFallBack(exception);

    private static bool ReportsEarlierFailure(
        Attempt attempt,
        Exception? earlier,
        Exception exception,
        CancellationToken cancellationToken
    ) => attempt.ReportsEarlierFailure && earlier is not null && !IsCallerCancellation(exception, cancellationToken);

    private static bool IsCallerCancellation(Exception exception, CancellationToken cancellationToken) =>
        exception is OperationCanceledException && cancellationToken.IsCancellationRequested;

    private void LogHop(Attempt from, Attempt to, Exception exception)
    {
        _logger.LogWarning(
            "Model fallback: {FromModel} failed with {FallbackReason}; trying {ToModel}",
            from.ModelId ?? "(unspecified)",
            DescribeFailure(exception),
            to.ModelId ?? "(unspecified)"
        );
    }

    private static string DescribeFailure(Exception exception) =>
        exception switch
        {
            HttpRequestException { StatusCode: { } status } => $"HTTP {(int)status}",
            HttpRequestException => "a transport error",
            TimeoutException or OperationCanceledException => "a timeout",
            _ => exception.GetType().Name,
        };

    /// <summary>
    ///     Credits a fallback reply's usage to the model that ran. A provider that reports its own model
    ///     keeps it; one that reports none would otherwise be billed as the primary model.
    /// </summary>
    private static IMessage CreditUsage(IMessage message, string ranModel)
    {
        if (message is not UsageMessage usageMessage)
        {
            return message;
        }

        var usage = usageMessage.Usage;
        if (!string.IsNullOrEmpty(usage.GetExtraProperty<string>(Usage.ModelExtraProperty)))
        {
            return message;
        }

        var extras = usage.ExtraProperties ?? ImmutableDictionary<string, object?>.Empty;
        return usageMessage with
        {
            Usage = usage with { ExtraProperties = extras.SetItem(Usage.ModelExtraProperty, ranModel) },
        };
    }

    private static async ValueTask DisposeQuietlyAsync(IAsyncEnumerator<IMessage>? reply)
    {
        if (reply is null)
        {
            return;
        }

        try
        {
            await reply.DisposeAsync();
        }
        catch (Exception)
        {
            // The attempt already failed; its cleanup failing too changes nothing the caller can act on.
        }
    }

    /// <summary>
    ///     Helper method to convert IEnumerable to IAsyncEnumerable
    /// </summary>
    private static async IAsyncEnumerable<IMessage> ToAsyncEnumerableInternal(
        IEnumerable<IMessage> messages,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        foreach (var message in messages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield(); // Make this truly async
            yield return message;
        }
    }

    /// <summary>
    ///     A provider reply whose first message has already been read. It owns the provider's enumerator and
    ///     the read token: disposing its enumerator releases both, whether or not anything was read, and the
    ///     enumeration's own token cancels the provider's reads. It can be enumerated once.
    /// </summary>
    private sealed class StartedReply(
        IAsyncEnumerator<IMessage> reply,
        bool started,
        string? fallbackModelId,
        CancellationTokenSource reads
    ) : IAsyncEnumerable<IMessage>
    {
        private int _enumerated;

        public IAsyncEnumerator<IMessage> GetAsyncEnumerator(CancellationToken cancellationToken = default) =>
            Interlocked.Exchange(ref _enumerated, 1) == 0
                ? new Enumerator(reply, started, fallbackModelId, reads, cancellationToken)
                : throw new InvalidOperationException("A model fallback reply can be enumerated only once.");

        private sealed class Enumerator : IAsyncEnumerator<IMessage>
        {
            private readonly string? _fallbackModelId;
            private readonly CancellationTokenRegistration _linked;
            private readonly CancellationTokenSource _reads;
            private readonly IAsyncEnumerator<IMessage> _reply;
            private bool _disposed;
            private bool _finished;
            private bool _firstPending;

            public Enumerator(
                IAsyncEnumerator<IMessage> reply,
                bool started,
                string? fallbackModelId,
                CancellationTokenSource reads,
                CancellationToken enumerationToken
            )
            {
                _reply = reply;
                _firstPending = started;
                _finished = !started;
                _fallbackModelId = fallbackModelId;
                _reads = reads;
                _linked = enumerationToken.CanBeCanceled
                    ? enumerationToken.Register(static state => ((CancellationTokenSource)state!).Cancel(), reads)
                    : default;
            }

            public IMessage Current { get; private set; } = null!;

            public async ValueTask<bool> MoveNextAsync()
            {
                if (_firstPending)
                {
                    _reads.Token.ThrowIfCancellationRequested();
                    _firstPending = false;
                    Current = Credit(_reply.Current);
                    return true;
                }

                if (_finished || !await _reply.MoveNextAsync())
                {
                    _finished = true;
                    return false;
                }

                Current = Credit(_reply.Current);
                return true;
            }

            public async ValueTask DisposeAsync()
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                await _linked.DisposeAsync();
                try
                {
                    await _reply.DisposeAsync();
                }
                finally
                {
                    _reads.Dispose();
                }
            }

            private IMessage Credit(IMessage message) =>
                _fallbackModelId is null ? message : CreditUsage(message, _fallbackModelId);
        }
    }

    /// <param name="Agent">The agent to call.</param>
    /// <param name="Options">The request it gets.</param>
    /// <param name="ModelId">The model it runs, for the hop log.</param>
    /// <param name="FallbackModelId">Set on a chain fallback: the model its usage is credited to.</param>
    /// <param name="ReportsEarlierFailure">If it fails too, rethrow the earlier failure instead.</param>
    private sealed record Attempt(
        IAgent Agent,
        GenerateReplyOptions? Options,
        string? ModelId,
        string? FallbackModelId,
        bool ReportsEarlierFailure = false
    );
}
