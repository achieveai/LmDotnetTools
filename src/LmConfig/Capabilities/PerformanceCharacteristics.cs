using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.LmConfig.Capabilities;

/// <summary>
/// Represents the performance characteristics of a model.
/// </summary>
public record PerformanceCharacteristics
{
    /// <summary>
    /// Typical response latency for the model.
    /// </summary>
    [JsonPropertyName("typical_latency")]
    public TimeSpan? TypicalLatency { get; init; }

    /// <summary>
    /// Maximum expected response latency for the model.
    /// </summary>
    [JsonPropertyName("max_latency")]
    public TimeSpan? MaxLatency { get; init; }

    /// <summary>
    /// Typical tokens generated per second.
    /// </summary>
    [JsonPropertyName("tokens_per_second")]
    public double? TokensPerSecond { get; init; }

    /// <summary>
    /// Quality tier of the model (e.g., "high", "medium", "low").
    /// </summary>
    [JsonPropertyName("quality_tier")]
    public string? QualityTier { get; init; }

    /// <summary>
    /// Cost tier of the model (e.g., "premium", "standard", "economy").
    /// </summary>
    [JsonPropertyName("cost_tier")]
    public string? CostTier { get; init; }

    /// <summary>
    /// Relative speed compared to other models (1.0 = baseline).
    /// </summary>
    [JsonPropertyName("relative_speed")]
    public double? RelativeSpeed { get; init; }

    /// <summary>
    /// Relative cost compared to other models (1.0 = baseline).
    /// </summary>
    [JsonPropertyName("relative_cost")]
    public double? RelativeCost { get; init; }

    /// <summary>
    /// Relative quality compared to other models (1.0 = baseline).
    /// </summary>
    [JsonPropertyName("relative_quality")]
    public double? RelativeQuality { get; init; }

    /// <summary>
    /// Whether the model performance varies significantly based on input complexity.
    /// </summary>
    [JsonPropertyName("variable_performance")]
    public bool VariablePerformance { get; init; } = false;

    /// <summary>
    /// Whether the model supports batching for improved throughput.
    /// </summary>
    [JsonPropertyName("supports_batching")]
    public bool SupportsBatching { get; init; } = false;

    /// <summary>
    /// Maximum batch size supported by the model.
    /// </summary>
    [JsonPropertyName("max_batch_size")]
    public int? MaxBatchSize { get; init; }

    /// <summary>
    /// Whether the model supports priority request handling.
    /// </summary>
    [JsonPropertyName("supports_priority")]
    public bool SupportsPriority { get; init; } = false;

    /// <summary>
    /// Available priority levels for request handling.
    /// </summary>
    [JsonPropertyName("priority_levels")]
    public IReadOnlyList<string> PriorityLevels { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Recommended concurrent request limit for optimal performance.
    /// </summary>
    [JsonPropertyName("recommended_concurrency")]
    public int? RecommendedConcurrency { get; init; }

    /// <summary>
    /// Maximum concurrent requests supported.
    /// </summary>
    [JsonPropertyName("max_concurrency")]
    public int? MaxConcurrency { get; init; }

    /// <summary>
    /// Whether the model has rate limits that may affect performance.
    /// </summary>
    [JsonPropertyName("has_rate_limits")]
    public bool HasRateLimits { get; init; } = true;

    /// <summary>
    /// Rate limit information (requests per minute, tokens per minute, etc.).
    /// </summary>
    [JsonPropertyName("rate_limits")]
    public IDictionary<string, object>? RateLimits { get; init; }
} 