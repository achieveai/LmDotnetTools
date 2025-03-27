using System.Text.Json.Nodes;

namespace AchieveAi.LmDotnetTools.LmCore.Messages;

/// <summary>
/// Represents a streaming text update from a language model.
/// Contains the current accumulated text at a point in time during streaming.
/// </summary>
public record TextUpdateMessage : IMessage, ICanGetText
{
    /// <summary>
    /// The current accumulated text content of the message.
    /// </summary>
    public required string Text { get; init; }

    /// <summary>
    /// Gets the text content of the message.
    /// </summary>
    /// <returns>The text content.</returns>
    public string? GetText() => Text;

    /// <summary>
    /// The role of the message sender (typically Assistant for LM responses).
    /// </summary>
    public Role Role { get; init; } = Role.Assistant;

    /// <summary>
    /// The name or identifier of the agent that generated this message.
    /// </summary>
    public string? FromAgent { get; init; }

    /// <summary>
    /// Additional metadata associated with the message.
    /// </summary>
    public JsonObject? Metadata { get; init; }

    /// <summary>
    /// A unique identifier for the generation this update is part of.
    /// </summary>
    public string? GenerationId { get; init; }

    /// <summary>
    /// Indicates this is a streaming update rather than a complete message.
    /// </summary>
    public bool IsUpdate { get; init; } = true;

    /// <summary>
    /// Not supported for text updates.
    /// </summary>
    public BinaryData? GetBinary() => null;

    /// <summary>
    /// Not supported for text updates.
    /// </summary>
    public ToolCall? GetToolCalls() => null;

    /// <summary>
    /// Not supported for text updates.
    /// </summary>
    public IEnumerable<IMessage>? GetMessages() => null;

    /// <summary>
    /// Converts this update to a complete TextMessage.
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
            GenerationId = GenerationId
        };
    }
} 