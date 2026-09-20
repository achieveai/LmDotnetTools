using System.Collections.Immutable;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AchieveAi.LmDotnetTools.LmCore.Middleware;

/// <summary>
///     Bidirectional middleware that transforms messages based on direction:
///     - Downstream (Provider → Application): Assigns messageOrderIdx to messages
///     - Upstream (Application → Provider): Reconstructs CompositeMessage and ToolsCallAggregateMessage
///     This enables the new simplified message flow while maintaining backward compatibility
///     with provider transformations that expect aggregated messages.
/// </summary>
/// <remarks>
///     Composition order matters on the response path: this middleware MUST run before
///     <see cref="MessageUpdateJoinerMiddleware" /> (Transformation → Joiner). Transformation assigns
///     the messageOrderIdx that lets a finalizing TextMessage merge onto its streamed deltas; the
///     Joiner then suppresses the synthesized duplicate for history. Omitting or reordering these
///     two middlewares reintroduces duplicate assistant messages.
/// </remarks>
public class MessageTransformationMiddleware : IStreamingMiddleware
{
    private readonly ILogger<MessageTransformationMiddleware> _logger;

    /// <summary>
    ///     Creates a new instance of MessageTransformationMiddleware
    /// </summary>
    /// <param name="name">Optional name for this middleware instance</param>
    /// <param name="logger">Optional logger</param>
    public MessageTransformationMiddleware(string? name = null, ILogger<MessageTransformationMiddleware>? logger = null)
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
        var modifiedContext = context with
        {
            Messages = aggregatedMessages,
        };

        // Call agent with aggregated messages
        ArgumentNullException.ThrowIfNull(agent);
        var replies = await agent.GenerateReplyAsync(
            modifiedContext.Messages,
            modifiedContext.Options,
            cancellationToken
        );

        // DOWNSTREAM: Assign message ordering to replies
        var orderedReplies = AssignMessageOrdering(replies, _logger);

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
        ArgumentNullException.ThrowIfNull(agent, nameof(agent));

