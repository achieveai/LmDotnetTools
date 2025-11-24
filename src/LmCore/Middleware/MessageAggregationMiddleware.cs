using System.Collections.Immutable;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AchieveAi.LmDotnetTools.LmCore.Middleware;

/// <summary>
/// Middleware that reconstructs CompositeMessage and ToolsCallAggregateMessage from ordered message streams.
/// This enables backward compatibility with provider transformation code that expects aggregated messages.
/// Should be positioned before OpenAI/Anthropic agents in the middleware pipeline.
/// </summary>
public class MessageAggregationMiddleware : IStreamingMiddleware
{
    private readonly ILogger<MessageAggregationMiddleware> _logger;

    /// <summary>
    /// Creates a new instance of MessageAggregationMiddleware
    /// </summary>
    /// <param name="name">Optional name for this middleware instance</param>
    /// <param name="logger">Optional logger</param>
    public MessageAggregationMiddleware(
        string? name = null,
        ILogger<MessageAggregationMiddleware>? logger = null
    )
    {
        _logger = logger ?? NullLogger<MessageAggregationMiddleware>.Instance;
        Name = name ?? nameof(MessageAggregationMiddleware);
    }

    public string? Name { get; }

    public async Task<IEnumerable<IMessage>> InvokeAsync(
        MiddlewareContext context,
        IAgent agent,
        CancellationToken cancellationToken = default
    )
    {
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug(
                "Reconstructing aggregates from {MessageCount} messages",
                context.Messages.Count()
            );
        }

        // Reconstruct aggregates from ordered messages
        var reconstructedMessages = MessageAggregationMiddleware.ReconstructAggregates(context.Messages);

        // Create new context with reconstructed messages
        var modifiedContext = context with { Messages = reconstructedMessages };

        // Generate reply with the modified context
        ArgumentNullException.ThrowIfNull(agent);
        var replies = await agent.GenerateReplyAsync(modifiedContext.Messages, modifiedContext.Options, cancellationToken);

        return replies;
    }

    public async Task<IAsyncEnumerable<IMessage>> InvokeStreamingAsync(
        MiddlewareContext context,
        IStreamingAgent agent,
        CancellationToken cancellationToken = default
    )
    {
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug(
                "Reconstructing aggregates from {MessageCount} messages for streaming",
                context.Messages.Count()
            );
        }

        // Reconstruct aggregates from ordered messages
        var reconstructedMessages = MessageAggregationMiddleware.ReconstructAggregates(context.Messages);

        // Create new context with reconstructed messages
        var modifiedContext = context with { Messages = reconstructedMessages };

        // Get the streaming response from the agent
        ArgumentNullException.ThrowIfNull(agent);
        var streamingResponse = await agent.GenerateReplyStreamingAsync(
            modifiedContext.Messages,
            modifiedContext.Options,
            cancellationToken
        );

        return streamingResponse;
    }

    /// <summary>
    /// Reconstructs CompositeMessage and ToolsCallAggregateMessage from ordered message stream
    /// </summary>
    private static IEnumerable<IMessage> ReconstructAggregates(IEnumerable<IMessage> messages)
    {
        var result = new List<IMessage>();
        var messageList = messages.ToList();

        // Group consecutive messages by GenerationId
        var groups = MessageAggregationMiddleware.GroupByGeneration(messageList);

        foreach (var group in groups)
        {
            // Check if this group can be reconstructed into a ToolsCallAggregateMessage
            var aggregate = MessageAggregationMiddleware.TryCreateToolCallAggregate(group);
            if (aggregate != null)
            {
                result.Add(aggregate);
                continue;
            }

            // Check if this group has multiple messages that should be composed
            if (group.Count > 1 && group.All(m => m.GenerationId == group[0].GenerationId))
            {
                // Create CompositeMessage for messages with same GenerationId
                var composite = MessageAggregationMiddleware.CreateCompositeMessage(group);
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
}
