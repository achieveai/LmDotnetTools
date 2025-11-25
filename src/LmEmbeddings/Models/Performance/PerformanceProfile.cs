using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.LmEmbeddings.Models;

/// <summary>
///     Comprehensive performance profile for a service, model, or endpoint
/// </summary>
public record PerformanceProfile
{
    /// <summary>
    ///     The service or model being profiled
    /// </summary>
    [JsonPropertyName("identifier")]
    public required string Identifier { get; init; }

    /// <summary>
    ///     Type of profile (service, model, endpoint)
    /// </summary>
    [JsonPropertyName("type")]
    public ProfileType Type { get; init; }

    /// <summary>
    ///     Time period this profile covers
    /// </summary>
    [JsonPropertyName("time_period")]
    public required TimePeriod TimePeriod { get; init; }

    /// <summary>
    ///     Response time statistics
    /// </summary>
    [JsonPropertyName("response_times")]
    public required ResponseTimeStats ResponseTimes { get; init; }

    /// <summary>
    ///     Throughput statistics
    /// </summary>
    [JsonPropertyName("throughput")]
    public required ThroughputStats Throughput { get; init; }

    /// <summary>
    ///     Error rate statistics
    /// </summary>
    [JsonPropertyName("error_rates")]
    public required ErrorRateStats ErrorRates { get; init; }

    /// <summary>
    ///     Resource utilization statistics
    /// </summary>
    [JsonPropertyName("resource_usage")]
    public ResourceUsageStats? ResourceUsage { get; init; }

    /// <summary>
    ///     Batch processing performance (if applicable)
    /// </summary>
    [JsonPropertyName("batch_performance")]
    public BatchPerformanceStats? BatchPerformance { get; init; }

    /// <summary>
    ///     Cost efficiency metrics
    /// </summary>
    [JsonPropertyName("cost_efficiency")]
    public CostEfficiencyStats? CostEfficiency { get; init; }

    /// <summary>
    ///     Performance trends over time
    /// </summary>
    [JsonPropertyName("trends")]
    public ImmutableList<PerformanceTrend>? Trends { get; init; }
}

/// <summary>
///     Performance trend data point
/// </summary>
public record PerformanceTrend
{
    /// <summary>
    ///     Timestamp for this data point
    /// </summary>
    [JsonPropertyName("timestamp")]
    public DateTime Timestamp { get; init; }

    /// <summary>
    ///     The metric being tracked
    /// </summary>
    [JsonPropertyName("metric")]
    public required string Metric { get; init; }

    /// <summary>
    ///     The value at this point in time
    /// </summary>
    [JsonPropertyName("value")]
    public double Value { get; init; }

    /// <summary>
    ///     Trend direction for this metric
    /// </summary>
    [JsonPropertyName("trend")]
    public TrendDirection Trend { get; init; }
}

/// <summary>
///     Type of performance profile
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ProfileType
{
    /// <summary>
    ///     Performance profile for an entire service
    /// </summary>
    Service,

    /// <summary>
    ///     Performance profile for a specific model
    /// </summary>
    Model,

    /// <summary>
    ///     Performance profile for an API endpoint
    /// </summary>
    Endpoint,

    /// <summary>
    ///     Performance profile for a user or tenant
    /// </summary>
    User,

    /// <summary>
    ///     Performance profile for a specific feature
    /// </summary>
    Feature,
}
