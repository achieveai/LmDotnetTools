using System.Text.Json;
using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Models.JsonlEvents;

/// <summary>
///     Assistant message event containing the assistant's response
/// </summary>
public record AssistantMessageEvent : JsonlEventBase
{
    [JsonPropertyName("uuid")]
    public required string Uuid { get; init; }

    [JsonPropertyName("parent_tool_use_id")]
    public string? ParentToolUseId { get; init; }

    [JsonPropertyName("session_id")]
    public string? SessionId { get; init; }

    [JsonPropertyName("message")]
    public required AssistantMessage Message { get; init; }

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
}

/// <summary>
///     Assistant message structure
/// </summary>
public record AssistantMessage
{
    [JsonPropertyName("model")]
    public required string Model { get; init; }

    [JsonPropertyName("id")]
    public required string Id { get; init; } // This is the GenerationId

    [JsonPropertyName("type")]
    public string Type { get; init; } = "message";

    [JsonPropertyName("role")]
    public required string Role { get; init; }

    [JsonPropertyName("content")]
    public required List<ContentBlock> Content { get; init; }

    [JsonPropertyName("stop_reason")]
    public string? StopReason { get; init; }

    [JsonPropertyName("stop_sequence")]
    public string? StopSequence { get; init; }

    [JsonPropertyName("usage")]
    public UsageInfo? Usage { get; init; }
}

/// <summary>
///     Content block can be text, thinking, tool_use, tool_result, or image
/// </summary>
public record ContentBlock
{
    [JsonPropertyName("type")]
    public required string Type { get; init; } // "text", "thinking", "tool_use", "tool_result", "image"

    // For text blocks
    [JsonPropertyName("text")]
    public string? Text { get; init; }

    // For thinking blocks
    [JsonPropertyName("thinking")]
    public string? Thinking { get; init; }

    [JsonPropertyName("signature")]
    public string? Signature { get; init; }

    // For tool_use blocks
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("input")]
    public JsonElement? Input { get; init; }

    // For tool_result blocks
    [JsonPropertyName("tool_use_id")]
    public string? ToolUseId { get; init; }

    [JsonPropertyName("content")]
    public JsonElement? Content { get; init; }

    [JsonPropertyName("is_error")]
    public bool? IsError { get; init; }

    // For image blocks
    [JsonPropertyName("source")]
    public ImageSourceBlock? Source { get; init; }
}

/// <summary>
///     Image source block containing base64 encoded image data
/// </summary>
public record ImageSourceBlock
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "base64"; // "base64" or "url"

    [JsonPropertyName("media_type")]
    public string? MediaType { get; init; } // e.g., "image/jpeg", "image/png", "image/gif", "image/webp"

    [JsonPropertyName("data")]
    public string? Data { get; init; } // Base64 encoded image data

    [JsonPropertyName("url")]
    public string? Url { get; init; } // URL for url-type sources
}

/// <summary>
///     Usage information for token tracking
/// </summary>
public record UsageInfo
{
    [JsonPropertyName("input_tokens")]
    public int InputTokens { get; init; }

    [JsonPropertyName("output_tokens")]
    public int OutputTokens { get; init; }

    [JsonPropertyName("cache_creation_input_tokens")]
    public int? CacheCreationInputTokens { get; init; }

    [JsonPropertyName("cache_read_input_tokens")]
    public int? CacheReadInputTokens { get; init; }

    [JsonPropertyName("cache_creation")]
    public CacheCreationInfo? CacheCreation { get; init; }

    [JsonPropertyName("service_tier")]
    public string? ServiceTier { get; init; }
}

/// <summary>
///     Cache creation details
/// </summary>
public record CacheCreationInfo
{
    [JsonPropertyName("ephemeral_5m_input_tokens")]
    public int? Ephemeral5mInputTokens { get; init; }

    [JsonPropertyName("ephemeral_1h_input_tokens")]
    public int? Ephemeral1hInputTokens { get; init; }
}
