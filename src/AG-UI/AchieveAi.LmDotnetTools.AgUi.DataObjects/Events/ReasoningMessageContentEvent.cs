using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.AgUi.DataObjects.Events;

/// <summary>
/// Streams reasoning content chunks as they become available
/// </summary>
public sealed record ReasoningMessageContentEvent : AgUiEventBase
{
    /// <inheritdoc/>
    [JsonIgnore]
    public override string Type => "REASONING_MESSAGE_CONTENT";

    /// <summary>
    /// Identifier for the reasoning message this content belongs to
    /// </summary>
    [JsonPropertyName("messageId")]
    public string MessageId { get; init; } = string.Empty;

    /// <summary>
    /// The reasoning content chunk
    /// </summary>
    [JsonPropertyName("delta")]
    public string Delta { get; init; } = string.Empty;

    /// <summary>
    /// Sequential index of this chunk within the message
    /// </summary>
    [JsonPropertyName("chunkIndex")]
    public int ChunkIndex { get; init; }
}
