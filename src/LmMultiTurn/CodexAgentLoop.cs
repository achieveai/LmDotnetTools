using System.Collections.Immutable;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AchieveAi.LmDotnetTools.CodexSdkProvider.Agents;
using AchieveAi.LmDotnetTools.CodexSdkProvider.Configuration;
using AchieveAi.LmDotnetTools.CodexSdkProvider.Models;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Models;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using Microsoft.Extensions.Logging;

namespace AchieveAi.LmDotnetTools.LmMultiTurn;

public sealed class CodexAgentLoop : MultiTurnAgentBase
{
    private const string CodexThreadIdProperty = "codex_thread_id";

    private readonly CodexSdkOptions _options;
    private readonly IReadOnlyDictionary<string, CodexMcpServerConfig> _mcpServers;
    private readonly ILoggerFactory? _loggerFactory;
    private readonly Func<CodexSdkOptions, ILogger?, ICodexSdkClient>? _clientFactory;
    private readonly Dictionary<string, string> _agentMessageAccumulator = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _reasoningAccumulator = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _activeMessageOrderByKey = new(StringComparer.Ordinal);
    private ICodexSdkClient? _client;
    private string? _codexThreadId;
    private bool _systemPromptApplied;
    private int _nextMessageOrderIdx;

    public CodexAgentLoop(
        CodexSdkOptions options,
        IReadOnlyDictionary<string, CodexMcpServerConfig>? mcpServers,
        string threadId,
        string? systemPrompt = null,
        GenerateReplyOptions? defaultOptions = null,
        int inputChannelCapacity = 100,
        int outputChannelCapacity = 1000,
        IConversationStore? store = null,
        ILogger<CodexAgentLoop>? logger = null,
        ILoggerFactory? loggerFactory = null,
        Func<CodexSdkOptions, ILogger?, ICodexSdkClient>? clientFactory = null)
        : base(
            threadId,
            systemPrompt,
            defaultOptions,
            maxTurnsPerRun: 1,
            inputChannelCapacity,
            outputChannelCapacity,
            store,
            logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _mcpServers = mcpServers ?? new Dictionary<string, CodexMcpServerConfig>();
        _loggerFactory = loggerFactory;
        _clientFactory = clientFactory;
    }

    protected override async Task OnBeforeRunAsync()
    {
        if (_client is { IsRunning: true })
        {
            return;
        }

        _client = _clientFactory != null
            ? _clientFactory(_options, _loggerFactory?.CreateLogger<CodexSdkClient>())
            : new CodexSdkClient(_options, _loggerFactory?.CreateLogger<CodexSdkClient>());

        await _client.EnsureStartedAsync(
            new CodexBridgeInitOptions
            {
                Model = _options.Model,
                ApprovalPolicy = _options.ApprovalPolicy,
                SandboxMode = _options.SandboxMode,
                SkipGitRepoCheck = _options.SkipGitRepoCheck,
                NetworkAccessEnabled = _options.NetworkAccessEnabled,
                WebSearchMode = _options.WebSearchMode,
                WorkingDirectory = _options.WorkingDirectory,
                McpServers = _mcpServers,
                BaseUrl = _options.BaseUrl,
                ApiKey = _options.ApiKey,
                ThreadId = _codexThreadId,
            });

        Logger.LogInformation(
            "{event_type} {event_status} {provider} {provider_mode} {thread_id} {codex_thread_id}",
            "codex.persistence.resume",
            "completed",
            _options.Provider,
            _options.ProviderMode,
            ThreadId,
            _codexThreadId);
    }

    protected override async Task OnDisposeAsync()
    {
        if (_client != null)
        {
            await _client.DisposeAsync();
            _client = null;
        }
    }

    protected override async Task RunLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (!await InputReader.WaitToReadAsync(ct))
            {
                break;
            }

            _ = TryDrainInputs(out var batch);
            if (batch.Count == 0)
            {
                continue;
            }

            var assignment = StartRun(batch);
            await PublishToAllAsync(new RunAssignmentMessage
            {
                Assignment = assignment,
                ThreadId = ThreadId,
            }, ct);

            Logger.LogInformation(
                "{event_type} {event_status} {provider} {provider_mode} {thread_id} {run_id} {generation_id} {input_id}",
                "codex.turn.assignment",
                "started",
                _options.Provider,
                _options.ProviderMode,
                ThreadId,
                assignment.RunId,
                assignment.GenerationId,
                string.Join(",", assignment.InputIds ?? []));

