using System.Text.Json;
using System.Threading.Channels;
using AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Agents;
using AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Configuration;
using AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Models;
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
    private readonly Func<ClaudeAgentSdkOptions, ILogger?, ClaudeAgentSdkClient>? _clientFactory;
    private readonly SemaphoreSlim _restartLock = new(1, 1);

    private IClaudeAgentSdkClient? _client;

    /// <summary>
    /// Tracks the session ID across process restarts (for OneShot mode session continuity).
    /// </summary>
    private string? _lastSessionId;

    /// <summary>
    /// Tracks inputs sent to CLI, awaiting enqueue/dequeue confirmation.
    /// Used for correlating queue-operation events with original user inputs.
    /// </summary>
    private readonly Queue<(QueuedInput Input, RunAssignment Assignment)> _pendingCliInputs = new();

    /// <summary>
    /// The active subscription to stdout messages (Interactive mode).
    /// </summary>
    private IAsyncEnumerator<IMessage>? _activeSubscription;

    /// <summary>
    /// Track if we're waiting for a dequeue (previous send not yet assigned).
    /// When true, new messages should be queued locally instead of sent to CLI.
    /// </summary>
    private bool _awaitingDequeue;

    /// <summary>
    /// Local queue for messages that arrive while we're waiting for dequeue.
    /// Stores both messages AND their source inputs for correlation with RunAssignment.
    /// </summary>
    private readonly Queue<(List<IMessage> Messages, List<QueuedInput> Inputs)> _localMessageQueue = new();

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
        Func<ClaudeAgentSdkOptions, ILogger?, ClaudeAgentSdkClient>? clientFactory = null)
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

        (_client as IDisposable)?.Dispose();
        _client = null;
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

                // Start run and track for correlation with queue-operation events
                var assignment = StartRun(batch);

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
                    await CompleteRunAsync(assignment.RunId, assignment.GenerationId, false, null, ct);
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
        await _client!.SendAsync(initialMessages, ct);
        _awaitingDequeue = true; // Now waiting for dequeue before sending more

        // Create linked cancellation for concurrent tasks
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // Start concurrent input watcher
        var inputWatchTask = WatchAndInjectInputsAsync(linkedCts.Token);

        try
        {
            // Read until ResultEvent
            while (await _activeSubscription.MoveNextAsync())
            {
                var msg = _activeSubscription.Current;

                // Check for turn completion
                if (msg is ResultEventMessage resultEvent)
                {
                    Logger.LogDebug("Turn complete. IsError: {IsError}", resultEvent.IsError);
                    break;
                }

                // Handle queue operation events for input correlation
                if (msg is QueueOperationMessage queueOp)
                {
                    await HandleQueueOperationAsync(queueOp, ct);
                    continue; // Don't publish QueueOperationMessage to subscribers
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

                    if (_awaitingDequeue)
                    {
                        // Queue locally instead of sending to CLI - will be sent after current batch is dequeued
                        _localMessageQueue.Enqueue((messagesToSend, newInputs));
                        Logger.LogDebug(
                            "Queued {MessageCount} messages locally (awaiting previous dequeue). InputCount: {InputCount}",
                            messagesToSend.Count,
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

                        await _client!.SendAsync(messagesToSend, ct);
                        _awaitingDequeue = true;

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
    /// Handle queue operation events for input correlation.
    /// - enqueue: CLI received the user input
    /// - dequeue: CLI accepted the input for processing, publish RunAssignmentMessage
    /// - remove: CLI removed the input because a new input arrived before dequeue
    /// </summary>
    private async Task HandleQueueOperationAsync(QueueOperationMessage queueOp, CancellationToken ct)
    {
        if (queueOp.Operation == "enqueue")
        {
            // CLI received our input - log for tracking
            var (enqFirstText, enqLastText) = queueOp.ContentMessages != null
                ? GetFirstLastTextMessages(queueOp.ContentMessages)
                : (null, null);
            Logger.LogDebug(
                "Queue enqueue received - SessionId: {SessionId}, Timestamp: {Timestamp}, ContentCount: {ContentCount}, FirstText: {First}, LastText: {Last}",
                queueOp.SessionId,
                queueOp.Timestamp,
                queueOp.ContentMessages?.Count ?? 0,
                enqFirstText ?? "(none)",
                enqLastText ?? "(same as first)");
        }
        else if (queueOp.Operation == "dequeue")
        {
            // CLI accepted input for processing - no longer awaiting dequeue
            _awaitingDequeue = false;

            Logger.LogDebug(
                "Queue dequeue received - SessionId: {SessionId}, Timestamp: {Timestamp}",
                queueOp.SessionId,
                queueOp.Timestamp);

            if (_pendingCliInputs.TryDequeue(out var pending))
            {
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
            else
            {
                Logger.LogWarning("Received dequeue but no pending inputs to correlate");
            }

            // Check if we have queued messages to send now
            if (_localMessageQueue.TryDequeue(out var nextBatch))
            {
                Logger.LogInformation(
                    "Sending queued messages after dequeue. MessageCount: {MessageCount}, InputCount: {InputCount}",
                    nextBatch.Messages.Count,
                    nextBatch.Inputs.Count);

                // Track inputs for dequeue correlation
                var assignment = StartRun(nextBatch.Inputs);
                foreach (var input in nextBatch.Inputs)
                {
                    _pendingCliInputs.Enqueue((input, assignment));
                }

                // Add to history
                foreach (var input in nextBatch.Inputs)
                {
                    foreach (var msg in input.Input.Messages)
                    {
                        AddToHistory(msg);
                    }
                }

                await _client!.SendAsync(nextBatch.Messages, ct);
                _awaitingDequeue = true;
            }
        }
        else if (queueOp.Operation == "remove")
        {
            // CLI removed input because a new input arrived before dequeue
            // This is an error condition - we should have waited for dequeue before sending more
            Logger.LogError(
                "Queue remove received - SessionId: {SessionId}. Messages were removed before dequeue. ContentCount: {Count}",
                queueOp.SessionId,
                queueOp.ContentMessages?.Count ?? 0);

            // Match removed content with pending inputs and re-queue them
            if (queueOp.ContentMessages != null && queueOp.ContentMessages.Count > 0)
            {
                var matchedInput = FindPendingInputByContent(queueOp.ContentMessages);
                if (matchedInput != null)
                {
                    // Re-queue to send after current batch is dequeued
                    _localMessageQueue.Enqueue((queueOp.ContentMessages, matchedInput.Value.Inputs));
                    Logger.LogInformation(
                        "Re-queued {Count} removed messages for later send",
                        queueOp.ContentMessages.Count);
                }
                else
                {
                    Logger.LogError("Could not match removed messages with any pending input");
                }
            }
        }
        else
        {
            Logger.LogWarning("Unknown queue operation: {Operation}", queueOp.Operation);
        }
    }

    /// <summary>
    /// Find pending input that matches the removed content by comparing message content.
    /// </summary>
    private (List<IMessage> Messages, List<QueuedInput> Inputs)? FindPendingInputByContent(List<IMessage> removedContent)
    {
        // Look through pending inputs to find one that matches
        var pendingList = _pendingCliInputs.ToList();
        foreach (var pending in pendingList)
        {
            var pendingMessages = pending.Input.Input.Messages;
            if (MessagesMatchByContent(pendingMessages, removedContent))
            {
                return (pendingMessages.ToList(), [pending.Input]);
            }
        }

        return null;
    }

    /// <summary>
    /// Compare two message collections by their content.
    /// </summary>
    private static bool MessagesMatchByContent(IEnumerable<IMessage> a, IEnumerable<IMessage> b)
    {
        var aList = a.ToList();
        var bList = b.ToList();

        if (aList.Count != bList.Count)
        {
            return false;
        }

        for (int i = 0; i < aList.Count; i++)
        {
            if (aList[i] is TextMessage ta && bList[i] is TextMessage tb)
            {
                if (ta.Text != tb.Text)
                {
                    return false;
                }
            }
            else if (aList[i] is ImageMessage ia && bList[i] is ImageMessage ib)
            {
                // Compare image messages by comparing ImageData bytes
                if (!ia.ImageData.ToMemory().Span.SequenceEqual(ib.ImageData.ToMemory().Span))
                {
                    return false;
                }
            }
            // Different types don't match
            else if (aList[i].GetType() != bList[i].GetType())
            {
                return false;
            }
        }

        return true;
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
            // Handle queue operation events for input correlation
            if (msg is QueueOperationMessage queueOp)
            {
                await HandleQueueOperationAsync(queueOp, ct);
                continue; // Don't publish QueueOperationMessage to subscribers
            }

            // Publish message to subscribers
            await PublishToAllAsync(msg, ct);

            // Add to conversation history
            AddToHistory(msg);
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
            if (_client.CurrentSession?.SessionId != null)
            {
                _lastSessionId = _client.CurrentSession.SessionId;
                Logger.LogDebug("Preserved sessionId from previous run: {SessionId}", _lastSessionId);
            }

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
    private ClaudeAgentSdkRequest BuildClaudeAgentSdkRequest()
    {
        var modelId = DefaultOptions.ModelId ?? "claude-sonnet-4-5-20250929";
        var maxTurns = _claudeOptions.MaxTurnsPerRun;

        // Max thinking tokens from options
        var maxThinkingTokens = _claudeOptions.MaxThinkingTokens;

        // Extract session ID: use preserved from previous run if available
        var sessionId = _lastSessionId;

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

        Logger.LogInformation(
            "Final MCP server configuration: {Count} servers configured",
            mcpServers?.Count ?? 0);

        // Build allowed tools list
        var allowedTools = "Read,Write,Edit,Bash,Grep,Glob,TodoWrite,Task,WebSearch,WebFetch";

        return new ClaudeAgentSdkRequest
        {
            ModelId = modelId,
            MaxTurns = maxTurns,
            MaxThinkingTokens = maxThinkingTokens,
            SessionId = sessionId,
            SystemPrompt = SystemPrompt,
            AllowedTools = allowedTools,
            McpServers = mcpServers,
            Verbose = true,
        };
    }
}
