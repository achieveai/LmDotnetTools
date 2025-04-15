using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Utils;

namespace AchieveAi.LmDotnetTools.LmCore.Middleware;

/// <summary>
/// Middleware for handling function calls in agent responses
/// </summary>
public class FunctionCallMiddleware : IStreamingMiddleware
{
    private readonly IEnumerable<FunctionContract> _functions;
    private readonly IDictionary<string, Func<string, Task<string>>> _functionMap;

    public FunctionCallMiddleware(
        IEnumerable<FunctionContract> functions,
        IDictionary<string, Func<string, Task<string>>> functionMap,
        string? name = null)
    {
        _functions = functions ?? throw new ArgumentNullException(nameof(functions));

        // Validate that each function has a corresponding entry in the function map
        if (functions.Any())
        {
            if (functionMap != null)
            {
                var missingFunctions = functions
                    .Select(f => string.IsNullOrEmpty(f.ClassName) ? f.Name :  $"{f.ClassName}-{f.Name}")
                    .Where(f => !functionMap.ContainsKey(f))
                    .ToList();

                if (missingFunctions.Any() || functionMap.Count != functions.Count())
                {
                    throw new ArgumentException(
                        $"The following functions do not have corresponding entries in the function map: {string.Join(", ", missingFunctions)}",
                        nameof(functionMap));
                }
            }
            else
            {
                throw new ArgumentException("Function map must be provided when functions are specified", nameof(functionMap));
            }
        }

        Name = name ?? nameof(FunctionCallMiddleware);
        _functionMap = functionMap;
    }

    public string? Name { get; }

    public async Task<IEnumerable<IMessage>> InvokeAsync(MiddlewareContext context, IAgent agent, CancellationToken cancellationToken = default)
    {
        // Process any existing tool calls in the last message
        var (hasPendingToolCalls, toolCalls, options) = PrepareInvocation(context);
        if (hasPendingToolCalls)
        {
            var result = await ExecuteToolCallsAsync(toolCalls!, agent);
            return new[] { result };
        }

        // Generate reply with the configured options
        var replies = await agent.GenerateReplyAsync(
            context.Messages,
            options,
            cancellationToken);

        var processedReplies = new List<IMessage>();
        
        // Process each message in the reply
        foreach (var reply in replies)
        {
            // Check if this message has tool calls
            var responseToolCall = reply as ICanGetToolCalls;
            var responseToolCalls = responseToolCall?.GetToolCalls();
            
            if (responseToolCalls != null && responseToolCalls.Any())
            {
                // Process the tool calls for this message
                var result = await ExecuteToolCallsAsync(responseToolCalls, agent);
                processedReplies.Add(new ToolsCallAggregateMessage(
                    responseToolCall!,
                    result));
            }
            else
            {
                // Pass through messages without tool calls
                processedReplies.Add(reply);
            }
        }

        return processedReplies;
    }

    public async Task<IAsyncEnumerable<IMessage>> InvokeStreamingAsync(
        MiddlewareContext context,
        IStreamingAgent agent,
        CancellationToken cancellationToken = default)
    {
        // Process any existing tool calls in the last message
        var (hasPendingToolCalls, toolCalls, options) = PrepareInvocation(context);
        if (hasPendingToolCalls)
        {
            var result = await ExecuteToolCallsAsync(toolCalls!, agent);
            return new[] { result }.ToAsyncEnumerable();
        }

        // Get the streaming response from the agent
        var streamingResponse = await agent.GenerateReplyStreamingAsync(
            context.Messages,
            options,
            cancellationToken);
        
        // Return a transformed stream that applies the builder pattern
        return TransformStreamWithBuilder(streamingResponse, cancellationToken);
    }

    private IEnumerable<FunctionContract>? CombineFunctions(IEnumerable<FunctionContract>? optionFunctions)
    {
        if (_functions == null && optionFunctions == null)
        {
            return null;
        }
        
        if (_functions == null)
        {
            return optionFunctions;
        }
        
        if (optionFunctions == null)
        {
            return _functions;
        }
        
        return _functions.Concat(optionFunctions);
    }

    /// <summary>
    /// Common method to prepare invocation by checking for tool calls and configuring options
    /// </summary>
    private (bool HasPendingToolCalls, IEnumerable<ToolCall>? ToolCalls, GenerateReplyOptions Options) PrepareInvocation(MiddlewareContext context)
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
    /// Execute a single tool call and return the result
    /// </summary>
    private async Task<ToolCallResult> ExecuteToolCallAsync(ToolCall toolCall)
    {
        var functionName = toolCall.FunctionName;
        var functionArgs = toolCall.FunctionArgs;
        
        if (_functionMap.TryGetValue(functionName, out var func))
        {
            try
            {
                var result = await func(functionArgs);
                return new ToolCallResult(toolCall.ToolCallId, result);
            }
            catch (Exception ex)
            {
                // Handle exceptions during function execution
                return new ToolCallResult(toolCall.ToolCallId, $"Error executing function: {ex.Message}");
            }
        }
        else
        {
            // Return error for unavailable function
            var availableFunctions = string.Join(", ", _functionMap.Keys);
            var errorMessage = $"Function '{functionName}' is not available. Available functions: {availableFunctions}";
            return new ToolCallResult(toolCall.ToolCallId, errorMessage);
        }
    }
    
