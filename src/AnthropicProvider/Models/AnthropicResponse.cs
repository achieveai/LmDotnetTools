using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.AnthropicProvider.Models;

/// <summary>
/// Represents a response from the Anthropic API.
/// </summary>
public record AnthropicResponse
{
    /// <summary>
    /// The ID of the response.
    /// </summary>
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    /// <summary>
    /// The type of the response. Usually "message".
    /// </summary>
    [JsonPropertyName("type")]
    public string Type { get; init; } = string.Empty;

    /// <summary>
    /// The role of the message. Usually "assistant".
    /// </summary>
    [JsonPropertyName("role")]
    public string Role { get; init; } = string.Empty;

    /// <summary>
    /// The content of the response.
    /// </summary>
    [JsonPropertyName("content")]
    public List<AnthropicResponseContent> Content { get; init; } =
        [];

    /// <summary>
    /// The model that generated the response.
    /// </summary>
    [JsonPropertyName("model")]
    public string Model { get; init; } = string.Empty;

    /// <summary>
    /// The reason the response stopped.
    /// </summary>
    [JsonPropertyName("stop_reason")]
    public string? StopReason { get; init; }

    /// <summary>
    /// The sequence that stopped the response, if any.
    /// </summary>
    [JsonPropertyName("stop_sequence")]
    public string? StopSequence { get; init; }

    /// <summary>
    /// Usage information for the request.
    /// </summary>
    [JsonPropertyName("usage")]
    public AnthropicUsage? Usage { get; init; }
}

/// <summary>
/// Represents usage information for an Anthropic API request.
/// </summary>
public record AnthropicUsage
{
    /// <summary>
    /// The number of input tokens processed.
    /// </summary>
    [JsonPropertyName("input_tokens")]
    public int InputTokens { get; init; }

    /// <summary>
    /// The number of tokens used for cache creation, if any.
    /// </summary>
    [JsonPropertyName("cache_creation_input_tokens")]
    public int CacheCreationInputTokens { get; init; }

    /// <summary>
    /// The number of tokens read from cache, if any.
    /// </summary>
    [JsonPropertyName("cache_read_input_tokens")]
    public int CacheReadInputTokens { get; init; }

    /// <summary>
    /// The number of output tokens generated.
    /// </summary>
    [JsonPropertyName("output_tokens")]
    public int OutputTokens { get; init; }
}

/// <summary>
/// Base record for all streaming events from the Anthropic API.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(AnthropicMessageStartEvent), typeDiscriminator: "message_start")]
[JsonDerivedType(typeof(AnthropicContentBlockStartEvent), typeDiscriminator: "content_block_start")]
[JsonDerivedType(typeof(AnthropicContentBlockDeltaEvent), typeDiscriminator: "content_block_delta")]
[JsonDerivedType(typeof(AnthropicContentBlockStopEvent), typeDiscriminator: "content_block_stop")]
[JsonDerivedType(typeof(AnthropicMessageDeltaEvent), typeDiscriminator: "message_delta")]
[JsonDerivedType(typeof(AnthropicMessageStopEvent), typeDiscriminator: "message_stop")]
[JsonDerivedType(typeof(AnthropicPingEvent), typeDiscriminator: "ping")]
[JsonDerivedType(typeof(AnthropicErrorEvent), typeDiscriminator: "error")]
public record AnthropicStreamEvent
{
    /// <summary>
    /// The type of the event.
    /// </summary>
    [JsonPropertyName("type")]
    public string Type =>
        this switch
        {
            AnthropicMessageStartEvent _ => "message_start",
            AnthropicContentBlockStartEvent _ => "content_block_start",
            AnthropicContentBlockDeltaEvent _ => "content_block_delta",
            AnthropicContentBlockStopEvent _ => "content_block_stop",
            AnthropicMessageDeltaEvent _ => "message_delta",
            AnthropicMessageStopEvent _ => "message_stop",
            AnthropicPingEvent _ => "ping",
            AnthropicErrorEvent _ => "error",
            _ => throw new InvalidOperationException("Invalid event type"),
        };
}

/// <summary>
/// Represents the start of a message in a streaming response.
/// </summary>
public record AnthropicMessageStartEvent : AnthropicStreamEvent
{
    /// <summary>
    /// The message being started.
    /// </summary>
    [JsonPropertyName("message")]
    public AnthropicResponse? Message { get; init; }
}

/// <summary>
/// Represents the start of a content block in a streaming response.
/// </summary>
public record AnthropicContentBlockStartEvent : AnthropicStreamEvent
{
    /// <summary>
    /// The index of the content block.
    /// </summary>
    [JsonPropertyName("index")]
    public int Index { get; init; }

    /// <summary>
    /// The content block being started.
    /// </summary>
    [JsonPropertyName("content_block")]
    public AnthropicResponseContent? ContentBlock { get; init; }
}

/// <summary>
/// Represents a delta update to a content block in a streaming response.
/// </summary>
public record AnthropicContentBlockDeltaEvent : AnthropicStreamEvent
{
    /// <summary>
    /// The index of the content block being updated.
    /// </summary>
    [JsonPropertyName("index")]
    public int Index { get; init; }

    /// <summary>
    /// The delta update to the content block.
    /// </summary>
    [JsonPropertyName("delta")]
    public AnthropicDelta? Delta { get; init; }
}

/// <summary>
/// Represents the end of a content block in a streaming response.
/// </summary>
public record AnthropicContentBlockStopEvent : AnthropicStreamEvent
{
    /// <summary>
    /// The index of the content block that has ended.
    /// </summary>
    [JsonPropertyName("index")]
    public int Index { get; init; }
}

