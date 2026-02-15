using System.Text.Json;
using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.AnthropicProvider.Models;

/// <summary>
///     JsonConverter that eagerly clones JsonElement during deserialization,
///     producing a stable copy that survives disposal of the source JsonDocument.
///     This is needed because SSE streaming disposes the per-line JsonDocument
///     after deserialization, leaving JsonElement properties as dangling references.
/// </summary>
internal sealed class StableJsonElementConverter : JsonConverter<JsonElement>
{
    public override JsonElement Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        return doc.RootElement.Clone();
    }

    public override void Write(Utf8JsonWriter writer, JsonElement value, JsonSerializerOptions options)
    {
        if (value.ValueKind == JsonValueKind.Undefined)
        {
            writer.WriteNullValue();
            return;
        }

        value.WriteTo(writer);
    }
}

/// <summary>
///     Base class for different types of content in an Anthropic response.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(AnthropicResponseTextContent), "text")]
[JsonDerivedType(typeof(AnthropicResponseToolUseContent), "tool_use")]
[JsonDerivedType(typeof(AnthropicResponseThinkingContent), "thinking")]
[JsonDerivedType(typeof(AnthropicResponseServerToolUseContent), "server_tool_use")]
[JsonDerivedType(typeof(AnthropicWebSearchToolResultContent), "web_search_tool_result")]
[JsonDerivedType(typeof(AnthropicWebFetchToolResultContent), "web_fetch_tool_result")]
[JsonDerivedType(typeof(AnthropicBashCodeExecutionToolResultContent), "bash_code_execution_tool_result")]
[JsonDerivedType(typeof(AnthropicTextEditorCodeExecutionToolResultContent), "text_editor_code_execution_tool_result")]
public abstract record AnthropicResponseContent
{
    /// <summary>
    ///     The type of content. This is handled by the polymorphic discriminator,
    ///     but kept for runtime type identification.
    /// </summary>
    [JsonIgnore]
    public string Type { get; init; } = string.Empty;
}

/// <summary>
///     Represents text content in an Anthropic response.
/// </summary>
public record AnthropicResponseTextContent : AnthropicResponseContent
{
    /// <summary>
    ///     Constructor that explicitly sets the Type property to "text"
    /// </summary>
    public AnthropicResponseTextContent()
    {
        Type = "text";
    }

    /// <summary>
    ///     The text content.
    /// </summary>
    [JsonPropertyName("text")]
    public string Text { get; init; } = string.Empty;

    /// <summary>
    ///     Citations associated with this text content (from web_search or web_fetch results).
    /// </summary>
    [JsonPropertyName("citations")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<Citation>? Citations { get; init; }
}

/// <summary>
///     Represents a tool use content in an Anthropic response.
/// </summary>
public record AnthropicResponseToolUseContent : AnthropicResponseContent
{
    /// <summary>
    ///     Constructor that explicitly sets the Type property to "tool_use"
    /// </summary>
    public AnthropicResponseToolUseContent()
    {
        Type = "tool_use";
    }

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
    ///     The input to the tool.
    /// </summary>
    [JsonPropertyName("input")]
    [JsonConverter(typeof(StableJsonElementConverter))]
    public JsonElement Input { get; init; }
}

/// <summary>
///     Represents thinking content in an Anthropic response.
/// </summary>
public record AnthropicResponseThinkingContent : AnthropicResponseContent
{
    /// <summary>
    ///     Constructor that explicitly sets the Type property to "thinking"
    /// </summary>
    public AnthropicResponseThinkingContent()
    {
        Type = "thinking";
    }

    /// <summary>
    ///     The thinking content.
    /// </summary>
    [JsonPropertyName("thinking")]
    public string Thinking { get; init; } = string.Empty;

    /// <summary>
    ///     The signature of the thinking content, if any.
    /// </summary>
    [JsonPropertyName("signature")]
    public string? Signature { get; init; }
}

/// <summary>
///     Represents a server-initiated tool use (built-in tools like web_search).
/// </summary>
public record AnthropicResponseServerToolUseContent : AnthropicResponseContent
{
    /// <summary>
    ///     Constructor that explicitly sets the Type property to "server_tool_use"
    /// </summary>
    public AnthropicResponseServerToolUseContent()
    {
        Type = "server_tool_use";
    }

    /// <summary>
    ///     The ID of the server tool use.
    /// </summary>
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    /// <summary>
    ///     The name of the tool (e.g., "web_search", "web_fetch", "bash_code_execution").
    /// </summary>
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    /// <summary>
    ///     The input to the tool.
    /// </summary>
    [JsonPropertyName("input")]
    [JsonConverter(typeof(StableJsonElementConverter))]
    public JsonElement Input { get; init; }
}

/// <summary>
///     Represents web search tool result content.
/// </summary>
public record AnthropicWebSearchToolResultContent : AnthropicResponseContent
{
    /// <summary>
    ///     Constructor that explicitly sets the Type property to "web_search_tool_result"
    /// </summary>
    public AnthropicWebSearchToolResultContent()
    {
        Type = "web_search_tool_result";
    }

