using System.Collections.Immutable;
using AchieveAi.LmDotnetTools.AgUi.DataObjects;
using AchieveAi.LmDotnetTools.AgUi.DataObjects.Enums;
using AchieveAi.LmDotnetTools.AgUi.DataObjects.Events;
using AchieveAi.LmDotnetTools.AgUi.Protocol.Tracking;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AchieveAi.LmDotnetTools.AgUi.Protocol.Converters;

/// <summary>
/// Converts LmCore messages to AG-UI protocol events
/// </summary>
public class MessageToAgUiConverter : IMessageConverter
{
    private readonly IToolCallTracker _toolCallTracker;
    private readonly ILogger<MessageToAgUiConverter> _logger;
    private readonly Dictionary<string, MessageState> _messageStates = [];
    private readonly Dictionary<string, int> _chunkCounters = [];
    private (string messageId, string messageType)? _activeMessageKey;

    public MessageToAgUiConverter(IToolCallTracker toolCallTracker, ILogger<MessageToAgUiConverter>? logger = null)
    {
        _toolCallTracker = toolCallTracker;
        _logger = logger ?? NullLogger<MessageToAgUiConverter>.Instance;
    }

    /// <inheritdoc/>
    public IEnumerable<AgUiEventBase> ConvertToAgUiEvents(
        IMessage message,
        string sessionId,
        string? threadId = null,
        string? runId = null
    )
    {
        return message switch
        {
            TextUpdateMessage textUpdate => ConvertTextUpdate(textUpdate, sessionId, threadId, runId),
            ToolsCallUpdateMessage toolUpdate => ConvertToolCallUpdate(toolUpdate, sessionId, threadId, runId),
            ReasoningUpdateMessage reasoningUpdate => ConvertReasoningUpdate(
                reasoningUpdate,
                sessionId,
                threadId,
                runId
            ),
            TextMessage textMessage => ConvertTextMessage(textMessage, sessionId, threadId, runId),
            ToolsCallAggregateMessage aggregate => ConvertToolsCallAggregateMessage(
                aggregate,
                sessionId,
                threadId,
                runId
            ),
            ReasoningMessage reasoningMessage => [], // ConvertReasoningMessage(reasoningMessage, sessionId, threadId, runId),
            ToolsCallMessage toolsCallMessage => ConvertToolsCallMessage(toolsCallMessage, sessionId, threadId, runId),
            _ => HandleUnknown(sessionId, threadId, runId),
        };
    }

    public IEnumerable<AgUiEventBase> Flush(string sessionId, string? threadId = null, string? runId = null)
    {
        if (_activeMessageKey.HasValue)
        {
            var (messageId, messageType) = _activeMessageKey.Value;
            foreach (var endEvent in CloseOrphanedMessage(messageId, messageType, sessionId, threadId, runId))
            {
                yield return endEvent;
            }
            _activeMessageKey = null;
        }
    }

    private IEnumerable<AgUiEventBase> HandleUnknown(string sessionId, string? threadId, string? runId)
    {
        // Check if we're switching to a different message - close the previous one
        if (_activeMessageKey.HasValue)
        {
            var (prevId, prevType) = _activeMessageKey.Value;
            foreach (var endEvent in CloseOrphanedMessage(prevId, prevType, sessionId, threadId, runId))
            {
                yield return endEvent;
            }
        }
    }

    private IEnumerable<AgUiEventBase> ConvertTextUpdate(
        TextUpdateMessage update,
        string sessionId,
        string? threadId,
        string? runId
    )
    {
        var messageType = update.IsThinking ? "Thinking" : "Text";
        var messageId = GetOrCreateMessageId(update.GenerationId);
        var currentKey = (messageId, messageType);

        // Check if we're switching to a different message - close the previous one
        if (_activeMessageKey.HasValue && _activeMessageKey.Value != currentKey)
        {
            var (prevId, prevType) = _activeMessageKey.Value;
            foreach (var endEvent in CloseOrphanedMessage(prevId, prevType, sessionId, threadId, runId))
            {
                yield return endEvent;
            }
        }

        var state = GetMessageState(messageId, messageType);

        // First update - emit start event
        if (!state.Started)
        {
            state.Started = true;
            state.Type = MessageType.Text; // Mark as text message
            _activeMessageKey = currentKey; // Track as active message

            yield return new TextMessageStartEvent
            {
                SessionId = sessionId,
                ThreadId = threadId,
                RunId = runId,
                MessageId = messageId,
                Role = ConvertRole(update.Role),
            };
        }

        // Emit content event if there's new text
        if (!string.IsNullOrEmpty(update.Text))
        {
            var chunkIndex = GetAndIncrementChunkCounter(messageId, messageType);
            yield return new TextMessageContentEvent
            {
                SessionId = sessionId,
                ThreadId = threadId,
                RunId = runId,
                MessageId = messageId,
                Delta = update.Text,
                ChunkIndex = chunkIndex,
            };

            state.TotalLength += update.Text.Length;
        }
    }

