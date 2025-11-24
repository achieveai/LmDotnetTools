using AchieveAi.LmDotnetTools.AnthropicProvider.Models;
using AchieveAi.LmDotnetTools.LmCore.Messages;

namespace AchieveAi.LmDotnetTools.AnthropicProvider.Utils;

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
        ArgumentNullException.ThrowIfNull(response);
        // Extract text content from the response
        var textContent = string.Empty;
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
            FromAgent = agentName,
        };

        // Note: In a full implementation, we would add usage information
        // to the message, but we're simplifying for now to avoid dependency issues

        return message;
    }

    /// <summary>
    /// Converts an Anthropic response to a collection of LmCore messages.
    /// </summary>
    /// <param name="response">The response to convert.</param>
    /// <returns>A collection of messages.</returns>
    [Obsolete("Use Models.AnthropicExtensions.ToMessages instead")]
    public static IEnumerable<IMessage> ToMessagesLegacy(this AnthropicResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        // First get messages using AnthropicStreamParser for consistent conversion
        var parser = new Models.AnthropicStreamParser();
        var messages = new List<IMessage>();

        // Process each content item
        foreach (var content in response.Content)
        {
            var message = ContentToMessage(content, response.Id);
            if (message != null)
            {
                messages.Add(message);
            }
        }

        return messages;
    }

    /// <summary>
    /// Converts an Anthropic content item to an appropriate message.
    /// </summary>
    /// <param name="content">The content to convert.</param>
    /// <param name="responseId">The ID of the response.</param>
    /// <returns>A message representing the content, or null if the content type is unsupported.</returns>
    private static IMessage? ContentToMessage(AnthropicResponseContent content, string responseId)
    {
        // Handle text content
        if (content is Models.AnthropicResponseTextContent textContent)
        {
            return new TextMessage
            {
                Text = textContent.Text,
                Role = Role.Assistant,
                GenerationId = responseId,
            };
        }
        // Handle tool use content
        else if (content is Models.AnthropicResponseToolUseContent toolUseContent)
        {
            var functionName = toolUseContent.Name;
            var arguments = toolUseContent.Input.ToString();

            return new ToolsCallMessage
            {
                ToolCalls = [new ToolCall { FunctionName = functionName, FunctionArgs = arguments, ToolCallId = toolUseContent.Id }],
                Role = Role.Assistant,
                GenerationId = responseId,
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
        ArgumentNullException.ThrowIfNull(response);
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
        ArgumentNullException.ThrowIfNull(response);
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
                        Role = Role.Assistant,
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
