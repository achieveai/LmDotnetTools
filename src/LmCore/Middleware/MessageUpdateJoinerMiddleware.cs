using System.Runtime.CompilerServices;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Utils;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AchieveAi.LmDotnetTools.LmCore.Middleware;

/// <summary>
///     Middleware that joins update messages into larger messages for more efficient processing.
/// </summary>
public class MessageUpdateJoinerMiddleware : IStreamingMiddleware
{
    private readonly ILogger _logger;

    /// <summary>
    ///     Initializes a new instance of the <see cref="MessageUpdateJoinerMiddleware" /> class.
    /// </summary>
    /// <param name="name">Optional name for the middleware.</param>
    /// <param name="logger">Optional logger; de-dup decisions are logged at Debug level.</param>
    public MessageUpdateJoinerMiddleware(string? name = null, ILogger<MessageUpdateJoinerMiddleware>? logger = null)
    {
        Name = name ?? nameof(MessageUpdateJoinerMiddleware);
        _logger = logger ?? NullLogger<MessageUpdateJoinerMiddleware>.Instance;
    }

    /// <summary>
    ///     Gets the name of the middleware.
    /// </summary>
    public string? Name { get; }

    /// <summary>
    ///     Invokes the middleware for synchronous scenarios.
    /// </summary>
    public async Task<IEnumerable<IMessage>> InvokeAsync(
        MiddlewareContext context,
        IAgent agent,
        CancellationToken cancellationToken = default
    )
    {
        // For non-streaming responses, we just pass through to the agent
        ArgumentNullException.ThrowIfNull(agent);
        return await agent.GenerateReplyAsync(context.Messages, context.Options, cancellationToken);
    }

    /// <summary>
    ///     Invokes the middleware for streaming scenarios, joining update messages into larger messages.
    /// </summary>
    public async Task<IAsyncEnumerable<IMessage>> InvokeStreamingAsync(
        MiddlewareContext context,
        IStreamingAgent agent,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(agent);
        var sourceStream = await agent.GenerateReplyStreamingAsync(
            context.Messages,
            context.Options,
            cancellationToken
        );
        return TransformStreamWithBuilder(sourceStream, _logger, cancellationToken);
    }

