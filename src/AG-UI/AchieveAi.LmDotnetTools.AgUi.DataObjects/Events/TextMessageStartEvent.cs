using System.Text.Json.Serialization;
using AchieveAi.LmDotnetTools.AgUi.DataObjects.Enums;

namespace AchieveAi.LmDotnetTools.AgUi.DataObjects.Events;

/// <summary>
/// Signals the beginning of a new text message from the agent
/// </summary>
public sealed record TextMessageStartEvent : AgUiEventBase
{
    /// <inheritdoc/>
    [JsonIgnore]
    public override string Type => "TEXT_MESSAGE_START";

    /// <summary>
    /// Unique identifier for this message
    /// </summary>
    [JsonPropertyName("messageId")]
    public string MessageId { get; init; } = Guid.NewGuid().ToString();

    /// <summary>
    /// Role of the message sender
    /// </summary>
    [JsonPropertyName("role")]
    public MessageRole Role { get; init; } = MessageRole.Assistant;
}
