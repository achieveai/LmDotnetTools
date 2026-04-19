using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using AchieveAi.LmDotnetTools.CopilotSdkProvider.Configuration;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Models;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;
using Microsoft.Extensions.Logging;

namespace AchieveAi.LmDotnetTools.LmMultiTurn;

/// <summary>
/// Converts Copilot ACP event envelopes (<c>session/update</c> notifications and the
/// synthesized <c>session/prompt/completed</c> terminal envelope) into LmCore
/// message types. Mirrors the shape of <c>CodexEventTranslator</c> but targets the
/// smaller ACP surface: text chunks, thought chunks, tool calls and tool updates.
/// </summary>
internal sealed class CopilotEventTranslator
{
    private readonly CopilotSdkOptions _options;
    private readonly ILogger? _logger;
    private readonly Dictionary<string, string> _agentMessageAccumulator = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _reasoningAccumulator = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _activeMessageOrderByKey = new(StringComparer.Ordinal);
    private int _nextMessageOrderIdx;

    public CopilotEventTranslator(CopilotSdkOptions options, ILogger? logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger;
    }

    public string ThreadId { get; set; } = string.Empty;

    public string? LastExtractedCopilotSessionId { get; internal set; }

    public void ResetRunState()
    {
        _nextMessageOrderIdx = 0;
        _activeMessageOrderByKey.Clear();
        _agentMessageAccumulator.Clear();
        _reasoningAccumulator.Clear();
    }

    public List<IMessage> ConvertEventToMessages(JsonElement eventElement, string runId, string generationId)
    {
        var eventType = ExtractEventType(eventElement);

        if (string.Equals(eventType, "session/prompt/completed", StringComparison.Ordinal))
        {
            return BuildTerminalMessages(eventElement, runId, generationId);
        }

        if (!string.Equals(eventType, "session/update", StringComparison.Ordinal))
        {
            return [];
        }

        if (!eventElement.TryGetProperty("sessionId", out var sessionIdProp)
            || sessionIdProp.ValueKind != JsonValueKind.String)
        {
            // Some payloads wrap session fields inside "params".
            if (eventElement.TryGetProperty("params", out var paramsProp) && paramsProp.ValueKind == JsonValueKind.Object)
            {
                if (paramsProp.TryGetProperty("sessionId", out var nestedSessionId) && nestedSessionId.ValueKind == JsonValueKind.String)
                {
                    LastExtractedCopilotSessionId = nestedSessionId.GetString();
                }
            }
        }
        else
        {
            LastExtractedCopilotSessionId = sessionIdProp.GetString();
        }

        if (!TryGetUpdate(eventElement, out var update))
        {
            return [];
        }

        if (!update.TryGetProperty("sessionUpdate", out var kindProp) || kindProp.ValueKind != JsonValueKind.String)
        {
            return [];
        }

        var kind = kindProp.GetString() ?? string.Empty;
        switch (kind)
        {
            case "agent_message_chunk":
                return ConvertAgentMessageChunk(update, runId, generationId);
            case "agent_thought_chunk":
                return ConvertAgentThoughtChunk(update, runId, generationId);
            case "tool_call":
                return ConvertToolCall(update, runId, generationId);
            case "tool_call_update":
                return ConvertToolCallUpdate(update, runId, generationId);
            case "plan":
                // Plans are informational; surface as a summary reasoning message.
                return ConvertPlan(update, runId, generationId);
            default:
                _logger?.LogDebug(
                    "{event_type} {event_status} {provider} {provider_mode} {session_update_kind}",
                    "copilot.session_update.unmapped",
                    "ignored",
                    _options.Provider,
                    _options.ProviderMode,
                    kind);
                return [];
        }
    }

    public static string ExtractEventType(JsonElement eventElement)
    {
        return eventElement.ValueKind == JsonValueKind.Object
               && eventElement.TryGetProperty("type", out var typeProp)
               && typeProp.ValueKind == JsonValueKind.String
            ? typeProp.GetString() ?? string.Empty
            : string.Empty;
    }

