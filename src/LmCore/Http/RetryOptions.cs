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
