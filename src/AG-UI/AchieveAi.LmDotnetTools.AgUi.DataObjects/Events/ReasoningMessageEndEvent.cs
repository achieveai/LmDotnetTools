using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.AgUi.DataObjects.Events;

/// <summary>
///     Signals the completion of a reasoning message
/// </summary>
public sealed record ReasoningMessageEndEvent : AgUiEventBase
{
    /// <inheritdoc />
    [JsonIgnore]
    public override string Type => "REASONING_MESSAGE_END";

    /// <summary>
    ///     Identifier for the reasoning message that completed
    /// </summary>
    [JsonPropertyName("messageId")]
    public string MessageId { get; init; } = string.Empty;

    /// <summary>
    ///     Total number of chunks in this reasoning message
    /// </summary>
    [JsonPropertyName("totalChunks")]
    public int TotalChunks { get; init; }

    /// <summary>
    ///     Total length of the reasoning content in characters
    /// </summary>
    [JsonPropertyName("totalLength")]
    public int TotalLength { get; init; }
}
