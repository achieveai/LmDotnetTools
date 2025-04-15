using System.Text.Json;
using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.AnthropicProvider.Models;

/// <summary>
/// Base class for different types of content in an Anthropic response.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(AnthropicResponseTextContent), typeDiscriminator: "text")]
[JsonDerivedType(typeof(AnthropicResponseToolUseContent), typeDiscriminator: "tool_use")]
[JsonDerivedType(typeof(AnthropicResponseThinkingContent), typeDiscriminator: "thinking")]
public abstract record AnthropicResponseContent
{
  /// <summary>
  /// The type of content.
  /// </summary>
  [JsonPropertyName("type")]
  public string Type { get; init; } = string.Empty;
}

/// <summary>
/// Represents text content in an Anthropic response.
/// </summary>
public record AnthropicResponseTextContent : AnthropicResponseContent
{
  /// <summary>
  /// Constructor that explicitly sets the Type property to "text"
  /// </summary>
  public AnthropicResponseTextContent()
  {
    Type = "text";
  }

  /// <summary>
  /// The text content.
  /// </summary>
  [JsonPropertyName("text")]
  public string Text { get; init; } = string.Empty;
}

/// <summary>
/// Represents a tool use content in an Anthropic response.
/// </summary>
public record AnthropicResponseToolUseContent : AnthropicResponseContent
{
  /// <summary>
  /// Constructor that explicitly sets the Type property to "tool_use"
  /// </summary>
  public AnthropicResponseToolUseContent()
  {
    Type = "tool_use";
  }

  /// <summary>
  /// The ID of the tool use.
  /// </summary>
  [JsonPropertyName("id")]
  public string Id { get; init; } = string.Empty;

  /// <summary>
  /// The name of the tool.
  /// </summary>
  [JsonPropertyName("name")]
  public string Name { get; init; } = string.Empty;

  /// <summary>
  /// The input to the tool.
  /// </summary>
  [JsonPropertyName("input")]
  public JsonElement Input { get; init; }
}

/// <summary>
/// Represents thinking content in an Anthropic response.
/// </summary>
public record AnthropicResponseThinkingContent : AnthropicResponseContent
{
  /// <summary>
  /// Constructor that explicitly sets the Type property to "thinking"
  /// </summary>
  public AnthropicResponseThinkingContent()
  {
    Type = "thinking";
  }

  /// <summary>
  /// The thinking content.
  /// </summary>
  [JsonPropertyName("thinking")]
  public string Thinking { get; init; } = string.Empty;

  /// <summary>
  /// The signature of the thinking content, if any.
  /// </summary>
  [JsonPropertyName("signature")]
  public string? Signature { get; init; }
} 