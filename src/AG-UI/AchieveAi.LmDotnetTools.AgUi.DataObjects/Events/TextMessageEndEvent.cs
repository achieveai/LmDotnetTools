using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.AgUi.DataObjects.Events;

/// <summary>
///     Signals completion of a text message
/// </summary>
public sealed record TextMessageEndEvent : AgUiEventBase
{
    /// <inheritdoc />
    [JsonIgnore]
    public override string Type => "TEXT_MESSAGE_END";

    /// <summary>
    ///     Identifier for the completed message (matches TextMessageStartEvent.MessageId)
    /// </summary>
    [JsonPropertyName("messageId")]
    public string MessageId { get; init; } = string.Empty;

    /// <summary>
    ///     Total number of chunks in this message
    /// </summary>
    [JsonPropertyName("totalChunks")]
    public int TotalChunks { get; init; }

    /// <summary>
    ///     Total length of the message in characters
    /// </summary>
    [JsonPropertyName("totalLength")]
    public int TotalLength { get; init; }
}