    private IEnumerable<AgUiEventBase> ConvertToolCallUpdate(
        ToolsCallUpdateMessage update,
        string sessionId,
        string? threadId,
        string? runId
    )
    {
        foreach (var toolCallUpdate in update.ToolCallUpdates)
        {
            const string messageType = "toolcall";
            var toolCallId = _toolCallTracker.GetOrCreateToolCallId(toolCallUpdate.ToolCallId);
            var currentKey = (toolCallId, messageType);

            // If this update has a function name, it's the start of a new tool call
            if (!string.IsNullOrEmpty(toolCallUpdate.FunctionName))
            {
                // Check if we're switching to a different message - close the previous one
                if (_activeMessageKey.HasValue && _activeMessageKey.Value != currentKey)
                {
                    var (prevId, prevType) = _activeMessageKey.Value;
                    foreach (var endEvent in CloseOrphanedMessage(prevId, prevType, sessionId, threadId, runId))
                    {
                        yield return endEvent;
                    }
                }

                _toolCallTracker.StartToolCall(toolCallId, toolCallUpdate.FunctionName);
                _activeMessageKey = currentKey; // Track as active message

                yield return new ToolCallStartEvent
                {
                    SessionId = sessionId,
                    ThreadId = threadId,
                    RunId = runId,
                    ToolCallId = toolCallId,
                    ToolName = toolCallUpdate.FunctionName,
                };
            }

            // If there are function arguments, emit arguments event
            if (!string.IsNullOrEmpty(toolCallUpdate.FunctionArgs))
            {
                // Check if arguments are complete (heuristic: ends with })
                var isComplete = toolCallUpdate.FunctionArgs.TrimEnd().EndsWith('}');

                var fragmentUpdates = toolCallUpdate.JsonFragmentUpdates?.ToImmutableList();

                yield return new ToolCallArgumentsEvent
                {
                    SessionId = sessionId,
                    ThreadId = threadId,
                    RunId = runId,
                    ToolCallId = toolCallId,
                    Delta = toolCallUpdate.FunctionArgs,
                    JsonFragmentUpdates = fragmentUpdates,
                };

                // If complete, emit end event
                if (isComplete)
                {
                    var duration = _toolCallTracker.EndToolCall(toolCallId);
                    yield return new ToolCallEndEvent
                    {
                        SessionId = sessionId,
                        ThreadId = threadId,
                        RunId = runId,
                        ToolCallId = toolCallId,
                        Duration = duration,
                    };

                    // Clear active message tracking since this message properly ended
                    if (_activeMessageKey.HasValue && _activeMessageKey.Value == currentKey)
                    {
                        _activeMessageKey = null;
                    }
                }
            }
        }
    }

    private static IEnumerable<AgUiEventBase> ConvertTextMessage(
        TextMessage textMessage,
        string sessionId,
        string? threadId,
        string? runId
    )
    {
        var messageId = GetOrCreateMessageId(textMessage.GenerationId);

        // Emit start event
        yield return new TextMessageStartEvent
        {
            SessionId = sessionId,
            ThreadId = threadId,
            RunId = runId,
            MessageId = messageId,
            Role = ConvertRole(textMessage.Role),
        };

        // Emit content event with the full text
        if (!string.IsNullOrEmpty(textMessage.Text))
        {
            yield return new TextMessageContentEvent
            {
                SessionId = sessionId,
                ThreadId = threadId,
                RunId = runId,
                MessageId = messageId,
                Delta = textMessage.Text,
                ChunkIndex = 0,
            };
        }

        // Emit end event
        yield return new TextMessageEndEvent
        {
            SessionId = sessionId,
            ThreadId = threadId,
            RunId = runId,
            MessageId = messageId,
            TotalChunks = string.IsNullOrEmpty(textMessage.Text) ? 0 : 1,
            TotalLength = textMessage.Text?.Length ?? 0,
        };
    }

