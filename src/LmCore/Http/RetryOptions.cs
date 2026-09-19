using System.Net;

namespace AchieveAi.LmDotnetTools.LmCore.Http;

/// <summary>
///     Configuration options for HTTP retry behavior.
///     Use different instances for production vs testing.
/// </summary>
public record RetryOptions
{
    /// <summary>
    ///     Default production settings with reasonable delays.
    /// </summary>
    public static readonly RetryOptions Default = new();

    /// <summary>
    ///     Fast settings for unit tests with minimal delays.
    /// </summary>
    public static readonly RetryOptions FastForTests = new()
    {
        MaxRetries = 2,
        InitialDelayMs = 10,
        MaxDelayMs = 50,
        BackoffMultiplier = 2.0,
    };

    /// <summary>
    ///     Maximum number of retry attempts after the initial request fails.
    ///     Default: 2 (total of 3 attempts including the initial request)
    /// </summary>
    public int MaxRetries { get; init; } = 2;

    /// <summary>
    ///     Initial delay in milliseconds before the first retry.
    ///     Default: 1000ms (1 second)
    /// </summary>
    public int InitialDelayMs { get; init; } = 1000;

    /// <summary>
    ///     Maximum delay in milliseconds between retries.
    ///     Default: 30000ms (30 seconds)
    /// </summary>
    public int MaxDelayMs { get; init; } = 30000;

    /// <summary>
    ///     Multiplier for exponential backoff.
    ///     Each retry delay is: InitialDelayMs * (BackoffMultiplier ^ (attempt - 1))
    ///     Default: 2.0 (delays: 1s, 2s, 4s, 8s, ...)
    /// </summary>
    public double BackoffMultiplier { get; init; } = 2.0;

    /// <summary>
    ///     Status codes this configuration treats as retryable IN ADDITION to the global set
    ///     (429 and 5xx, see <see cref="HttpRetryHelper.IsRetryableStatusCode(HttpStatusCode)" />).
    ///     Empty by default, so the global classification is unchanged for every caller that does not
    ///     opt in. Exists for transports with a known transient status outside the global set — the
    ///     GitHub Copilot <c>/responses</c> backend intermittently answers
    ///     <c>404 {"error":{"message":"","code":"not_found"}}</c> for a request that succeeds on retry.
    /// </summary>
    public IReadOnlyCollection<HttpStatusCode> AdditionalRetryableStatusCodes { get; init; } = [];

    /// <summary>
    ///     Determines whether <paramref name="statusCode" /> is retryable under THIS configuration:
    ///     the global 429/5xx set widened by <see cref="AdditionalRetryableStatusCodes" />.
    /// </summary>
    public bool IsRetryableStatusCode(HttpStatusCode statusCode) =>
        HttpRetryHelper.IsRetryableStatusCode(statusCode) || AdditionalRetryableStatusCodes.Contains(statusCode);

    /// <summary>
    ///     Returns options whose <see cref="AdditionalRetryableStatusCodes" /> also contains
    ///     <paramref name="statusCodes" />. Every other value (including status codes the caller already
    ///     opted into) is preserved — this merges, it never overwrites. Returns the same instance when
    ///     all codes are already present.
    /// </summary>
    public RetryOptions WithAdditionalRetryableStatusCodes(params HttpStatusCode[] statusCodes)
    {
        ArgumentNullException.ThrowIfNull(statusCodes);
        var missing = statusCodes.Where(code => !AdditionalRetryableStatusCodes.Contains(code)).Distinct().ToArray();
        if (missing.Length == 0)
        {
            return this;
        }

        return this with
        {
            AdditionalRetryableStatusCodes = [.. AdditionalRetryableStatusCodes, .. missing],
        };
    }

    /// <summary>
    ///     Calculates the delay for a given retry attempt.
    /// </summary>
    /// <param name="attempt">The retry attempt number (1-based)</param>
    /// <returns>The delay before this retry attempt</returns>
    public TimeSpan CalculateDelay(int attempt)
    {
        var delayMs = InitialDelayMs * Math.Pow(BackoffMultiplier, attempt - 1);
        delayMs = Math.Min(delayMs, MaxDelayMs);
        return TimeSpan.FromMilliseconds(delayMs);
    }
}
