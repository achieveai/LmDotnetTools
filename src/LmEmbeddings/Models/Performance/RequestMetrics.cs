using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.LmEmbeddings.Models;

/// <summary>
///     Comprehensive metrics for a single request
/// </summary>
public record RequestMetrics
{
    /// <summary>
    ///     Unique identifier for this request
    /// </summary>
    [JsonPropertyName("request_id")]
    public required string RequestId { get; init; }

    /// <summary>
    ///     The service that handled this request
    /// </summary>
    [JsonPropertyName("service")]
    public required string Service { get; init; }

    /// <summary>
    ///     The model used for this request
    /// </summary>
    [JsonPropertyName("model")]
    public required string Model { get; init; }

    /// <summary>
    ///     When the request started
    /// </summary>
    [JsonPropertyName("start_time")]
    public DateTime StartTime { get; init; } = DateTime.UtcNow;

    /// <summary>
    ///     When the request completed
    /// </summary>
    [JsonPropertyName("end_time")]
    public DateTime? EndTime { get; init; }

    /// <summary>
    ///     Total request duration in milliseconds
    /// </summary>
    [JsonPropertyName("duration_ms")]
    public double? DurationMs { get; init; }

    /// <summary>
    ///     Time spent waiting for the API response
    /// </summary>
    [JsonPropertyName("api_response_time_ms")]
    public double? ApiResponseTimeMs { get; init; }

    /// <summary>
    ///     Number of input texts processed
    /// </summary>
    [JsonPropertyName("input_count")]
    public int InputCount { get; init; }

    /// <summary>
    ///     Total number of tokens processed
    /// </summary>
    [JsonPropertyName("total_tokens")]
    public int? TotalTokens { get; init; }

    /// <summary>
    ///     Number of retry attempts made
    /// </summary>
    [JsonPropertyName("retry_count")]
    public int RetryCount { get; init; }

    /// <summary>
    ///     Whether the request succeeded
    /// </summary>
    [JsonPropertyName("success")]
    public bool Success { get; init; }

    /// <summary>
    ///     Error information if the request failed
    /// </summary>
    [JsonPropertyName("error")]
    public string? Error { get; init; }

    /// <summary>
    ///     HTTP status code from the API
    /// </summary>
    [JsonPropertyName("status_code")]
    public int? StatusCode { get; init; }

    /// <summary>
    ///     Size of the request payload in bytes
    /// </summary>
    [JsonPropertyName("request_size_bytes")]
    public long? RequestSizeBytes { get; init; }

    /// <summary>
    ///     Size of the response payload in bytes
    /// </summary>
    [JsonPropertyName("response_size_bytes")]
    public long? ResponseSizeBytes { get; init; }

    /// <summary>
    ///     Additional timing breakdowns
    /// </summary>
    [JsonPropertyName("timing_breakdown")]
    public TimingBreakdown? TimingBreakdown { get; init; }

    /// <summary>
    ///     Cost information for this request
    /// </summary>
    [JsonPropertyName("cost")]
    public CostMetrics? Cost { get; init; }

    /// <summary>
    ///     Additional metadata for this request
    /// </summary>
    [JsonPropertyName("metadata")]
    public ImmutableDictionary<string, object>? Metadata { get; init; }
}

/// <summary>
///     Detailed timing breakdown for request phases
/// </summary>
public record TimingBreakdown
{
    /// <summary>
    ///     Time spent on input validation and preprocessing
    /// </summary>
    [JsonPropertyName("validation_ms")]
    public double? ValidationMs { get; init; }

    /// <summary>
    ///     Time spent preparing the HTTP request
    /// </summary>
    [JsonPropertyName("request_preparation_ms")]
    public double? RequestPreparationMs { get; init; }

    /// <summary>
    ///     Time spent waiting for network connection
    /// </summary>
    [JsonPropertyName("connection_ms")]
    public double? ConnectionMs { get; init; }

    /// <summary>
    ///     Time spent sending the request payload
    /// </summary>
    [JsonPropertyName("request_send_ms")]
    public double? RequestSendMs { get; init; }

    /// <summary>
    ///     Time spent waiting for server processing
    /// </summary>
    [JsonPropertyName("server_processing_ms")]
    public double? ServerProcessingMs { get; init; }

    /// <summary>
    ///     Time spent downloading the response
    /// </summary>
    [JsonPropertyName("response_download_ms")]
    public double? ResponseDownloadMs { get; init; }

    /// <summary>
    ///     Time spent parsing and processing the response
    /// </summary>
    [JsonPropertyName("response_processing_ms")]
    public double? ResponseProcessingMs { get; init; }

    /// <summary>
    ///     Total time spent on retries
    /// </summary>
    [JsonPropertyName("retry_delays_ms")]
    public double? RetryDelaysMs { get; init; }
}

/// <summary>
///     Cost metrics for requests
/// </summary>
public record CostMetrics
{
    /// <summary>
    ///     Estimated cost for this request
    /// </summary>
    [JsonPropertyName("amount")]
    public decimal? Amount { get; init; }

    /// <summary>
    ///     Currency for the cost
    /// </summary>
    [JsonPropertyName("currency")]
    public string Currency { get; init; } = "USD";

    /// <summary>
    ///     Cost per token (if applicable)
    /// </summary>
    [JsonPropertyName("cost_per_token")]
    public decimal? CostPerToken { get; init; }

    /// <summary>
    ///     Billing tier or plan used
    /// </summary>
    [JsonPropertyName("billing_tier")]
    public string? BillingTier { get; init; }

    /// <summary>
    ///     Cost breakdown by operation type
    /// </summary>
    [JsonPropertyName("cost_breakdown")]
    public ImmutableDictionary<string, decimal>? CostBreakdown { get; init; }
}
