using System.Text.Json;
using AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Models.JsonlEvents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using Microsoft.Extensions.Logging;

namespace AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Parsers;

/// <summary>
/// Parser for JSONL stream events from claude-agent-sdk CLI
/// Converts JSONL events to IMessage types for the LmDotnetTools framework
/// </summary>
public class JsonlStreamParser
{
    private readonly ILogger<JsonlStreamParser>? _logger;
    private readonly JsonSerializerOptions _jsonOptions;

    public JsonlStreamParser(ILogger<JsonlStreamParser>? logger = null)
    {
        _logger = logger;
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            AllowTrailingCommas = true,
        };
    }

    /// <summary>
    /// Parse a single JSONL line into a JsonlEventBase
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
    /// Convert an AssistantMessageEvent to IMessage instances
    /// Returns multiple messages: content messages + usage message
    /// </summary>
    public IEnumerable<IMessage> ConvertToMessages(AssistantMessageEvent assistantEvent)
    {
        ArgumentNullException.ThrowIfNull(assistantEvent);

        var messages = new List<IMessage>();

        // Map event properties to message properties
        var runId = assistantEvent.Uuid;
        var parentRunId = assistantEvent.ParentUuid;
        var threadId = assistantEvent.SessionId;
        var generationId = assistantEvent.Message.Id;
        var role = ParseRole(assistantEvent.Message.Role);

        // Process each content block
        foreach (var contentBlock in assistantEvent.Message.Content)
        {
            var message = ConvertContentBlock(contentBlock, role, generationId, runId, parentRunId, threadId);
            if (message != null)
            {
                messages.Add(message);
            }
        }

        // Add usage message if available
        if (assistantEvent.Message.Usage != null)
        {
            var usageMessage = ConvertUsage(
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
    /// Convert a single content block to an IMessage
    /// </summary>
    private IMessage? ConvertContentBlock(
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
                IsThinking = false
            },

            "thinking" when contentBlock.Thinking != null => new ReasoningMessage
            {
                Reasoning = contentBlock.Thinking,
                Role = role,
                GenerationId = generationId,
                RunId = runId,
                ParentRunId = parentRunId,
                ThreadId = threadId,
                Visibility = ReasoningVisibility.Plain
            },

            "tool_use" when contentBlock.Id != null && contentBlock.Name != null => new ToolsCallMessage
            {
                Role = role,
                GenerationId = generationId,
                RunId = runId,
                ParentRunId = parentRunId,
                ThreadId = threadId,
                ToolCalls = [new ToolCall
                {
                    FunctionName = contentBlock.Name,
                    FunctionArgs = contentBlock.Input?.GetRawText() ?? "{}",
                    ToolCallId = contentBlock.Id
                }]
            },

            "tool_result" when contentBlock.ToolUseId != null => new ToolsCallResultMessage
            {
                Role = Role.User,  // Tool results are from user/system
                GenerationId = generationId,
                RunId = runId,
                ThreadId = threadId,
                ToolCallResults = [new ToolCallResult(
                    ToolCallId: contentBlock.ToolUseId,
                    Result: contentBlock.Content?.GetRawText() ?? ""
                )]
            },

            _ => null
        };
    }

    /// <summary>
    /// Convert usage information to UsageMessage
    /// </summary>
    private UsageMessage ConvertUsage(
        UsageInfo usageInfo,
        Role role,
        string generationId,
        string runId,
        string? parentRunId,
        string threadId
    )
    {
        var usage = new Usage
        {
            PromptTokens = usageInfo.InputTokens,
            CompletionTokens = usageInfo.OutputTokens,
            TotalTokens = usageInfo.InputTokens + usageInfo.OutputTokens,
            InputTokenDetails = usageInfo.CacheReadInputTokens > 0 || usageInfo.CacheCreationInputTokens > 0
                ? new InputTokenDetails
                {
                    CachedTokens = usageInfo.CacheReadInputTokens ?? 0
                }
                : null
        };

        return new UsageMessage
        {
            Usage = usage,
            Role = role,
            GenerationId = generationId,
            RunId = runId,
            ThreadId = threadId
        };
    }

    /// <summary>
    /// Parse role string to Role enum
    /// </summary>
    private static Role ParseRole(string roleString)
    {
        return roleString?.ToLowerInvariant() switch
        {
            "user" => Role.User,
            "assistant" => Role.Assistant,
            "system" => Role.System,
            "tool" => Role.Tool,
            _ => Role.None
        };
    }
}
