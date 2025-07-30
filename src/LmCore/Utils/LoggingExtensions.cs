using Microsoft.Extensions.Logging;

namespace AchieveAi.LmDotnetTools.LmCore.Utils;

/// <summary>
/// Extension methods for common logging patterns in LmDotnetTools.
/// </summary>
public static class LoggingExtensions
{
    /// <summary>
    /// Logs LLM request initiation with structured parameters.
    /// </summary>
    public static void LogLlmRequestInitiated(this ILogger logger, string modelId, string agentName, int messageCount, string requestType)
    {
        logger.LogInformation(LogEventIds.AgentRequestInitiated,
            "LLM request initiated: Model={Model}, Agent={AgentName}, MessageCount={MessageCount}, Type={RequestType}",
            modelId, agentName, messageCount, requestType);
    }

    /// <summary>
    /// Logs LLM request completion with performance metrics.
    /// </summary>
    public static void LogLlmRequestCompleted(this ILogger logger, string? completionId, string modelId, 
        int promptTokens, int completionTokens, decimal totalCost, long durationMs)
    {
        logger.LogInformation(LogEventIds.AgentRequestCompleted,
            "LLM request completed: CompletionId={CompletionId}, Model={Model}, PromptTokens={PromptTokens}, CompletionTokens={CompletionTokens}, TotalCost={TotalCost:F6}, Duration={Duration}ms",
            completionId, modelId, promptTokens, completionTokens, totalCost, durationMs);
    }

    /// <summary>
    /// Logs LLM request failure with exception details.
    /// </summary>
    public static void LogLlmRequestFailed(this ILogger logger, Exception exception, string modelId, string agentType, int messageCount)
    {
        logger.LogError(LogEventIds.AgentRequestFailed, exception,
            "LLM request failed: Model={Model}, Agent={AgentType}, MessageCount={MessageCount}",
            modelId, agentType, messageCount);
    }

    /// <summary>
    /// Logs function call execution with timing and success status.
    /// </summary>
    public static void LogFunctionExecuted(this ILogger logger, string functionName, long durationMs, bool success)
    {
        logger.LogInformation(LogEventIds.FunctionCallExecuted,
            "Function executed: Name={FunctionName}, Duration={Duration}ms, Success={Success}",
            functionName, durationMs, success);
    }

    /// <summary>
    /// Logs function call failure with exception and arguments.
    /// </summary>
    public static void LogFunctionFailed(this ILogger logger, Exception exception, string functionName, string? argsJson = null)
    {
        logger.LogError(LogEventIds.FunctionCallFailed, exception,
            "Function execution failed: Name={FunctionName}, Args={Args}",
            functionName, argsJson ?? "null");
    }

    /// <summary>
    /// Logs usage data enrichment with structured format (matching existing OpenRouterUsageMiddleware format).
    /// </summary>
    public static void LogUsageDataEnriched(this ILogger logger, string completionId, string model, 
        int promptTokens, int completionTokens, decimal totalCost, bool cached)
    {
        logger.LogInformation(LogEventIds.UsageDataEnriched,
            "Usage data enriched: {{completionId: {CompletionId}, model: {Model}, promptTokens: {PromptTokens}, completionTokens: {CompletionTokens}, totalCost: {TotalCost:F6}, cached: {Cached}}}",
            completionId, model, promptTokens, completionTokens, totalCost, cached);
    }

    /// <summary>
    /// Logs usage enrichment failure with retry information.
    /// </summary>
    public static void LogUsageEnrichmentFailed(this ILogger logger, Exception exception, string completionId, int attempt, int maxAttempts)
    {
        if (attempt < maxAttempts)
        {
            logger.LogWarning(LogEventIds.UsageEnrichmentRetry,
                "Usage enrichment retry: CompletionId={CompletionId}, Attempt={Attempt}/{MaxAttempts}, Error={Error}",
                completionId, attempt + 1, maxAttempts, exception.Message);
        }
        else
        {
            logger.LogError(LogEventIds.UsageEnrichmentFailed, exception,
                "Usage enrichment failed after all retries: CompletionId={CompletionId}",
                completionId);
        }
    }

    /// <summary>
    /// Logs streaming metrics with performance data.
    /// </summary>
    public static void LogStreamingMetrics(this ILogger logger, string? completionId, int totalChunks, 
        long timeToFirstTokenMs, double tokensPerSecond)
    {
        logger.LogDebug(LogEventIds.StreamingMetrics,
            "Streaming metrics: CompletionId={CompletionId}, TotalChunks={TotalChunks}, TimeToFirstToken={TimeToFirstToken}ms, TokensPerSecond={TokensPerSecond:F2}",
            completionId, totalChunks, timeToFirstTokenMs, tokensPerSecond);
    }

