using System.Runtime.ExceptionServices;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using Microsoft.Extensions.Logging;

namespace AchieveAi.LmDotnetTools.LmCore.Middleware;

/// <summary>
///     A provider agent with model fallback: the primary agent first, then each fallback, through
///     <see cref="ModelFallbackMiddleware" />. Disposing it disposes every agent it holds, so a host that
///     owns its provider agent releases the fallbacks with it.
/// </summary>
public sealed class ModelFallbackAgent : MiddlewareWrappingStreamingAgent, IAsyncDisposable
{
    private readonly IReadOnlyList<object> _owned;

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
        : base(primary, new ModelFallbackMiddleware(fallbacks, logger, shouldFallBack))
    {
        _owned = [primary, .. fallbacks.Select(f => (object)f.Agent)];
    }

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
