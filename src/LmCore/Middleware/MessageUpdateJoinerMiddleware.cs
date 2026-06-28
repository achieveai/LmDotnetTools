using System.Runtime.CompilerServices;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Utils;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AchieveAi.LmDotnetTools.LmCore.Middleware;

/// <summary>
///     Middleware that joins update messages into larger messages for more efficient processing.
/// </summary>
/// <remarks>
///     Composition order matters on the response path: this middleware MUST run after
///     <see cref="MessageTransformationMiddleware" /> (Transformation → Joiner) so that finalizing
///     text messages already carry their assigned messageOrderIdx. Reordering or omitting either
///     middleware reintroduces duplicate assistant messages.
/// </remarks>
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

        // Ids of query-less server (provider-executed) tool calls — e.g. a web_search the model
        // emitted with empty {} input — that the joiner skipped building. Their now-orphaned empty
        // results (which arrive as standalone messages) are dropped too, so neither half is persisted
        // or replayed (the "empty web_search loop").
        var skippedServerToolCallIds = new HashSet<string>(StringComparer.Ordinal);

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
                // Also require a matching GenerationId: a finalizing TextMessage may only supersede
                // the builder it actually finalizes. Without this, an interleaved generation
                // (gen1: TextUpdate…, gen2: TextMessage) could silently drop gen1's accumulated text.
                var incomingFinalizesActiveBuilder =
                    message is TextMessage
                    && activeBuilder is TextMessageBuilder textBuilder
                    && textBuilder.GenerationId == message.GenerationId;

                if (incomingFinalizesActiveBuilder)
                {
                    if (logger.IsEnabled(LogLevel.Debug))
                    {
                        logger.LogDebug(
                            "Joiner suppressed synthesized {MessageType} duplicate for generation {GenerationId}; provider supplied a finalizing complete message",
                            message.GetType().Name,
                            message.GenerationId
                        );
                    }

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
                    var builtMessage = BuildJoinedOrSkip(activeBuilder, skippedServerToolCallIds, logger);
                    activeBuilder = null;
                    activeBuilderType = null;
                    if (builtMessage != null)
                    {
                        yield return builtMessage;
                    }
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
                    var builtMessage = BuildJoinedOrSkip(activeBuilder, skippedServerToolCallIds, logger);
                    activeBuilder = null;
                    activeBuilderType = null;
                    if (builtMessage != null)
                    {
                        yield return builtMessage;
                    }
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
                // Drop the empty server-tool RESULT whose query-less call the joiner just skipped.
                // Without its matching call it is an orphan tool_result the provider rejects on replay,
                // and keeping it would defeat the point of not recording the empty search at all.
                if (processedMessage is ToolCallResultMessage serverResult
                    && serverResult.ExecutionTarget == ExecutionTarget.ProviderServer
                    && !string.IsNullOrEmpty(serverResult.ToolCallId)
                    && skippedServerToolCallIds.Contains(serverResult.ToolCallId))
                {
                    continue;
                }

                yield return processedMessage;
            }
        }

        // Process final built message at the end of the stream
        if (activeBuilder != null)
        {
            var builtMessage = BuildJoinedOrSkip(activeBuilder, skippedServerToolCallIds, logger);
            if (builtMessage != null)
            {
                yield return builtMessage;
            }
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

    /// <summary>
    ///     Builds the active builder's message, but returns <c>null</c> (skipping it) when the result
    ///     is a query-less server (provider-executed) tool call — e.g. a web_search the model emitted
    ///     with empty <c>{}</c> input. The provider answers such a call with empty results, and
    ///     recording/replaying the degenerate call teaches the model to repeat it every turn until
    ///     "Max turns reached" (the "empty web_search loop"). Not creating the message keeps the empty
    ///     search out of joined history (and therefore out of the next request) entirely. The skipped
    ///     call id is recorded so its orphaned empty result can be dropped too. Real, query-bearing
    ///     searches build normally.
    /// </summary>
    private static IMessage? BuildJoinedOrSkip(
        IMessageBuilder builder,
        HashSet<string> skippedServerToolCallIds,
        ILogger logger
    )
    {
        var built = builder.Build();
        if (built is ToolCallMessage toolCall && IsQuerylessServerToolCall(toolCall))
        {
            if (!string.IsNullOrEmpty(toolCall.ToolCallId))
            {
                _ = skippedServerToolCallIds.Add(toolCall.ToolCallId);
            }

            if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.LogDebug(
                    "Joiner skipped query-less server tool call {FunctionName} (id {ToolCallId}); not creating a history message for an empty server-tool call",
                    toolCall.FunctionName,
                    toolCall.ToolCallId
                );
            }

            return null;
        }

        return built;
    }

    /// <summary>
    ///     True for a provider-executed (server-side) tool call whose arguments are empty — null,
    ///     blank, or an empty object (<c>{}</c>). Mirrors the request-side guard
    ///     (<c>AnthropicRequest.IsQuerylessServerToolUse</c>) but at the joiner, so the empty call is
    ///     never recorded in the first place. Conservative: any arguments object with at least one
    ///     property (a real query) is left intact, and unparseable arguments are kept rather than risk
    ///     dropping signal.
    /// </summary>
    private static bool IsQuerylessServerToolCall(ToolCallMessage toolCall)
    {
        if (toolCall.ExecutionTarget != ExecutionTarget.ProviderServer)
        {
            return false;
        }

        var args = toolCall.FunctionArgs;
        if (string.IsNullOrWhiteSpace(args))
        {
            return true;
        }

        try
        {
            using var doc = JsonDocument.Parse(args);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && !doc.RootElement.EnumerateObject().MoveNext();
        }
        catch (JsonException)
        {
            return false;
        }
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
