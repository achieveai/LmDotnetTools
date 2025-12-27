using AchieveAi.LmDotnetTools.LmCore.Messages;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AchieveAi.LmDotnetTools.LmCore.Middleware;

/// <summary>
///     Stateless executor for tool calls. Takes a ToolCallMessage and executes the tools,
///     returning a ToolsCallResultMessage. Designed for explicit tool execution in application code
///     for the new simplified message flow where applications control the agentic loop.
/// </summary>
public class ToolCallExecutor
{
    /// <summary>
    ///     Executes all tool calls in the provided message using the given function map
    /// </summary>
    /// <param name="toolCallMessage">The message containing tool calls to execute</param>
    /// <param name="functionMap">Map of function names to their implementations</param>
    /// <param name="resultCallback">Optional callback for tool execution events</param>
    /// <param name="logger">Optional logger</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>A ToolsCallResultMessage containing all execution results</returns>
    public static async Task<ToolsCallResultMessage> ExecuteAsync(
        ToolsCallMessage toolCallMessage,
        IDictionary<string, Func<string, Task<string>>> functionMap,
        IToolResultCallback? resultCallback = null,
        ILogger? logger = null,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(toolCallMessage);

        ArgumentNullException.ThrowIfNull(functionMap);

        var effectiveLogger = logger ?? NullLogger.Instance;
        var toolCalls = toolCallMessage.ToolCalls;
        var toolCallResults = new List<ToolCallResult>();
        var toolCallCount = toolCalls.Count;
        var startTime = DateTime.UtcNow;

        effectiveLogger.LogInformation("Tool call execution started: ToolCallCount={ToolCallCount}", toolCallCount);

        foreach (var toolCall in toolCalls)
        {
            try
            {
                var result = await ExecuteToolCallAsync(
                    toolCall,
                    functionMap,
                    resultCallback,
                    effectiveLogger,
                    cancellationToken
                );
                toolCallResults.Add(result);
            }
            catch (Exception ex)
            {
                effectiveLogger.LogError(
                    ex,
                    "Tool call execution error: ToolCallId={ToolCallId}, FunctionName={FunctionName}",
                    toolCall.ToolCallId,
                    toolCall.FunctionName
                );

                // Add an error result for this tool call
                toolCallResults.Add(
                    new ToolCallResult(toolCall.ToolCallId, $"Tool call execution error: {ex.Message}")
                );
            }
        }

        var duration = (DateTime.UtcNow - startTime).TotalMilliseconds;
        var successCount = toolCallResults.Count(r =>
            !r.Result.StartsWith("Error executing function:")
            && !r.Result.Contains("is not available")
            && !r.Result.StartsWith("Tool call execution error:")
        );

        effectiveLogger.LogInformation(
            "Tool call execution completed: ToolCallCount={ToolCallCount}, SuccessCount={SuccessCount}, Duration={Duration}ms",
            toolCallCount,
            successCount,
            duration
        );

        // Return a ToolsCallResultMessage with all results
        // Preserve GenerationId from the original tool call message
        return new ToolsCallResultMessage
        {
            ToolCallResults = [.. toolCallResults],
            Role = Role.Tool,
            FromAgent = string.Empty,
            GenerationId = toolCallMessage.GenerationId,
            ThreadId = toolCallMessage.ThreadId,
            RunId = toolCallMessage.RunId,
        };
    }

