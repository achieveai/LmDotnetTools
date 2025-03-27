using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Messages;

namespace AchieveAi.LmDotnetTools.LmCore.Middleware;

/// <summary>
/// Middleware that joins update messages into larger messages for more efficient processing.
/// </summary>
public class MessageUpdateJoinerMiddleware : IStreamingMiddleware
{
    private readonly bool _preserveUpdateMessages;

    /// <summary>
    /// Initializes a new instance of the <see cref="MessageUpdateJoinerMiddleware"/> class.
    /// </summary>
    /// <param name="preserveUpdateMessages">If true, update messages will still be emitted alongside the joined messages.</param>
    /// <param name="name">Optional name for the middleware.</param>
    public MessageUpdateJoinerMiddleware(
        bool preserveUpdateMessages = false,
        string? name = null)
    {
        Name = name ?? nameof(MessageUpdateJoinerMiddleware);
        _preserveUpdateMessages = preserveUpdateMessages;
    }

    /// <summary>
    /// Gets the name of the middleware.
    /// </summary>
    public string? Name { get; }

    /// <summary>
    /// Invokes the middleware for synchronous scenarios.
    /// </summary>
    public async Task<IMessage> InvokeAsync(
        MiddlewareContext context,
        IAgent agent,
        CancellationToken cancellationToken = default)
    {
        // For non-streaming responses, we just pass through to the agent
        return await agent.GenerateReplyAsync(context.Messages, context.Options, cancellationToken);
    }

    /// <summary>
    /// Invokes the middleware for streaming scenarios, joining update messages into larger messages.
    /// </summary>
    public async Task<IAsyncEnumerable<IMessage>> InvokeStreamingAsync(
        MiddlewareContext context,
        IStreamingAgent agent,
        CancellationToken cancellationToken = default)
    {
        var sourceStream = await agent.GenerateReplyStreamingAsync(context.Messages, context.Options, cancellationToken);
        return TransformStreamWithBuilder(sourceStream, cancellationToken);
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
            var processedMessage = await ProcessStreamingMessage(message, builders);
            
            // Only emit the message if it's not an update message or if we're preserving update messages
            bool isUpdateMessage = message.GetType().Name.Contains("Update");
            if (_preserveUpdateMessages || !isUpdateMessage)
            {
                yield return processedMessage;
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
        if (messageType.Name.Contains("Update"))
        {
            // Find the corresponding builder for this update type
            foreach (var kvp in builders)
            {
                // Use the existing IMessageBuilder interface with a safer approach that doesn't rely on reflection
                // for the Build method
                if (kvp.Value is IMessageBuilder<IMessage, IMessage> builder)
                {
                    // Check if this builder is appropriate for the message type
                    if (builder.GetType().GetInterfaces()
                           .Any(i => i.IsGenericType && 
                                i.GetGenericArguments().Any(arg => arg.Name == messageType.Name)))
                    {
                        var builtMessage = builder.Build();
                        // Remove the builder since we're done with it
                        builders.Remove(kvp.Key);
                        return Task.FromResult(builtMessage);
                    }
                }
            }
        }
        
        // No pending builder to complete or couldn't build message
        return Task.FromResult<IMessage>(new TextMessage { Text = string.Empty, Role = Role.System });
    }

    private Task<IMessage> ProcessStreamingMessage(IMessage message, Dictionary<Type, object> builders)
    {
        // Handle tool call updates (ToolsCallUpdateMessage)
        if (message is ToolsCallUpdateMessage toolCallUpdate)
        {
            return ProcessToolCallUpdate(toolCallUpdate, builders);
        }
        
        // For text update messages - assume there's a TextUpdateMessage similar to ToolsCallUpdateMessage
        if (message.GetType().Name.Contains("TextUpdate"))
        {
            return ProcessTextUpdate(message, builders);
        }
        
        // For all other message types, pass through as-is
        return Task.FromResult(message);
    }

    private Task<IMessage> ProcessToolCallUpdate(
        ToolsCallUpdateMessage toolCallUpdate, 
        Dictionary<Type, object> builders)
    {
        Type builderKey = typeof(ToolsCallMessage);
        
        if (!builders.TryGetValue(builderKey, out var builderObj))
        {
            // Create a new builder for the first update
            var builder = new ToolsCallMessageBuilder
            {
                FromAgent = toolCallUpdate.FromAgent,
                Role = toolCallUpdate.Role
            };
            builders[builderKey] = builder;
            builder.Add(toolCallUpdate);
            // Return the original update for the first time
            return Task.FromResult<IMessage>(toolCallUpdate);
        }
        else
        {
            // Add to existing builder
            var builder = (ToolsCallMessageBuilder)builderObj;
            builder.Add(toolCallUpdate);
            // Return the current accumulated state
            return Task.FromResult<IMessage>(builder.Build());
        }
    }

    private Task<IMessage> ProcessTextUpdate(IMessage textUpdate, Dictionary<Type, object> builders)
    {
        // This would handle TextUpdateMessage if it exists
        // Similar to ProcessToolCallUpdate but for text updates
        // For now just returning the original message
        return Task.FromResult(textUpdate);
    }

    private IEnumerable<IMessage> ProcessFinalBuiltMessages(Dictionary<Type, object> builders)
    {
        // Process all accumulated builders and emit final complete messages
        foreach (var builderEntry in builders)
        {
            if (builderEntry.Value is IMessageBuilder<IMessage, IMessage> builder)
            {
                var builtMessage = builder.Build();
                if (builtMessage != null)
                {
                    yield return builtMessage;
                }
            }
        }
    }
}
