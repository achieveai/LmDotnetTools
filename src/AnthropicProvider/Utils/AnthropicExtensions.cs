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
    // First get messages using AnthropicStreamParser for consistent conversion
    var messages = response.ToMessages().ToList();
    
    // Then set the agent name for all messages
    for (int i = 0; i < messages.Count; i++)
    {
      var message = messages[i];
      
      if (message is TextMessage textMessage)
      {
        messages[i] = textMessage with { FromAgent = agentName };
      }
      else if (message is ToolsCallMessage toolsCallMessage)
      {
        messages[i] = toolsCallMessage with { FromAgent = agentName };
      }
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
