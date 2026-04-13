using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Models.JsonlEvents;

/// <summary>
///     Summary event containing a brief description and leaf UUID
/// </summary>
public record SummaryEvent : JsonlEventBase
{
    [JsonPropertyName("summary")]
    public required string Summary { get; init; }

    [JsonPropertyName("leaf_uuid")]
    public required string LeafUuid { get; init; }
}
