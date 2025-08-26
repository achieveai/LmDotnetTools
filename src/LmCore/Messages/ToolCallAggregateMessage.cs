using System.Collections.Immutable;
using System.Text.Json.Serialization;
using AchieveAi.LmDotnetTools.LmCore.Utils;

namespace AchieveAi.LmDotnetTools.LmCore.Messages;

/// <summary>
/// Combines a tool call message and its result into a single message
/// </summary>
[JsonConverter(typeof(ToolsCallAggregateMessageJsonConverter))]
public record ToolsCallAggregateMessage : IMessage
{
    /// <summary>
    /// The original tool call message
    /// </summary>
    [JsonPropertyName("tool_call_message")]
    public ToolsCallMessage ToolsCallMessage { get; init; }

    /// <summary>
    /// The result of the tool call
    /// </summary>
    [JsonPropertyName("tool_call_result")]
    public ToolsCallResultMessage ToolsCallResult { get; init; }

    /// <summary>
    /// The agent that processed this aggregate message
    /// </summary>
    [JsonPropertyName("from_agent")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FromAgent { get; init; }

    /// <summary>
    /// The role of this message (typically Assistant)
    /// </summary>
    [JsonPropertyName("role")]
    public Role Role => Role.Assistant;

    /// <summary>
    /// Combined metadata from the tool call and its result
    /// </summary>
    [JsonIgnore]
    public ImmutableDictionary<string, object>? Metadata { get; init; }

    /// <summary>
    /// Generation ID from the original tool call
    /// </summary>
    [JsonPropertyName("generation_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? GenerationId => ToolsCallMessage.GenerationId;

    public ToolsCallAggregateMessage(
        ToolsCallMessage toolCallMessage,
        ToolsCallResultMessage toolCallResult,
        string? fromAgent = null
    )
    {
        ToolsCallMessage = toolCallMessage;
        ToolsCallResult = toolCallResult;
        FromAgent = fromAgent;

        // Combine metadata from both messages
        if (toolCallMessage.Metadata != null || toolCallResult.Metadata != null)
        {
            Metadata = ImmutableDictionary<string, object>.Empty;

            if (toolCallResult.Metadata != null)
            {
                foreach (var prop in toolCallResult.Metadata)
                {
                    Metadata = Metadata.Add(prop.Key, prop.Value);
                }
            }

            if (toolCallMessage.Metadata != null)
            {
                foreach (var prop in toolCallMessage.Metadata)
                {
                    if (!Metadata.ContainsKey(prop.Key))
                    {
                        // Property from Result wins over Proeprty form toolcall.
                        Metadata = Metadata.Add(prop.Key, prop.Value);
                    }
                }
            }
        }
    }

    // IMessage implementation

    public string? GetText()
    {
        // Use text from the result if available
        var resultText = ToolsCallResult.GetText();
        if (resultText != null)
        {
            return resultText;
        }

        // Otherwise, delegate to the tool call message
        return null;
    }

    public BinaryData? GetBinary()
    {
        // Use binary from the result if available
        var resultBinary = ToolsCallResult.GetBinary();
        if (resultBinary != null)
        {
            return resultBinary;
        }

        // Otherwise, delegate to the tool call message
        return null;
    }
}

public class ToolsCallAggregateMessageJsonConverter
    : ShadowPropertiesJsonConverter<ToolsCallAggregateMessage>
{
    protected override ToolsCallAggregateMessage CreateInstance()
    {
        // Create a minimal instance with default values
        var defaultToolCall = new ToolsCallMessage();
        var defaultResult = new ToolsCallResultMessage();
        return new ToolsCallAggregateMessage(defaultToolCall, defaultResult);
    }
}
