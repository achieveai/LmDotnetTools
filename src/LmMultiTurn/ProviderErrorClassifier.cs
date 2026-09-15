using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

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
/// scope. Size is now only a tie-breaker for the one ambiguous shape — an aborted response — where the
/// endpoint gives no other signal. The abort must be evidenced (a typed <see cref="HttpIOException"/>, a
/// reset-family socket code, or an abort signature in the message): a transport-layer base type or an
/// availability status on its own says nothing about request size, and neither do lifecycle and programming
/// faults (<see cref="ObjectDisposedException"/>, <see cref="InvalidOperationException"/>,
/// <see cref="ArgumentException"/>, serialization errors, …), however large the history.
/// </para>
/// <para>
/// The provider error is often wrapped (retry helper, middleware, <see cref="AggregateException"/>), so every
/// exception in the chain is inspected and the strongest verdict wins.
/// </para>
/// </remarks>
internal static partial class ProviderErrorClassifier
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
    //   Copilot    — code "model_max_prompt_tokens_exceeded" ("prompt token count of N exceeds the limit of M")
    //   Anthropic  — "input length and `max_tokens` exceed context limit: N + M > L"
    // plus the generic phrasings gateways (OpenRouter, Copilot) forward.
    private static readonly string[] s_overflowSignatures =
    [
        "max_prompt_tokens_exceeded",
        "exceed context limit",
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

    /// <summary>
    /// The provider told us: an overflow status, or an overflow error body in the message. A known status other than
    /// 400 or 413 is never an overflow, whatever its body mentions (a tokens-per-minute 429, a 500). Nor is a body
    /// whose output reservation is at least its input: a smaller conversation cannot make that request fit.
    /// </summary>
    private static bool IsProviderOverflow(Exception candidate) =>
        candidate switch
        {
            // 413 is "request too large" on every provider here (Anthropic request_too_large, OpenAI/gateways
            // for an oversized payload) — reduce-scope advice applies whatever the body says.
            HttpRequestException { StatusCode: HttpStatusCode.RequestEntityTooLarge } => true,
            HttpRequestException { StatusCode: { } status } when status != HttpStatusCode.BadRequest => false,
            _ => ContainsAny(candidate.Message, s_overflowSignatures) && !IsOutputDominated(candidate.Message),
        };

    /// <summary>
    /// Whether the refusal names its token split and the output side is at least the input side: OpenRouter's
    /// "(N of text input, K of tool input, M in the output)" or Anthropic's "exceed context limit: N + M > L".
    /// A split with a count too large to parse is not output-dominated: this runs on the error path, so it must never
    /// throw and mask the provider's error.
    /// </summary>
    private static bool IsOutputDominated(string message)
    {
        if (OutputSplitPattern().Match(message) is { Success: true } split)
        {
            if (!TryTokens(split.Groups["output"], out var remaining))
            {
                return false;
            }

            // Subtract each input from the output rather than summing the inputs, so the total cannot overflow.
            foreach (Capture capture in split.Groups["input"].Captures)
            {
                if (!TryTokens(capture, out var tokens) || tokens > remaining)
                {
                    return false;
                }

                remaining -= tokens;
            }

            return true;
        }

        return MaxTokensSplitPattern().Match(message) is { Success: true } sum
            && TryTokens(sum.Groups["input"], out var input)
            && TryTokens(sum.Groups["output"], out var output)
            && output >= input;

        static bool TryTokens(Capture capture, out long tokens) =>
            long.TryParse(capture.Value, NumberStyles.None, CultureInfo.InvariantCulture, out tokens);
    }

    /// <summary>
    /// Evidence that the RESPONSE was aborted mid-flight: a typed abort where .NET types it, a reset-family
    /// socket code, or an abort signature where a provider or proxy re-throws it as plain text. An exception's
    /// base type is not evidence — a bare <see cref="IOException"/> may be a storage fault, a
    /// <see cref="SocketException"/> an unreachable host, and a status-only 502/503/504/408 an availability
    /// answer — so those shapes only match if their message carries a signature. Programming and lifecycle
    /// exceptions never match.
    /// </summary>
    private static bool IsTransportAbort(Exception candidate)
    {
        return candidate switch
        {
            HttpIOException => true,
            // The code, not the message: Windows and Linux render different text for the same reset.
            SocketException
            {
                SocketErrorCode: SocketError.ConnectionReset or SocketError.ConnectionAborted or SocketError.Shutdown,
            } => true,
            // ConnectionError is deliberately absent: the connection could not be established at all,
            // which is availability, not an aborted response.
            HttpRequestException { HttpRequestError: HttpRequestError.ResponseEnded } => true,
            _ => ContainsAny(candidate.Message, s_transportAbortSignatures),
        };
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

    [GeneratedRegex(
        @"\((?:(?<input>\d+) of [a-z]+ input, )+(?<output>\d+) in the output\)",
        RegexOptions.CultureInvariant
    )]
    private static partial Regex OutputSplitPattern();

    [GeneratedRegex(@"exceed context limit: (?<input>\d+) \+ (?<output>\d+) >", RegexOptions.CultureInvariant)]
    private static partial Regex MaxTokensSplitPattern();
}