            foreach (var input in batch)
            {
                foreach (var msg in input.Input.Messages)
                {
                    AddToHistory(msg);
                }
            }

            var streamMetrics = new RunStreamMetrics();
            try
            {
                await ExecuteRunAsync(batch, assignment.RunId, assignment.GenerationId, streamMetrics, ct);

                await CompleteRunAsync(
                    assignment.RunId,
                    assignment.GenerationId,
                    wasForked: false,
                    forkedToRunId: null,
                    pendingMessageCount: 0,
                    isError: false,
                    ct: ct);

                Logger.LogInformation(
                    "{event_type} {event_status} {provider} {provider_mode} {thread_id} {run_id} {generation_id} {bridge_event_count} {item_updated_count} {item_completed_count}",
                    "codex.turn.completed",
                    "completed",
                    _options.Provider,
                    _options.ProviderMode,
                    ThreadId,
                    assignment.RunId,
                    assignment.GenerationId,
                    streamMetrics.BridgeEventCount,
                    streamMetrics.ItemUpdatedCount,
                    streamMetrics.ItemCompletedCount);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Logger.LogError(
                    ex,
                    "{event_type} {event_status} {provider} {provider_mode} {thread_id} {run_id} {generation_id} {bridge_event_count} {item_updated_count} {item_completed_count} {error_code} {exception_type}",
                    "codex.turn.failed",
                    "failed",
                    _options.Provider,
                    _options.ProviderMode,
                    ThreadId,
                    assignment.RunId,
                    assignment.GenerationId,
                    streamMetrics.BridgeEventCount,
                    streamMetrics.ItemUpdatedCount,
                    streamMetrics.ItemCompletedCount,
                    "turn_failed",
                    ex.GetType().Name);

                await CompleteRunAsync(
                    assignment.RunId,
                    assignment.GenerationId,
                    isError: true,
                    errorMessage: ex.Message,
                    ct: ct);
            }
        }
    }

    private async Task ExecuteRunAsync(
        IReadOnlyList<QueuedInput> inputs,
        string runId,
        string generationId,
        RunStreamMetrics streamMetrics,
        CancellationToken ct)
    {
        if (_client == null)
        {
            throw new InvalidOperationException("Codex client has not been initialized.");
        }

        var prompt = BuildPrompt(inputs);
        var runStopwatch = Stopwatch.StartNew();
        var eventSequence = 0;
        _nextMessageOrderIdx = 0;
        _activeMessageOrderByKey.Clear();

        await foreach (var envelope in _client.RunStreamingAsync(prompt, ct))
        {
            eventSequence++;
            var bridgeEventType = ExtractEventType(envelope.Event);
            streamMetrics.BridgeEventCount = eventSequence;
            if (string.Equals(bridgeEventType, "item.updated", StringComparison.Ordinal))
            {
                streamMetrics.ItemUpdatedCount++;
            }
            else if (string.Equals(bridgeEventType, "item.completed", StringComparison.Ordinal))
            {
                streamMetrics.ItemCompletedCount++;
            }

            Logger.LogInformation(
                "{event_type} {event_status} {provider} {provider_mode} {thread_id} {run_id} {generation_id} {input_id} {bridge_request_id} {codex_thread_id} {model} {latency_ms} {event_sequence} {bridge_event_type} {event_timestamp_ms}",
                "codex.bridge.event.received",
                "received",
                _options.Provider,
                _options.ProviderMode,
                ThreadId,
                runId,
                generationId,
                string.Empty,
                envelope.RequestId,
                envelope.ThreadId ?? _codexThreadId,
                _options.Model,
                runStopwatch.ElapsedMilliseconds,
                eventSequence,
                bridgeEventType,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

            var messages = ConvertEventToMessages(envelope.Event, runId, generationId);
            foreach (var message in messages)
            {
                AddToHistory(message);
                await PublishToAllAsync(message, ct);
                LogStreamingPublishTelemetry(
                    message,
                    runId,
                    generationId,
                    envelope.RequestId,
                    envelope.ThreadId,
                    runStopwatch.ElapsedMilliseconds,
                    eventSequence);
            }
        }
    }

    private sealed class RunStreamMetrics
    {
        public int BridgeEventCount { get; set; }

        public int ItemUpdatedCount { get; set; }

        public int ItemCompletedCount { get; set; }
    }

    private void LogStreamingPublishTelemetry(
        IMessage message,
        string runId,
        string generationId,
        string bridgeRequestId,
        string? codexThreadId,
        long latencyMs,
        int eventSequence)
    {
        if (message is TextUpdateMessage textUpdate)
        {
            Logger.LogInformation(
                "{event_type} {event_status} {provider} {provider_mode} {thread_id} {run_id} {generation_id} {input_id} {bridge_request_id} {codex_thread_id} {model} {latency_ms} {event_sequence} {event_timestamp_ms} {text_chars} {message_order_idx}",
                "codex.text_update.published",
                "published",
                _options.Provider,
                _options.ProviderMode,
                ThreadId,
                runId,
                generationId,
                string.Empty,
                bridgeRequestId,
                codexThreadId ?? _codexThreadId,
                _options.Model,
                latencyMs,
                eventSequence,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                textUpdate.Text?.Length ?? 0,
                textUpdate.MessageOrderIdx);
            return;
        }

        if (message is TextMessage { Role: Role.Assistant } textMessage)
        {
            Logger.LogInformation(
                "{event_type} {event_status} {provider} {provider_mode} {thread_id} {run_id} {generation_id} {input_id} {bridge_request_id} {codex_thread_id} {model} {latency_ms} {event_sequence} {event_timestamp_ms} {text_chars} {message_order_idx}",
                "codex.text.published",
                "published",
                _options.Provider,
                _options.ProviderMode,
                ThreadId,
                runId,
                generationId,
                string.Empty,
                bridgeRequestId,
                codexThreadId ?? _codexThreadId,
                _options.Model,
                latencyMs,
                eventSequence,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                textMessage.Text?.Length ?? 0,
                textMessage.MessageOrderIdx);
        }
    }

    private static string ExtractEventType(JsonElement eventElement)
    {
        return eventElement.TryGetProperty("type", out var typeProp) && typeProp.ValueKind == JsonValueKind.String
            ? typeProp.GetString() ?? "unknown"
            : "unknown";
    }

    private List<IMessage> ConvertEventToMessages(JsonElement eventElement, string runId, string generationId)
    {
        if (!eventElement.TryGetProperty("type", out var typeProp) || typeProp.ValueKind != JsonValueKind.String)
        {
            return [];
        }

        var eventType = typeProp.GetString();
        switch (eventType)
        {
            case "thread.started":
                if (eventElement.TryGetProperty("thread_id", out var threadIdProp))
                {
                    _codexThreadId = threadIdProp.GetString();
                    if (!string.IsNullOrWhiteSpace(_codexThreadId))
                    {
                        _systemPromptApplied = true;
                    }
                }

                return [];

            case "turn.completed":
                return [CreateUsageMessage(eventElement, runId, generationId, NextMessageOrderIdx())];

            case "turn.failed":
            {
                var err = ExtractErrorMessage(eventElement) ?? "Codex turn failed";
                throw new InvalidOperationException(err);
            }

            case "error":
            {
                var err = eventElement.TryGetProperty("message", out var msg)
                    ? msg.GetString()
                    : "Codex stream error";
                throw new InvalidOperationException(err);
            }

            case "item.started":
            case "item.updated":
            case "item.completed":
                return ConvertItemEvent(eventType, eventElement, runId, generationId);

            default:
                return [];
        }
    }

    private List<IMessage> ConvertItemEvent(string eventType, JsonElement eventElement, string runId, string generationId)
    {
        if (!eventElement.TryGetProperty("item", out var itemElement)
            || !itemElement.TryGetProperty("type", out var itemTypeProp)
            || itemTypeProp.ValueKind != JsonValueKind.String)
        {
            return [];
        }

        var itemType = itemTypeProp.GetString();
        var itemId = itemElement.TryGetProperty("id", out var id) ? id.GetString() : null;

        switch (itemType)
        {
            case "agent_message":
                return ConvertAgentMessage(eventType, itemElement, itemId, runId, generationId);

            case "reasoning":
                return ConvertReasoningMessage(eventType, itemElement, itemId, runId, generationId);

            case "mcp_tool_call":
                return ConvertToolCallMessage(eventType, itemElement, itemId, runId, generationId);

            case "command_execution":
            case "file_change":
            case "todo_list":
            case "web_search":
            case "error":
                return eventType == "item.completed"
                    ?
                    [
                        new ReasoningMessage
                        {
                            Role = Role.Assistant,
                            ThreadId = ThreadId,
                            RunId = runId,
                            GenerationId = generationId,
                            MessageOrderIdx = NextMessageOrderIdx(),
                            Visibility = ReasoningVisibility.Summary,
                            Reasoning = BuildSummary(itemType, itemElement),
                        },
                    ]
                    : [];

            default:
                return [];
        }
    }

    private List<IMessage> ConvertAgentMessage(
        string eventType,
        JsonElement itemElement,
        string? itemId,
        string runId,
        string generationId)
    {
        var text = itemElement.TryGetProperty("text", out var textProp) ? textProp.GetString() : null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var key = itemId ?? $"agent_message:{runId}";
        var orderIdx = GetOrCreateMessageOrderIdx($"agent:{key}");
        if (eventType is "item.started" or "item.updated")
        {
            var existing = _agentMessageAccumulator.TryGetValue(key, out var currentSnapshot)
                ? currentSnapshot
                : string.Empty;
            var (delta, accumulated) = AppendAndDiff(existing, text);
            _agentMessageAccumulator[key] = accumulated;
            if (string.IsNullOrEmpty(delta))
            {
                return [];
            }

            return
            [
                new TextUpdateMessage
                {
                    Role = Role.Assistant,
                    ThreadId = ThreadId,
                    RunId = runId,
                    GenerationId = generationId,
                    MessageOrderIdx = orderIdx,
                    Text = delta,
                },
            ];
        }

        if (eventType != "item.completed")
        {
            return [];
        }

        var messages = new List<IMessage>();
        var accumulatedText = _agentMessageAccumulator.TryGetValue(key, out var completionSnapshot)
            ? completionSnapshot
            : null;

        if (!string.IsNullOrEmpty(accumulatedText))
        {
            // Preserve raw provider semantics: only emit a tail delta when updates already started for this item.
            if (text.StartsWith(accumulatedText, StringComparison.Ordinal))
            {
                var tail = text[accumulatedText.Length..];
                if (!string.IsNullOrEmpty(tail))
                {
                    messages.Add(new TextUpdateMessage
                    {
                        Role = Role.Assistant,
                        ThreadId = ThreadId,
                        RunId = runId,
                        GenerationId = generationId,
                        MessageOrderIdx = orderIdx,
                        Text = tail,
                    });
                }
            }
        }

        _agentMessageAccumulator.Remove(key);

        messages.Add(
            new TextMessage
            {
                Role = Role.Assistant,
                ThreadId = ThreadId,
                RunId = runId,
                GenerationId = generationId,
                MessageOrderIdx = orderIdx,
                Text = text,
            });

        ReleaseMessageOrderIdx($"agent:{key}");
        return messages;
    }

    private List<IMessage> ConvertReasoningMessage(
        string eventType,
        JsonElement itemElement,
        string? itemId,
        string runId,
        string generationId)
    {
        var text = itemElement.TryGetProperty("text", out var textProp) ? textProp.GetString() : null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var key = itemId ?? $"reasoning:{runId}";
        var orderIdx = GetOrCreateMessageOrderIdx($"reasoning:{key}");
        if (eventType is "item.started" or "item.updated")
        {
            var existing = _reasoningAccumulator.TryGetValue(key, out var snapshot) ? snapshot : string.Empty;
            var (delta, accumulated) = AppendAndDiff(existing, text);
            _reasoningAccumulator[key] = accumulated;
            if (string.IsNullOrEmpty(delta))
            {
                return [];
            }

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
                    Reasoning = delta,
                },
            ];
        }

        if (eventType != "item.completed")
        {
            return [];
        }

        var messages = new List<IMessage>();
        if (_reasoningAccumulator.TryGetValue(key, out var accumulatedText)
            && !string.IsNullOrEmpty(accumulatedText)
            && text.StartsWith(accumulatedText, StringComparison.Ordinal))
        {
            var tail = text[accumulatedText.Length..];
            if (!string.IsNullOrEmpty(tail))
            {
                messages.Add(new ReasoningUpdateMessage
                {
                    Role = Role.Assistant,
                    ThreadId = ThreadId,
                    RunId = runId,
                    GenerationId = generationId,
                    MessageOrderIdx = orderIdx,
                    Visibility = ReasoningVisibility.Summary,
                    Reasoning = tail,
                });
            }
        }

        _reasoningAccumulator.Remove(key);
        messages.Add(
            new ReasoningMessage
            {
                Role = Role.Assistant,
                ThreadId = ThreadId,
                RunId = runId,
                GenerationId = generationId,
                MessageOrderIdx = orderIdx,
                Visibility = ReasoningVisibility.Summary,
                Reasoning = text,
            });

        ReleaseMessageOrderIdx($"reasoning:{key}");
        return messages;
    }

    private static (string Delta, string Accumulated) AppendAndDiff(string existing, string current)
    {
        if (string.IsNullOrEmpty(existing))
        {
            return (current, current);
        }

        if (current.StartsWith(existing, StringComparison.Ordinal))
        {
            return (current[existing.Length..], current);
        }

        // Some providers emit pure deltas instead of full snapshots.
        return (current, existing + current);
    }

    private List<IMessage> ConvertToolCallMessage(
        string eventType,
        JsonElement itemElement,
        string? itemId,
        string runId,
        string generationId)
    {
        var toolName = itemElement.TryGetProperty("tool", out var toolProp) ? toolProp.GetString() : null;
        var mcpServer = itemElement.TryGetProperty("server", out var serverProp) ? serverProp.GetString() : null;
        var toolMessageKey = $"tool:{itemId ?? toolName ?? "unknown"}";

        if (eventType == "item.started")
        {
            var orderIdx = GetOrCreateMessageOrderIdx(toolMessageKey);
            var args = itemElement.TryGetProperty("arguments", out var arguments)
                ? arguments.GetRawText()
                : "{}";

            Logger.LogInformation(
                "{event_type} {event_status} {provider} {provider_mode} {thread_id} {run_id} {generation_id} {tool_call_id} {mcp_server} {tool_name}",
                "codex.tool.started",
                "started",
                _options.Provider,
                _options.ProviderMode,
                ThreadId,
                runId,
                generationId,
                itemId,
                mcpServer,
                toolName);

            return
            [
                new ToolCallMessage
                {
                    Role = Role.Assistant,
                    ThreadId = ThreadId,
                    RunId = runId,
                    GenerationId = generationId,
                    MessageOrderIdx = orderIdx,
                    ToolCallId = itemId,
                    FunctionName = toolName,
                    FunctionArgs = args,
                    ExecutionTarget = ExecutionTarget.ProviderServer,
                    Metadata = ImmutableDictionary<string, object>.Empty.Add("mcp_server", mcpServer ?? string.Empty),
                },
            ];
        }

        if (eventType != "item.completed")
        {
            return [];
        }

        var status = itemElement.TryGetProperty("status", out var statusProp) ? statusProp.GetString() : "completed";
        var isError = string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase)
            || (itemElement.TryGetProperty("error", out var errorProp) && errorProp.ValueKind != JsonValueKind.Null);
        var completionOrderIdx = GetOrCreateMessageOrderIdx(toolMessageKey);

        var resultString = itemElement.TryGetProperty("result", out var resultProp)
            ? resultProp.GetRawText()
            : itemElement.TryGetProperty("error", out var errProp)
                ? errProp.GetRawText()
                : "{}";

        Logger.LogInformation(
            "{event_type} {event_status} {provider} {provider_mode} {thread_id} {run_id} {generation_id} {tool_call_id} {mcp_server} {tool_name} {error_code}",
            isError ? "codex.tool.failed" : "codex.tool.completed",
            isError ? "failed" : "completed",
            _options.Provider,
            _options.ProviderMode,
            ThreadId,
            runId,
            generationId,
            itemId,
            mcpServer,
            toolName,
            isError ? "mcp_tool_failed" : string.Empty);

        var resultMessages = new List<IMessage>
        {
            new ToolCallResultMessage
            {
                Role = Role.User,
                ThreadId = ThreadId,
                RunId = runId,
                GenerationId = generationId,
                ToolCallId = itemId,
                ToolName = toolName,
                IsError = isError,
                ErrorCode = isError ? "mcp_tool_failed" : null,
                Result = resultString,
                MessageOrderIdx = completionOrderIdx,
                ExecutionTarget = ExecutionTarget.ProviderServer,
                Metadata = ImmutableDictionary<string, object>.Empty.Add("mcp_server", mcpServer ?? string.Empty),
            },
        };

        ReleaseMessageOrderIdx(toolMessageKey);
        return resultMessages;
    }

    private UsageMessage CreateUsageMessage(JsonElement eventElement, string runId, string generationId, int messageOrderIdx)
    {
        var usage = new Usage();

        if (eventElement.TryGetProperty("usage", out var usageElement))
        {
            var inputTokens = usageElement.TryGetProperty("input_tokens", out var inProp) ? inProp.GetInt32() : 0;
            var outputTokens = usageElement.TryGetProperty("output_tokens", out var outProp) ? outProp.GetInt32() : 0;
            var cachedInputTokens = usageElement.TryGetProperty("cached_input_tokens", out var cachedProp)
                ? cachedProp.GetInt32()
                : 0;

            usage = new Usage
            {
                PromptTokens = inputTokens,
                CompletionTokens = outputTokens,
                TotalTokens = inputTokens + outputTokens,
                InputTokenDetails = new InputTokenDetails { CachedTokens = cachedInputTokens },
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

    private static string? ExtractErrorMessage(JsonElement eventElement)
    {
        if (eventElement.TryGetProperty("error", out var errorObj)
            && errorObj.ValueKind == JsonValueKind.Object
            && errorObj.TryGetProperty("message", out var msgProp)
            && msgProp.ValueKind == JsonValueKind.String)
        {
            return msgProp.GetString();
        }

        return null;
    }

    private static string BuildSummary(string itemType, JsonElement item)
    {
        return itemType switch
        {
            "command_execution" when item.TryGetProperty("command", out var cmd) => $"Command executed: {cmd.GetString()}",
            "file_change" when item.TryGetProperty("changes", out var changes) => $"File changes applied: {changes.GetRawText()}",
            "todo_list" when item.TryGetProperty("items", out var items) => $"Todo list updated: {items.GetRawText()}",
            "web_search" when item.TryGetProperty("query", out var query) => $"Web search completed: {query.GetString()}",
            "error" when item.TryGetProperty("message", out var errorMessage) => $"Codex item error: {errorMessage.GetString()}",
            _ => $"Codex item completed: {itemType}",
        };
    }

    private string BuildPrompt(IReadOnlyList<QueuedInput> inputs)
    {
        var sb = new StringBuilder();

        if (!_systemPromptApplied && !string.IsNullOrWhiteSpace(SystemPrompt))
        {
            _ = sb.AppendLine(SystemPrompt);
            _ = sb.AppendLine();
            _systemPromptApplied = true;
        }

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
                    _ = sb.AppendLine($"[Unsupported input message type: {message.GetType().Name}]");
                }
            }
        }

        return sb.ToString().Trim();
    }

    protected override async Task UpdateMetadataAsync(CancellationToken ct)
    {
        if (Store == null)
        {
            return;
        }

        try
        {
            var existing = await Store.LoadMetadataAsync(ThreadId, ct);
            var properties = existing?.Properties?.ToBuilder()
                ?? ImmutableDictionary.CreateBuilder<string, object>();

            if (!string.IsNullOrWhiteSpace(_codexThreadId))
            {
                properties[CodexThreadIdProperty] = _codexThreadId;
            }

            var metadata = new ThreadMetadata
            {
                ThreadId = ThreadId,
                CurrentRunId = null,
                LatestRunId = existing?.LatestRunId,
                LastUpdated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Properties = properties.ToImmutable(),
                SessionMappings = existing?.SessionMappings,
            };

            await Store.SaveMetadataAsync(ThreadId, metadata, ct);

            Logger.LogInformation(
                "{event_type} {event_status} {provider} {provider_mode} {thread_id} {codex_thread_id}",
                "codex.persistence.save",
                "completed",
                _options.Provider,
                _options.ProviderMode,
                ThreadId,
                _codexThreadId);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(
                ex,
                "{event_type} {event_status} {provider} {provider_mode} {thread_id} {error_code} {exception_type}",
                "codex.persistence.save",
                "failed",
                _options.Provider,
                _options.ProviderMode,
                ThreadId,
                "metadata_save_failed",
                ex.GetType().Name);
        }
    }

    public override async Task<bool> RecoverAsync(CancellationToken ct = default)
    {
        var recovered = await base.RecoverAsync(ct);

        if (Store == null)
        {
            return recovered;
        }

        var metadata = await Store.LoadMetadataAsync(ThreadId, ct);
        if (metadata?.Properties?.TryGetValue(CodexThreadIdProperty, out var value) == true)
        {
            _codexThreadId = value?.ToString();
            _systemPromptApplied = !string.IsNullOrWhiteSpace(_codexThreadId);
        }

        return recovered;
    }
}
