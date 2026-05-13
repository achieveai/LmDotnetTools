using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Agents;
using AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Configuration;
using AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Models;
using AchieveAi.LmDotnetTools.LmCore.AgentRuntime;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using Microsoft.Extensions.Logging;
#pragma warning disable IDE0058 // Expression value is never used

namespace AchieveAi.LmDotnetTools.LmMultiTurn;

/// <summary>
/// Multi-turn agent implementation using Claude Agent SDK CLI with MCP tools.
/// Thread-safe for concurrent input via SendAsync.
/// Supports multiple independent output subscribers via SubscribeAsync.
/// </summary>
/// <remarks>
/// This class works directly with ClaudeAgentSdkClient (no intermediate agent layer).
/// Tools are exposed via MCP servers configured externally.
/// </remarks>
public sealed class ClaudeAgentLoop : MultiTurnAgentBase
{
    private readonly ClaudeAgentSdkOptions _claudeOptions;
    private readonly Dictionary<string, McpServerConfig> _mcpServers;
    private readonly ILoggerFactory? _loggerFactory;
    private readonly Func<ClaudeAgentSdkOptions, ILogger?, IClaudeAgentSdkClient>? _clientFactory;
    private readonly SemaphoreSlim _restartLock = new(1, 1);
    private readonly MaterializedProfile _materializedProfile;

    private IClaudeAgentSdkClient? _client;

    /// <summary>
    /// The most recent session ID observed from the underlying claude-agent-sdk run.
    /// Becomes non-null shortly after the first message is observed in a run, and
    /// persists across runs so it can be passed as <c>--resume</c> on the next run.
    /// Also written into <see cref="ThreadMetadata.SessionMappings"/> when a store
    /// is configured. SDK metadata such as <c>SystemInitMessage</c> is intentionally
    /// NOT published into the common message stream — read this property instead.
    ///
    /// May also be pre-populated via the <c>initialSessionId</c> constructor
    /// parameter so callers driving session resumption can have the first
    /// underlying run pass <c>--resume &lt;SessionId&gt;</c> before any
    /// <c>SystemInitMessage</c> has been observed.
    /// </summary>
    public string? CurrentSessionId { get; private set; }

    /// <summary>
    /// Tracks inputs sent to CLI, awaiting enqueue/dequeue confirmation.
    /// Used for correlating queue-operation events with original user inputs.
    /// Concurrent because writers run on the run-loop thread (Enqueue) while
    /// readers can run on Claude SDK event-handler threads (TryDequeue from
    /// <see cref="OnDequeueDetectedAsync"/>) and on the run-loop thread again
    /// (TryDequeue from <see cref="FlushPendingRunAssignmentsAsync"/>).
    /// Internal for direct test coverage of the flush path.
    /// </summary>
    internal readonly ConcurrentQueue<(QueuedInput Input, RunAssignment Assignment)> _pendingCliInputs = new();

    /// <summary>
    /// The active subscription to stdout messages (Interactive mode).
    /// </summary>
    private IAsyncEnumerator<IMessage>? _activeSubscription;

    /// <summary>
    /// Track if we're waiting for a dequeue (previous send not yet assigned).
    /// When true, new messages should be queued locally instead of sent to CLI.
    /// Volatile because the field is read on Claude SDK event-handler threads
    /// while the run-loop thread mutates it.
    /// </summary>
    private volatile bool _awaitingDequeue;
    private string? _lastObservedGenerationId;

    /// <summary>
    /// Local queue for messages that arrive while we're waiting for dequeue.
    /// Stores both messages AND their source inputs for correlation with RunAssignment.
    /// </summary>
    private readonly Queue<(List<IMessage> Messages, List<QueuedInput> Inputs)> _localMessageQueue = new();

    /// <summary>
    /// Track pending tool calls to prevent message loss.
    /// When pendingToolCalls > 0, messages should be queued locally instead of sent to CLI
    /// because the SDK queue will REMOVE them when a new message arrives during tool execution.
    /// </summary>
    private int _pendingToolCalls;

    /// <summary>
    /// Track whether a run is in progress (generation + tool execution).
    /// When true, the SDK queue may REMOVE messages, so we queue locally.
    /// Only reset to false when ResultEventMessage is received (run complete).
    /// This is more robust than _awaitingDequeue which can be reset by heuristics mid-run.
    /// </summary>
    private bool _runInProgress;

