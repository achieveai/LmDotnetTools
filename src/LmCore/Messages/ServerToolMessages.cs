using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using AchieveAi.LmDotnetTools.LmCore.Utils;

namespace AchieveAi.LmDotnetTools.LmCore.Messages;

/// <summary>
///     Represents a server-side tool use message (built-in tools like web_search, web_fetch, code_execution).
///     These tools execute on the LLM provider's server, not locally.
/// </summary>
[JsonConverter(typeof(ServerToolUseMessageJsonConverter))]
public record ServerToolUseMessage : IMessage
{
    /// <summary>
    ///     The unique identifier for this tool use.
    /// </summary>
    [JsonPropertyName("tool_use_id")]
    public string ToolUseId { get; init; } = string.Empty;

    /// <summary>
    ///     The name of the tool being used (e.g., "web_search", "web_fetch", "bash_code_execution").
    /// </summary>
    [JsonPropertyName("tool_name")]
    public string ToolName { get; init; } = string.Empty;

    /// <summary>
    ///     The input parameters to the tool.
    /// </summary>
    [JsonPropertyName("input")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public JsonElement Input { get; init; }

    /// <summary>
    ///     The role of the message (always Assistant for server tool use).
    /// </summary>
    [JsonPropertyName("role")]
    public Role Role { get; init; } = Role.Assistant;

    /// <summary>
    ///     The agent that initiated this tool use.
    /// </summary>
    [JsonPropertyName("from_agent")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FromAgent { get; init; }

    /// <summary>
    ///     The generation ID for this message.
    ///     Uses camelCase "generationId" to match TextUpdateMessage/TextMessage for frontend merge key consistency.
    /// </summary>
    [JsonPropertyName("generationId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? GenerationId { get; init; }

    /// <summary>
    ///     Additional metadata for the message.
    /// </summary>
    [JsonIgnore]
    public ImmutableDictionary<string, object>? Metadata { get; init; }

    /// <summary>
    ///     Thread ID for conversation continuity.
    /// </summary>
    [JsonPropertyName("threadId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ThreadId { get; init; }

    /// <summary>
    ///     Run ID for this specific execution.
    /// </summary>
    [JsonPropertyName("runId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RunId { get; init; }

    /// <summary>
    ///     Parent Run ID for branching.
    /// </summary>
    [JsonPropertyName("parentRunId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ParentRunId { get; init; }

    /// <summary>
    ///     Order index of this message within its generation.
    /// </summary>
    [JsonPropertyName("messageOrderIdx")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MessageOrderIdx { get; init; }
}

/// <summary>
///     JSON converter for ServerToolUseMessage.
/// </summary>
public class ServerToolUseMessageJsonConverter : ShadowPropertiesJsonConverter<ServerToolUseMessage>
{
    protected override ServerToolUseMessage CreateInstance()
    {
        return new ServerToolUseMessage();
    }
}

/// <summary>
///     Represents results from server-side tool execution.
/// </summary>
[JsonConverter(typeof(ServerToolResultMessageJsonConverter))]
public record ServerToolResultMessage : IMessage
{
    /// <summary>
    ///     The tool use ID this result corresponds to.
    /// </summary>
    [JsonPropertyName("tool_use_id")]
    public string ToolUseId { get; init; } = string.Empty;

    /// <summary>
    ///     The name of the tool that was executed.
    /// </summary>
    [JsonPropertyName("tool_name")]
    public string ToolName { get; init; } = string.Empty;

    /// <summary>
    ///     The result content from the tool execution.
    /// </summary>
    [JsonPropertyName("result")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public JsonElement Result { get; init; }

    /// <summary>
    ///     Whether the result indicates an error.
    /// </summary>
    [JsonPropertyName("is_error")]
    public bool IsError { get; init; }

    /// <summary>
    ///     Error code if IsError is true.
    /// </summary>
    [JsonPropertyName("error_code")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ErrorCode { get; init; }

    /// <summary>
    ///     The role of the message (always Assistant for server tool results).
    /// </summary>
    [JsonPropertyName("role")]
    public Role Role { get; init; } = Role.Assistant;

    /// <summary>
    ///     The agent that generated this result.
    /// </summary>
    [JsonPropertyName("from_agent")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FromAgent { get; init; }

    /// <summary>
    ///     The generation ID for this message.
    ///     Uses camelCase "generationId" to match TextUpdateMessage/TextMessage for frontend merge key consistency.
    /// </summary>
    [JsonPropertyName("generationId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? GenerationId { get; init; }

    /// <summary>
    ///     Additional metadata for the message.
    /// </summary>
    [JsonIgnore]
    public ImmutableDictionary<string, object>? Metadata { get; init; }

    /// <summary>
    ///     Thread ID for conversation continuity.
    /// </summary>
    [JsonPropertyName("threadId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ThreadId { get; init; }

    /// <summary>
    ///     Run ID for this specific execution.
    /// </summary>
    [JsonPropertyName("runId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RunId { get; init; }

    /// <summary>
    ///     Parent Run ID for branching.
    /// </summary>
    [JsonPropertyName("parentRunId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ParentRunId { get; init; }

    /// <summary>
    ///     Order index of this message within its generation.
    /// </summary>
    [JsonPropertyName("messageOrderIdx")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MessageOrderIdx { get; init; }
}

/// <summary>
///     JSON converter for ServerToolResultMessage.
/// </summary>
public class ServerToolResultMessageJsonConverter : ShadowPropertiesJsonConverter<ServerToolResultMessage>
{
    protected override ServerToolResultMessage CreateInstance()
    {
        return new ServerToolResultMessage();
    }
}

/// <summary>
///     Represents citation information from built-in tools.
/// </summary>
public record CitationInfo
{
    /// <summary>
    ///     The type of citation (e.g., "url_citation", "web_search_result_location").
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
    ///     Start index of the citation in the text.
    /// </summary>
    [JsonPropertyName("start_index")]
    public int? StartIndex { get; init; }

    /// <summary>
    ///     End index of the citation in the text.
    /// </summary>
    [JsonPropertyName("end_index")]
    public int? EndIndex { get; init; }
}

/// <summary>
///     Represents a text message with citations from built-in tools.
/// </summary>
[JsonConverter(typeof(TextWithCitationsMessageJsonConverter))]
public record TextWithCitationsMessage : IMessage, ICanGetText
{
    /// <summary>
    ///     The text content.
    /// </summary>
    [JsonPropertyName("text")]
    public required string Text { get; init; }

    /// <summary>
    ///     The citations associated with this text.
    /// </summary>
    [JsonPropertyName("citations")]
    public ImmutableList<CitationInfo>? Citations { get; init; }

    /// <summary>
    ///     Gets the text content.
    /// </summary>
    public string? GetText() => Text;

    /// <summary>
    ///     The role of the message.
    /// </summary>
    [JsonPropertyName("role")]
    public Role Role { get; init; }

    /// <summary>
    ///     The agent that generated this message.
    /// </summary>
    [JsonPropertyName("from_agent")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FromAgent { get; init; }

    /// <summary>
    ///     The generation ID for this message.
    ///     Uses camelCase "generationId" to match TextUpdateMessage/TextMessage for frontend merge key consistency.
    /// </summary>
    [JsonPropertyName("generationId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? GenerationId { get; init; }

    /// <summary>
    ///     Additional metadata for the message.
    /// </summary>
    [JsonIgnore]
    public ImmutableDictionary<string, object>? Metadata { get; init; }

    /// <summary>
    ///     Thread ID for conversation continuity.
    /// </summary>
    [JsonPropertyName("threadId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ThreadId { get; init; }

    /// <summary>
    ///     Run ID for this specific execution.
    /// </summary>
    [JsonPropertyName("runId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RunId { get; init; }

    /// <summary>
    ///     Parent Run ID for branching.
    /// </summary>
    [JsonPropertyName("parentRunId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ParentRunId { get; init; }

    /// <summary>
    ///     Order index of this message within its generation.
    /// </summary>
    [JsonPropertyName("messageOrderIdx")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MessageOrderIdx { get; init; }
}

/// <summary>
///     JSON converter for TextWithCitationsMessage.
/// </summary>
public class TextWithCitationsMessageJsonConverter : ShadowPropertiesJsonConverter<TextWithCitationsMessage>
{
    protected override TextWithCitationsMessage CreateInstance()
    {
        return new TextWithCitationsMessage { Text = string.Empty };
    }
}
