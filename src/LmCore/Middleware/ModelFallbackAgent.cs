using System.Runtime.ExceptionServices;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using Microsoft.Extensions.Logging;

namespace AchieveAi.LmDotnetTools.LmCore.Middleware;

/// <summary>
///     A provider agent with model fallback: the primary agent first, then each fallback, through
///     <see cref="ModelFallbackMiddleware" />. Disposing it disposes every agent it holds, so a host that
///     owns its provider agent releases the fallbacks with it.
/// </summary>
/// <remarks>
///     A streamed reply is returned exactly as the middleware made it. A deferred wrapper would never reach
///     the provider stream the middleware has already opened if the reply were disposed before its first read.
/// </remarks>
public sealed class ModelFallbackAgent : IStreamingAgent, IAsyncDisposable
{
    private readonly ModelFallbackMiddleware _middleware;
    private readonly IReadOnlyList<object> _owned;
    private readonly IStreamingAgent _primary;

    /// <param name="primary">The agent tried first, with the request as given.</param>
    /// <param name="fallbacks">The models to try after it, in order.</param>
    /// <param name="logger">Receives one warning per fallback hop.</param>
    /// <param name="shouldFallBack">Which failures fall back; defaults to <see cref="ModelFallbackMiddleware.IsProviderOutage" />.</param>
    public ModelFallbackAgent(
        IStreamingAgent primary,
        IReadOnlyList<FallbackTarget> fallbacks,
        ILogger? logger = null,
        Func<Exception, bool>? shouldFallBack = null
    )
    {
        ArgumentNullException.ThrowIfNull(primary);
        _primary = primary;
        _middleware = new ModelFallbackMiddleware(fallbacks, logger, shouldFallBack);
        _owned = [primary, .. fallbacks.Select(f => (object)f.Agent)];
    }

    /// <inheritdoc />
    public Task<IEnumerable<IMessage>> GenerateReplyAsync(
        IEnumerable<IMessage> messages,
        GenerateReplyOptions? options = null,
        CancellationToken cancellationToken = default
    ) => _middleware.InvokeAsync(new MiddlewareContext(messages, options), _primary, cancellationToken);

    /// <inheritdoc />
    public Task<IAsyncEnumerable<IMessage>> GenerateReplyStreamingAsync(
        IEnumerable<IMessage> messages,
        GenerateReplyOptions? options = null,
        CancellationToken cancellationToken = default
    ) => _middleware.InvokeStreamingAsync(new MiddlewareContext(messages, options), _primary, cancellationToken);

    /// <summary>
    ///     Disposes every distinct agent once. One that fails to dispose does not stop the rest; the first
    ///     failure is rethrown after all of them have been tried.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        ExceptionDispatchInfo? firstFailure = null;
        foreach (var agent in _owned)
        {
            if (!seen.Add(agent))
            {
                continue;
            }

            try
            {
                if (agent is IAsyncDisposable asyncDisposable)
                {
                    await asyncDisposable.DisposeAsync();
                }
                else if (agent is IDisposable disposable)
                {
                    disposable.Dispose();
                }
            }
            catch (Exception ex)
            {
                firstFailure ??= ExceptionDispatchInfo.Capture(ex);
            }
        }

        firstFailure?.Throw();
    }
}
