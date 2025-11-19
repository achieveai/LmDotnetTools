using System.Collections.Immutable;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AchieveAi.LmDotnetTools.AgUi.Protocol.Converters;

/// <summary>
/// Converts LmCore messages to AG-UI protocol format (outbound conversion)
/// </summary>
public class LmCoreToAgUiConverter : ILmCoreToAgUiConverter
{
    private readonly ILogger<LmCoreToAgUiConverter> _logger;

    public LmCoreToAgUiConverter(ILogger<LmCoreToAgUiConverter>? logger = null)
    {
        _logger = logger ?? NullLogger<LmCoreToAgUiConverter>.Instance;
    }

    /// <inheritdoc/>
    public ImmutableList<DataObjects.DTOs.Message> ConvertMessage(IMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        // Validate GenerationId
        if (string.IsNullOrWhiteSpace(message.GenerationId))
        {
            throw new InvalidOperationException(
                "Cannot convert LmCore message to AG-UI format: GenerationId is required but was null or empty. " +
                "Ensure all messages have a valid GenerationId before conversion.");
        }

        return message switch
        {
            TextMessage textMessage => [ConvertTextMessage(textMessage)],
            ToolsCallMessage toolsCallMessage => [ConvertToolsCallMessage(toolsCallMessage)],
            ToolsCallResultMessage toolsCallResultMessage => ConvertToolsCallResultMessage(toolsCallResultMessage),
            CompositeMessage compositeMessage => ConvertCompositeMessage(compositeMessage),
            ToolsCallAggregateMessage aggregateMessage => ConvertToolsCallAggregateMessage(aggregateMessage),
            _ => []  // Unsupported message types return empty list
        };
    }

    /// <inheritdoc/>
    public ImmutableList<DataObjects.DTOs.Message> ConvertMessageHistory(IEnumerable<IMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var result = ImmutableList.CreateBuilder<DataObjects.DTOs.Message>();

        foreach (var message in messages)
        {
            try
            {
                var converted = ConvertMessage(message);
                result.AddRange(converted);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to convert message of type {MessageType}", message.GetType().Name);
                // Continue processing other messages
            }
        }

        return result.ToImmutable();
    }

    /// <inheritdoc/>
    public DataObjects.DTOs.ToolCall ConvertToolCall(ToolCall toolCall)
    {
        if (string.IsNullOrWhiteSpace(toolCall.ToolCallId))
        {
            throw new ArgumentException("ToolCall.ToolCallId cannot be null or empty", nameof(toolCall));
        }

        if (string.IsNullOrWhiteSpace(toolCall.FunctionName))
        {
            throw new ArgumentException("ToolCall.FunctionName cannot be null or empty", nameof(toolCall));
        }

        // Parse JSON string to JsonElement
        JsonElement arguments;
        try
        {
            arguments = JsonSerializer.Deserialize<JsonElement>(toolCall.FunctionArgs ?? "{}");
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to parse ToolCall.FunctionArgs as JSON: {Args}", toolCall.FunctionArgs);
            throw new JsonException($"Invalid JSON in ToolCall.FunctionArgs: {toolCall.FunctionArgs}", ex);
        }

        return new DataObjects.DTOs.ToolCall
        {
            Id = toolCall.ToolCallId,
            Function = new DataObjects.DTOs.FunctionCall
            {
                Name = toolCall.FunctionName,
                Arguments = arguments
            }
        };
    }

    private DataObjects.DTOs.Message ConvertTextMessage(TextMessage textMessage)
    {
        return new DataObjects.DTOs.Message
        {
            Id = textMessage.GenerationId!,  // Already validated
            Role = ConvertRole(textMessage.Role),
            Content = textMessage.Text,
            Name = textMessage.FromAgent  // May be null
        };
    }

    private DataObjects.DTOs.Message ConvertToolsCallMessage(ToolsCallMessage toolsCallMessage)
    {
        var toolCalls = toolsCallMessage.ToolCalls
            .Select(ConvertToolCall)
            .ToImmutableList();

        return new DataObjects.DTOs.Message
        {
            Id = toolsCallMessage.GenerationId!,
            Role = ConvertRole(toolsCallMessage.Role),
            ToolCalls = toolCalls,
            Name = toolsCallMessage.FromAgent
        };
    }

    private ImmutableList<DataObjects.DTOs.Message> ConvertToolsCallResultMessage(ToolsCallResultMessage resultMessage)
    {
        // One AG-UI Message per result (per user decision)
        var results = new List<DataObjects.DTOs.Message>();

        for (int i = 0; i < resultMessage.ToolCallResults.Count; i++)
        {
            var result = resultMessage.ToolCallResults[i];

            results.Add(new DataObjects.DTOs.Message
            {
                Id = $"{resultMessage.GenerationId}_result_{i}",
                Role = "tool",
                Content = result.Result,
                ToolCallId = result.ToolCallId,
                Name = resultMessage.FromAgent
            });
        }

        return results.ToImmutableList();
    }

