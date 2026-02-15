using System.Collections.Immutable;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Models;
namespace AchieveAi.LmDotnetTools.AnthropicProvider.Models;

/// <summary>
///     Extension methods for converting Anthropic API responses to IMessage format.
/// </summary>
public static class AnthropicExtensions
{
    /// <summary>
    ///     Converts an Anthropic API response to LmCore messages.
    /// </summary>
    /// <param name="response">The Anthropic API response.</param>
    /// <param name="agentName">The name of the agent.</param>
    /// <returns>A list of IMessage objects representing the response content.</returns>
    public static List<IMessage> ToMessages(this AnthropicResponse response, string agentName)
    {
        ArgumentNullException.ThrowIfNull(response);
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
            messages.Add(
                new UsageMessage
                {
                    Usage = new Usage
                    {
                        PromptTokens = response.Usage.InputTokens,
                        CompletionTokens = response.Usage.OutputTokens,
                        TotalTokens = response.Usage.InputTokens + response.Usage.OutputTokens,
                    },
                    Role = Role.Assistant,
                    FromAgent = agentName,
                    GenerationId = response.Id,
                }
            );
        }

        return messages;
    }

    /// <summary>
    ///     Converts an Anthropic streaming event to an update message.
    /// </summary>
    /// <param name="streamEvent">The streaming event from Anthropic API.</param>
    /// <returns>An IMessage representing the streaming update.</returns>
    public static IMessage ToUpdateMessage(this AnthropicStreamEvent streamEvent)
    {
        return streamEvent switch
        {
            // Handle message delta event with usage information
            AnthropicMessageDeltaEvent messageDeltaEvent
                when messageDeltaEvent.Delta?.StopReason != null && messageDeltaEvent.Usage != null =>
                new TextUpdateMessage
                {
                    Text = string.Empty,
                    Role = Role.Assistant,
                    Metadata = ImmutableDictionary<string, object>.Empty.Add(
                        "usage",
                        new
                        {
                            messageDeltaEvent.Usage.InputTokens,
                            messageDeltaEvent.Usage.OutputTokens,
                            TotalTokens = messageDeltaEvent.Usage.InputTokens + messageDeltaEvent.Usage.OutputTokens,
                        }
                    ),
                },

            // Handle content block delta event for text content
            AnthropicContentBlockDeltaEvent contentBlockDeltaEvent
                when contentBlockDeltaEvent.Delta is AnthropicTextDelta textDelta => new TextUpdateMessage
                {
                    Text = textDelta.Text,
                    Role = Role.Assistant,
                    IsThinking = false,
                },

            // Handle content block delta event for thinking content
            AnthropicContentBlockDeltaEvent contentBlockDeltaEvent
                when contentBlockDeltaEvent.Delta is AnthropicThinkingDelta thinkingDelta => new TextUpdateMessage
                {
                    Text = thinkingDelta.Thinking,
                    Role = Role.Assistant,
                    IsThinking = true,
                },

            // Handle content block delta event for tool calls
            AnthropicContentBlockDeltaEvent contentBlockDeltaEvent
                when contentBlockDeltaEvent.Delta is AnthropicToolCallsDelta toolCallsDelta
                    && toolCallsDelta.ToolCalls.Count > 0 => new ToolsCallUpdateMessage
                    {
                        Role = Role.Assistant,
                        ToolCallUpdates =
                [
                    new ToolCallUpdate
                    {
                        ToolCallId = toolCallsDelta.ToolCalls[0].Id,
                        FunctionName = toolCallsDelta.ToolCalls[0].Name,
                        FunctionArgs = toolCallsDelta.ToolCalls[0].Input.ToString(),
                        Index = toolCallsDelta.ToolCalls[0].Index,
                        ExecutionTarget = ExecutionTarget.LocalFunction,
                    },
                ],
                    },

            // Default empty update message for unhandled event types
            _ => new TextUpdateMessage { Text = string.Empty, Role = Role.Assistant },
        };
    }

    /// <summary>
    ///     Maps an Anthropic role string to LmCore Role enum.
    /// </summary>
    /// <param name="role">The Anthropic role string.</param>
    /// <returns>The corresponding LmCore Role.</returns>
    public static Role ParseRole(string role)
    {
        ArgumentNullException.ThrowIfNull(role);
        return role.ToLower() switch
        {
            "assistant" => Role.Assistant,
            "user" => Role.User,
            "system" => Role.System,
            "tool" => Role.Tool,
            _ => Role.None,
        };
    }

    /// <summary>
    ///     Converts an AnthropicResponseContent to an appropriate IMessage.
    /// </summary>
    /// <param name="content">The content to convert.</param>
    /// <param name="messageId">The message ID.</param>
    /// <param name="agentName">The agent name.</param>
    /// <returns>The converted message, or null if the content couldn't be converted.</returns>
    private static IMessage? ContentToMessage(AnthropicResponseContent content, string messageId, string agentName)
    {
        return content switch
        {
            // Text with citations must be checked before plain text (Citations property on base class)
            AnthropicResponseTextContent { Citations.Count: > 0 } textWithCitations =>
                new TextWithCitationsMessage
                {
                    Text = textWithCitations.Text,
                    Citations = [.. textWithCitations.Citations!
                        .Select(c => new CitationInfo
                        {
                            Type = c.Type,
                            Url = c.Url,
                            Title = c.Title,
                            CitedText = c.CitedText,
                            StartIndex = c.StartCharIndex,
                            EndIndex = c.EndCharIndex,
                        })],
                    Role = ParseRole("assistant"),
                    FromAgent = agentName,
                    GenerationId = messageId,
                },

            AnthropicResponseTextContent textContent => new TextMessage
            {
                Text = textContent.Text,
                Role = ParseRole("assistant"),
                FromAgent = agentName,
                GenerationId = messageId,
            },

            AnthropicResponseToolUseContent toolContent => new ToolsCallMessage
            {
                Role = ParseRole("assistant"),
                FromAgent = agentName,
                GenerationId = messageId,
                ToolCalls =
                [
                    new ToolCall
                    {
                        FunctionName = toolContent.Name,
                        FunctionArgs = toolContent.Input.ToString(),
                        ToolCallId = toolContent.Id,
                        ExecutionTarget = ExecutionTarget.LocalFunction,
                    },
                ],
            },

            AnthropicResponseThinkingContent thinkingContent => new TextMessage
            {
                Text = thinkingContent.Thinking,
                Role = ParseRole("assistant"),
                FromAgent = agentName,
                GenerationId = messageId,
                IsThinking = true,
            },

            AnthropicResponseServerToolUseContent serverToolUse => new ToolCallMessage
            {
                ToolCallId = serverToolUse.Id,
                FunctionName = serverToolUse.Name,
                FunctionArgs = serverToolUse.Input.ValueKind != JsonValueKind.Undefined
                    ? serverToolUse.Input.ToString()
                    : "{}",
                ExecutionTarget = ExecutionTarget.ProviderServer,
                Role = ParseRole("assistant"),
                FromAgent = agentName,
                GenerationId = messageId,
            },

            AnthropicWebSearchToolResultContent webSearchResult => new ToolCallResultMessage
            {
                ToolCallId = webSearchResult.ToolUseId,
                ToolName = "web_search",
                Result = webSearchResult.Content.ValueKind != JsonValueKind.Undefined
                    ? webSearchResult.Content.GetRawText()
                    : "{}",
                IsError = IsContentError(webSearchResult.Content),
                ErrorCode = GetContentErrorCode(webSearchResult.Content),
                ExecutionTarget = ExecutionTarget.ProviderServer,
                Role = ParseRole("assistant"),
                FromAgent = agentName,
                GenerationId = messageId,
            },

            AnthropicWebFetchToolResultContent webFetchResult => new ToolCallResultMessage
            {
                ToolCallId = webFetchResult.ToolUseId,
                ToolName = "web_fetch",
                Result = webFetchResult.Content.ValueKind != JsonValueKind.Undefined
                    ? webFetchResult.Content.GetRawText()
                    : "{}",
                IsError = IsContentError(webFetchResult.Content),
                ErrorCode = GetContentErrorCode(webFetchResult.Content),
                ExecutionTarget = ExecutionTarget.ProviderServer,
                Role = ParseRole("assistant"),
                FromAgent = agentName,
                GenerationId = messageId,
            },

            AnthropicBashCodeExecutionToolResultContent bashResult => new ToolCallResultMessage
            {
                ToolCallId = bashResult.ToolUseId,
                ToolName = "bash_code_execution",
                Result = bashResult.Content.ValueKind != JsonValueKind.Undefined
                    ? bashResult.Content.GetRawText()
                    : "{}",
                IsError = IsContentError(bashResult.Content),
                ErrorCode = GetContentErrorCode(bashResult.Content),
                ExecutionTarget = ExecutionTarget.ProviderServer,
                Role = ParseRole("assistant"),
                FromAgent = agentName,
                GenerationId = messageId,
            },

            AnthropicTextEditorCodeExecutionToolResultContent textEditorResult => new ToolCallResultMessage
            {
                ToolCallId = textEditorResult.ToolUseId,
                ToolName = "text_editor_code_execution",
                Result = textEditorResult.Content.ValueKind != JsonValueKind.Undefined
                    ? textEditorResult.Content.GetRawText()
                    : "{}",
                IsError = IsContentError(textEditorResult.Content),
                ErrorCode = GetContentErrorCode(textEditorResult.Content),
                ExecutionTarget = ExecutionTarget.ProviderServer,
                Role = ParseRole("assistant"),
                FromAgent = agentName,
                GenerationId = messageId,
            },

            _ => null,
        };
    }

    private static bool IsContentError(JsonElement content)
    {
        return content.ValueKind == JsonValueKind.Object && content.TryGetProperty("type", out var typeElement)
            && typeElement.GetString()?.EndsWith("_error") == true;
    }

    private static string? GetContentErrorCode(JsonElement content)
    {
        return content.ValueKind == JsonValueKind.Object && content.TryGetProperty("error_code", out var errorElement)
            ? errorElement.GetString()
            : null;
    }
}
