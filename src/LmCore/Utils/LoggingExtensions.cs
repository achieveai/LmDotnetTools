using Microsoft.Extensions.Logging;

namespace AchieveAi.LmDotnetTools.LmCore.Utils;

/// <summary>
/// Extension methods for common logging patterns in the LmDotnetTools library.
/// </summary>
public static class LoggingExtensions
{
    #region LLM Operation Logging Extensions

    /// <summary>
    /// Logs the initiation of an LLM request with structured parameters.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    /// <param name="modelId">The model ID being used.</param>
    /// <param name="agentName">The name of the agent making the request.</param>
    /// <param name="messageCount">The number of messages in the request.</param>
    /// <param name="requestType">The type of request (streaming/non-streaming).</param>
    public static void LogLlmRequestInitiated(
        this ILogger logger,
        string modelId,
        string agentName,
        int messageCount,
        string requestType
    )
    {
        logger.LogInformation(
            LogEventIds.AgentRequestInitiated,
            "LLM request initiated: Model={Model}, Agent={AgentName}, MessageCount={MessageCount}, Type={RequestType}",
            modelId,
            agentName,
            messageCount,
            requestType
        );
    }

    /// <summary>
    /// Logs the completion of an LLM request with structured parameters.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    /// <param name="completionId">The completion ID.</param>
    /// <param name="modelId">The model ID used.</param>
    /// <param name="promptTokens">The number of prompt tokens.</param>
    /// <param name="completionTokens">The number of completion tokens.</param>
    /// <param name="totalCost">The total cost of the request.</param>
    /// <param name="durationMs">The duration of the request in milliseconds.</param>
    public static void LogLlmRequestCompleted(
        this ILogger logger,
        string? completionId,
        string modelId,
        int? promptTokens,
        int? completionTokens,
        decimal? totalCost,
        long durationMs
    )
    {
        logger.LogInformation(
            LogEventIds.AgentRequestCompleted,
            "LLM request completed: CompletionId={CompletionId}, Model={Model}, PromptTokens={PromptTokens}, CompletionTokens={CompletionTokens}, TotalCost={TotalCost:F6}, Duration={Duration}ms",
            completionId,
            modelId,
            promptTokens,
            completionTokens,
            totalCost,
            durationMs
        );
    }

    /// <summary>
    /// Logs the failure of an LLM request with structured parameters.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    /// <param name="exception">The exception that occurred.</param>
    /// <param name="modelId">The model ID being used.</param>
    /// <param name="agentType">The type of agent that failed.</param>
    /// <param name="messageCount">The number of messages in the request.</param>
    public static void LogLlmRequestFailed(
        this ILogger logger,
        Exception exception,
        string modelId,
        string agentType,
        int messageCount
    )
    {
        logger.LogError(
            LogEventIds.AgentRequestFailed,
            exception,
            "LLM request failed: Model={Model}, Agent={AgentType}, MessageCount={MessageCount}",
            modelId,
            agentType,
            messageCount
        );
    }

    /// <summary>
    /// Logs the initiation of streaming with structured parameters.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    /// <param name="modelId">The model ID being used.</param>
    /// <param name="agentName">The name of the agent.</param>
    /// <param name="messageCount">The number of messages.</param>
    public static void LogStreamingInitiated(
        this ILogger logger,
        string modelId,
        string agentName,
        int messageCount
    )
    {
        logger.LogInformation(
            LogEventIds.AgentStreamingInitiated,
            "Streaming initiated: Model={Model}, Agent={AgentName}, MessageCount={MessageCount}",
            modelId,
            agentName,
            messageCount
        );
    }

    /// <summary>
    /// Logs the completion of streaming with structured parameters.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    /// <param name="completionId">The completion ID.</param>
    /// <param name="totalChunks">The total number of chunks received.</param>
    /// <param name="durationMs">The total streaming duration in milliseconds.</param>
    public static void LogStreamingCompleted(
        this ILogger logger,
        string? completionId,
        int totalChunks,
        long durationMs
    )
    {
        logger.LogInformation(
            LogEventIds.AgentStreamingCompleted,
            "Streaming completed: CompletionId={CompletionId}, TotalChunks={TotalChunks}, Duration={Duration}ms",
            completionId,
            totalChunks,
            durationMs
        );
    }

    #endregion

    #region Function Call Logging Extensions

