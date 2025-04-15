namespace AchieveAi.LmDotnetTools.AnthropicProvider.Utils;

using System;
using System.Text.Json;
using AchieveAi.LmDotnetTools.AnthropicProvider.Models;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Models;
using AchieveAi.LmDotnetTools.LmCore.Utils;

/// <summary>
/// Extension methods for the Anthropic API.
/// </summary>
public static class AnthropicExtensions
{
  /// <summary>
  /// Converts an Anthropic response to an LmCore message.
  /// </summary>
  /// <param name="response">The response to convert.</param>
  /// <param name="agentName">The name of the agent that generated the response.</param>
  /// <returns>A new message.</returns>
  public static IMessage ToMessage(this AnthropicResponse response, string agentName)
  {
    // Extract text content from the response
    string textContent = string.Empty;
    foreach (var content in response.Content)
    {
      if (content.Type == "text" && content is AnthropicResponseTextContent textContent1)
      {
        textContent += textContent1.Text;
      }
    }

    // Create a message with usage information
    var message = new TextMessage
    {
      Text = textContent,
      Role = Role.Assistant,
      FromAgent = agentName
    };
    
    // Note: In a full implementation, we would add usage information
    // to the message, but we're simplifying for now to avoid dependency issues

    return message;
  }

  /// <summary>
  /// Converts an Anthropic response to a collection of LmCore messages.
  /// </summary>
  /// <param name="response">The response to convert.</param>
  /// <param name="agentName">The name of the agent that generated the response.</param>
  /// <returns>A collection of messages.</returns>
  public static IEnumerable<IMessage> ToMessages(this AnthropicResponse response, string agentName)
  {
    var messages = new List<IMessage>();
    bool hasToolCalls = false;
    
    // Check if we have any tool_use content
    foreach (var content in response.Content)
    {
      if (content.Type == "tool_use")
      {
        hasToolCalls = true;
        break;
      }
    }
    
    // If we only have text content, combine it into a single message for compatibility with tests
    if (!hasToolCalls && response.Content.All(c => c.Type == "text"))
    {
      string combinedText = string.Join("", response.Content
        .Where(c => c.Type == "text")
        .OfType<AnthropicResponseTextContent>()
        .Select(c => c.Text));
        
      return new[] { new TextMessage
      {
        Text = combinedText,
        Role = Role.Assistant,
        FromAgent = agentName
      }};
    }
    
    // For tool calls or mixed content, convert each content item to a message
    foreach (var content in response.Content)
    {
      var message = ContentToMessage(content, agentName);
      if (message != null)
      {
        messages.Add(message);
      }
    }
    
    // If no content was processed successfully, return a default message
    if (messages.Count == 0)
    {
      messages.Add(new TextMessage
      {
        Text = string.Empty,
        Role = Role.Assistant,
        FromAgent = agentName
      });
    }
    
    return messages;
  }

  /// <summary>
  /// Converts an Anthropic content item to an appropriate message.
  /// </summary>
  /// <param name="content">The content to convert.</param>
  /// <param name="agentName">The name of the agent that generated the content.</param>
  /// <returns>A message representing the content, or null if the content type is unsupported.</returns>
  private static IMessage? ContentToMessage(AnthropicResponseContent content, string agentName)
  {
    // Handle text content
    if (content is AnthropicResponseTextContent textContent)
    {
      return new TextMessage
      {
        Text = textContent.Text,
        Role = Role.Assistant,
        FromAgent = agentName
      };
    }
    // Handle tool use content
    else if (content is AnthropicResponseToolUseContent toolUseContent)
    {
      var functionName = toolUseContent.Name;
      var arguments = toolUseContent.Input.ToString();
      
      return new ToolsCallMessage
      {
        ToolCalls = System.Collections.Immutable.ImmutableList.Create(
          new ToolCall(functionName, arguments)),
        Role = Role.Assistant,
        FromAgent = agentName
      };
    }
    
    // Additional content types can be handled here
    
    // Return null for unsupported content types
    return null;
  }

  /// <summary>
  /// Checks if the response contains a tool call.
  /// </summary>
  /// <param name="response">The response to check.</param>
  /// <returns>True if the response contains a tool call, otherwise false.</returns>
  public static bool ContainsToolCall(this AnthropicResponse response)
  {
    foreach (var content in response.Content)
    {
      if (content.Type == "tool_use")
      {
        return true;
      }
    }
    
    return false;
  }
  
  /// <summary>
  /// Creates a tool call message from an Anthropic response if present.
  /// </summary>
  /// <param name="response">The response to create from.</param>
  /// <returns>A message for a tool call if present, otherwise null.</returns>
  public static IMessage? CreateToolCallMessage(this AnthropicResponse response)
  {
    foreach (var content in response.Content)
    {
      if (content.Type == "tool_use" && content is AnthropicResponseToolUseContent toolUseContent)
      {
        try
        {
          // Extract tool info from the Content properties
          var functionName = toolUseContent.Name;
          var arguments = toolUseContent.Input.ToString();
          
          // Create a text message with tool call information
          return new TextMessage
          {
            Text = $"Tool call: {functionName} with arguments: {arguments}",
            Role = Role.Assistant
          };
        }
        catch (Exception)
        {
          // Ignore parsing errors
        }
      }
    }

    return null;
  }
}