    /// <summary>
    ///     Executes all tool calls using a multi-modal function map that returns ToolCallResult directly.
    ///     This overload supports functions that return both text and image content blocks.
    /// </summary>
    /// <param name="toolCallMessage">The message containing tool calls to execute</param>
    /// <param name="multiModalFunctionMap">Map of function names to multi-modal implementations</param>
    /// <param name="resultCallback">Optional callback for tool execution events</param>
    /// <param name="logger">Optional logger</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>A ToolsCallResultMessage containing all execution results with content blocks</returns>
    public static async Task<ToolsCallResultMessage> ExecuteMultiModalAsync(
        ToolsCallMessage toolCallMessage,
        IDictionary<string, Func<string, Task<ToolCallResult>>> multiModalFunctionMap,
        IToolResultCallback? resultCallback = null,
        ILogger? logger = null,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(toolCallMessage);
        ArgumentNullException.ThrowIfNull(multiModalFunctionMap);

        var effectiveLogger = logger ?? NullLogger.Instance;
        var toolCalls = toolCallMessage.ToolCalls;
        var toolCallResults = new List<ToolCallResult>();
        var toolCallCount = toolCalls.Count;
        var startTime = DateTime.UtcNow;

        effectiveLogger.LogInformation(
            "Multi-modal tool call execution started: ToolCallCount={ToolCallCount}",
            toolCallCount
        );

        foreach (var toolCall in toolCalls)
        {
            try
            {
                var result = await ExecuteMultiModalToolCallAsync(
                    toolCall,
                    multiModalFunctionMap,
                    resultCallback,
                    effectiveLogger,
                    cancellationToken
                );
                toolCallResults.Add(result);
            }
            catch (Exception ex)
            {
                effectiveLogger.LogError(
                    ex,
                    "Multi-modal tool call execution error: ToolCallId={ToolCallId}, FunctionName={FunctionName}",
                    toolCall.ToolCallId,
                    toolCall.FunctionName
                );

                toolCallResults.Add(
                    new ToolCallResult(toolCall.ToolCallId, $"Tool call execution error: {ex.Message}")
                );
            }
        }

        var duration = (DateTime.UtcNow - startTime).TotalMilliseconds;
        var successCount = toolCallResults.Count(r =>
            !r.Result.StartsWith("Error executing function:")
            && !r.Result.Contains("is not available")
            && !r.Result.StartsWith("Tool call execution error:")
        );

        effectiveLogger.LogInformation(
            "Multi-modal tool call execution completed: ToolCallCount={ToolCallCount}, SuccessCount={SuccessCount}, Duration={Duration}ms",
            toolCallCount,
            successCount,
            duration
        );

        return new ToolsCallResultMessage
        {
            ToolCallResults = [.. toolCallResults],
            Role = Role.Tool,
            FromAgent = string.Empty,
            GenerationId = toolCallMessage.GenerationId,
            ThreadId = toolCallMessage.ThreadId,
            RunId = toolCallMessage.RunId,
        };
    }

