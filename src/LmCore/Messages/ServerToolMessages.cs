using System.Collections.Immutable;
using System.Text.Json.Serialization;
using AchieveAi.LmDotnetTools.LmCore.Utils;

namespace AchieveAi.LmDotnetTools.LmCore.Messages;

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
