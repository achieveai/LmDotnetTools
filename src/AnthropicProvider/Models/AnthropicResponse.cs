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
  public List<AnthropicContent> Content { get; init; } = new List<AnthropicContent>();

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
  /// The number of output tokens generated.
  /// </summary>
  [JsonPropertyName("output_tokens")]
  public int OutputTokens { get; init; }
}

/// <summary>
/// Represents a streaming event from the Anthropic API.
/// </summary>
public record AnthropicStreamEvent
{
  /// <summary>
  /// The type of the event.
  /// </summary>
  [JsonPropertyName("type")]
  public string Type { get; init; } = string.Empty;

  /// <summary>
  /// The delta content for "content_block_delta" events.
  /// </summary>
  [JsonPropertyName("delta")]
  public AnthropicDelta? Delta { get; init; }

  /// <summary>
  /// The index of the content block for "content_block_delta" and "content_block_stop" events.
  /// </summary>
  [JsonPropertyName("index")]
  public int? Index { get; init; }

  /// <summary>
  /// The message for "message_start", "message_delta", and "message_stop" events.
  /// </summary>
  [JsonPropertyName("message")]
  public AnthropicResponse? Message { get; init; }

  /// <summary>
  /// Usage information for "message_stop" events.
  /// </summary>
  [JsonPropertyName("usage")]
  public AnthropicUsage? Usage { get; init; }
}

/// <summary>
/// Represents a delta update in a streaming response.
/// </summary>
public record AnthropicDelta
{
  /// <summary>
  /// The type of the delta.
  /// </summary>
  [JsonPropertyName("type")]
  public string Type { get; init; } = string.Empty;

  /// <summary>
  /// The text update for "text_delta" deltas.
  /// </summary>
  [JsonPropertyName("text")]
  public string? Text { get; init; }

  /// <summary>
  /// The tool calls for "tool_use" deltas.
  /// </summary>
  [JsonPropertyName("tool_calls")]
  public List<AnthropicToolCall>? ToolCalls { get; init; }
}
