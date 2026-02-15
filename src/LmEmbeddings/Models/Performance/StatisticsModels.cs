using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.LmEmbeddings.Models;

/// <summary>
///     Response time statistics
/// </summary>
public record ResponseTimeStats
{
    /// <summary>
    ///     Average response time in milliseconds
    /// </summary>
    [JsonPropertyName("average_ms")]
    public double AverageMs { get; init; }

    /// <summary>
    ///     Median response time in milliseconds
    /// </summary>
    [JsonPropertyName("median_ms")]
    public double MedianMs { get; init; }

    /// <summary>
    ///     95th percentile response time
    /// </summary>
    [JsonPropertyName("p95_ms")]
    public double P95Ms { get; init; }

    /// <summary>
    ///     99th percentile response time
    /// </summary>
    [JsonPropertyName("p99_ms")]
    public double P99Ms { get; init; }

    /// <summary>
    ///     Minimum response time
    /// </summary>
    [JsonPropertyName("min_ms")]
    public double MinMs { get; init; }

    /// <summary>
    ///     Maximum response time
    /// </summary>
    [JsonPropertyName("max_ms")]
    public double MaxMs { get; init; }

    /// <summary>
    ///     Standard deviation of response times
    /// </summary>
    [JsonPropertyName("std_dev_ms")]
    public double StdDevMs { get; init; }
}

/// <summary>
///     Throughput statistics
/// </summary>
public record ThroughputStats
{
    /// <summary>
    ///     Requests per second
    /// </summary>
    [JsonPropertyName("requests_per_second")]
    public double RequestsPerSecond { get; init; }

    /// <summary>
    ///     Tokens processed per second
    /// </summary>
    [JsonPropertyName("tokens_per_second")]
    public double? TokensPerSecond { get; init; }

    /// <summary>
    ///     Peak requests per second
    /// </summary>
    [JsonPropertyName("peak_rps")]
    public double PeakRps { get; init; }

    /// <summary>
    ///     Total requests in the time period
    /// </summary>
    [JsonPropertyName("total_requests")]
    public long TotalRequests { get; init; }

    /// <summary>
    ///     Total tokens processed
    /// </summary>
    [JsonPropertyName("total_tokens")]
    public long? TotalTokens { get; init; }
}

/// <summary>
///     Error rate statistics
/// </summary>
public record ErrorRateStats
{
    /// <summary>
    ///     Overall error rate as a percentage
    /// </summary>
    [JsonPropertyName("error_rate_percent")]
    public double ErrorRatePercent { get; init; }

    /// <summary>
    ///     Total number of errors
    /// </summary>
    [JsonPropertyName("total_errors")]
    public long TotalErrors { get; init; }

    /// <summary>
    ///     Error breakdown by type
    /// </summary>
    [JsonPropertyName("error_breakdown")]
    public ImmutableDictionary<string, long>? ErrorBreakdown { get; init; }

    /// <summary>
    ///     Average retry count per failed request
    /// </summary>
    [JsonPropertyName("average_retries")]
    public double AverageRetries { get; init; }

    /// <summary>
    ///     Success rate after retries
    /// </summary>
    [JsonPropertyName("success_rate_after_retries_percent")]
    public double SuccessRateAfterRetriesPercent { get; init; }
}

/// <summary>
///     Resource usage statistics
/// </summary>
public record ResourceUsageStats
{
    /// <summary>
    ///     Average memory usage in MB
    /// </summary>
    [JsonPropertyName("avg_memory_mb")]
    public double? AvgMemoryMb { get; init; }

    /// <summary>
    ///     Peak memory usage in MB
    /// </summary>
    [JsonPropertyName("peak_memory_mb")]
    public double? PeakMemoryMb { get; init; }

    /// <summary>
    ///     Average CPU usage percentage
    /// </summary>
    [JsonPropertyName("avg_cpu_percent")]
    public double? AvgCpuPercent { get; init; }

    /// <summary>
    ///     Network bandwidth usage in MB
    /// </summary>
    [JsonPropertyName("network_mb")]
    public double? NetworkMb { get; init; }

    /// <summary>
    ///     Average concurrent connections
    /// </summary>
    [JsonPropertyName("avg_connections")]
    public double? AvgConnections { get; init; }
}

/// <summary>
///     Batch processing performance statistics
/// </summary>
public record BatchPerformanceStats
{
    /// <summary>
    ///     Average batch size
    /// </summary>
    [JsonPropertyName("avg_batch_size")]
    public double AvgBatchSize { get; init; }

    /// <summary>
    ///     Optimal batch size for best performance
    /// </summary>
    [JsonPropertyName("optimal_batch_size")]
    public int? OptimalBatchSize { get; init; }

    /// <summary>
    ///     Batch processing efficiency (throughput improvement vs single requests)
    /// </summary>
    [JsonPropertyName("batch_efficiency_percent")]
    public double? BatchEfficiencyPercent { get; init; }

    /// <summary>
    ///     Average time spent waiting for batches to fill
    /// </summary>
    [JsonPropertyName("avg_batch_wait_ms")]
    public double? AvgBatchWaitMs { get; init; }

    /// <summary>
    ///     Percentage of requests that were batched
    /// </summary>
    [JsonPropertyName("batch_utilization_percent")]
    public double BatchUtilizationPercent { get; init; }
}

/// <summary>
///     Cost efficiency statistics
/// </summary>
public record CostEfficiencyStats
{
    /// <summary>
    ///     Average cost per request
    /// </summary>
    [JsonPropertyName("avg_cost_per_request")]
    public decimal AvgCostPerRequest { get; init; }

    /// <summary>
    ///     Cost per successful request (excluding failed attempts)
    /// </summary>
    [JsonPropertyName("cost_per_success")]
    public decimal CostPerSuccess { get; init; }

    /// <summary>
    ///     Total cost for the time period
    /// </summary>
    [JsonPropertyName("total_cost")]
    public decimal TotalCost { get; init; }

    /// <summary>
    ///     Currency for cost metrics
    /// </summary>
    [JsonPropertyName("currency")]
    public string Currency { get; init; } = "USD";

    /// <summary>
    ///     Cost efficiency trend (improving/degrading)
    /// </summary>
    [JsonPropertyName("efficiency_trend")]
    public TrendDirection? EfficiencyTrend { get; init; }
}
