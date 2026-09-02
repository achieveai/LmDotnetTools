using System.Net;
using System.Net.Sockets;

namespace AchieveAi.LmDotnetTools.LmMultiTurn;

/// <summary>
/// How confidently a failed provider call can be attributed to the conversation outgrowing the model's
/// context window. Drives whether the run error the caller sees carries "reduce scope / bigger window" advice.
/// </summary>
internal enum ContextOverflowVerdict
{
    /// <summary>The failure is not an overflow (or nothing about it says it is). No advice is added.</summary>
    NotOverflow,

    /// <summary>
    /// A transport-level abort on a conversation already past the large-history bar — the shape a huge request
    /// usually fails in, because the endpoint cuts the stream instead of returning a clean 400.
    /// </summary>
    LikelyOverflow,

    /// <summary>The provider said so: a recognised overflow error body or status.</summary>
    Overflow,
}

/// <summary>
/// Classifies a per-run provider failure as a context-window overflow (or not) from the exception itself —
/// its type, HTTP status and message — never from the size of the history alone.
/// </summary>
/// <remarks>
/// <para>
/// History size used to be the only gate (#693): every failure on a 100k+ token conversation was labelled an
/// overflow, so 26 disposed-client failures and a deferred-tool programming error told operators to reduce
/// scope. Size is now only a tie-breaker for the one ambiguous shape — a transport abort — where the endpoint
/// gives no other signal. Lifecycle and programming faults (<see cref="ObjectDisposedException"/>,
/// <see cref="InvalidOperationException"/>, <see cref="ArgumentException"/>, serialization errors, …) are never
/// overflow, however large the history.
/// </para>
/// <para>
/// The provider error is often wrapped (retry helper, middleware, <see cref="AggregateException"/>), so every
/// exception in the chain is inspected and the strongest verdict wins.
/// </para>
/// </remarks>
internal static class ProviderErrorClassifier
{
    /// <summary>
    /// The estimated token count (chars / 4) at which a conversation counts as large enough that a transport
    /// abort is more plausibly an overflow than a network blip.
    /// </summary>
    internal const long LargeConversationTokenEstimate = 100_000;

    // Bodies the providers in this repo return on a clean overflow 400, matched case-insensitively:
    //   Anthropic  — "prompt is too long: 213462 tokens > 200000 maximum"
    //   OpenAI     — code "context_length_exceeded" / "This model's maximum context length is N tokens"
    //   Responses  — "Your input exceeds the context window of this model"
    // plus the generic phrasings gateways (OpenRouter, Copilot) forward.
    private static readonly string[] s_overflowSignatures =
    [
        "prompt is too long",
        "prompt too long",
        "input is too long",
        "context_length_exceeded",
        "context_length",
        "context length",
        "maximum context",
        "context window",
        "too many tokens",
        "exceeds the maximum number of tokens",
        "exceeded the maximum number of tokens",
        "request_too_large",
    ];

    // The transport abort a huge request usually dies with instead of a clean 400.
    private static readonly string[] s_transportAbortSignatures =
    [
        "response ended prematurely",
        "unexpected end of stream",
        "connection was forcibly closed",
        "connection reset",
        "connection was closed",
    ];

    /// <summary>
    /// Classifies <paramref name="exception"/> given the conversation's estimated size.
    /// </summary>
    /// <param name="exception">The failure that ended the run.</param>
    /// <param name="estimatedTokens">Best-effort size of the conversation (chars / 4); 0 when unknown.</param>
    public static ContextOverflowVerdict ClassifyContextOverflow(Exception exception, long estimatedTokens)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var transportAbort = false;
        foreach (var candidate in Unwrap(exception))
        {
            if (IsProviderOverflow(candidate))
            {
                return ContextOverflowVerdict.Overflow;
            }

            transportAbort |= IsTransportAbort(candidate);
        }

        return transportAbort && estimatedTokens >= LargeConversationTokenEstimate
            ? ContextOverflowVerdict.LikelyOverflow
            : ContextOverflowVerdict.NotOverflow;
    }

    /// <summary>The provider told us: an overflow status, or an overflow error body in the message.</summary>
    private static bool IsProviderOverflow(Exception candidate)
    {
        // 413 is "request too large" on every provider here (Anthropic request_too_large, OpenAI/gateways
        // for an oversized payload) — reduce-scope advice applies whatever the body says.
        if (candidate is HttpRequestException { StatusCode: HttpStatusCode.RequestEntityTooLarge })
        {
            return true;
        }

        return ContainsAny(candidate.Message, s_overflowSignatures);
    }

    /// <summary>
    /// A response that ended at the transport layer — typed where .NET types it, message-matched where a
    /// provider or proxy re-throws it as plain text. Programming and lifecycle exceptions never match.
    /// </summary>
    private static bool IsTransportAbort(Exception candidate)
    {
        switch (candidate)
        {
            case HttpIOException:
            case SocketException:
            case IOException:
                return true;
            case HttpRequestException { HttpRequestError: HttpRequestError.ResponseEnded }:
            case HttpRequestException { HttpRequestError: HttpRequestError.ConnectionError }:
                return true;
            // A status-bearing HttpRequestException is a real answer from the server: only the gateway
            // family (the proxy gave up on a huge request) keeps the transport shape; 4xx is not overflow.
            case HttpRequestException
            {
                StatusCode: HttpStatusCode.BadGateway
                    or HttpStatusCode.ServiceUnavailable
                    or HttpStatusCode.GatewayTimeout
                    or HttpStatusCode.RequestTimeout
            }:
                return true;
            case HttpRequestException { StatusCode: not null }:
                return false;
            default:
                return ContainsAny(candidate.Message, s_transportAbortSignatures);
        }
    }

    private static bool ContainsAny(string? message, string[] signatures)
    {
        if (string.IsNullOrEmpty(message))
        {
            return false;
        }

        foreach (var signature in signatures)
        {
            if (message.Contains(signature, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Every exception in the chain, flattening aggregates, in outer-to-inner order.</summary>
    private static IEnumerable<Exception> Unwrap(Exception root)
    {
        var pending = new Stack<Exception>();
        pending.Push(root);
        var seen = new HashSet<Exception>(ReferenceEqualityComparer.Instance);

        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (!seen.Add(current))
            {
                continue;
            }

            yield return current;

            if (current is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions)
                {
                    pending.Push(inner);
                }
            }
            else if (current.InnerException is { } inner)
            {
                pending.Push(inner);
            }
        }
    }
}
