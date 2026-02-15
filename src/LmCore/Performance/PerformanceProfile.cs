namespace AchieveAi.LmDotnetTools.LmCore.Performance;

/// <summary>
///     Aggregates performance data over time periods for profiling and analysis.
///     Provides statistical insights into provider performance patterns.
/// </summary>
public record PerformanceProfile
{
    /// <summary>Provider name for this profile</summary>
    public string Provider { get; init; } = string.Empty;

    /// <summary>Model name for this profile (empty for all models)</summary>
    public string Model { get; init; } = string.Empty;

    /// <summary>Time period start for this profile</summary>
    public DateTimeOffset PeriodStart { get; init; }

    /// <summary>Time period end for this profile</summary>
    public DateTimeOffset PeriodEnd { get; init; }

    /// <summary>Total number of requests in this period</summary>
    public int TotalRequests { get; init; }

    /// <summary>Number of successful requests</summary>
    public int SuccessfulRequests { get; init; }

    /// <summary>Number of failed requests</summary>
    public int FailedRequests { get; init; }

    /// <summary>Number of requests that required retries</summary>
    public int RetriedRequests { get; init; }

    /// <summary>Success rate as a percentage (0-100)</summary>
    public double SuccessRate => TotalRequests > 0 ? SuccessfulRequests * 100.0 / TotalRequests : 0;

    /// <summary>Retry rate as a percentage (0-100)</summary>
    public double RetryRate => TotalRequests > 0 ? RetriedRequests * 100.0 / TotalRequests : 0;

    /// <summary>Average request duration</summary>
    public TimeSpan AverageRequestDuration { get; init; }

    /// <summary>Minimum request duration</summary>
    public TimeSpan MinRequestDuration { get; init; }

    /// <summary>Maximum request duration</summary>
    public TimeSpan MaxRequestDuration { get; init; }

    /// <summary>95th percentile request duration</summary>
    public TimeSpan P95RequestDuration { get; init; }

    /// <summary>99th percentile request duration</summary>
    public TimeSpan P99RequestDuration { get; init; }

    /// <summary>Total tokens processed (input + output)</summary>
    public long TotalTokens { get; init; }

    /// <summary>Total prompt tokens (input)</summary>
    public long PromptTokens { get; init; }

    /// <summary>Total completion tokens (output)</summary>
    public long CompletionTokens { get; init; }

    /// <summary>Average tokens per request</summary>
    public double AverageTokensPerRequest => TotalRequests > 0 ? (double)TotalTokens / TotalRequests : 0;

    /// <summary>Total request size in bytes</summary>
    public long TotalRequestBytes { get; init; }

    /// <summary>Total response size in bytes</summary>
    public long TotalResponseBytes { get; init; }

    /// <summary>Average request size in bytes</summary>
    public double AverageRequestSizeBytes => TotalRequests > 0 ? (double)TotalRequestBytes / TotalRequests : 0;

    /// <summary>Average response size in bytes</summary>
    public double AverageResponseSizeBytes => TotalRequests > 0 ? (double)TotalResponseBytes / TotalRequests : 0;

    /// <summary>Most common error types and their counts</summary>
    public Dictionary<string, int> ErrorTypes { get; init; } = [];

    /// <summary>HTTP status code distribution</summary>
    public Dictionary<int, int> StatusCodeDistribution { get; init; } = [];

    /// <summary>Creates a performance profile from a collection of request metrics</summary>
    /// <param name="metrics">Collection of request metrics</param>
    /// <param name="provider">Provider name filter (optional)</param>
    /// <param name="model">Model name filter (optional)</param>
    /// <returns>Aggregated performance profile</returns>
    public static PerformanceProfile FromMetrics(
        IEnumerable<RequestMetrics> metrics,
        string provider = "",
        string model = ""
    )
    {
        var metricsList = metrics.ToList();

        if (metricsList.Count == 0)
        {
            return new PerformanceProfile
            {
                Provider = provider,
                Model = model,
                PeriodStart = DateTimeOffset.UtcNow,
                PeriodEnd = DateTimeOffset.UtcNow,
            };
        }

        // Filter by provider and model if specified
        if (!string.IsNullOrEmpty(provider))
        {
            metricsList = [.. metricsList.Where(m => m.Provider.Equals(provider, StringComparison.OrdinalIgnoreCase))];
        }

        if (!string.IsNullOrEmpty(model))
        {
            metricsList = [.. metricsList.Where(m => m.Model.Equals(model, StringComparison.OrdinalIgnoreCase))];
        }

        var successfulRequests = metricsList.Where(m => m.IsSuccess).ToList();
        var failedRequests = metricsList.Where(m => !m.IsSuccess).ToList();
        var retriedRequests = metricsList.Where(m => m.RetryAttempts > 0).ToList();

        var durations = metricsList.Select(m => m.Duration).Where(d => d > TimeSpan.Zero).ToList();
        durations.Sort();

        var totalTokens = metricsList.Sum(m => m.Usage?.TotalTokens ?? 0);
        var inputTokens = metricsList.Sum(m => m.Usage?.PromptTokens ?? 0);
        var outputTokens = metricsList.Sum(m => m.Usage?.CompletionTokens ?? 0);

        return new PerformanceProfile
        {
            Provider = provider,
            Model = model,
            PeriodStart = metricsList.Min(m => m.StartTime),
            PeriodEnd = metricsList.Max(m => m.EndTime),
            TotalRequests = metricsList.Count,
            SuccessfulRequests = successfulRequests.Count,
            FailedRequests = failedRequests.Count,
            RetriedRequests = retriedRequests.Count,
            AverageRequestDuration =
                durations.Count != 0 ? TimeSpan.FromTicks((long)durations.Average(d => d.Ticks)) : TimeSpan.Zero,
            MinRequestDuration = durations.Count != 0 ? durations.Min() : TimeSpan.Zero,
            MaxRequestDuration = durations.Count != 0 ? durations.Max() : TimeSpan.Zero,
            P95RequestDuration = durations.Count != 0 ? durations[(int)(durations.Count * 0.95)] : TimeSpan.Zero,
            P99RequestDuration = durations.Count != 0 ? durations[(int)(durations.Count * 0.99)] : TimeSpan.Zero,
            TotalTokens = totalTokens,
            PromptTokens = inputTokens,
            CompletionTokens = outputTokens,
            TotalRequestBytes = metricsList.Sum(m => m.RequestSizeBytes),
            TotalResponseBytes = metricsList.Sum(m => m.ResponseSizeBytes),
            ErrorTypes = failedRequests
                .Where(m => !string.IsNullOrEmpty(m.ExceptionType))
                .GroupBy(m => m.ExceptionType!)
                .ToDictionary(g => g.Key, g => g.Count()),
            StatusCodeDistribution = metricsList
                .Where(m => m.StatusCode > 0)
                .GroupBy(m => m.StatusCode)
                .ToDictionary(g => g.Key, g => g.Count()),
        };
    }
}