    /// <summary>
    /// Creates a new ClaudeAgentLoop.
    /// </summary>
    /// <param name="claudeOptions">Options for the ClaudeAgentSdk client</param>
    /// <param name="mcpServers">MCP server configurations for tools</param>
    /// <param name="threadId">Unique identifier for this conversation thread</param>
    /// <param name="systemPrompt">System prompt for the agent (persists across all runs)</param>
    /// <param name="defaultOptions">Default GenerateReplyOptions template</param>
    /// <param name="inputChannelCapacity">Capacity of the input queue (default: 100)</param>
    /// <param name="outputChannelCapacity">Capacity per subscriber output channel (default: 1000)</param>
    /// <param name="store">Optional persistence store for conversation state</param>
    /// <param name="logger">Optional logger</param>
    /// <param name="loggerFactory">Optional logger factory for creating loggers for internal components</param>
    /// <param name="clientFactory">Optional factory for creating ClaudeAgentSdkClient (for testing/mocking)</param>
    /// <param name="initialSessionId">
    /// Optional Claude SDK session id to seed <see cref="CurrentSessionId"/> with before
    /// the first run starts. When non-empty, the first <see cref="BuildClaudeAgentSdkRequest"/>
    /// call will emit <c>--resume &lt;SessionId&gt;</c>, letting callers drive resumption of
    /// a previously persisted Claude SDK session. <c>null</c> or empty leaves
    /// <see cref="CurrentSessionId"/> unset. If the SDK later assigns a different id via
    /// <see cref="SystemInitMessage"/> or <see cref="IClaudeAgentSdkClient.CurrentSession"/>,
    /// that live value replaces the seed (the transition is logged at Warning level).
    /// Note: when <see cref="ClaudeAgentSdkOptions.DisableSessionPersistence"/> is also true,
    /// the CLI is likely to reject the resume because no session is persisted on disk —
    /// the seed is preserved regardless so callers managing persistence externally are not
    /// silently overridden, and a Warning is logged on each request build.
    /// </param>
    public ClaudeAgentLoop(
        ClaudeAgentSdkOptions claudeOptions,
        Dictionary<string, McpServerConfig>? mcpServers,
        string threadId,
        string? systemPrompt = null,
        GenerateReplyOptions? defaultOptions = null,
        int inputChannelCapacity = 100,
        int outputChannelCapacity = 1000,
        IConversationStore? store = null,
        ILogger<ClaudeAgentLoop>? logger = null,
        ILoggerFactory? loggerFactory = null,
        Func<ClaudeAgentSdkOptions, ILogger?, IClaudeAgentSdkClient>? clientFactory = null,
        string? initialSessionId = null)
        : base(
            threadId,
            systemPrompt,
            defaultOptions,
            claudeOptions?.MaxTurnsPerRun ?? 50,
            inputChannelCapacity,
            outputChannelCapacity,
            store,
            logger)
    {
        ArgumentNullException.ThrowIfNull(claudeOptions);

        _claudeOptions = claudeOptions;
        _mcpServers = mcpServers ?? [];
        _loggerFactory = loggerFactory;
        _clientFactory = clientFactory;

        // Seed CurrentSessionId via the same mutation path used by live SDK
        // events so there is a single source of truth for session-id state
        // transitions. CaptureSessionId itself no-ops on null/empty/duplicate.
        if (!string.IsNullOrEmpty(initialSessionId))
        {
            CaptureSessionId(initialSessionId, isSeed: true);
        }

        // MUST remain the last statement in this constructor: the returned MaterializedProfile
        // owns a temp directory and is disposed by DisposeAsync. Any throw after this line would
        // leak the staging dir.
        _materializedProfile = ProfileMaterializer.Materialize(claudeOptions.Profile);
    }

    /// <inheritdoc />
    protected override async Task OnBeforeRunAsync()
    {
        // In Interactive mode, reuse existing running client
        if (_claudeOptions.Mode == ClaudeAgentSdkMode.Interactive && _client?.IsRunning == true)
        {
            Logger.LogDebug("Interactive mode: Reusing existing running client");
            return;
        }

        // Clean up existing client if present but not usable
        if (_client != null)
        {
            Logger.LogInformation(
                "Cleaning up existing client. Mode: {Mode}, IsRunning: {IsRunning}",
                _claudeOptions.Mode,
                _client.IsRunning);
            await DisposeClientResourcesAsync();
        }

        Logger.LogInformation(
            "Initializing ClaudeAgentSdk with {Count} MCP servers",
            _mcpServers.Count);

        foreach (var (name, config) in _mcpServers)
        {
            Logger.LogDebug(
                "MCP Server '{Name}': Type={Type}, Command={Command}, Url={Url}",
                name,
                config.Type,
                config.Command,
                config.Url);
        }

        CreateClientResources();
    }

    /// <inheritdoc />
    protected override async Task OnDisposeAsync()
    {
        Logger.LogDebug("Disposing ClaudeAgentLoop resources");
        await DisposeClientResourcesAsync();
        _restartLock.Dispose();
        _materializedProfile.Dispose();
    }

    /// <inheritdoc />
    protected override async Task OnAfterRunAsync()
    {
        // In OneShot mode, clean up client after run completes
        if (_claudeOptions.Mode == ClaudeAgentSdkMode.OneShot)
        {
            Logger.LogDebug("OneShot mode: Cleaning up client after run");
            await DisposeClientResourcesAsync();
        }
        // In Interactive mode, keep client alive for next run
    }

