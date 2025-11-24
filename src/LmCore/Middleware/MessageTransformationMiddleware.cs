using System.Collections.Immutable;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AchieveAi.LmDotnetTools.LmCore.Middleware;

/// <summary>
/// Bidirectional middleware that transforms messages based on direction:
/// - Downstream (Provider → Application): Assigns messageOrderIdx to messages
/// - Upstream (Application → Provider): Reconstructs CompositeMessage and ToolsCallAggregateMessage
///
/// This enables the new simplified message flow while maintaining backward compatibility
/// with provider transformations that expect aggregated messages.
/// </summary>
public class MessageTransformationMiddleware : IStreamingMiddleware
{
    private readonly ILogger<MessageTransformationMiddleware> _logger;

    /// <summary>
    /// Creates a new instance of MessageTransformationMiddleware
    /// </summary>
    /// <param name="name">Optional name for this middleware instance</param>
    /// <param name="logger">Optional logger</param>
    public MessageTransformationMiddleware(
        string? name = null,
        ILogger<MessageTransformationMiddleware>? logger = null
    )
    {
        _logger = logger ?? NullLogger<MessageTransformationMiddleware>.Instance;
        Name = name ?? nameof(MessageTransformationMiddleware);
    }

    public string? Name { get; }

    public async Task<IEnumerable<IMessage>> InvokeAsync(
        MiddlewareContext context,
        IAgent agent,
        CancellationToken cancellationToken = default
    )
    {
        // UPSTREAM: Reconstruct aggregates for provider
        var aggregatedMessages = MessageTransformationMiddleware.ReconstructAggregates(context.Messages);

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug(
                "Upstream transformation: Reconstructed aggregates from {OriginalCount} to {AggregatedCount} messages",
                context.Messages.Count(),
                aggregatedMessages.Count()
            );
        }

        // Create modified context with aggregated messages
        var modifiedContext = context with { Messages = aggregatedMessages };

        // Call agent with aggregated messages
        ArgumentNullException.ThrowIfNull(agent);
        var replies = await agent.GenerateReplyAsync(modifiedContext.Messages, modifiedContext.Options, cancellationToken);