    /// <summary>
    /// Logs the successful execution of a function call.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    /// <param name="functionName">The name of the function executed.</param>
    /// <param name="durationMs">The execution duration in milliseconds.</param>
    /// <param name="success">Whether the function executed successfully.</param>
    public static void LogFunctionExecuted(
        this ILogger logger,
        string functionName,
        long durationMs,
        bool success = true
    )
    {
        logger.LogInformation(
            LogEventIds.FunctionCallExecuted,
            "Function executed: Name={FunctionName}, Duration={Duration}ms, Success={Success}",
            functionName,
            durationMs,
            success
        );
    }

    /// <summary>
    /// Logs the failure of a function call execution.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    /// <param name="exception">The exception that occurred.</param>
    /// <param name="functionName">The name of the function that failed.</param>
    /// <param name="arguments">The function arguments (as JSON string).</param>
    public static void LogFunctionExecutionFailed(
        this ILogger logger,
        Exception exception,
        string functionName,
        string? arguments = null
    )
    {
        logger.LogError(
            LogEventIds.FunctionCallFailed,
            exception,
            "Function execution failed: Name={FunctionName}, Args={Args}",
            functionName,
            arguments
        );
    }

    /// <summary>
    /// Logs the processing of function arguments.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    /// <param name="functionName">The name of the function.</param>
    /// <param name="argumentCount">The number of arguments processed.</param>
    /// <param name="processingTimeMs">The time taken to process arguments in milliseconds.</param>
    public static void LogFunctionArgumentsProcessed(
        this ILogger logger,
        string functionName,
        int argumentCount,
        long processingTimeMs
    )
    {
        logger.LogDebug(
            LogEventIds.FunctionArgumentsProcessed,
            "Function arguments processed: Name={FunctionName}, ArgumentCount={ArgumentCount}, ProcessingTime={ProcessingTime}ms",
            functionName,
            argumentCount,
            processingTimeMs
        );
    }

    /// <summary>
    /// Logs the transformation of function call results.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    /// <param name="functionName">The name of the function.</param>
    /// <param name="resultLength">The length of the result string.</param>
    /// <param name="transformationTimeMs">The time taken for transformation in milliseconds.</param>
    public static void LogFunctionResultTransformed(
        this ILogger logger,
        string functionName,
        int resultLength,
        long transformationTimeMs
    )
    {
        logger.LogDebug(
            LogEventIds.FunctionResultTransformed,
            "Function result transformed: Name={FunctionName}, ResultLength={ResultLength}, TransformationTime={TransformationTime}ms",
            functionName,
            resultLength,
            transformationTimeMs
        );
    }

    /// <summary>
    /// Logs MCP tool execution.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    /// <param name="toolName">The name of the MCP tool.</param>
    /// <param name="clientId">The MCP client ID.</param>
    /// <param name="durationMs">The execution duration in milliseconds.</param>
    /// <param name="success">Whether the tool executed successfully.</param>
    public static void LogMcpToolExecuted(
        this ILogger logger,
        string toolName,
        string clientId,
        long durationMs,
        bool success = true
    )
    {
        logger.LogInformation(
            LogEventIds.McpToolExecuted,
            "MCP tool executed: Tool={ToolName}, Client={ClientId}, Duration={Duration}ms, Success={Success}",
            toolName,
            clientId,
            durationMs,
            success
        );
    }

    /// <summary>
    /// Logs MCP tool execution failure.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    /// <param name="exception">The exception that occurred.</param>
    /// <param name="toolName">The name of the MCP tool.</param>
    /// <param name="clientId">The MCP client ID.</param>
    public static void LogMcpToolExecutionFailed(
        this ILogger logger,
        Exception exception,
        string toolName,
        string clientId
    )
    {
        logger.LogError(
            LogEventIds.McpToolExecutionFailed,
            exception,
            "MCP tool execution failed: Tool={ToolName}, Client={ClientId}",
            toolName,
            clientId
        );
    }

    #endregion

    #region Performance Metrics Logging Extensions

    /// <summary>
    /// Logs streaming performance metrics.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    /// <param name="completionId">The completion ID.</param>
    /// <param name="totalChunks">The total number of chunks received.</param>
    /// <param name="timeToFirstTokenMs">The time to first token in milliseconds.</param>
    /// <param name="tokensPerSecond">The tokens per second rate.</param>
    public static void LogStreamingMetrics(
        this ILogger logger,
        string? completionId,
        int totalChunks,
        long? timeToFirstTokenMs,
        double? tokensPerSecond
    )
    {
        logger.LogDebug(
            LogEventIds.StreamingMetrics,
            "Streaming metrics: CompletionId={CompletionId}, TotalChunks={TotalChunks}, TimeToFirstToken={TimeToFirstToken}ms, TokensPerSecond={TokensPerSecond:F2}",
            completionId,
            totalChunks,
            timeToFirstTokenMs,
            tokensPerSecond
        );
    }

