using System.Text.Json;
using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Models.JsonlEvents;

/// <summary>
///     User message event containing user input
/// </summary>
public record UserMessageEvent : JsonlEventBase
{
    [JsonPropertyName("uuid")]
    public required string Uuid { get; init; }

    [JsonPropertyName("parent_uuid")]
    public string? ParentUuid { get; init; }

    [JsonPropertyName("session_id")]
    public required string SessionId { get; init; }

    [JsonPropertyName("message")]
    public required UserMessage Message { get; init; }

    [JsonPropertyName("timestamp")]
    public DateTime? Timestamp { get; init; }

    [JsonPropertyName("cwd")]
    public string? Cwd { get; init; }

    [JsonPropertyName("git_branch")]
    public string? GitBranch { get; init; }

    [JsonPropertyName("version")]
    public string? Version { get; init; }

    [JsonPropertyName("is_sidechain")]
    public bool IsSidechain { get; init; }

    [JsonPropertyName("user_type")]
    public string? UserType { get; init; }

    [JsonPropertyName("tool_use_result")]
    public JsonElement? ToolUseResult { get; init; }
}

/// <summary>
///     User message structure
/// </summary>
public record UserMessage
{
    [JsonPropertyName("role")]
    public required string Role { get; init; }

    [JsonPropertyName("content")]
    public required JsonElement Content { get; init; } // Can be string or array of content blocks
}
