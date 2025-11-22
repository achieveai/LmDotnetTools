using System.Text.Json;
using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Models.JsonlEvents;

/// <summary>
/// File history snapshot event for tracking file changes
/// </summary>
public record FileHistorySnapshotEvent : JsonlEventBase
{
    [JsonPropertyName("messageId")]
    public required string MessageId { get; init; }

    [JsonPropertyName("snapshot")]
    public JsonElement? Snapshot { get; init; }

    [JsonPropertyName("isSnapshotUpdate")]
    public bool IsSnapshotUpdate { get; init; }
}
