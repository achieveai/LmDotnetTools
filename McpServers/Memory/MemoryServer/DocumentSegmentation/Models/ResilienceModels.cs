using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace MemoryServer.DocumentSegmentation.Models;

/// <summary>
///     Configuration for circuit breaker behavior.
///     Implements AC-2.2 and AC-2.3 from ErrorHandling-TestAcceptanceCriteria.
/// </summary>
public record CircuitBreakerConfiguration
{
    /// <summary>
    ///     Number of consecutive failures before opening the circuit.
    ///     Default: 5 failures (as per AC-2.2)
    /// </summary>
    [JsonPropertyName("failure_threshold")]
    public int FailureThreshold { get; init; } = 5;

    /// <summary>
    ///     Time in milliseconds to wait before transitioning from Open to Half-Open state.
    ///     Default: 30 seconds (as per AC-2.3)
    /// </summary>
    [JsonPropertyName("timeout_ms")]
    public int TimeoutMs { get; init; } = 30000;

    /// <summary>
    ///     Maximum timeout cap in milliseconds (5 minutes as per AC-2.3).
    /// </summary>
    [JsonPropertyName("max_timeout_ms")]
    public int MaxTimeoutMs { get; init; } = 300000;

    /// <summary>
    ///     Exponential backoff factor for recovery timing.
    /// </summary>
    [JsonPropertyName("exponential_factor")]
    public double ExponentialFactor { get; init; } = 2.0;

    /// <summary>
    ///     Different failure thresholds for different error types.
    ///     Key: HTTP status code or error type, Value: threshold
    /// </summary>
    [JsonPropertyName("error_type_thresholds")]
    public ImmutableDictionary<string, int> ErrorTypeThresholds { get; init; } =
        ImmutableDictionary
            .Create<string, int>()
            .Add("401", 3) // Auth errors fail faster
            .Add("503", 5); // Service unavailable uses default
}

/// <summary>
///     Configuration for retry behavior with exponential backoff.
///     Implements AC-3.1, AC-3.2, and AC-3.3 from ErrorHandling-TestAcceptanceCriteria.
/// </summary>
public record RetryConfiguration
{
    /// <summary>
    ///     Maximum number of retry attempts.
    ///     Default: 3 (as per AC-3.1)
    /// </summary>
    [JsonPropertyName("max_retries")]
    public int MaxRetries { get; init; } = 3;

    /// <summary>
    ///     Base delay in milliseconds for the first retry.
    ///     Default: 1 second (as per AC-3.2)
    /// </summary>
    [JsonPropertyName("base_delay_ms")]
    public int BaseDelayMs { get; init; } = 1000;

    /// <summary>
    ///     Exponential factor for backoff calculation.
    ///     Default: 2.0 (as per AC-3.2)
    /// </summary>
    [JsonPropertyName("exponential_factor")]
    public double ExponentialFactor { get; init; } = 2.0;

    /// <summary>
    ///     Maximum delay cap in milliseconds.
    ///     Default: 30 seconds (as per AC-3.2)
    /// </summary>
    [JsonPropertyName("max_delay_ms")]
    public int MaxDelayMs { get; init; } = 30000;

    /// <summary>
    ///     Jitter percentage (±) to prevent thundering herd.
    ///     Default: 10% (as per AC-3.3)
    /// </summary>
    [JsonPropertyName("jitter_percent")]
    public double JitterPercent { get; init; } = 0.1;

    /// <summary>
    ///     Error types that should not be retried.
    /// </summary>
    [JsonPropertyName("non_retryable_errors")]
    public ImmutableList<string> NonRetryableErrors { get; init; } = ImmutableList.Create("401", "400", "403");

    /// <summary>
    ///     Different retry counts for different error types.
    /// </summary>
    [JsonPropertyName("error_type_retries")]
    public ImmutableDictionary<string, int> ErrorTypeRetries { get; init; } =
        ImmutableDictionary
            .Create<string, int>()
            .Add("429", 5) // Rate limiting gets more retries
            .Add("503", 3) // Service unavailable
            .Add("timeout", 3);
}

/// <summary>
///     Circuit breaker states as per AC-2.1.
/// </summary>
public enum CircuitBreakerStateEnum
{
    /// <summary>
    ///     All API calls pass through normally.
    ///     Failure count resets on successful call.
    ///     Transitions to Open on reaching failure threshold.
    /// </summary>
    Closed,

