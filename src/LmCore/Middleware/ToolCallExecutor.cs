using AchieveAi.LmDotnetTools.LmCore.Approval;
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
///     Deferred tool execution is surfaced — handlers signaling
///     <see cref="ToolHandlerResult.Deferred"/> are adapted by
///     <see cref="FunctionCallMiddleware"/> into <see cref="ToolCallResult"/>s with
///     <see cref="ToolCallResult.IsDeferred"/> = true; the executor passes them through
///     unchanged. Resolution is the caller's responsibility.
/// </remarks>
public class ToolCallExecutor
{
    /// <summary>
    ///     Executes all tool calls in the provided message using the given function map.
    /// </summary>
    public static Task<ToolsCallResultMessage> ExecuteAsync(
        ToolsCallMessage toolCallMessage,
        IDictionary<string, ToolCallResultHandler> functionMap,
        IToolResultCallback? resultCallback = null,
        ILogger? logger = null,
        CancellationToken cancellationToken = default,
        ToolResultLimits? resultLimits = null
    ) =>
        ExecuteAsync(
            toolCallMessage,
            functionMap,
            ToolInvocationPreparer.Disabled,
            resultCallback,
            logger,
            cancellationToken,
            resultLimits
        );

    /// <summary>
    ///     Executes all tool calls in the provided message, gating each one through
    ///     <paramref name="preparer"/> first.
    /// </summary>
    /// <remarks>
    ///     Approvals for the whole batch are settled concurrently before anything runs, then the
    ///     calls are invoked in the order they arrived — so a slow approval does not reorder
    ///     execution, and a batch of ten calls does not take ten approval waits end to end.
    ///     A call the preparer refuses never reaches its handler; it comes back as an error result
    ///     carrying the outcome code, in the same shape as an unavailable function.
    ///     Every result — success, handler error, unavailable function, refusal — is bounded by
    ///     <paramref name="resultLimits"/> (default <see cref="ToolResultLimits.Default"/>) before
    ///     it is reported to the callback or returned, so history never carries a payload a
    ///     provider would reject (#694).
    /// </remarks>
    public static async Task<ToolsCallResultMessage> ExecuteAsync(
        ToolsCallMessage toolCallMessage,
        IDictionary<string, ToolCallResultHandler> functionMap,
        ToolInvocationPreparer preparer,
        IToolResultCallback? resultCallback = null,
        ILogger? logger = null,
        CancellationToken cancellationToken = default,
        ToolResultLimits? resultLimits = null
    )
    {
        ArgumentNullException.ThrowIfNull(toolCallMessage);
        ArgumentNullException.ThrowIfNull(functionMap);
        ArgumentNullException.ThrowIfNull(preparer);

        var effectiveLogger = logger ?? NullLogger.Instance;
        var effectiveLimits = resultLimits ?? ToolResultLimits.Default;
        var toolCalls = toolCallMessage.ToolCalls;
        var toolCallResults = new List<ToolCallResult>();
        var toolCallCount = toolCalls.Count;
        var startTime = DateTime.UtcNow;

        effectiveLogger.LogInformation("Tool call execution started: ToolCallCount={ToolCallCount}", toolCallCount);

        var prepared = await PrepareAllAsync(toolCallMessage, functionMap, preparer, cancellationToken);

        for (var i = 0; i < toolCalls.Count; i++)
        {
            var toolCall = toolCalls[i];
            try
            {
                var result = await ExecuteToolCallAsync(
                    toolCall,
                    functionMap,
                    preparer,
                    prepared[i],
                    resultCallback,
                    effectiveLogger,
                    effectiveLimits,
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
                    Bound(
                        new ToolCallResult(toolCall.ToolCallId, $"Tool call execution error: {ex.Message}")
                        {
                            ToolName = toolCall.FunctionName,
                            ExecutionTarget = toolCall.ExecutionTarget,
                            IsError = true,
                        },
                        effectiveLimits,
                        effectiveLogger
                    )
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

    /// <summary>
    ///     Settles the approval decision for every call in the batch at once, returning them by
    ///     index so execution can still proceed in the caller's order.
    /// </summary>
    /// <remarks>
    ///     A call whose function is not registered is deliberately left unprepared: refusing an
    ///     unknown function is a mapping error the executor already reports, and asking a human to
    ///     approve a tool that cannot run would be noise. Nothing executes if preparation itself
    ///     fails, which is the fail-closed outcome.
    /// </remarks>
    private static async Task<PreparedToolInvocation?[]> PrepareAllAsync(
        ToolsCallMessage toolCallMessage,
        IDictionary<string, ToolCallResultHandler> functionMap,
        ToolInvocationPreparer preparer,
        CancellationToken cancellationToken
    )
    {
        var toolCalls = toolCallMessage.ToolCalls;
        var prepared = new PreparedToolInvocation?[toolCalls.Count];

        if (!preparer.IsEnabled)
        {
            return prepared;
        }

        var preparations = new Task<PreparedToolInvocation>?[toolCalls.Count];
        for (var i = 0; i < toolCalls.Count; i++)
        {
            var toolCall = toolCalls[i];
            if (toolCall.FunctionName == null || !functionMap.ContainsKey(toolCall.FunctionName))
            {
                continue;
            }

            preparations[i] = preparer.PrepareAsync(
                new ToolInvocationRequest
                {
                    ToolName = toolCall.FunctionName,
                    ArgumentsJson = toolCall.FunctionArgs,
                    ToolCallId = toolCall.ToolCallId,
                    ExecutionTarget = toolCall.ExecutionTarget,
                    ThreadId = toolCallMessage.ThreadId,
                    RunId = toolCallMessage.RunId,
                    GenerationId = toolCallMessage.GenerationId,
                },
                cancellationToken
            );
        }

        _ = await Task.WhenAll(preparations.Where(t => t != null)!);

        for (var i = 0; i < preparations.Length; i++)
        {
            prepared[i] = preparations[i]?.Result;
        }

        return prepared;
    }

    /// <summary>
    ///     Applies <paramref name="limits"/> to a result on its way out, logging when it had to cut.
    /// </summary>
    private static ToolCallResult Bound(ToolCallResult result, ToolResultLimits limits, ILogger logger)
    {
        var bounded = limits.Apply(result);
        if (bounded.IsTruncated && !result.IsTruncated)
        {
            logger.LogWarning(
                "Tool result truncated: ToolCallId={ToolCallId}, FunctionName={FunctionName}, OriginalLength={OriginalLength}, MaxResultBytes={MaxResultBytes}",
                result.ToolCallId,
                result.ToolName,
                result.Result?.Length ?? 0,
                limits.MaxResultBytes
            );
        }

        return bounded;
    }

    private static async Task<ToolCallResult> ExecuteToolCallAsync(
        ToolCall toolCall,
        IDictionary<string, ToolCallResultHandler> functionMap,
        ToolInvocationPreparer preparer,
        PreparedToolInvocation? prepared,
        IToolResultCallback? resultCallback,
        ILogger logger,
        ToolResultLimits limits,
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
            functionArgs?.Length ?? 0
        );

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
            if (prepared is { IsApproved: false })
            {
                return await ReportBlockedAsync(toolCall, prepared, resultCallback, logger, limits, cancellationToken);
            }

            try
            {
                var ctx = new ToolCallContext { ToolCallId = toolCall.ToolCallId };
                var result =
                    prepared == null
                        ? await func(functionArgs ?? "{}", ctx, cancellationToken)
                        : await preparer.InvokeAsync(prepared, func, ctx, cancellationToken);
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

                // Ensure tool call ID is set (handlers typically leave it null), then bound the
                // payload before anyone (callback, history) sees it.
                var stamped = Bound(
                    result with
                    {
                        ToolCallId = toolCall.ToolCallId,
                        ToolName = string.IsNullOrEmpty(result.ToolName) ? functionName : result.ToolName,
                        ExecutionTarget = toolCall.ExecutionTarget,
                    },
                    limits,
                    logger
                );

                if (resultCallback != null && !string.IsNullOrEmpty(toolCall.ToolCallId))
                {
                    await resultCallback.OnToolResultAvailableAsync(toolCall.ToolCallId, stamped, cancellationToken);
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

                var errorResult = Bound(
                    new ToolCallResult(toolCall.ToolCallId, errorMessage)
                    {
                        ToolName = functionName,
                        ExecutionTarget = toolCall.ExecutionTarget,
                        IsError = true,
                    },
                    limits,
                    logger
                );

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
        var unavailableMessage =
            $"Function '{functionName}' is not available. Available functions: {availableFunctions}";

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

        var unavailableResult = Bound(
            new ToolCallResult(toolCall.ToolCallId, unavailableMessage)
            {
                ToolName = functionName,
                ExecutionTarget = toolCall.ExecutionTarget,
                IsError = true,
            },
            limits,
            logger
        );

        if (resultCallback != null && !string.IsNullOrEmpty(toolCall.ToolCallId))
        {
            await resultCallback.OnToolResultAvailableAsync(toolCall.ToolCallId, unavailableResult, cancellationToken);
        }

        return unavailableResult;
    }

    /// <summary>
    ///     Turns a refusal into the error result the caller already knows how to handle, reporting
    ///     it through the same callbacks a failed execution would use so a UI does not leave the
    ///     call spinning.
    /// </summary>
    private static async Task<ToolCallResult> ReportBlockedAsync(
        ToolCall toolCall,
        PreparedToolInvocation prepared,
        IToolResultCallback? resultCallback,
        ILogger logger,
        ToolResultLimits limits,
        CancellationToken cancellationToken
    )
    {
        var blockedResult = Bound(prepared.ToBlockedResult(), limits, logger);

        logger.LogWarning(
            "Tool call not executed: FunctionName={FunctionName}, ToolCallId={ToolCallId}, Outcome={Outcome}, Reason={Reason}",
            prepared.ToolName,
            toolCall.ToolCallId,
            prepared.Outcome,
            prepared.Reason
        );

        if (resultCallback != null && !string.IsNullOrEmpty(toolCall.ToolCallId))
        {
            await resultCallback.OnToolCallErrorAsync(
                toolCall.ToolCallId,
                prepared.ToolName,
                prepared.BlockedMessage,
                cancellationToken
            );

            await resultCallback.OnToolResultAvailableAsync(toolCall.ToolCallId, blockedResult, cancellationToken);
        }

        return blockedResult;
    }
}