    /// <summary>
    /// Execute multiple tool calls and return a message with results
    /// </summary>
    private async Task<ToolsCallResultMessage> ExecuteToolCallsAsync(IEnumerable<ToolCall> toolCalls, IAgent agent)
    {
        var toolCallResults = new List<ToolCallResult>();
        
        foreach (var toolCall in toolCalls)
        {
            var result = await ExecuteToolCallAsync(toolCall);
            toolCallResults.Add(result);
        }
        
        // Return a ToolsCallResultMessage with all results
        return new ToolsCallResultMessage
        {
            ToolCallResults = toolCallResults.ToImmutableList(),
            Role = Role.Tool,
            FromAgent = string.Empty  // No Id property in IAgent
        };
    }

    // Method removed as it was replaced by ExecuteToolCallsAsync

    /// <summary>
    /// Transform a stream of messages using a message builder for aggregation
    /// </summary>
    private async IAsyncEnumerable<IMessage> TransformStreamWithBuilder(
        IAsyncEnumerable<IMessage> sourceStream,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Single builder for tool call messages
        ToolsCallMessageBuilder? toolsCallBuilder = null;
        bool wasProcessingToolCallUpdate = false;

        await foreach (var message in sourceStream.WithCancellation(cancellationToken))
        {
            // Check if we're switching message types and need to complete any pending builder
            var toolUpdateMessage = message as ToolsCallUpdateMessage;
            var textUpdateMessage = message as TextUpdateMessage;
            var hasUsage = textUpdateMessage != null && textUpdateMessage.Metadata != null && textUpdateMessage.Metadata.ContainsKey("usage");

            if (wasProcessingToolCallUpdate && toolUpdateMessage == null && toolsCallBuilder != null)
            {
                // Check if text update message is present and contains usage. In which case we are at the last message.
                var usage = hasUsage
                    ? textUpdateMessage!.Metadata!["usage"]
                    : null;

                // Complete the previous builder before processing the new message
                var rv = await ProcessFinalToolCallMessage(toolsCallBuilder);
                toolsCallBuilder = null;

                if (usage != null && rv is ToolsCallAggregateMessage aggregateMessage)
                {
                    // Clone the usage node instead of reusing it directly
                    var usageClone = JsonNode.Parse(usage.ToJsonString());
                    rv = aggregateMessage with { Metadata = new JsonObject
                        {
                            ["usage"] = usageClone,
                        }
                    };

                    yield return rv;
                    continue;
                }
                else
                {
                    yield return rv;
                }
            }
            
            // Update tracking state
            wasProcessingToolCallUpdate = toolUpdateMessage != null;

            // Skip empty text updates
            if (textUpdateMessage != null && string.IsNullOrEmpty(textUpdateMessage.Text) && !hasUsage)
            {
                continue;
            }
            
            // Handle ToolsCallUpdateMessage (partial updates to be accumulated)
            if (message is ToolsCallUpdateMessage updateMessage)
            {
                // Process partial tool call update
                toolsCallBuilder = ProcessToolCallUpdate(updateMessage, toolsCallBuilder);
            }
            // Handle complete ToolsCallMessage (no builder needed)
            else if (message is ICanGetToolCalls toolsCallMessage)
            {
                // If it has tool calls, execute them directly
                if (toolsCallMessage.GetToolCalls() != null && toolsCallMessage.GetToolCalls()!.Any())
                {
                    yield return await ProcessCompleteToolCallMessage(toolsCallMessage);
                }
                else
                {
                    // Just pass through empty tool call messages
                    yield return toolsCallMessage;
                }
            }
            else
            {
                // Pass through other message types
                yield return message;
            }
        }
        
        // Process any final built message at the end of the stream
        if (toolsCallBuilder != null)
        {
            yield return await ProcessFinalToolCallMessage(toolsCallBuilder);
        }
    }

    private Task<IMessage> CompletePendingBuilder(ToolsCallMessageBuilder builder)
    {
        var builtMessage = builder.Build();
        return Task.FromResult<IMessage>(builtMessage);
    }

    // This method has been removed as its functionality has been integrated into TransformStreamWithBuilder

