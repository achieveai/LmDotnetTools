using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Nodes;

namespace AchieveAi.LmDotnetTools.AnthropicProvider.Models;

/// <summary>
/// Represents a tool that can be used by Claude.
/// </summary>
public record AnthropicTool
{
  /// <summary>
  /// The type of the tool. Currently only "function" is supported.
  /// </summary>
  [JsonPropertyName("type")]
  public string Type { get; init; } = "function";

  /// <summary>
  /// The function definition if Type is "function".
  /// </summary>
  [JsonPropertyName("function")]
  public AnthropicFunction? Function { get; init; }
}

/// <summary>
/// Represents a function that can be called by Claude.
/// </summary>
public record AnthropicFunction
{
  /// <summary>
  /// The name of the function.
  /// </summary>
  [JsonPropertyName("name")]
  public string Name { get; init; } = string.Empty;

  /// <summary>
  /// The description of the function.
  /// </summary>
  [JsonPropertyName("description")]
  public string? Description { get; init; }

  /// <summary>
  /// The parameters of the function as a JSON schema object.
  /// </summary>
  [JsonPropertyName("parameters")]
  public JsonObject? Parameters { get; init; }
}

/// <summary>
/// Represents a tool call made by Claude.
/// </summary>
public record AnthropicToolCall
{
  /// <summary>
  /// The ID of the tool call.
  /// </summary>
  [JsonPropertyName("id")]
  public string Id { get; init; } = string.Empty;
  
  /// <summary>
  /// The type of the tool. Currently only "function" is supported.
  /// </summary>
  [JsonPropertyName("type")]
  public string Type { get; init; } = "function";
  
  /// <summary>
  /// The function that was called.
  /// </summary>
  [JsonPropertyName("function")]
  public AnthropicToolCallFunction Function { get; init; } = new();
}

/// <summary>
/// Represents the function that was called in a tool call.
/// </summary>
public record AnthropicToolCallFunction
{
  /// <summary>
  /// The name of the function that was called.
  /// </summary>
  [JsonPropertyName("name")]
  public string Name { get; init; } = string.Empty;
  
  /// <summary>
  /// The arguments to the function as a JSON string.
  /// </summary>
  [JsonPropertyName("arguments")]
  public string Arguments { get; init; } = string.Empty;
}

/// <summary>
/// Represents the input to submit a tool output back to Claude.
/// </summary>
public record AnthropicToolOutput
{
  /// <summary>
  /// The ID of the tool call that this output is for.
  /// </summary>
  [JsonPropertyName("tool_call_id")]
  public string ToolCallId { get; init; } = string.Empty;
  
  /// <summary>
  /// The output of the tool call.
  /// </summary>
  [JsonPropertyName("output")]
  public string Output { get; init; } = string.Empty;
}
