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
        ArgumentNullException.ThrowIfNull(agent, nameof(agent));

        // UPSTREAM: Reconstruct aggregates for provider
        var aggregatedMessages = ReconstructAggregates(context.Messages);

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
        var replies = await agent.GenerateReplyAsync(modifiedContext.Messages, modifiedContext.Options, cancellationToken);

        // DOWNSTREAM: Assign message ordering to replies
        var orderedReplies = AssignMessageOrdering(replies);

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
    /// Tracking state for message and chunk indices per generation
    /// </summary>
    private class OrderingState
    {
        public Dictionary<string, int> MessageOrderByGeneration { get; } = [];
        public Dictionary<string, int> ChunkIdxByGeneration { get; } = [];
        /// <summary>
        /// Tracks the current message identity to detect when we need to start a new message.
        /// Identity is message type for most updates, or "tool_call_update_{toolCallId}" for tool call updates.
        /// </summary>
        public Dictionary<string, string?> CurrentMessageIdentity { get; } = [];
    }

    /// <summary>
    /// Processes a single message and assigns messageOrderIdx and chunkIdx.
    /// Yields one or more messages (e.g., expanding plural messages into singular ones).
    /// </summary>
    private static IEnumerable<IMessage> ProcessMessageForOrdering(
        IMessage message,
        OrderingState state
    )
    {
        // Only assign ordering to messages with a GenerationId
        if (string.IsNullOrEmpty(message.GenerationId))
        {
            yield return message;
            yield break;
        }

        var generationId = message.GenerationId;

        // Initialize state for this generation if needed
        if (!state.MessageOrderByGeneration.ContainsKey(generationId))
        {
            state.MessageOrderByGeneration[generationId] = -1; // Start at -1 so first increment gives 0
            state.ChunkIdxByGeneration[generationId] = 0;
            state.CurrentMessageIdentity[generationId] = null;
        }

        // Local helper: Gets current indices from state
        (int orderIdx, int chunkIdx) GetCurrentIndices()
        {
            return (state.MessageOrderByGeneration[generationId], state.ChunkIdxByGeneration[generationId]);
        }

        // Local helper: Starts a new message by incrementing messageOrderIdx and resetting chunkIdx
        void StartNewMessage(string? newIdentity = null)
        {
            state.MessageOrderByGeneration[generationId]++;
            state.ChunkIdxByGeneration[generationId] = 0;
            state.CurrentMessageIdentity[generationId] = newIdentity;
        }

        // Local helper: Increments chunkIdx for continuing the current message
        void IncrementChunk()
        {
            state.ChunkIdxByGeneration[generationId]++;
        }

        // Local helper: Checks if we should start a new message based on identity change
        void CheckAndHandleIdentityChange(string newIdentity)
        {
            var currentIdentity = state.CurrentMessageIdentity[generationId];
            if (currentIdentity != newIdentity)
            {
                // Identity changed or first message with this identity - start new message
                StartNewMessage(newIdentity);
            }
        }

        // Process message and assign indices
        // For plural message types, convert them to singular messages
        switch (message)
        {
            case TextMessage m:
                StartNewMessage();
                var (textOrderIdx, _) = GetCurrentIndices();
                yield return m with { MessageOrderIdx = textOrderIdx };
                break;

            case TextUpdateMessage m:
                // Check if message type changed, which triggers new message
                CheckAndHandleIdentityChange("text_update");
                var (textUpdateOrderIdx, textUpdateChunkIdx) = GetCurrentIndices();
                yield return m with { MessageOrderIdx = textUpdateOrderIdx, ChunkIdx = textUpdateChunkIdx };
                IncrementChunk();
                break;

            case ReasoningMessage m:
                StartNewMessage();
                var (reasoningOrderIdx, _) = GetCurrentIndices();
                yield return m with { MessageOrderIdx = reasoningOrderIdx };
                break;

            case ReasoningUpdateMessage m:
                // Check if message type changed, which triggers new message
                CheckAndHandleIdentityChange("reasoning_update");
                var (reasoningUpdateOrderIdx, reasoningUpdateChunkIdx) = GetCurrentIndices();
                yield return m with { MessageOrderIdx = reasoningUpdateOrderIdx, ChunkIdx = reasoningUpdateChunkIdx };
                IncrementChunk();
                break;

            case ImageMessage m:
                StartNewMessage();
                var (imageOrderIdx, _) = GetCurrentIndices();
                yield return new ImageMessage
                {
                    Role = m.Role,
                    ImageData = m.ImageData,
                    FromAgent = m.FromAgent,
                    GenerationId = m.GenerationId,
                    Metadata = m.Metadata,
                    ThreadId = m.ThreadId,
                    RunId = m.RunId,
                    MessageOrderIdx = imageOrderIdx,
                };
                break;

            case ToolsCallMessage m:
                // Convert ToolsCallMessage (plural) into individual ToolCallMessage (singular) instances
                foreach (var toolCall in m.ToolCalls)
                {
                    StartNewMessage();
                    var (toolCallOrderIdx, _) = GetCurrentIndices();
                    yield return new ToolCallMessage
                    {
                        FunctionName = toolCall.FunctionName,
                        FunctionArgs = toolCall.FunctionArgs,
                        Index = toolCall.Index,
                        ToolCallId = toolCall.ToolCallId,
                        ToolCallIdx = toolCall.ToolCallIdx,
                        Role = m.Role,
                        FromAgent = m.FromAgent,
                        GenerationId = m.GenerationId,
                        Metadata = m.Metadata,
                        ThreadId = m.ThreadId,
                        RunId = m.RunId,
                        ParentRunId = m.ParentRunId,
                        MessageOrderIdx = toolCallOrderIdx,
                    };
                }
                break;

            case ToolsCallUpdateMessage m:
                // Convert ToolsCallUpdateMessage (plural) into individual ToolCallUpdateMessage (singular) instances
                // Note: typically contains a single delta during streaming
                foreach (var update in m.ToolCallUpdates)
                {
                    // Identity based on tool call ID - different tool calls get different messages
                    var toolCallIdentity = $"tool_call_update_{update.ToolCallId ?? update.Index?.ToString() ?? "unknown"}";
                    CheckAndHandleIdentityChange(toolCallIdentity);

                    var (toolCallUpdateOrderIdx, toolCallUpdateChunkIdx) = GetCurrentIndices();

                    yield return new ToolCallUpdateMessage
                    {
                        ToolCallId = update.ToolCallId,
                        Index = update.Index,
                        FunctionName = update.FunctionName,
                        FunctionArgs = update.FunctionArgs,
                        JsonFragmentUpdates = update.JsonFragmentUpdates,
                        Role = m.Role,
                        FromAgent = m.FromAgent,
                        GenerationId = m.GenerationId,
                        Metadata = m.Metadata,
                        ThreadId = m.ThreadId,
                        RunId = m.RunId,
                        ParentRunId = m.ParentRunId,
                        MessageOrderIdx = toolCallUpdateOrderIdx,
                        ChunkIdx = toolCallUpdateChunkIdx,
                    };

                    IncrementChunk();
                }
                break;

            case ToolsCallResultMessage m:
                // Convert ToolsCallResultMessage (plural) into individual ToolCallResultMessage (singular) instances
                foreach (var result in m.ToolCallResults)
                {
                    StartNewMessage();
                    var (toolCallResultOrderIdx, _) = GetCurrentIndices();
                    yield return new ToolCallResultMessage
                    {
                        ToolCallId = result.ToolCallId,
                        Result = result.Result,
                        Role = m.Role,
                        FromAgent = m.FromAgent,
                        GenerationId = m.GenerationId,
                        Metadata = m.Metadata,
                        ThreadId = m.ThreadId,
                        RunId = m.RunId,
                        MessageOrderIdx = toolCallResultOrderIdx,
                    };
                }
                break;

            case UsageMessage m:
                StartNewMessage();
                var (usageOrderIdx, _) = GetCurrentIndices();
                yield return m with { MessageOrderIdx = usageOrderIdx };
                break;

            case CompositeMessage:
                throw new NotSupportedException(
                    "CompositeMessage should not appear when assigning message orderings. " +
                    "The downstream flow expects individual messages, not composites."
                );

            case ToolsCallAggregateMessage:
                throw new NotSupportedException(
                    "ToolsCallAggregateMessage should not appear when assigning message orderings. " +
                    "The downstream flow expects individual messages, not aggregates."
                );

            default:
                // Unknown message type, pass through unchanged
                StartNewMessage();
                yield return message;
                break;
        }
    }

    /// <summary>
    /// Assigns messageOrderIdx and chunkIdx to messages with the same GenerationId
    /// </summary>
    private static IEnumerable<IMessage> AssignMessageOrdering(IEnumerable<IMessage> messages)
    {
        var state = new OrderingState();

        foreach (var message in messages)
        {
            foreach (var processedMessage in ProcessMessageForOrdering(message, state))
            {
                yield return processedMessage;
            }
        }
    }

    /// <summary>
    /// Assigns messageOrderIdx and chunkIdx to streaming messages on the fly
    /// </summary>
    private static async IAsyncEnumerable<IMessage> AssignMessageOrderingStreaming(
        IAsyncEnumerable<IMessage> messages
    )
    {
        var state = new OrderingState();

        await foreach (var message in messages)
        {
            foreach (var processedMessage in ProcessMessageForOrdering(message, state))
            {
                yield return processedMessage;
            }
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
        var groups = GroupByGeneration(messageList);

        foreach (var group in groups)
        {
            // First, aggregate singular tool messages into plural versions
            // This converts ToolCallMessage[] → ToolsCallMessage and ToolCallResultMessage[] → ToolsCallResultMessage
            var aggregatedGroup = AggregateToolMessages(group);

            // Check if this group can be reconstructed into a ToolsCallAggregateMessage
            var aggregate = TryCreateToolCallAggregate(aggregatedGroup);
            if (aggregate != null)
            {
                result.Add(aggregate);
                continue;
            }

            // Check if this group has multiple messages that should be composed
            if (aggregatedGroup.Count > 1 && aggregatedGroup.All(m => m.GenerationId == aggregatedGroup[0].GenerationId))
            {
                // Create CompositeMessage for messages with same GenerationId
                var composite = CreateCompositeMessage(aggregatedGroup);
                result.Add(composite);
            }
            else
            {
                // Add messages individually if they don't need aggregation
                result.AddRange(aggregatedGroup);
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
                currentGroup = new List<IMessage> { message };
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
    /// Aggregates singular tool messages into plural versions.
    /// Converts multiple ToolCallMessage instances into a single ToolsCallMessage,
    /// and multiple ToolCallResultMessage instances into a single ToolsCallResultMessage.
    /// Preserves MessageOrderIdx ordering throughout.
    /// </summary>
    private static List<IMessage> AggregateToolMessages(List<IMessage> group)
    {
        // If group is empty, nothing to do
        if (group.Count == 0)
        {
            return group;
        }

        // Sort by MessageOrderIdx first to maintain order
        var sorted = group.OrderBy(m => m.MessageOrderIdx ?? int.MaxValue).ToList();

        var result = new List<IMessage>();
        var toolCallMessages = new List<ToolCallMessage>();
        var toolCallResultMessages = new List<ToolCallResultMessage>();

        // Separate tool messages from other messages, preserving order
        foreach (var message in sorted)
        {
            switch (message)
            {
                case ToolCallMessage tcm:
                    toolCallMessages.Add(tcm);
                    break;
                case ToolCallResultMessage tcrm:
                    toolCallResultMessages.Add(tcrm);
                    break;
                default:
                    result.Add(message);
                    break;
            }
        }

        // Aggregate ToolCallMessages into ToolsCallMessage
        if (toolCallMessages.Count > 0)
        {
            var firstToolCall = toolCallMessages[0];

            // Convert each ToolCallMessage to ToolCall
            // ToolCallMessage inherits from ToolCall, so we can create ToolCall from it
            var toolCalls = toolCallMessages
                .Select(tcm => new ToolCall
                {
                    FunctionName = tcm.FunctionName,
                    FunctionArgs = tcm.FunctionArgs,
                    Index = tcm.Index,
                    ToolCallId = tcm.ToolCallId,
                    ToolCallIdx = tcm.ToolCallIdx
                })
                .ToImmutableList();

            var toolsCallMessage = new ToolsCallMessage
            {
                ToolCalls = toolCalls,
                Role = firstToolCall.Role,
                FromAgent = firstToolCall.FromAgent,
                GenerationId = firstToolCall.GenerationId,
                Metadata = firstToolCall.Metadata,
                ThreadId = firstToolCall.ThreadId,
                RunId = firstToolCall.RunId,
                ParentRunId = firstToolCall.ParentRunId,
                MessageOrderIdx = firstToolCall.MessageOrderIdx,
            };

            result.Add(toolsCallMessage);
        }

        // Aggregate ToolCallResultMessages into ToolsCallResultMessage
        if (toolCallResultMessages.Count > 0)
        {
            var firstResult = toolCallResultMessages[0];

            // Convert each ToolCallResultMessage to ToolCallResult
            var toolCallResults = toolCallResultMessages
                .Select(tcrm => new ToolCallResult(tcrm.ToolCallId, tcrm.Result))
                .ToImmutableList();

            var toolsCallResultMessage = new ToolsCallResultMessage
            {
                ToolCallResults = toolCallResults,
                Role = firstResult.Role,
                FromAgent = firstResult.FromAgent,
                GenerationId = firstResult.GenerationId,
                Metadata = firstResult.Metadata,
                ThreadId = firstResult.ThreadId,
                RunId = firstResult.RunId,
                MessageOrderIdx = firstResult.MessageOrderIdx,
            };

            result.Add(toolsCallResultMessage);
        }

        // Re-sort result by MessageOrderIdx to maintain order
        return result.OrderBy(m => m.MessageOrderIdx ?? int.MaxValue).ToList();
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
        if (toolCallMessage != null && toolCallResult != null)
        {
            return new ToolsCallAggregateMessage(
                toolCallMessage,
                toolCallResult,
                toolCallMessage.FromAgent
            );
        }

        return null;
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