    /// <summary>
    /// Logs provider resolution with selection details.
    /// </summary>
    public static void LogProviderResolved(this ILogger logger, string providerName, string modelId, string agentType)
    {
        logger.LogDebug(LogEventIds.ProviderResolved,
            "Provider resolved: Provider={Provider}, Model={Model}, AgentType={AgentType}",
            providerName, modelId, agentType);
    }

    /// <summary>
    /// Logs provider resolution failure.
    /// </summary>
    public static void LogProviderResolutionFailed(this ILogger logger, Exception exception, string modelId)
    {
        logger.LogError(LogEventIds.ProviderResolutionFailed, exception,
            "Provider resolution failed: Model={Model}",
            modelId);
    }

    /// <summary>
    /// Logs agent cache hit/miss with cache key.
    /// </summary>
    public static void LogAgentCacheResult(this ILogger logger, string cacheKey, bool hit, string agentType)
    {
        var eventId = hit ? LogEventIds.AgentCacheHit : LogEventIds.AgentCacheMiss;
        var status = hit ? "hit" : "miss";
        
        logger.LogDebug(eventId,
            "Agent cache {Status}: Key={CacheKey}, AgentType={AgentType}",
            status, cacheKey, agentType);
    }

    /// <summary>
    /// Logs middleware processing with context information.
    /// </summary>
    public static void LogMiddlewareProcessing(this ILogger logger, string middlewareName, string operationType, long durationMs)
    {
        logger.LogInformation(LogEventIds.MiddlewareProcessing,
            "Middleware processing: Name={MiddlewareName}, Operation={OperationType}, Duration={Duration}ms",
            middlewareName, operationType, durationMs);
    }

    /// <summary>
    /// Logs middleware processing failure.
    /// </summary>
    public static void LogMiddlewareProcessingFailed(this ILogger logger, Exception exception, string middlewareName, string operationType)
    {
        logger.LogError(LogEventIds.MiddlewareProcessingFailed, exception,
            "Middleware processing failed: Name={MiddlewareName}, Operation={OperationType}",
            middlewareName, operationType);
    }

    /// <summary>
    /// Logs MCP tool execution with client and result information.
    /// </summary>
    public static void LogMcpToolExecuted(this ILogger logger, string toolName, string clientId, bool success, long durationMs)
    {
        logger.LogInformation(LogEventIds.McpToolExecuted,
            "MCP tool executed: Tool={ToolName}, Client={ClientId}, Success={Success}, Duration={Duration}ms",
            toolName, clientId, success, durationMs);
    }

    /// <summary>
    /// Logs MCP tool execution failure.
    /// </summary>
    public static void LogMcpToolExecutionFailed(this ILogger logger, Exception exception, string toolName, string clientId)
    {
        logger.LogError(LogEventIds.McpToolExecutionFailed, exception,
            "MCP tool execution failed: Tool={ToolName}, Client={ClientId}",
            toolName, clientId);
    }

    /// <summary>
    /// Logs API call retry attempts.
    /// </summary>
    public static void LogApiCallRetry(this ILogger logger, int attempt, int maxAttempts, string? completionId, string error)
    {
        logger.LogWarning(LogEventIds.ApiCallRetry,
            "API call retry: Attempt={Attempt}/{MaxAttempts}, CompletionId={CompletionId}, Error={Error}",
            attempt, maxAttempts, completionId, error);
    }

    /// <summary>
    /// Logs performance metrics with operation details.
    /// </summary>
    public static void LogPerformanceMetrics(this ILogger logger, string operation, long durationMs, 
        int? tokenCount = null, decimal? cost = null)
    {
        logger.LogDebug(LogEventIds.PerformanceMetrics,
            "Performance metrics: Operation={Operation}, Duration={Duration}ms, Tokens={Tokens}, Cost={Cost:F6}",
            operation, durationMs, tokenCount, cost);
    }

    /// <summary>
    /// Logs message transformation details.
    /// </summary>
    public static void LogMessageTransformed(this ILogger logger, string fromType, string toType, int messageCount)
    {
        logger.LogDebug(LogEventIds.MessageTransformed,
            "Message transformed: From={FromType}, To={ToType}, Count={MessageCount}",
            fromType, toType, messageCount);
    }

    /// <summary>
    /// Logs tool call processing with aggregation details.
    /// </summary>
    public static void LogToolCallProcessed(this ILogger logger, string toolCallId, string toolName, bool success)
    {
        logger.LogDebug(LogEventIds.ToolCallProcessed,
            "Tool call processed: Id={ToolCallId}, Tool={ToolName}, Success={Success}",
            toolCallId, toolName, success);
    }
}