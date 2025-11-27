using System.Runtime.CompilerServices;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Utils;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AchieveAi.LmDotnetTools.LmCore.Middleware;

/// <summary>
///     Middleware for handling function calls in agent responses
/// </summary>
public class FunctionCallMiddleware : IStreamingMiddleware
{
    private readonly IDictionary<string, Func<string, Task<string>>> _functionMap;
    private readonly IEnumerable<FunctionContract> _functions;

    private readonly ILogger<FunctionCallMiddleware> _logger;

    // Dictionary to track pending tool call results by their ID
    private readonly Dictionary<string, Task<ToolCallResult>> _pendingToolCallResults = [];

    private IToolResultCallback? _resultCallback;

    public FunctionCallMiddleware(
        IEnumerable<FunctionContract> functions,
        IDictionary<string, Func<string, Task<string>>> functionMap,
        string? name = null,
        ILogger<FunctionCallMiddleware>? logger = null,
        IToolResultCallback? resultCallback = null
    )
    {
        _functions = functions ?? throw new ArgumentNullException(nameof(functions));
        _logger = logger ?? NullLogger<FunctionCallMiddleware>.Instance;

        // Validate that each function has a corresponding entry in the function map
        if (functions.Any())
        {
            if (functionMap != null)
            {
                var missingFunctions = functions
                    .Select(f => string.IsNullOrEmpty(f.ClassName) ? f.Name : $"{f.ClassName}-{f.Name}")
                    .Where(f => !functionMap.ContainsKey(f))
                    .ToList();

                // Removing following check ` || functionMap.Count != functions.Count()`
                if (missingFunctions.Count != 0)
                {
                    throw new ArgumentException(
                        $"The following functions do not have corresponding entries in the function map: {string.Join(", ", missingFunctions)}",
                        nameof(functionMap)
                    );
                }
            }
            else
            {
                throw new ArgumentException(
                    "Function map must be provided when functions are specified",
                    nameof(functionMap)
                );
            }
        }

        Name = name ?? nameof(FunctionCallMiddleware);
        _functionMap = functionMap;
        _resultCallback = resultCallback;
    }

    public string? Name { get; }

    public async Task<IEnumerable<IMessage>> InvokeAsync(
        MiddlewareContext context,
        IAgent agent,
        CancellationToken cancellationToken = default
    )
    {
        var startTime = DateTime.UtcNow;
        var messageCount = context.Messages.Count();

        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation(
                "Middleware processing started: MessageCount={MessageCount}, MiddlewareName={MiddlewareName}",
                messageCount,
                Name
            );
        }

        // Process any existing tool calls in the last message
        var (hasPendingToolCalls, toolCalls, options) = PrepareInvocation(context);
        if (hasPendingToolCalls)
        {
            var result = await ExecuteToolCallsAsync(toolCalls!, agent, cancellationToken);
            var duration = (DateTime.UtcNow - startTime).TotalMilliseconds;

            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation(
                    "Middleware processing completed: MessageCount={MessageCount}, ProcessedMessages={ProcessedMessages}, Duration={Duration}ms",
                    messageCount,
                    1,
                    duration
                );
            }

