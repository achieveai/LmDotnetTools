using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;

namespace AchieveAi.LmDotnetTools.AnthropicProvider.Models;

/// <summary>
/// Extension methods for converting Anthropic API responses to IMessage format.
/// </summary>
public static class AnthropicExtensions
{
    /// <summary>
    /// Converts an Anthropic API response to LmCore messages.
    /// </summary>
    /// <param name="response">The Anthropic API response.</param>
    /// <param name="agentName">The name of the agent.</param>
    /// <returns>A list of IMessage objects representing the response content.</returns>
    public static List<IMessage> ToMessages(this AnthropicResponse response, string agentName)
    {
        var messages = new List<IMessage>();

        // Process content blocks
        foreach (var content in response.Content)
        {
            var message = ContentToMessage(content, response.Id, agentName);
            if (message != null)
            {
                messages.Add(message);
            }
        }

        // Add usage information as a separate message if available
        if (response.Usage != null)
        {
            messages.Add(new UsageMessage
            {
                Usage = new Usage
                {
                    PromptTokens = response.Usage.InputTokens,
                    CompletionTokens = response.Usage.OutputTokens,
                    TotalTokens = response.Usage.InputTokens + response.Usage.OutputTokens
                },
                Role = Role.Assistant,
                FromAgent = agentName,
                GenerationId = response.Id
            });
        }
        
        return messages;
    }

    /// <summary>
    /// Converts an Anthropic streaming event to an update message.
    /// </summary>
    /// <param name="streamEvent">The streaming event from Anthropic API.</param>
    /// <returns>An IMessage representing the streaming update.</returns>
    public static IMessage ToUpdateMessage(this AnthropicStreamEvent streamEvent)
    {
        return streamEvent switch
        {
            // Handle message delta event with usage information
            AnthropicMessageDeltaEvent messageDeltaEvent when messageDeltaEvent.Delta?.StopReason != null && messageDeltaEvent.Usage != null =>
                new TextUpdateMessage
                {
                    Text = string.Empty,
                    Role = Role.Assistant,
                    Metadata = ImmutableDictionary<string, object>.Empty
                        .Add("usage", new
                        {
                            InputTokens = messageDeltaEvent.Usage.InputTokens,
                            OutputTokens = messageDeltaEvent.Usage.OutputTokens,
                            TotalTokens = messageDeltaEvent.Usage.InputTokens + messageDeltaEvent.Usage.OutputTokens
                        })
                },
            
            // Handle content block delta event for text content
            AnthropicContentBlockDeltaEvent contentBlockDeltaEvent when contentBlockDeltaEvent.Delta is AnthropicTextDelta textDelta =>
                new TextUpdateMessage
                {
                    Text = textDelta.Text,
                    Role = Role.Assistant,
                    IsThinking = false
                },
            
            // Handle content block delta event for thinking content
            AnthropicContentBlockDeltaEvent contentBlockDeltaEvent when contentBlockDeltaEvent.Delta is AnthropicThinkingDelta thinkingDelta =>
                new TextUpdateMessage
                {
                    Text = thinkingDelta.Thinking,
                    Role = Role.Assistant,
                    IsThinking = true
                },
                
            // Handle content block delta event for tool calls
            AnthropicContentBlockDeltaEvent contentBlockDeltaEvent when contentBlockDeltaEvent.Delta is AnthropicToolCallsDelta toolCallsDelta 
                && toolCallsDelta.ToolCalls.Count > 0 =>
                new ToolsCallUpdateMessage
                {
                    Role = Role.Assistant,
                    ToolCallUpdates = ImmutableList.Create(new ToolCallUpdate 
                    {
                        ToolCallId = toolCallsDelta.ToolCalls[0].Id,
                        FunctionName = toolCallsDelta.ToolCalls[0].Name,
                        FunctionArgs = toolCallsDelta.ToolCalls[0].Input.ToString(),
                        Index = toolCallsDelta.ToolCalls[0].Index
                    })
                },
            
            // Default empty update message for unhandled event types
            _ => new TextUpdateMessage
            {
                Text = string.Empty,
                Role = Role.Assistant
            }
        };
    }
    
    /// <summary>
    /// Maps an Anthropic role string to LmCore Role enum.
    /// </summary>
    /// <param name="role">The Anthropic role string.</param>
    /// <returns>The corresponding LmCore Role.</returns>
    public static Role ParseRole(string role)
    {
        return role.ToLower() switch
        {
            "assistant" => Role.Assistant,
            "user" => Role.User,
            "system" => Role.System,
            "tool" => Role.Tool,
            _ => Role.None
        };
    }

    /// <summary>
    /// Converts an AnthropicResponseContent to an appropriate IMessage.
    /// </summary>
    /// <param name="content">The content to convert.</param>
    /// <param name="messageId">The message ID.</param>
    /// <param name="agentName">The agent name.</param>
    /// <returns>The converted message, or null if the content couldn't be converted.</returns>
    private static IMessage? ContentToMessage(AnthropicResponseContent content, string messageId, string agentName)
    {
        return content switch
        {
            AnthropicResponseTextContent textContent => new TextMessage
            {
                Text = textContent.Text,
                Role = ParseRole("assistant"),
                FromAgent = agentName,
                GenerationId = messageId
            },
            
            AnthropicResponseToolUseContent toolContent => new ToolsCallMessage
            {
                Role = ParseRole("assistant"),
                FromAgent = agentName,
                GenerationId = messageId,
                ToolCalls = ImmutableList.Create(new ToolCall(
                    toolContent.Name,
                    toolContent.Input.ToString()
                ) { ToolCallId = toolContent.Id })
            },
            
            AnthropicResponseThinkingContent thinkingContent => new TextMessage
            {
                Text = thinkingContent.Thinking,
                Role = ParseRole("assistant"),
                FromAgent = agentName,
                GenerationId = messageId,
                IsThinking = true
            },
            
            _ => null
        };
    }
} 