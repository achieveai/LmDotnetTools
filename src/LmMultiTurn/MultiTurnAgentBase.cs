using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AchieveAi.LmDotnetTools.LmMultiTurn;

/// <summary>
/// Abstract base class for multi-turn agents providing common infrastructure for
/// channel management, subscription handling, and lifecycle management.
/// </summary>
public abstract class MultiTurnAgentBase : IMultiTurnAgent
{
    #region Fields

    private readonly int _outputChannelCapacity;
    private readonly int _inputChannelCapacity;

    // Channels - _inputChannel is recreatable to support restart
    private Channel<QueuedInput> _inputChannel;
    private readonly object _channelLock = new();
    private readonly ConcurrentDictionary<string, Channel<IMessage>> _outputSubscribers = new();

    // State
    private string? _currentRunId;
    private string? _latestRunId;
    private readonly object _stateLock = new();
    private readonly object _historyLock = new();

    // Lifecycle
    private Task? _runTask;
    private CancellationTokenSource? _internalCts;
    private volatile bool _isDisposed;

    #endregion

    #region Protected Properties

    /// <summary>
    /// Logger for this agent instance.
    /// </summary>
    protected ILogger Logger { get; }

    /// <summary>
    /// The system prompt for the agent.
    /// </summary>
    protected string? SystemPrompt { get; }

    /// <summary>
    /// The maximum turns per run.
    /// </summary>
    protected int MaxTurnsPerRun { get; }

    /// <summary>
    /// The default options for generating replies.
    /// </summary>
    protected GenerateReplyOptions DefaultOptions { get; }

    /// <summary>
    /// The conversation history. Access via AddToHistory and GetHistorySnapshot for thread safety.
    /// </summary>
    private List<IMessage> ConversationHistory { get; } = [];

    /// <summary>
    /// Pending injections queue.
    /// </summary>
    protected ConcurrentQueue<(UserInput Input, RunAssignment Assignment)> PendingInjections { get; } = new();

    /// <summary>
    /// The messages from the current input being processed.
    /// Available during ExecuteAgenticLoopAsync execution.
    /// Returns empty list when not processing an input.
    /// </summary>
    protected IReadOnlyList<IMessage> CurrentInputMessages { get; private set; } = [];

    /// <summary>
    /// Optional persistence store for conversation state.
    /// </summary>
    protected IConversationStore? Store { get; }

    #endregion

    #region Public Properties

    /// <inheritdoc />
    public string? CurrentRunId
    {
        get
        {
            lock (_stateLock)
            {
                return _currentRunId;
            }
        }
    }

    /// <inheritdoc />
    public string ThreadId { get; }

    /// <inheritdoc />
    public bool IsRunning => _runTask != null && !_runTask.IsCompleted;

    #endregion

    #region Constructor

    /// <summary>
    /// Creates a new MultiTurnAgentBase.
    /// </summary>
    /// <param name="threadId">Unique identifier for this conversation thread</param>
    /// <param name="systemPrompt">System prompt for the agent (persists across all runs)</param>
    /// <param name="defaultOptions">Default GenerateReplyOptions template</param>
    /// <param name="maxTurnsPerRun">Maximum turns per run (default: 50)</param>
    /// <param name="inputChannelCapacity">Capacity of the input queue (default: 100)</param>
    /// <param name="outputChannelCapacity">Capacity per subscriber output channel (default: 1000)</param>
    /// <param name="store">Optional persistence store for conversation state</param>
    /// <param name="logger">Optional logger</param>
    protected MultiTurnAgentBase(
        string threadId,
        string? systemPrompt = null,
        GenerateReplyOptions? defaultOptions = null,
        int maxTurnsPerRun = 50,
        int inputChannelCapacity = 100,
        int outputChannelCapacity = 1000,
        IConversationStore? store = null,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(threadId);

        ThreadId = threadId;
        SystemPrompt = systemPrompt;
        MaxTurnsPerRun = maxTurnsPerRun;
        _inputChannelCapacity = inputChannelCapacity;
        _outputChannelCapacity = outputChannelCapacity;
        DefaultOptions = defaultOptions ?? new GenerateReplyOptions();
        Store = store;
        Logger = logger ?? NullLogger.Instance;

        // Create initial channel
        _inputChannel = CreateInputChannel();
    }

    /// <summary>
    /// Creates a new input channel with the configured capacity.
    /// </summary>
    private Channel<QueuedInput> CreateInputChannel()
    {
        return Channel.CreateBounded<QueuedInput>(
            new BoundedChannelOptions(_inputChannelCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false,
            });
    }

