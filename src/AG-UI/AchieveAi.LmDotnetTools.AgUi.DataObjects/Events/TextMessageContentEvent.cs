using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.AgUi.DataObjects.Events;

/// <summary>
///     Streams chunks of text content as they become available
/// </summary>
public sealed record TextMessageContentEvent : AgUiEventBase
{
    /// <inheritdoc />
    [JsonIgnore]
    public override string Type => "TEXT_MESSAGE_CONTENT";

    /// <summary>
    ///     Identifier for the message this content belongs to (matches TextMessageStartEvent.MessageId)
    /// </summary>
    [JsonPropertyName("messageId")]
    public string MessageId { get; init; } = string.Empty;

    /// <summary>
    ///     The text content chunk
    /// </summary>
    [JsonPropertyName("delta")]
    public string Delta { get; init; } = string.Empty;

    /// <summary>
    ///     Sequential index of this chunk within the message
    /// </summary>
    [JsonPropertyName("chunkIndex")]
    public int ChunkIndex { get; init; }
}
