using System.Text.Json.Serialization;
using System.Text.Json;

namespace AchieveAi.LmDotnetTools.AnthropicProvider.Models;

/// <summary>
/// Represents a message in the Anthropic API.
/// </summary>
public record AnthropicMessage
{
  /// <summary>
  /// The role of the message. Can be "user" or "assistant".
  /// </summary>
  [JsonPropertyName("role")]
  public string Role { get; init; } = string.Empty;

  /// <summary>
  /// The content of the message, which can be text or other content types.
  /// </summary>
  [JsonPropertyName("content")]
  public List<AnthropicContent> Content { get; init; } = new List<AnthropicContent>();

  /// <summary>
  /// Creates a user message with the given text.
  /// </summary>
  /// <param name="text">The text of the message.</param>
  /// <returns>A new user message.</returns>
  public static AnthropicMessage CreateUserMessage(string text)
  {
    return new AnthropicMessage
    {
      Role = "user",
      Content = new List<AnthropicContent>
      {
        new AnthropicContent { Type = "text", Text = text }
      }
    };
  }

  /// <summary>
  /// Creates an assistant message with the given text.
  /// </summary>
  /// <param name="text">The text of the message.</param>
  /// <returns>A new assistant message.</returns>
  public static AnthropicMessage CreateAssistantMessage(string text)
  {
    return new AnthropicMessage
    {
      Role = "assistant",
      Content = new List<AnthropicContent>
      {
        new AnthropicContent { Type = "text", Text = text }
      }
    };
  }
}

/// <summary>
/// Represents a content item in an Anthropic message.
/// </summary>
public record AnthropicContent
{
  /// <summary>
  /// The type of content. Can be "text", "image", or other types supported by Anthropic.
  /// </summary>
  [JsonPropertyName("type")]
  public string Type { get; init; } = string.Empty;

  /// <summary>
  /// The text of the content if Type is "text".
  /// </summary>
  [JsonPropertyName("text")]
  public string? Text { get; init; }

  /// <summary>
  /// The source of an image if Type is "image".
  /// </summary>
  [JsonPropertyName("source")]
  public ImageSource? Source { get; init; }
  
  /// <summary>
  /// The tool usage information if Type is "tool_use".
  /// </summary>
  [JsonPropertyName("tool_use")]
  public AnthropicToolUse? ToolUse { get; init; }
  
  /// <summary>
  /// The ID of the tool use, when Type is "tool_use".
  /// </summary>
  [JsonPropertyName("id")]
  public string? Id { get; init; }
  
  /// <summary>
  /// The name of the tool, when Type is "tool_use".
  /// </summary>
  [JsonPropertyName("name")]
  public string? Name { get; init; }
  
  /// <summary>
  /// The input to the tool, when Type is "tool_use".
  /// </summary>
  [JsonPropertyName("input")]
  public JsonElement? Input { get; init; }
}

/// <summary>
/// Represents the source of an image in an Anthropic content item.
/// </summary>
public record ImageSource
{
  /// <summary>
  /// The type of image source. Can be "base64" or "url".
  /// </summary>
  [JsonPropertyName("type")]
  public string Type { get; init; } = string.Empty;

  /// <summary>
  /// The media type of the image.
  /// </summary>
  [JsonPropertyName("media_type")]
  public string? MediaType { get; init; }

  /// <summary>
  /// The data of the image if Type is "base64".
  /// </summary>
  [JsonPropertyName("data")]
  public string? Data { get; init; }

  /// <summary>
  /// The URL of the image if Type is "url".
  /// </summary>
  [JsonPropertyName("url")]
  public string? Url { get; init; }
}

/// <summary>
/// Represents tool usage in an Anthropic content item.
/// </summary>
public record AnthropicToolUse
{
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
  /// The input to the tool as a JSON string.
  /// </summary>
  [JsonPropertyName("input")]
  public string Input { get; init; } = string.Empty;
}
