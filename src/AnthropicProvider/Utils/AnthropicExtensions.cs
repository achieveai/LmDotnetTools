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
      if (content.Type == "text" && content.Text != null)
      {
        textContent += content.Text;
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
      if (content.Type == "tool_use")
      {
        try
        {
          // This is a simplified implementation - actual tool call extraction
          // would depend on the specific structure of the Anthropic API response
          var functionName = "example_function_name";
          var arguments = "{}";
          
          // Create a text message with tool call information for now
          // In a real implementation, this would be properly structured 
          // according to the message interface in LmCore
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
