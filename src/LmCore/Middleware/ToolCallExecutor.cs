using AchieveAi.LmDotnetTools.LmCore.Messages;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AchieveAi.LmDotnetTools.LmCore.Middleware;

/// <summary>
///     Stateless executor for tool calls. Takes a <see cref="ToolsCallMessage"/> and executes
///     the tools, returning a <see cref="ToolsCallResultMessage"/>. Single execution path:
///     handlers return <see cref="ToolCallResult"/> directly (with optional
///     <see cref="ToolCallResult.ContentBlocks"/> for multi-modal payloads); the executor
///     stamps each result with its originating <see cref="ToolCall.ToolCallId"/>.
/// </summary>
/// <remarks>
///     Deferred tool execution is not supported here — this executor has no resolution
///     channel. <see cref="FunctionCallMiddleware"/> adapts unified <see cref="ToolHandlerResult"/>
///     handlers down to <see cref="ToolCallResult"/>-returning handlers (throwing on
///     <see cref="ToolHandlerResult.Deferred"/>) before invoking this executor.
/// </remarks>
public class ToolCallExecutor
{
    /// <summary>
    ///     Executes all tool calls in the provided message using the given function map.
    /// </summary>
    public static async Task<ToolsCallResultMessage> ExecuteAsync(
        ToolsCallMessage toolCallMessage,
        IDictionary<string, Func<string, Task<ToolCallResult>>> functionMap,
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

                toolCallResults.Add(
                    new ToolCallResult(toolCall.ToolCallId, $"Tool call execution error: {ex.Message}")
                    {
                        ToolName = toolCall.FunctionName,
                        ExecutionTarget = toolCall.ExecutionTarget,
                        IsError = true,
                    }
                );
            }
        }

        var duration = (DateTime.UtcNow - startTime).TotalMilliseconds;
        var successCount = toolCallResults.Count(r => !r.IsError);

        effectiveLogger.LogInformation(
            "Tool call execution completed: ToolCallCount={ToolCallCount}, SuccessCount={SuccessCount}, Duration={Duration}ms",
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

    private static async Task<ToolCallResult> ExecuteToolCallAsync(
        ToolCall toolCall,
        IDictionary<string, Func<string, Task<ToolCallResult>>> functionMap,
        IToolResultCallback? resultCallback,
        ILogger logger,
        CancellationToken cancellationToken
    )
    {
        var functionName = toolCall.FunctionName!;
        var functionArgs = toolCall.FunctionArgs!;
        var startTime = DateTime.UtcNow;

        logger.LogTrace(
            "ExecuteToolCallAsync entry: FunctionName={FunctionName}, ToolCallId={ToolCallId}, ArgsLength={ArgsLength}",
            functionName,
            toolCall.ToolCallId,
            functionArgs?.Length ?? 0);

        if (resultCallback != null && !string.IsNullOrEmpty(toolCall.ToolCallId))
        {
            await resultCallback.OnToolCallStartedAsync(
                toolCall.ToolCallId,
                functionName,
                functionArgs ?? string.Empty,
                cancellationToken
            );
        }

        if (functionMap.TryGetValue(functionName, out var func))
        {
            try
            {
                var result = await func(functionArgs ?? "{}");
                var duration = (DateTime.UtcNow - startTime).TotalMilliseconds;

                var imageBlockCount = result.ContentBlocks?.OfType<ImageToolResultBlock>().Count() ?? 0;
                var totalBlockCount = result.ContentBlocks?.Count ?? 0;

                logger.LogInformation(
                    "Function executed: Name={FunctionName}, Duration={Duration}ms, Success={Success}, ContentBlocks={ContentBlockCount} (Images={ImageBlocks}), ResultLength={ResultLength}",
                    functionName,
                    duration,
                    !result.IsError,
                    totalBlockCount,
                    imageBlockCount,
                    result.Result?.Length ?? 0
                );

                // Ensure tool call ID is set (handlers typically leave it null).
                var stamped = result with
                {
                    ToolCallId = toolCall.ToolCallId,
                    ToolName = string.IsNullOrEmpty(result.ToolName) ? functionName : result.ToolName,
                    ExecutionTarget = toolCall.ExecutionTarget,
                };

                if (resultCallback != null && !string.IsNullOrEmpty(toolCall.ToolCallId))
                {
                    await resultCallback.OnToolResultAvailableAsync(
                        toolCall.ToolCallId,
                        stamped,
                        cancellationToken
                    );
                }

                return stamped;
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

                var errorMessage = $"Error executing function: {ex.Message}";

                if (resultCallback != null && !string.IsNullOrEmpty(toolCall.ToolCallId))
                {
                    await resultCallback.OnToolCallErrorAsync(
                        toolCall.ToolCallId,
                        functionName,
                        errorMessage,
                        cancellationToken
                    );
                }

                var errorResult = new ToolCallResult(toolCall.ToolCallId, errorMessage)
                {
                    ToolName = functionName,
                    ExecutionTarget = toolCall.ExecutionTarget,
                    IsError = true,
                };

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

        // Unavailable function — return error so the LLM can self-correct.
        var availableFunctions = string.Join(", ", functionMap.Keys);
        var unavailableMessage = $"Function '{functionName}' is not available. Available functions: {availableFunctions}";

        logger.LogError(
            "Function mapping error: Unavailable function '{FunctionName}' requested, ToolCallId={ToolCallId}, AvailableFunctions=[{AvailableFunctions}]",
            functionName,
            toolCall.ToolCallId,
            availableFunctions
        );

        if (resultCallback != null && !string.IsNullOrEmpty(toolCall.ToolCallId))
        {
            await resultCallback.OnToolCallErrorAsync(
                toolCall.ToolCallId,
                functionName,
                unavailableMessage,
                cancellationToken
            );
        }

        var unavailableResult = new ToolCallResult(toolCall.ToolCallId, unavailableMessage)
        {
            ToolName = functionName,
            ExecutionTarget = toolCall.ExecutionTarget,
            IsError = true,
        };

        if (resultCallback != null && !string.IsNullOrEmpty(toolCall.ToolCallId))
        {
            await resultCallback.OnToolResultAvailableAsync(toolCall.ToolCallId, unavailableResult, cancellationToken);
        }

        return unavailableResult;
    }
}
