using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.LmEmbeddings.Models;

/// <summary>
/// Usage statistics for an entity (service, user, tenant, etc.)
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
/// Volume statistics for a specific metric
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
/// Cost statistics for usage tracking
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