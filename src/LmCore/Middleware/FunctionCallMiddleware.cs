using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Messages;

namespace AchieveAi.LmDotnetTools.LmCore.Middleware;

/// <summary>
/// Middleware for handling function calls in agent responses
/// </summary>
public class FunctionCallMiddleware : IStreamingMiddleware
{
    private readonly IEnumerable<FunctionContract>? _functions;
    private readonly IDictionary<string, Func<string, Task<string>>>? _functionMap;

    public FunctionCallMiddleware(
        IEnumerable<FunctionContract>? functions = null,
        IDictionary<string, Func<string, Task<string>>>? functionMap = null,
        string? name = null)
    {
        Name = name ?? nameof(FunctionCallMiddleware);
        _functions = functions;
        _functionMap = functionMap;
    }

    public string? Name { get; }

    public async Task<IMessage> InvokeAsync(MiddlewareContext context, IAgent agent, CancellationToken cancellationToken = default)
    {
        var lastMessage = context.Messages.Last() as ICanGetToolCalls;
        var toolCalls = lastMessage?.GetToolCalls();
        if (toolCalls != null && toolCalls.Any())
        {
            return await ProcessToolCallsAsync(toolCalls, agent);
        }

        // Clone options and add functions
        var options = context.Options ?? new GenerateReplyOptions();
        var combinedFunctions = CombineFunctions(options.Functions);
        options.Functions = combinedFunctions?.ToArray();

        var reply = await agent.GenerateReplyAsync(context.Messages, options, cancellationToken);

        // Process any tool calls in the response
        var responseToolCall = reply as ICanGetToolCalls;
        var responseToolCalls = responseToolCall?.GetToolCalls();
        if (responseToolCalls != null && responseToolCalls.Any())
        {
            return await ProcessResponseToolCallsAsync(responseToolCalls, agent);
        }

        return reply;
    }

    public async Task<IAsyncEnumerable<IMessage>> InvokeStreamingAsync(
        MiddlewareContext context,
        IStreamingAgent agent,
        CancellationToken cancellationToken = default)
    {
        var lastMessage = context.Messages.Last() as ICanGetToolCalls;
        var toolCalls = lastMessage?.GetToolCalls();
        if (toolCalls != null && toolCalls.Any())
        {
            var result = await ProcessToolCallsAsync(toolCalls, agent);
            return new[] { result }.ToAsyncEnumerable();
        }

        // Clone options and add functions
        var options = context.Options ?? new GenerateReplyOptions();
        var combinedFunctions = CombineFunctions(options.Functions);
        options.Functions = combinedFunctions?.ToArray();

        // Get the streaming response from the agent
        var streamingResponse = await agent.GenerateReplyStreamingAsync(context.Messages, options, cancellationToken);
        
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

    private async Task<IMessage> ProcessToolCallsAsync(IEnumerable<ToolCall> toolCalls, IAgent agent)
    {
        if (_functionMap == null || !_functionMap.Any())
        {
            throw new InvalidOperationException("Function map is not available");
        }

        var toolCallResults = new List<ToolCallResult>();
        
        foreach (var toolCall in toolCalls)
        {
            var functionName = toolCall.FunctionName;
            var functionArgs = toolCall.FunctionArgs;
            
            if (_functionMap.TryGetValue(functionName, out var func))
            {
                var result = await func(functionArgs);
                toolCallResults.Add(new ToolCallResult(toolCall, result));
            }
            else
            {
                // Add error result for unavailable function
                var availableFunctions = string.Join(", ", _functionMap.Keys);
                var errorMessage = $"Function '{functionName}' is not available. Available functions: {availableFunctions}";
                toolCallResults.Add(new ToolCallResult(toolCall, errorMessage));
            }
        }
        
        // Return a ToolsCallResultMessage with all results
        return new ToolsCallResultMessage
        {
            ToolCallResults = toolCallResults.ToImmutableList(),
            Role = Role.Assistant,
            FromAgent = string.Empty  // No Id property in IAgent
        };
    }

    private async Task<IMessage> ProcessResponseToolCallsAsync(
        IEnumerable<ToolCall> toolCalls,
        IAgent agent)
    {
        if (_functionMap == null || !_functionMap.Any())
        {
            // If no function map is available, just return a message about the tool calls
            var toolCallNames = string.Join(", ", toolCalls.Select(tc => tc.FunctionName));
            return new TextMessage
            {
                Text = $"Tool calls requested: {toolCallNames}",
                Role = Role.Assistant,
                FromAgent = string.Empty  // No Id property in IAgent
            };
        }

        var toolCallResults = new List<ToolCallResult>();
        
        foreach (var toolCall in toolCalls)
        {
            var functionName = toolCall.FunctionName;
            var functionArgs = toolCall.FunctionArgs;
            
            if (_functionMap.TryGetValue(functionName, out var func))
            {
                // Execute the function
                var result = await func(functionArgs);
                toolCallResults.Add(new ToolCallResult(toolCall, result));
            }
            else
            {
                // Add error result for unavailable function
                var availableFunctions = string.Join(", ", _functionMap.Keys);
                var errorMessage = $"Function '{functionName}' is not available. Available functions: {availableFunctions}";
                toolCallResults.Add(new ToolCallResult(toolCall, errorMessage));
            }
        }
        
        // Return a ToolsCallResultMessage with all results
        return new ToolsCallResultMessage
        {
            ToolCallResults = toolCallResults.ToImmutableList(),
            Role = Role.Assistant,
            FromAgent = string.Empty  // No Id property in IAgent
        };
    }

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
            
            // Process the current message
            yield return await ProcessStreamingMessage(message, builders, cancellationToken);
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

    private Task<IMessage> ProcessStreamingMessage(
        IMessage message, 
        Dictionary<Type, object> builders,
        CancellationToken cancellationToken)
    {
        // Handle tool call updates for ToolsCallUpdateMessage
        if (message is ToolsCallUpdateMessage toolCallUpdate)
        {
            return ProcessToolCallUpdate(toolCallUpdate, builders);
        }
        
        // Handle completed tool call messages
        if (message is ToolsCallMessage toolCallMessage)
        {
            return ProcessCompleteToolCallMessage(toolCallMessage, builders);
        }
        
        // For text messages, use the TextMessageBuilder - but let TextUpdateMessage pass through
        if (message is TextMessage textMessage && !(message.GetType().Name.Contains("Update")))
        {
            return Task.FromResult(ProcessTextMessage(textMessage, builders));
        }
        
        // For all other message types, pass through as-is
        return Task.FromResult(message);
    }

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

    private Task<IMessage> ProcessCompleteToolCallMessage(
        ToolsCallMessage toolCallMessage,
        Dictionary<Type, object> builders)
    {
        // Process the tool calls if needed
        var toolCalls = toolCallMessage.ToolCalls;
        if (toolCalls.Any() && _functionMap != null)
        {
            var toolCallResults = new List<ToolCallResult>();
            
            foreach (var toolCall in toolCalls)
            {
                var functionName = toolCall.FunctionName;
                var functionArgs = toolCall.FunctionArgs;
                
                if (_functionMap.TryGetValue(functionName, out var func))
                {
                    var result = func(functionArgs).GetAwaiter().GetResult(); // Sync execution at end of stream
                    toolCallResults.Add(new ToolCallResult(toolCall, result));
                }
                else
                {
                    // Add error result for unavailable function
                    var availableFunctions = string.Join(", ", _functionMap.Keys);
                    var errorMessage = $"Function '{functionName}' is not available. Available functions: {availableFunctions}";
                    toolCallResults.Add(new ToolCallResult(toolCall, errorMessage));
                }
            }
            
            if (toolCallResults.Any())
            {
                return Task.FromResult<IMessage>(new ToolsCallResultMessage
                {
                    ToolCallResults = toolCallResults.ToImmutableList(),
                    Role = Role.Assistant,
                    FromAgent = toolCallMessage.FromAgent
                });
            }
        }

        return Task.FromResult<IMessage>(toolCallMessage);
    }

    private IMessage ProcessTextMessage(
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
        }
    }

