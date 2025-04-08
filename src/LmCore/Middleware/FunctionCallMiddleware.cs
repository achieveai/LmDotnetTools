using System.Collections.Immutable;
using System.Runtime.CompilerServices;
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
        // Validate functions parameter
        if (functions == null)
        {
            throw new ArgumentNullException(nameof(functions));
        }
        
        // Validate that each function has a corresponding entry in the function map
        if (functions.Any())
        {
            if (functionMap != null)
            {
                var missingFunctions = functions
                    .Where(f => !functionMap.ContainsKey(f.Name))
                    .Select(f => f.Name)
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
        _functions = functions;
        _functionMap = functionMap;
    }

    public string? Name { get; }

    public async Task<IMessage> InvokeAsync(MiddlewareContext context, IAgent agent, CancellationToken cancellationToken = default)
    {
        // Process any existing tool calls in the last message
        var (hasPendingToolCalls, toolCalls, options) = PrepareInvocation(context);
        if (hasPendingToolCalls)
        {
            return await ExecuteToolCallsAsync(toolCalls!, agent);
        }

        // Generate reply with the configured options
        var reply = await agent.GenerateReplyAsync(
            context.Messages,
            options,
            cancellationToken);

        // Process any tool calls in the response
        var responseToolCall = reply as ICanGetToolCalls;
        var responseToolCalls = responseToolCall?.GetToolCalls();
        if (responseToolCalls != null && responseToolCalls.Any())
        {
            var result = await ExecuteToolCallsAsync(responseToolCalls, agent);
            return new ToolsCallAggregateMessage(
                responseToolCall!,
                result);
        }

        return reply;
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
    /// Transform a stream of messages using message builders for aggregation
    /// </summary>
    private async IAsyncEnumerable<IMessage> TransformStreamWithBuilder(
        IAsyncEnumerable<IMessage> sourceStream,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Dictionary to track message builders by their type
        var builders = new Dictionary<Type, object>();
        Type? lastMessageType = null;
        
        await foreach (var message in sourceStream.WithCancellation(cancellationToken))
        {
            // Check if we're switching message types and need to complete any pending builders
            if (lastMessageType != null && lastMessageType != message.GetType() && builders.ContainsKey(lastMessageType))
            {
                // Complete the previous builder before processing the new message
                yield return await CompletePendingBuilder(lastMessageType, builders);
            }
            
            // Update last message type
            lastMessageType = message.GetType();
            
            // Process the current message based on its type
            if (message is TextMessage textMessage)
            {
                yield return ProcessTextMessage(textMessage, builders);
            }
            // Handle ToolsCallUpdateMessage (partial updates to be accumulated)
            else if (message is ToolsCallUpdateMessage updateMessage)
            {
                // Process partial tool call update
                yield return await ProcessToolCallUpdate(updateMessage, builders);
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
        
        // Process any final built messages at the end of the stream
        foreach (var finalMessage in ProcessFinalBuiltMessages(builders))
        {
            yield return finalMessage;
        }
    }

    private Task<IMessage> CompletePendingBuilder(Type messageType, Dictionary<Type, object> builders)
    {
        if (messageType == typeof(ToolsCallUpdateMessage) && builders.TryGetValue(typeof(ToolsCallMessage), out var toolsCallBuilder))
        {
            var builder = (ToolsCallMessageBuilder)toolsCallBuilder;
            var builtMessage = builder.Build();
            builders.Remove(typeof(ToolsCallMessage));
            return Task.FromResult<IMessage>(builtMessage);
        }
        else if (messageType == typeof(TextMessage) && builders.TryGetValue(typeof(TextMessage), out var textBuilder))
        {
            var builder = (TextMessageBuilder)textBuilder;
            var builtMessage = builder.Build();
            builders.Remove(typeof(TextMessage));
            return Task.FromResult<IMessage>(builtMessage);
        }
        
        // No pending builder to complete
        return Task.FromResult<IMessage>(new TextMessage { Text = string.Empty, Role = Role.System });
    }

    // This method has been removed as its functionality has been integrated into TransformStreamWithBuilder

    private Task<IMessage> ProcessToolCallUpdate(
        ToolsCallUpdateMessage toolCallUpdate, 
        Dictionary<Type, object> builders)
    {
        // Get or create a ToolsCallMessageBuilder
        if (!builders.TryGetValue(typeof(ToolsCallMessage), out var builderObj))
        {
            var builder = new ToolsCallMessageBuilder
            {
                FromAgent = toolCallUpdate.FromAgent,
                Role = toolCallUpdate.Role,
                GenerationId = toolCallUpdate.GenerationId
            };
            builders[typeof(ToolsCallMessage)] = builder;
            builderObj = builder;
        }

        var toolsCallBuilder = (ToolsCallMessageBuilder)builderObj;
        toolsCallBuilder.Add(toolCallUpdate);

        return Task.FromResult<IMessage>(new TextMessage { Text = string.Empty, Role = Role.System });
    }

    /// <summary>
    /// Process a complete tool call message by executing all tool calls
    /// </summary>
    private Task<IMessage> ProcessCompleteToolCallMessage(ICanGetToolCalls toolCallMessage)
    {
        // Process the tool calls if needed
        var toolCalls = toolCallMessage.GetToolCalls();
        if (toolCalls != null && toolCalls.Any() && _functionMap != null)
        {
            var toolCallResults = new List<ToolCallResult>();
            
            foreach (var toolCall in toolCalls)
            {
                // Use the common execution logic but run it synchronously
                var result = ExecuteToolCallAsync(toolCall).GetAwaiter().GetResult();
                toolCallResults.Add(result);
            }
            
            if (toolCallResults.Any())
            {
                return Task.FromResult<IMessage>(
                    new ToolsCallAggregateMessage(
                        toolCallMessage,
                        new ToolsCallResultMessage
                        {
                            ToolCallResults = toolCallResults.ToImmutableList(),
                            Role = Role.Tool,
                            FromAgent = toolCallMessage.FromAgent
                        }));
            }
        }

        return Task.FromResult<IMessage>(toolCallMessage);
    }

    private static TextMessage ProcessTextMessage(
        TextMessage textMessage,
        Dictionary<Type, object> builders)
    {
        if (!builders.TryGetValue(typeof(TextMessage), out var builderObj))
        {
            var builder = new TextMessageBuilder
            {
                Role = textMessage.Role,
                FromAgent = textMessage.FromAgent
            };
            builders[typeof(TextMessage)] = builder;
            builder.Add(textMessage);
            return textMessage; // First message is passed through as-is
        }
        else
        {
            var builder = (TextMessageBuilder)builderObj;
            builder.Add(textMessage);
            return builder.Build(); // Return the accumulated message
        }
    }

    /// <summary>
    /// Process all final message builders into complete messages
    /// </summary>
    private IEnumerable<IMessage> ProcessFinalBuiltMessages(Dictionary<Type, object> builders)
    {
        foreach (var (type, builder) in builders)
        {
            if (type == typeof(ToolsCallMessage))
            {
                foreach (var message in ProcessFinalToolCallMessage((ToolsCallMessageBuilder)builder))
                {
                    yield return message;
                }
            }
            else if (type == typeof(TextMessage))
            {
                var textBuilder = (TextMessageBuilder)builder;
                yield return textBuilder.Build();
            }
            // Extensible: additional message types can be added here
        }
    }

    /// <summary>
    /// Process a final tool call message by executing all tool calls synchronously
    /// </summary>
    private IEnumerable<ToolsCallAggregateMessage> ProcessFinalToolCallMessage(ToolsCallMessageBuilder toolsCallBuilder)
    {
        var builtMessage = toolsCallBuilder.Build();
        var toolCallResults = new List<ToolCallResult>();
        
        if (builtMessage.ToolCalls.Count > 0)
        {
            // Process each tool call synchronously
            foreach (var toolCall in builtMessage.ToolCalls)
            {
                // Use the same execution logic but run it synchronously
                var result = ExecuteToolCallAsync(toolCall).GetAwaiter().GetResult();
                toolCallResults.Add(result);
            }
        }

        yield return new ToolsCallAggregateMessage(
            builtMessage,
            new ToolsCallResultMessage
            {
                ToolCallResults = toolCallResults.ToImmutableList(),
                Role = Role.Tool,
                FromAgent = builtMessage.FromAgent
            });
    }
}

// Extension method to convert an array to IAsyncEnumerable