    /// <summary>
    /// Ensures the input channel exists and is usable. Recreates if completed/closed.
    /// </summary>
    private void EnsureChannelExists()
    {
        lock (_channelLock)
        {
            if (_inputChannel.Reader.Completion.IsCompleted)
            {
                Logger.LogDebug("Recreating input channel (previous was completed)");
                _inputChannel = CreateInputChannel();
            }
        }
    }

    #endregion

    #region Conversation History Thread-Safe Access

    /// <summary>
    /// Adds a message to the conversation history in a thread-safe manner.
    /// If a persistence store is configured, the message is also persisted (fire-and-forget).
    /// </summary>
    /// <param name="message">The message to add</param>
    protected void AddToHistory(IMessage message)
    {
        lock (_historyLock)
        {
            ConversationHistory.Add(message);
        }

        // Fire-and-forget persistence
        if (Store != null)
        {
            string? runId;
            lock (_stateLock)
            {
                runId = _currentRunId;
            }

            if (runId != null)
            {
                _ = PersistMessageAsync(message, runId, CancellationToken.None);
            }
        }
    }

    /// <summary>
    /// Gets a snapshot of the conversation history in a thread-safe manner.
    /// </summary>
    /// <returns>A read-only list containing the current conversation history</returns>
    protected IReadOnlyList<IMessage> GetHistorySnapshot()
    {
        lock (_historyLock)
        {
            return [.. ConversationHistory];
        }
    }

    /// <summary>
    /// Gets messages with the system prompt prepended if configured.
    /// This is a helper to avoid code duplication across implementations.
    /// </summary>
    /// <returns>Messages ready to send to the LLM</returns>
    protected IEnumerable<IMessage> GetMessagesWithSystemPrompt()
    {
        var history = GetHistorySnapshot();

        if (!string.IsNullOrEmpty(SystemPrompt))
        {
            var systemMessage = new TextMessage { Text = SystemPrompt, Role = Role.System };
            return new IMessage[] { systemMessage }.Concat(history);
        }

        return history;
    }

    /// <summary>
    /// Restores conversation history from the store by appending loaded messages.
    /// </summary>
    protected void RestoreHistory(IReadOnlyList<IMessage> messages)
    {
        lock (_historyLock)
        {
            ConversationHistory.AddRange(messages);
        }
    }

    #endregion

    #region Persistence

    /// <summary>
    /// Persists a message to the store. Called by AddToHistory when a store is configured.
    /// Override to customize persistence behavior.
    /// </summary>
    /// <param name="message">The message to persist</param>
    /// <param name="runId">The current run ID</param>
    /// <param name="ct">Cancellation token</param>
    protected virtual async Task PersistMessageAsync(IMessage message, string runId, CancellationToken ct)
    {
        if (Store == null)
        {
            return;
        }

        try
        {
            var persisted = MessagePersistenceConverter.ToPersistedMessage(message, ThreadId, runId);
            await Store.AppendMessagesAsync(ThreadId, [persisted], ct);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to persist message");
        }
    }

    /// <summary>
    /// Updates thread metadata in the store. Called after each run completes.
    /// Override to include additional metadata (e.g., session mappings).
    /// </summary>
    /// <param name="ct">Cancellation token</param>
    protected virtual async Task UpdateMetadataAsync(CancellationToken ct)
    {
        if (Store == null)
        {
            return;
        }

        try
        {
            string? latestRun;
            lock (_stateLock)
            {
                latestRun = _latestRunId;
            }

            // Load existing metadata to preserve Properties and SessionMappings
            var existing = await Store.LoadMetadataAsync(ThreadId, ct);

            var metadata = new ThreadMetadata
            {
                ThreadId = ThreadId,
                CurrentRunId = null, // Only save when run is complete
                LatestRunId = latestRun,
                LastUpdated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Properties = existing?.Properties,
                SessionMappings = existing?.SessionMappings,
            };

            await Store.SaveMetadataAsync(ThreadId, metadata, ct);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to update thread metadata");
        }
    }

