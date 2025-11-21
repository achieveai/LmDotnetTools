using System;
using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.AgUi.DataObjects.Events;

/// <summary>
/// Signals the beginning of a streaming reasoning message
/// </summary>
public sealed record ReasoningMessageStartEvent : AgUiEventBase
{
    /// <inheritdoc/>
    [JsonIgnore]
    public override string Type => "REASONING_MESSAGE_START";

    /// <summary>
    /// Unique identifier for this reasoning message
    /// </summary>
    [JsonPropertyName("messageId")]
    public string MessageId { get; init; } = Guid.NewGuid().ToString();

    /// <summary>
    /// Visibility level for this reasoning message (plain, summary, encrypted)
    /// </summary>
    [JsonPropertyName("visibility")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Visibility { get; init; }
}
