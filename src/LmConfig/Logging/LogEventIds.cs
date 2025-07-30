using Microsoft.Extensions.Logging;

namespace AchieveAi.LmDotnetTools.LmConfig.Logging;

/// <summary>
/// Defines structured event IDs for logging throughout the LmConfig library.
/// </summary>
public static class LogEventIds
{
    // Agent Events (1000-1999)
    public static readonly EventId AgentRequestInitiated = new(1001, "AgentRequestInitiated");
    public static readonly EventId AgentRequestCompleted = new(1002, "AgentRequestCompleted");
    public static readonly EventId AgentRequestFailed = new(1003, "AgentRequestFailed");
    public static readonly EventId AgentCacheHit = new(1004, "AgentCacheHit");
    public static readonly EventId AgentCacheMiss = new(1005, "AgentCacheMiss");
    public static readonly EventId AgentDelegation = new(1006, "AgentDelegation");
    
    // Provider Resolution Events (3000-3999)
    public static readonly EventId ProviderResolved = new(3001, "ProviderResolved");
    public static readonly EventId ProviderResolutionFailed = new(3002, "ProviderResolutionFailed");
    public static readonly EventId ProviderSelectionCriteria = new(3003, "ProviderSelectionCriteria");
    public static readonly EventId AvailableProvidersEvaluated = new(3004, "AvailableProvidersEvaluated");
}