    /// <summary>
    /// Recovers conversation state from the persistence store.
    /// Call this before starting the agent to restore previous conversation.
    /// </summary>
    /// <param name="ct">Cancellation token</param>
    /// <returns>True if state was recovered, false if no stored state exists</returns>
    public virtual async Task<bool> RecoverAsync(CancellationToken ct = default)
    {
        if (Store == null)
        {
            throw new InvalidOperationException("No persistence store configured");
        }

        // Load metadata first
        var metadata = await Store.LoadMetadataAsync(ThreadId, ct);
        if (metadata == null)
        {
            Logger.LogDebug("No stored metadata found for thread {ThreadId}", ThreadId);
            return false;
        }

        // Load messages
        var persistedMessages = await Store.LoadMessagesAsync(ThreadId, ct);
        if (persistedMessages.Count == 0)
        {
            Logger.LogDebug("No stored messages found for thread {ThreadId}", ThreadId);
            return false;
        }

        // Convert persisted messages back to IMessages
        var messages = MessagePersistenceConverter.FromPersistedMessages(persistedMessages);

        // Restore history
        RestoreHistory(messages);

        // Restore state
        lock (_stateLock)
        {
            _latestRunId = metadata.LatestRunId;
        }

        Logger.LogInformation(
            "Recovered {MessageCount} messages for thread {ThreadId}. LatestRunId: {LatestRunId}",
            messages.Count,
            ThreadId,
            metadata.LatestRunId);

        return true;
    }

    #endregion

    #region Input API

    /// <summary>
    /// Direct access to the input channel reader for implementations that need push-based notification.
    /// </summary>
    protected ChannelReader<QueuedInput> InputReader => _inputChannel.Reader;

    /// <summary>
    /// Convenience method to drain all currently available inputs from the queue.
    /// Non-blocking - returns immediately with whatever is currently available.
    /// </summary>
    /// <param name="inputs">The drained inputs</param>
    /// <returns>True if any inputs were drained, false if queue was empty</returns>
    protected bool TryDrainInputs(out List<QueuedInput> inputs)
    {
        inputs = [];
        while (_inputChannel.Reader.TryRead(out var item))
        {
            inputs.Add(item);
        }

        if (inputs.Count > 1)
        {
            Logger.LogInformation("Drained {Count} inputs from queue", inputs.Count);
        }

        return inputs.Count > 0;
    }

    /// <inheritdoc />
    public virtual ValueTask<SendReceipt> SendAsync(
        List<IMessage> messages,
        string? inputId = null,
        string? parentRunId = null,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        var receiptId = inputId ?? Guid.NewGuid().ToString("N");
        var queuedAt = DateTimeOffset.UtcNow;
        var input = new UserInput(messages, inputId, parentRunId);
        var queued = new QueuedInput(input, receiptId, queuedAt);

        // Fire-and-forget write to channel (non-blocking if not full)
        if (!_inputChannel.Writer.TryWrite(queued))
        {
            // Channel is full - this shouldn't happen often with Wait mode
            // but we use TryWrite to avoid blocking the caller
            Logger.LogWarning("Input channel full, message queued with backpressure");
            return new ValueTask<SendReceipt>(WriteWithBackpressureAsync());

            async Task<SendReceipt> WriteWithBackpressureAsync()
            {
                await _inputChannel.Writer.WriteAsync(queued, ct);
                return new SendReceipt(receiptId, inputId, queuedAt);
            }
        }

        Logger.LogDebug("Message queued. ReceiptId: {ReceiptId}, InputId: {InputId}", receiptId, inputId);

        return ValueTask.FromResult(new SendReceipt(receiptId, inputId, queuedAt));
    }

    /// <inheritdoc />
    public virtual async IAsyncEnumerable<IMessage> ExecuteRunAsync(
        UserInput userInput,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(userInput);
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        // Subscribe first to ensure we don't miss any messages
        var subscriberId = Guid.NewGuid().ToString("N");
        var outputChannel = Channel.CreateBounded<IMessage>(new BoundedChannelOptions(_outputChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true,
        });

        if (!_outputSubscribers.TryAdd(subscriberId, outputChannel))
        {
            throw new InvalidOperationException("Failed to create subscriber for ExecuteRun");
        }

        try
        {
            // Send the input and get receipt (non-blocking)
            var receipt = await SendAsync(userInput.Messages, userInput.InputId, userInput.ParentRunId, ct);
            var receiptId = receipt.ReceiptId;

            Logger.LogDebug("ExecuteRun queued. ReceiptId: {ReceiptId}", receiptId);

            string? targetRunId = null;

            // Yield messages until run completes
            await foreach (var msg in outputChannel.Reader.ReadAllAsync(ct))
            {
                yield return msg;

                // Track run assignment for our receipt
                if (msg is RunAssignmentMessage assignment &&
                    assignment.Assignment.InputIds?.Contains(receiptId) == true)
                {
                    targetRunId = assignment.Assignment.RunId;
                    Logger.LogDebug("ExecuteRun assigned to RunId: {RunId}", targetRunId);
                }

                // Check for run completion
                if (msg is RunCompletedMessage completed &&
                    completed.CompletedRunId == targetRunId)
                {
                    Logger.LogDebug("ExecuteRun completed for RunId: {RunId}", targetRunId);
                    yield break;
                }
            }
        }
        finally
        {
            // Clean up subscriber
            if (_outputSubscribers.TryRemove(subscriberId, out var channel))
            {
                _ = channel.Writer.TryComplete();
            }
        }
    }

