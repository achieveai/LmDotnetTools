using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.AgUi.DataObjects.Events;

/// <summary>
/// Signals the start of reasoning (chain-of-thought) phase
/// </summary>
public sealed record ReasoningStartEvent : AgUiEventBase
{
    /// <inheritdoc/>
    [JsonIgnore]
    public override string Type => "REASONING_START";

    /// <summary>
    /// Optional encrypted reasoning content (visibility=Encrypted)
    /// </summary>
    [JsonPropertyName("encryptedReasoning")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EncryptedReasoning { get; init; }
}
