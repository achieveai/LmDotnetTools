namespace AchieveAi.LmDotnetTools.LmCore.Performance;

/// <summary>
///     Maintains running statistics for each provider, including model-specific metrics and operational insights.
///     Provides real-time provider performance monitoring capabilities.
/// </summary>
public class ProviderStatistics
{
    private readonly object _lock = new();
    private readonly Dictionary<string, ModelStatistics> _modelStats = [];
    private readonly List<RequestMetrics> _recentMetrics = [];

    /// <summary>Creates a new ProviderStatistics instance</summary>
    /// <param name="provider">Provider name</param>
    /// <param name="maxRecentMetrics">Maximum number of recent metrics to retain</param>
    public ProviderStatistics(string provider, int maxRecentMetrics = 1000)
    {
        Provider = provider;
        MaxRecentMetrics = maxRecentMetrics;
    }

    /// <summary>Maximum number of recent metrics to keep in memory</summary>
    public int MaxRecentMetrics { get; init; } = 1000;

    /// <summary>Provider name</summary>
    public string Provider { get; init; } = string.Empty;

    /// <summary>Statistics collection start time</summary>
    public DateTimeOffset CollectionStartTime { get; private set; } = DateTimeOffset.UtcNow;

    /// <summary>Total number of requests processed</summary>
    public long TotalRequests { get; private set; }

    /// <summary>Total number of successful requests</summary>
    public long SuccessfulRequests { get; private set; }

    /// <summary>Total number of failed requests</summary>
    public long FailedRequests { get; private set; }

    /// <summary>Total number of retried requests</summary>
    public long RetriedRequests { get; private set; }

    /// <summary>Total tokens processed across all requests</summary>
    public long TotalTokensProcessed { get; private set; }

    /// <summary>Total request processing time</summary>
    public TimeSpan TotalProcessingTime { get; private set; }

    /// <summary>Average request duration (all time)</summary>
    public TimeSpan AverageRequestDuration =>
        TotalRequests > 0 ? TimeSpan.FromTicks(TotalProcessingTime.Ticks / TotalRequests) : TimeSpan.Zero;

    /// <summary>Success rate as percentage (all time)</summary>
    public double SuccessRate => TotalRequests > 0 ? SuccessfulRequests * 100.0 / TotalRequests : 0;

    /// <summary>Retry rate as percentage (all time)</summary>
    public double RetryRate => TotalRequests > 0 ? RetriedRequests * 100.0 / TotalRequests : 0;

    /// <summary>Model-specific statistics</summary>
    public IReadOnlyDictionary<string, ModelStatistics> ModelStatistics
    {
        get
        {
            lock (_lock)
            {
                return _modelStats.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
            }
        }
    }

    /// <summary>Recent metrics (limited by MaxRecentMetrics)</summary>
    public IReadOnlyList<RequestMetrics> RecentMetrics
    {
        get
        {
            lock (_lock)
            {
                return [.. _recentMetrics];
            }
        }
    }

    /// <summary>Records a request metric and updates statistics</summary>
    /// <param name="metric">Request metric to record</param>
    public void RecordMetric(RequestMetrics metric)
    {
        if (metric == null)
        {
            return;
        }

        lock (_lock)
        {
            // Update overall statistics
            TotalRequests++;

            if (metric.IsSuccess)
            {
                SuccessfulRequests++;
            }
            else
            {
                FailedRequests++;
            }

            if (metric.RetryAttempts > 0)
            {
                RetriedRequests++;
            }

            TotalTokensProcessed += metric.Usage?.TotalTokens ?? 0;
            TotalProcessingTime = TotalProcessingTime.Add(metric.Duration);

            // Update model-specific statistics
            if (!string.IsNullOrEmpty(metric.Model))
            {
                if (!_modelStats.TryGetValue(metric.Model, out var modelStats))
                {
                    modelStats = new ModelStatistics(metric.Model);
                    _modelStats[metric.Model] = modelStats;
                }

                modelStats.RecordMetric(metric);
            }

            // Maintain recent metrics list
            _recentMetrics.Add(metric);
            if (_recentMetrics.Count > MaxRecentMetrics)
            {
                _recentMetrics.RemoveAt(0);
            }
        }
    }