    /// <summary>
    ///     Executes a single tool call and returns the result
    /// </summary>
    private static async Task<ToolCallResult> ExecuteToolCallAsync(
        ToolCall toolCall,
        IDictionary<string, Func<string, Task<string>>> functionMap,
        IToolResultCallback? resultCallback,
        ILogger logger,
        CancellationToken cancellationToken
    )
    {
        var functionName = toolCall.FunctionName!;
        var functionArgs = toolCall.FunctionArgs!;
        var startTime = DateTime.UtcNow;

        // Notify callback that tool call is starting
        if (resultCallback != null && !string.IsNullOrEmpty(toolCall.ToolCallId))
        {
            await resultCallback.OnToolCallStartedAsync(
                toolCall.ToolCallId,
                functionName,
                functionArgs,
                cancellationToken
            );
        }

        if (functionMap.TryGetValue(functionName, out var func))
        {
            try
            {
                var result = await func(functionArgs);
                var duration = (DateTime.UtcNow - startTime).TotalMilliseconds;

                logger.LogInformation(
                    "Function executed: Name={FunctionName}, Duration={Duration}ms, Success={Success}",
                    functionName,
                    duration,
                    true
                );

                var toolCallResult = new ToolCallResult(toolCall.ToolCallId, result);

                // Notify callback that result is available
                if (resultCallback != null && !string.IsNullOrEmpty(toolCall.ToolCallId))
                {
                    await resultCallback.OnToolResultAvailableAsync(
                        toolCall.ToolCallId,
                        toolCallResult,
                        cancellationToken
                    );
                }

                return toolCallResult;
            }
            catch (Exception ex)
            {
                var duration = (DateTime.UtcNow - startTime).TotalMilliseconds;

                logger.LogError(
                    ex,
                    "Function execution failed: Name={FunctionName}, Args={Args}, Duration={Duration}ms, ToolCallId={ToolCallId}",
                    functionName,
                    functionArgs,
                    duration,
                    toolCall.ToolCallId
                );

                logger.LogInformation(
                    "Function executed: Name={FunctionName}, Duration={Duration}ms, Success={Success}",
                    functionName,
                    duration,
                    false
                );

                var errorMessage = $"Error executing function: {ex.Message}";

                // Notify callback about the error
                if (resultCallback != null && !string.IsNullOrEmpty(toolCall.ToolCallId))
                {
                    await resultCallback.OnToolCallErrorAsync(
                        toolCall.ToolCallId,
                        functionName,
                        errorMessage,
                        cancellationToken
                    );
                }

                // Handle exceptions during function execution
                var errorResult = new ToolCallResult(toolCall.ToolCallId, errorMessage);

                // Still notify with result (containing error)
                if (resultCallback != null && !string.IsNullOrEmpty(toolCall.ToolCallId))
                {
                    await resultCallback.OnToolResultAvailableAsync(
                        toolCall.ToolCallId,
                        errorResult,
                        cancellationToken
                    );
                }

                return errorResult;
            }
        }

        {
            // Return error for unavailable function
            var availableFunctions = string.Join(", ", functionMap.Keys);
            var errorMessage = $"Function '{functionName}' is not available. Available functions: {availableFunctions}";

            logger.LogError(
                "Function mapping error: Unavailable function '{FunctionName}' requested, ToolCallId={ToolCallId}, AvailableFunctions=[{AvailableFunctions}]",
                functionName,
                toolCall.ToolCallId,
                availableFunctions
            );

            logger.LogInformation(
                "Function executed: Name={FunctionName}, Duration={Duration}ms, Success={Success}",
                functionName,
                0,
                false
            );

            // Notify callback about the error
            if (resultCallback != null && !string.IsNullOrEmpty(toolCall.ToolCallId))
            {
                await resultCallback.OnToolCallErrorAsync(
                    toolCall.ToolCallId,
                    functionName,
                    errorMessage,
                    cancellationToken
                );
            }

            var errorResult = new ToolCallResult(toolCall.ToolCallId, errorMessage);

            // Still notify with result (containing error)
            if (resultCallback != null && !string.IsNullOrEmpty(toolCall.ToolCallId))
            {
                await resultCallback.OnToolResultAvailableAsync(toolCall.ToolCallId, errorResult, cancellationToken);
            }

            return errorResult;
        }
    }

