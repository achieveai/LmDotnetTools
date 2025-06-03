using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.LmEmbeddings.Models;

/// <summary>
/// Comprehensive metrics for a single request
/// </summary>
public record RequestMetrics
{
    /// <summary>
    /// Unique identifier for this request
    /// </summary>
    [JsonPropertyName("request_id")]
    public required string RequestId { get; init; }

    /// <summary>
    /// The service that handled this request
    /// </summary>
    [JsonPropertyName("service")]
    public required string Service { get; init; }

    /// <summary>
    /// The model used for this request
    /// </summary>
    [JsonPropertyName("model")]
    public required string Model { get; init; }

    /// <summary>
    /// When the request started
    /// </summary>
    [JsonPropertyName("start_time")]
    public DateTime StartTime { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// When the request completed
    /// </summary>
    [JsonPropertyName("end_time")]
    public DateTime? EndTime { get; init; }

    /// <summary>
    /// Total request duration in milliseconds
    /// </summary>
    [JsonPropertyName("duration_ms")]
    public double? DurationMs { get; init; }

    /// <summary>
    /// Time spent waiting for the API response
    /// </summary>
    [JsonPropertyName("api_response_time_ms")]
    public double? ApiResponseTimeMs { get; init; }

    /// <summary>
    /// Number of input texts processed
    /// </summary>
    [JsonPropertyName("input_count")]
    public int InputCount { get; init; }

    /// <summary>
    /// Total number of tokens processed
    /// </summary>
    [JsonPropertyName("total_tokens")]
    public int? TotalTokens { get; init; }

    /// <summary>
    /// Number of retry attempts made
    /// </summary>
    [JsonPropertyName("retry_count")]
    public int RetryCount { get; init; }

    /// <summary>
    /// Whether the request succeeded
    /// </summary>
    [JsonPropertyName("success")]
    public bool Success { get; init; }

    /// <summary>
    /// Error information if the request failed
    /// </summary>
    [JsonPropertyName("error")]
    public string? Error { get; init; }

    /// <summary>
    /// HTTP status code from the API
    /// </summary>
    [JsonPropertyName("status_code")]
    public int? StatusCode { get; init; }

    /// <summary>
    /// Size of the request payload in bytes
    /// </summary>
    [JsonPropertyName("request_size_bytes")]
    public long? RequestSizeBytes { get; init; }

    /// <summary>
    /// Size of the response payload in bytes
    /// </summary>
    [JsonPropertyName("response_size_bytes")]
    public long? ResponseSizeBytes { get; init; }

    /// <summary>
    /// Additional timing breakdowns
    /// </summary>
    [JsonPropertyName("timing_breakdown")]
    public TimingBreakdown? TimingBreakdown { get; init; }

    /// <summary>
    /// Cost information for this request
    /// </summary>
    [JsonPropertyName("cost")]
    public CostMetrics? Cost { get; init; }

    /// <summary>
    /// Additional metadata for this request
    /// </summary>
    [JsonPropertyName("metadata")]
    public ImmutableDictionary<string, object>? Metadata { get; init; }
}

/// <summary>
/// Detailed timing breakdown for request phases
/// </summary>
public record TimingBreakdown
{
    /// <summary>
    /// Time spent on input validation and preprocessing
    /// </summary>
    [JsonPropertyName("validation_ms")]
    public double? ValidationMs { get; init; }

    /// <summary>
    /// Time spent preparing the HTTP request
    /// </summary>
    [JsonPropertyName("request_preparation_ms")]
    public double? RequestPreparationMs { get; init; }

    /// <summary>
    /// Time spent waiting for network connection
    /// </summary>
    [JsonPropertyName("connection_ms")]
    public double? ConnectionMs { get; init; }

    /// <summary>
    /// Time spent sending the request payload
    /// </summary>
    [JsonPropertyName("request_send_ms")]
    public double? RequestSendMs { get; init; }

    /// <summary>
    /// Time spent waiting for server processing
    /// </summary>
    [JsonPropertyName("server_processing_ms")]
    public double? ServerProcessingMs { get; init; }

    /// <summary>
    /// Time spent downloading the response
    /// </summary>
    [JsonPropertyName("response_download_ms")]
    public double? ResponseDownloadMs { get; init; }

    /// <summary>
    /// Time spent parsing and processing the response
    /// </summary>
    [JsonPropertyName("response_processing_ms")]
    public double? ResponseProcessingMs { get; init; }

