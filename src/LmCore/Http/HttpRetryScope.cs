using System.Net;
using System.Runtime.CompilerServices;

namespace AchieveAi.LmDotnetTools.LmCore.Http;

/// <summary>
///     The ambient listener for provider HTTP retries on the current async flow, or none.
/// </summary>
/// <remarks>
///     <para>
///         Exists so an agent loop can show a user that its provider is being retried without every
///         provider client, agent and middleware growing a callback parameter. The retry happens deep
///         inside a client (<see cref="HttpRetryHelper" />), several layers below the loop that owns the
///         conversation, and none of those layers has a reason to know about the listener.
///     </para>
///     <para>
///         An <see cref="AsyncLocal{T}" />, like <c>AgentActorScope</c>: the loop opens a scope around one
///         provider call and the read of its stream, and the listener is captured into every
///         continuation under it — including the lazily-started request inside the stream. Per flow, not
///         per thread, so concurrent conversations on shared pool threads never see each other's
///         listener. No scope means no listener, which is the behavior every caller had before.
///     </para>
/// </remarks>
public static class HttpRetryScope
{
    private sealed class Scope(Action<HttpRetryNotice>? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Ambient.Value = previous;
        }
    }

    private static readonly AsyncLocal<Action<HttpRetryNotice>?> Ambient = new();

    /// <summary>
    ///     Sends <paramref name="listener" /> every retry on the current async flow until the returned
    ///     scope is disposed. Dispose restores the previous listener, so scopes nest.
    /// </summary>
    public static IDisposable Begin(Action<HttpRetryNotice> listener)
    {
        ArgumentNullException.ThrowIfNull(listener);

        var previous = Ambient.Value;
        Ambient.Value = listener;
        return new Scope(previous);
    }

    /// <summary>
    ///     Reads <paramref name="source" /> with <paramref name="listener" /> in scope while each item is
    ///     being FETCHED, and out of scope while the consumer handles it.
    /// </summary>
    /// <remarks>
    ///     Provider streams are lazy: the request, and so every retry, runs inside the first
    ///     <c>MoveNextAsync</c>, not inside the call that returned the stream. A scope opened only around
    ///     that call would see nothing. A scope held open around the whole <c>await foreach</c> would
    ///     see too much: the consumer's own work — a tool that calls a web API through the same retry
    ///     helper — would be reported as a provider retry. Scoping each fetch reports exactly the
    ///     source's retries.
    /// </remarks>
    public static async IAsyncEnumerable<T> Observe<T>(
        IAsyncEnumerable<T> source,
        Action<HttpRetryNotice> listener,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(listener);

        await using var enumerator = source.GetAsyncEnumerator(cancellationToken);
        while (true)
        {
            bool moved;
            using (Begin(listener))
            {
                moved = await enumerator.MoveNextAsync();
            }

            if (!moved)
            {
                yield break;
            }

            yield return enumerator.Current;
        }
    }

    /// <summary>
    ///     Tells the current listener, if any, about a retry. A listener that throws is ignored: showing a
    ///     retry must never be the reason the retry does not happen.
    /// </summary>
    public static void Notify(HttpRetryNotice notice)
    {
        ArgumentNullException.ThrowIfNull(notice);

        try
        {
            Ambient.Value?.Invoke(notice);
        }
        catch (Exception)
        {
            // Deliberately swallowed: see the summary. The listener owns its own diagnostics.
        }
    }

    /// <summary>
    ///     Reports retry number <paramref name="attempt" /> under <paramref name="options" />. The one place
    ///     a retry loop builds its notice, so the attempt budget is always
    ///     <see cref="RetryOptions.MaxAttempts" />.
    /// </summary>
    public static void NotifyRetry(
        RetryOptions options,
        int attempt,
        HttpStatusCode? statusCode,
        TimeSpan delay,
        bool fromRetryAfter
    )
    {
        ArgumentNullException.ThrowIfNull(options);
        Notify(new HttpRetryNotice(attempt, options.MaxAttempts, statusCode, delay, fromRetryAfter));
    }
}
