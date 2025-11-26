using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Models;

namespace AchieveAi.LmDotnetTools.LmCore.Performance;

/// <summary>
/// Tracks comprehensive metrics for individual provider requests.
/// Supports OpenAI, Anthropic, and other provider-specific metrics.
/// </summary>
public record RequestMetrics
{
    /// <summary>Request start timestamp</summary>
    public DateTimeOffset StartTime { get; init; }

    /// <summary>Request end timestamp</summary>
    public DateTimeOffset EndTime { get; init; }

    /// <summary>Total request duration</summary>
    public TimeSpan Duration => EndTime - StartTime;

    /// <summary>Provider name (OpenAI, Anthropic, etc.)</summary>
    public string Provider { get; init; } = string.Empty;

    /// <summary>Model used for the request</summary>
    public string Model { get; init; } = string.Empty;

    /// <summary>Operation type (chat, completion, embedding, etc.)</summary>
    public string Operation { get; init; } = string.Empty;

    /// <summary>HTTP status code returned</summary>
    public int StatusCode { get; init; }

    /// <summary>Number of retry attempts made</summary>
    public int RetryAttempts { get; init; }

    /// <summary>Token usage information</summary>
    public Usage? Usage { get; init; }

    /// <summary>Request size in bytes</summary>
    public long RequestSizeBytes { get; init; }

    /// <summary>Response size in bytes</summary>
    public long ResponseSizeBytes { get; init; }

    /// <summary>Error message if request failed</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>Exception type if request failed</summary>
    public string? ExceptionType { get; init; }

    /// <summary>Whether the request was successful</summary>
    public bool IsSuccess => StatusCode >= 200 && StatusCode < 300 && string.IsNullOrEmpty(ErrorMessage);

    /// <summary>Additional provider-specific properties</summary>
    public Dictionary<string, object> AdditionalProperties { get; init; } = [];

    /// <summary>Creates a new RequestMetrics instance with start time set to now</summary>
    /// <param name="provider">Provider name</param>
    /// <param name="model">Model name</param>
    /// <param name="operation">Operation type</param>
    /// <returns>RequestMetrics with StartTime set</returns>
    public static RequestMetrics StartNew(string provider, string model, string operation = "")
    {
        return new RequestMetrics
        {
            StartTime = DateTimeOffset.UtcNow,
            Provider = provider,
            Model = model,
            Operation = operation,
        };
    }

    /// <summary>Creates a completed RequestMetrics from a started instance</summary>
    /// <param name="statusCode">HTTP status code</param>
    /// <param name="usage">Token usage information</param>
    /// <param name="requestSizeBytes">Request size in bytes</param>
    /// <param name="responseSizeBytes">Response size in bytes</param>
    /// <param name="retryAttempts">Number of retry attempts</param>
    /// <param name="errorMessage">Error message if failed</param>
    /// <param name="exceptionType">Exception type if failed</param>
    /// <param name="additionalProperties">Additional provider-specific properties</param>
    /// <returns>Completed RequestMetrics instance</returns>
    public RequestMetrics Complete(
        int statusCode,
        Usage? usage = null,
        long requestSizeBytes = 0,
        long responseSizeBytes = 0,
        int retryAttempts = 0,
        string? errorMessage = null,
        string? exceptionType = null,
        Dictionary<string, object>? additionalProperties = null
    )
    {
        return this with
        {
            EndTime = DateTimeOffset.UtcNow,
            StatusCode = statusCode,
            Usage = usage,
            RequestSizeBytes = requestSizeBytes,
            ResponseSizeBytes = responseSizeBytes,
            RetryAttempts = retryAttempts,
            ErrorMessage = errorMessage,
            ExceptionType = exceptionType,
            AdditionalProperties = additionalProperties ?? AdditionalProperties,
        };
    }
}