    /// <summary>
    /// Total time spent on retries
    /// </summary>
    [JsonPropertyName("retry_delays_ms")]
    public double? RetryDelaysMs { get; init; }
}

/// <summary>
/// Cost metrics for requests
/// </summary>
public record CostMetrics
{
    /// <summary>
    /// Estimated cost for this request
    /// </summary>
    [JsonPropertyName("amount")]
    public decimal? Amount { get; init; }

    /// <summary>
    /// Currency for the cost
    /// </summary>
    [JsonPropertyName("currency")]
    public string Currency { get; init; } = "USD";

    /// <summary>
    /// Cost per token (if applicable)
    /// </summary>
    [JsonPropertyName("cost_per_token")]
    public decimal? CostPerToken { get; init; }

    /// <summary>
    /// Billing tier or plan used
    /// </summary>
    [JsonPropertyName("billing_tier")]
    public string? BillingTier { get; init; }
}

/// <summary>
/// Performance profile for a service or model
/// </summary>
public record PerformanceProfile
{
    /// <summary>
    /// The service or model being profiled
    /// </summary>
    [JsonPropertyName("identifier")]
    public required string Identifier { get; init; }

    /// <summary>
    /// Type of profile (service, model, endpoint)
    /// </summary>
    [JsonPropertyName("type")]
    public ProfileType Type { get; init; }

    /// <summary>
    /// Time period this profile covers
    /// </summary>
    [JsonPropertyName("time_period")]
    public required TimePeriod TimePeriod { get; init; }

    /// <summary>
    /// Response time statistics
    /// </summary>
    [JsonPropertyName("response_times")]
    public required ResponseTimeStats ResponseTimes { get; init; }

    /// <summary>
    /// Throughput statistics
    /// </summary>
    [JsonPropertyName("throughput")]
    public required ThroughputStats Throughput { get; init; }

    /// <summary>
    /// Error rate statistics
    /// </summary>
    [JsonPropertyName("error_rates")]
    public required ErrorRateStats ErrorRates { get; init; }

    /// <summary>
    /// Resource utilization statistics
    /// </summary>
    [JsonPropertyName("resource_usage")]
    public ResourceUsageStats? ResourceUsage { get; init; }

    /// <summary>
    /// Batch processing performance (if applicable)
    /// </summary>
    [JsonPropertyName("batch_performance")]
    public BatchPerformanceStats? BatchPerformance { get; init; }

    /// <summary>
    /// Cost efficiency metrics
    /// </summary>
    [JsonPropertyName("cost_efficiency")]
    public CostEfficiencyStats? CostEfficiency { get; init; }

    /// <summary>
    /// Performance trends over time
    /// </summary>
    [JsonPropertyName("trends")]
    public ImmutableList<PerformanceTrend>? Trends { get; init; }
}

/// <summary>
/// Response time statistics
/// </summary>
public record ResponseTimeStats
{
    /// <summary>
    /// Average response time in milliseconds
    /// </summary>
    [JsonPropertyName("average_ms")]
    public double AverageMs { get; init; }

    /// <summary>
    /// Median response time in milliseconds
    /// </summary>
    [JsonPropertyName("median_ms")]
    public double MedianMs { get; init; }

    /// <summary>
    /// 95th percentile response time
    /// </summary>
    [JsonPropertyName("p95_ms")]
    public double P95Ms { get; init; }

    /// <summary>
    /// 99th percentile response time
    /// </summary>
    [JsonPropertyName("p99_ms")]
    public double P99Ms { get; init; }

    /// <summary>
    /// Minimum response time
    /// </summary>
    [JsonPropertyName("min_ms")]
    public double MinMs { get; init; }

    /// <summary>
    /// Maximum response time
    /// </summary>
    [JsonPropertyName("max_ms")]
    public double MaxMs { get; init; }

    /// <summary>
    /// Standard deviation of response times
    /// </summary>
    [JsonPropertyName("std_dev_ms")]
    public double StdDevMs { get; init; }
}

/// <summary>
/// Throughput statistics
/// </summary>
public record ThroughputStats
{
    /// <summary>
    /// Requests per second
    /// </summary>
    [JsonPropertyName("requests_per_second")]
    public double RequestsPerSecond { get; init; }

    /// <summary>
    /// Tokens processed per second
    /// </summary>
    [JsonPropertyName("tokens_per_second")]
    public double? TokensPerSecond { get; init; }

    /// <summary>
    /// Peak requests per second
    /// </summary>
    [JsonPropertyName("peak_rps")]
    public double PeakRps { get; init; }

    /// <summary>
    /// Total requests in the time period
    /// </summary>
    [JsonPropertyName("total_requests")]
    public long TotalRequests { get; init; }