    /// <summary>
    ///     The ID of the tool use this result corresponds to.
    /// </summary>
    [JsonPropertyName("tool_use_id")]
    public string ToolUseId { get; init; } = string.Empty;

    /// <summary>
    ///     The search results or error content.
    /// </summary>
    [JsonPropertyName("content")]
    [JsonConverter(typeof(StableJsonElementConverter))]
    public JsonElement Content { get; init; }
}

/// <summary>
///     Represents a single web search result.
/// </summary>
public record WebSearchResult
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "web_search_result";

    [JsonPropertyName("url")]
    public string? Url { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("encrypted_content")]
    public string? EncryptedContent { get; init; }

    [JsonPropertyName("page_age")]
    public string? PageAge { get; init; }
}

/// <summary>
///     Represents web fetch tool result content.
/// </summary>
public record AnthropicWebFetchToolResultContent : AnthropicResponseContent
{
    /// <summary>
    ///     Constructor that explicitly sets the Type property to "web_fetch_tool_result"
    /// </summary>
    public AnthropicWebFetchToolResultContent()
    {
        Type = "web_fetch_tool_result";
    }

    /// <summary>
    ///     The ID of the tool use this result corresponds to.
    /// </summary>
    [JsonPropertyName("tool_use_id")]
    public string ToolUseId { get; init; } = string.Empty;

    /// <summary>
    ///     The fetch result content.
    /// </summary>
    [JsonPropertyName("content")]
    [JsonConverter(typeof(StableJsonElementConverter))]
    public JsonElement Content { get; init; }
}

/// <summary>
///     Represents bash code execution tool result content.
/// </summary>
public record AnthropicBashCodeExecutionToolResultContent : AnthropicResponseContent
{
    /// <summary>
    ///     Constructor that explicitly sets the Type property to "bash_code_execution_tool_result"
    /// </summary>
    public AnthropicBashCodeExecutionToolResultContent()
    {
        Type = "bash_code_execution_tool_result";
    }

    /// <summary>
    ///     The ID of the tool use this result corresponds to.
    /// </summary>
    [JsonPropertyName("tool_use_id")]
    public string ToolUseId { get; init; } = string.Empty;

    /// <summary>
    ///     The execution result content.
    /// </summary>
    [JsonPropertyName("content")]
    [JsonConverter(typeof(StableJsonElementConverter))]
    public JsonElement Content { get; init; }
}

/// <summary>
///     Represents text editor code execution tool result content.
/// </summary>
public record AnthropicTextEditorCodeExecutionToolResultContent : AnthropicResponseContent
{
    /// <summary>
    ///     Constructor that explicitly sets the Type property to "text_editor_code_execution_tool_result"
    /// </summary>
    public AnthropicTextEditorCodeExecutionToolResultContent()
    {
        Type = "text_editor_code_execution_tool_result";
    }

    /// <summary>
    ///     The ID of the tool use this result corresponds to.
    /// </summary>
    [JsonPropertyName("tool_use_id")]
    public string ToolUseId { get; init; } = string.Empty;

    /// <summary>
    ///     The execution result content.
    /// </summary>
    [JsonPropertyName("content")]
    [JsonConverter(typeof(StableJsonElementConverter))]
    public JsonElement Content { get; init; }
}

/// <summary>
///     Represents a citation reference in text content.
/// </summary>
public record Citation
{
    /// <summary>
    ///     The type of citation (e.g., "web_search_result_location", "char_location").
    /// </summary>
    [JsonPropertyName("type")]
    public string? Type { get; init; }

    /// <summary>
    ///     The URL of the cited source.
    /// </summary>
    [JsonPropertyName("url")]
    public string? Url { get; init; }

    /// <summary>
    ///     The title of the cited source.
    /// </summary>
    [JsonPropertyName("title")]
    public string? Title { get; init; }

    /// <summary>
    ///     The actual text that was cited.
    /// </summary>
    [JsonPropertyName("cited_text")]
    public string? CitedText { get; init; }

    /// <summary>
    ///     Encrypted index for citation lookup.
    /// </summary>
    [JsonPropertyName("encrypted_index")]
    public string? EncryptedIndex { get; init; }

    /// <summary>
    ///     Start character index in the text.
    /// </summary>
    [JsonPropertyName("start_char_index")]
    public int? StartCharIndex { get; init; }

    /// <summary>
    ///     End character index in the text.
    /// </summary>
    [JsonPropertyName("end_char_index")]
    public int? EndCharIndex { get; init; }
}

/// <summary>
///     Represents text content with citations from built-in tools.
/// </summary>
public record AnthropicResponseTextWithCitationsContent : AnthropicResponseTextContent
{
    /// <summary>
    ///     The citations associated with this text content.
    /// </summary>
    [JsonPropertyName("citations")]
    public new List<Citation>? Citations { get; init; }
}