    /// <summary>
    ///     No API calls attempted.
    ///     Immediate fallback to rule-based segmentation.
    ///     Transitions to Half-Open after timeout.
    /// </summary>
    Open,

    /// <summary>
    ///     Single test API call allowed.
    ///     Success transitions to Closed.
    ///     Failure transitions back to Open.
    /// </summary>
    HalfOpen,
}

/// <summary>
///     Represents the current state of a circuit breaker.
/// </summary>
public record CircuitBreakerState
{
    /// <summary>
    ///     Current state of the circuit breaker.
    /// </summary>
    public CircuitBreakerStateEnum State { get; init; } = CircuitBreakerStateEnum.Closed;

    /// <summary>
    ///     Current failure count.
    /// </summary>
    public int FailureCount { get; init; }

    /// <summary>
    ///     Timestamp when the circuit was last opened.
    /// </summary>
    public DateTime? LastOpenedAt { get; init; }

    /// <summary>
    ///     Timestamp when the next retry should be allowed (for Open state).
    /// </summary>
    public DateTime? NextRetryAt { get; init; }

    /// <summary>
    ///     Number of times the circuit has been opened.
    /// </summary>
    public int TotalOpenings { get; init; }

    /// <summary>
    ///     Last error that caused the circuit to open.
    /// </summary>
    public string? LastError { get; init; }
}

/// <summary>
///     Error metrics for tracking different types of errors.
///     Implements AC-5.3 from ErrorHandling-TestAcceptanceCriteria.
/// </summary>
public record ErrorMetrics
{
    /// <summary>
    ///     Total number of errors by type.
    /// </summary>
    public ImmutableDictionary<string, int> ErrorCounts { get; init; } = ImmutableDictionary<string, int>.Empty;

    /// <summary>
    ///     Circuit breaker state duration tracking.
    /// </summary>
    public ImmutableDictionary<CircuitBreakerStateEnum, TimeSpan> StateDurations { get; init; } =
        ImmutableDictionary<CircuitBreakerStateEnum, TimeSpan>.Empty;

    /// <summary>
    ///     Fallback usage frequency.
    /// </summary>
    public int FallbackUsageCount { get; init; }

    /// <summary>
    ///     Recovery time measurements in milliseconds.
    /// </summary>
    public ImmutableList<double> RecoveryTimes { get; init; } = [];

    /// <summary>
    ///     API response time percentiles.
    /// </summary>
    public ResponseTimePercentiles? ResponseTimes { get; init; }

    /// <summary>
    ///     Timestamp when metrics were last updated.
    /// </summary>
    public DateTime LastUpdated { get; init; } = DateTime.UtcNow;
}

/// <summary>
///     Response time percentiles for performance monitoring.
/// </summary>
public record ResponseTimePercentiles
{
    /// <summary>
    ///     50th percentile (median) response time in milliseconds.
    /// </summary>
    public double P50 { get; init; }

    /// <summary>
    ///     95th percentile response time in milliseconds.
    ///     Target: &lt;15 seconds as per Success Metrics.
    /// </summary>
    public double P95 { get; init; }

    /// <summary>
    ///     99th percentile response time in milliseconds.
    ///     Target: &lt;30 seconds as per Success Metrics.
    /// </summary>
    public double P99 { get; init; }

    /// <summary>
    ///     Average response time in milliseconds.
    /// </summary>
    public double Average { get; init; }

    /// <summary>
    ///     Minimum response time in milliseconds.
    /// </summary>
    public double Min { get; init; }

    /// <summary>
    ///     Maximum response time in milliseconds.
    /// </summary>
    public double Max { get; init; }
}

/// <summary>
///     Configuration for graceful degradation behavior.
///     Implements AC-4.1, AC-4.2, AC-4.3, and AC-4.4 from ErrorHandling-TestAcceptanceCriteria.
/// </summary>
public record GracefulDegradationConfiguration
{
    /// <summary>
    ///     Maximum time in milliseconds for fallback to occur.
    ///     Default: 5 seconds (as per AC-4.1)
    /// </summary>
    [JsonPropertyName("fallback_timeout_ms")]
    public int FallbackTimeoutMs { get; init; } = 5000;

    /// <summary>
    ///     Quality score for rule-based confidence.
    ///     Default: 0.7 (as per AC-4.2)
    /// </summary>
    [JsonPropertyName("rule_based_quality_score")]
    public double RuleBasedQualityScore { get; init; } = 0.7;