    /// <summary>
    /// Total tokens processed
    /// </summary>
    [JsonPropertyName("total_tokens")]
    public long? TotalTokens { get; init; }
}

/// <summary>
/// Error rate statistics
/// </summary>
public record ErrorRateStats
{
    /// <summary>
    /// Overall error rate as a percentage
    /// </summary>
    [JsonPropertyName("error_rate_percent")]
    public double ErrorRatePercent { get; init; }

    /// <summary>
    /// Total number of errors
    /// </summary>
    [JsonPropertyName("total_errors")]
    public long TotalErrors { get; init; }

    /// <summary>
    /// Error breakdown by type
    /// </summary>
    [JsonPropertyName("error_breakdown")]
    public ImmutableDictionary<string, long>? ErrorBreakdown { get; init; }

    /// <summary>
    /// Average retry count per failed request
    /// </summary>
    [JsonPropertyName("average_retries")]
    public double AverageRetries { get; init; }

    /// <summary>
    /// Success rate after retries
    /// </summary>
    [JsonPropertyName("success_rate_after_retries_percent")]
    public double SuccessRateAfterRetriesPercent { get; init; }
}

/// <summary>
/// Resource utilization statistics
/// </summary>
public record ResourceUsageStats
{
    /// <summary>
    /// Average memory usage in MB
    /// </summary>
    [JsonPropertyName("avg_memory_mb")]
    public double? AvgMemoryMb { get; init; }

    /// <summary>
    /// Peak memory usage in MB
    /// </summary>
    [JsonPropertyName("peak_memory_mb")]
    public double? PeakMemoryMb { get; init; }

    /// <summary>
    /// Average CPU usage percentage
    /// </summary>
    [JsonPropertyName("avg_cpu_percent")]
    public double? AvgCpuPercent { get; init; }

    /// <summary>
    /// Network bandwidth usage in MB
    /// </summary>
    [JsonPropertyName("network_mb")]
    public double? NetworkMb { get; init; }

    /// <summary>
    /// Average concurrent connections
    /// </summary>
    [JsonPropertyName("avg_connections")]
    public double? AvgConnections { get; init; }
}

/// <summary>
/// Batch processing performance statistics
/// </summary>
public record BatchPerformanceStats
{
    /// <summary>
    /// Average batch size
    /// </summary>
    [JsonPropertyName("avg_batch_size")]
    public double AvgBatchSize { get; init; }

    /// <summary>
    /// Optimal batch size for best performance
    /// </summary>
    [JsonPropertyName("optimal_batch_size")]
    public int? OptimalBatchSize { get; init; }

    /// <summary>
    /// Batch processing efficiency (throughput improvement vs single requests)
    /// </summary>
    [JsonPropertyName("batch_efficiency_percent")]
    public double? BatchEfficiencyPercent { get; init; }

    /// <summary>
    /// Average time spent waiting for batches to fill
    /// </summary>
    [JsonPropertyName("avg_batch_wait_ms")]
    public double? AvgBatchWaitMs { get; init; }

    /// <summary>
    /// Percentage of requests that were batched
    /// </summary>
    [JsonPropertyName("batch_utilization_percent")]
    public double BatchUtilizationPercent { get; init; }
}

/// <summary>
/// Cost efficiency statistics
/// </summary>
public record CostEfficiencyStats
{
    /// <summary>
    /// Average cost per request
    /// </summary>
    [JsonPropertyName("avg_cost_per_request")]
    public decimal AvgCostPerRequest { get; init; }

    /// <summary>
    /// Cost per successful request (excluding failed attempts)
    /// </summary>
    [JsonPropertyName("cost_per_success")]
    public decimal CostPerSuccess { get; init; }

    /// <summary>
    /// Total cost for the time period
    /// </summary>
    [JsonPropertyName("total_cost")]
    public decimal TotalCost { get; init; }

    /// <summary>
    /// Currency for cost metrics
    /// </summary>
    [JsonPropertyName("currency")]
    public string Currency { get; init; } = "USD";

    /// <summary>
    /// Cost efficiency trend (improving/degrading)
    /// </summary>
    [JsonPropertyName("efficiency_trend")]
    public TrendDirection? EfficiencyTrend { get; init; }
}

/// <summary>
/// Performance trend data point
/// </summary>
public record PerformanceTrend
{
    /// <summary>
    /// Timestamp for this data point
    /// </summary>
    [JsonPropertyName("timestamp")]
    public DateTime Timestamp { get; init; }

    /// <summary>
    /// The metric being tracked
    /// </summary>
    [JsonPropertyName("metric")]
    public required string Metric { get; init; }

    /// <summary>
    /// The value at this point in time
    /// </summary>
    [JsonPropertyName("value")]
    public double Value { get; init; }