    private ImmutableList<DataObjects.DTOs.Message> ConvertCompositeMessage(CompositeMessage compositeMessage)
    {
        // Flatten: each nested message becomes separate AG-UI Message
        var results = new List<DataObjects.DTOs.Message>();

        for (int i = 0; i < compositeMessage.Messages.Count; i++)
        {
            var nestedMessage = compositeMessage.Messages[i];

            try
            {
                // Convert the message and update IDs with index suffix
                var converted = ConvertMessageWithIdSuffix(nestedMessage, $"{compositeMessage.GenerationId}_{i}");
                results.AddRange(converted);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to convert nested message {Index} in CompositeMessage", i);
            }
        }

        return results.ToImmutableList();
    }

    private ImmutableList<DataObjects.DTOs.Message> ConvertMessageWithIdSuffix(IMessage message, string newGenerationId)
    {
        // Convert based on message type, but use the provided GenerationId instead of the message's own
        return message switch
        {
            TextMessage textMessage => [ConvertTextMessageWithId(textMessage, newGenerationId)],
            ToolsCallMessage toolsCallMessage => [ConvertToolsCallMessageWithId(toolsCallMessage, newGenerationId)],
            ToolsCallResultMessage toolsCallResultMessage => ConvertToolsCallResultMessageWithId(toolsCallResultMessage, newGenerationId),
            CompositeMessage compositeMessage => ConvertCompositeMessageWithId(compositeMessage, newGenerationId),
            ToolsCallAggregateMessage aggregateMessage => ConvertToolsCallAggregateMessageWithId(aggregateMessage, newGenerationId),
            _ => []
        };
    }

    private DataObjects.DTOs.Message ConvertTextMessageWithId(TextMessage textMessage, string id)
    {
        return new DataObjects.DTOs.Message
        {
            Id = id,
            Role = ConvertRole(textMessage.Role),
            Content = textMessage.Text,
            Name = textMessage.FromAgent
        };
    }

    private DataObjects.DTOs.Message ConvertToolsCallMessageWithId(ToolsCallMessage toolsCallMessage, string id)
    {
        var toolCalls = toolsCallMessage.ToolCalls
            .Select(ConvertToolCall)
            .ToImmutableList();

        return new DataObjects.DTOs.Message
        {
            Id = id,
            Role = ConvertRole(toolsCallMessage.Role),
            ToolCalls = toolCalls,
            Name = toolsCallMessage.FromAgent
        };
    }

    private ImmutableList<DataObjects.DTOs.Message> ConvertToolsCallResultMessageWithId(ToolsCallResultMessage resultMessage, string baseId)
    {
        var results = new List<DataObjects.DTOs.Message>();

        for (int i = 0; i < resultMessage.ToolCallResults.Count; i++)
        {
            var result = resultMessage.ToolCallResults[i];

            results.Add(new DataObjects.DTOs.Message
            {
                Id = $"{baseId}_result_{i}",
                Role = "tool",
                Content = result.Result,
                ToolCallId = result.ToolCallId,
                Name = resultMessage.FromAgent
            });
        }

        return results.ToImmutableList();
    }

    private ImmutableList<DataObjects.DTOs.Message> ConvertCompositeMessageWithId(CompositeMessage compositeMessage, string baseId)
    {
        var results = new List<DataObjects.DTOs.Message>();

        for (int i = 0; i < compositeMessage.Messages.Count; i++)
        {
            var nestedMessage = compositeMessage.Messages[i];

            try
            {
                var converted = ConvertMessageWithIdSuffix(nestedMessage, $"{baseId}_{i}");
                results.AddRange(converted);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to convert nested message {Index} in CompositeMessage", i);
            }
        }

        return results.ToImmutableList();
    }

    private ImmutableList<DataObjects.DTOs.Message> ConvertToolsCallAggregateMessageWithId(ToolsCallAggregateMessage aggregateMessage, string baseId)
    {
        var results = new List<DataObjects.DTOs.Message>();

        // 1. Convert the tool call message
        var toolCallMessage = ConvertToolsCallMessageWithId(aggregateMessage.ToolsCallMessage, baseId);
        results.Add(toolCallMessage);

        // 2. Convert the tool results
        var toolResults = ConvertToolsCallResultMessageWithId(aggregateMessage.ToolsCallResult, baseId);
        results.AddRange(toolResults);

        return results.ToImmutableList();
    }

    private ImmutableList<DataObjects.DTOs.Message> ConvertToolsCallAggregateMessage(ToolsCallAggregateMessage aggregateMessage)
    {
        var results = new List<DataObjects.DTOs.Message>();

        // 1. Convert the tool call message
        var toolCallMessage = ConvertToolsCallMessage(aggregateMessage.ToolsCallMessage);
        results.Add(toolCallMessage);

        // 2. Convert the tool results
        var toolResults = ConvertToolsCallResultMessage(aggregateMessage.ToolsCallResult);
        results.AddRange(toolResults);

        return results.ToImmutableList();
    }

    private static string ConvertRole(Role role)
    {
        return role switch
        {
            Role.System => "system",
            Role.User => "user",
            Role.Assistant => "assistant",
            Role.Tool => "tool",
            _ => "assistant"  // Default fallback
        };
    }
}