        // UPSTREAM: Reconstruct aggregates for provider
        var aggregatedMessages = ReconstructAggregates(context.Messages);

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug(
                "Upstream transformation (streaming): Reconstructed aggregates from {OriginalCount} to {AggregatedCount} messages",
                context.Messages.Count(),
                aggregatedMessages.Count()
            );
        }

        // Create modified context with aggregated messages
        var modifiedContext = context with
        {
            Messages = aggregatedMessages,
        };

        // Call agent with aggregated messages
        ArgumentNullException.ThrowIfNull(agent);
        var streamingResponse = await agent.GenerateReplyStreamingAsync(
            modifiedContext.Messages,
            modifiedContext.Options,
            cancellationToken
        );

        // DOWNSTREAM: Assign message ordering to streaming replies
        return AssignMessageOrderingStreaming(streamingResponse, _logger);
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
        OrderingState state,
        ILogger logger
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
        if (!state.MessageOrderByGeneration.TryGetValue(generationId, out var value))
        {
            value = -1;
            state.MessageOrderByGeneration[generationId] = value; // Start at -1 so first increment gives 0
            state.ChunkIdxByGeneration[generationId] = 0;
            state.CurrentMessageIdentity[generationId] = null;
        }

        // Local helper: Gets current indices from state
        (int orderIdx, int chunkIdx) GetCurrentIndices()
        {
            return (value, state.ChunkIdxByGeneration[generationId]);
        }

        // Local helper: Starts a new message by incrementing messageOrderIdx and resetting chunkIdx
        void StartNewMessage(string? newIdentity = null)
        {
            state.MessageOrderByGeneration[generationId] = ++value;
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
            case TextWithCitationsMessage m:
                // TextWithCitationsMessage replaces the accumulated TextUpdateMessage stream,
                // so it must share the same messageOrderIdx identity as "text_update"
                CheckAndHandleIdentityChange("text_update");
                var (citationsOrderIdx, _) = GetCurrentIndices();
                yield return m with
                {
                    MessageOrderIdx = citationsOrderIdx,
                };
                break;

            case TextMessage m:
                // A finalizing TextMessage consolidates the immediately-preceding TextUpdateMessage
                // stream for this generation, so it must reuse that stream's messageOrderIdx — same
                // as the TextWithCitationsMessage case above. Otherwise a consumer that merges by
                // (generationId, messageOrderIdx) cannot merge it onto the streamed message and
                // renders a duplicate text bubble. A standalone complete TextMessage (no open
                // text_update stream) still starts a new message.
                if (state.CurrentMessageIdentity[generationId] == "text_update")
                {
                    var (finalizedTextOrderIdx, _) = GetCurrentIndices();
                    // Close the stream so a subsequent message (even another TextMessage) starts fresh.
                    state.CurrentMessageIdentity[generationId] = null;
                    if (logger.IsEnabled(LogLevel.Debug))
                    {
                        logger.LogDebug(
                            "Finalizing TextMessage reuses streamed messageOrderIdx {MessageOrderIdx} for generation {GenerationId} (merged with its text_update stream rather than creating a duplicate)",
                            finalizedTextOrderIdx,
                            generationId
                        );
                    }

                    yield return m with
                    {
                        MessageOrderIdx = finalizedTextOrderIdx,
                    };
                }
                else
                {
                    StartNewMessage();
                    var (textOrderIdx, _) = GetCurrentIndices();
                    yield return m with
                    {
                        MessageOrderIdx = textOrderIdx,
                    };
                }

                break;

            case TextUpdateMessage m:
                // Check if message type changed, which triggers new message
                CheckAndHandleIdentityChange("text_update");
                var (textUpdateOrderIdx, textUpdateChunkIdx) = GetCurrentIndices();
                yield return m with
                {
                    MessageOrderIdx = textUpdateOrderIdx,
                    ChunkIdx = textUpdateChunkIdx,
                };
                IncrementChunk();
                break;

            case ReasoningMessage m:
                // Reasoning finalization is intentionally NOT merged onto its update stream here.
                // The OpenAI Responses reasoning item carries its content differently from the
                // streamed deltas, and merging caused thinking blocks to stop rendering. Reasoning
                // duplicate-display is already handled client-side, so a finalizing ReasoningMessage
                // starts its own message (original behavior).
                StartNewMessage();
                var (reasoningOrderIdx, _) = GetCurrentIndices();
                yield return m with
                {
                    MessageOrderIdx = reasoningOrderIdx,
                };
                break;

            case ReasoningUpdateMessage m:
                // Check if message type changed, which triggers new message
                CheckAndHandleIdentityChange("reasoning_update");
                var (reasoningUpdateOrderIdx, reasoningUpdateChunkIdx) = GetCurrentIndices();
                yield return m with
                {
                    MessageOrderIdx = reasoningUpdateOrderIdx,
                    ChunkIdx = reasoningUpdateChunkIdx,
                };
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
                        ExecutionTarget = toolCall.ExecutionTarget,
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
                    var toolCallIdentity =
                        $"tool_call_update_{update.ToolCallId ?? update.Index?.ToString() ?? "unknown"}";
                    CheckAndHandleIdentityChange(toolCallIdentity);

                    var (toolCallUpdateOrderIdx, toolCallUpdateChunkIdx) = GetCurrentIndices();

                    yield return new ToolCallUpdateMessage
                    {
                        ToolCallId = update.ToolCallId,
                        Index = update.Index,
                        FunctionName = update.FunctionName,
                        FunctionArgs = update.FunctionArgs,
                        ExecutionTarget = update.ExecutionTarget,
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

            case ToolCallUpdateMessage m:
                // Handle singular ToolCallUpdateMessage - track identity based on tool call ID
                var singularToolCallIdentity = $"tool_call_update_{m.ToolCallId ?? m.Index?.ToString() ?? "unknown"}";
                CheckAndHandleIdentityChange(singularToolCallIdentity);

                var (singularOrderIdx, singularChunkIdx) = GetCurrentIndices();
                yield return m with
                {
                    MessageOrderIdx = singularOrderIdx,
                    ChunkIdx = singularChunkIdx,
                };
                IncrementChunk();
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
                        ToolName = result.ToolName,
                        IsError = result.IsError,
                        ErrorCode = result.ErrorCode,
                        ExecutionTarget = result.ExecutionTarget,
                        IsTruncated = result.IsTruncated,
                        OriginalBytes = result.OriginalBytes,
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

            case ToolCallMessage m:
                StartNewMessage();
                var (stuOrderIdx, _) = GetCurrentIndices();
                yield return m with
                {
                    MessageOrderIdx = stuOrderIdx,
                };
                break;

            case ToolCallResultMessage m:
                StartNewMessage();
                var (strOrderIdx, _) = GetCurrentIndices();
                yield return m with
                {
                    MessageOrderIdx = strOrderIdx,
                };
                break;

            case UsageMessage m:
                StartNewMessage();
                var (usageOrderIdx, _) = GetCurrentIndices();
                yield return m with
                {
                    MessageOrderIdx = usageOrderIdx,
                };
                break;

            case CompositeMessage:
                throw new NotSupportedException(
                    "CompositeMessage should not appear when assigning message orderings. "
                        + "The downstream flow expects individual messages, not composites."
                );

            case ToolsCallAggregateMessage:
                throw new NotSupportedException(
                    "ToolsCallAggregateMessage should not appear when assigning message orderings. "
                        + "The downstream flow expects individual messages, not aggregates."
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
    private static IEnumerable<IMessage> AssignMessageOrdering(IEnumerable<IMessage> messages, ILogger logger)
    {
        var state = new OrderingState();

        foreach (var message in messages)
        {
            foreach (var processedMessage in ProcessMessageForOrdering(message, state, logger))
            {
                yield return processedMessage;
            }
        }
    }

    /// <summary>
    /// Assigns messageOrderIdx and chunkIdx to streaming messages on the fly
    /// </summary>
    private static async IAsyncEnumerable<IMessage> AssignMessageOrderingStreaming(
        IAsyncEnumerable<IMessage> messages,
        ILogger logger
    )
    {
        var state = new OrderingState();

        await foreach (var message in messages)
        {
            foreach (var processedMessage in ProcessMessageForOrdering(message, state, logger))
            {
                yield return processedMessage;
            }
        }
    }

    #endregion

    #region Upstream: Reconstruct Aggregates

    /// <summary>
    ///     Reconstructs CompositeMessage and ToolsCallAggregateMessage from ordered message stream
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
                // Collect non-tool messages from the group (e.g., ReasoningMessage, TextMessage)
                // that must be preserved alongside the tool call aggregate.
                var nonToolMessages = aggregatedGroup
                    .Where(m => m is not ToolsCallMessage and not ToolsCallResultMessage)
                    .ToList();

                if (nonToolMessages.Count > 0)
                {
                    // Re-sort: non-tool messages + aggregate, ordered by MessageOrderIdx
                    var combined = new List<IMessage>(nonToolMessages) { aggregate };
                    combined.Sort(
                        (a, b) => (a.MessageOrderIdx ?? int.MaxValue).CompareTo(b.MessageOrderIdx ?? int.MaxValue)
                    );

                    // Wrap in CompositeMessage since we have multiple messages with the same GenerationId
                    var composite = CreateCompositeMessage(combined);
                    result.Add(composite);
                }
                else
                {
                    result.Add(aggregate);
                }

                continue;
            }

            // Check if this group has multiple messages that should be composed
            if (
                aggregatedGroup.Count > 1
                && aggregatedGroup.All(m => m.GenerationId == aggregatedGroup[0].GenerationId)
            )
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
    ///     Groups consecutive messages by GenerationId, keeping messages without GenerationId separate
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
    /// Folds every tool message in a generation into at most one ToolsCallMessage and one
    /// ToolsCallResultMessage. Singular ToolCallMessage/ToolCallResultMessage instances are
    /// converted to their plural form, and several plural messages in the same generation are
    /// merged rather than left side by side. Preserves MessageOrderIdx ordering throughout.
    /// </summary>
    /// <remarks>
    /// The single-message-per-kind result is a precondition of
    /// <see cref="TryCreateToolCallAggregate" />, which only ever picks up the first call message
    /// and the first result message in a group. Anything it leaves behind is discarded by the
    /// caller, so a generation carrying two call messages used to lose the second one's calls
    /// while the matching results still reached the provider. That combination is rejected with
    /// "No tool call found for function call output with call_id ...", which no retry can clear.
    /// A generation can hold more than one message of a kind when history mixes singular and
    /// plural forms, or when two tool rounds were recorded under one GenerationId.
    /// </remarks>
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
        var toolCallSources = new List<IMessage>();
        var toolCallResultSources = new List<IMessage>();

        // Separate tool messages from other messages, preserving order
        foreach (var message in sorted)
        {
            switch (message)
            {
                case ToolCallMessage or ToolsCallMessage:
                    toolCallSources.Add(message);
                    break;
                case ToolCallResultMessage or ToolsCallResultMessage:
                    toolCallResultSources.Add(message);
                    break;
                default:
                    result.Add(message);
                    break;
            }
        }

        // Aggregate every tool call in the generation into one ToolsCallMessage
        if (toolCallSources.Count > 0)
        {
            // A lone plural message is already in the target shape; keep the instance as-is.
            result.Add(
                toolCallSources is [ToolsCallMessage onlyToolsCall]
                    ? onlyToolsCall
                    : MergeToolCallMessages(toolCallSources)
            );
        }

        // Aggregate every tool result in the generation into one ToolsCallResultMessage
        if (toolCallResultSources.Count > 0)
        {
            result.Add(
                toolCallResultSources is [ToolsCallResultMessage onlyToolsCallResult]
                    ? onlyToolsCallResult
                    : MergeToolCallResultMessages(toolCallResultSources)
            );
        }

        // Re-sort result by MessageOrderIdx to maintain order
        return [.. result.OrderBy(m => m.MessageOrderIdx ?? int.MaxValue)];
    }

    /// <summary>
    /// Merges call-bearing messages (singular or plural) into a single ToolsCallMessage,
    /// keeping the calls in source order and taking envelope properties from the first source.
    /// </summary>
    private static ToolsCallMessage MergeToolCallMessages(List<IMessage> sources)
    {
        var toolCalls = ImmutableList.CreateBuilder<ToolCall>();

        // Only the two call-bearing types reach here; the caller partitions the group by type.
        foreach (var source in sources)
        {
            if (source is ToolsCallMessage toolsCallMessage)
            {
                toolCalls.AddRange(toolsCallMessage.ToolCalls);
            }
            else if (source is ToolCallMessage toolCallMessage)
            {
                // ToolCallMessage inherits from ToolCall, so we can create ToolCall from it
                toolCalls.Add(
                    new ToolCall
                    {
                        FunctionName = toolCallMessage.FunctionName,
                        FunctionArgs = toolCallMessage.FunctionArgs,
                        Index = toolCallMessage.Index,
                        ToolCallId = toolCallMessage.ToolCallId,
                        ToolCallIdx = toolCallMessage.ToolCallIdx,
                        ExecutionTarget = toolCallMessage.ExecutionTarget,
                    }
                );
            }
        }

        var merged = toolCalls.ToImmutable();

        return sources[0] switch
        {
            ToolsCallMessage first => first with { ToolCalls = merged },
            ToolCallMessage first => new ToolsCallMessage
            {
                ToolCalls = merged,
                Role = first.Role,
                FromAgent = first.FromAgent,
                GenerationId = first.GenerationId,
                Metadata = first.Metadata,
                ThreadId = first.ThreadId,
                RunId = first.RunId,
                ParentRunId = first.ParentRunId,
                MessageOrderIdx = first.MessageOrderIdx,
            },
            _ => new ToolsCallMessage { ToolCalls = merged },
        };
    }

    /// <summary>
    /// Merges result-bearing messages (singular or plural) into a single ToolsCallResultMessage,
    /// keeping the results in source order and taking envelope properties from the first source.
    /// </summary>
    private static ToolsCallResultMessage MergeToolCallResultMessages(List<IMessage> sources)
    {
        var toolCallResults = ImmutableList.CreateBuilder<ToolCallResult>();

        // Only the two result-bearing types reach here; the caller partitions the group by type.
        foreach (var source in sources)
        {
            if (source is ToolsCallResultMessage toolsCallResultMessage)
            {
                toolCallResults.AddRange(toolsCallResultMessage.ToolCallResults);
            }
            else if (source is ToolCallResultMessage toolCallResultMessage)
            {
                toolCallResults.Add(
                    new ToolCallResult(toolCallResultMessage.ToolCallId, toolCallResultMessage.Result)
                    {
                        ToolName = toolCallResultMessage.ToolName,
                        IsError = toolCallResultMessage.IsError,
                        ErrorCode = toolCallResultMessage.ErrorCode,
                        ExecutionTarget = toolCallResultMessage.ExecutionTarget,
                        ContentBlocks = toolCallResultMessage.ContentBlocks,
                        IsTruncated = toolCallResultMessage.IsTruncated,
                        OriginalBytes = toolCallResultMessage.OriginalBytes,
                    }
                );
            }
        }

        var merged = toolCallResults.ToImmutable();

        return sources[0] switch
        {
            ToolsCallResultMessage first => first with { ToolCallResults = merged },
            ToolCallResultMessage first => new ToolsCallResultMessage
            {
                ToolCallResults = merged,
                Role = first.Role,
                FromAgent = first.FromAgent,
                GenerationId = first.GenerationId,
                Metadata = first.Metadata,
                ThreadId = first.ThreadId,
                RunId = first.RunId,
                MessageOrderIdx = first.MessageOrderIdx,
            },
            _ => new ToolsCallResultMessage { ToolCallResults = merged },
        };
    }

    /// <summary>
    /// Attempts to create a ToolsCallAggregateMessage if the group contains a ToolsCallMessage followed by ToolsCallResultMessage
    /// </summary>
    private static ToolsCallAggregateMessage? TryCreateToolCallAggregate(List<IMessage> group)
    {
        // Sort by MessageOrderIdx if available
        var sortedGroup = group.OrderBy(m => m.MessageOrderIdx ?? int.MaxValue).ToList();

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
            ? new ToolsCallAggregateMessage(toolCallMessage, toolCallResult, toolCallMessage.FromAgent)
            : null;
    }

    /// <summary>
    ///     Creates a CompositeMessage from a group of messages
    /// </summary>
    private static CompositeMessage CreateCompositeMessage(List<IMessage> group)
    {
        // Sort by MessageOrderIdx
        var sortedMessages = group.OrderBy(m => m.MessageOrderIdx ?? int.MaxValue).ToImmutableList();

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