    private IEnumerable<AgUiEventBase> ConvertToolsCallMessage(
        ToolsCallMessage toolsCallMessage,
        string sessionId,
        string? threadId,
        string? runId
    )
    {
        foreach (var toolCall in toolsCallMessage.ToolCalls)
        {
            if (string.IsNullOrEmpty(toolCall.FunctionName))
            {
                continue; // Skip tool calls without a function name
            }

            var toolCallId = _toolCallTracker.GetOrCreateToolCallId(toolCall.ToolCallId);

            // Start the tool call
            _toolCallTracker.StartToolCall(toolCallId, toolCall.FunctionName);

            yield return new ToolCallStartEvent
            {
                SessionId = sessionId,
                ThreadId = threadId,
                RunId = runId,
                ToolCallId = toolCallId,
                ToolName = toolCall.FunctionName,
            };

            // Emit arguments event with the full arguments
            if (!string.IsNullOrEmpty(toolCall.FunctionArgs))
            {
                yield return new ToolCallArgumentsEvent
                {
                    SessionId = sessionId,
                    ThreadId = threadId,
                    RunId = runId,
                    ToolCallId = toolCallId,
                    Delta = toolCall.FunctionArgs,
                    JsonFragmentUpdates = null,
                };
            }

            // End the tool call
            var duration = _toolCallTracker.EndToolCall(toolCallId);
            yield return new ToolCallEndEvent
            {
                SessionId = sessionId,
                ThreadId = threadId,
                RunId = runId,
                ToolCallId = toolCallId,
                Duration = duration,
            };
        }
    }

    private IEnumerable<AgUiEventBase> ConvertToolsCallAggregateMessage(
        ToolsCallAggregateMessage aggregate,
        string sessionId,
        string? threadId,
        string? runId
    )
    {
        // First, emit events for the tool call request
        foreach (var evt in ConvertToolsCallMessage(aggregate.ToolsCallMessage, sessionId, threadId, runId))
        {
            yield return evt;
        }

        // Then, emit events for the tool call result
        if (aggregate.ToolsCallResult != null)
        {
            foreach (var result in aggregate.ToolsCallResult.ToolCallResults)
            {
                var toolCallId = _toolCallTracker.GetOrCreateToolCallId(result.ToolCallId);

                yield return new ToolCallResultEvent
                {
                    SessionId = sessionId,
                    ThreadId = threadId,
                    RunId = runId,
                    ToolCallId = toolCallId,
                    Content = result.Result,
                };
            }
        }
    }

    private IEnumerable<AgUiEventBase> ConvertReasoningMessage(
        ReasoningMessage reasoningMessage,
        string sessionId,
        string? threadId,
        string? runId
    )
    {
        // Check if we're switching to a different message - close the previous one
        if (_activeMessageKey.HasValue)
        {
            var (prevId, prevType) = _activeMessageKey.Value;
            foreach (var endEvent in CloseOrphanedMessage(prevId, prevType, sessionId, threadId, runId))
            {
                yield return endEvent;
            }
        }

        var messageId = GetOrCreateMessageId(reasoningMessage.GenerationId);
        var visibilityString = ConvertReasoningVisibility(reasoningMessage.Visibility);

        // Emit REASONING_START with encrypted content if applicable
        var reasoningStartEvent = new ReasoningStartEvent
        {
            SessionId = sessionId,
            ThreadId = threadId,
            RunId = runId,
            EncryptedReasoning =
                reasoningMessage.Visibility == ReasoningVisibility.Encrypted ? reasoningMessage.Reasoning : null,
        };
        yield return reasoningStartEvent;

        // For non-encrypted reasoning, emit the full message stream
        if (reasoningMessage.Visibility != ReasoningVisibility.Encrypted)
        {
            // Emit REASONING_MESSAGE_START
            yield return new ReasoningMessageStartEvent
            {
                SessionId = sessionId,
                ThreadId = threadId,
                RunId = runId,
                MessageId = messageId,
                Visibility = visibilityString,
            };

            // Emit REASONING_MESSAGE_CONTENT with full text
            if (!string.IsNullOrEmpty(reasoningMessage.Reasoning))
            {
                yield return new ReasoningMessageContentEvent
                {
                    SessionId = sessionId,
                    ThreadId = threadId,
                    RunId = runId,
                    MessageId = messageId,
                    Delta = reasoningMessage.Reasoning,
                    ChunkIndex = 0,
                };
            }

            // Emit REASONING_MESSAGE_END
            yield return new ReasoningMessageEndEvent
            {
                SessionId = sessionId,
                ThreadId = threadId,
                RunId = runId,
                MessageId = messageId,
                TotalChunks = string.IsNullOrEmpty(reasoningMessage.Reasoning) ? 0 : 1,
                TotalLength = reasoningMessage.Reasoning?.Length ?? 0,
            };
        }

        // Emit REASONING_END with summary if applicable
        var reasoningEndEvent = new ReasoningEndEvent
        {
            SessionId = sessionId,
            ThreadId = threadId,
            RunId = runId,
            Summary = reasoningMessage.Visibility == ReasoningVisibility.Summary ? reasoningMessage.Reasoning : null,
        };
        yield return reasoningEndEvent;
    }

