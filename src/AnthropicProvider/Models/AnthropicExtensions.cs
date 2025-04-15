using System.Collections.Immutable;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Messages;

namespace AchieveAi.LmDotnetTools.AnthropicProvider.Models;

/// <summary>
/// Extension methods for converting Anthropic API responses to IMessage format.
/// </summary>
public static class AnthropicExtensions
{
    /// <summary>
    /// Converts an Anthropic response to a list of IMessage objects.
    /// </summary>
    /// <param name="response">The Anthropic API response.</param>
    /// <returns>A list of IMessage objects representing the response content.</returns>
    public static List<IMessage> ToMessages(this AnthropicResponse response)
    {
        var messages = new List<IMessage>();
        
        foreach (var content in response.Content)
        {
            switch (content)
            {
                case AnthropicResponseTextContent textContent:
                    messages.Add(new TextMessage
                    {
                        Text = textContent.Text,
                        Role = ParseRole(response.Role),
                        FromAgent = response.Id,
                        GenerationId = response.Id,
                        IsThinking = false
                    });
                    break;
                    
                case AnthropicResponseThinkingContent thinkingContent:
                    messages.Add(new TextMessage
                    {
                        Text = thinkingContent.Thinking,
                        Role = ParseRole(response.Role),
                        FromAgent = response.Id,
                        GenerationId = response.Id,
                        IsThinking = true
                    });
                    break;
                    
                case AnthropicResponseToolUseContent toolContent:
                    messages.Add(new ToolsCallMessage
                    {
                        Role = ParseRole(response.Role),
                        FromAgent = response.Id,
                        GenerationId = response.Id,
                        ToolCalls = ImmutableList.Create(new ToolCall(
                            toolContent.Name,
                            toolContent.Input.ToString()
                        ) { ToolCallId = toolContent.Id })
                    });
                    break;
            }
        }
        
        // Set usage on the last message if messages exist
        if (messages.Count > 0 && response.Usage != null)
        {
            var lastMessage = messages[messages.Count - 1];
            if (lastMessage is TextMessage textMessage)
            {
                // Add usage data to metadata
                var metadata = ImmutableDictionary<string, object>.Empty
                    .Add("usage", new
                    {
                        InputTokens = response.Usage.InputTokens,
                        OutputTokens = response.Usage.OutputTokens,
                        TotalTokens = response.Usage.InputTokens + response.Usage.OutputTokens
                    });
                
                // Replace the message with updated metadata
                messages[messages.Count - 1] = textMessage with { Metadata = metadata };
            }
            else if (lastMessage is ToolsCallMessage toolsCallMessage)
            {
                // Add usage data to metadata
                var metadata = ImmutableDictionary<string, object>.Empty
                    .Add("usage", new
                    {
                        InputTokens = response.Usage.InputTokens,
                        OutputTokens = response.Usage.OutputTokens,
                        TotalTokens = response.Usage.InputTokens + response.Usage.OutputTokens
                    });
                
                // Replace the message with updated metadata
                messages[messages.Count - 1] = toolsCallMessage with { Metadata = metadata };
            }
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
        // Handle message_delta event with usage information
        if (streamEvent.Type == "message_delta" && streamEvent.Delta != null)
        {
            if (streamEvent.Delta.StopReason != null && streamEvent.Usage != null)
            {
                // This is the final message with usage statistics
                return new TextUpdateMessage
                {
                    Text = string.Empty,
                    Role = Role.Assistant,
                    Metadata = ImmutableDictionary<string, object>.Empty
                        .Add("usage", new
                        {
                            InputTokens = streamEvent.Usage.InputTokens,
                            OutputTokens = streamEvent.Usage.OutputTokens,
                            TotalTokens = streamEvent.Usage.InputTokens + streamEvent.Usage.OutputTokens
                        })
                };
            }
        }
        
        // Handle content_block_delta event for text content
        if (streamEvent.Type == "content_block_delta" && 
            streamEvent.Delta?.Type == "text_delta" && 
            !string.IsNullOrEmpty(streamEvent.Delta.Text))
        {
            return new TextUpdateMessage
            {
                Text = streamEvent.Delta.Text ?? string.Empty,
                Role = Role.Assistant,
                IsThinking = false
            };
        }
        
        // Handle content_block_delta event for thinking content
        if (streamEvent.Type == "content_block_delta" && 
            streamEvent.Delta?.PartialJson != null &&
            streamEvent.Delta.Type == "thinking_delta")
        {
            // Extract thinking text if possible
            var thinkingText = string.Empty;
            try
            {
                var jsonDoc = JsonDocument.Parse(streamEvent.Delta.PartialJson);
                if (jsonDoc.RootElement.TryGetProperty("thinking", out var thinking))
                {
                    thinkingText = thinking.GetString() ?? string.Empty;
                }
            }
            catch
            {
                // In case of parsing issues, use empty string
            }
            
            return new TextUpdateMessage
            {
                Text = thinkingText,
                Role = Role.Assistant,
                IsThinking = true
            };
        }
        
        // Handle tool calls in streaming content
        if (streamEvent.Type == "content_block_delta" && 
            streamEvent.Delta?.ToolCalls != null &&
            streamEvent.Delta.ToolCalls.Count > 0)
        {
            var toolCall = streamEvent.Delta.ToolCalls[0];
            return new ToolsCallUpdateMessage
            {
                Role = Role.Assistant,
                ToolCallUpdates = ImmutableList.Create(new ToolCallUpdate 
                {
                    ToolCallId = toolCall.Id,
                    FunctionName = toolCall.Name,
                    FunctionArgs = toolCall.Input.ToString(),
                    Index = toolCall.Index
                })
            };
        }
        
        // Default empty update message for unhandled event types
        return new TextUpdateMessage
        {
            Text = string.Empty,
            Role = Role.Assistant
        };
    }
    
    /// <summary>
    /// Parses an Anthropic role string to the appropriate Role enum value.
    /// </summary>
    /// <param name="role">The role string from Anthropic API.</param>
    /// <returns>The corresponding Role enum value.</returns>
    private static Role ParseRole(string role)
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
} 