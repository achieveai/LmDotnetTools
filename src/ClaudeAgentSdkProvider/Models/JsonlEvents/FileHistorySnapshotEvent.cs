using System.Text.Json;
using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Models.JsonlEvents;

/// <summary>
/// File history snapshot event for tracking file changes
/// </summary>
public record FileHistorySnapshotEvent : JsonlEventBase
{
    [JsonPropertyName("message_id")]
    public required string MessageId { get; init; }

    [JsonPropertyName("snapshot")]
    public JsonElement? Snapshot { get; init; }

    [JsonPropertyName("is_snapshot_update")]
    public bool IsSnapshotUpdate { get; init; }
}
