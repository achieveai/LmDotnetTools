using System;
using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.AgUi.DataObjects.Events;

/// <summary>
/// Signals tool call completion
/// </summary>
public sealed record ToolCallEndEvent : AgUiEventBase
{
    /// <inheritdoc/>
    [JsonIgnore]
    public override string Type => "TOOL_CALL_END";

    /// <summary>
    /// Identifier for the completed tool call (matches ToolCallStartEvent.ToolCallId)
    /// </summary>
    [JsonPropertyName("toolCallId")]
    public string ToolCallId { get; init; } = string.Empty;

    /// <summary>
    /// Timestamp when the tool call ended (UTC)
    /// </summary>
    [JsonPropertyName("endedAt")]
    public DateTime EndedAt { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// Duration of the tool execution
    /// </summary>
    [JsonPropertyName("duration")]
    public TimeSpan Duration { get; init; }
}
