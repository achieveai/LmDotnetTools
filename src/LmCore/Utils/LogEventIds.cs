using Microsoft.Extensions.Logging;

namespace AchieveAi.LmDotnetTools.LmCore.Utils;

/// <summary>
///     Defines structured event IDs for logging across the LmDotnetTools library.
///     Event ID ranges:
///     - Agent Events: 1000-1999
///     - Middleware Events: 2000-2999
///     - Provider Events: 3000-3999
///     - Performance Events: 4000-4999
/// </summary>
public static class LogEventIds
{
    #region Agent Events (1000-1999)

    /// <summary>
    ///     Logged when an LLM request is initiated by an agent.
    /// </summary>
    public static readonly EventId AgentRequestInitiated = new(1001, "AgentRequestInitiated");

    /// <summary>
    ///     Logged when an LLM request is completed successfully by an agent.
    /// </summary>
    public static readonly EventId AgentRequestCompleted = new(1002, "AgentRequestCompleted");

    /// <summary>
    ///     Logged when an LLM request fails in an agent.
    /// </summary>
    public static readonly EventId AgentRequestFailed = new(1003, "AgentRequestFailed");

    /// <summary>
    ///     Logged when an agent cache hit occurs.
    /// </summary>
    public static readonly EventId AgentCacheHit = new(1004, "AgentCacheHit");

    /// <summary>
    ///     Logged when an agent cache miss occurs.
    /// </summary>
    public static readonly EventId AgentCacheMiss = new(1005, "AgentCacheMiss");

    /// <summary>
    ///     Logged when an agent is created or resolved.
    /// </summary>
    public static readonly EventId AgentCreated = new(1006, "AgentCreated");

    /// <summary>
    ///     Logged when agent creation fails.
    /// </summary>
    public static readonly EventId AgentCreationFailed = new(1007, "AgentCreationFailed");

    /// <summary>
    ///     Logged when an agent is disposed.
    /// </summary>
    public static readonly EventId AgentDisposed = new(1008, "AgentDisposed");

    /// <summary>
    ///     Logged when streaming is initiated by an agent.
    /// </summary>
    public static readonly EventId AgentStreamingInitiated = new(1009, "AgentStreamingInitiated");

    /// <summary>
    ///     Logged when streaming is completed by an agent.
    /// </summary>
    public static readonly EventId AgentStreamingCompleted = new(1010, "AgentStreamingCompleted");

    /// <summary>
    ///     Logged when model resolution occurs in UnifiedAgent.
    /// </summary>
    public static readonly EventId ModelResolved = new(1011, "ModelResolved");

    /// <summary>
    ///     Logged when model resolution fails in UnifiedAgent.
    /// </summary>
    public static readonly EventId ModelResolutionFailed = new(1012, "ModelResolutionFailed");

    /// <summary>
    ///     Logged when agent delegation occurs in UnifiedAgent.
    /// </summary>
    public static readonly EventId AgentDelegated = new(1013, "AgentDelegated");

    #endregion

    #region Middleware Events (2000-2999)

    /// <summary>
    ///     Logged when middleware begins processing a request.
    /// </summary>
    public static readonly EventId MiddlewareProcessing = new(2001, "MiddlewareProcessing");

    /// <summary>
    ///     Logged when middleware completes processing a request.
    /// </summary>
    public static readonly EventId MiddlewareProcessingCompleted = new(2002, "MiddlewareProcessingCompleted");

    /// <summary>
    ///     Logged when middleware processing fails.
    /// </summary>
    public static readonly EventId MiddlewareProcessingFailed = new(2003, "MiddlewareProcessingFailed");

    /// <summary>
    ///     Logged when a function call is executed successfully.
    /// </summary>
    public static readonly EventId FunctionCallExecuted = new(2004, "FunctionCallExecuted");

    /// <summary>
    ///     Logged when a function call execution fails.
    /// </summary>
    public static readonly EventId FunctionCallFailed = new(2005, "FunctionCallFailed");

    /// <summary>
    ///     Logged when function call arguments are processed.
    /// </summary>
    public static readonly EventId FunctionArgumentsProcessed = new(2006, "FunctionArgumentsProcessed");

    /// <summary>
    ///     Logged when function call results are transformed.
    /// </summary>
    public static readonly EventId FunctionResultTransformed = new(2007, "FunctionResultTransformed");

    /// <summary>
    ///     Logged when usage data is successfully enriched.
    /// </summary>
    public static readonly EventId UsageDataEnriched = new(2008, "UsageDataEnriched");

    /// <summary>
    ///     Logged when usage data enrichment fails.
    /// </summary>
    public static readonly EventId UsageEnrichmentFailed = new(2009, "UsageEnrichmentFailed");

    /// <summary>
    ///     Logged when usage data cache hit occurs.
    /// </summary>
    public static readonly EventId UsageCacheHit = new(2010, "UsageCacheHit");

    /// <summary>
    ///     Logged when usage data cache miss occurs.
    /// </summary>
    public static readonly EventId UsageCacheMiss = new(2011, "UsageCacheMiss");

    /// <summary>
    ///     Logged when MCP tool execution occurs.
    /// </summary>
    public static readonly EventId McpToolExecuted = new(2012, "McpToolExecuted");

