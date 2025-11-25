using System.Collections.Immutable;
using System.Text.Json.Serialization;
using AchieveAi.LmDotnetTools.LmCore.Utils;

namespace AchieveAi.LmDotnetTools.LmCore.Messages;

/// <summary>
///     Represents a streaming text update from a language model.
///     Contains the current accumulated text at a point in time during streaming.
/// </summary>
[JsonConverter(typeof(TextUpdateMessageJsonConverter))]
public record TextUpdateMessage : IMessage, ICanGetText
{
    /// <summary>
    ///     The current accumulated text content of the message.
    /// </summary>
    [JsonPropertyName("text")]
    public required string Text { get; init; }

    /// <summary>
    ///     Indicates this is a streaming update rather than a complete message.
    /// </summary>
    [JsonPropertyName("isUpdate")]
    public bool IsUpdate { get; init; } = true;

    /// <summary>
    ///     Indicates if this message is a thinking message (for Anthropic thinking content).
    /// </summary>
    [JsonPropertyName("isThinking")]
    public bool IsThinking { get; init; }

    /// <summary>
    ///     Gets the text content of the message.
    /// </summary>
    /// <returns>The text content.</returns>
    public string? GetText()
    {
        return Text;
    }

    /// <summary>
    ///     The role of the message sender (typically Assistant for LM responses).
    /// </summary>
    [JsonPropertyName("role")]
    public Role Role { get; init; } = Role.Assistant;

    /// <summary>
    ///     The name or identifier of the agent that generated this message.
    /// </summary>
    [JsonPropertyName("fromAgent")]
    public string? FromAgent { get; init; }

    /// <summary>
    ///     Additional metadata associated with the message.
    /// </summary>
    [JsonIgnore]
    public ImmutableDictionary<string, object>? Metadata { get; init; }

    /// <summary>
    ///     A unique identifier for the generation this update is part of.
    /// </summary>
    [JsonPropertyName("generationId")]
    public string? GenerationId { get; init; }

    /// <summary>
    ///     Thread identifier for conversation continuity (used with AG-UI protocol).
    /// </summary>
    [JsonPropertyName("threadId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ThreadId { get; init; }

    /// <summary>
    ///     Run identifier for this specific execution (used with AG-UI protocol).
    /// </summary>
    [JsonPropertyName("runId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RunId { get; init; }

    /// <summary>
    ///     Parent Run identifier for branching/time travel (creates git-like lineage).
    /// </summary>
    [JsonPropertyName("parentRunId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ParentRunId { get; init; }

    /// <summary>
    ///     Order index of this message within its generation (same GenerationId).
    /// </summary>
    [JsonPropertyName("messageOrderIdx")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MessageOrderIdx { get; init; }

    /// <summary>
    ///     Not supported for text updates.
    /// </summary>
    public static BinaryData? GetBinary()
    {
        return null;
    }

    /// <summary>
    ///     Not supported for text updates.
    /// </summary>
    public static ToolCall? GetToolCalls()
    {
        return null;
    }

    /// <summary>
    ///     Not supported for text updates.
    /// </summary>
    public static IEnumerable<IMessage>? GetMessages()
    {
        return null;
    }

    /// <summary>
    ///     Converts this update to a complete TextMessage.
    /// </summary>
    /// <returns>A TextMessage with the same content and properties.</returns>
    public TextMessage ToTextMessage()
    {
        return new TextMessage
        {
            Text = Text,
            Role = Role,
            FromAgent = FromAgent,
            Metadata = Metadata,
            GenerationId = GenerationId,
            IsThinking = IsThinking,
            ThreadId = ThreadId,
            RunId = RunId,
            ParentRunId = ParentRunId,
            MessageOrderIdx = MessageOrderIdx,
        };
    }
}

/// <summary>
///     JSON converter for TextUpdateMessage that supports the shadow properties pattern.
/// </summary>
public class TextUpdateMessageJsonConverter : ShadowPropertiesJsonConverter<TextUpdateMessage>
{
    /// <summary>
    ///     Creates a new instance of TextUpdateMessage during deserialization.
    /// </summary>
    /// <returns>A minimal TextUpdateMessage instance.</returns>
    protected override TextUpdateMessage CreateInstance()
    {
        return new TextUpdateMessage { Text = string.Empty };
    }
}