    /// <summary>
    /// Persist the latest Claude SDK session id into <see cref="ThreadMetadata.SessionMappings"/>
    /// alongside the base metadata. The mapping uses the conventional
    /// <c>"claude-sdk:{sessionId}"</c> key shape so it does not collide with mappings
    /// from other providers.
    /// </summary>
    protected override async Task UpdateMetadataAsync(CancellationToken ct)
    {
        if (Store == null)
        {
            return;
        }

        try
        {
            var latestRun = LatestRunId;
            var existing = await Store.LoadMetadataAsync(ThreadId, ct);

            // Merge claude-sdk session mapping into the existing dictionary.
            var sessionMappings = existing?.SessionMappings;
            if (!string.IsNullOrEmpty(CurrentSessionId) && !string.IsNullOrEmpty(latestRun))
            {
                var merged = sessionMappings == null
                    ? []
                    : new Dictionary<string, string>(sessionMappings);
                merged[$"claude-sdk:{CurrentSessionId}"] = latestRun;
                sessionMappings = merged;
            }

            var metadata = new ThreadMetadata
            {
                ThreadId = ThreadId,
                CurrentRunId = null,
                LatestRunId = latestRun,
                LastUpdated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Properties = existing?.Properties,
                SessionMappings = sessionMappings,
            };

            await Store.SaveMetadataAsync(ThreadId, metadata, ct);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to update thread metadata");
        }
    }

