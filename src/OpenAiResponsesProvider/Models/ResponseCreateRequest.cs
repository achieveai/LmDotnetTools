using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.OpenAiResponsesProvider.Models;

/// <summary>
///     A <c>response.create</c> request body for the OpenAI Responses API.
///     Models the subset of fields the provider emits and the mock host inspects.
/// </summary>
public sealed record ResponseCreateRequest
{
    [JsonPropertyName("model")]
    public string? Model { get; init; }

    [JsonPropertyName("input")]
    public IReadOnlyList<ResponseInputItem> Input { get; init; } = [];

    [JsonPropertyName("instructions")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Instructions { get; init; }

    [JsonPropertyName("stream")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Stream { get; init; }

    [JsonPropertyName("temperature")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public float? Temperature { get; init; }

    [JsonPropertyName("top_p")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public float? TopP { get; init; }

    [JsonPropertyName("max_output_tokens")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaxOutputTokens { get; init; }

    [JsonPropertyName("tools")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<ResponseToolSpec>? Tools { get; init; }

    [JsonPropertyName("tool_choice")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonNode? ToolChoice { get; init; }

    [JsonPropertyName("include")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Include { get; init; }

    /// <summary>
    ///     Reasoning configuration (e.g. <c>{ "effort": "high" }</c>). Used by reasoning-capable
    ///     models surfaced through the Copilot Responses transport.
    /// </summary>
    [JsonPropertyName("reasoning")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ResponseReasoningOptions? Reasoning { get; init; }

    /// <summary>
    ///     Text output configuration (e.g. <c>{ "verbosity": "low" }</c>).
    /// </summary>
    [JsonPropertyName("text")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ResponseTextOptions? Text { get; init; }

    /// <summary>
    ///     Whether the server should persist response state. The Copilot CLI sends <c>false</c> and
    ///     carries reasoning across turns via <c>include: ["reasoning.encrypted_content"]</c>.
    /// </summary>
    [JsonPropertyName("store")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Store { get; init; }

    /// <summary>Whether the model may emit tool calls in parallel.</summary>
    [JsonPropertyName("parallel_tool_calls")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? ParallelToolCalls { get; init; }

    /// <summary>
    ///     Who initiated this turn (<c>user</c> or <c>agent</c>). Carried in the WebSocket
    ///     <c>response.create</c> frame.
    /// </summary>
    [JsonPropertyName("initiator")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Initiator { get; init; }

    [JsonPropertyName("metadata")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonNode? Metadata { get; init; }

    [JsonPropertyName("client_metadata")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ClientMetadata { get; init; }

    /// <summary>
    ///     For multi-turn responses-over-WebSocket sessions: the ID of the previous response
    ///     to chain off. Server may use this to load prior conversation state.
    /// </summary>
    [JsonPropertyName("previous_response_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PreviousResponseId { get; init; }

    /// <summary>
    ///     Frame envelope discriminator. WebSocket transport uses an outer wrapper
    ///     <c>{"type":"response.create", ...request fields...}</c>; HTTP omits this and
    ///     posts the request body directly.
    /// </summary>
    [JsonPropertyName("type")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Type { get; init; }
}

/// <summary>
///     Reasoning configuration for a <c>response.create</c> request.
/// </summary>
public sealed record ResponseReasoningOptions
{
    [JsonPropertyName("effort")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Effort { get; init; }

    [JsonPropertyName("summary")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Summary { get; init; }
}

/// <summary>
///     Text output configuration for a <c>response.create</c> request.
/// </summary>
public sealed record ResponseTextOptions
{
    [JsonPropertyName("verbosity")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Verbosity { get; init; }
}

/// <summary>
///     Item in the <c>input</c> array of a <c>response.create</c> request.
///     Models user/assistant message inputs and function-call outputs that come back
///     in subsequent turns.
/// </summary>
public sealed record ResponseInputItem
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "message";

    [JsonPropertyName("role")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Role { get; init; }

    [JsonPropertyName("content")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<ResponseInputContent>? Content { get; init; }

    /// <summary>
    ///     For <c>type = "function_call"</c> and <c>type = "function_call_output"</c>: the call ID
    ///     correlating the model's tool call with its result. Echoed verbatim from the
    ///     <c>response.output_item.added</c> function_call event.
    /// </summary>
    [JsonPropertyName("call_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CallId { get; init; }

    /// <summary>
    ///     For <c>type = "function_call"</c>: the tool name. Required by the Responses API when a
    ///     prior assistant turn's tool call is replayed as conversation history.
    /// </summary>
    [JsonPropertyName("name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; init; }

    /// <summary>
    ///     For <c>type = "function_call"</c>: the JSON-encoded arguments string the model produced.
    ///     Required alongside <see cref="Name"/> — omitting it makes the API reject the input item.
    /// </summary>
    [JsonPropertyName("arguments")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Arguments { get; init; }

    /// <summary>
    ///     For <c>type = "function_call_output"</c>: the textual result of the local tool
    ///     execution that the model should consume on its next turn.
    /// </summary>
    [JsonPropertyName("output")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Output { get; init; }
}

/// <summary>
///     Content block inside a <see cref="ResponseInputItem"/>.
/// </summary>
public sealed record ResponseInputContent
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "input_text";

    [JsonPropertyName("text")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Text { get; init; }
}

/// <summary>
///     Tool specification carried in the <c>tools</c> array of a <c>response.create</c> request.
///     Currently models the <c>function</c> shape only; non-function tool types
///     (e.g. <c>web_search</c>, <c>code_interpreter</c>) round-trip via <see cref="ExtraProperties"/>.
/// </summary>
public sealed record ResponseToolSpec
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "function";

    [JsonPropertyName("name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; init; }

    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; init; }

    [JsonPropertyName("parameters")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonNode? Parameters { get; init; }

    [JsonPropertyName("strict")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Strict { get; init; }

    /// <summary>Additional non-modeled properties carried verbatim through serialization.</summary>
    [JsonExtensionData]
    public JsonObject? ExtraProperties { get; init; }
}
