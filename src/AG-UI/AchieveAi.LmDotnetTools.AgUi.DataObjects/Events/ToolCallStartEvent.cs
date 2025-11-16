using System;
using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.AgUi.DataObjects.Events;

/// <summary>
/// Initiates a tool/function call
/// </summary>
public sealed record ToolCallStartEvent : AgUiEventBase
{
    /// <inheritdoc/>
    [JsonPropertyName("type")]
    public override string Type => "TOOL_CALL_START";

    /// <summary>
    /// Unique identifier for this tool call
    /// </summary>
    [JsonPropertyName("toolCallId")]
    public string ToolCallId { get; init; } = Guid.NewGuid().ToString();

    /// <summary>
    /// Name of the tool being called
    /// </summary>
    [JsonPropertyName("toolName")]
    public string ToolName { get; init; } = string.Empty;

    /// <summary>
    /// Timestamp when the tool call started (UTC)
    /// </summary>
    [JsonPropertyName("startedAt")]
    public DateTime StartedAt { get; init; } = DateTime.UtcNow;
}