    private IEnumerable<AgUiEventBase> ConvertReasoningUpdate(
        ReasoningUpdateMessage update,
        string sessionId,
        string? threadId,
        string? runId
    )
    {
        const string messageType = "reasoning";
        var messageId = GetOrCreateMessageId(update.GenerationId);
        var currentKey = (messageId, messageType);

        // Check if we're switching to a different message - close the previous one
        if (_activeMessageKey.HasValue && _activeMessageKey.Value != currentKey)
        {
            var (prevId, prevType) = _activeMessageKey.Value;
            foreach (var endEvent in CloseOrphanedMessage(prevId, prevType, sessionId, threadId, runId))
            {
                yield return endEvent;
            }
        }

        var state = GetMessageState(messageId, messageType);
        var visibilityString = update.Visibility.HasValue ? ConvertReasoningVisibility(update.Visibility.Value) : null;

        // First update - emit REASONING_START and REASONING_MESSAGE_START
        if (!state.Started)
        {
            state.Started = true;
            state.Type = MessageType.Reasoning; // Mark as reasoning message
            _activeMessageKey = currentKey; // Track as active message

            // Emit REASONING_START (no encrypted content for streaming)
            yield return new ReasoningStartEvent
            {
                SessionId = sessionId,
                ThreadId = threadId,
                RunId = runId,
                EncryptedReasoning = null,
            };

            // Emit REASONING_MESSAGE_START
            yield return new ReasoningMessageStartEvent
            {
                SessionId = sessionId,
                ThreadId = threadId,
                RunId = runId,
                MessageId = messageId,
                Visibility = visibilityString,
            };
        }

        // Emit REASONING_MESSAGE_CONTENT if there's new reasoning text
        if (!string.IsNullOrEmpty(update.Reasoning))
        {
            var chunkIndex = GetAndIncrementChunkCounter(messageId, messageType);
            yield return new ReasoningMessageContentEvent
            {
                SessionId = sessionId,
                ThreadId = threadId,
                RunId = runId,
                MessageId = messageId,
                Delta = update.Reasoning,
                ChunkIndex = chunkIndex,
            };

            state.TotalLength += update.Reasoning.Length;
        }

        // Check if this appears to be the final update
        if (!update.IsUpdate || update.Reasoning?.EndsWith("</s>") == true)
        {
            var totalChunks = GetChunkCounter(messageId, messageType);

            // Emit REASONING_MESSAGE_END
            yield return new ReasoningMessageEndEvent
            {
                SessionId = sessionId,
                ThreadId = threadId,
                RunId = runId,
                MessageId = messageId,
                TotalChunks = totalChunks,
                TotalLength = state.TotalLength,
            };

            // Emit REASONING_END
            yield return new ReasoningEndEvent
            {
                SessionId = sessionId,
                ThreadId = threadId,
                RunId = runId,
                Summary = null, // Summary would come from a separate message
            };

            CleanupMessageState(messageId, messageType);

            // Clear active message tracking since this message properly ended
            if (_activeMessageKey.HasValue && _activeMessageKey.Value == currentKey)
            {
                _activeMessageKey = null;
            }
        }
    }

