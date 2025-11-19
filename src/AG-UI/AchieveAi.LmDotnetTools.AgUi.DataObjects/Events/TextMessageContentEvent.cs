using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.AgUi.DataObjects.Events;

/// <summary>
/// Streams chunks of text content as they become available
/// </summary>
public sealed record TextMessageContentEvent : AgUiEventBase
{
    /// <inheritdoc/>
    [JsonIgnore]
    public override string Type => "TEXT_MESSAGE_CONTENT";

    /// <summary>
    /// Identifier for the message this content belongs to (matches TextMessageStartEvent.MessageId)
    /// </summary>
    [JsonPropertyName("messageId")]
    public string MessageId { get; init; } = string.Empty;

    /// <summary>
    /// The text content chunk
    /// </summary>
    [JsonPropertyName("content")]
    public string Content { get; init; } = string.Empty;

    /// <summary>
    /// Sequential index of this chunk within the message
    /// </summary>
    [JsonPropertyName("chunkIndex")]
    public int ChunkIndex { get; init; }

    /// <summary>
    /// Indicates whether this content is thinking/reasoning (vs final response)
    /// </summary>
    [JsonPropertyName("isThinking")]
    public bool IsThinking { get; init; }
}
