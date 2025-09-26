namespace AchieveAi.LmDotnetTools.LmCore.Http;

/// <summary>
/// Configuration settings for HTTP operations across all providers
/// Provides centralized settings for timeouts, retry behavior, and other HTTP parameters
/// </summary>
public class HttpConfiguration
{
    /// <summary>
    /// Default maximum number of retry attempts for HTTP operations
    /// </summary>
    public const int DefaultMaxRetries = 3;

    /// <summary>
    /// Default timeout for HTTP requests in seconds
    /// </summary>
    public const int DefaultTimeoutSeconds = 30;

    /// <summary>
    /// Default base delay for exponential backoff in milliseconds
    /// </summary>
    public const int DefaultBaseDelayMilliseconds = 1000;

    /// <summary>
    /// Maximum number of retry attempts for HTTP operations
    /// </summary>
    public int MaxRetries { get; set; } = DefaultMaxRetries;

    /// <summary>
    /// Timeout for HTTP requests
    /// </summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(DefaultTimeoutSeconds);

    /// <summary>
    /// Base delay for exponential backoff retry logic
    /// </summary>
    public TimeSpan BaseDelay { get; set; } =
        TimeSpan.FromMilliseconds(DefaultBaseDelayMilliseconds);

    /// <summary>
    /// Whether to enable detailed HTTP logging
    /// </summary>
    public bool EnableDetailedLogging { get; set; } = false;

    /// <summary>
    /// Whether to retry on network/timeout errors
    /// </summary>
    public bool RetryOnNetworkErrors { get; set; } = true;

    /// <summary>
    /// Whether to retry on HTTP server errors (5xx status codes)
    /// </summary>
    public bool RetryOnServerErrors { get; set; } = true;

    /// <summary>
    /// User agent string to include in HTTP requests
    /// </summary>
    public string UserAgent { get; set; } = "LmDotnetTools/1.0";

    /// <summary>
    /// Creates a new HttpConfiguration with default values
    /// </summary>
    public HttpConfiguration() { }

    /// <summary>
    /// Creates a new HttpConfiguration with custom values
    /// </summary>
    /// <param name="maxRetries">Maximum number of retry attempts</param>
    /// <param name="timeout">Timeout for HTTP requests</param>
    /// <param name="baseDelay">Base delay for exponential backoff</param>
    public HttpConfiguration(int maxRetries, TimeSpan timeout, TimeSpan baseDelay)
    {
        MaxRetries = maxRetries;
        Timeout = timeout;
        BaseDelay = baseDelay;
    }

    /// <summary>
    /// Validates the configuration and throws an exception if invalid
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when configuration values are invalid</exception>
    public void Validate()
    {
        if (MaxRetries < 0)
        {
            throw new ArgumentException("MaxRetries cannot be negative", nameof(MaxRetries));
        }

        if (Timeout <= TimeSpan.Zero)
        {
            throw new ArgumentException("Timeout must be greater than zero", nameof(Timeout));
        }

        if (BaseDelay <= TimeSpan.Zero)
        {
            throw new ArgumentException("BaseDelay must be greater than zero", nameof(BaseDelay));
        }

        if (string.IsNullOrWhiteSpace(UserAgent))
        {
            throw new ArgumentException(
                "UserAgent cannot be null or whitespace",
                nameof(UserAgent)
            );
        }
    }

    /// <summary>
    /// Creates a copy of this configuration
    /// </summary>
    /// <returns>A new HttpConfiguration instance with the same values</returns>
    public HttpConfiguration Clone()
    {
        return new HttpConfiguration(MaxRetries, Timeout, BaseDelay)
        {
            EnableDetailedLogging = EnableDetailedLogging,
            RetryOnNetworkErrors = RetryOnNetworkErrors,
            RetryOnServerErrors = RetryOnServerErrors,
            UserAgent = UserAgent,
        };
    }
}
