using System.Globalization;

namespace AchieveAi.LmDotnetTools.LmCore.Http;

/// <summary>
///     Reads how long a server asked the client to wait before retrying.
/// </summary>
/// <remarks>
///     Shared by two callers that honor the header the same way: the provider retry loop
///     (<see cref="HttpRetryHelper" />) and the lifecycle webhook sender
///     (<c>HttpLifecycleDeliverySender</c> in LmAgentInfra). A cap on the result is the caller's
///     decision, not this reader's — the two callers answer an over-long wait differently — so the
///     wait is returned at full length, however long, for the caller to compare.
/// </remarks>
public static class RetryAfterHeader
{
    /// <summary>
    ///     Non-standard millisecond form OpenAI sends beside <c>Retry-After</c>. Preferred when both
    ///     are present because it is the more precise of the two.
    /// </summary>
    public const string RetryAfterMsHeaderName = "retry-after-ms";

    private const string RetryAfterHeaderName = "Retry-After";

    /// <summary>
    ///     The wait the response asks for, or <see langword="null" /> when it asks for none or the
    ///     header cannot be read. A wait in the past, or a negative one, is <see cref="TimeSpan.Zero" />.
    ///     A wait too long for <see cref="TimeSpan" /> is <see cref="TimeSpan.MaxValue" />.
    /// </summary>
    /// <param name="response">The response whose headers are read.</param>
    /// <param name="timeProvider">Clock used to turn an absolute HTTP-date into a wait.</param>
    public static TimeSpan? Read(HttpResponseMessage response, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(timeProvider);

        return ReadMilliseconds(response) ?? ReadStandard(response, timeProvider);
    }

    /// <summary>The <c>retry-after-ms</c> form: a number of milliseconds, fractional allowed.</summary>
    private static TimeSpan? ReadMilliseconds(HttpResponseMessage response)
    {
        return
            response.Headers.TryGetValues(RetryAfterMsHeaderName, out var values)
            && double.TryParse(
                values.FirstOrDefault(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var milliseconds
            )
            && double.IsFinite(milliseconds)
            ? FromMilliseconds(milliseconds)
            : null;
    }

    /// <summary>
    ///     The standard <c>Retry-After</c> form: delay-seconds or an HTTP-date. The seconds are read from
    ///     the raw value, because the typed parser drops a negative or an over-<see cref="int" /> value
    ///     as unreadable — a negative wait must mean "now" and a huge one must reach the caller's cap.
    /// </summary>
    private static TimeSpan? ReadStandard(HttpResponseMessage response, TimeProvider timeProvider)
    {
        if (!response.Headers.TryGetValues(RetryAfterHeaderName, out var values))
        {
            return null;
        }

        if (
            long.TryParse(
                values.FirstOrDefault()?.Trim(),
                NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture,
                out var seconds
            )
        )
        {
            return FromMilliseconds(seconds * 1000d);
        }

        if (response.Headers.RetryAfter?.Date is { } date)
        {
            var wait = date - timeProvider.GetUtcNow();
            return wait < TimeSpan.Zero ? TimeSpan.Zero : wait;
        }

        return null;
    }

    private static TimeSpan FromMilliseconds(double milliseconds) =>
        milliseconds switch
        {
            <= 0 => TimeSpan.Zero,
            _ when milliseconds >= TimeSpan.MaxValue.TotalMilliseconds => TimeSpan.MaxValue,
            _ => TimeSpan.FromMilliseconds(milliseconds),
        };
}
