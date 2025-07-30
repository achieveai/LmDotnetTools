using Microsoft.Extensions.Logging;

namespace AchieveAi.LmDotnetTools.LmCore.Utils;

/// <summary>
/// Defines structured event IDs for logging across all LmDotnetTools components.
/// Event ID ranges:
/// - 1000-1999: Agent Events
/// - 2000-2999: Middleware Events  
/// - 3000-3999: Provider Events
/// - 4000-4999: Performance Events
/// </summary>
public static class LogEventIds
{
    // Agent Events (1000-1999)
    public static readonly EventId AgentRequestInitiated = new(1001, "AgentRequestInitiated");
    public static readonly EventId AgentRequestCompleted = new(1002, "AgentRequestCompleted");
    public static readonly EventId AgentRequestFailed = new(1003, "AgentRequestFailed");
    public static readonly EventId AgentCacheHit = new(1004, "AgentCacheHit");
    public static readonly EventId AgentCacheMiss = new(1005, "AgentCacheMiss");
    public static readonly EventId AgentCreated = new(1006, "AgentCreated");
    public static readonly EventId AgentDisposed = new(1007, "AgentDisposed");
    public static readonly EventId AgentDelegation = new(1008, "AgentDelegation");
    public static readonly EventId ModelResolved = new(1009, "ModelResolved");
    public static readonly EventId ModelResolutionFailed = new(1010, "ModelResolutionFailed");
    
    // Middleware Events (2000-2999)
    public static readonly EventId MiddlewareProcessing = new(2001, "MiddlewareProcessing");
    public static readonly EventId MiddlewareProcessingCompleted = new(2002, "MiddlewareProcessingCompleted");
    public static readonly EventId MiddlewareProcessingFailed = new(2003, "MiddlewareProcessingFailed");
    public static readonly EventId FunctionCallExecuted = new(2004, "FunctionCallExecuted");
    public static readonly EventId FunctionCallFailed = new(2005, "FunctionCallFailed");
    public static readonly EventId FunctionCallMappingError = new(2006, "FunctionCallMappingError");
    public static readonly EventId UsageDataEnriched = new(2007, "UsageDataEnriched");
    public static readonly EventId UsageEnrichmentFailed = new(2008, "UsageEnrichmentFailed");
    public static readonly EventId UsageEnrichmentRetry = new(2009, "UsageEnrichmentRetry");
    public static readonly EventId MessageTransformed = new(2010, "MessageTransformed");
    public static readonly EventId ToolCallProcessed = new(2011, "ToolCallProcessed");
    public static readonly EventId ToolCallAggregated = new(2012, "ToolCallAggregated");
    public static readonly EventId McpClientInitialized = new(2013, "McpClientInitialized");
    public static readonly EventId McpToolExecuted = new(2014, "McpToolExecuted");
    public static readonly EventId McpToolExecutionFailed = new(2015, "McpToolExecutionFailed");
    public static readonly EventId FunctionContractExtracted = new(2016, "FunctionContractExtracted");
    public static readonly EventId FunctionContractExtractionFailed = new(2017, "FunctionContractExtractionFailed");
    
    // Provider Events (3000-3999)
    public static readonly EventId ProviderResolved = new(3001, "ProviderResolved");
    public static readonly EventId ProviderResolutionFailed = new(3002, "ProviderResolutionFailed");
    public static readonly EventId ApiCallRetry = new(3003, "ApiCallRetry");
    public static readonly EventId ApiCallAuthentication = new(3004, "ApiCallAuthentication");
    public static readonly EventId ApiCallAuthenticationFailed = new(3005, "ApiCallAuthenticationFailed");
    public static readonly EventId ApiResponseReceived = new(3006, "ApiResponseReceived");
    public static readonly EventId ApiResponseProcessed = new(3007, "ApiResponseProcessed");
    public static readonly EventId ApiResponseProcessingFailed = new(3008, "ApiResponseProcessingFailed");
    public static readonly EventId StreamingStarted = new(3009, "StreamingStarted");
    public static readonly EventId StreamingChunkReceived = new(3010, "StreamingChunkReceived");
    public static readonly EventId StreamingCompleted = new(3011, "StreamingCompleted");
    public static readonly EventId StreamingFailed = new(3012, "StreamingFailed");
    public static readonly EventId ClientDisposed = new(3013, "ClientDisposed");
    public static readonly EventId ClientDisposalFailed = new(3014, "ClientDisposalFailed");
    
    // Performance Events (4000-4999)
    public static readonly EventId PerformanceMetrics = new(4001, "PerformanceMetrics");
    public static readonly EventId StreamingMetrics = new(4002, "StreamingMetrics");
    public static readonly EventId TokenUsageMetrics = new(4003, "TokenUsageMetrics");
    public static readonly EventId CostMetrics = new(4004, "CostMetrics");
    public static readonly EventId LatencyMetrics = new(4005, "LatencyMetrics");
    public static readonly EventId ThroughputMetrics = new(4006, "ThroughputMetrics");
    public static readonly EventId CacheMetrics = new(4007, "CacheMetrics");
    public static readonly EventId MemoryMetrics = new(4008, "MemoryMetrics");
    public static readonly EventId SerializationMetrics = new(4009, "SerializationMetrics");
    public static readonly EventId TimeToFirstToken = new(4010, "TimeToFirstToken");
    public static readonly EventId TokensPerSecond = new(4011, "TokensPerSecond");
}