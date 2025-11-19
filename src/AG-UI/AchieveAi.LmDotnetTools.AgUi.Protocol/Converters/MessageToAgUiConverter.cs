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
    private readonly Dictionary<string, MessageState> _messageStates = new();
    private readonly Dictionary<string, int> _chunkCounters = new();

    public MessageToAgUiConverter(
        IToolCallTracker toolCallTracker,
        ILogger<MessageToAgUiConverter>? logger = null)
    {
        _toolCallTracker = toolCallTracker;
        _logger = logger ?? NullLogger<MessageToAgUiConverter>.Instance;
    }

    /// <inheritdoc/>
    public IEnumerable<AgUiEventBase> ConvertToAgUiEvents(IMessage message, string sessionId)
    {
        return message switch
        {
            TextUpdateMessage textUpdate => ConvertTextUpdate(textUpdate, sessionId),
            ToolsCallUpdateMessage toolUpdate => ConvertToolCallUpdate(toolUpdate, sessionId),
            TextMessage textMessage => ConvertTextMessage(textMessage, sessionId),
            ToolsCallMessage toolsCallMessage => ConvertToolsCallMessage(toolsCallMessage, sessionId),
            _ => Enumerable.Empty<AgUiEventBase>()
        };
    }

    private IEnumerable<AgUiEventBase> ConvertTextUpdate(TextUpdateMessage update, string sessionId)
    {
        var messageId = GetOrCreateMessageId(update.GenerationId);
        var state = GetMessageState(messageId);

        // First update - emit start event
        if (!state.Started)
        {
            state.Started = true;
            yield return new TextMessageStartEvent
            {
                SessionId = sessionId,
                MessageId = messageId,
                Role = ConvertRole(update.Role)
            };
        }

        // Emit content event if there's new text
        if (!string.IsNullOrEmpty(update.Text))
        {
            var chunkIndex = GetAndIncrementChunkCounter(messageId);
            yield return new TextMessageContentEvent
            {
                SessionId = sessionId,
                MessageId = messageId,
                Content = update.Text,
                ChunkIndex = chunkIndex,
                IsThinking = update.IsThinking
            };

            state.TotalLength += update.Text.Length;
        }

        // Check if this appears to be the final update (heuristic)
        // In practice, you'd need a completion signal from LmCore
        if (update.IsUpdate == false || update.Text?.EndsWith("</s>") == true)
        {
            var totalChunks = GetChunkCounter(messageId);
            yield return new TextMessageEndEvent
            {
                SessionId = sessionId,
                MessageId = messageId,
                TotalChunks = totalChunks,
                TotalLength = state.TotalLength
            };

            CleanupMessageState(messageId);
        }
    }

    private IEnumerable<AgUiEventBase> ConvertToolCallUpdate(ToolsCallUpdateMessage update, string sessionId)
    {
        foreach (var toolCallUpdate in update.ToolCallUpdates)
        {
            var toolCallId = _toolCallTracker.GetOrCreateToolCallId(toolCallUpdate.ToolCallId);

            // If this update has a function name, it's the start of a new tool call
            if (!string.IsNullOrEmpty(toolCallUpdate.FunctionName))
            {
                _toolCallTracker.StartToolCall(toolCallId, toolCallUpdate.FunctionName);

                yield return new ToolCallStartEvent
                {
                    SessionId = sessionId,
                    ToolCallId = toolCallId,
                    ToolName = toolCallUpdate.FunctionName
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
                    ToolCallId = toolCallId,
                    Delta = toolCallUpdate.FunctionArgs,
                    JsonFragmentUpdates = fragmentUpdates
                };

                // If complete, emit end event
                if (isComplete)
                {
                    var duration = _toolCallTracker.EndToolCall(toolCallId);
                    yield return new ToolCallEndEvent
                    {
                        SessionId = sessionId,
                        ToolCallId = toolCallId,
                        Duration = duration
                    };
                }
            }
        }
    }

    private IEnumerable<AgUiEventBase> ConvertTextMessage(TextMessage textMessage, string sessionId)
    {
        var messageId = GetOrCreateMessageId(textMessage.GenerationId);

        // Emit start event
        yield return new TextMessageStartEvent
        {
            SessionId = sessionId,
            MessageId = messageId,
            Role = ConvertRole(textMessage.Role)
        };

        // Emit content event with the full text
        if (!string.IsNullOrEmpty(textMessage.Text))
        {
            yield return new TextMessageContentEvent
            {
                SessionId = sessionId,
                MessageId = messageId,
                Content = textMessage.Text,
                ChunkIndex = 0
            };
        }

        // Emit end event
        yield return new TextMessageEndEvent
        {
            SessionId = sessionId,
            MessageId = messageId,
            TotalChunks = string.IsNullOrEmpty(textMessage.Text) ? 0 : 1,
            TotalLength = textMessage.Text?.Length ?? 0
        };
    }

    private IEnumerable<AgUiEventBase> ConvertToolsCallMessage(ToolsCallMessage toolsCallMessage, string sessionId)
    {
        foreach (var toolCall in toolsCallMessage.ToolCalls)
        {
            var toolCallId = _toolCallTracker.GetOrCreateToolCallId(toolCall.ToolCallId);

            // Start the tool call
            _toolCallTracker.StartToolCall(toolCallId, toolCall.FunctionName);

            yield return new ToolCallStartEvent
            {
                SessionId = sessionId,
                ToolCallId = toolCallId,
                ToolName = toolCall.FunctionName
            };

            // Emit arguments event with the full arguments
            if (!string.IsNullOrEmpty(toolCall.FunctionArgs))
            {
                yield return new ToolCallArgumentsEvent
                {
                    SessionId = sessionId,
                    ToolCallId = toolCallId,
                    Delta = toolCall.FunctionArgs,
                    JsonFragmentUpdates = null
                };
            }

            // End the tool call
            var duration = _toolCallTracker.EndToolCall(toolCallId);
            yield return new ToolCallEndEvent
            {
                SessionId = sessionId,
                ToolCallId = toolCallId,
                Duration = duration
            };
        }
    }


    private MessageRole ConvertRole(Role lmCoreRole)
    {
        return lmCoreRole switch
        {
            Role.System => MessageRole.System,
            Role.User => MessageRole.User,
            Role.Assistant => MessageRole.Assistant,
            Role.Tool => MessageRole.Tool,
            _ => MessageRole.Assistant
        };
    }

    private string GetOrCreateMessageId(string? generationId)
        => string.IsNullOrEmpty(generationId) ? Guid.NewGuid().ToString() : generationId;

    private MessageState GetMessageState(string messageId)
    {
        if (!_messageStates.TryGetValue(messageId, out var state))
        {
            state = new MessageState();
            _messageStates[messageId] = state;
        }
        return state;
    }

    private void CleanupMessageState(string messageId)
    {
        _messageStates.Remove(messageId);
        _chunkCounters.Remove(messageId);
    }

    private int GetAndIncrementChunkCounter(string messageId)
    {
        if (!_chunkCounters.TryGetValue(messageId, out var count))
        {
            count = 0;
        }

        _chunkCounters[messageId] = count + 1;
        return count;
    }

    private int GetChunkCounter(string messageId)
    {
        return _chunkCounters.TryGetValue(messageId, out var count) ? count : 0;
    }

    private class MessageState
    {
        public bool Started { get; set; }
        public int TotalLength { get; set; }
    }
}
