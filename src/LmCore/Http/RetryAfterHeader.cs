using System.Globalization;

namespace AchieveAi.LmDotnetTools.LmCore.Http;

/// <summary>
///     Reads how long a server asked the client to wait before retrying.
/// </summary>
/// <remarks>
///     One reader for every HTTP caller in the solution: the provider retry loop
///     (<see cref="HttpRetryHelper" />) and the lifecycle webhook sender both honor the same header the
///     same way. A cap on the result is the caller's decision, not this reader's — the two callers
///     answer an over-long wait differently.
/// </remarks>
public static class RetryAfterHeader
{
    /// <summary>
    ///     Non-standard millisecond form OpenAI sends beside <c>Retry-After</c>. Preferred when both
    ///     are present because it is the more precise of the two.
    /// </summary>
    public const string RetryAfterMsHeaderName = "retry-after-ms";

    /// <summary>
    ///     The wait the response asks for, or <see langword="null" /> when it asks for none or the
    ///     header cannot be read. A wait in the past, or a negative one, is <see cref="TimeSpan.Zero" />.
    /// </summary>
    /// <param name="response">The response whose headers are read.</param>
    /// <param name="timeProvider">Clock used to turn an absolute HTTP-date into a wait.</param>
    public static TimeSpan? Read(HttpResponseMessage response, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(timeProvider);

        if (
            response.Headers.TryGetValues(RetryAfterMsHeaderName, out var msValues)
            && double.TryParse(
                msValues.FirstOrDefault(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var milliseconds
            )
            && double.IsFinite(milliseconds)
        )
        {
            return milliseconds <= 0 ? TimeSpan.Zero : TimeSpan.FromMilliseconds(Math.Min(milliseconds, int.MaxValue));
        }

        if (response.Headers.RetryAfter is not { } retryAfter)
        {
            return null;
        }

        if (retryAfter.Delta is { } delta)
        {
            return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
        }

        if (retryAfter.Date is { } date)
        {
            var wait = date - timeProvider.GetUtcNow();
            return wait < TimeSpan.Zero ? TimeSpan.Zero : wait;
        }

        return null;
    }
}