    private ToolsCallMessageBuilder ProcessToolCallUpdate(
        ToolsCallUpdateMessage toolCallUpdate,
        ToolsCallMessageBuilder? existingBuilder)
    {
        // Get or create a ToolsCallMessageBuilder
        var builder = existingBuilder;
        
        if (builder == null)
        {
            builder = new ToolsCallMessageBuilder
            {
                FromAgent = toolCallUpdate.FromAgent,
                Role = toolCallUpdate.Role,
                GenerationId = toolCallUpdate.GenerationId,
                OnToolCall = OnToolCall
            };
        }

        builder.Add(toolCallUpdate);
        return builder;
    }

    // Dictionary to track pending tool call results by their ID
    private readonly Dictionary<string, Task<ToolCallResult>> _pendingToolCallResults = new();

    // Execute tool calls as soon as they're received during streaming
    private void OnToolCall(ToolCall call)
    {
        // Skip if we don't have a valid tool call ID or if we've already started processing this tool call
        if (string.IsNullOrEmpty(call.ToolCallId) || _pendingToolCallResults.ContainsKey(call.ToolCallId))
        {
            return;
        }

        // Start executing the tool call immediately and store the task
        _pendingToolCallResults[call.ToolCallId] = ExecuteToolCallAsync(call);
    }


    /// <summary>
    /// Process a complete tool call message by executing all tool calls
    /// </summary>
    private async Task<IMessage> ProcessCompleteToolCallMessage(ICanGetToolCalls toolCallMessage)
    {
        // Process the tool calls if needed
        var toolCalls = toolCallMessage.GetToolCalls();
        if (toolCalls != null && toolCalls.Any() && _functionMap != null)
        {
            var toolCallResults = new List<ToolCallResult>();
            var pendingToolCallTasks = new List<Task<ToolCallResult>>();
            
            foreach (var toolCall in toolCalls)
            {
                // Check if we already started executing this tool call
                if (!string.IsNullOrEmpty(toolCall.ToolCallId) && _pendingToolCallResults.TryGetValue(toolCall.ToolCallId, out var pendingTask))
                {
                    // Add to pending tasks to await later
                    pendingToolCallTasks.Add(pendingTask);
                }
                else
                {
                    // Execute the tool call now if it wasn't done already
                    pendingToolCallTasks.Add(ExecuteToolCallAsync(toolCall));
                }
            }

            // Await all pending tool call tasks
            var results = await Task.WhenAll(pendingToolCallTasks);
            toolCallResults.AddRange(results);

            // Clear completed tasks from the pending dictionary
            foreach (var toolCall in toolCalls)
            {
                if (!string.IsNullOrEmpty(toolCall.ToolCallId))
                {
                    _pendingToolCallResults.Remove(toolCall.ToolCallId);
                }
            }
            
            if (toolCallResults.Any())
            {
                return new ToolsCallAggregateMessage(
                    toolCallMessage,
                    new ToolsCallResultMessage
                    {
                        ToolCallResults = toolCallResults.ToImmutableList(),
                        Role = Role.Tool,
                        FromAgent = toolCallMessage.FromAgent
                    });
            }
        }

        return toolCallMessage;
    }

    /// <summary>
    /// Process a final tool call message by executing all tool calls synchronously
    /// </summary>
    private async Task<ToolsCallAggregateMessage> ProcessFinalToolCallMessage(ToolsCallMessageBuilder toolsCallBuilder)
    {
        var builtMessage = toolsCallBuilder.Build();
        var toolCallResults = new List<ToolCallResult>();
        var pendingToolCallTasks = new List<Task<ToolCallResult>>();

        if (builtMessage.ToolCalls.Count > 0)
        {
            // Process each tool call, using pre-executed results when available
            foreach (var toolCall in builtMessage.ToolCalls)
            {
                // Check if we already started executing this tool call
                if (!string.IsNullOrEmpty(toolCall.ToolCallId) && _pendingToolCallResults.TryGetValue(toolCall.ToolCallId, out var pendingTask))
                {
                    // Add to pending tasks to await later
                    pendingToolCallTasks.Add(pendingTask);
                }
                else
                {
                    // Execute the tool call now if it wasn't done already
                    pendingToolCallTasks.Add(ExecuteToolCallAsync(toolCall));
                }
            }
        }

        // Await all pending tool call tasks
        if (pendingToolCallTasks.Count > 0)
        {
            var results = await Task.WhenAll(pendingToolCallTasks);
            toolCallResults.AddRange(results);
            
            // Clear completed tasks from the pending dictionary
            foreach (var toolCall in builtMessage.ToolCalls)
            {
                if (!string.IsNullOrEmpty(toolCall.ToolCallId))
                {
                    _pendingToolCallResults.Remove(toolCall.ToolCallId);
                }
            }
        }
        
        return new ToolsCallAggregateMessage(
            builtMessage,
            new ToolsCallResultMessage
            {
                ToolCallResults = toolCallResults.ToImmutableList(),
                Role = Role.Tool,
                FromAgent = builtMessage.FromAgent
            });
    }
}