    private IEnumerable<IMessage> ProcessFinalToolCallMessage(ToolsCallMessageBuilder toolsCallBuilder)
    {
        var builtMessage = toolsCallBuilder.Build();
        var toolCallResults = new List<ToolCallResult>();
        
        if (builtMessage.ToolCalls.Count > 0)
        {
            // Process each tool call if we have a function map
            foreach (var toolCall in builtMessage.ToolCalls)
            {
                if (_functionMap != null && _functionMap.TryGetValue(toolCall.FunctionName, out var func))
                {
                    try
                    {
                        var result = func(toolCall.FunctionArgs).GetAwaiter().GetResult(); // Sync execution at end of stream
                        toolCallResults.Add(new ToolCallResult(toolCall, result));
                    }
                    catch (Exception ex)
                    {
                        // Add error result
                        toolCallResults.Add(new ToolCallResult(toolCall, $"Error executing function: {ex.Message}"));
                    }
                }
                else
                {
                    toolCallResults.Add(new ToolCallResult(toolCall, string.Empty));
                }
            }
        }

        if (toolCallResults.Count > 0)
        {
            yield return new ToolsCallResultMessage
            {
                Role = Role.Assistant,
                FromAgent = builtMessage.FromAgent,
                ToolCallResults = toolCallResults.ToImmutableList()
            };
        }
        else
        {
            // Otherwise, just return the built message
            yield return builtMessage;
        }
    }
}

// Extension method to convert an array to IAsyncEnumerable
public static class AsyncEnumerableExtensions
{
    public static IAsyncEnumerable<T> ToAsyncEnumerable<T>(this T[] array)
    {
        return array.ToAsyncEnumerableInternal();
    }
    
    private static async IAsyncEnumerable<T> ToAsyncEnumerableInternal<T>(this T[] array)
    {
        foreach (var item in array)
        {
            await Task.Yield(); // Add await to make this truly async
            yield return item;
        }
    }
}