    /// <summary>
    /// Trend direction for this metric
    /// </summary>
    [JsonPropertyName("trend")]
    public TrendDirection Trend { get; init; }
}

/// <summary>
/// Aggregated usage statistics
/// </summary>
public record UsageStatistics
{
    /// <summary>
    /// The entity these statistics apply to (service, user, tenant, etc.)
    /// </summary>
    [JsonPropertyName("entity")]
    public required string Entity { get; init; }

    /// <summary>
    /// Type of entity (service, user, tenant)
    /// </summary>
    [JsonPropertyName("entity_type")]
    public required string EntityType { get; init; }

    /// <summary>
    /// Time period for these statistics
    /// </summary>
    [JsonPropertyName("time_period")]
    public required TimePeriod TimePeriod { get; init; }

    /// <summary>
    /// Request volume statistics
    /// </summary>
    [JsonPropertyName("request_volume")]
    public required VolumeStats RequestVolume { get; init; }

    /// <summary>
    /// Token usage statistics
    /// </summary>
    [JsonPropertyName("token_usage")]
    public TokenUsageStats? TokenUsage { get; init; }

    /// <summary>
    /// Model usage breakdown
    /// </summary>
    [JsonPropertyName("model_usage")]
    public ImmutableDictionary<string, VolumeStats>? ModelUsage { get; init; }

    /// <summary>
    /// Feature usage statistics
    /// </summary>
    [JsonPropertyName("feature_usage")]
    public FeatureUsageStats? FeatureUsage { get; init; }

    /// <summary>
    /// Cost statistics
    /// </summary>
    [JsonPropertyName("cost_stats")]
    public CostStatistics? CostStats { get; init; }

    /// <summary>
    /// Quality metrics
    /// </summary>
    [JsonPropertyName("quality_metrics")]
    public QualityMetrics? QualityMetrics { get; init; }
}

/// <summary>
/// Volume statistics for requests or other countable entities
/// </summary>
public record VolumeStats
{
    /// <summary>
    /// Total count for the period
    /// </summary>
    [JsonPropertyName("total")]
    public long Total { get; init; }

    /// <summary>
    /// Average per day
    /// </summary>
    [JsonPropertyName("avg_per_day")]
    public double AvgPerDay { get; init; }

    /// <summary>
    /// Peak count in a single day
    /// </summary>
    [JsonPropertyName("peak_per_day")]
    public long PeakPerDay { get; init; }

    /// <summary>
    /// Growth rate compared to previous period
    /// </summary>
    [JsonPropertyName("growth_rate_percent")]
    public double? GrowthRatePercent { get; init; }
}

/// <summary>
/// Token usage statistics
/// </summary>
public record TokenUsageStats
{
    /// <summary>
    /// Total tokens processed
    /// </summary>
    [JsonPropertyName("total_tokens")]
    public long TotalTokens { get; init; }

    /// <summary>
    /// Average tokens per request
    /// </summary>
    [JsonPropertyName("avg_tokens_per_request")]
    public double AvgTokensPerRequest { get; init; }

    /// <summary>
    /// Token usage by input size brackets
    /// </summary>
    [JsonPropertyName("usage_by_size")]
    public ImmutableDictionary<string, long>? UsageBySize { get; init; }

    /// <summary>
    /// Token efficiency metrics
    /// </summary>
    [JsonPropertyName("efficiency_metrics")]
    public TokenEfficiencyStats? EfficiencyMetrics { get; init; }
}

/// <summary>
/// Token efficiency statistics
/// </summary>
public record TokenEfficiencyStats
{
    /// <summary>
    /// Percentage of tokens that were useful vs padding/overhead
    /// </summary>
    [JsonPropertyName("utilization_percent")]
    public double UtilizationPercent { get; init; }

    /// <summary>
    /// Average compression ratio achieved
    /// </summary>
    [JsonPropertyName("compression_ratio")]
    public double? CompressionRatio { get; init; }

    /// <summary>
    /// Tokens saved through optimization
    /// </summary>
    [JsonPropertyName("tokens_saved")]
    public long? TokensSaved { get; init; }
}

/// <summary>
/// Feature usage statistics
/// </summary>
public record FeatureUsageStats
{
    /// <summary>
    /// Percentage of requests using batch processing
    /// </summary>
    [JsonPropertyName("batch_usage_percent")]
    public double BatchUsagePercent { get; init; }

    /// <summary>
    /// Percentage of requests using custom dimensions
    /// </summary>
    [JsonPropertyName("custom_dimensions_percent")]
    public double CustomDimensionsPercent { get; init; }

