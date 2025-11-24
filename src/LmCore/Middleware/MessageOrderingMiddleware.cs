using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AchieveAi.LmDotnetTools.LmCore.Middleware;

/// <summary>
/// Middleware that assigns messageOrderIdx to messages within the same generation.
/// This middleware should be positioned immediately after the provider agent and before MessageAggregationMiddleware.
/// It assigns sequential indices (0, 1, 2...) to messages with the same GenerationId.
/// </summary>
public class MessageOrderingMiddleware : IStreamingMiddleware
{
    private readonly ILogger<MessageOrderingMiddleware> _logger;

    /// <summary>
    /// Creates a new instance of MessageOrderingMiddleware
    /// </summary>
    /// <param name="name">Optional name for this middleware instance</param>
    /// <param name="logger">Optional logger</param>
    public MessageOrderingMiddleware(
        string? name = null,
        ILogger<MessageOrderingMiddleware>? logger = null
    )
    {
        _logger = logger ?? NullLogger<MessageOrderingMiddleware>.Instance;
        Name = name ?? nameof(MessageOrderingMiddleware);
    }

    public string? Name { get; }

    public async Task<IEnumerable<IMessage>> InvokeAsync(
        MiddlewareContext context,
        IAgent agent,
        CancellationToken cancellationToken = default
    )
    {
        // Generate reply from the agent
        ArgumentNullException.ThrowIfNull(agent);
        var replies = await agent.GenerateReplyAsync(context.Messages, context.Options, cancellationToken);

        // Assign message ordering
        var orderedMessages = MessageOrderingMiddleware.AssignMessageOrdering(replies);

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug(
                "Assigned messageOrderIdx to {MessageCount} messages",
                orderedMessages.Count()
            );
        }

        return orderedMessages;
    }

    public async Task<IAsyncEnumerable<IMessage>> InvokeStreamingAsync(
        MiddlewareContext context,
        IStreamingAgent agent,
        CancellationToken cancellationToken = default
    )
    {
        // Get the streaming response from the agent
        ArgumentNullException.ThrowIfNull(agent);
        var streamingResponse = await agent.GenerateReplyStreamingAsync(
            context.Messages,
            context.Options,
            cancellationToken
        );

        // Wrap the async enumerable to assign ordering on the fly
        return MessageOrderingMiddleware.AssignMessageOrderingStreaming(streamingResponse);
    }

    /// <summary>
    /// Assigns messageOrderIdx to messages with the same GenerationId
    /// </summary>
    private static IEnumerable<IMessage> AssignMessageOrdering(IEnumerable<IMessage> messages)
    {
        var messageList = messages.ToList();
        var orderIndexByGeneration = new Dictionary<string, int>();

        foreach (var message in messageList)
        {
            // Only assign ordering to messages with a GenerationId
            if (string.IsNullOrEmpty(message.GenerationId))
            {
                yield return message;
                continue;
            }

            var generationId = message.GenerationId;

            // Get or initialize the order index for this generation
            if (!orderIndexByGeneration.ContainsKey(generationId))
            {
                orderIndexByGeneration[generationId] = 0;
            }

            var orderIdx = orderIndexByGeneration[generationId]++;

            // Create a new message with the assigned messageOrderIdx
            yield return message switch
            {
                TextMessage m => m with { MessageOrderIdx = orderIdx },
                TextUpdateMessage m => m with { MessageOrderIdx = orderIdx },
                ReasoningMessage m => m with { MessageOrderIdx = orderIdx },
                ReasoningUpdateMessage m => m with { MessageOrderIdx = orderIdx },
                ImageMessage m => new ImageMessage
                {
                    Role = m.Role,
                    ImageData = m.ImageData,
                    FromAgent = m.FromAgent,
                    GenerationId = m.GenerationId,
                    Metadata = m.Metadata,
                    ThreadId = m.ThreadId,
                    RunId = m.RunId,
                    MessageOrderIdx = orderIdx,
                },
                ToolsCallMessage m => m with { MessageOrderIdx = orderIdx },
                ToolsCallUpdateMessage m => m with { MessageOrderIdx = orderIdx },
                ToolsCallResultMessage m => m with { MessageOrderIdx = orderIdx },
                UsageMessage m => m with { MessageOrderIdx = orderIdx },
                CompositeMessage m => new CompositeMessage
                {
                    Role = m.Role,
                    FromAgent = m.FromAgent,
                    GenerationId = m.GenerationId,
                    Metadata = m.Metadata,
                    Messages = m.Messages,
                    ThreadId = m.ThreadId,
                    RunId = m.RunId,
                    MessageOrderIdx = orderIdx,
                },
                ToolsCallAggregateMessage m => new ToolsCallAggregateMessage(
                    m.ToolsCallMessage with { MessageOrderIdx = orderIdx },
                    m.ToolsCallResult,
                    m.FromAgent
                ),
                _ => message, // Unknown message type, pass through unchanged
            };
        }
    }

    /// <summary>
    /// Assigns messageOrderIdx to streaming messages on the fly
    /// </summary>
    private static async IAsyncEnumerable<IMessage> AssignMessageOrderingStreaming(
        IAsyncEnumerable<IMessage> messages
    )
    {
        var orderIndexByGeneration = new Dictionary<string, int>();

        await foreach (var message in messages)
        {
            // Only assign ordering to messages with a GenerationId
            if (string.IsNullOrEmpty(message.GenerationId))
            {
                yield return message;
                continue;
            }

            var generationId = message.GenerationId;

            // Get or initialize the order index for this generation
            if (!orderIndexByGeneration.ContainsKey(generationId))
            {
                orderIndexByGeneration[generationId] = 0;
            }

            var orderIdx = orderIndexByGeneration[generationId]++;

            // Create a new message with the assigned messageOrderIdx
            yield return message switch
            {
                TextMessage m => m with { MessageOrderIdx = orderIdx },
                TextUpdateMessage m => m with { MessageOrderIdx = orderIdx },
                ReasoningMessage m => m with { MessageOrderIdx = orderIdx },
                ReasoningUpdateMessage m => m with { MessageOrderIdx = orderIdx },
                ImageMessage m => new ImageMessage
                {
                    Role = m.Role,
                    ImageData = m.ImageData,
                    FromAgent = m.FromAgent,
                    GenerationId = m.GenerationId,
                    Metadata = m.Metadata,
                    ThreadId = m.ThreadId,
                    RunId = m.RunId,
                    MessageOrderIdx = orderIdx,
                },
                ToolsCallMessage m => m with { MessageOrderIdx = orderIdx },
                ToolsCallUpdateMessage m => m with { MessageOrderIdx = orderIdx },
                ToolsCallResultMessage m => m with { MessageOrderIdx = orderIdx },
                UsageMessage m => m with { MessageOrderIdx = orderIdx },
                CompositeMessage m => new CompositeMessage
                {
                    Role = m.Role,
                    FromAgent = m.FromAgent,
                    GenerationId = m.GenerationId,
                    Metadata = m.Metadata,
                    Messages = m.Messages,
                    ThreadId = m.ThreadId,
                    RunId = m.RunId,
                    MessageOrderIdx = orderIdx,
                },
                ToolsCallAggregateMessage m => new ToolsCallAggregateMessage(
                    m.ToolsCallMessage with { MessageOrderIdx = orderIdx },
                    m.ToolsCallResult,
                    m.FromAgent
                ),
                _ => message, // Unknown message type, pass through unchanged
            };
        }
    }
}
