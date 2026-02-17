using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using AchieveAi.LmDotnetTools.CodexSdkProvider.Agents;
using AchieveAi.LmDotnetTools.CodexSdkProvider.Configuration;
using AchieveAi.LmDotnetTools.CodexSdkProvider.Models;
using AchieveAi.LmDotnetTools.CodexSdkProvider.Tools;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
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
    private readonly CodexToolPolicyEngine _toolPolicy;
    private readonly CodexDynamicToolBridge? _dynamicToolBridge;
    private readonly Dictionary<string, string> _agentMessageAccumulator = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _reasoningAccumulator = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Usage> _latestUsageByTurn = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _activeMessageOrderByKey = new(StringComparer.Ordinal);
    private ICodexSdkClient? _client;
    private string? _codexThreadId;
    private string? _generatedModelInstructionsFile;
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
        : this(
            options,
            mcpServers,
            functionRegistry: null,
            enabledTools: null,
            threadId,
            systemPrompt,
            defaultOptions,
            inputChannelCapacity,
            outputChannelCapacity,
            store,
            logger,
            loggerFactory,
            clientFactory)
    {
    }

    public CodexAgentLoop(
        CodexSdkOptions options,
        IReadOnlyDictionary<string, CodexMcpServerConfig>? mcpServers,
        FunctionRegistry? functionRegistry,
        IReadOnlyList<string>? enabledTools,
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

        IReadOnlyList<FunctionContract> dynamicContracts = [];
        IDictionary<string, Func<string, Task<string>>> dynamicHandlers = new Dictionary<string, Func<string, Task<string>>>(StringComparer.OrdinalIgnoreCase);
        if (functionRegistry != null && _options.ToolBridgeMode is CodexToolBridgeMode.Dynamic or CodexToolBridgeMode.Hybrid)
        {
            var (contracts, handlers) = functionRegistry.Build();
            dynamicContracts = contracts.Where(static c => !string.IsNullOrWhiteSpace(c.Name)).ToList();
            dynamicHandlers = new Dictionary<string, Func<string, Task<string>>>(handlers, StringComparer.OrdinalIgnoreCase);
        }

        _toolPolicy = new CodexToolPolicyEngine(
            _mcpServers,
            dynamicContracts.Select(static c => c.Name),
            enabledTools);

        if (dynamicContracts.Count > 0 && _options.ToolBridgeMode is CodexToolBridgeMode.Dynamic or CodexToolBridgeMode.Hybrid)
        {
            _dynamicToolBridge = new CodexDynamicToolBridge(
                dynamicContracts,
                dynamicHandlers,
                _toolPolicy,
                _loggerFactory?.CreateLogger<CodexDynamicToolBridge>());
        }
    }

    protected override async Task OnBeforeRunAsync()
    {
        if (_client is { IsRunning: true })
        {
            return;
        }

        if (_client != null)
        {
            await _client.DisposeAsync();
            _client = null;
        }

        _client = _clientFactory != null
            ? _clientFactory(_options, _loggerFactory?.CreateLogger<CodexSdkClient>())
            : new CodexSdkClient(_options, _loggerFactory?.CreateLogger<CodexSdkClient>());

        if (_dynamicToolBridge != null)
        {
            _client.ConfigureDynamicToolExecutor((request, token) => _dynamicToolBridge.ExecuteAsync(request, token));
        }
        else
        {
            _client.ConfigureDynamicToolExecutor(null);
        }

        var (baseInstructions, developerInstructions, modelInstructionsFile) = ResolveInstructions();
        var effectiveWebSearchMode = _toolPolicy.IsBuiltInAllowed("web_search")
            ? _options.WebSearchMode
            : "disabled";
        var effectiveMcpServers = _options.ToolBridgeMode == CodexToolBridgeMode.Dynamic
            ? new Dictionary<string, CodexMcpServerConfig>()
            : _mcpServers;
        var dynamicTools = _options.ToolBridgeMode == CodexToolBridgeMode.Mcp
            ? null
            : _dynamicToolBridge?.GetToolSpecs();

        await _client.StartOrResumeThreadAsync(
            new CodexBridgeInitOptions
            {
                Model = _options.Model,
                ApprovalPolicy = _options.ApprovalPolicy,
                SandboxMode = _options.SandboxMode,
                SkipGitRepoCheck = _options.SkipGitRepoCheck,
                NetworkAccessEnabled = _options.NetworkAccessEnabled,
                WebSearchMode = effectiveWebSearchMode,
                WorkingDirectory = _options.WorkingDirectory,
                McpServers = effectiveMcpServers,
                BaseInstructions = baseInstructions,
                DeveloperInstructions = developerInstructions,
                ModelInstructionsFile = modelInstructionsFile,
                DynamicTools = dynamicTools,
                ToolBridgeMode = _options.ToolBridgeMode.ToString().ToLowerInvariant(),
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
        if (!string.IsNullOrWhiteSpace(_generatedModelInstructionsFile))
        {
            try
            {
                File.Delete(_generatedModelInstructionsFile);
            }
            catch
            {
                // Best effort cleanup for temporary instructions file.
            }
        }

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
            var queueDepth = InputReader.CanCount ? InputReader.Count : -1;
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

            Logger.LogInformation(
                "{event_type} {event_status} {provider} {provider_mode} {thread_id} {run_id} {generation_id} {batch_input_count} {queued_input_count}",
                "codex.turn.queue",
                "observed",
                _options.Provider,
                _options.ProviderMode,
                ThreadId,
                assignment.RunId,
                assignment.GenerationId,
                batch.Count,
                queueDepth);

            foreach (var input in batch)
            {
                foreach (var msg in input.Input.Messages)
                {
                    AddToHistory(msg);
                }
            }

            var streamMetrics = new RunStreamMetrics();
            var runTimer = Stopwatch.StartNew();
            try
            {
                await OnBeforeRunAsync();
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

                Logger.LogInformation(
                    "{event_type} {event_status} {provider} {provider_mode} {thread_id} {run_id} {generation_id} {run_age_ms}",
                    "codex.turn.latency",
                    "completed",
                    _options.Provider,
                    _options.ProviderMode,
                    ThreadId,
                    assignment.RunId,
                    assignment.GenerationId,
                    runTimer.ElapsedMilliseconds);
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

                Logger.LogWarning(
                    "{event_type} {event_status} {provider} {provider_mode} {thread_id} {run_id} {generation_id} {run_age_ms}",
                    "codex.turn.latency",
                    "failed",
                    _options.Provider,
                    _options.ProviderMode,
                    ThreadId,
                    assignment.RunId,
                    assignment.GenerationId,
                    runTimer.ElapsedMilliseconds);

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
        _latestUsageByTurn.Clear();

        try
        {
            await foreach (var envelope in _client.RunStreamingAsync(prompt, ct))
            {
                eventSequence++;
                var bridgeEventType = ExtractEventType(envelope.Event);
                streamMetrics.BridgeEventCount = eventSequence;
                if (string.Equals(bridgeEventType, "item.updated", StringComparison.Ordinal)
                    || string.Equals(bridgeEventType, "item/agentMessage/delta", StringComparison.Ordinal)
                    || string.Equals(bridgeEventType, "item/reasoning/textDelta", StringComparison.Ordinal)
                    || string.Equals(bridgeEventType, "item/reasoning/summaryTextDelta", StringComparison.Ordinal))
                {
                    streamMetrics.ItemUpdatedCount++;
                }
                else if (string.Equals(bridgeEventType, "item.completed", StringComparison.Ordinal)
                         || string.Equals(bridgeEventType, "item/completed", StringComparison.Ordinal))
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
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await _client.InterruptTurnAsync(CancellationToken.None);
            throw;
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

        var eventType = typeProp.GetString() ?? string.Empty;
        switch (eventType)
        {
            case "thread.started":
            case "thread/started":
                _codexThreadId = ExtractThreadId(eventElement, _codexThreadId);
                return [];

            case "thread/tokenUsage/updated":
                RecordTurnUsage(eventElement);
                return [];

            case "turn.completed":
            case "turn/completed":
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
            case "item/started":
            case "item/completed":
                return ConvertItemEvent(eventType, eventElement, runId, generationId);

            case "item/agentMessage/delta":
                return ConvertAgentMessageDeltaEvent(eventElement, runId, generationId);

            case "item/reasoning/textDelta":
            case "item/reasoning/summaryTextDelta":
                return ConvertReasoningDeltaEvent(eventElement, runId, generationId);

            default:
                return [];
        }
    }

    private List<IMessage> ConvertItemEvent(string eventType, JsonElement eventElement, string runId, string generationId)
    {
        var normalizedEventType = eventType switch
        {
            "item/started" => "item.started",
            "item/completed" => "item.completed",
            _ => eventType,
        };

        if (!eventElement.TryGetProperty("item", out var itemElement)
            || !itemElement.TryGetProperty("type", out var itemTypeProp)
            || itemTypeProp.ValueKind != JsonValueKind.String)
        {
            return [];
        }

        var itemType = NormalizeItemType(itemTypeProp.GetString());
        var itemId = itemElement.TryGetProperty("id", out var id) ? id.GetString() : null;

        switch (itemType)
        {
            case "agent_message":
                return ConvertAgentMessage(normalizedEventType, itemElement, itemId, runId, generationId);

            case "reasoning":
                return ConvertReasoningMessage(normalizedEventType, itemElement, itemId, runId, generationId);

            case "mcp_tool_call":
            case "tool_call":
            case "dynamic_tool_call":
                return ConvertToolCallMessage(normalizedEventType, itemElement, itemId, runId, generationId);

            case "command_execution":
            case "file_change":
            case "todo_list":
            case "web_search":
                if (_options.ExposeCodexInternalToolsAsToolMessages)
                {
                    var toolMessages = ConvertInternalToolItemToToolMessage(
                        normalizedEventType,
                        itemType,
                        itemElement,
                        itemId,
                        runId,
                        generationId);
                    if (toolMessages.Count > 0)
                    {
                        return toolMessages;
                    }

                    if (!_options.EmitLegacyInternalToolReasoningSummaries)
                    {
                        return [];
                    }
                }

                return normalizedEventType == "item.completed"
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

            case "error":
                return normalizedEventType == "item.completed"
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
                Logger.LogInformation(
                    "{event_type} {event_status} {provider} {provider_mode} {thread_id} {run_id} {generation_id} {codex_item_type} {bridge_event_type} {item_id}",
                    "codex.item.unmapped",
                    "ignored",
                    _options.Provider,
                    _options.ProviderMode,
                    ThreadId,
                    runId,
                    generationId,
                    itemType,
                    normalizedEventType,
                    itemId ?? string.Empty);
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
        var text = ExtractReasoningText(itemElement);
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

        var status = itemElement.TryGetProperty("status", out var statusProp) ? NormalizeStatus(statusProp.GetString()) : "completed";
        var isError = string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase)
            || (itemElement.TryGetProperty("error", out var errorProp) && errorProp.ValueKind != JsonValueKind.Null);
        var completionOrderIdx = GetOrCreateMessageOrderIdx(toolMessageKey);

        var resultString = itemElement.TryGetProperty("result", out var resultProp)
            ? ExtractCanonicalToolResult(resultProp)
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

    private List<IMessage> ConvertInternalToolItemToToolMessage(
        string eventType,
        string toolName,
        JsonElement itemElement,
        string? itemId,
        string runId,
        string generationId)
    {
        if (eventType is not ("item.started" or "item.completed"))
        {
            return [];
        }

        var callId = itemId;
        if (string.IsNullOrWhiteSpace(callId)
            && itemElement.TryGetProperty("call_id", out var callIdSnake)
            && callIdSnake.ValueKind == JsonValueKind.String)
        {
            callId = callIdSnake.GetString();
        }

        if (string.IsNullOrWhiteSpace(callId)
            && itemElement.TryGetProperty("callId", out var callIdCamel)
            && callIdCamel.ValueKind == JsonValueKind.String)
        {
            callId = callIdCamel.GetString();
        }

        if (string.IsNullOrWhiteSpace(callId))
        {
            Logger.LogWarning(
                "{event_type} {event_status} {provider} {provider_mode} {thread_id} {run_id} {generation_id} {codex_item_type} {reason}",
                "codex.internal_tool.mapping_failed",
                "failed",
                _options.Provider,
                _options.ProviderMode,
                ThreadId,
                runId,
                generationId,
                toolName,
                "missing_call_id");
            return [];
        }

        var source = eventType == "item.started" ? "item/started" : "item/completed";
        var arguments = BuildInternalToolArgumentsPayload(toolName, itemElement, source);
        var status = itemElement.TryGetProperty("status", out var statusProp) && statusProp.ValueKind == JsonValueKind.String
            ? NormalizeStatus(statusProp.GetString())
            : "completed";

        var completionStatus = status switch
        {
            "completed" => "success",
            "failed" => "error",
            "interrupted" => "cancelled",
            _ => "success",
        };

        var result = eventType == "item.completed"
            ? BuildInternalToolResultPayload(toolName, itemElement, source, completionStatus)
            : (JsonElement?)null;
        var error = eventType == "item.completed"
            ? BuildInternalToolErrorPayload(toolName, itemElement, completionStatus)
            : null;

        var normalizedItem = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["id"] = callId,
            ["type"] = "toolCall",
            ["tool"] = toolName,
            ["server"] = "codex_internal",
            ["status"] = eventType == "item.started"
                ? "inProgress"
                : error.HasValue ? "failed" : "completed",
            ["arguments"] = arguments,
            ["result"] = result.HasValue ? result.Value : null,
            ["error"] = error.HasValue ? error.Value : null,
        });

        return ConvertToolCallMessage(eventType, normalizedItem, callId, runId, generationId);
    }

    private static JsonElement BuildInternalToolArgumentsPayload(string toolName, JsonElement itemElement, string source)
    {
        var arguments = new Dictionary<string, object?>
        {
            ["source"] = source,
        };

        AddInternalToolFields(arguments, toolName, itemElement, isResultPayload: false);
        arguments["raw"] = itemElement;
        return JsonSerializer.SerializeToElement(arguments);
    }

    private static JsonElement BuildInternalToolResultPayload(
        string toolName,
        JsonElement itemElement,
        string source,
        string status)
    {
        var result = new Dictionary<string, object?>
        {
            ["source"] = source,
            ["status"] = status,
        };

        AddInternalToolFields(result, toolName, itemElement, isResultPayload: true);
        result["raw"] = itemElement;
        return JsonSerializer.SerializeToElement(result);
    }

    private static JsonElement? BuildInternalToolErrorPayload(string toolName, JsonElement itemElement, string status)
    {
        if (itemElement.TryGetProperty("error", out var explicitError)
            && explicitError.ValueKind != JsonValueKind.Null
            && explicitError.ValueKind != JsonValueKind.Undefined)
        {
            var error = new Dictionary<string, object?>
            {
                ["status"] = status,
                ["raw"] = explicitError,
                ["message"] = explicitError.ValueKind == JsonValueKind.String
                    ? explicitError.GetString()
                    : explicitError.TryGetProperty("message", out var messageProp) && messageProp.ValueKind == JsonValueKind.String
                        ? messageProp.GetString()
                        : $"Codex internal tool '{toolName}' failed.",
            };
            return JsonSerializer.SerializeToElement(error);
        }

        if (string.Equals(status, "success", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["status"] = status,
            ["message"] = $"Codex internal tool '{toolName}' completed with status '{status}'.",
        });
    }

    private static void AddInternalToolFields(
        Dictionary<string, object?> destination,
        string toolName,
        JsonElement itemElement,
        bool isResultPayload)
    {
        switch (toolName)
        {
            case "web_search":
                AddStringField(destination, itemElement, "query");
                AddStringField(destination, itemElement, "action");
                AddStringField(destination, itemElement, "target_url", "target_url", "targetUrl", "url");
                if (isResultPayload)
                {
                    AddStringField(destination, itemElement, "opened_url", "opened_url", "openedUrl", "url");
                    AddRawField(destination, itemElement, "matches", "matches", "resultMatches");
                    AddRawField(destination, itemElement, "snippets", "snippets", "results");
                }
                else
                {
                    AddRawField(destination, itemElement, "filters", "filters");
                }

                break;

            case "command_execution":
                AddStringField(destination, itemElement, "command");
                AddStringField(destination, itemElement, "cwd", "cwd", "workingDirectory");
                AddIntField(destination, itemElement, "timeout_ms", "timeout_ms", "timeoutMs");
                if (isResultPayload)
                {
                    AddIntField(destination, itemElement, "exit_code", "exit_code", "exitCode");
                    AddStringField(destination, itemElement, "stdout_excerpt", "stdout_excerpt", "stdout", "output");
                    AddStringField(destination, itemElement, "stderr_excerpt", "stderr_excerpt", "stderr");
                }

                break;

            case "file_change":
                AddStringField(destination, itemElement, "decision", "decision", "action");
                AddRawField(destination, itemElement, "changes", "changes", "patch", "files", "paths");
                break;

            case "todo_list":
                AddStringField(destination, itemElement, "operation", "operation", "action");
                AddRawField(destination, itemElement, "items", "items", "todos");
                break;
        }
    }

    private static void AddStringField(Dictionary<string, object?> destination, JsonElement payload, string targetName, params string[] sourceCandidates)
    {
        var names = sourceCandidates.Length == 0 ? new[] { targetName } : sourceCandidates;
        foreach (var candidate in names)
        {
            if (payload.TryGetProperty(candidate, out var value) && value.ValueKind == JsonValueKind.String)
            {
                destination[targetName] = value.GetString();
                return;
            }
        }
    }

    private static void AddIntField(Dictionary<string, object?> destination, JsonElement payload, string targetName, params string[] sourceCandidates)
    {
        var names = sourceCandidates.Length == 0 ? new[] { targetName } : sourceCandidates;
        foreach (var candidate in names)
        {
            if (!payload.TryGetProperty(candidate, out var value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var intValue))
            {
                destination[targetName] = intValue;
                return;
            }

            if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out intValue))
            {
                destination[targetName] = intValue;
                return;
            }
        }
    }

    private static void AddRawField(Dictionary<string, object?> destination, JsonElement payload, string targetName, params string[] sourceCandidates)
    {
        var names = sourceCandidates.Length == 0 ? new[] { targetName } : sourceCandidates;
        foreach (var candidate in names)
        {
            if (payload.TryGetProperty(candidate, out var value))
            {
                destination[targetName] = value;
                return;
            }
        }
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
        else
        {
            var turnId = ExtractTurnId(eventElement);
            if (!string.IsNullOrWhiteSpace(turnId)
                && _latestUsageByTurn.TryGetValue(turnId, out var latestUsage))
            {
                usage = latestUsage;
            }
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

    private static string ExtractCanonicalToolResult(JsonElement resultProp)
    {
        if (resultProp.ValueKind == JsonValueKind.String)
        {
            return resultProp.GetString() ?? string.Empty;
        }

        // Codex MCP tool results are often wrapped as:
        // {"content":[{"type":"text","text":"..."}], "structured_content": ...}
        if (resultProp.ValueKind == JsonValueKind.Object
            && resultProp.TryGetProperty("content", out var contentProp)
            && contentProp.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in contentProp.EnumerateArray())
            {
                if (entry.ValueKind == JsonValueKind.Object
                    && entry.TryGetProperty("type", out var typeProp)
                    && typeProp.ValueKind == JsonValueKind.String
                    && string.Equals(typeProp.GetString(), "text", StringComparison.Ordinal)
                    && entry.TryGetProperty("text", out var textProp)
                    && textProp.ValueKind == JsonValueKind.String)
                {
                    return textProp.GetString() ?? string.Empty;
                }
            }
        }

        return resultProp.GetRawText();
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

    private (string? BaseInstructions, string? DeveloperInstructions, string? ModelInstructionsFile) ResolveInstructions()
    {
        var baseInstructions = string.IsNullOrWhiteSpace(_options.BaseInstructions)
            ? null
            : _options.BaseInstructions;
        var developerInstructions = !string.IsNullOrWhiteSpace(SystemPrompt)
            ? SystemPrompt
            : string.IsNullOrWhiteSpace(_options.DeveloperInstructions)
                ? null
                : _options.DeveloperInstructions;
        var modelInstructionsFile = string.IsNullOrWhiteSpace(_options.ModelInstructionsFile)
            ? null
            : _options.ModelInstructionsFile;

        if (string.IsNullOrWhiteSpace(modelInstructionsFile)
            && !string.IsNullOrWhiteSpace(developerInstructions)
            && developerInstructions.Length > _options.UseModelInstructionsFileThresholdChars)
        {
            _generatedModelInstructionsFile ??= CreateModelInstructionsFile(developerInstructions);
            modelInstructionsFile = _generatedModelInstructionsFile;
            developerInstructions = null;
        }

        return (baseInstructions, developerInstructions, modelInstructionsFile);
    }

    private static string CreateModelInstructionsFile(string developerInstructions)
    {
        var tempFilePath = Path.Combine(
            Path.GetTempPath(),
            $"codex-model-instructions-{Guid.NewGuid():N}.txt");
        File.WriteAllText(tempFilePath, developerInstructions);
        return tempFilePath;
    }

    private List<IMessage> ConvertAgentMessageDeltaEvent(JsonElement eventElement, string runId, string generationId)
    {
        var itemId = eventElement.TryGetProperty("itemId", out var itemIdProp) && itemIdProp.ValueKind == JsonValueKind.String
            ? itemIdProp.GetString()
            : null;
        var delta = eventElement.TryGetProperty("delta", out var deltaProp) && deltaProp.ValueKind == JsonValueKind.String
            ? deltaProp.GetString()
            : null;
        if (string.IsNullOrEmpty(delta))
        {
            return [];
        }

        var key = itemId ?? $"agent_message:{runId}";
        var orderIdx = GetOrCreateMessageOrderIdx($"agent:{key}");
        var existing = _agentMessageAccumulator.TryGetValue(key, out var currentText) ? currentText : string.Empty;
        _agentMessageAccumulator[key] = existing + delta;

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

    private List<IMessage> ConvertReasoningDeltaEvent(JsonElement eventElement, string runId, string generationId)
    {
        var itemId = eventElement.TryGetProperty("itemId", out var itemIdProp) && itemIdProp.ValueKind == JsonValueKind.String
            ? itemIdProp.GetString()
            : null;
        var delta = eventElement.TryGetProperty("delta", out var deltaProp) && deltaProp.ValueKind == JsonValueKind.String
            ? deltaProp.GetString()
            : null;
        if (string.IsNullOrEmpty(delta))
        {
            return [];
        }

        var key = itemId ?? $"reasoning:{runId}";
        var orderIdx = GetOrCreateMessageOrderIdx($"reasoning:{key}");
        var existing = _reasoningAccumulator.TryGetValue(key, out var currentText) ? currentText : string.Empty;
        _reasoningAccumulator[key] = existing + delta;

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

    private void RecordTurnUsage(JsonElement eventElement)
    {
        var turnId = ExtractTurnId(eventElement);
        if (string.IsNullOrWhiteSpace(turnId))
        {
            return;
        }

        if (!eventElement.TryGetProperty("tokenUsage", out var tokenUsage)
            || tokenUsage.ValueKind != JsonValueKind.Object
            || !tokenUsage.TryGetProperty("last", out var lastUsage)
            || lastUsage.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var inputTokens = GetInt32(lastUsage, "inputTokens");
        var outputTokens = GetInt32(lastUsage, "outputTokens");
        var cachedInputTokens = GetInt32(lastUsage, "cachedInputTokens");
        var reasoningOutputTokens = GetInt32(lastUsage, "reasoningOutputTokens");

        _latestUsageByTurn[turnId] = new Usage
        {
            PromptTokens = inputTokens,
            CompletionTokens = outputTokens,
            TotalTokens = inputTokens + outputTokens,
            InputTokenDetails = new InputTokenDetails
            {
                CachedTokens = cachedInputTokens,
            },
            OutputTokenDetails = new OutputTokenDetails
            {
                ReasoningTokens = reasoningOutputTokens,
            },
        };
    }

    private static int GetInt32(JsonElement obj, string propertyName)
    {
        if (!obj.TryGetProperty(propertyName, out var property))
        {
            return 0;
        }

        return property.ValueKind switch
        {
            JsonValueKind.Number => property.TryGetInt32(out var value) ? value : 0,
            JsonValueKind.String => int.TryParse(property.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0,
            _ => 0,
        };
    }

    private static string? ExtractThreadId(JsonElement eventElement, string? fallbackThreadId)
    {
        if (eventElement.TryGetProperty("thread_id", out var threadIdProp)
            && threadIdProp.ValueKind == JsonValueKind.String)
        {
            return threadIdProp.GetString() ?? fallbackThreadId;
        }

        if (eventElement.TryGetProperty("threadId", out var threadIdCamel)
            && threadIdCamel.ValueKind == JsonValueKind.String)
        {
            return threadIdCamel.GetString() ?? fallbackThreadId;
        }

        if (eventElement.TryGetProperty("thread", out var threadObj)
            && threadObj.ValueKind == JsonValueKind.Object
            && threadObj.TryGetProperty("id", out var threadIdObj)
            && threadIdObj.ValueKind == JsonValueKind.String)
        {
            return threadIdObj.GetString() ?? fallbackThreadId;
        }

        return fallbackThreadId;
    }

    private static string? ExtractTurnId(JsonElement eventElement)
    {
        if (eventElement.TryGetProperty("turn_id", out var turnIdProp)
            && turnIdProp.ValueKind == JsonValueKind.String)
        {
            return turnIdProp.GetString();
        }

        if (eventElement.TryGetProperty("turnId", out var turnIdCamel)
            && turnIdCamel.ValueKind == JsonValueKind.String)
        {
            return turnIdCamel.GetString();
        }

        if (eventElement.TryGetProperty("turn", out var turnObj)
            && turnObj.ValueKind == JsonValueKind.Object
            && turnObj.TryGetProperty("id", out var turnIdObj)
            && turnIdObj.ValueKind == JsonValueKind.String)
        {
            return turnIdObj.GetString();
        }

        return null;
    }

    private static string NormalizeItemType(string? itemType)
    {
        return itemType switch
        {
            "agentMessage" => "agent_message",
            "mcpToolCall" => "mcp_tool_call",
            "toolCall" => "tool_call",
            "dynamicToolCall" => "dynamic_tool_call",
            "tool_call" => "tool_call",
            "dynamic_tool_call" => "dynamic_tool_call",
            "commandExecution" => "command_execution",
            "fileChange" => "file_change",
            "todoList" => "todo_list",
            "webSearch" => "web_search",
            "userMessage" => "user_message",
            null => string.Empty,
            _ => itemType,
        };
    }

    private static string NormalizeStatus(string? status)
    {
        return status switch
        {
            "inProgress" => "in_progress",
            "completed" => "completed",
            "failed" => "failed",
            "interrupted" => "interrupted",
            null => "completed",
            _ => status,
        };
    }

    private static string? ExtractReasoningText(JsonElement itemElement)
    {
        if (itemElement.TryGetProperty("text", out var textProp) && textProp.ValueKind == JsonValueKind.String)
        {
            return textProp.GetString();
        }

        if (itemElement.TryGetProperty("summary", out var summaryProp) && summaryProp.ValueKind == JsonValueKind.Array)
        {
            var summaryLines = summaryProp
                .EnumerateArray()
                .Where(static line => line.ValueKind == JsonValueKind.String)
                .Select(static line => line.GetString())
                .Where(static line => !string.IsNullOrWhiteSpace(line))
                .ToList();
            if (summaryLines.Count > 0)
            {
                return string.Join(Environment.NewLine, summaryLines);
            }
        }

        if (itemElement.TryGetProperty("content", out var contentProp) && contentProp.ValueKind == JsonValueKind.Array)
        {
            var contentLines = contentProp
                .EnumerateArray()
                .Where(static line => line.ValueKind == JsonValueKind.String)
                .Select(static line => line.GetString())
                .Where(static line => !string.IsNullOrWhiteSpace(line))
                .ToList();
            if (contentLines.Count > 0)
            {
                return string.Join(Environment.NewLine, contentLines);
            }
        }

        return null;
    }

    private string BuildPrompt(IReadOnlyList<QueuedInput> inputs)
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
        }

        return recovered;
    }
}
