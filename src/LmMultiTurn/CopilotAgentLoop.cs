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
    private bool _profileUnsupportedWarningLogged;

    /// <summary>
    /// Creates a new CopilotAgentLoop.
    /// </summary>
    /// <param name="options">Options for the Copilot SDK client</param>
    /// <param name="threadId">Unique identifier for this conversation thread</param>
    /// <param name="systemPrompt">System prompt for the agent (persists across all runs)</param>
    /// <param name="defaultOptions">Default GenerateReplyOptions template</param>
    /// <param name="inputChannelCapacity">Capacity of the input queue (default: 100)</param>
    /// <param name="outputChannelCapacity">Capacity per subscriber output channel (default: 1000)</param>
    /// <param name="store">Optional persistence store for conversation state</param>
    /// <param name="logger">Optional logger</param>
    /// <param name="loggerFactory">Optional logger factory for creating loggers for internal components</param>
    /// <param name="clientFactory">Optional factory for creating ICopilotSdkClient (for testing/mocking)</param>
    /// <param name="persistRunLedger">
    /// When true, enables durable run-ledger persistence via <see cref="IRunLedgerStore"/>
    /// (requires <paramref name="store"/> to implement it).
    /// </param>
    /// <param name="publicationObserver">
    /// Optional agent-wide hook observing every message this loop publishes (see
    /// <see cref="IAgentPublicationObserver"/>). Null (default) preserves existing behavior.
    /// </param>
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
        Func<CopilotSdkOptions, ILogger?, ICopilotSdkClient>? clientFactory = null,
        bool persistRunLedger = false,
        IAgentPublicationObserver? publicationObserver = null)
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
            clientFactory,
            persistRunLedger: persistRunLedger,
            publicationObserver: publicationObserver)
    {
    }

    /// <summary>
    /// Creates a new CopilotAgentLoop with optional dynamic tool bridging.
    /// </summary>
    /// <param name="options">Options for the Copilot SDK client</param>
    /// <param name="functionRegistry">Optional registry of dynamic functions to bridge as Copilot tools</param>
    /// <param name="enabledTools">Optional allow-list of tool names to expose to the session</param>
    /// <param name="threadId">Unique identifier for this conversation thread</param>
    /// <param name="systemPrompt">System prompt for the agent (persists across all runs)</param>
    /// <param name="defaultOptions">Default GenerateReplyOptions template</param>
    /// <param name="inputChannelCapacity">Capacity of the input queue (default: 100)</param>
    /// <param name="outputChannelCapacity">Capacity per subscriber output channel (default: 1000)</param>
    /// <param name="store">Optional persistence store for conversation state</param>
    /// <param name="logger">Optional logger</param>
    /// <param name="loggerFactory">Optional logger factory for creating loggers for internal components</param>
    /// <param name="clientFactory">Optional factory for creating ICopilotSdkClient (for testing/mocking)</param>
    /// <param name="persistRunLedger">
    /// When true, enables durable run-ledger persistence via <see cref="IRunLedgerStore"/>
    /// (requires <paramref name="store"/> to implement it).
    /// </param>
    /// <param name="publicationObserver">
    /// Optional agent-wide hook observing every message this loop publishes (see
    /// <see cref="IAgentPublicationObserver"/>). Null (default) preserves existing behavior.
    /// </param>
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
        Func<CopilotSdkOptions, ILogger?, ICopilotSdkClient>? clientFactory = null,
        bool persistRunLedger = false,
        IAgentPublicationObserver? publicationObserver = null)
        : base(
            threadId,
            systemPrompt,
            defaultOptions,
            maxTurnsPerRun: 1,
            inputChannelCapacity,
            outputChannelCapacity,
            store,
            logger,
            persistRunLedger: persistRunLedger,
            publicationObserver: publicationObserver)
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
            // Copilot SDK consumes legacy string-returning handlers. Deferred tool execution
            // is a MultiTurnAgentLoop-only feature; unwrap Resolved here and surface
            // NotSupportedException if a handler ever returns Deferred.
            dynamicHandlers = LegacyHandlerAdapter.WrapToLegacyHandlers(handlers, StringComparer.OrdinalIgnoreCase);
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

        LogProfileUnsupportedOnce();
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
                // Forward MCP servers explicitly even though ResolveEffectiveOptions
                // falls back to _options.McpServers — keeping the wiring visible at
                // the call site means future per-turn overrides have a single edit
                // point and tests can stub the client without surprise behavior.
                McpServers = _options.McpServers,
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

            var (batchParent, isExplicitFork) = ResolveBatchParent(batch);
            var assignment = await StartRunAsync(batch, batchParent, ct);

            // Use this run's own token (linked to, but independently cancellable from, the
            // loop's ct) for turn execution — a matching CancelCurrentRunAsync(RunId) call
            // signals only this token, so it never looks like outer loop-shutdown below.
            var runToken = CurrentRunToken;
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
                await ExecuteRunAsync(batch, assignment.RunId, assignment.GenerationId, streamMetrics, runToken);

                await CompleteRunAsync(
                    assignment.RunId,
                    assignment.GenerationId,
                    wasForked: isExplicitFork,
                    forkedToRunId: isExplicitFork ? assignment.RunId : null,
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
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // The loop's own token (ct) is NOT cancelled, so this OperationCanceledException
                // can only have come from runToken — a matching CancelCurrentRunAsync(RunId) call
                // for THIS run via expected-run Stop (ExecuteRunAsync already interrupted the
                // bridge client on this token — see its own catch). Complete as Cancelled and let
                // the outer while loop continue to the next input instead of propagating past this
                // catch, which has no ct.IsCancellationRequested guard and would otherwise let the
                // exception escape RunLoopAsync entirely.
                Logger.LogInformation(
                    "{event_type} {event_status} {provider} {provider_mode} {thread_id} {run_id} {generation_id}",
                    "copilot.turn.cancelled",
                    "cancelled",
                    _options.Provider,
                    _options.ProviderMode,
                    ThreadId,
                    assignment.RunId,
                    assignment.GenerationId);

                try
                {
                    await CompleteRunAsync(
                        assignment.RunId,
                        assignment.GenerationId,
                        wasForked: isExplicitFork,
                        forkedToRunId: isExplicitFork ? assignment.RunId : null,
                        isCancelled: true,
                        ct: ct);
                }
                catch (Exception completeEx)
                {
                    Logger.LogWarning(
                        completeEx,
                        "{event_type} {event_status} {provider} {provider_mode} {thread_id} {run_id} {generation_id}",
                        "copilot.turn.complete_on_cancel",
                        "failed",
                        _options.Provider,
                        _options.ProviderMode,
                        ThreadId,
                        assignment.RunId,
                        assignment.GenerationId);
                }
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

                try
                {
                    await CompleteRunAsync(
                        assignment.RunId,
                        assignment.GenerationId,
                        wasForked: isExplicitFork,
                        forkedToRunId: isExplicitFork ? assignment.RunId : null,
                        isError: true,
                        errorMessage: ex.Message,
                        ct: CancellationToken.None);
                }
                catch (Exception completeEx)
                {
                    Logger.LogWarning(
                        completeEx,
                        "{event_type} {event_status} {provider} {provider_mode} {thread_id} {run_id} {generation_id}",
                        "copilot.turn.complete_on_error",
                        "failed",
                        _options.Provider,
                        _options.ProviderMode,
                        ThreadId,
                        assignment.RunId,
                        assignment.GenerationId);
                }
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

    private void LogProfileUnsupportedOnce()
    {
        if (_profileUnsupportedWarningLogged)
        {
            return;
        }

        var profile = _options.Profile;
        if (profile is null)
        {
            return;
        }

        // Profile.McpServers is the legacy/profile-driven knob; callers should
        // route MCP via CopilotSdkOptions.McpServers instead so the single
        // explicit field controls what reaches the ACP `session/new` array.
        // The warning still counts profile entries so misuse is observable.
        var mcpCount = profile.McpServers.Count;
        var skillCount = profile.Skills.Count;
        var subAgentCount = profile.SubAgents.Count;
        if (mcpCount == 0 && skillCount == 0 && subAgentCount == 0)
        {
            return;
        }

        Logger.LogWarning(
            "{event_type} {event_status} {provider} {provider_mode} {thread_id} {mcp_count} {skill_count} {sub_agent_count}",
            "copilot.profile.unsupported",
            "ignored",
            _options.Provider,
            _options.ProviderMode,
            ThreadId,
            mcpCount,
            skillCount,
            subAgentCount);
        _profileUnsupportedWarningLogged = true;
    }

    private (string? BaseInstructions, string? DeveloperInstructions, string? ModelInstructionsFile) ResolveInstructions()
    {
        var baseInstructions = string.IsNullOrWhiteSpace(_options.BaseInstructions) ? null : _options.BaseInstructions;
        var developerInstructions = ProfileSystemPromptResolver.Resolve(
            _options.Profile,
            SystemPrompt,
            _options.DeveloperInstructions);
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
        catch (OperationCanceledException)
        {
            throw;
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