    #endregion

    #region Output API

    /// <inheritdoc />
    public async IAsyncEnumerable<IMessage> SubscribeAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var subscriberId = Guid.NewGuid().ToString("N");
        var channel = Channel.CreateBounded<IMessage>(new BoundedChannelOptions(_outputChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });

        _outputSubscribers[subscriberId] = channel;
        Logger.LogDebug("Subscriber {SubscriberId} connected", subscriberId);

        try
        {
            await foreach (var message in channel.Reader.ReadAllAsync(ct))
            {
                yield return message;
            }
        }
        finally
        {
            // Cleanup on unsubscribe
            if (_outputSubscribers.TryRemove(subscriberId, out var removed))
            {
                _ = removed.Writer.TryComplete();
            }

            Logger.LogDebug("Subscriber {SubscriberId} disconnected", subscriberId);
        }
    }

    /// <summary>
    /// Publish a message to all subscribers.
    /// </summary>
    /// <param name="message">The message to publish</param>
    /// <param name="ct">Cancellation token</param>
    protected async ValueTask PublishToAllAsync(IMessage message, CancellationToken ct)
    {
        var tasks = new List<Task>();

        foreach (var (subscriberId, channel) in _outputSubscribers)
        {
            tasks.Add(PublishToSubscriberAsync(subscriberId, channel, message, ct));
        }

        await Task.WhenAll(tasks);
    }

    private async Task PublishToSubscriberAsync(
        string subscriberId,
        Channel<IMessage> channel,
        IMessage message,
        CancellationToken ct)
    {
        try
        {
            await channel.Writer.WriteAsync(message, ct);
        }
        catch (ChannelClosedException)
        {
            Logger.LogDebug("Channel for subscriber {SubscriberId} is closed", subscriberId);
        }
        catch (OperationCanceledException)
        {
            // Ignore cancellation
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Error publishing to subscriber {SubscriberId}", subscriberId);
        }
    }

    #endregion

    #region Lifecycle API

    /// <inheritdoc />
    public async Task RunAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        if (_runTask != null && !_runTask.IsCompleted)
        {
            throw new InvalidOperationException("Loop is already running");
        }

        // Ensure channel exists (recreate if it was completed by previous stop)
        EnsureChannelExists();

        await OnBeforeRunAsync();

        _internalCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _runTask = RunLoopAsync(_internalCts.Token);

        Logger.LogInformation("{AgentType} started. ThreadId: {ThreadId}", GetType().Name, ThreadId);

        await _runTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(TimeSpan? timeout = null)
    {
        if (_internalCts == null || _runTask == null)
        {
            return;
        }

        Logger.LogInformation("Stopping {AgentType}...", GetType().Name);

        // Signal cancellation
        await _internalCts.CancelAsync();

        // NOTE: We intentionally do NOT complete the input channel here
        // to allow restart via RunAsync. The channel will be recreated if needed.
        // The cancellation token signals the loop to exit cleanly.

        // Wait for loop to finish
        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(30);
        try
        {
            await _runTask.WaitAsync(effectiveTimeout);
        }
        catch (TimeoutException)
        {
            Logger.LogWarning("Loop stop timed out after {Timeout}", effectiveTimeout);
        }
        catch (OperationCanceledException)
        {
            // Expected
        }

        // Clean up for potential restart
        _runTask = null;
        _internalCts?.Dispose();
        _internalCts = null;

        Logger.LogInformation("{AgentType} stopped, ready for restart", GetType().Name);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;

        await StopAsync();

        _internalCts?.Dispose();

        await OnDisposeAsync();

        // Complete input channel on disposal (final cleanup - no restart possible)
        _ = _inputChannel.Writer.TryComplete();

        // Close all subscriber channels
        foreach (var (_, channel) in _outputSubscribers)
        {
            _ = channel.Writer.TryComplete();
        }

        _outputSubscribers.Clear();

        GC.SuppressFinalize(this);
    }

    #endregion

    #region Run Lifecycle Helpers

    /// <summary>
    /// Start a new run for the given inputs. Call this from RunLoopAsync when ready to process.
    /// </summary>
    /// <param name="inputs">The queued inputs to process in this run</param>
    /// <param name="parentRunId">Optional parent run ID (defaults to latest run)</param>
    /// <returns>The run assignment</returns>
    protected RunAssignment StartRun(IReadOnlyList<QueuedInput> inputs, string? parentRunId = null)
    {
        ArgumentNullException.ThrowIfNull(inputs);

        var runId = Guid.NewGuid().ToString("N");
        var generationId = Guid.NewGuid().ToString("N");
        var inputIds = inputs.Select(i => i.ReceiptId).ToList();

        lock (_stateLock)
        {
            parentRunId ??= _latestRunId;
            _currentRunId = runId;
        }

        Logger.LogInformation(
            "Starting run {RunId} (parent: {ParentRunId}, generation: {GenerationId}, inputs: {InputCount})",
            runId,
            parentRunId ?? "none",
            generationId,
            inputs.Count);

        return new RunAssignment(runId, generationId, inputIds, parentRunId);
    }

    /// <summary>
    /// Complete a run and publish the completion message.
    /// </summary>
    /// <param name="runId">The run ID that completed</param>
    /// <param name="generationId">The generation ID</param>
    /// <param name="wasForked">Whether the run was forked due to new input</param>
    /// <param name="forkedToRunId">The run ID that was forked to (if applicable)</param>
    /// <param name="pendingMessageCount">Number of pending message batches waiting to be processed</param>
    /// <param name="isError">Whether the run completed due to an error</param>
    /// <param name="errorMessage">Error message when isError is true</param>
    /// <param name="ct">Cancellation token</param>
    protected async Task CompleteRunAsync(
        string runId,
        string generationId,
        bool wasForked = false,
        string? forkedToRunId = null,
        int pendingMessageCount = 0,
        bool isError = false,
        string? errorMessage = null,
        CancellationToken ct = default)
    {
        await PublishToAllAsync(new RunCompletedMessage
        {
            CompletedRunId = runId,
            WasForked = wasForked,
            ForkedToRunId = forkedToRunId,
            ThreadId = ThreadId,
            GenerationId = generationId,
            HasPendingMessages = pendingMessageCount > 0,
            PendingMessageCount = pendingMessageCount,
            IsError = isError,
            ErrorMessage = errorMessage,
        }, ct);

        lock (_stateLock)
        {
            _latestRunId = runId;
            _currentRunId = null;
        }

        if (isError)
        {
            Logger.LogWarning("Run {RunId} completed with error: {ErrorMessage}", runId, errorMessage);
        }
        else
        {
            Logger.LogInformation("Run {RunId} completed. WasForked: {WasForked}", runId, wasForked);
        }

        // Persist metadata after run completes
        await UpdateMetadataAsync(ct);
    }

    #endregion

    #region Abstract/Virtual Members

    /// <summary>
    /// Called before the run loop starts. Override to perform async initialization.
    /// </summary>
    protected virtual Task OnBeforeRunAsync()
    {
        return Task.CompletedTask;
    }

    /// <summary>
    /// Called during disposal. Override to clean up implementation-specific resources asynchronously.
    /// </summary>
    protected virtual Task OnDisposeAsync()
    {
        return Task.CompletedTask;
    }

    /// <summary>
    /// Called after the run loop stops. Override to perform async cleanup after each run cycle.
    /// </summary>
    protected virtual Task OnAfterRunAsync()
    {
        return Task.CompletedTask;
    }

    /// <summary>
    /// The main run loop. Implementation owns this entirely and decides:
    /// - When to drain inputs from the queue (TryDrainInputs or InputReader.WaitToReadAsync)
    /// - When to start runs (StartRun)
    /// - How to handle mid-run input (poll between turns vs concurrent watching)
    /// - When to complete runs (CompleteRunAsync)
    /// </summary>
    /// <param name="ct">Cancellation token</param>
    protected abstract Task RunLoopAsync(CancellationToken ct);

    #endregion
}
