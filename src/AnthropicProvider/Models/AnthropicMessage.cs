using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.AnthropicProvider.Models;

/// <summary>
///     Represents a message in the Anthropic API.
/// </summary>
public record AnthropicMessage
{
    /// <summary>
    ///     The role of the message. Can be "user" or "assistant".
    /// </summary>
    [JsonPropertyName("role")]
    public string Role { get; init; } = string.Empty;

    /// <summary>
    ///     The content of the message, which can be text or other content types.
    /// </summary>
    [JsonPropertyName("content")]
    public List<AnthropicContent> Content { get; init; } = [];
}

/// <summary>
///     Represents a content item in an Anthropic message.
/// </summary>
public record AnthropicContent
{
    /// <summary>
    ///     The type of content. Can be "text", "image", or other types supported by Anthropic.
    /// </summary>
    [JsonPropertyName("type")]
    public string Type { get; init; } = string.Empty;

    /// <summary>
    ///     The text of the content if Type is "text".
    /// </summary>
    [JsonPropertyName("text")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Text { get; init; }

    /// <summary>
    ///     The thinking content if Type is "thinking".
    /// </summary>
    [JsonPropertyName("thinking")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Thinking { get; init; }

    /// <summary>
    ///     The signature for thinking content if Type is "thinking".
    /// </summary>
    [JsonPropertyName("signature")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ThinkingSignature { get; init; }

    /// <summary>
    ///     The source of an image if Type is "image".
    /// </summary>
    [JsonPropertyName("source")]
    public ImageSource? Source { get; init; }

    /// <summary>
    ///     The tool usage information if Type is "tool_use".
    /// </summary>
    [JsonPropertyName("tool_use")]
    public AnthropicToolUse? ToolUse { get; init; }

    /// <summary>
    ///     The ID of the tool use, when Type is "tool_use".
    /// </summary>
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    /// <summary>
    ///     The name of the tool, when Type is "tool_use".
    /// </summary>
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    /// <summary>
    ///     The input to the tool, when Type is "tool_use".
    /// </summary>
    [JsonPropertyName("input")]
    public JsonElement? Input { get; init; }

    /// <summary>
    ///     The tool result information when Type is "tool_result".
    /// </summary>
    [JsonPropertyName("tool_result")]
    public AnthropicToolResult? ToolResult { get; init; }

    /// <summary>
    ///     The ID of the tool use that this result is for, when Type is "tool_result".
    /// </summary>
    [JsonPropertyName("tool_use_id")]
    public string? ToolUseId { get; init; }

    /// <summary>
    ///     The text content of the tool result, when Type is "tool_result".
    ///     For text-only results, this is serialized as a string.
    ///     Unconditional [JsonIgnore] avoids collision with ContentRaw's [JsonPropertyName("content")].
    /// </summary>
    [JsonIgnore]
    public string? Content { get; init; }

    /// <summary>
    ///     Multimodal content blocks for tool_result (text + images).
    ///     When set, ContentRaw serializes as an array of content blocks instead of a plain string.
    /// </summary>
    [JsonIgnore]
    public IList<AnthropicContent>? ToolResultContentBlocks { get; init; }

    /// <summary>
    ///     Serialization property for the tool result content.
    ///     Serializes as an array when ToolResultContentBlocks is set, otherwise as a string.
    ///     Follows the same computed-property pattern as SystemRaw in AnthropicRequest.
    /// </summary>
    [JsonPropertyName("content")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonNode? ContentRaw =>
        ToolResultContentBlocks is { Count: > 0 }
            ? JsonSerializer.SerializeToNode(ToolResultContentBlocks, s_contentBlockOptions)
            : Content != null ? JsonValue.Create(Content) : null;

    /// <summary>
    ///     Serialization options for content blocks — omits null fields to keep the JSON clean.
    /// </summary>
    private static readonly JsonSerializerOptions s_contentBlockOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    ///     Cache control directive for prompt caching.
    ///     When set, marks the end of a cacheable prefix.
    /// </summary>
    [JsonPropertyName("cache_control")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AnthropicCacheControl? CacheControl { get; init; }
}

/// <summary>
///     Represents the source of an image in an Anthropic content item.
/// </summary>
public record ImageSource
{
    /// <summary>
    ///     The type of image source. Can be "base64" or "url".
    /// </summary>
    [JsonPropertyName("type")]
    public string Type { get; init; } = string.Empty;

    /// <summary>
    ///     The media type of the image.
    /// </summary>
    [JsonPropertyName("media_type")]
    public string? MediaType { get; init; }

    /// <summary>
    ///     The data of the image if Type is "base64".
    /// </summary>
    [JsonPropertyName("data")]
    public string? Data { get; init; }

    /// <summary>
    ///     The URL of the image if Type is "url".
    /// </summary>
    [JsonPropertyName("url")]
    public string? Url { get; init; }
}

/// <summary>
///     Represents tool usage in an Anthropic content item.
/// </summary>
public record AnthropicToolUse
{
    /// <summary>
    ///     The ID of the tool use.
    /// </summary>
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    /// <summary>
    ///     The name of the tool.
    /// </summary>
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    /// <summary>
    ///     The input to the tool as a JSON string.
    /// </summary>
    [JsonPropertyName("input")]
    public string Input { get; init; } = string.Empty;
}

/// <summary>
///     Represents a tool result in an Anthropic content item.
/// </summary>
public record AnthropicToolResult
{
    /// <summary>
    ///     The ID of the tool use that this result is for.
    /// </summary>
    [JsonPropertyName("tool_use_id")]
    public string ToolUseId { get; init; } = string.Empty;

    /// <summary>
    ///     The content of the tool result.
    /// </summary>
    [JsonPropertyName("content")]
    public JsonElement Content { get; init; }
}