    /// <summary>
    ///     Executes a single tool call using a multi-modal function map
    /// </summary>
    private static async Task<ToolCallResult> ExecuteMultiModalToolCallAsync(
        ToolCall toolCall,
        IDictionary<string, Func<string, Task<ToolCallResult>>> multiModalFunctionMap,
        IToolResultCallback? resultCallback,
        ILogger logger,
        CancellationToken cancellationToken
    )
    {
        var functionName = toolCall.FunctionName!;
        var functionArgs = toolCall.FunctionArgs!;
        var startTime = DateTime.UtcNow;

        logger.LogTrace(
            "ExecuteMultiModalToolCallAsync entry: FunctionName={FunctionName}, ToolCallId={ToolCallId}, ArgsLength={ArgsLength}",
            functionName,
            toolCall.ToolCallId,
            functionArgs.Length);

        // Notify callback that tool call is starting
        if (resultCallback != null && !string.IsNullOrEmpty(toolCall.ToolCallId))
        {
            await resultCallback.OnToolCallStartedAsync(
                toolCall.ToolCallId,
                functionName,
                functionArgs,
                cancellationToken
            );
        }

        if (multiModalFunctionMap.TryGetValue(functionName, out var func))
        {
            try
            {
                // Function returns ToolCallResult directly with content blocks
                var result = await func(functionArgs);
                var duration = (DateTime.UtcNow - startTime).TotalMilliseconds;

                // Count content block types for detailed logging
                var imageBlockCount = result.ContentBlocks?.OfType<ImageToolResultBlock>().Count() ?? 0;
                var textBlockCount = result.ContentBlocks?.OfType<TextToolResultBlock>().Count() ?? 0;
                var totalBlockCount = result.ContentBlocks?.Count ?? 0;

                logger.LogInformation(
                    "Multi-modal function executed: Name={FunctionName}, Duration={Duration}ms, Success={Success}, ContentBlocks={ContentBlockCount} (Images={ImageBlocks}, Text={TextBlocks}), ResultLength={ResultLength}",
                    functionName,
                    duration,
                    true,
                    totalBlockCount,
                    imageBlockCount,
                    textBlockCount,
                    result.Result?.Length ?? 0
                );

                if (imageBlockCount > 0)
                {
                    // Log image details at debug level
                    var imageDetails = result.ContentBlocks?
                        .OfType<ImageToolResultBlock>()
                        .Select((img, idx) => $"[{idx}:{img.MimeType}:{img.Data?.Length ?? 0}b]");
                    logger.LogDebug(
                        "Multi-modal result image details: FunctionName={FunctionName}, Images={ImageDetails}",
                        functionName,
                        string.Join(", ", imageDetails ?? []));
                }

                // Ensure tool call ID is set
                var toolCallResult = result with { ToolCallId = toolCall.ToolCallId };

                // Notify callback that result is available
                if (resultCallback != null && !string.IsNullOrEmpty(toolCall.ToolCallId))
                {
                    await resultCallback.OnToolResultAvailableAsync(
                        toolCall.ToolCallId,
                        toolCallResult,
                        cancellationToken
                    );
                }

                return toolCallResult;
            }
            catch (Exception ex)
            {
                var duration = (DateTime.UtcNow - startTime).TotalMilliseconds;

                logger.LogError(
                    ex,
                    "Multi-modal function execution failed: Name={FunctionName}, Args={Args}, Duration={Duration}ms, ToolCallId={ToolCallId}",
                    functionName,
                    functionArgs,
                    duration,
                    toolCall.ToolCallId
                );

                var errorMessage = $"Error executing function: {ex.Message}";

                // Notify callback about the error
                if (resultCallback != null && !string.IsNullOrEmpty(toolCall.ToolCallId))
                {
                    await resultCallback.OnToolCallErrorAsync(
                        toolCall.ToolCallId,
                        functionName,
                        errorMessage,
                        cancellationToken
                    );
                }

                var errorResult = new ToolCallResult(toolCall.ToolCallId, errorMessage);

                if (resultCallback != null && !string.IsNullOrEmpty(toolCall.ToolCallId))
                {
                    await resultCallback.OnToolResultAvailableAsync(
                        toolCall.ToolCallId,
                        errorResult,
                        cancellationToken
                    );
                }

                return errorResult;
            }
        }

        // Return error for unavailable function
        var availableFunctions = string.Join(", ", multiModalFunctionMap.Keys);
        var unavailableErrorMessage = $"Function '{functionName}' is not available. Available functions: {availableFunctions}";

        logger.LogError(
            "Multi-modal function mapping error: Unavailable function '{FunctionName}' requested, ToolCallId={ToolCallId}",
            functionName,
            toolCall.ToolCallId
        );

        if (resultCallback != null && !string.IsNullOrEmpty(toolCall.ToolCallId))
        {
            await resultCallback.OnToolCallErrorAsync(
                toolCall.ToolCallId,
                functionName,
                unavailableErrorMessage,
                cancellationToken
            );
        }

        var unavailableErrorResult = new ToolCallResult(toolCall.ToolCallId, unavailableErrorMessage);

        if (resultCallback != null && !string.IsNullOrEmpty(toolCall.ToolCallId))
        {
            await resultCallback.OnToolResultAvailableAsync(toolCall.ToolCallId, unavailableErrorResult, cancellationToken);
        }

        return unavailableErrorResult;
    }
}
