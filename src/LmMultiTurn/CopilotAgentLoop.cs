using System.Collections.Immutable;
using System.Diagnostics;
using AchieveAi.LmDotnetTools.CopilotSdkProvider.Agents;
using AchieveAi.LmDotnetTools.CopilotSdkProvider.Configuration;
using AchieveAi.LmDotnetTools.CopilotSdkProvider.Models;
using AchieveAi.LmDotnetTools.CopilotSdkProvider.Tools;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using Microsoft.Extensions.Logging;

namespace AchieveAi.LmDotnetTools.LmMultiTurn;

/// <summary>
/// Multi-turn agent loop that drives a Copilot ACP session via
/// <see cref="ICopilotSdkClient"/>. Mirrors the Codex agent loop but without MCP
/// server bridging or internal-tool span translation.
/// </summary>
public sealed class CopilotAgentLoop : MultiTurnAgentBase
{
    private const string CopilotSessionIdProperty = "copilot_session_id";

    private readonly CopilotSdkOptions _options;
    private readonly ILoggerFactory? _loggerFactory;
    private readonly Func<CopilotSdkOptions, ILogger?, ICopilotSdkClient>? _clientFactory;
    private readonly CopilotToolPolicyEngine _toolPolicy;
    private readonly CopilotDynamicToolBridge? _dynamicToolBridge;
    private readonly CopilotEventTranslator _translator;
    private ICopilotSdkClient? _client;
    private string? _copilotSessionId;
    private string? _generatedModelInstructionsFile;

    public CopilotAgentLoop(
        CopilotSdkOptions options,
        string threadId,
        string? systemPrompt = null,
        GenerateReplyOptions? defaultOptions = null,
        int inputChannelCapacity = 100,
        int outputChannelCapacity = 1000,
        IConversationStore? store = null,
        ILogger<CopilotAgentLoop>? logger = null,
        ILoggerFactory? loggerFactory = null,
        Func<CopilotSdkOptions, ILogger?, ICopilotSdkClient>? clientFactory = null)
        : this(
            options,
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

    public CopilotAgentLoop(
        CopilotSdkOptions options,
        FunctionRegistry? functionRegistry,
        IReadOnlyList<string>? enabledTools,
        string threadId,
        string? systemPrompt = null,
        GenerateReplyOptions? defaultOptions = null,
        int inputChannelCapacity = 100,
        int outputChannelCapacity = 1000,
        IConversationStore? store = null,
        ILogger<CopilotAgentLoop>? logger = null,
        ILoggerFactory? loggerFactory = null,
        Func<CopilotSdkOptions, ILogger?, ICopilotSdkClient>? clientFactory = null)
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
        _loggerFactory = loggerFactory;
        _clientFactory = clientFactory;

        IReadOnlyList<FunctionContract> dynamicContracts = [];
        IDictionary<string, Func<string, Task<string>>> dynamicHandlers = new Dictionary<string, Func<string, Task<string>>>(StringComparer.OrdinalIgnoreCase);
        if (functionRegistry != null && _options.ToolBridgeMode == CopilotToolBridgeMode.Dynamic)
        {
            var (contracts, handlers) = functionRegistry.Build();
            dynamicContracts = [.. contracts.Where(static c => !string.IsNullOrWhiteSpace(c.Name))];
            dynamicHandlers = new Dictionary<string, Func<string, Task<string>>>(handlers, StringComparer.OrdinalIgnoreCase);
        }

        _toolPolicy = new CopilotToolPolicyEngine(
            dynamicContracts.Select(static c => c.Name),
            enabledTools);

        if (dynamicContracts.Count > 0 && _options.ToolBridgeMode == CopilotToolBridgeMode.Dynamic)
        {
            _dynamicToolBridge = new CopilotDynamicToolBridge(
                dynamicContracts,
                dynamicHandlers,
                _toolPolicy,
                _loggerFactory?.CreateLogger<CopilotDynamicToolBridge>());
        }

        _translator = new CopilotEventTranslator(_options, logger)
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
            ? _clientFactory(_options, _loggerFactory?.CreateLogger<CopilotSdkClient>())
            : new CopilotSdkClient(_options, _loggerFactory?.CreateLogger<CopilotSdkClient>());

        if (_dynamicToolBridge != null)
        {
            _client.ConfigureDynamicToolExecutor(_dynamicToolBridge.ExecuteAsync);
        }
        else
        {
            _client.ConfigureDynamicToolExecutor(null);
        }

        var (baseInstructions, developerInstructions, modelInstructionsFile) = ResolveInstructions();
        var tools = _dynamicToolBridge?.GetToolSpecs();

        await _client.StartOrResumeSessionAsync(
            new CopilotBridgeInitOptions
            {
                Model = _options.Model,
                WorkingDirectory = _options.WorkingDirectory,
                BaseInstructions = baseInstructions,
                DeveloperInstructions = developerInstructions,
                ModelInstructionsFile = modelInstructionsFile,
                Tools = tools,
                BaseUrl = _options.BaseUrl,
                ApiKey = _options.ApiKey,
                SessionId = _copilotSessionId,
            });

        Logger.LogInformation(
            "{event_type} {event_status} {provider} {provider_mode} {thread_id} {copilot_session_id}",
            "copilot.persistence.resume",
            "completed",
            _options.Provider,
            _options.ProviderMode,
            ThreadId,
            _copilotSessionId);
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
                // best-effort cleanup
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
                "copilot.turn.assignment",
                "started",
                _options.Provider,
                _options.ProviderMode,
                ThreadId,
                assignment.RunId,
                assignment.GenerationId,
                string.Join(",", assignment.InputIds ?? []));