/// <summary>
/// Represents a delta update to a message in a streaming response.
/// </summary>
public record AnthropicMessageDeltaEvent : AnthropicStreamEvent
{
    /// <summary>
    /// The delta update to the message.
    /// </summary>
    [JsonPropertyName("delta")]
    public AnthropicMessageDelta? Delta { get; init; }

    /// <summary>
    /// Usage information for the message delta.
    /// </summary>
    [JsonPropertyName("usage")]
    public AnthropicUsage? Usage { get; init; }
}

/// <summary>
/// Represents the end of a message in a streaming response.
/// </summary>
public record AnthropicMessageStopEvent : AnthropicStreamEvent { }

/// <summary>
/// Represents a ping event in a streaming response.
/// </summary>
public record AnthropicPingEvent : AnthropicStreamEvent { }

/// <summary>
/// Represents an error event in a streaming response.
/// </summary>
public record AnthropicErrorEvent : AnthropicStreamEvent
{
    /// <summary>
    /// The error details.
    /// </summary>
    [JsonPropertyName("error")]
    public AnthropicStreamingError? Error { get; init; }
}

/// <summary>
/// Represents an error in a streaming response.
/// </summary>
public record AnthropicStreamingError
{
    /// <summary>
    /// The type of error.
    /// </summary>
    [JsonPropertyName("type")]
    public string Type { get; init; } = string.Empty;

    /// <summary>
    /// The error message.
    /// </summary>
    [JsonPropertyName("message")]
    public string Message { get; init; } = string.Empty;
}

/// <summary>
/// Represents delta updates to a message.
/// </summary>
public record AnthropicMessageDelta
{
    /// <summary>
    /// The stop reason for the message.
    /// </summary>
    [JsonPropertyName("stop_reason")]
    public string? StopReason { get; init; }

    /// <summary>
    /// The stop sequence for the message.
    /// </summary>
    [JsonPropertyName("stop_sequence")]
    public string? StopSequence { get; init; }
}

/// <summary>
/// Represents various types of delta updates in a streaming response.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(AnthropicTextDelta), typeDiscriminator: "text_delta")]
[JsonDerivedType(typeof(AnthropicInputJsonDelta), typeDiscriminator: "input_json_delta")]
[JsonDerivedType(typeof(AnthropicThinkingDelta), typeDiscriminator: "thinking_delta")]
[JsonDerivedType(typeof(AnthropicSignatureDelta), typeDiscriminator: "signature_delta")]
[JsonDerivedType(typeof(AnthropicToolCallsDelta), typeDiscriminator: "tool_calls_delta")]
public record AnthropicDelta
{
    /// <summary>
    /// The type of the delta.
    /// </summary>
    [JsonPropertyName("type")]
    public string Type =>
        this switch
        {
            AnthropicTextDelta _ => "text_delta",
            AnthropicInputJsonDelta _ => "input_json_delta",
            AnthropicThinkingDelta _ => "thinking_delta",
            AnthropicSignatureDelta _ => "signature_delta",
            AnthropicToolCallsDelta _ => "tool_calls_delta",
            _ => throw new InvalidOperationException("Invalid delta type"),
        };
}

/// <summary>
/// Represents a text delta update in a streaming response.
/// </summary>
public record AnthropicTextDelta : AnthropicDelta
{
    /// <summary>
    /// The text content of the delta.
    /// </summary>
    [JsonPropertyName("text")]
    public string Text { get; init; } = string.Empty;
}

/// <summary>
/// Represents an input JSON delta update in a streaming response.
/// </summary>
public record AnthropicInputJsonDelta : AnthropicDelta
{
    /// <summary>
    /// The partial JSON content of the delta.
    /// </summary>
    [JsonPropertyName("partial_json")]
    public string PartialJson { get; init; } = string.Empty;
}

/// <summary>
/// Represents a thinking delta update in a streaming response.
/// </summary>
public record AnthropicThinkingDelta : AnthropicDelta
{
    /// <summary>
    /// The thinking content of the delta.
    /// </summary>
    [JsonPropertyName("thinking")]
    public string Thinking { get; init; } = string.Empty;
}

/// <summary>
/// Represents a signature delta update in a streaming response.
/// </summary>
public record AnthropicSignatureDelta : AnthropicDelta
{
    /// <summary>
    /// The signature content of the delta.
    /// </summary>
    [JsonPropertyName("signature")]
    public string Signature { get; init; } = string.Empty;
}

/// <summary>
/// Represents a tool calls delta update in a streaming response.
/// </summary>
public record AnthropicToolCallsDelta : AnthropicDelta
{
    /// <summary>
    /// The tool calls in this delta.
    /// </summary>
    [JsonPropertyName("tool_calls")]
    public List<AnthropicDeltaToolCall> ToolCalls { get; init; } =
        [];
}

/// <summary>
/// Represents a tool call in a delta update.
/// </summary>
public record AnthropicDeltaToolCall
{
    /// <summary>
    /// The index of the tool call.
    /// </summary>
    [JsonPropertyName("index")]
    public int Index { get; init; }

    /// <summary>
    /// The ID of the tool call.
    /// </summary>
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    /// <summary>
    /// The type of the tool call.
    /// </summary>
    [JsonPropertyName("type")]
    public string Type { get; init; } = string.Empty;

    /// <summary>
    /// The name of the tool.
    /// </summary>
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// The input to the tool.
    /// </summary>
    [JsonPropertyName("input")]
    public System.Text.Json.JsonElement Input { get; init; }
}