    /// <summary>
    ///     Maximum processing time for rule-based fallback in milliseconds.
    ///     Default: 10 seconds (as per AC-4.3)
    /// </summary>
    [JsonPropertyName("rule_based_max_processing_ms")]
    public int RuleBasedMaxProcessingMs { get; init; } = 10000;

    /// <summary>
    ///     Maximum acceptable performance degradation percentage.
    ///     Default: 20% (as per AC-4.3)
    /// </summary>
    [JsonPropertyName("max_performance_degradation_percent")]
    public double MaxPerformanceDegradationPercent { get; init; } = 0.2;
}

/// <summary>
///     Result of an operation that may have degraded due to error handling.
///     Implements response structure requirements from AC-4.1 and AC-4.2.
/// </summary>
public record ResilienceOperationResult<T>
{
    /// <summary>
    ///     The actual result data.
    /// </summary>
    public T? Data { get; init; }

    /// <summary>
    ///     Whether the operation ran in degraded mode.
    /// </summary>
    [JsonPropertyName("degraded_mode")]
    public bool DegradedMode { get; init; }

    /// <summary>
    ///     Quality score of the result (0.0 to 1.0).
    /// </summary>
    [JsonPropertyName("quality_score")]
    public double QualityScore { get; init; } = 1.0;

    /// <summary>
    ///     Strategy used for segmentation.
    /// </summary>
    [JsonPropertyName("strategy_used")]
    public string? StrategyUsed { get; init; }

    /// <summary>
    ///     Explanation for degradation (if any).
    /// </summary>
    [JsonPropertyName("degradation_reason")]
    public string? DegradationReason { get; init; }

    /// <summary>
    ///     Processing time in milliseconds.
    /// </summary>
    [JsonPropertyName("processing_time_ms")]
    public double ProcessingTimeMs { get; init; }

    /// <summary>
    ///     Whether the operation was successful.
    /// </summary>
    [JsonPropertyName("success")]
    public bool Success { get; init; } = true;

    /// <summary>
    ///     Error message if operation failed.
    /// </summary>
    [JsonPropertyName("error_message")]
    public string? ErrorMessage { get; init; }

    /// <summary>
    ///     Correlation ID for tracking requests.
    /// </summary>
    [JsonPropertyName("correlation_id")]
    public string? CorrelationId { get; init; }
}

/// <summary>
///     Enum for different types of errors for classification.
/// </summary>
public enum ErrorType
{
    /// <summary>
    ///     Network timeout errors.
    /// </summary>
    NetworkTimeout,

    /// <summary>
    ///     Rate limiting (HTTP 429).
    /// </summary>
    RateLimit,

    /// <summary>
    ///     Authentication errors (HTTP 401).
    /// </summary>
    Authentication,

    /// <summary>
    ///     Service unavailable (HTTP 503).
    /// </summary>
    ServiceUnavailable,

    /// <summary>
    ///     Malformed response from LLM.
    /// </summary>
    MalformedResponse,

    /// <summary>
    ///     Connection failure.
    /// </summary>
    ConnectionFailure,

    /// <summary>
    ///     Unknown or generic error.
    /// </summary>
    Unknown,
}

/// <summary>
///     Context information for retry operations.
///     Implements AC-3.4 from ErrorHandling-TestAcceptanceCriteria.
/// </summary>
public record RetryContext
{
    /// <summary>
    ///     Current attempt number (1-based).
    /// </summary>
    public int AttemptNumber { get; init; }

    /// <summary>
    ///     Maximum number of attempts allowed.
    /// </summary>
    public int MaxAttempts { get; init; }

    /// <summary>
    ///     Correlation ID maintained across retries.
    /// </summary>
    public string CorrelationId { get; init; } = Guid.NewGuid().ToString();

    /// <summary>
    ///     Original request parameters (preserved across retries).
    /// </summary>
    public ImmutableDictionary<string, object> RequestParameters { get; init; } =
        ImmutableDictionary<string, object>.Empty;

    /// <summary>
    ///     Total time spent on all attempts so far.
    /// </summary>
    public TimeSpan TotalElapsed { get; init; }

    /// <summary>
    ///     Last error encountered.
    /// </summary>
    public Exception? LastError { get; init; }

    /// <summary>
    ///     Type of error for specialized handling.
    /// </summary>
    public ErrorType ErrorType { get; init; }
}
