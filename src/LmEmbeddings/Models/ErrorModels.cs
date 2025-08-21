using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.LmEmbeddings.Models;

/// <summary>
/// Represents a standardized error response from embedding services
/// </summary>
public record EmbeddingError
{
    /// <summary>
    /// The error code identifying the type of error
    /// </summary>
    [JsonPropertyName("code")]
    public required string Code { get; init; }

    /// <summary>
    /// Human-readable error message
    /// </summary>
    [JsonPropertyName("message")]
    public required string Message { get; init; }

    /// <summary>
    /// Detailed error description for debugging
    /// </summary>
    [JsonPropertyName("details")]
    public string? Details { get; init; }

    /// <summary>
    /// The source of the error (service, validation, api, etc.)
    /// </summary>
    [JsonPropertyName("source")]
    public required ErrorSource Source { get; init; }

    /// <summary>
    /// Timestamp when the error occurred
    /// </summary>
    [JsonPropertyName("timestamp")]
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// Additional context about the error
    /// </summary>
    [JsonPropertyName("context")]
    public ImmutableDictionary<string, object>? Context { get; init; }

    /// <summary>
    /// Nested validation errors (if applicable)
    /// </summary>
    [JsonPropertyName("validation_errors")]
    public ImmutableList<ValidationError>? ValidationErrors { get; init; }

    /// <summary>
    /// The request ID associated with this error
    /// </summary>
    [JsonPropertyName("request_id")]
    public string? RequestId { get; init; }

    /// <summary>
    /// Whether this error is retryable
    /// </summary>
    [JsonPropertyName("retryable")]
    public bool IsRetryable { get; init; }

    /// <summary>
    /// Suggested retry delay in milliseconds (if retryable)
    /// </summary>
    [JsonPropertyName("retry_after_ms")]
    public int? RetryAfterMs { get; init; }
}

/// <summary>
/// Represents a validation error for input parameters
/// </summary>
public record ValidationError
{
    /// <summary>
    /// The field or parameter that failed validation
    /// </summary>
    [JsonPropertyName("field")]
    public required string Field { get; init; }

    /// <summary>
    /// The validation error message
    /// </summary>
    [JsonPropertyName("message")]
    public required string Message { get; init; }

    /// <summary>
    /// The value that failed validation
    /// </summary>
    [JsonPropertyName("value")]
    public object? Value { get; init; }

    /// <summary>
    /// The validation rule that was violated
    /// </summary>
    [JsonPropertyName("rule")]
    public string? Rule { get; init; }

    /// <summary>
    /// Additional validation context
    /// </summary>
    [JsonPropertyName("context")]
    public ImmutableDictionary<string, object>? Context { get; init; }
}

/// <summary>
/// Represents an API-specific error from external services
/// </summary>
public record ApiError
{
    /// <summary>
    /// The API provider that returned the error
    /// </summary>
    [JsonPropertyName("provider")]
    public required string Provider { get; init; }

    /// <summary>
    /// The HTTP status code returned by the API
    /// </summary>
    [JsonPropertyName("status_code")]
    public int StatusCode { get; init; }

    /// <summary>
    /// The API's error code (if provided)
    /// </summary>
    [JsonPropertyName("api_error_code")]
    public string? ApiErrorCode { get; init; }

    /// <summary>
    /// The API's error message
    /// </summary>
    [JsonPropertyName("api_error_message")]
    public string? ApiErrorMessage { get; init; }

    /// <summary>
    /// The raw response body from the API
    /// </summary>
    [JsonPropertyName("raw_response")]
    public string? RawResponse { get; init; }

    /// <summary>
    /// Rate limit information (if applicable)
    /// </summary>
    [JsonPropertyName("rate_limit")]
    public RateLimitInfo? RateLimit { get; init; }

    /// <summary>
    /// The endpoint that was called
    /// </summary>
    [JsonPropertyName("endpoint")]
    public string? Endpoint { get; init; }

    /// <summary>
    /// The request method used
    /// </summary>
    [JsonPropertyName("method")]
    public string? Method { get; init; }

    /// <summary>
    /// When the API call was made
    /// </summary>
    [JsonPropertyName("timestamp")]
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Contains rate limiting information from API responses
/// </summary>
public record RateLimitInfo
{
    /// <summary>
    /// The maximum number of requests allowed
    /// </summary>
    [JsonPropertyName("limit")]
    public int Limit { get; init; }

    /// <summary>
    /// The number of requests remaining in the current window
    /// </summary>
    [JsonPropertyName("remaining")]
    public int Remaining { get; init; }

    /// <summary>
    /// When the rate limit window resets
    /// </summary>
    [JsonPropertyName("reset_time")]
    public DateTime ResetTime { get; init; }

    /// <summary>
    /// The rate limit window duration in seconds
    /// </summary>
    [JsonPropertyName("window_seconds")]
    public int WindowSeconds { get; init; }

    /// <summary>
    /// Whether the rate limit has been exceeded
    /// </summary>
    [JsonPropertyName("exceeded")]
    public bool IsExceeded { get; init; }
}

/// <summary>
/// Defines the source/category of an error
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ErrorSource
{
    /// <summary>
    /// Error from client-side validation
    /// </summary>
    Validation,

    /// <summary>
    /// Error from external API call
    /// </summary>
    Api,

    /// <summary>
    /// Error from service configuration
    /// </summary>
    Configuration,

    /// <summary>
    /// Error from network/connectivity issues
    /// </summary>
    Network,

    /// <summary>
    /// Error from authentication/authorization
    /// </summary>
    Authentication,

    /// <summary>
    /// Error from rate limiting
    /// </summary>
    RateLimit,

    /// <summary>
    /// Internal service error
    /// </summary>
    Internal,

    /// <summary>
    /// Error from resource constraints (memory, disk, etc.)
    /// </summary>
    Resource,

    /// <summary>
    /// Error from timeout
    /// </summary>
    Timeout,

    /// <summary>
    /// Error from data serialization/deserialization
    /// </summary>
    Serialization
}