namespace AchieveAi.LmDotnetTools.LmCore.Performance;

/// <summary>
/// Default implementation of IPerformanceTracker that provides comprehensive performance tracking
/// across all providers with thread-safe operations.
/// </summary>
public class PerformanceTracker : IPerformanceTracker
{
    private readonly object _lock = new();
    private readonly Dictionary<string, ProviderStatistics> _providerStats = new();

    /// <summary>Maximum number of recent metrics to keep per provider</summary>
    public int MaxRecentMetricsPerProvider { get; init; } = 1000;

    /// <summary>Creates a new PerformanceTracker instance</summary>
    /// <param name="maxRecentMetricsPerProvider">Maximum recent metrics to retain per provider</param>
    public PerformanceTracker(int maxRecentMetricsPerProvider = 1000)
    {
        MaxRecentMetricsPerProvider = maxRecentMetricsPerProvider;
    }

    /// <summary>Records a request metric</summary>
    /// <param name="metric">The request metric to record</param>
    public void TrackRequest(RequestMetrics metric)
    {
        if (metric == null || string.IsNullOrEmpty(metric.Provider))
            return;

        lock (_lock)
        {
            if (!_providerStats.TryGetValue(metric.Provider, out var providerStats))
            {
                providerStats = new ProviderStatistics(metric.Provider, MaxRecentMetricsPerProvider);
                _providerStats[metric.Provider] = providerStats;
            }

            providerStats.RecordMetric(metric);
        }
    }

    /// <summary>Gets statistics for a specific provider</summary>
    /// <param name="provider">Provider name (OpenAI, Anthropic, etc.)</param>
    /// <returns>Provider statistics or null if not found</returns>
    public ProviderStatistics? GetProviderStatistics(string provider)
    {
        if (string.IsNullOrEmpty(provider))
            return null;

        lock (_lock)
        {
            return _providerStats.TryGetValue(provider, out var stats) ? stats : null;
        }
    }

    /// <summary>Gets statistics for all providers</summary>
    /// <returns>Dictionary of provider statistics by provider name</returns>
    public IReadOnlyDictionary<string, ProviderStatistics> GetAllProviderStatistics()
    {
        lock (_lock)
        {
            return _providerStats.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        }
    }

    /// <summary>Gets a performance profile for a specific provider</summary>
    /// <param name="provider">Provider name</param>
    /// <param name="model">Optional model filter</param>
    /// <param name="since">Optional time filter (defaults to last 24 hours)</param>
    /// <returns>Performance profile for the specified criteria</returns>
    public PerformanceProfile GetPerformanceProfile(string provider, string model = "", DateTimeOffset? since = null)
    {
        if (string.IsNullOrEmpty(provider))
        {
            return new PerformanceProfile
            {
                Provider = provider,
                Model = model,
                PeriodStart = DateTimeOffset.UtcNow,
                PeriodEnd = DateTimeOffset.UtcNow
            };
        }

        var providerStats = GetProviderStatistics(provider);
        if (providerStats == null)
        {
            return new PerformanceProfile
            {
                Provider = provider,
                Model = model,
                PeriodStart = DateTimeOffset.UtcNow,
                PeriodEnd = DateTimeOffset.UtcNow
            };
        }

        var sinceTime = since ?? DateTimeOffset.UtcNow.AddDays(-1);
        return providerStats.GetProfileSince(sinceTime, model);
    }

    /// <summary>Gets performance profiles for all providers</summary>
    /// <param name="since">Optional time filter (defaults to last 24 hours)</param>
    /// <returns>Performance profiles for all providers</returns>
    public IEnumerable<PerformanceProfile> GetAllPerformanceProfiles(DateTimeOffset? since = null)
    {
        var sinceTime = since ?? DateTimeOffset.UtcNow.AddDays(-1);

        lock (_lock)
        {
            return _providerStats.Values
                .Select(stats => stats.GetProfileSince(sinceTime))
                .ToList();
        }
    }

    /// <summary>Resets statistics for a specific provider</summary>
    /// <param name="provider">Provider name to reset</param>
    public void ResetProviderStatistics(string provider)
    {
        if (string.IsNullOrEmpty(provider))
            return;

        lock (_lock)
        {
            if (_providerStats.TryGetValue(provider, out var stats))
            {
                stats.Reset();
            }
        }
    }

    /// <summary>Resets all statistics</summary>
    public void ResetAllStatistics()
    {
        lock (_lock)
        {
            foreach (var stats in _providerStats.Values)
            {
                stats.Reset();
            }
        }
    }

    /// <summary>Gets the top performing models across all providers</summary>
    /// <param name="count">Number of top models to return</param>
    /// <param name="orderBy">Ordering criteria (requests, tokens, success_rate, avg_duration)</param>
    /// <returns>Top performing models</returns>
    public IEnumerable<(string Provider, string Model, ModelStatistics Stats)> GetTopModels(
        int count = 10,
        string orderBy = "requests")
    {
        lock (_lock)
        {
            var allModels = _providerStats
                .SelectMany(kvp => kvp.Value.ModelStatistics.Select(ms =>
                    (Provider: kvp.Key, Model: ms.Key, Stats: ms.Value)))
                .ToList();

            var ordered = orderBy.ToLower() switch
            {
                "tokens" => allModels.OrderByDescending(m => m.Stats.TotalTokens),
                "success_rate" => allModels.OrderByDescending(m => m.Stats.SuccessRate),
                "avg_duration" => allModels.OrderBy(m => m.Stats.AverageRequestDuration),
                _ => allModels.OrderByDescending(m => m.Stats.TotalRequests)
            };

            return ordered.Take(count).ToList();
        }
    }

    /// <summary>Gets current overall statistics across all providers</summary>
    /// <returns>Aggregated statistics</returns>
    public OverallStatistics GetOverallStatistics()
    {
        lock (_lock)
        {
            var allProviders = _providerStats.Values.ToList();

            if (!allProviders.Any())
            {
                return new OverallStatistics
                {
                    TotalProviders = 0,
                    TotalModels = 0,
                    ProviderSummaries = new Dictionary<string, ProviderSummary>()
                };
            }

            var totalRequests = allProviders.Sum(p => p.TotalRequests);
            var successfulRequests = allProviders.Sum(p => p.SuccessfulRequests);
            var failedRequests = allProviders.Sum(p => p.FailedRequests);
            var retriedRequests = allProviders.Sum(p => p.RetriedRequests);
            var totalTokens = allProviders.Sum(p => p.TotalTokensProcessed);
            var totalProcessingTime = allProviders
                .Select(p => p.TotalProcessingTime)
                .Aggregate(TimeSpan.Zero, (acc, time) => acc.Add(time));

            var providerSummaries = allProviders.ToDictionary(
                p => p.Provider,
                p => new ProviderSummary
                {
                    Provider = p.Provider,
                    ModelCount = p.ModelStatistics.Count,
                    TotalRequests = p.TotalRequests,
                    SuccessRate = p.SuccessRate,
                    AverageRequestDuration = p.AverageRequestDuration,
                    TotalTokens = p.TotalTokensProcessed
                });

            return new OverallStatistics
            {
                TotalProviders = allProviders.Count,
                TotalModels = allProviders.Sum(p => p.ModelStatistics.Count),
                TotalRequests = totalRequests,
                SuccessfulRequests = successfulRequests,
                FailedRequests = failedRequests,
                RetriedRequests = retriedRequests,
                TotalTokensProcessed = totalTokens,
                TotalProcessingTime = totalProcessingTime,
                ProviderSummaries = providerSummaries
            };
        }
    }
}