    /// <summary>
    /// Logs token processing metrics.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    /// <param name="promptTokens">The number of prompt tokens.</param>
    /// <param name="completionTokens">The number of completion tokens.</param>
    /// <param name="totalTokens">The total number of tokens.</param>
    /// <param name="tokensPerSecond">The tokens per second rate.</param>
    public static void LogTokenMetrics(
        this ILogger logger,
        int? promptTokens,
        int? completionTokens,
        int? totalTokens,
        double? tokensPerSecond
    )
    {
        logger.LogDebug(
            LogEventIds.TokenMetrics,
            "Token metrics: PromptTokens={PromptTokens}, CompletionTokens={CompletionTokens}, TotalTokens={TotalTokens}, TokensPerSecond={TokensPerSecond:F2}",
            promptTokens,
            completionTokens,
            totalTokens,
            tokensPerSecond
        );
    }

    /// <summary>
    /// Logs operation latency metrics.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    /// <param name="operationName">The name of the operation.</param>
    /// <param name="latencyMs">The latency in milliseconds.</param>
    /// <param name="isHighLatency">Whether this is considered high latency.</param>
    public static void LogLatencyMetrics(
        this ILogger logger,
        string operationName,
        long latencyMs,
        bool isHighLatency = false
    )
    {
        var logLevel = isHighLatency ? LogLevel.Warning : LogLevel.Debug;
        logger.Log(
            logLevel,
            LogEventIds.LatencyMetrics,
            "Latency metrics: Operation={Operation}, Latency={Latency}ms, HighLatency={HighLatency}",
            operationName,
            latencyMs,
            isHighLatency
        );
    }

    /// <summary>
    /// Logs API response time metrics.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    /// <param name="provider">The API provider name.</param>
    /// <param name="endpoint">The API endpoint.</param>
    /// <param name="responseTimeMs">The response time in milliseconds.</param>
    /// <param name="statusCode">The HTTP status code.</param>
    public static void LogApiResponseTimeMetrics(
        this ILogger logger,
        string provider,
        string endpoint,
        long responseTimeMs,
        int? statusCode = null
    )
    {
        logger.LogDebug(
            LogEventIds.ApiResponseTimeMetrics,
            "API response time: Provider={Provider}, Endpoint={Endpoint}, ResponseTime={ResponseTime}ms, StatusCode={StatusCode}",
            provider,
            endpoint,
            responseTimeMs,
            statusCode
        );
    }

    /// <summary>
    /// Logs cache performance metrics.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    /// <param name="cacheType">The type of cache (e.g., "usage", "agent").</param>
    /// <param name="operation">The cache operation (e.g., "hit", "miss", "set").</param>
    /// <param name="key">The cache key.</param>
    /// <param name="durationMs">The operation duration in milliseconds.</param>
    public static void LogCacheMetrics(
        this ILogger logger,
        string cacheType,
        string operation,
        string key,
        long? durationMs = null
    )
    {
        logger.LogDebug(
            LogEventIds.CacheMetrics,
            "Cache metrics: Type={CacheType}, Operation={Operation}, Key={Key}, Duration={Duration}ms",
            cacheType,
            operation,
            key,
            durationMs
        );
    }

    /// <summary>
    /// Logs memory usage metrics.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    /// <param name="component">The component name.</param>
    /// <param name="memoryUsageBytes">The memory usage in bytes.</param>
    /// <param name="operation">The operation that triggered the measurement.</param>
    public static void LogMemoryMetrics(
        this ILogger logger,
        string component,
        long memoryUsageBytes,
        string operation
    )
    {
        logger.LogDebug(
            LogEventIds.MemoryMetrics,
            "Memory metrics: Component={Component}, MemoryUsage={MemoryUsage} bytes, Operation={Operation}",
            component,
            memoryUsageBytes,
            operation
        );
    }

    #endregion

    #region Middleware Logging Extensions

    /// <summary>
    /// Logs middleware processing initiation.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    /// <param name="middlewareName">The name of the middleware.</param>
    /// <param name="messageCount">The number of messages being processed.</param>
    public static void LogMiddlewareProcessing(
        this ILogger logger,
        string middlewareName,
        int messageCount
    )
    {
        logger.LogInformation(
            LogEventIds.MiddlewareProcessing,
            "Middleware processing: Name={MiddlewareName}, MessageCount={MessageCount}",
            middlewareName,
            messageCount
        );
    }

