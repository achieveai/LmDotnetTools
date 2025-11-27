using System.Text.Json;
using AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Models.JsonlEvents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using Microsoft.Extensions.Logging;
using LmModels = AchieveAi.LmDotnetTools.LmCore.Models;

namespace AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Parsers;

/// <summary>
///     Parser for JSONL stream events from claude-agent-sdk CLI
///     Converts JSONL events to IMessage types for the LmDotnetTools framework
/// </summary>
public class JsonlStreamParser
{
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly ILogger<JsonlStreamParser>? _logger;

    public JsonlStreamParser(ILogger<JsonlStreamParser>? logger = null)
    {
        _logger = logger;
        _jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, AllowTrailingCommas = true };
    }

    /// <summary>
    ///     Parse a single JSONL line into a JsonlEventBase
    /// </summary>
    public JsonlEventBase? ParseLine(string jsonLine)
    {
        if (string.IsNullOrWhiteSpace(jsonLine))
        {
            return null;
        }

        try
        {
            var eventBase = JsonSerializer.Deserialize<JsonlEventBase>(jsonLine, _jsonOptions);
            return eventBase;
        }
        catch (JsonException ex)
        {
            _logger?.LogWarning(ex, "Failed to parse JSONL line: {Line}", jsonLine);
            return null;
        }
    }

    /// <summary>
    ///     Convert an AssistantMessageEvent to IMessage instances
    ///     Returns multiple messages: content messages + usage message
    /// </summary>
    public static IEnumerable<IMessage> ConvertToMessages(AssistantMessageEvent assistantEvent)
    {
        ArgumentNullException.ThrowIfNull(assistantEvent);

        var messages = new List<IMessage>();

        // Map event properties to message properties
        var runId = assistantEvent.Uuid;
        var parentRunId = assistantEvent.ParentToolUseId;
        var threadId = assistantEvent.SessionId ?? string.Empty; // Provide default if session_id is missing
        var generationId = assistantEvent.Message.Id;
        var role = ParseRole(assistantEvent.Message.Role);

        // Process each content block
        foreach (var contentBlock in assistantEvent.Message.Content)
        {
            var message = JsonlStreamParser.ConvertContentBlock(contentBlock, role, generationId, runId, parentRunId, threadId);
            if (message != null)
            {
                messages.Add(message);
            }
        }

        // Add usage message if available
        if (assistantEvent.Message.Usage != null)
        {
            var usageMessage = JsonlStreamParser.ConvertUsage(
                assistantEvent.Message.Usage,
                role,
                generationId,
                runId,
                parentRunId,
                threadId
            );
            messages.Add(usageMessage);
        }

        return messages;
    }

    /// <summary>
    ///     Convert a UserMessageEvent to IMessage instances
    ///     Returns messages for tool results or other user content
    /// </summary>
    public IEnumerable<IMessage> ConvertToMessages(UserMessageEvent userEvent)
    {
        ArgumentNullException.ThrowIfNull(userEvent);

        var messages = new List<IMessage>();

        // Map event properties to message properties
        var runId = userEvent.Uuid;
        var threadId = userEvent.SessionId ?? string.Empty;
        var role = ParseRole(userEvent.Message.Role);

        // For user events, we don't have a message.id like assistant events
        // Use the UUID as the generation ID
        var generationId = userEvent.Uuid;

        // Parse content - it could be a string or array of content blocks
        if (userEvent.Message.Content.ValueKind == JsonValueKind.Array)
        {
            var contentBlocks = JsonSerializer.Deserialize<ContentBlock[]>(
                userEvent.Message.Content.GetRawText(),
                _jsonOptions
            );

            if (contentBlocks != null)
            {
                foreach (var contentBlock in contentBlocks)
                {
                    var message = JsonlStreamParser.ConvertContentBlock(contentBlock, role, generationId, runId, null, threadId);
                    if (message != null)
                    {
                        messages.Add(message);
                    }
                }
            }
        }
        else if (userEvent.Message.Content.ValueKind == JsonValueKind.String)
        {
            // Simple text content
            var text = userEvent.Message.Content.GetString();
            if (!string.IsNullOrEmpty(text))
            {
                messages.Add(
                    new TextMessage
                    {
                        Text = text,
                        Role = role,
                        GenerationId = generationId,
                        RunId = runId,
                        ThreadId = threadId,
                        IsThinking = false,
                    }
                );
            }
        }

        return messages;
    }

    /// <summary>
    ///     Convert a single content block to an IMessage
    /// </summary>
    private static IMessage? ConvertContentBlock(
        ContentBlock contentBlock,
        Role role,
        string generationId,
        string runId,
        string? parentRunId,
        string threadId
    )
    {
        return contentBlock.Type switch
        {
            "text" when contentBlock.Text != null => new TextMessage
            {
                Text = contentBlock.Text,
                Role = role,
                GenerationId = generationId,
                RunId = runId,
                ParentRunId = parentRunId,
                ThreadId = threadId,
                IsThinking = false,
            },

            "thinking" when contentBlock.Thinking != null => new ReasoningMessage
            {
                Reasoning = contentBlock.Thinking,
                Role = role,
                GenerationId = generationId,
                RunId = runId,
                ParentRunId = parentRunId,
                ThreadId = threadId,
                Visibility = ReasoningVisibility.Plain,
            },

            "tool_use" when contentBlock.Id != null && contentBlock.Name != null => new ToolsCallMessage
            {
                Role = role,
                GenerationId = generationId,
                RunId = runId,
                ParentRunId = parentRunId,
                ThreadId = threadId,
                ToolCalls =
                [
                    new ToolCall
                    {
                        FunctionName = contentBlock.Name,
                        FunctionArgs = contentBlock.Input?.GetRawText() ?? "{}",
                        ToolCallId = contentBlock.Id,
                    },
                ],
            },

            "tool_result" when contentBlock.ToolUseId != null => new ToolsCallResultMessage
            {
                Role = Role.User, // Tool results are from user/system
                GenerationId = generationId,
                RunId = runId,
                ThreadId = threadId,
                ToolCallResults =
                [
                    new ToolCallResult(contentBlock.ToolUseId, contentBlock.Content?.GetRawText() ?? ""),
                ],
            },

            _ => null,
        };
    }

    /// <summary>
    ///     Convert usage information to UsageMessage
    /// </summary>
    private static UsageMessage ConvertUsage(
        UsageInfo usageInfo,
        Role role,
        string generationId,
        string runId,
        string? parentRunId,
        string threadId
    )
    {
        var usage = new LmModels.Usage
        {
            PromptTokens = usageInfo.InputTokens,
            CompletionTokens = usageInfo.OutputTokens,
            TotalTokens = usageInfo.InputTokens + usageInfo.OutputTokens,
            InputTokenDetails =
                usageInfo.CacheReadInputTokens > 0 || usageInfo.CacheCreationInputTokens > 0
                    ? new LmModels.InputTokenDetails { CachedTokens = usageInfo.CacheReadInputTokens ?? 0 }
                    : null,
        };

        return new UsageMessage
        {
            Usage = usage,
            Role = role,
            GenerationId = generationId,
            RunId = runId,
            ThreadId = threadId,
        };
    }

    /// <summary>
    ///     Parse role string to Role enum
    /// </summary>
    private static Role ParseRole(string roleString)
    {
        return roleString?.ToLowerInvariant() switch
        {
            "user" => Role.User,
            "assistant" => Role.Assistant,
            "system" => Role.System,
            "tool" => Role.Tool,
            _ => Role.None,
        };
    }
}