    /// <summary>Gets performance profile for recent requests</summary>
    /// <param name="model">Optional model filter</param>
    /// <returns>Performance profile for recent activity</returns>
    public PerformanceProfile GetRecentProfile(string model = "")
    {
        lock (_lock)
        {
            return PerformanceProfile.FromMetrics(_recentMetrics, Provider, model);
        }
    }

    /// <summary>Gets performance profile for a specific time window</summary>
    /// <param name="since">Start time for the profile</param>
    /// <param name="model">Optional model filter</param>
    /// <returns>Performance profile for the specified time window</returns>
    public PerformanceProfile GetProfileSince(DateTimeOffset since, string model = "")
    {
        lock (_lock)
        {
            var metrics = _recentMetrics.Where(m => m.StartTime >= since);
            return PerformanceProfile.FromMetrics(metrics, Provider, model);
        }
    }

    /// <summary>Resets all statistics</summary>
    public void Reset()
    {
        lock (_lock)
        {
            CollectionStartTime = DateTimeOffset.UtcNow;
            TotalRequests = 0;
            SuccessfulRequests = 0;
            FailedRequests = 0;
            RetriedRequests = 0;
            TotalTokensProcessed = 0;
            TotalProcessingTime = TimeSpan.Zero;
            _recentMetrics.Clear();
            _modelStats.Clear();
        }
    }

    /// <summary>Gets the top N models by request count</summary>
    /// <param name="count">Number of top models to return</param>
    /// <returns>Top models ordered by request count</returns>
    public IEnumerable<ModelStatistics> GetTopModelsByRequests(int count = 10)
    {
        lock (_lock)
        {
            return [.. _modelStats.Values.OrderByDescending(m => m.TotalRequests).Take(count)];
        }
    }

    /// <summary>Gets the top N models by token usage</summary>
    /// <param name="count">Number of top models to return</param>
    /// <returns>Top models ordered by token usage</returns>
    public IEnumerable<ModelStatistics> GetTopModelsByTokens(int count = 10)
    {
        lock (_lock)
        {
            return [.. _modelStats.Values.OrderByDescending(m => m.TotalTokens).Take(count)];
        }
    }
}

/// <summary>
///     Statistics for a specific model within a provider.
/// </summary>
public class ModelStatistics
{
    /// <summary>Creates a new ModelStatistics instance</summary>
    /// <param name="model">Model name</param>
    public ModelStatistics(string model)
    {
        Model = model;
    }

    /// <summary>Model name</summary>
    public string Model { get; }

    /// <summary>Total requests for this model</summary>
    public long TotalRequests { get; private set; }

    /// <summary>Successful requests for this model</summary>
    public long SuccessfulRequests { get; private set; }

    /// <summary>Failed requests for this model</summary>
    public long FailedRequests { get; private set; }

    /// <summary>Retried requests for this model</summary>
    public long RetriedRequests { get; private set; }

    /// <summary>Total tokens processed by this model</summary>
    public long TotalTokens { get; private set; }

    /// <summary>Total processing time for this model</summary>
    public TimeSpan TotalProcessingTime { get; private set; }

    /// <summary>Average request duration for this model</summary>
    public TimeSpan AverageRequestDuration =>
        TotalRequests > 0 ? TimeSpan.FromTicks(TotalProcessingTime.Ticks / TotalRequests) : TimeSpan.Zero;

    /// <summary>Success rate for this model</summary>
    public double SuccessRate => TotalRequests > 0 ? SuccessfulRequests * 100.0 / TotalRequests : 0;

    /// <summary>Average tokens per request for this model</summary>
    public double AverageTokensPerRequest => TotalRequests > 0 ? (double)TotalTokens / TotalRequests : 0;

    /// <summary>Records a metric for this model</summary>
    /// <param name="metric">Request metric to record</param>
    internal void RecordMetric(RequestMetrics metric)
    {
        TotalRequests++;

        if (metric.IsSuccess)
        {
            SuccessfulRequests++;
        }
        else
        {
            FailedRequests++;
        }

        if (metric.RetryAttempts > 0)
        {
            RetriedRequests++;
        }

        TotalTokens += metric.Usage?.TotalTokens ?? 0;
        TotalProcessingTime = TotalProcessingTime.Add(metric.Duration);
    }
}