        // DOWNSTREAM: Assign message ordering to replies
        var orderedReplies = MessageTransformationMiddleware.AssignMessageOrdering(replies);

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug(
                "Downstream transformation: Assigned messageOrderIdx to {MessageCount} messages",
                orderedReplies.Count()
            );
        }

        return orderedReplies;
    }

    public async Task<IAsyncEnumerable<IMessage>> InvokeStreamingAsync(
        MiddlewareContext context,
        IStreamingAgent agent,
        CancellationToken cancellationToken = default
    )
    {
        // UPSTREAM: Reconstruct aggregates for provider
        var aggregatedMessages = MessageTransformationMiddleware.ReconstructAggregates(context.Messages);

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug(
                "Upstream transformation (streaming): Reconstructed aggregates from {OriginalCount} to {AggregatedCount} messages",
                context.Messages.Count(),
                aggregatedMessages.Count()
            );
        }

        // Create modified context with aggregated messages
        var modifiedContext = context with { Messages = aggregatedMessages };

        // Call agent with aggregated messages
        ArgumentNullException.ThrowIfNull(agent);
        var streamingResponse = await agent.GenerateReplyStreamingAsync(
            modifiedContext.Messages,
            modifiedContext.Options,
            cancellationToken
        );

        // DOWNSTREAM: Assign message ordering to streaming replies
        return MessageTransformationMiddleware.AssignMessageOrderingStreaming(streamingResponse);
    }

    #region Downstream: Assign Message Ordering

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

    #endregion

    #region Upstream: Reconstruct Aggregates

    /// <summary>
    /// Reconstructs CompositeMessage and ToolsCallAggregateMessage from ordered message stream
    /// </summary>
    private static IEnumerable<IMessage> ReconstructAggregates(IEnumerable<IMessage> messages)
    {
        var result = new List<IMessage>();
        var messageList = messages.ToList();

        // Group consecutive messages by GenerationId
        var groups = MessageTransformationMiddleware.GroupByGeneration(messageList);

        foreach (var group in groups)
        {
            // Check if this group can be reconstructed into a ToolsCallAggregateMessage
            var aggregate = MessageTransformationMiddleware.TryCreateToolCallAggregate(group);
            if (aggregate != null)
            {
                result.Add(aggregate);
                continue;
            }

            // Check if this group has multiple messages that should be composed
            if (group.Count > 1 && group.All(m => m.GenerationId == group[0].GenerationId))
            {
                // Create CompositeMessage for messages with same GenerationId
                var composite = MessageTransformationMiddleware.CreateCompositeMessage(group);
                result.Add(composite);
            }
            else
            {
                // Add messages individually if they don't need aggregation
                result.AddRange(group);
            }
        }

        return result;
    }

    /// <summary>
    /// Groups consecutive messages by GenerationId, keeping messages without GenerationId separate
    /// </summary>
    private static List<List<IMessage>> GroupByGeneration(List<IMessage> messages)
    {
        var groups = new List<List<IMessage>>();
        var currentGroup = new List<IMessage>();
        string? currentGenerationId = null;

        foreach (var message in messages)
        {
            var messageGenerationId = message.GenerationId;

            // Start a new group if GenerationId changes or message has no GenerationId
            if (messageGenerationId == null || messageGenerationId != currentGenerationId)
            {
                // Save current group if it has messages
                if (currentGroup.Count > 0)
                {
                    groups.Add(currentGroup);
                }

                // Start new group
                currentGroup = [message];
                currentGenerationId = messageGenerationId;
            }
            else
            {
                // Add to current group
                currentGroup.Add(message);
            }
        }

        // Add the last group
        if (currentGroup.Count > 0)
        {
            groups.Add(currentGroup);
        }

        return groups;
    }

    /// <summary>
    /// Attempts to create a ToolsCallAggregateMessage if the group contains a ToolsCallMessage followed by ToolsCallResultMessage
    /// </summary>
    private static ToolsCallAggregateMessage? TryCreateToolCallAggregate(List<IMessage> group)
    {
        // Sort by MessageOrderIdx if available
        var sortedGroup = group
            .OrderBy(m => m.MessageOrderIdx ?? int.MaxValue)
            .ToList();

        // Look for ToolsCallMessage followed by ToolsCallResultMessage
        ToolsCallMessage? toolCallMessage = null;
        ToolsCallResultMessage? toolCallResult = null;

        foreach (var message in sortedGroup)
        {
            if (message is ToolsCallMessage tcm && toolCallMessage == null)
            {
                toolCallMessage = tcm;
            }
            else if (message is ToolsCallResultMessage tcrm && toolCallMessage != null && toolCallResult == null)
            {
                toolCallResult = tcrm;
            }
        }

        // If we found both, create aggregate
        return toolCallMessage != null && toolCallResult != null
            ? new ToolsCallAggregateMessage(
                toolCallMessage,
                toolCallResult,
                toolCallMessage.FromAgent
            )
            : null;
    }

    /// <summary>
    /// Creates a CompositeMessage from a group of messages
    /// </summary>
    private static CompositeMessage CreateCompositeMessage(List<IMessage> group)
    {
        // Sort by MessageOrderIdx
        var sortedMessages = group
            .OrderBy(m => m.MessageOrderIdx ?? int.MaxValue)
            .ToImmutableList();

        // Use properties from the first message
        var firstMessage = group[0];

        return new CompositeMessage
        {
            Messages = sortedMessages,
            Role = firstMessage.Role,
            FromAgent = firstMessage.FromAgent,
            GenerationId = firstMessage.GenerationId,
            Metadata = firstMessage.Metadata,
            ThreadId = firstMessage.ThreadId,
            RunId = firstMessage.RunId,
            MessageOrderIdx = firstMessage.MessageOrderIdx,
        };
    }

    #endregion
}
