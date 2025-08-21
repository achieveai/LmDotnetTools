using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.LmEmbeddings.Models;

/// <summary>
/// Represents a structured result from embedding operations that includes performance metrics and error handling
/// </summary>
/// <typeparam name="T">The type of the successful result data</typeparam>
public record EmbeddingServiceResult<T>
{
    /// <summary>
    /// Whether the operation was successful
    /// </summary>
    [JsonPropertyName("success")]
    public bool Success { get; init; }

    /// <summary>
    /// The successful result data (if Success is true)
    /// </summary>
    [JsonPropertyName("data")]
    public T? Data { get; init; }

    /// <summary>
    /// Error information (if Success is false)
    /// </summary>
    [JsonPropertyName("error")]
    public EmbeddingError? Error { get; init; }

    /// <summary>
    /// Performance metrics for this operation
    /// </summary>
    [JsonPropertyName("metrics")]
    public RequestMetrics? Metrics { get; init; }

    /// <summary>
    /// Additional metadata about the operation
    /// </summary>
    [JsonPropertyName("metadata")]
    public ImmutableDictionary<string, object>? Metadata { get; init; }

    /// <summary>
    /// Creates a successful result with performance metrics
    /// </summary>
    /// <param name="data">The successful result data</param>
    /// <param name="metrics">Performance metrics for the operation</param>
    /// <param name="metadata">Additional metadata</param>
    /// <returns>A successful result</returns>
    public static EmbeddingServiceResult<T> CreateSuccess(T data, RequestMetrics? metrics = null, ImmutableDictionary<string, object>? metadata = null)
    {
        return new EmbeddingServiceResult<T>
        {
            Success = true,
            Data = data,
            Metrics = metrics,
            Metadata = metadata
        };
    }

    /// <summary>
    /// Creates a failed result with structured error information
    /// </summary>
    /// <param name="error">The error that occurred</param>
    /// <param name="metrics">Performance metrics (if available)</param>
    /// <param name="metadata">Additional metadata</param>
    /// <returns>A failed result</returns>
    public static EmbeddingServiceResult<T> CreateFailure(EmbeddingError error, RequestMetrics? metrics = null, ImmutableDictionary<string, object>? metadata = null)
    {
        return new EmbeddingServiceResult<T>
        {
            Success = false,
            Error = error,
            Metrics = metrics,
            Metadata = metadata
        };
    }

    /// <summary>
    /// Creates a failed result from an exception
    /// </summary>
    /// <param name="exception">The exception that occurred</param>
    /// <param name="requestId">The request ID for tracking</param>
    /// <param name="metrics">Performance metrics (if available)</param>
    /// <returns>A failed result</returns>
    public static EmbeddingServiceResult<T> FromException(Exception exception, string requestId, RequestMetrics? metrics = null)
    {
        var errorSource = ClassifyException(exception);
        var isRetryable = IsRetryableException(exception);

        var error = new EmbeddingError
        {
            Code = exception.GetType().Name,
            Message = exception.Message,
            Details = exception.ToString(),
            Source = errorSource,
            RequestId = requestId,
            IsRetryable = isRetryable,
            RetryAfterMs = isRetryable ? 1000 : null
        };

        return CreateFailure(error, metrics);
    }

    private static ErrorSource ClassifyException(Exception exception)
    {
        return exception switch
        {
            ArgumentException or ArgumentNullException => ErrorSource.Validation,
            HttpRequestException => ErrorSource.Api,
            TimeoutException => ErrorSource.Timeout,
            UnauthorizedAccessException => ErrorSource.Authentication,
            NotSupportedException => ErrorSource.Configuration,
            _ => ErrorSource.Internal
        };
    }

    private static bool IsRetryableException(Exception exception)
    {
        return exception switch
        {
            HttpRequestException http when http.Message.Contains("5") => true,
            TimeoutException => true,
            TaskCanceledException => true,
            _ => false
        };
    }
}

/// <summary>
/// Helper class for creating common embedding service results
/// </summary>
public static class EmbeddingResults
{
    /// <summary>
    /// Creates a validation error result
    /// </summary>
    public static EmbeddingServiceResult<T> ValidationError<T>(string field, string message, object? value = null, string? requestId = null)
    {
        var validationError = new ValidationError
        {
            Field = field,
            Message = message,
            Value = value,
            Rule = "Required"
        };

        var error = new EmbeddingError
        {
            Code = "VALIDATION_ERROR",
            Message = $"Validation failed for field '{field}': {message}",
            Source = ErrorSource.Validation,
            RequestId = requestId,
            IsRetryable = false,
            ValidationErrors = ImmutableList.Create(validationError)
        };

        return EmbeddingServiceResult<T>.CreateFailure(error);
    }

    /// <summary>
    /// Creates an API error result
    /// </summary>
    public static EmbeddingServiceResult<T> ApiError<T>(string provider, int statusCode, string? apiErrorCode = null, string? requestId = null)
    {
        var apiError = new ApiError
        {
            Provider = provider,
            StatusCode = statusCode,
            ApiErrorCode = apiErrorCode,
            ApiErrorMessage = $"HTTP {statusCode} error from {provider}"
        };

        var error = new EmbeddingError
        {
            Code = "API_ERROR",
            Message = $"API call to {provider} failed with status {statusCode}",
            Source = ErrorSource.Api,
            RequestId = requestId,
            IsRetryable = statusCode >= 500 || statusCode == 429,
            RetryAfterMs = statusCode >= 500 ? 1000 : null
        };

        return EmbeddingServiceResult<T>.CreateFailure(error);
    }

    /// <summary>
    /// Creates a rate limit error result
    /// </summary>
    public static EmbeddingServiceResult<T> RateLimitError<T>(RateLimitInfo rateLimitInfo, string? requestId = null)
    {
        var error = new EmbeddingError
        {
            Code = "RATE_LIMIT_EXCEEDED",
            Message = "Rate limit exceeded",
            Source = ErrorSource.RateLimit,
            RequestId = requestId,
            IsRetryable = true,
            RetryAfterMs = (int)(rateLimitInfo.ResetTime - DateTime.UtcNow).TotalMilliseconds
        };

        return EmbeddingServiceResult<T>.CreateFailure(error);
    }
}