    private static async IAsyncEnumerable<IMessage> TransformStreamWithBuilder(
        IAsyncEnumerable<IMessage> sourceStream,
        ILogger logger,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        // Track a single active builder instead of a dictionary
        IMessageBuilder? activeBuilder = null;
        Type? activeBuilderType = null;
        Type? lastMessageType = null;

        // Track the number of completed tool calls for ToolCallIdx assignment
        var completedToolCallCount = 0;

        // Use the usage accumulator to track usage data
        var usageAccumulator = new UsageAccumulator();

        await foreach (var message in sourceStream.WithCancellation(cancellationToken))
        {
            // If we receive a usage message, store it to emit at the end
            if (message is UsageMessage usage)
            {
                _ = usageAccumulator.AddUsageFromMessage(usage);
                continue; // Don't yield usage message yet
            }

            // Check if the message has usage in metadata (legacy support)
            if (message.Metadata != null && message.Metadata.ContainsKey("usage"))
            {
                _ = usageAccumulator.AddUsageFromMessageMetadata(message);
            }

            // Check if we're switching message types and need to complete current builder
            if (lastMessageType != null && lastMessageType != message.GetType() && activeBuilder != null)
            {
                // When the provider follows a streamed text-delta sequence with its OWN finalized
                // TextMessage, that finalized message already IS the joined result. Emitting the
                // builder's synthesized copy as well would forward/persist the same logical message
                // twice (duplicate assistant bubble + duplicate history that also bloats the next
                // turn's prompt). Discard the synthesized copy and let the incoming finalized message
                // be the single representation.
                //
                // NOTE: This applies to text only. Reasoning is intentionally excluded — the OpenAI
                // Responses reasoning item carries its content differently from the streamed reasoning
                // deltas, so suppressing the built reasoning here stops thinking blocks from rendering.
                var incomingFinalizesActiveBuilder =
                    message is TextMessage && activeBuilder is TextMessageBuilder;

                if (incomingFinalizesActiveBuilder)
                {
                    logger.LogDebug(
                        "Joiner suppressed synthesized {MessageType} duplicate for generation {GenerationId}; provider supplied a finalizing complete message",
                        message.GetType().Name,
                        message.GenerationId
                    );
                    activeBuilder = null;
                    activeBuilderType = null;
                }
                else
                {
                    // Track if we're completing a tool call builder
                    if (activeBuilder is ToolCallMessageBuilder)
                    {
                        completedToolCallCount++;
                    }

                    // Complete the previous builder before processing the new message
                    var builtMessage = activeBuilder.Build();
                    activeBuilder = null;
                    activeBuilderType = null;
                    yield return builtMessage;
                }
            }

            // Check if tool call ID/Index changed for singular ToolCallUpdateMessage
            // (ToolCallMessage builder handles single tool call, so we need to complete it when a new one starts)
            if (message is ToolCallUpdateMessage toolCallMsg
                && activeBuilder is ToolCallMessageBuilder currentBuilder
                && activeBuilderType == typeof(ToolCallMessage))
            {
                var isDifferentToolCall =
                    (currentBuilder.CurrentToolCallId != null && toolCallMsg.ToolCallId != null
                        && currentBuilder.CurrentToolCallId != toolCallMsg.ToolCallId)
                    || (currentBuilder.CurrentIndex != null && toolCallMsg.Index != null
                        && currentBuilder.CurrentIndex != toolCallMsg.Index);

                if (isDifferentToolCall)
                {
                    // Complete the previous tool call before starting the new one
                    completedToolCallCount++;
                    var builtMessage = activeBuilder.Build();
                    activeBuilder = null;
                    activeBuilderType = null;
                    yield return builtMessage;
                }
            }

            // Update last message type
            lastMessageType = message.GetType();

            // Process the current message
            var processedMessage = ProcessStreamingMessage(
                message,
                ref activeBuilder,
                ref activeBuilderType,
                completedToolCallCount
            );

            // Only emit the message if it's not being accumulated by a builder
            var isBeingAccumulated =
                activeBuilder != null
                && (
                    message is TextUpdateMessage
                    || message is ReasoningUpdateMessage
                    || message is ToolsCallUpdateMessage
                    || message is ToolCallUpdateMessage
                    || (message is ReasoningMessage && activeBuilderType == typeof(ReasoningMessage))
                );

            if (!isBeingAccumulated)
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

    private static IMessage ProcessStreamingMessage(
        IMessage message,
        ref IMessageBuilder? activeBuilder,
        ref Type? activeBuilderType,
        int toolCallIdx
    )
    {
        // Handle tool call updates (ToolsCallUpdateMessage - plural)
        if (message is ToolsCallUpdateMessage toolCallUpdate)
        {
            return ProcessToolCallUpdate(toolCallUpdate, ref activeBuilder, ref activeBuilderType);
        }
        // Handle tool call updates (ToolCallUpdateMessage - singular)
        else if (message is ToolCallUpdateMessage toolCallSingularUpdate)
        {
            return ProcessToolCallSingularUpdate(
                toolCallSingularUpdate,
                toolCallIdx,
                ref activeBuilder,
                ref activeBuilderType
            );
        }
        // For text update messages

        if (message is TextUpdateMessage textUpdate)
        {
            return ProcessTextUpdate(textUpdate, ref activeBuilder, ref activeBuilderType);
        }
        // For rqwen/qwen3-235b-a22b-thinking-2507easoning update messages

        return message is ReasoningUpdateMessage reasoningUpdate
            ? ProcessReasoningUpdate(reasoningUpdate, ref activeBuilder, ref activeBuilderType)
            : message;
    }

    private static IMessage ProcessToolCallUpdate(
        ToolsCallUpdateMessage toolCallUpdate,
        ref IMessageBuilder? activeBuilder,
        ref Type? activeBuilderType
    )
    {
        var builderType = typeof(ToolsCallMessage);

        if (activeBuilder == null || activeBuilderType != builderType)
        {
            // Create a new builder for the first update
            var builder = new ToolsCallMessageBuilder
            {
                FromAgent = toolCallUpdate.FromAgent,
                Role = toolCallUpdate.Role,
                MessageOrderIdx = toolCallUpdate.MessageOrderIdx,
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

    private static IMessage ProcessToolCallSingularUpdate(
        ToolCallUpdateMessage toolCallUpdate,
        int toolCallIdx,
        ref IMessageBuilder? activeBuilder,
        ref Type? activeBuilderType
    )
    {
        var builderType = typeof(ToolCallMessage);

        if (activeBuilder == null || activeBuilderType != builderType)
        {
            // Create a new builder for the first update
            var builder = new ToolCallMessageBuilder
            {
                FromAgent = toolCallUpdate.FromAgent,
                Role = toolCallUpdate.Role,
                ToolCallIdx = toolCallIdx,
                MessageOrderIdx = toolCallUpdate.MessageOrderIdx,
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
            var builder = (ToolCallMessageBuilder)activeBuilder;
            builder.Add(toolCallUpdate);
            return toolCallUpdate;
        }
    }

    private static IMessage ProcessTextUpdate(
        TextUpdateMessage textUpdateMessage,
        ref IMessageBuilder? activeBuilder,
        ref Type? activeBuilderType
    )
    {
        var builderType = typeof(TextMessage);

        if (activeBuilder == null || activeBuilderType != builderType)
        {
            // Create a new builder for the first update
            var builder = new TextMessageBuilder
            {
                FromAgent = textUpdateMessage.FromAgent,
                Role = textUpdateMessage.Role,
                GenerationId = textUpdateMessage.GenerationId,
                MessageOrderIdx = textUpdateMessage.MessageOrderIdx,
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

            // Return the original update to maintain streaming behavior
            return textUpdateMessage;
        }
    }

    private static IMessage ProcessReasoningUpdate(
        ReasoningUpdateMessage reasoningUpdate,
        ref IMessageBuilder? activeBuilder,
        ref Type? activeBuilderType
    )
    {
        var builderType = typeof(ReasoningMessage);

        if (activeBuilder == null || activeBuilderType != builderType)
        {
            // Create a new builder for the first update
            var builder = new ReasoningMessageBuilder
            {
                FromAgent = reasoningUpdate.FromAgent,
                Role = reasoningUpdate.Role,
                GenerationId = reasoningUpdate.GenerationId,
                Visibility = ReasoningVisibility.Plain, // Default to Plain for updates
                MessageOrderIdx = reasoningUpdate.MessageOrderIdx,
            };
            activeBuilder = builder;
            activeBuilderType = builderType;
            builder.Add(reasoningUpdate);
            // Return the original update for the first time
            return reasoningUpdate;
        }
        else
        {
            // Add to existing builder
            var builder = (ReasoningMessageBuilder)activeBuilder;
            builder.Add(reasoningUpdate);
            return reasoningUpdate;
        }
    }
}