    /// <summary>
    /// Logs middleware processing completion.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    /// <param name="middlewareName">The name of the middleware.</param>
    /// <param name="durationMs">The processing duration in milliseconds.</param>
    /// <param name="transformedMessages">The number of transformed messages.</param>
    public static void LogMiddlewareProcessingCompleted(
        this ILogger logger,
        string middlewareName,
        long durationMs,
        int transformedMessages
    )
    {
        logger.LogInformation(
            LogEventIds.MiddlewareProcessingCompleted,
            "Middleware processing completed: Name={MiddlewareName}, Duration={Duration}ms, TransformedMessages={TransformedMessages}",
            middlewareName,
            durationMs,
            transformedMessages
        );
    }

    /// <summary>
    /// Logs middleware processing failure.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    /// <param name="exception">The exception that occurred.</param>
    /// <param name="middlewareName">The name of the middleware.</param>
    public static void LogMiddlewareProcessingFailed(
        this ILogger logger,
        Exception exception,
        string middlewareName
    )
    {
        logger.LogError(
            LogEventIds.MiddlewareProcessingFailed,
            exception,
            "Middleware processing failed: Name={MiddlewareName}",
            middlewareName
        );
    }

    /// <summary>
    /// Logs usage data enrichment with the specific format required.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    /// <param name="completionId">The completion ID.</param>
    /// <param name="model">The model name.</param>
    /// <param name="promptTokens">The number of prompt tokens.</param>
    /// <param name="completionTokens">The number of completion tokens.</param>
    /// <param name="totalCost">The total cost.</param>
    /// <param name="cached">Whether the response was cached.</param>
    public static void LogUsageDataEnriched(
        this ILogger logger,
        string completionId,
        string model,
        int promptTokens,
        int completionTokens,
        decimal totalCost,
        bool cached = false
    )
    {
        logger.LogInformation(
            LogEventIds.UsageDataEnriched,
            "Usage data enriched: {{completionId: {CompletionId}, model: {Model}, promptTokens: {PromptTokens}, completionTokens: {CompletionTokens}, totalCost: {TotalCost:F6}, cached: {Cached}}}",
            completionId,
            model,
            promptTokens,
            completionTokens,
            totalCost,
            cached
        );
    }

    #endregion

    #region Provider and Agent Logging Extensions

    /// <summary>
    /// Logs model resolution results.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    /// <param name="modelId">The requested model ID.</param>
    /// <param name="resolvedProvider">The resolved provider name.</param>
    /// <param name="resolvedModel">The resolved model name.</param>
    /// <param name="resolutionTimeMs">The resolution time in milliseconds.</param>
    public static void LogModelResolved(
        this ILogger logger,
        string modelId,
        string resolvedProvider,
        string resolvedModel,
        long resolutionTimeMs
    )
    {
        logger.LogInformation(
            LogEventIds.ModelResolved,
            "Model resolved: RequestedModel={ModelId}, Provider={Provider}, ResolvedModel={ResolvedModel}, ResolutionTime={ResolutionTime}ms",
            modelId,
            resolvedProvider,
            resolvedModel,
            resolutionTimeMs
        );
    }

    /// <summary>
    /// Logs agent delegation decisions.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    /// <param name="agentType">The type of agent being delegated to.</param>
    /// <param name="modelName">The effective model name.</param>
    /// <param name="reason">The reason for delegation.</param>
    public static void LogAgentDelegated(
        this ILogger logger,
        string agentType,
        string modelName,
        string reason
    )
    {
        logger.LogInformation(
            LogEventIds.AgentDelegated,
            "Agent delegated: AgentType={AgentType}, Model={ModelName}, Reason={Reason}",
            agentType,
            modelName,
            reason
        );
    }

    /// <summary>
    /// Logs provider resolution failures.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    /// <param name="exception">The exception that occurred.</param>
    /// <param name="modelId">The model ID that failed to resolve.</param>
    /// <param name="availableProviders">The list of available providers.</param>
    public static void LogProviderResolutionFailed(
        this ILogger logger,
        Exception exception,
        string modelId,
        string[] availableProviders
    )
    {
        logger.LogError(
            LogEventIds.ProviderResolutionFailed,
            exception,
            "Provider resolution failed: ModelId={ModelId}, AvailableProviders={AvailableProviders}",
            modelId,
            string.Join(", ", availableProviders)
        );
    }

    #endregion
}