            Logger.LogInformation(
                "{event_type} {event_status} {provider} {provider_mode} {thread_id} {run_id} {generation_id} {batch_input_count} {queued_input_count}",
                "copilot.turn.queue",
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
                    "{event_type} {event_status} {provider} {provider_mode} {thread_id} {run_id} {generation_id} {bridge_event_count}",
                    "copilot.turn.completed",
                    "completed",
                    _options.Provider,
                    _options.ProviderMode,
                    ThreadId,
                    assignment.RunId,
                    assignment.GenerationId,
                    streamMetrics.BridgeEventCount);

                Logger.LogInformation(
                    "{event_type} {event_status} {provider} {provider_mode} {thread_id} {run_id} {generation_id} {run_age_ms}",
                    "copilot.turn.latency",
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
                    "{event_type} {event_status} {provider} {provider_mode} {thread_id} {run_id} {generation_id} {error_code} {exception_type}",
                    "copilot.turn.failed",
                    "failed",
                    _options.Provider,
                    _options.ProviderMode,
                    ThreadId,
                    assignment.RunId,
                    assignment.GenerationId,
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
            throw new InvalidOperationException("Copilot client has not been initialized.");
        }

        var prompt = CopilotEventTranslator.BuildPrompt(inputs, Logger);
        var runStopwatch = Stopwatch.StartNew();
        var eventSequence = 0;

        _translator.ThreadId = ThreadId;
        _translator.ResetRunState();
        _translator.LastExtractedCopilotSessionId = _copilotSessionId;

        try
        {
            await foreach (var envelope in _client.RunStreamingAsync(prompt, ct))
            {
                eventSequence++;
                streamMetrics.BridgeEventCount = eventSequence;

                Logger.LogDebug(
                    "{event_type} {event_status} {provider} {provider_mode} {thread_id} {run_id} {generation_id} {bridge_request_id} {copilot_session_id} {model} {latency_ms} {event_sequence}",
                    "copilot.bridge.event.received",
                    "received",
                    _options.Provider,
                    _options.ProviderMode,
                    ThreadId,
                    runId,
                    generationId,
                    envelope.RequestId,
                    envelope.SessionId ?? _copilotSessionId,
                    _options.Model,
                    runStopwatch.ElapsedMilliseconds,
                    eventSequence);

                var messages = _translator.ConvertEventToMessages(envelope.Event, runId, generationId);

                if (_translator.LastExtractedCopilotSessionId != _copilotSessionId)
                {
                    _copilotSessionId = _translator.LastExtractedCopilotSessionId;
                }

                foreach (var message in messages)
                {
                    AddToHistory(message);
                    await PublishToAllAsync(message, ct);
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
    }

    private (string? BaseInstructions, string? DeveloperInstructions, string? ModelInstructionsFile) ResolveInstructions()
    {
        var baseInstructions = string.IsNullOrWhiteSpace(_options.BaseInstructions) ? null : _options.BaseInstructions;
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
            _generatedModelInstructionsFile ??= CopilotEventTranslator.CreateModelInstructionsFile(developerInstructions);
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

            if (!string.IsNullOrWhiteSpace(_copilotSessionId))
            {
                properties[CopilotSessionIdProperty] = _copilotSessionId;
            }

            var updatedMetadata = metadata with
            {
                LastUpdated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Properties = properties.ToImmutable(),
            };

            await Store.SaveMetadataAsync(ThreadId, updatedMetadata, ct);

            Logger.LogInformation(
                "{event_type} {event_status} {provider} {provider_mode} {thread_id} {copilot_session_id}",
                "copilot.persistence.save",
                "completed",
                _options.Provider,
                _options.ProviderMode,
                ThreadId,
                _copilotSessionId);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(
                ex,
                "{event_type} {event_status} {provider} {provider_mode} {thread_id} {error_code} {exception_type}",
                "copilot.persistence.save",
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
        if (metadata?.Properties?.TryGetValue(CopilotSessionIdProperty, out var value) == true)
        {
            _copilotSessionId = value?.ToString();
        }

        return recovered;
    }
}
