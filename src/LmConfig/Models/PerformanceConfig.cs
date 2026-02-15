using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.LmConfig.Models;

/// <summary>
///     Core performance metrics for provider comparison.
///     Focuses on the 2 key dimensions: latency and throughput.
/// </summary>
public record PerformanceConfig
{
    /// <summary>
    ///     Latency for first token in milliseconds - key user experience metric.
    /// </summary>
    [JsonPropertyName("latency_first_token_ms")]
    public double? LatencyFirstTokenMs { get; init; }

    /// <summary>
    ///     Throughput in tokens per second - key speed metric.
    /// </summary>
    [JsonPropertyName("throughput_tokens_per_second")]
    public double? ThroughputTokensPerSecond { get; init; }

    /// <summary>
    ///     Whether these are real measured stats or estimates.
    /// </summary>
    [JsonPropertyName("has_real_stats")]
    public bool HasRealStats { get; init; } = false;
}