    public static string BuildPrompt(IReadOnlyList<QueuedInput> inputs, ILogger? logger = null)
    {
        var sb = new StringBuilder();
        foreach (var input in inputs)
        {
            foreach (var message in input.Input.Messages)
            {
                if (message is TextMessage textMessage)
                {
                    _ = sb.AppendLine(textMessage.Text);
                }
                else
                {
                    logger?.LogWarning(
                        "{event_type} {event_status} {message_type}",
                        "copilot.prompt.unsupported_message_type",
                        "skipped",
                        message.GetType().Name);
                }
            }
        }

        return sb.ToString().Trim();
    }

    public static string CreateModelInstructionsFile(string developerInstructions)
    {
        var tempFilePath = Path.Combine(
            Path.GetTempPath(),
            $"copilot-model-instructions-{Guid.NewGuid():N}.txt");
        File.WriteAllText(tempFilePath, developerInstructions);
        return tempFilePath;
    }

    private static bool TryGetUpdate(JsonElement eventElement, out JsonElement update)
    {
        if (eventElement.TryGetProperty("update", out var direct) && direct.ValueKind == JsonValueKind.Object)
        {
            update = direct;
            return true;
        }

        if (eventElement.TryGetProperty("params", out var paramsProp)
            && paramsProp.ValueKind == JsonValueKind.Object
            && paramsProp.TryGetProperty("update", out var nested)
            && nested.ValueKind == JsonValueKind.Object)
        {
            update = nested;
            return true;
        }

        update = default;
        return false;
    }

    private List<IMessage> ConvertAgentMessageChunk(JsonElement update, string runId, string generationId)
    {
        var (text, contentType) = ExtractChunkText(update);
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        _ = contentType;
        var key = "agent_message";
        var orderIdx = GetOrCreateMessageOrderIdx($"agent:{key}");
        var existing = _agentMessageAccumulator.TryGetValue(key, out var currentSnapshot) ? currentSnapshot : string.Empty;
        _agentMessageAccumulator[key] = existing + text;

        return
        [
            new TextUpdateMessage
            {
                Role = Role.Assistant,
                ThreadId = ThreadId,
                RunId = runId,
                GenerationId = generationId,
                MessageOrderIdx = orderIdx,
                Text = text,
            },
        ];
    }

    private List<IMessage> ConvertAgentThoughtChunk(JsonElement update, string runId, string generationId)
    {
        var (text, _) = ExtractChunkText(update);
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        var key = "reasoning";
        var orderIdx = GetOrCreateMessageOrderIdx($"reasoning:{key}");
        var existing = _reasoningAccumulator.TryGetValue(key, out var currentSnapshot) ? currentSnapshot : string.Empty;
        _reasoningAccumulator[key] = existing + text;

        return
        [
            new ReasoningUpdateMessage
            {
                Role = Role.Assistant,
                ThreadId = ThreadId,
                RunId = runId,
                GenerationId = generationId,
                MessageOrderIdx = orderIdx,
                Visibility = ReasoningVisibility.Summary,
                Reasoning = text,
            },
        ];
    }

    private List<IMessage> ConvertToolCall(JsonElement update, string runId, string generationId)
    {
        var toolCallId = TryGetString(update, "toolCallId") ?? TryGetString(update, "id");
        var toolName = TryGetString(update, "title") ?? TryGetString(update, "name") ?? TryGetString(update, "kind");
        var rawInput = update.TryGetProperty("rawInput", out var rawInputProp)
            ? rawInputProp.GetRawText()
            : update.TryGetProperty("input", out var inputProp)
                ? inputProp.GetRawText()
                : "{}";
        var toolKey = $"tool:{toolCallId ?? toolName ?? "unknown"}";
        var orderIdx = GetOrCreateMessageOrderIdx(toolKey);

        return
        [
            new ToolCallMessage
            {
                Role = Role.Assistant,
                ThreadId = ThreadId,
                RunId = runId,
                GenerationId = generationId,
                MessageOrderIdx = orderIdx,
                ToolCallId = toolCallId,
                FunctionName = toolName,
                FunctionArgs = rawInput,
                ExecutionTarget = ExecutionTarget.ProviderServer,
                Metadata = ImmutableDictionary<string, object>.Empty,
            },
        ];
    }

