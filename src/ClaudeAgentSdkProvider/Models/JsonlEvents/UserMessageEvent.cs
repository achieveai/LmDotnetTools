using System.Text.Json;
using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Models.JsonlEvents;

/// <summary>
/// User message event containing user input
/// </summary>
public record UserMessageEvent : JsonlEventBase
{
    [JsonPropertyName("uuid")]
    public required string Uuid { get; init; }

    [JsonPropertyName("parentUuid")]
    public string? ParentUuid { get; init; }

    [JsonPropertyName("sessionId")]
    public required string SessionId { get; init; }

    [JsonPropertyName("message")]
    public required UserMessage Message { get; init; }

    [JsonPropertyName("timestamp")]
    public required DateTime Timestamp { get; init; }

    [JsonPropertyName("cwd")]
    public string? Cwd { get; init; }

    [JsonPropertyName("gitBranch")]
    public string? GitBranch { get; init; }

    [JsonPropertyName("version")]
    public string? Version { get; init; }

    [JsonPropertyName("isSidechain")]
    public bool IsSidechain { get; init; }

    [JsonPropertyName("userType")]
    public string? UserType { get; init; }

    [JsonPropertyName("toolUseResult")]
    public JsonElement? ToolUseResult { get; init; }
}

/// <summary>
/// User message structure
/// </summary>
public record UserMessage
{
    [JsonPropertyName("role")]
    public required string Role { get; init; }

    [JsonPropertyName("content")]
    public required JsonElement Content { get; init; }  // Can be string or array of content blocks
}
