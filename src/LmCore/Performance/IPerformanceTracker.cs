namespace AchieveAi.LmDotnetTools.LmCore.Performance;

/// <summary>
/// Interface for tracking performance metrics across all providers.
/// Enables consistent performance monitoring for OpenAI, Anthropic, and other providers.
/// </summary>
public interface IPerformanceTracker
{
    /// <summary>Records a request metric</summary>
    /// <param name="metric">The request metric to record</param>
    void TrackRequest(RequestMetrics metric);
    
    /// <summary>Gets statistics for a specific provider</summary>
    /// <param name="provider">Provider name (OpenAI, Anthropic, etc.)</param>
    /// <returns>Provider statistics or null if not found</returns>
    ProviderStatistics? GetProviderStatistics(string provider);
    
    /// <summary>Gets statistics for all providers</summary>
    /// <returns>Dictionary of provider statistics by provider name</returns>
    IReadOnlyDictionary<string, ProviderStatistics> GetAllProviderStatistics();
    
    /// <summary>Gets a performance profile for a specific provider</summary>
    /// <param name="provider">Provider name</param>
    /// <param name="model">Optional model filter</param>
    /// <param name="since">Optional time filter (defaults to last 24 hours)</param>
    /// <returns>Performance profile for the specified criteria</returns>
    PerformanceProfile GetPerformanceProfile(string provider, string model = "", DateTimeOffset? since = null);
    
    /// <summary>Gets performance profiles for all providers</summary>
    /// <param name="since">Optional time filter (defaults to last 24 hours)</param>
    /// <returns>Performance profiles for all providers</returns>
    IEnumerable<PerformanceProfile> GetAllPerformanceProfiles(DateTimeOffset? since = null);
    
    /// <summary>Resets statistics for a specific provider</summary>
    /// <param name="provider">Provider name to reset</param>
    void ResetProviderStatistics(string provider);
    
    /// <summary>Resets all statistics</summary>
    void ResetAllStatistics();
    
    /// <summary>Gets the top performing models across all providers</summary>
    /// <param name="count">Number of top models to return</param>
    /// <param name="orderBy">Ordering criteria (requests, tokens, success_rate, avg_duration)</param>
    /// <returns>Top performing models</returns>
    IEnumerable<(string Provider, string Model, ModelStatistics Stats)> GetTopModels(
        int count = 10, 
        string orderBy = "requests");
    
    /// <summary>Gets current overall statistics across all providers</summary>
    /// <returns>Aggregated statistics</returns>
    OverallStatistics GetOverallStatistics();
}

/// <summary>
/// Overall statistics across all providers and models.
/// </summary>
public record OverallStatistics
{
    /// <summary>Total providers being tracked</summary>
    public int TotalProviders { get; init; }
    
    /// <summary>Total unique models being tracked</summary>
    public int TotalModels { get; init; }
    
    /// <summary>Total requests across all providers</summary>
    public long TotalRequests { get; init; }
    
    /// <summary>Total successful requests across all providers</summary>
    public long SuccessfulRequests { get; init; }
    
    /// <summary>Total failed requests across all providers</summary>
    public long FailedRequests { get; init; }
    
    /// <summary>Total requests that required retries</summary>
    public long RetriedRequests { get; init; }
    
    /// <summary>Overall success rate as percentage</summary>
    public double OverallSuccessRate => 
        TotalRequests > 0 ? (SuccessfulRequests * 100.0) / TotalRequests : 0;
    
    /// <summary>Overall retry rate as percentage</summary>
    public double OverallRetryRate => 
        TotalRequests > 0 ? (RetriedRequests * 100.0) / TotalRequests : 0;
    
    /// <summary>Total tokens processed across all providers</summary>
    public long TotalTokensProcessed { get; init; }
    
    /// <summary>Total processing time across all providers</summary>
    public TimeSpan TotalProcessingTime { get; init; }
    
    /// <summary>Average request duration across all providers</summary>
    public TimeSpan AverageRequestDuration => 
        TotalRequests > 0 ? TimeSpan.FromTicks(TotalProcessingTime.Ticks / TotalRequests) : TimeSpan.Zero;
    
    /// <summary>Provider performance breakdown</summary>
    public Dictionary<string, ProviderSummary> ProviderSummaries { get; init; } = new();
}

/// <summary>
/// Summary statistics for a single provider.
/// </summary>
public record ProviderSummary
{
    /// <summary>Provider name</summary>
    public string Provider { get; init; } = string.Empty;
    
    /// <summary>Number of unique models for this provider</summary>
    public int ModelCount { get; init; }
    
    /// <summary>Total requests for this provider</summary>
    public long TotalRequests { get; init; }
    
    /// <summary>Success rate for this provider</summary>
    public double SuccessRate { get; init; }
    
    /// <summary>Average request duration for this provider</summary>
    public TimeSpan AverageRequestDuration { get; init; }
    
    /// <summary>Total tokens processed by this provider</summary>
    public long TotalTokens { get; init; }
}