    /// <summary>
    /// Percentage of requests using normalization
    /// </summary>
    [JsonPropertyName("normalization_percent")]
    public double NormalizationPercent { get; init; }

    /// <summary>
    /// Usage of different encoding formats
    /// </summary>
    [JsonPropertyName("encoding_format_usage")]
    public ImmutableDictionary<string, double>? EncodingFormatUsage { get; init; }
}

/// <summary>
/// Cost statistics
/// </summary>
public record CostStatistics
{
    /// <summary>
    /// Total cost for the period
    /// </summary>
    [JsonPropertyName("total_cost")]
    public decimal TotalCost { get; init; }

    /// <summary>
    /// Average cost per request
    /// </summary>
    [JsonPropertyName("avg_cost_per_request")]
    public decimal AvgCostPerRequest { get; init; }

    /// <summary>
    /// Cost breakdown by model
    /// </summary>
    [JsonPropertyName("cost_by_model")]
    public ImmutableDictionary<string, decimal>? CostByModel { get; init; }

    /// <summary>
    /// Currency for all cost values
    /// </summary>
    [JsonPropertyName("currency")]
    public string Currency { get; init; } = "USD";

    /// <summary>
    /// Cost trend compared to previous period
    /// </summary>
    [JsonPropertyName("cost_trend")]
    public TrendDirection? CostTrend { get; init; }
}

/// <summary>
/// Quality metrics for embeddings and responses
/// </summary>
public record QualityMetrics
{
    /// <summary>
    /// Average response quality score (0-1)
    /// </summary>
    [JsonPropertyName("avg_quality_score")]
    public double? AvgQualityScore { get; init; }

    /// <summary>
    /// Percentage of responses that met quality thresholds
    /// </summary>
    [JsonPropertyName("quality_threshold_met_percent")]
    public double? QualityThresholdMetPercent { get; init; }

    /// <summary>
    /// User satisfaction scores (if available)
    /// </summary>
    [JsonPropertyName("user_satisfaction")]
    public double? UserSatisfaction { get; init; }

    /// <summary>
    /// Embedding coherence metrics
    /// </summary>
    [JsonPropertyName("coherence_metrics")]
    public CoherenceMetrics? CoherenceMetrics { get; init; }
}

/// <summary>
/// Coherence metrics for embedding quality
/// </summary>
public record CoherenceMetrics
{
    /// <summary>
    /// Average cosine similarity within document clusters
    /// </summary>
    [JsonPropertyName("avg_intra_cluster_similarity")]
    public double? AvgIntraClusterSimilarity { get; init; }

    /// <summary>
    /// Average distance between different document clusters
    /// </summary>
    [JsonPropertyName("avg_inter_cluster_distance")]
    public double? AvgInterClusterDistance { get; init; }

    /// <summary>
    /// Silhouette score for clustering quality
    /// </summary>
    [JsonPropertyName("silhouette_score")]
    public double? SilhouetteScore { get; init; }
}

/// <summary>
/// Time period specification
/// </summary>
public record TimePeriod
{
    /// <summary>
    /// Start of the time period
    /// </summary>
    [JsonPropertyName("start")]
    public DateTime Start { get; init; }

    /// <summary>
    /// End of the time period
    /// </summary>
    [JsonPropertyName("end")]
    public DateTime End { get; init; }

    /// <summary>
    /// Duration in seconds
    /// </summary>
    [JsonPropertyName("duration_seconds")]
    public double DurationSeconds => (End - Start).TotalSeconds;

    /// <summary>
    /// Human-readable description of the period
    /// </summary>
    [JsonPropertyName("description")]
    public string? Description { get; init; }
}

/// <summary>
/// Profile type enumeration
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ProfileType
{
    /// <summary>
    /// Performance profile for an entire service
    /// </summary>
    Service,

    /// <summary>
    /// Performance profile for a specific model
    /// </summary>
    Model,

    /// <summary>
    /// Performance profile for an API endpoint
    /// </summary>
    Endpoint,

    /// <summary>
    /// Performance profile for a user or tenant
    /// </summary>
    User,

    /// <summary>
    /// Performance profile for a specific feature
    /// </summary>
    Feature
}

/// <summary>
/// Trend direction enumeration
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TrendDirection
{
    /// <summary>
    /// Metric is improving
    /// </summary>
    Improving,

    /// <summary>
    /// Metric is stable
    /// </summary>
    Stable,

    /// <summary>
    /// Metric is degrading
    /// </summary>
    Degrading,

    /// <summary>
    /// Trend is unknown or inconsistent
    /// </summary>
    Unknown
} 