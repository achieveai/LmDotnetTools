using System.Collections.Immutable;
using System.Diagnostics;
using AchieveAi.LmDotnetTools.CodexSdkProvider.Agents;
using AchieveAi.LmDotnetTools.CodexSdkProvider.Configuration;
using AchieveAi.LmDotnetTools.CodexSdkProvider.Models;
using AchieveAi.LmDotnetTools.CodexSdkProvider.Tools;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
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
    private readonly CodexEventTranslator _translator;
    private ICodexSdkClient? _client;
    private string? _codexThreadId;
    private string? _generatedModelInstructionsFile;

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
            dynamicContracts = [.. contracts.Where(static c => !string.IsNullOrWhiteSpace(c.Name))];
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

        _translator = new CodexEventTranslator(_options, logger)
        {
            ThreadId = threadId,
        };
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
            _client.ConfigureDynamicToolExecutor(_dynamicToolBridge.ExecuteAsync);
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

        var prompt = CodexEventTranslator.BuildPrompt(inputs, Logger);
        var runStopwatch = Stopwatch.StartNew();
        var eventSequence = 0;

        _translator.ThreadId = ThreadId;
        _translator.ResetRunState();
        _translator.LastExtractedCodexThreadId = _codexThreadId;

        try
        {
            await foreach (var envelope in _client.RunStreamingAsync(prompt, ct))
            {
                eventSequence++;
                var bridgeEventType = CodexEventTranslator.ExtractEventType(envelope.Event);
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

                var messages = _translator.ConvertEventToMessages(envelope.Event, runId, generationId);

                // Update codex thread ID if translator extracted one
                if (_translator.LastExtractedCodexThreadId != _codexThreadId)
                {
                    _codexThreadId = _translator.LastExtractedCodexThreadId;
                }

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
            _generatedModelInstructionsFile ??= CodexEventTranslator.CreateModelInstructionsFile(developerInstructions);
            modelInstructionsFile = _generatedModelInstructionsFile;
            developerInstructions = null;
        }

        return (baseInstructions, developerInstructions, modelInstructionsFile);
    }

    protected override async Task UpdateMetadataAsync(CancellationToken ct)
    {
        if (Store == null)
        {
            return;
        }

        try
        {
            await base.UpdateMetadataAsync(ct);

            var metadata = await Store.LoadMetadataAsync(ThreadId, ct);
            if (metadata == null)
            {
                return;
            }

            var properties = metadata.Properties?.ToBuilder()
                ?? ImmutableDictionary.CreateBuilder<string, object>();

            if (!string.IsNullOrWhiteSpace(_codexThreadId))
            {
                properties[CodexThreadIdProperty] = _codexThreadId;
            }

            var updatedMetadata = metadata with
            {
                LastUpdated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Properties = properties.ToImmutable(),
            };

            await Store.SaveMetadataAsync(ThreadId, updatedMetadata, ct);

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