    private static string? ConvertReasoningVisibility(ReasoningVisibility visibility)
    {
        return visibility switch
        {
            ReasoningVisibility.Plain => "plain",
            ReasoningVisibility.Summary => "summary",
            ReasoningVisibility.Encrypted => "encrypted",
            _ => null,
        };
    }

    /// <summary>
    /// Closes an orphaned message by emitting appropriate END events
    /// </summary>
    private IEnumerable<AgUiEventBase> CloseOrphanedMessage(
        string messageId,
        string messageType,
        string sessionId,
        string? threadId,
        string? runId
    )
    {
        var key = $"{messageId}_{messageType}";
        if (!_messageStates.TryGetValue(key, out var state))
        {
            yield break; // No state means message was never started
        }

        if (!state.Started)
        {
            yield break; // Message never started, nothing to close
        }

        var totalChunks = GetChunkCounter(messageId, messageType);

        switch (state.Type)
        {
            case MessageType.Text:
                _logger.LogWarning("Closing orphaned text message {MessageId}", messageId);
                yield return new TextMessageEndEvent
                {
                    SessionId = sessionId,
                    ThreadId = threadId,
                    RunId = runId,
                    MessageId = messageId,
                    TotalChunks = totalChunks,
                    TotalLength = state.TotalLength,
                };
                break;

            case MessageType.Reasoning:
                _logger.LogWarning("Closing orphaned reasoning message {MessageId}", messageId);
                yield return new ReasoningMessageEndEvent
                {
                    SessionId = sessionId,
                    ThreadId = threadId,
                    RunId = runId,
                    MessageId = messageId,
                    TotalChunks = totalChunks,
                    TotalLength = state.TotalLength,
                };
                yield return new ReasoningEndEvent
                {
                    SessionId = sessionId,
                    ThreadId = threadId,
                    RunId = runId,
                    Summary = null,
                };
                break;

            case MessageType.ToolCall:
                _logger.LogWarning("Closing orphaned tool call {MessageId}", messageId);
                // Tool calls are tracked separately via IToolCallTracker
                // Just emit TOOL_CALL_END
                yield return new ToolCallEndEvent
                {
                    SessionId = sessionId,
                    ThreadId = threadId,
                    RunId = runId,
                    ToolCallId = messageId,
                };
                break;
            default:
                break;
        }

        CleanupMessageState(messageId, messageType);
    }

    private static MessageRole ConvertRole(Role lmCoreRole)
    {
        return lmCoreRole switch
        {
            Role.System => MessageRole.System,
            Role.User => MessageRole.User,
            Role.Assistant => MessageRole.Assistant,
            Role.Tool => MessageRole.Tool,
            _ => MessageRole.Assistant,
        };
    }

    private static string GetOrCreateMessageId(string? generationId)
    {
        return string.IsNullOrEmpty(generationId) ? Guid.NewGuid().ToString() : generationId;
    }

    private MessageState GetMessageState(string messageId, string messageType)
    {
        var key = $"{messageId}_{messageType}";
        if (!_messageStates.TryGetValue(key, out var state))
        {
            state = new MessageState();
            _messageStates[key] = state;
        }

        return state;
    }

    private void CleanupMessageState(string messageId, string messageType)
    {
        var key = $"{messageId}_{messageType}";
        _ = _messageStates.Remove(key);
        _ = _chunkCounters.Remove(key);
    }

    private int GetAndIncrementChunkCounter(string messageId, string messageType)
    {
        var key = $"{messageId}_{messageType}";
        if (!_chunkCounters.TryGetValue(key, out var count))
        {
            count = 0;
        }

        _chunkCounters[key] = count + 1;
        return count;
    }

    private int GetChunkCounter(string messageId, string messageType)
    {
        var key = $"{messageId}_{messageType}";
        return _chunkCounters.TryGetValue(key, out var count) ? count : 0;
    }

    private class MessageState
    {
        public bool Started { get; set; }
        public int TotalLength { get; set; }
        public MessageType Type { get; set; }
    }

    private enum MessageType
    {
        Text,
        Reasoning,
        ToolCall,
    }
}
