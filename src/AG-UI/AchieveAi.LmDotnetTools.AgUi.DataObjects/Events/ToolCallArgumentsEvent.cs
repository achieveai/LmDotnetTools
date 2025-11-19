using System.Collections.Immutable;
using System.Text.Json.Serialization;
using AchieveAi.LmDotnetTools.LmCore.Utils;

namespace AchieveAi.LmDotnetTools.AgUi.DataObjects.Events;

/// <summary>
/// Streams tool call arguments (supports incremental JSON)
/// </summary>
public sealed record ToolCallArgumentsEvent : AgUiEventBase
{
    /// <inheritdoc/>
    [JsonIgnore]
    public override string Type => "TOOL_CALL_ARGS";

    /// <summary>
    /// Identifier for the tool call these arguments belong to
    /// </summary>
    [JsonPropertyName("toolCallId")]
    public required string ToolCallId { get; init; }

    /// <summary>
    /// Delta chunk of JSON arguments (incremental update)
    /// </summary>
    [JsonPropertyName("delta")]
    public required string Delta { get; init; }

    /// <summary>
    /// Structured JSON fragment updates (optional, for detailed tracking)
    /// </summary>
    [JsonPropertyName("jsonFragmentUpdates")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ImmutableList<JsonFragmentUpdate>? JsonFragmentUpdates { get; init; }
}
