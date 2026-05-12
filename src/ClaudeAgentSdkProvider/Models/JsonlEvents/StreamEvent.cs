using System.Text.Json;
using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Models.JsonlEvents;

/// <summary>
///     Low-level stream event emitted when Claude CLI is run with partial messages enabled.
/// </summary>
public record StreamEvent : JsonlEventBase
{
    [JsonPropertyName("event")]
    public required JsonElement Event { get; init; }

    [JsonPropertyName("session_id")]
    public required string SessionId { get; init; }

    [JsonPropertyName("parent_tool_use_id")]
    public string? ParentToolUseId { get; init; }

    [JsonPropertyName("uuid")]
    public required string Uuid { get; init; }

    public string? EventType =>
        Event.ValueKind == JsonValueKind.Object
        && Event.TryGetProperty("type", out var type)
        && type.ValueKind == JsonValueKind.String
            ? type.GetString()
            : null;
}