            return [result];
        }

        // Generate reply with the configured options
        ArgumentNullException.ThrowIfNull(agent);
        var replies = await agent.GenerateReplyAsync(context.Messages, options, cancellationToken);

        var processedReplies = new List<IMessage>();
        var usageAccumulator = new UsageAccumulator();

        // Process each message in the reply
        foreach (var reply in replies)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(
                    "Processing message: Type={MessageType}, HasMetadata={HasMetadata}",
                    reply.GetType().Name,
                    reply.Metadata != null
                );
            }

            // Check if this is a usage message
            if (reply is UsageMessage usageMessage)
            {
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug("Message transformation: UsageMessage accumulated");
                }

                _ = usageAccumulator.AddUsageFromMessage(usageMessage);
                continue; // We'll add a consolidated usage message at the end
            }

            // Legacy support: Check if the message has usage data in metadata
            var hasUsage = reply.Metadata != null && reply.Metadata.ContainsKey("usage");
            if (hasUsage)
            {
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug("Message transformation: Usage data extracted from metadata");
                }

                _ = usageAccumulator.AddUsageFromMessageMetadata(reply);

                // If this is an empty text message just for usage, don't add it to results
                var textMessage = reply as TextMessage;
                if (textMessage != null && string.IsNullOrEmpty(textMessage.Text))
                {
                    if (_logger.IsEnabled(LogLevel.Debug))
                    {
                        _logger.LogDebug("Message transformation: Empty text message with usage skipped");
                    }

                    continue;
                }
            }

            // Check if this message has tool calls
            var responseToolCall = reply as ToolsCallMessage;
            var responseToolCalls = responseToolCall?.ToolCalls;

            if (responseToolCalls != null && !responseToolCalls.IsEmpty)
            {
                _logger.LogDebug(
                    "Tool call aggregation: Processing {ToolCallCount} tool calls",
                    responseToolCalls.Count
                );

                // Process the tool calls for this message
                var result = await ExecuteToolCallsAsync(responseToolCalls, agent, cancellationToken);
                var aggregateMessage = new ToolsCallAggregateMessage(responseToolCall!, result);

                _logger.LogDebug(
                    "Tool call aggregation: Created aggregate message with {ResultCount} results",
                    result.ToolCallResults.Count
                );

                processedReplies.Add(aggregateMessage);
            }
            else
            {
                // Pass through messages without tool calls, but strip usage from metadata
                if (hasUsage)
                {
                    _logger.LogDebug(
                        "Message transformation: Stripping usage metadata from {MessageType}",
                        reply.GetType().Name
                    );

                    // Clone the message without usage metadata
                    var metadataWithoutUsage = reply.Metadata!.Remove("usage");

                    if (reply is TextMessage textMsg)
                    {
                        processedReplies.Add(
                            textMsg with
                            {
                                Metadata = metadataWithoutUsage.Count > 0 ? metadataWithoutUsage : null,
                            }
                        );
                    }
                    else if (reply is ToolsCallMessage toolCallMsg)
                    {
                        processedReplies.Add(
                            toolCallMsg with
                            {
                                Metadata = metadataWithoutUsage.Count > 0 ? metadataWithoutUsage : null,
                            }
                        );
                    }
                    else
                    {
                        // For other message types, just add the original
                        processedReplies.Add(reply);
                    }
                }
                else
                {
                    _logger.LogDebug(
                        "Message transformation: Passing through {MessageType} without changes",
                        reply.GetType().Name
                    );

                    // Pass through messages without usage metadata
                    processedReplies.Add(reply);
                }
            }
        }

        // Add accumulated usage message at the end if we extracted usage data
        var finalUsageMessage = usageAccumulator.CreateUsageMessage();
        if (finalUsageMessage != null)
        {
            processedReplies.Add(finalUsageMessage);
        }

        var totalDuration = (DateTime.UtcNow - startTime).TotalMilliseconds;

        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation(
                "Middleware processing completed: MessageCount={MessageCount}, ProcessedMessages={ProcessedMessages}, Duration={Duration}ms",
                messageCount,
                processedReplies.Count,
                totalDuration
            );
        }

        return processedReplies;
    }

    public async Task<IAsyncEnumerable<IMessage>> InvokeStreamingAsync(
        MiddlewareContext context,
        IStreamingAgent agent,
        CancellationToken cancellationToken = default
    )
    {
        var messageCount = context.Messages.Count();

        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation(
                "Middleware streaming processing started: MessageCount={MessageCount}, MiddlewareName={MiddlewareName}",
                messageCount,
                Name
            );
        }

        // Process any existing tool calls in the last message
        var (hasPendingToolCalls, toolCalls, options) = PrepareInvocation(context);
        if (hasPendingToolCalls)
        {
            var result = await ExecuteToolCallsAsync(toolCalls!, agent, cancellationToken);

            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation(
                    "Middleware streaming processing completed: MessageCount={MessageCount}, ProcessedMessages={ProcessedMessages}",
                    messageCount,
                    1
                );
            }

            return new[] { result }.ToAsyncEnumerable();
        }

        // Get the streaming response from the agent
        ArgumentNullException.ThrowIfNull(agent);
        var streamingResponse = await agent.GenerateReplyStreamingAsync(context.Messages, options, cancellationToken);

        // Return a transformed stream that applies the builder pattern
        return TransformStreamWithBuilder(streamingResponse, cancellationToken);
    }

    /// <summary>
    ///     Sets or updates the tool result callback for this middleware instance.
    /// </summary>
    /// <param name="callback">The callback to notify when tool results are available</param>
    /// <returns>This middleware instance for chaining</returns>
    public FunctionCallMiddleware WithResultCallback(IToolResultCallback? callback)
    {
        _resultCallback = callback;
        return this;
    }

    private IEnumerable<FunctionContract>? CombineFunctions(IEnumerable<FunctionContract>? optionFunctions)
    {
        return _functions == null && optionFunctions == null ? null
            : _functions == null ? optionFunctions
            : optionFunctions == null ? _functions
            : _functions.Concat(optionFunctions);
    }

    /// <summary>
    ///     Common method to prepare invocation by checking for tool calls and configuring options
    /// </summary>
    private (
        bool HasPendingToolCalls,
        IEnumerable<ToolCall>? ToolCalls,
        GenerateReplyOptions Options
    ) PrepareInvocation(MiddlewareContext context)
    {
        // Check for existing tool calls that need processing
        var lastMessage = context.Messages.Last() as ICanGetToolCalls;
        var toolCalls = lastMessage?.GetToolCalls();
        var hasPendingToolCalls = toolCalls != null && toolCalls.Any();

        // Clone options and add functions
        var options = context.Options ?? new GenerateReplyOptions();
        var combinedFunctions = CombineFunctions(options.Functions);
        options = options with { Functions = combinedFunctions?.ToArray() };

        return (hasPendingToolCalls, toolCalls, options);
    }

    /// <summary>
    ///     Execute multiple tool calls and return a message with results.
    ///     This method now delegates to ToolCallExecutor for actual execution.
    /// </summary>
    private async Task<ToolsCallResultMessage> ExecuteToolCallsAsync(
        IEnumerable<ToolCall> toolCalls,
        IAgent agent,
        CancellationToken cancellationToken = default
    )
    {
        // Create a ToolsCallMessage from the tool calls for the executor
        var toolsCallMessage = new ToolsCallMessage
        {
            ToolCalls = [.. toolCalls],
            Role = Role.Assistant,
            FromAgent = string.Empty,
        };

        // Delegate to ToolCallExecutor
        return await ToolCallExecutor.ExecuteAsync(
            toolsCallMessage,
            _functionMap,
            _resultCallback,
            _logger,
            cancellationToken
        );
    }

    /// <summary>
    ///     Execute a single tool call and return the result.
    ///     Helper method for streaming scenarios. Delegates to ToolCallExecutor.
    /// </summary>
    private async Task<ToolCallResult> ExecuteToolCallAsync(
        ToolCall toolCall,
        CancellationToken cancellationToken = default
    )
    {
        // Create a ToolsCallMessage with a single tool call
        var toolsCallMessage = new ToolsCallMessage
        {
            ToolCalls = [toolCall],
            Role = Role.Assistant,
            FromAgent = string.Empty,
        };

        // Execute using ToolCallExecutor
        var result = await ToolCallExecutor.ExecuteAsync(
            toolsCallMessage,
            _functionMap,
            _resultCallback,
            _logger,
            cancellationToken
        );

        // Return the first (and only) result
        return result.ToolCallResults.First();
    }

    /// <summary>
    ///     Transform a stream of messages using a message builder for aggregation
    /// </summary>
    private async IAsyncEnumerable<IMessage> TransformStreamWithBuilder(
        IAsyncEnumerable<IMessage> sourceStream,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        // Single builder for tool call messages
        ToolsCallMessageBuilder? toolsCallBuilder = null;
        var wasProcessingToolCallUpdate = false;

        // Use the usage accumulator to track usage data
        var usageAccumulator = new UsageAccumulator();

        await foreach (var message in sourceStream.WithCancellation(cancellationToken))
        {
            _logger.LogDebug(
                "Streaming message processing: Type={MessageType}, HasMetadata={HasMetadata}",
                message.GetType().Name,
                message.Metadata != null
            );

            // If it's a usage message, add to accumulator and pass through
            if (message is UsageMessage usageMessage)
            {
                _logger.LogDebug("Streaming message processing: UsageMessage accumulated and passed through");
                _ = usageAccumulator.AddUsageFromMessage(usageMessage);
                yield return usageMessage;
                continue;
            }

            // Check if we're switching message types and need to complete any pending builder
            var toolUpdateMessage = message as ToolsCallUpdateMessage;
            var textUpdateMessage = message as TextUpdateMessage;
            var hasUsage = message.Metadata != null && message.Metadata.ContainsKey("usage");

            // Extract any usage data from message metadata
            if (hasUsage)
            {
                _logger.LogDebug("Streaming message processing: Usage data extracted from metadata");
                _ = usageAccumulator.AddUsageFromMessageMetadata(message);
            }

            if (wasProcessingToolCallUpdate && toolUpdateMessage == null && toolsCallBuilder != null)
            {
                _logger.LogDebug("Streaming message processing: Completing pending tool call builder");

                // Complete the previous builder before processing the new message
                var rv = await ProcessFinalToolCallMessage(toolsCallBuilder);
                toolsCallBuilder = null;

                yield return rv;
                continue;
            }

            // Update tracking state
            wasProcessingToolCallUpdate = toolUpdateMessage != null;

            // Skip empty text updates that are just for usage
            if (textUpdateMessage != null && string.IsNullOrEmpty(textUpdateMessage.Text) && hasUsage)
            {
                _logger.LogDebug("Streaming message processing: Skipping empty text update with usage");
                continue;
            }

            // Skip empty text updates
            if (textUpdateMessage != null && string.IsNullOrEmpty(textUpdateMessage.Text) && !hasUsage)
            {
                _logger.LogDebug("Streaming message processing: Skipping empty text update");
                continue;
            }

            // Handle ToolsCallUpdateMessage (partial updates to be accumulated)
            if (message is ToolsCallUpdateMessage updateMessage)
            {
                _logger.LogDebug(
                    "Streaming message processing: Processing tool call update, BuilderExists={BuilderExists}",
                    toolsCallBuilder != null
                );

                // Process partial tool call update
                toolsCallBuilder = ProcessToolCallUpdate(updateMessage, toolsCallBuilder);
            }
            // Handle complete ToolsCallMessage (no builder needed)
            else if (message is ToolsCallMessage toolsCallMessage)
            {
                // If it has tool calls, execute them directly
                if (toolsCallMessage.ToolCalls != null && !toolsCallMessage.ToolCalls.IsEmpty)
                {
                    _logger.LogDebug(
                        "Streaming message processing: Processing complete tool call message with {ToolCallCount} calls",
                        toolsCallMessage.ToolCalls.Count
                    );

                    yield return await ProcessCompleteToolCallMessage(toolsCallMessage);
                }
                else
                {
                    _logger.LogDebug("Streaming message processing: Passing through empty tool call message");

                    // Just pass through empty tool call messages
                    yield return toolsCallMessage;
                }
            }
            else
            {
                _logger.LogDebug("Streaming message processing: Passing through {MessageType}", message.GetType().Name);

                // Pass through other message types
                yield return message;
            }
        }

        // Process any final built message at the end of the stream
        if (toolsCallBuilder != null)
        {
            yield return await ProcessFinalToolCallMessage(toolsCallBuilder);
        }

        // Emit accumulated usage as a separate message at the end
        var finalUsageMessage = usageAccumulator.CreateUsageMessage();
        if (finalUsageMessage != null)
        {
            yield return finalUsageMessage;
        }
    }

    private static Task<IMessage> CompletePendingBuilder(ToolsCallMessageBuilder builder)
    {
        var builtMessage = builder.Build();
        return Task.FromResult<IMessage>(builtMessage);
    }

    // This method has been removed as its functionality has been integrated into TransformStreamWithBuilder

    private ToolsCallMessageBuilder ProcessToolCallUpdate(
        ToolsCallUpdateMessage toolCallUpdate,
        ToolsCallMessageBuilder? existingBuilder
    )
    {
        // Get or create a ToolsCallMessageBuilder
        var builder = existingBuilder;

        if (builder == null)
        {
            _logger.LogDebug(
                "Builder pattern usage: Creating new ToolsCallMessageBuilder for agent {FromAgent}",
                toolCallUpdate.FromAgent
            );

            builder = new ToolsCallMessageBuilder
            {
                FromAgent = toolCallUpdate.FromAgent,
                Role = toolCallUpdate.Role,
                GenerationId = toolCallUpdate.GenerationId,
                OnToolCall = OnToolCall,
            };
        }

        _logger.LogDebug("Builder pattern usage: Adding tool call update to builder");
        builder.Add(toolCallUpdate);
        return builder;
    }

    // Execute tool calls as soon as they're received during streaming
    private void OnToolCall(ToolCall call)
    {
        // Skip if we don't have a valid tool call ID or if we've already started processing this tool call
        if (string.IsNullOrEmpty(call.ToolCallId) || _pendingToolCallResults.ContainsKey(call.ToolCallId))
        {
            return;
        }

        // Start executing the tool call immediately and store the task
        // Note: We use CancellationToken.None here as we don't have access to the streaming cancellation token
        // in this callback. This is acceptable as tool calls should complete regardless.
        var task = ExecuteToolCallAsync(call, CancellationToken.None);
        _pendingToolCallResults[call.ToolCallId] = task;
    }

    /// <summary>
    ///     Process a complete tool call message by executing all tool calls
    /// </summary>
    private async Task<IMessage> ProcessCompleteToolCallMessage(ToolsCallMessage toolCallMessage)
    {
        // Process the tool calls if needed
        var toolCalls = toolCallMessage.ToolCalls;
        if (toolCalls != null && !toolCalls.IsEmpty && _functionMap != null)
        {
            var toolCallResults = new List<ToolCallResult>();
            var pendingToolCallTasks = new List<Task<ToolCallResult>>();

            foreach (var toolCall in toolCalls)
            {
                // Check if we already started executing this tool call
                if (
                    !string.IsNullOrEmpty(toolCall.ToolCallId)
                    && _pendingToolCallResults.TryGetValue(toolCall.ToolCallId, out var pendingTask)
                )
                {
                    // Add to pending tasks to await later
                    pendingToolCallTasks.Add(pendingTask);
                }
                else
                {
                    // Execute the tool call now if it wasn't done already
                    pendingToolCallTasks.Add(ExecuteToolCallAsync(toolCall, CancellationToken.None));
                }
            }

            // Await all pending tool call tasks
            try
            {
                var results = await Task.WhenAll(pendingToolCallTasks);
                toolCallResults.AddRange(results);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Tool call processing error in complete message processing: ToolCallCount={ToolCallCount}",
                    toolCalls.Count
                );

                // Handle individual task failures
                for (var i = 0; i < pendingToolCallTasks.Count; i++)
                {
                    var task = pendingToolCallTasks[i];
                    if (task.IsFaulted)
                    {
                        var toolCall = toolCalls.ElementAt(i);
                        _logger.LogError(
                            task.Exception,
                            "Individual tool call failed: ToolCallId={ToolCallId}, FunctionName={FunctionName}",
                            toolCall.ToolCallId,
                            toolCall.FunctionName
                        );

                        toolCallResults.Add(
                            new ToolCallResult(
                                toolCall.ToolCallId,
                                $"Tool call failed: {task.Exception?.GetBaseException().Message}"
                            )
                        );
                    }
                    else if (task.IsCompletedSuccessfully)
                    {
                        toolCallResults.Add(task.Result);
                    }
                }
            }

            // Clear completed tasks from the pending dictionary
            foreach (var toolCall in toolCalls)
            {
                if (!string.IsNullOrEmpty(toolCall.ToolCallId))
                {
                    _ = _pendingToolCallResults.Remove(toolCall.ToolCallId);
                }
            }

            if (toolCallResults.Count != 0)
            {
                return new ToolsCallAggregateMessage(
                    toolCallMessage,
                    new ToolsCallResultMessage
                    {
                        ToolCallResults = [.. toolCallResults],
                        Role = Role.Tool,
                        FromAgent = toolCallMessage.FromAgent,
                    }
                );
            }
        }

        return toolCallMessage;
    }

    /// <summary>
    ///     Process a final tool call message by executing all tool calls synchronously
    /// </summary>
    private async Task<ToolsCallAggregateMessage> ProcessFinalToolCallMessage(ToolsCallMessageBuilder toolsCallBuilder)
    {
        var builtMessage = toolsCallBuilder.Build();
        var toolCallResults = new List<ToolCallResult>();
        var pendingToolCallTasks = new List<Task<ToolCallResult>>();

        _logger.LogDebug(
            "Tool call aggregation: Building final message with {ToolCallCount} tool calls",
            builtMessage.ToolCalls.Count
        );

        if (builtMessage.ToolCalls.Count > 0)
        {
            // Process each tool call, using pre-executed results when available
            foreach (var toolCall in builtMessage.ToolCalls)
            {
                // Check if we already started executing this tool call
                if (
                    !string.IsNullOrEmpty(toolCall.ToolCallId)
                    && _pendingToolCallResults.TryGetValue(toolCall.ToolCallId, out var pendingTask)
                )
                {
                    _logger.LogDebug(
                        "Tool call aggregation: Using pre-executed result for tool call {ToolCallId}",
                        toolCall.ToolCallId
                    );

                    // Add to pending tasks to await later
                    pendingToolCallTasks.Add(pendingTask);
                }
                else
                {
                    _logger.LogDebug(
                        "Tool call aggregation: Executing tool call {ToolCallId} now",
                        toolCall.ToolCallId
                    );

                    // Execute the tool call now if it wasn't done already
                    pendingToolCallTasks.Add(ExecuteToolCallAsync(toolCall, CancellationToken.None));
                }
            }
        }

        // Await all pending tool call tasks
        if (pendingToolCallTasks.Count > 0)
        {
            _logger.LogDebug("Tool call aggregation: Awaiting {TaskCount} tool call tasks", pendingToolCallTasks.Count);

            try
            {
                var results = await Task.WhenAll(pendingToolCallTasks);
                toolCallResults.AddRange(results);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Tool call processing error in final message processing: ToolCallCount={ToolCallCount}",
                    builtMessage.ToolCalls.Count
                );

                // Handle individual task failures
                for (var i = 0; i < pendingToolCallTasks.Count; i++)
                {
                    var task = pendingToolCallTasks[i];
                    if (task.IsFaulted)
                    {
                        var toolCall = builtMessage.ToolCalls[i];
                        _logger.LogError(
                            task.Exception,
                            "Individual tool call failed in final processing: ToolCallId={ToolCallId}, FunctionName={FunctionName}",
                            toolCall.ToolCallId,
                            toolCall.FunctionName
                        );

                        toolCallResults.Add(
                            new ToolCallResult(
                                toolCall.ToolCallId,
                                $"Tool call failed: {task.Exception?.GetBaseException().Message}"
                            )
                        );
                    }
                    else if (task.IsCompletedSuccessfully)
                    {
                        toolCallResults.Add(task.Result);
                    }
                }
            }

            // Clear completed tasks from the pending dictionary
            foreach (var toolCall in builtMessage.ToolCalls)
            {
                if (!string.IsNullOrEmpty(toolCall.ToolCallId))
                {
                    _ = _pendingToolCallResults.Remove(toolCall.ToolCallId);
                }
            }
        }

        _logger.LogDebug(
            "Tool call aggregation: Created final aggregate message with {ResultCount} results",
            toolCallResults.Count
        );

        return new ToolsCallAggregateMessage(
            builtMessage,
            new ToolsCallResultMessage
            {
                ToolCallResults = [.. toolCallResults],
                Role = Role.Tool,
                FromAgent = builtMessage.FromAgent,
            }
        );
    }
}