    /// <summary>
    /// Disposes the client and clears references asynchronously.
    /// </summary>
    private async Task DisposeClientResourcesAsync()
    {
        if (_activeSubscription != null)
        {
            try
            {
                await _activeSubscription.DisposeAsync().AsTask()
                    .WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (TimeoutException)
            {
                Logger.LogWarning("Subscription dispose timed out after 2s");
            }

            _activeSubscription = null;
        }

        // Snapshot session id from the client before tearing it down so
        // OneShot mode (which disposes the client after every run) doesn't
        // lose the session id assigned by the SDK during the run.
        if (_client?.CurrentSession?.SessionId is { } sid)
        {
            CaptureSessionId(sid);
        }

        (_client as IDisposable)?.Dispose();
        _client = null;
    }

    /// <summary>
    /// Tracks whether the current <see cref="CurrentSessionId"/> originated from the
    /// <c>initialSessionId</c> constructor parameter (i.e. caller-seeded for
    /// <c>--resume</c>) rather than from a live SDK event. Used by
    /// <see cref="CaptureSessionId"/> to escalate the log level when a seeded id is
    /// later replaced by a live one — that transition is a correctness signal for
    /// callers driving resume workflows.
    /// </summary>
    private bool _sessionIdWasSeeded;

    /// <summary>
    /// Records a Claude SDK session id. No-op for null/empty/duplicate values.
    /// Logging escalates with semantic importance:
    /// <list type="bullet">
    ///   <item><description>Information on first capture (whether from a live SDK event or a constructor seed).</description></item>
    ///   <item><description>Information when a live SDK event replaces another live value.</description></item>
    ///   <item><description>Warning when a live SDK event replaces a previously seeded value — surfaces resume mismatches.</description></item>
    /// </list>
    /// </summary>
    /// <param name="sessionId">The session id to record.</param>
    /// <param name="isSeed">
    /// True when the source is the constructor's <c>initialSessionId</c> parameter; false
    /// when the source is the live SDK (<see cref="SystemInitMessage"/> or
    /// <see cref="IClaudeAgentSdkClient.CurrentSession"/>).
    /// </param>
    private void CaptureSessionId(string? sessionId, bool isSeed = false)
    {
        if (string.IsNullOrEmpty(sessionId) || sessionId == CurrentSessionId)
        {
            return;
        }

        var previous = CurrentSessionId;
        var previousWasSeeded = _sessionIdWasSeeded;
        CurrentSessionId = sessionId;
        _sessionIdWasSeeded = isSeed;

        if (previous == null)
        {
            Logger.LogInformation(
                "Captured Claude SDK SessionId: {SessionId} (seeded: {Seeded})",
                sessionId,
                isSeed);
        }
        else if (previousWasSeeded && !isSeed)
        {
            // Live SDK overrode a caller-seeded id — important enough to bump
            // to Warning so callers driving --resume can audit the mismatch.
            Logger.LogWarning(
                "Claude SDK SessionId replaced caller-seeded value: {Seeded} -> {Live}",
                previous,
                sessionId);
        }
        else
        {
            Logger.LogInformation(
                "Claude SDK SessionId changed: {Old} -> {New}",
                previous,
                sessionId);
        }
    }

    /// <summary>
    /// Creates new client instance.
    /// </summary>
    private void CreateClientResources()
    {
        var clientLogger = _loggerFactory?.CreateLogger<ClaudeAgentSdkClient>();

        _client = _clientFactory != null
            ? _clientFactory(_claudeOptions, clientLogger)
            : new ClaudeAgentSdkClient(_claudeOptions, clientLogger);
    }

    /// <summary>
    /// Stop the current run AND the underlying claude-agent-sdk process.
    /// Can restart via SendAsync which will trigger RunAsync.
    /// </summary>
    /// <param name="ct">Cancellation token</param>
    public async Task StopProcessAsync(CancellationToken ct = default)
    {
        await _restartLock.WaitAsync(ct);
        try
        {
            Logger.LogInformation("Stopping process and run loop...");

            // First stop the run loop
            await StopAsync();

            // Then shutdown the client process gracefully
            if (_client != null)
            {
                await _client.ShutdownAsync(TimeSpan.FromSeconds(10), ct);
            }

            Logger.LogInformation("Process stopped, ready for restart via SendAsync");
        }
        finally
        {
            _restartLock.Release();
        }
    }

    /// <inheritdoc />
    public override async ValueTask<SendReceipt> SendAsync(
        List<IMessage> messages,
        string? inputId = null,
        string? parentRunId = null,
        CancellationToken ct = default)
    {
        await _restartLock.WaitAsync(ct);
        try
        {
            // Auto-restart only in Interactive mode when client died unexpectedly
            if (_claudeOptions.Mode == ClaudeAgentSdkMode.Interactive
                && _client is { IsRunning: false, LastRequest: not null })
            {
                Logger.LogInformation(
                    "Interactive mode: Client stopped unexpectedly, restarting process...");
                await _client.StartAsync(_client.LastRequest, ct);
            }

            // Start run loop if not running
            if (!IsRunning)
            {
                Logger.LogInformation("Run loop not active, starting...");
                _ = RunAsync(ct);
            }

            return await base.SendAsync(messages, inputId, parentRunId, ct);
        }
        finally
        {
            _restartLock.Release();
        }
    }

    /// <inheritdoc />
    protected override async Task RunLoopAsync(CancellationToken ct)
    {
        Logger.LogDebug("ClaudeAgentLoop run loop started");

        try
        {
            while (!ct.IsCancellationRequested)
            {
                // Before waiting for new input, send any queued messages from previous run
                // This ensures queued messages are fully processed before new input arrives
                await SendQueuedMessagesBeforeNewInputAsync(ct);

                // Wait for at least one input
                if (!await InputReader.WaitToReadAsync(ct))
                {
                    break; // Channel completed
                }

                // Drain all available inputs
                TryDrainInputs(out var batch);
                if (batch.Count == 0)
                {
                    continue;
                }

                // Start run and track for correlation with queue-operation events.
                // ParentRunId / WasForked is sourced from explicit caller fork signal
                // (UserInput.ParentRunId); absence falls back to _latestRunId continuation.
                var (batchParent, isExplicitFork) = ResolveBatchParent(batch);
                var assignment = StartRun(batch, batchParent);

                // Track each input for correlation with enqueue/dequeue events
                foreach (var input in batch)
                {
                    _pendingCliInputs.Enqueue((input, assignment));
                }

                // Note: RunAssignmentMessage will be published when dequeue is received
                // This ensures we only confirm assignment after CLI accepts the input

                // Collect messages from all inputs
                var messagesToSend = GetMessagesForClaudeSdk(batch).ToList();

                // Add messages to history
                foreach (var input in batch)
                {
                    foreach (var msg in input.Input.Messages)
                    {
                        AddToHistory(msg);
                    }
                }

                try
                {
                    if (_claudeOptions.Mode == ClaudeAgentSdkMode.Interactive)
                    {
                        await ExecuteInteractiveModeAsync(assignment, messagesToSend, ct);
                    }
                    else
                    {
                        await ExecuteOneShotModeAsync(assignment, messagesToSend, ct);
                    }
                }
                finally
                {
                    // Safety net: if the dequeue heuristic never fired (e.g., CLI exited
                    // without producing a recognizable signal, mock providers, or a
                    // partial protocol response), the RunAssignmentMessage was never
                    // published and consumers correlating completion to assignment via
                    // receipt id would hang. Flush any still-pending inputs now so the
                    // completion below can be matched.
                    await FlushPendingRunAssignmentsAsync(assignment.RunId, ct);

                    // Pass pending message count so workflows know if more runs will follow
                    await CompleteRunAsync(
                        assignment.RunId,
                        assignment.GenerationId,
                        wasForked: isExplicitFork,
                        forkedToRunId: isExplicitFork ? assignment.RunId : null,
                        pendingMessageCount: _localMessageQueue.Count,
                        ct: ct);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Logger.LogDebug("ClaudeAgentLoop run loop cancelled");
        }
        catch (ChannelClosedException)
        {
            Logger.LogDebug("Input channel closed");
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Unexpected error in run loop");
            throw;
        }
        finally
        {
            await OnAfterRunAsync();
        }
    }

    /// <summary>
    /// Execute in Interactive mode: concurrent input watching with push notification.
    /// </summary>
    private async Task ExecuteInteractiveModeAsync(
        RunAssignment assignment,
        List<IMessage> initialMessages,
        CancellationToken ct)
    {
        await EnsureClientStartedAsync(ct);

        // Get or create subscription
        _activeSubscription ??= _client!.SubscribeToMessagesAsync(ct).GetAsyncEnumerator(ct);

        // Send initial messages
        // IMPORTANT: Set _runInProgress BEFORE SendAsync to prevent race condition.
        // If we set it after, WatchAndInjectInputsAsync can check _runInProgress (still false)
        // while SendAsync is yielding, and send another message that gets removed from SDK queue.
        _runInProgress = true;
        _awaitingDequeue = true; // Now waiting for dequeue before sending more
        await _client!.SendAsync(initialMessages, ct);

        // Create linked cancellation for concurrent tasks
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // Start concurrent input watcher
        var inputWatchTask = WatchAndInjectInputsAsync(linkedCts.Token);

        try
        {
            _lastObservedGenerationId = assignment.GenerationId;

            // Read until ResultEvent
            while (await _activeSubscription.MoveNextAsync())
            {
                var msg = _activeSubscription.Current;

                // HEURISTIC 1: If we get SystemInit (RunStarted), previous enqueue likely processed or reset
                if (msg is SystemInitMessage systemInit)
                {
                    await OnDequeueDetectedAsync("SystemInitMessage", ct);
                    // Capture SessionId for --resume on later runs and for consumers
                    // reading CurrentSessionId. SystemInitMessage is SDK metadata —
                    // intentionally NOT published into the common message stream.
                    CaptureSessionId(systemInit.SessionId);
                    continue;
                }

                // Check for turn completion
                if (msg is ResultEventMessage resultEvent)
                {
                    // HEURISTIC 2: Run completion implies dequeue happened
                    Logger.LogDebug("Turn complete (ResultEvent). IsError: {IsError}", resultEvent.IsError);

                    // Reset run state on turn completion
                    if (_pendingToolCalls > 0)
                    {
                        Logger.LogDebug(
                            "Resetting pendingToolCalls from {Count} to 0 on turn complete",
                            _pendingToolCalls);
                        _pendingToolCalls = 0;
                    }

                    _runInProgress = false; // Run is complete - safe to send messages now
                    _awaitingDequeue = false; // Ready to send more
                    Logger.LogDebug("Run complete - runInProgress = false, awaitingDequeue = false");

                    await OnDequeueDetectedAsync("ResultEvent", ct);

                    // Note: Queued messages will be sent in RunLoopAsync before new input is processed
                    // This prevents rapid double-enqueue race condition

                    break;
                }

                // HEURISTIC 3: If generationId changes unexpectedly, assume dequeue happened
                if (!string.IsNullOrEmpty(msg.GenerationId) &&
                    msg.GenerationId != _lastObservedGenerationId)
                {
                    // Only trigger if we were actually waiting
                    if (_awaitingDequeue)
                    {
                        Logger.LogInformation(
                            "GenerationId changed from {Old} to {New} while awaiting dequeue.",
                            _lastObservedGenerationId,
                            msg.GenerationId);
                        await OnDequeueDetectedAsync("GenerationId change", ct);
                    }

                    _lastObservedGenerationId = msg.GenerationId;
                }

                // Track pending tool calls to know when safe to inject messages
                // This prevents message loss - SDK removes queued messages during tool execution
                if (msg is ToolCallMessage toolCall)
                {
                    _pendingToolCalls++;
                    Logger.LogDebug(
                        "Tool call started: {ToolName}, pending: {Count}",
                        toolCall.FunctionName,
                        _pendingToolCalls);
                }
                else if (msg is ToolCallResultMessage toolResult)
                {
                    _pendingToolCalls--;
                    if (_pendingToolCalls < 0)
                    {
                        _pendingToolCalls = 0; // Safety clamp
                    }

                    Logger.LogDebug(
                        "Tool call completed: {ToolId}, pending: {Count}",
                        toolResult.ToolCallId,
                        _pendingToolCalls);

                    // Note: We don't try to send queued messages here because _runInProgress
                    // is still true. Messages will be sent when the run completes (ResultEventMessage).
                }

                // Publish message to subscribers
                await PublishToAllAsync(msg, ct);

                // Add to conversation history
                AddToHistory(msg);
            }
        }
        finally
        {
            // Stop input watcher
            await linkedCts.CancelAsync();
        }
    }

    /// <summary>
    /// Watch for new inputs and inject them to CLI immediately (Interactive mode only).
    /// </summary>
    private async Task WatchAndInjectInputsAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await InputReader.WaitToReadAsync(ct);

                if (TryDrainInputs(out var newInputs) && newInputs.Count > 0)
                {
                    // Collect messages
                    var messagesToSend = GetMessagesForClaudeSdk(newInputs).ToList();

                    // Check if we should queue locally:
                    // _runInProgress covers the entire run (generation + tool calls) and only resets
                    // on ResultEventMessage. When true, SDK will REMOVE messages, so we queue locally.
                    //
                    // Note: _awaitingDequeue is no longer needed here because _runInProgress is set
                    // BEFORE SendAsync and only reset on ResultEventMessage, so it covers the entire
                    // period when messages would be removed.
                    if (_runInProgress)
                    {
                        // Queue locally - run is in progress (would get removed)
                        _localMessageQueue.Enqueue((messagesToSend, newInputs));
                        Logger.LogDebug(
                            "Queued {MessageCount} messages locally (run in progress, tools pending: {PendingTools}). InputCount: {InputCount}",
                            messagesToSend.Count,
                            _pendingToolCalls,
                            newInputs.Count);
                    }
                    else
                    {
                        // Safe to send directly
                        // Track inputs for dequeue correlation
                        var assignment = StartRun(newInputs);
                        foreach (var input in newInputs)
                        {
                            _pendingCliInputs.Enqueue((input, assignment));
                        }

                        // Add to history
                        foreach (var input in newInputs)
                        {
                            foreach (var msg in input.Input.Messages)
                            {
                                AddToHistory(msg);
                            }
                        }

                        // Set flags BEFORE SendAsync to prevent race condition
                        _runInProgress = true;
                        _awaitingDequeue = true;
                        await _client!.SendAsync(messagesToSend, ct);

                        Logger.LogInformation("Injected {Count} inputs to CLI mid-run", newInputs.Count);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Expected when run completes
        }
    }

    /// <summary>
    /// Send queued messages before processing new input.
    /// MERGES all queued batches into a SINGLE run to ensure all pending input IDs
    /// are included in one RunAssignmentMessage.
    /// </summary>
    private async Task SendQueuedMessagesBeforeNewInputAsync(CancellationToken ct)
    {
        // Check if there are any queued messages
        if (_localMessageQueue.Count == 0)
        {
            return;
        }

        // Merge ALL queued batches into a single batch
        var mergedMessages = new List<IMessage>();
        var mergedInputs = new List<QueuedInput>();

        while (_localMessageQueue.TryDequeue(out var nextBatch))
        {
            mergedMessages.AddRange(nextBatch.Messages);
            mergedInputs.AddRange(nextBatch.Inputs);
        }

        Logger.LogInformation(
            "Merged {BatchCount} queued batches into single run. Total messages: {MessageCount}, Total inputs: {InputCount}",
            mergedInputs.Count,
            mergedMessages.Count,
            mergedInputs.Count);

        // Start a SINGLE run for ALL merged inputs. Caller-supplied ParentRunId on
        // any merged input promotes this to an explicit fork.
        var (mergedParent, isExplicitFork) = ResolveBatchParent(mergedInputs);
        var assignment = StartRun(mergedInputs, mergedParent);

        // Track ALL inputs for dequeue correlation with the SAME assignment
        foreach (var input in mergedInputs)
        {
            _pendingCliInputs.Enqueue((input, assignment));
        }

        // Add ALL messages to history
        foreach (var input in mergedInputs)
        {
            foreach (var msg in input.Input.Messages)
            {
                AddToHistory(msg);
            }
        }

        try
        {
            // Execute the merged batch as a full interactive run (waits for ResultEvent)
            await ExecuteInteractiveModeAsync(assignment, mergedMessages, ct);
        }
        finally
        {
            // No pending messages after merge (we processed all of them)
            await CompleteRunAsync(
                assignment.RunId,
                assignment.GenerationId,
                wasForked: isExplicitFork,
                forkedToRunId: isExplicitFork ? assignment.RunId : null,
                pendingMessageCount: 0,
                ct: ct);
        }
    }

    /// <summary>
    /// Called when dequeue is detected via heuristics.
    /// Sets _awaitingDequeue = false and publishes RunAssignmentMessage.
    /// Note: Local queue is now drained in RunLoopAsync via SendQueuedMessagesBeforeNewInputAsync
    /// to prevent rapid double-enqueue race conditions.
    /// </summary>
    /// <param name="trigger">Description of what triggered this (for logging)</param>
    /// <param name="ct">Cancellation token</param>
    private async Task OnDequeueDetectedAsync(string trigger, CancellationToken ct)
    {
        // Early exit if not actually waiting
        if (!_awaitingDequeue)
        {
            return;
        }

        _awaitingDequeue = false;
        Logger.LogDebug("Dequeue detected via {Trigger} - resetting awaitingDequeue", trigger);

        // Publish RunAssignmentMessage for ALL pending inputs with the same assignment
        // (inputs in the same batch share the same assignment and are accepted together)
        RunAssignment? currentAssignment = null;
        while (_pendingCliInputs.TryPeek(out var peeked))
        {
            // If this is a different assignment, stop - it belongs to a different batch
            if (currentAssignment != null && peeked.Assignment.RunId != currentAssignment.RunId)
            {
                break;
            }

            // Dequeue and process this input
            if (!_pendingCliInputs.TryDequeue(out var pending))
            {
                break;
            }

            currentAssignment = pending.Assignment;

            var (firstText, lastText) = GetFirstLastTextMessages(pending.Input.Input.Messages);
            Logger.LogInformation(
                "Publishing RunAssignmentMessage - RunId: {RunId}, InputId: {InputId}, FirstText: {First}, LastText: {Last}",
                pending.Assignment.RunId,
                pending.Input.ReceiptId,
                firstText ?? "(none)",
                lastText ?? "(same as first)");

            await PublishToAllAsync(new RunAssignmentMessage
            {
                Assignment = pending.Assignment,
                ThreadId = ThreadId,
            }, ct);
        }

        // Note: Local queue draining is now handled in RunLoopAsync via SendQueuedMessagesBeforeNewInputAsync
        // This ensures each batch gets a full ExecuteInteractiveModeAsync run, preventing rapid double-enqueue
        if (_localMessageQueue.Count > 0)
        {
            Logger.LogDebug(
                "OnDequeueDetected: {QueueSize} messages in local queue, will be processed in RunLoopAsync",
                _localMessageQueue.Count);
        }
    }

    /// <summary>
    /// Drain any inputs whose RunAssignmentMessage hasn't yet been published for
    /// <paramref name="runId"/> and publish them before run completion. This is a
    /// safety net for cases where the dequeue heuristic in
    /// <see cref="OnDequeueDetectedAsync"/> never fired — without it, consumers
    /// correlating <see cref="RunCompletedMessage"/> to <see cref="RunAssignmentMessage"/>
    /// would never see an assignment for this run.
    /// </summary>
    internal async Task FlushPendingRunAssignmentsAsync(string runId, CancellationToken ct)
    {
        // Only drain inputs belonging to this run; later runs' inputs stay queued.
        while (_pendingCliInputs.TryPeek(out var peeked) &&
               string.Equals(peeked.Assignment.RunId, runId, StringComparison.Ordinal))
        {
            if (!_pendingCliInputs.TryDequeue(out var pending))
            {
                break;
            }

            Logger.LogWarning(
                "Publishing deferred RunAssignmentMessage at run completion (dequeue heuristic missed) - RunId: {RunId}, ReceiptId: {ReceiptId}",
                pending.Assignment.RunId,
                pending.Input.ReceiptId);

            await PublishToAllAsync(new RunAssignmentMessage
            {
                Assignment = pending.Assignment,
                ThreadId = ThreadId,
            }, ct);
        }

        // We've published whatever we owed; clear awaiting flag so subsequent runs
        // start clean.
        _awaitingDequeue = false;
    }

    /// <summary>
    /// Extract first and last text messages from a collection for logging.
    /// </summary>
    private static (string? First, string? Last) GetFirstLastTextMessages(
        IEnumerable<IMessage> messages,
        int maxLength = 100)
    {
        var textMessages = messages.OfType<TextMessage>().ToList();
        if (textMessages.Count == 0)
        {
            return (null, null);
        }

        var first = TruncateForLog(textMessages[0].Text, maxLength);
        var last = textMessages.Count > 1
            ? TruncateForLog(textMessages[^1].Text, maxLength)
            : null;
        return (first, last);
    }

    /// <summary>
    /// Truncate a string for logging, adding ellipsis if truncated.
    /// </summary>
    private static string TruncateForLog(string? text, int maxLength)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "(empty)";
        }

        // Collapse all whitespace and newlines to single spaces for preview
        var preview = text.Replace("\r\n", " ").Replace("\n", " ").Replace("\r", " ").Trim();

        return string.IsNullOrEmpty(preview)
            ? "(whitespace only)"
            : preview.Length <= maxLength ? preview : preview[..maxLength] + "...";
    }

    /// <summary>
    /// Execute in OneShot mode: no mid-run injection, inputs queue for next run.
    /// </summary>
    private async Task ExecuteOneShotModeAsync(
        RunAssignment assignment,
        List<IMessage> messagesToSend,
        CancellationToken ct)
    {
        await EnsureClientStartedAsync(ct);

        // Send all messages - process completes when done
        var stream = _client!.SendMessagesAsync(messagesToSend, ct);

        await foreach (var msg in stream.WithCancellation(ct))
        {
            // Publish message to subscribers
            await PublishToAllAsync(msg, ct);

            // Add to conversation history
            AddToHistory(msg);

            // OneShot's SendMessagesAsync intentionally suppresses SystemInitMessage,
            // but the underlying client still updates CurrentSession.SessionId when
            // it parses the system init event. Poll it so CurrentSessionId becomes
            // available without leaking SDK metadata into the public message stream.
            CaptureSessionId(_client?.CurrentSession?.SessionId);
        }

        // OneShot: any inputs that arrived during run stay in queue for next iteration
    }

    /// <summary>
    /// Ensures the client is started, creating it if needed.
    /// </summary>
    private async Task EnsureClientStartedAsync(CancellationToken ct)
    {
        if (_client == null)
        {
            throw new InvalidOperationException("Client not initialized");
        }

        if (!_client.IsRunning)
        {
            // Preserve session ID from previous run (for session continuity)
            CaptureSessionId(_client.CurrentSession?.SessionId);

            var request = BuildClaudeAgentSdkRequest();
            Logger.LogInformation(
                "Starting claude-agent-sdk client with model {Model}, maxTurns {MaxTurns}, sessionId {SessionId}",
                request.ModelId,
                request.MaxTurns,
                request.SessionId ?? "(new session)");

            await _client.StartAsync(request, ct);
        }
    }

    /// <summary>
    /// Gets messages to send to Claude SDK CLI from queued inputs.
    /// Claude SDK CLI maintains its own conversation history internally.
    /// </summary>
    private IEnumerable<IMessage> GetMessagesForClaudeSdk(IReadOnlyList<QueuedInput> inputs)
    {
        // System prompt goes first (if configured)
        if (!string.IsNullOrEmpty(SystemPrompt))
        {
            yield return new TextMessage { Text = SystemPrompt, Role = Role.System };
        }

        // Only send new messages from inputs (not full history)
        foreach (var input in inputs)
        {
            foreach (var msg in input.Input.Messages)
            {
                yield return msg;
            }
        }
    }

    /// <summary>
    /// Build ClaudeAgentSdkRequest from current configuration and options.
    /// Moved from ClaudeAgentSdkAgent.
    /// </summary>
    internal ClaudeAgentSdkRequest BuildClaudeAgentSdkRequest()
    {
        var modelId = DefaultOptions.ModelId ?? "claude-sonnet-4-5-20250929";
        var maxTurns = _claudeOptions.MaxTurnsPerRun;

        // Max thinking tokens from options
        var maxThinkingTokens = _claudeOptions.MaxThinkingTokens;

        // Extract session ID: use preserved from previous run if available.
        // A non-null value here may originate from a live SDK event OR from the
        // caller-supplied initialSessionId constructor parameter (see _sessionIdWasSeeded).
        var sessionId = CurrentSessionId;

        if (_sessionIdWasSeeded
            && !string.IsNullOrEmpty(sessionId)
            && _claudeOptions.DisableSessionPersistence)
        {
            // The CLI is unlikely to find the seeded session on disk when the
            // SDK is told to skip session persistence. We preserve the value
            // because the caller may know better (e.g. manages persistence
            // externally), but surface the conflict.
            Logger.LogWarning(
                "Seeded Claude SDK SessionId {SessionId} is being emitted to --resume even though DisableSessionPersistence=true; the CLI may reject the resume if the session is not persisted on disk",
                sessionId);
        }

        // Build MCP server configuration
        Dictionary<string, McpServerConfig>? mcpServers = null;

        // First, try to load from file
        if (!string.IsNullOrEmpty(_claudeOptions.McpConfigPath) && File.Exists(_claudeOptions.McpConfigPath))
        {
            try
            {
                var json = File.ReadAllText(_claudeOptions.McpConfigPath);
                var mcpConfig = JsonSerializer.Deserialize<McpConfiguration>(json);
                mcpServers = mcpConfig?.McpServers;
                Logger.LogDebug(
                    "Loaded {Count} MCP servers from file: {Path}",
                    mcpServers?.Count ?? 0,
                    _claudeOptions.McpConfigPath);
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Failed to load MCP configuration from {Path}", _claudeOptions.McpConfigPath);
            }
        }

        // Use provided MCP servers (take precedence over file)
        if (_mcpServers.Count > 0)
        {
            if (mcpServers == null)
            {
                mcpServers = _mcpServers;
            }
            else
            {
                // Merge: provided servers override file-based ones
                foreach (var (name, config) in _mcpServers)
                {
                    mcpServers[name] = config;
                }
            }
        }

        // Profile-supplied MCP servers win on key collision (documented in
        // ClaudeAgentSdkOptions.Profile XML doc).
        if (_materializedProfile.McpServers.Count > 0)
        {
            mcpServers ??= [];
            foreach (var (name, config) in _materializedProfile.McpServers)
            {
                mcpServers[name] = config;
            }
        }

        Logger.LogInformation(
            "Final MCP server configuration: {Count} servers configured",
            mcpServers?.Count ?? 0);

        // Build tools list (controls which built-in tools are available via --tools flag)
        var allowedTools = _claudeOptions.AllowedTools
            ?? "Read,Write,Edit,Bash,Grep,Glob,TodoWrite,Task,WebSearch,WebFetch";

        return new ClaudeAgentSdkRequest
        {
            ModelId = modelId,
            MaxTurns = maxTurns,
            MaxThinkingTokens = maxThinkingTokens,
            SessionId = sessionId,
            SystemPrompt = _materializedProfile.SystemPrompt ?? SystemPrompt,
            AllowedTools = allowedTools,
            McpServers = mcpServers,
            Verbose = true,
            ReasoningEffort = _claudeOptions.ReasoningEffort,
            SettingSources = BuildSettingSources(_claudeOptions),
            StagingDirectory = _materializedProfile.StagingDirectory,
        };
    }

    // Maps the IncludeUser/Project/LocalSettings booleans to the CLI's
    // --setting-sources value. Tri-state semantics:
    //   all true  => null  (omit flag, CLI uses its own default of user,project,local)
    //   all false => ""    (emit empty value, CLI loads nothing -> isolated agent)
    //   mixed     => comma-joined list of the enabled sources
    internal static string? BuildSettingSources(ClaudeAgentSdkOptions options)
    {
        var user = options.IncludeUserSettings;
        var project = options.IncludeProjectSettings;
        var local = options.IncludeLocalSettings;

        if (user && project && local)
        {
            return null;
        }

        if (!user && !project && !local)
        {
            return string.Empty;
        }

        var parts = new List<string>(3);
        if (user)
        {
            parts.Add("user");
        }
        if (project)
        {
            parts.Add("project");
        }
        if (local)
        {
            parts.Add("local");
        }
        return string.Join(',', parts);
    }
}