    /// <summary>
    ///     Logged when MCP tool execution fails.
    /// </summary>
    public static readonly EventId McpToolExecutionFailed = new(2013, "McpToolExecutionFailed");

    /// <summary>
    ///     Logged when MCP client initialization occurs.
    /// </summary>
    public static readonly EventId McpClientInitialized = new(2014, "McpClientInitialized");

    /// <summary>
    ///     Logged when message transformation occurs in middleware.
    /// </summary>
    public static readonly EventId MessageTransformed = new(2015, "MessageTransformed");

    /// <summary>
    ///     Logged when tool call aggregation occurs.
    /// </summary>
    public static readonly EventId ToolCallAggregated = new(2016, "ToolCallAggregated");

    #endregion

    #region Provider Events (3000-3999)

    /// <summary>
    ///     Logged when a provider is successfully resolved.
    /// </summary>
    public static readonly EventId ProviderResolved = new(3001, "ProviderResolved");

    /// <summary>
    ///     Logged when provider resolution fails.
    /// </summary>
    public static readonly EventId ProviderResolutionFailed = new(3002, "ProviderResolutionFailed");

    /// <summary>
    ///     Logged when an API call retry is attempted.
    /// </summary>
    public static readonly EventId ApiCallRetry = new(3003, "ApiCallRetry");

    /// <summary>
    ///     Logged when API authentication occurs.
    /// </summary>
    public static readonly EventId ApiAuthentication = new(3004, "ApiAuthentication");

    /// <summary>
    ///     Logged when API authentication fails.
    /// </summary>
    public static readonly EventId ApiAuthenticationFailed = new(3005, "ApiAuthenticationFailed");

    /// <summary>
    ///     Logged when API rate limiting is encountered.
    /// </summary>
    public static readonly EventId ApiRateLimited = new(3006, "ApiRateLimited");

    /// <summary>
    ///     Logged when API quota is exceeded.
    /// </summary>
    public static readonly EventId ApiQuotaExceeded = new(3007, "ApiQuotaExceeded");

    /// <summary>
    ///     Logged when provider configuration is loaded.
    /// </summary>
    public static readonly EventId ProviderConfigurationLoaded = new(3008, "ProviderConfigurationLoaded");

    /// <summary>
    ///     Logged when provider selection criteria are evaluated.
    /// </summary>
    public static readonly EventId ProviderSelectionCriteria = new(3009, "ProviderSelectionCriteria");

    /// <summary>
    ///     Logged when fallback provider is used.
    /// </summary>
    public static readonly EventId FallbackProviderUsed = new(3010, "FallbackProviderUsed");

    #endregion

    #region Performance Events (4000-4999)

    /// <summary>
    ///     Logged when general performance metrics are recorded.
    /// </summary>
    public static readonly EventId PerformanceMetrics = new(4001, "PerformanceMetrics");

    /// <summary>
    ///     Logged when streaming performance metrics are recorded.
    /// </summary>
    public static readonly EventId StreamingMetrics = new(4002, "StreamingMetrics");

    /// <summary>
    ///     Logged when token processing metrics are recorded.
    /// </summary>
    public static readonly EventId TokenMetrics = new(4003, "TokenMetrics");

    /// <summary>
    ///     Logged when latency metrics are recorded.
    /// </summary>
    public static readonly EventId LatencyMetrics = new(4004, "LatencyMetrics");

    /// <summary>
    ///     Logged when throughput metrics are recorded.
    /// </summary>
    public static readonly EventId ThroughputMetrics = new(4005, "ThroughputMetrics");

    /// <summary>
    ///     Logged when memory usage metrics are recorded.
    /// </summary>
    public static readonly EventId MemoryMetrics = new(4006, "MemoryMetrics");

    /// <summary>
    ///     Logged when cache performance metrics are recorded.
    /// </summary>
    public static readonly EventId CacheMetrics = new(4007, "CacheMetrics");

    /// <summary>
    ///     Logged when API response time metrics are recorded.
    /// </summary>
    public static readonly EventId ApiResponseTimeMetrics = new(4008, "ApiResponseTimeMetrics");

    /// <summary>
    ///     Logged when time to first token metrics are recorded.
    /// </summary>
    public static readonly EventId TimeToFirstTokenMetrics = new(4009, "TimeToFirstTokenMetrics");

    /// <summary>
    ///     Logged when tokens per second metrics are recorded.
    /// </summary>
    public static readonly EventId TokensPerSecondMetrics = new(4010, "TokensPerSecondMetrics");

    /// <summary>
    ///     Logged when operation duration metrics are recorded.
    /// </summary>
    public static readonly EventId OperationDurationMetrics = new(4011, "OperationDurationMetrics");

    /// <summary>
    ///     Logged when serialization performance metrics are recorded.
    /// </summary>
    public static readonly EventId SerializationMetrics = new(4012, "SerializationMetrics");

    /// <summary>
    ///     Logged when tokens per second metrics are recorded (legacy compatibility).
    /// </summary>
    public static readonly EventId TokensPerSecond = new(4013, "TokensPerSecond");

    /// <summary>
    ///     Logged when time to first token metrics are recorded (legacy compatibility).
    /// </summary>
    public static readonly EventId TimeToFirstToken = new(4014, "TimeToFirstToken");

    #endregion
}