    private List<IMessage> ConvertToolCallUpdate(JsonElement update, string runId, string generationId)
    {
        var status = TryGetString(update, "status");
        if (!string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        var toolCallId = TryGetString(update, "toolCallId") ?? TryGetString(update, "id");
        var toolName = TryGetString(update, "title") ?? TryGetString(update, "name");
        var isError = string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase);
        var toolKey = $"tool:{toolCallId ?? toolName ?? "unknown"}";
        var orderIdx = GetOrCreateMessageOrderIdx(toolKey);
        var resultText = ExtractToolResultContent(update);

        ReleaseMessageOrderIdx(toolKey);

        return
        [
            new ToolCallResultMessage
            {
                Role = Role.User,
                ThreadId = ThreadId,
                RunId = runId,
                GenerationId = generationId,
                ToolCallId = toolCallId,
                ToolName = toolName,
                IsError = isError,
                ErrorCode = isError ? "copilot_tool_failed" : null,
                Result = resultText,
                MessageOrderIdx = orderIdx,
                ExecutionTarget = ExecutionTarget.ProviderServer,
                Metadata = ImmutableDictionary<string, object>.Empty,
            },
        ];
    }

    private List<IMessage> ConvertPlan(JsonElement update, string runId, string generationId)
    {
        if (!update.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var sb = new StringBuilder();
        foreach (var entry in entries.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var content = TryGetString(entry, "content") ?? TryGetString(entry, "text");
            if (string.IsNullOrWhiteSpace(content))
            {
                continue;
            }

            _ = sb.Append("- ").AppendLine(content);
        }

        var planText = sb.ToString().Trim();
        return string.IsNullOrWhiteSpace(planText)
            ? []
            :
            [
                new ReasoningMessage
                {
                    Role = Role.Assistant,
                    ThreadId = ThreadId,
                    RunId = runId,
                    GenerationId = generationId,
                    MessageOrderIdx = NextMessageOrderIdx(),
                    Visibility = ReasoningVisibility.Summary,
                    Reasoning = planText,
                },
            ];
    }

    private List<IMessage> BuildTerminalMessages(JsonElement eventElement, string runId, string generationId)
    {
        var messages = new List<IMessage>();

        // Emit the final consolidated agent message if we accumulated deltas.
        if (_agentMessageAccumulator.TryGetValue("agent_message", out var accumulatedAgentText)
            && !string.IsNullOrEmpty(accumulatedAgentText))
        {
            var orderIdx = GetOrCreateMessageOrderIdx("agent:agent_message");
            messages.Add(new TextMessage
            {
                Role = Role.Assistant,
                ThreadId = ThreadId,
                RunId = runId,
                GenerationId = generationId,
                MessageOrderIdx = orderIdx,
                Text = accumulatedAgentText,
            });
            ReleaseMessageOrderIdx("agent:agent_message");
            _ = _agentMessageAccumulator.Remove("agent_message");
        }

        // Emit a final reasoning message if we accumulated thought chunks.
        if (_reasoningAccumulator.TryGetValue("reasoning", out var accumulatedReasoning)
            && !string.IsNullOrEmpty(accumulatedReasoning))
        {
            var orderIdx = GetOrCreateMessageOrderIdx("reasoning:reasoning");
            messages.Add(new ReasoningMessage
            {
                Role = Role.Assistant,
                ThreadId = ThreadId,
                RunId = runId,
                GenerationId = generationId,
                MessageOrderIdx = orderIdx,
                Visibility = ReasoningVisibility.Summary,
                Reasoning = accumulatedReasoning,
            });
            ReleaseMessageOrderIdx("reasoning:reasoning");
            _ = _reasoningAccumulator.Remove("reasoning");
        }

        messages.Add(CreateUsageMessage(eventElement, runId, generationId, NextMessageOrderIdx()));
        return messages;
    }

    private UsageMessage CreateUsageMessage(JsonElement eventElement, string runId, string generationId, int messageOrderIdx)
    {
        var usage = new Usage();
        if (eventElement.TryGetProperty("usage", out var usageElement) && usageElement.ValueKind == JsonValueKind.Object)
        {
            var inputTokens = GetInt32(usageElement, "inputTokens", "input_tokens", "prompt_tokens");
            var outputTokens = GetInt32(usageElement, "outputTokens", "output_tokens", "completion_tokens");
            var cachedTokens = GetInt32(usageElement, "cachedInputTokens", "cached_input_tokens", "prompt_cache_hit_tokens");
            usage = new Usage
            {
                PromptTokens = inputTokens,
                CompletionTokens = outputTokens,
                TotalTokens = inputTokens + outputTokens,
                InputTokenDetails = new InputTokenDetails { CachedTokens = cachedTokens },
            };
        }

        return new UsageMessage
        {
            Usage = usage,
            Role = Role.Assistant,
            ThreadId = ThreadId,
            RunId = runId,
            GenerationId = generationId,
            MessageOrderIdx = messageOrderIdx,
        };
    }

    private static (string Text, string? ContentType) ExtractChunkText(JsonElement update)
    {
        if (!update.TryGetProperty("content", out var content))
        {
            return (string.Empty, null);
        }

        if (content.ValueKind == JsonValueKind.String)
        {
            return (content.GetString() ?? string.Empty, "text");
        }

        if (content.ValueKind == JsonValueKind.Object)
        {
            var type = TryGetString(content, "type");
            var text = TryGetString(content, "text") ?? string.Empty;
            return (text, type);
        }

        return (string.Empty, null);
    }

    private static string ExtractToolResultContent(JsonElement update)
    {
        if (update.TryGetProperty("content", out var content))
        {
            if (content.ValueKind == JsonValueKind.String)
            {
                return content.GetString() ?? string.Empty;
            }

            if (content.ValueKind == JsonValueKind.Array)
            {
                var sb = new StringBuilder();
                foreach (var part in content.EnumerateArray())
                {
                    if (part.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    var text = TryGetString(part, "text");
                    if (!string.IsNullOrEmpty(text))
                    {
                        _ = sb.AppendLine(text);
                    }
                }

                var combined = sb.ToString().Trim();
                if (!string.IsNullOrEmpty(combined))
                {
                    return combined;
                }

                return content.GetRawText();
            }

            return content.GetRawText();
        }

        return update.TryGetProperty("rawOutput", out var rawOutput)
            ? rawOutput.GetRawText()
            : "{}";
    }

    private static string? TryGetString(JsonElement obj, string propertyName)
    {
        return obj.ValueKind == JsonValueKind.Object
               && obj.TryGetProperty(propertyName, out var value)
               && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static int GetInt32(JsonElement obj, params string[] propertyNames)
    {
        foreach (var name in propertyNames)
        {
            if (obj.TryGetProperty(name, out var value)
                && value.ValueKind == JsonValueKind.Number
                && value.TryGetInt32(out var result))
            {
                return result;
            }
        }

        return 0;
    }

    private int GetOrCreateMessageOrderIdx(string key)
    {
        if (_activeMessageOrderByKey.TryGetValue(key, out var existing))
        {
            return existing;
        }

        var next = NextMessageOrderIdx();
        _activeMessageOrderByKey[key] = next;
        return next;
    }

    private void ReleaseMessageOrderIdx(string key)
    {
        _ = _activeMessageOrderByKey.Remove(key);
    }

    private int NextMessageOrderIdx()
    {
        return _nextMessageOrderIdx++;
    }
}
