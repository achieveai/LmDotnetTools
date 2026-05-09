using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.OpenAiResponsesProvider.Models;

/// <summary>
///     Base record for all server→client frames in the OpenAI Responses API event stream.
///     Subclasses model individual event payload shapes; the <see cref="Type"/> property
///     is the discriminator and matches the wire-format <c>type</c> field exactly.
/// </summary>
/// <remarks>
///     The event stream is ordered by <see cref="SequenceNumber"/> within a single response;
///     callers reading multi-response sessions over a WebSocket should track sequence per
///     response ID.
/// </remarks>
public abstract record ResponseEvent
{
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("sequence_number")]
    public int? SequenceNumber { get; init; }
}

/// <summary>
///     Catch-all event for types not yet modeled with a dedicated record. Carries the
///     raw JSON object so consumers can still round-trip and inspect the payload.
/// </summary>
public sealed record GenericResponseEvent : ResponseEvent
{
    [JsonExtensionData]
    public JsonObject? ExtraProperties { get; init; }
}

/// <summary>
///     <c>response.created</c> / <c>response.in_progress</c> / <c>response.completed</c> /
///     <c>response.failed</c>: lifecycle events that carry the full <c>response</c> object
///     payload. The payload is preserved verbatim as a <see cref="JsonElement"/> so the
///     provider can surface fields (id, status, usage) without forcing every field to be modeled.
/// </summary>
public sealed record ResponseLifecycleEvent : ResponseEvent
{
    [JsonPropertyName("response")]
    public JsonElement Response { get; init; }
}

/// <summary>
///     <c>response.output_item.added</c> and <c>response.output_item.done</c> events.
///     The <see cref="Item"/> payload is one of: <c>message</c>, <c>function_call</c>, or
///     a server-side tool item (e.g. <c>web_search_call</c>).
/// </summary>
public sealed record ResponseOutputItemEvent : ResponseEvent
{
    [JsonPropertyName("output_index")]
    public int OutputIndex { get; init; }

    [JsonPropertyName("item")]
    public JsonElement Item { get; init; }
}

/// <summary>
///     <c>response.content_part.added</c> and <c>response.content_part.done</c>:
///     a content-part (typically an output_text block) is opened or closed within an output_item.
/// </summary>
public sealed record ResponseContentPartEvent : ResponseEvent
{
    [JsonPropertyName("item_id")]
    public string ItemId { get; init; } = string.Empty;

    [JsonPropertyName("output_index")]
    public int OutputIndex { get; init; }

    [JsonPropertyName("content_index")]
    public int ContentIndex { get; init; }

    [JsonPropertyName("part")]
    public JsonElement Part { get; init; }
}

/// <summary>
///     <c>response.output_text.delta</c>: incremental token of an in-flight assistant text part.
/// </summary>
public sealed record ResponseOutputTextDeltaEvent : ResponseEvent
{
    [JsonPropertyName("item_id")]
    public string ItemId { get; init; } = string.Empty;

    [JsonPropertyName("output_index")]
    public int OutputIndex { get; init; }

    [JsonPropertyName("content_index")]
    public int ContentIndex { get; init; }

    [JsonPropertyName("delta")]
    public string Delta { get; init; } = string.Empty;
}

/// <summary>
///     <c>response.output_text.done</c>: terminal frame for a streaming text part — carries the
///     concatenated text the model produced so callers can validate against their own delta sum.
/// </summary>
public sealed record ResponseOutputTextDoneEvent : ResponseEvent
{
    [JsonPropertyName("item_id")]
    public string ItemId { get; init; } = string.Empty;

    [JsonPropertyName("output_index")]
    public int OutputIndex { get; init; }

    [JsonPropertyName("content_index")]
    public int ContentIndex { get; init; }

    [JsonPropertyName("text")]
    public string Text { get; init; } = string.Empty;
}

/// <summary>
///     <c>response.function_call_arguments.delta</c>: incremental argument-string token of an
///     in-flight function-call output_item.
/// </summary>
public sealed record ResponseFunctionCallArgumentsDeltaEvent : ResponseEvent
{
    [JsonPropertyName("item_id")]
    public string ItemId { get; init; } = string.Empty;

    [JsonPropertyName("output_index")]
    public int OutputIndex { get; init; }

    [JsonPropertyName("delta")]
    public string Delta { get; init; } = string.Empty;
}

/// <summary>
///     <c>response.function_call_arguments.done</c>: terminal frame for a function-call's
///     argument stream — carries the full <c>arguments</c> JSON string.
/// </summary>
public sealed record ResponseFunctionCallArgumentsDoneEvent : ResponseEvent
{
    [JsonPropertyName("item_id")]
    public string ItemId { get; init; } = string.Empty;

    [JsonPropertyName("output_index")]
    public int OutputIndex { get; init; }

    [JsonPropertyName("arguments")]
    public string Arguments { get; init; } = string.Empty;
}
