using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using System.Collections.Immutable;
using AchieveAi.LmDotnetTools.LmCore.Utils;

namespace AchieveAi.LmDotnetTools.LmCore.Middleware;

/// <summary>
/// Middleware that joins update messages into larger messages for more efficient processing.
/// </summary>
public class MessageUpdateJoinerMiddleware : IStreamingMiddleware
{
    /// <summary>
    /// Initializes a new instance of the <see cref="MessageUpdateJoinerMiddleware"/> class.
    /// </summary>
    /// <param name="name">Optional name for the middleware.</param>
    /// 
    public MessageUpdateJoinerMiddleware(
        string? name = null)
    {
        Name = name ?? nameof(MessageUpdateJoinerMiddleware);
    }

    /// <summary>
    /// Gets the name of the middleware.
    /// </summary>
    public string? Name { get; }

    /// <summary>
    /// Invokes the middleware for synchronous scenarios.
    /// </summary>
    public async Task<IEnumerable<IMessage>> InvokeAsync(
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
        // Track a single active builder instead of a dictionary
        IMessageBuilder? activeBuilder = null;
        Type? activeBuilderType = null;
        Type? lastMessageType = null;
        
        // Use the usage accumulator to track usage data
        var usageAccumulator = new UsageAccumulator();
        
        await foreach (var message in sourceStream.WithCancellation(cancellationToken))
        {
            // If we receive a usage message, store it to emit at the end
            if (message is UsageMessage usage)
            {
                usageAccumulator.AddUsageFromMessage(usage);
                continue; // Don't yield usage message yet
            }
            
            // Check if the message has usage in metadata (legacy support)
            if (message.Metadata != null && message.Metadata.ContainsKey("usage"))
            {
                usageAccumulator.AddUsageFromMessageMetadata(message);
            }
            
            // Check if we're switching message types and need to complete current builder
            if (lastMessageType != null && lastMessageType != message.GetType() && activeBuilder != null)
            {
                // Complete the previous builder before processing the new message
                var builtMessage = activeBuilder.Build();
                activeBuilder = null;
                activeBuilderType = null;
                yield return builtMessage;
            }
            
            // Update last message type
            lastMessageType = message.GetType();
            
            // Process the current message
            var processedMessage = ProcessStreamingMessage(
                message,
                ref activeBuilder,
                ref activeBuilderType);
            
            // Only emit the message if it's not an update message
            bool isUpdateMessage = message.GetType().Name.Contains("Update");
            if (!isUpdateMessage)
            {
                yield return processedMessage;
            }
        }
        
        // Process final built message at the end of the stream
        if (activeBuilder != null)
        {
            yield return activeBuilder.Build();
        }
        
        // Emit accumulated usage message at the end if we have one
        var finalUsageMessage = usageAccumulator.CreateUsageMessage();
        if (finalUsageMessage != null)
        {
            yield return finalUsageMessage;
        }
    }

    private IMessage ProcessStreamingMessage(
        IMessage message,
        ref IMessageBuilder? activeBuilder,
        ref Type? activeBuilderType)
    {
        // Handle tool call updates (ToolsCallUpdateMessage)
        if (message is ToolsCallUpdateMessage toolCallUpdate)
        {
            return ProcessToolCallUpdate(toolCallUpdate, ref activeBuilder, ref activeBuilderType);
        }
        // For text update messages
        else if (message is TextUpdateMessage textUpdate)
        {
            return ProcessTextUpdate(textUpdate, ref activeBuilder, ref activeBuilderType);
        }

        return message;
    }

    private IMessage ProcessToolCallUpdate(
        ToolsCallUpdateMessage toolCallUpdate, 
        ref IMessageBuilder? activeBuilder,
        ref Type? activeBuilderType)
    {
        Type builderType = typeof(ToolsCallMessage);
        
        if (activeBuilder == null || activeBuilderType != builderType)
        {
            // Create a new builder for the first update
            var builder = new ToolsCallMessageBuilder
            {
                FromAgent = toolCallUpdate.FromAgent,
                Role = toolCallUpdate.Role
            };
            activeBuilder = builder;
            activeBuilderType = builderType;
            builder.Add(toolCallUpdate);
            // Return the original update for the first time
            return toolCallUpdate;
        }
        else
        {
            // Add to existing builder
            var builder = (ToolsCallMessageBuilder)activeBuilder;
            builder.Add(toolCallUpdate);
            return toolCallUpdate;
        }
    }

    private static IMessage ProcessTextUpdate(
        TextUpdateMessage textUpdateMessage,
        ref IMessageBuilder? activeBuilder,
        ref Type? activeBuilderType)
    {
        Type builderType = typeof(TextMessage);
        
        if (activeBuilder == null || activeBuilderType != builderType)
        {
            // Create a new builder for the first update
            var builder = new TextMessageBuilder
            {
                FromAgent = textUpdateMessage.FromAgent,
                Role = textUpdateMessage.Role,
                GenerationId = textUpdateMessage.GenerationId
            };
            activeBuilder = builder;
            activeBuilderType = builderType;
            
            // Convert the update to a TextMessage for the builder
            builder.Add(textUpdateMessage);
            
            // Return the original update for the first time
            return textUpdateMessage;
        }
        else
        {
            // Add to existing builder
            var builder = (TextMessageBuilder)activeBuilder;
            
            // Convert the update to a TextMessage for the builder
            builder.Add(textUpdateMessage);
            
            // Return the current accumulated state
            return builder.Build();
        }
    }
}
