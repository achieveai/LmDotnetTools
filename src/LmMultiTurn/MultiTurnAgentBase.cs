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

    // Replay buffer for the in-flight run. A client that reconnects mid-run (after switching
    // conversations or refreshing the page) re-subscribes via SubscribeAsync; without replay it
    // would only see messages published AFTER re-subscribing, so the visible stream froze. We
    // buffer the current run's published messages (from its RunAssignmentMessage until its
    // RunCompletedMessage) and replay them to a joining subscriber. `_replayLock` guards
    // register-subscriber + buffer-snapshot (SubscribeAsync) atomically against the buffer-append +
    // subscriber-snapshot (PublishToAllAsync), so a message published concurrently with a subscribe
    // reaches that subscriber EXACTLY once (replay XOR live) — this holds even if publishes overlap
    // (e.g. parallel tool-call results). Relative ordering of concurrently-published messages is not
    // guaranteed (the channel writes happen outside the lock), exactly as before this change.
    private readonly object _replayLock = new();
    private readonly List<IMessage> _replayBuffer = [];
    private bool _replayRunActive;
    private bool _replayBufferTruncated;
    private long _replayBufferBytes;
    // Replay is bounded by BOTH a message count and an estimated byte budget: a long tool/reasoning
    // turn can stay under the count cap while still retaining large per-message payloads (text, tool
    // args/results), and multiple live conversations multiply that. Whichever cap trips first stops
    // buffering — the run keeps streaming live; only a mid-run reconnect's replay is truncated.
    private readonly int _maxReplayBufferSize;
    private readonly long _maxReplayBufferBytes;

    // State
    private string? _currentRunId;
    private string? _latestRunId;
    private readonly object _stateLock = new();
    private readonly object _historyLock = new();

    // Lifecycle
    private Task? _runTask;
    private CancellationTokenSource? _internalCts;

    // Set once history has been (attempted to be) recovered from the store, so RunAsync's
    // startup recovery and any explicit RecoverAsync call never double-restore (RestoreHistory
    // appends). Guards the "recover persisted history on (re)create" path used by the agent pool.
    private bool _historyRecovered;
    private volatile bool _isDisposed;

    // Set once run-ledger reconciliation has run for this process instance, so RunAsync never
    // re-reconciles on an explicit restart within the same process (only a genuine new process
    // start should treat prior Queued/InProgress rows as dangling).
    private bool _runLedgerReconciled;

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

    /// <summary>
    /// Non-null only when the constructor's <c>persistRunLedger</c> flag is set, in which case
    /// <see cref="Store"/> is guaranteed to also implement this interface. All run-ledger
    /// durability (atomic mint+queue write, InProgress/terminal transitions, injected-input
    /// folding, and restart reconciliation) is gated on this being non-null, so it — not a
    /// separate bool — is the single source of truth for whether run-ledger persistence is on.
    /// </summary>
    protected IRunLedgerStore? RunLedgerStore { get; }

    /// <summary>
    /// Grace period the deferred-fallback in <see cref="ExecuteRunAsync"/> waits
    /// for additional channel activity before firing on a completion that has no
    /// receipt-correlated assignment. Override in tests to keep them fast.
    /// </summary>
    protected virtual TimeSpan FallbackGracePeriod => TimeSpan.FromSeconds(2);

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

    /// <summary>
    /// The most recent run id observed (current or last completed). Available to
    /// subclasses overriding metadata persistence — for example, recording a
    /// provider session id alongside the run it belongs to.
    /// </summary>
    protected string? LatestRunId
    {
        get
        {
            lock (_stateLock)
            {
                return _latestRunId;
            }
        }
    }

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
    /// <param name="maxReplayBufferSize">Max messages buffered for mid-run reconnect replay (default: 10,000).</param>
    /// <param name="maxReplayBufferBytes">Max estimated bytes buffered for mid-run reconnect replay (default: 8 MiB).</param>
    /// <param name="persistRunLedger">
    /// When true, durably tracks run status and pre-run input acceptance via <paramref name="store"/>
    /// (which must then also implement <see cref="IRunLedgerStore"/>) — enables <see cref="TrySendAsync"/>
    /// and restart reconciliation. Default false preserves existing in-memory-only behavior.
    /// </param>
    protected MultiTurnAgentBase(
        string threadId,
        string? systemPrompt = null,
        GenerateReplyOptions? defaultOptions = null,
        int maxTurnsPerRun = 50,
        int inputChannelCapacity = 100,
        int outputChannelCapacity = 1000,
        IConversationStore? store = null,
        ILogger? logger = null,
        int maxReplayBufferSize = 10_000,
        long maxReplayBufferBytes = 8L * 1024 * 1024,
        bool persistRunLedger = false)
    {
        ArgumentNullException.ThrowIfNull(threadId);

        ThreadId = threadId;
        SystemPrompt = systemPrompt;
        MaxTurnsPerRun = maxTurnsPerRun;
        _inputChannelCapacity = inputChannelCapacity;
        _outputChannelCapacity = outputChannelCapacity;
        _maxReplayBufferSize = maxReplayBufferSize;
        _maxReplayBufferBytes = maxReplayBufferBytes;
        DefaultOptions = defaultOptions ?? new GenerateReplyOptions();
        Store = store;
        Logger = logger ?? NullLogger.Instance;

        if (persistRunLedger)
        {
            RunLedgerStore = store as IRunLedgerStore
                ?? throw new ArgumentException(
                    $"{nameof(persistRunLedger)} is true but {nameof(store)} is null or does not implement {nameof(IRunLedgerStore)}.",
                    nameof(store));
        }

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

    /// <summary>
    /// Adds a message that represents a deferred tool-call placeholder, waiting on persistence
    /// to complete before returning. Used by <c>MultiTurnAgentLoop</c> when a tool handler
    /// returns <see cref="ToolHandlerResult.Deferred"/>: the placeholder must
    /// be durable in the store before any subscriber sees it, so a webhook-triggered
    /// <c>ResolveToolCallAsync</c> can safely call <see cref="IConversationStore.ReplaceMessageAsync"/>
    /// without racing the placeholder's persistence.
    /// </summary>
    /// <remarks>
    /// Persistence runs first; the in-memory append happens only after the store has accepted
    /// the message. If persistence fails, the exception propagates and no in-memory state is
    /// mutated — callers (e.g., <c>MultiTurnAgentLoop.ExecuteAndPublishToolCallAsync</c>) are
    /// responsible for unwinding any pre-registered deferred entries on failure. The
    /// non-deferred <see cref="AddToHistory"/> path remains fire-and-forget; synchronous
    /// persistence is only required where in-place replacement is on the table.
    /// </remarks>
    protected async Task AddDeferredToHistoryAsync(IMessage message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (Store != null)
        {
            string? runId;
            lock (_stateLock)
            {
                runId = _currentRunId;
            }

            if (runId != null)
            {
                // Persist BEFORE the in-memory append so a failure leaves no orphaned entry
                // in ConversationHistory. Letting the exception propagate is intentional —
                // the deferred-tool guarantee is load-bearing for webhook resolution.
                var persisted = MessagePersistenceConverter.ToPersistedMessage(message, ThreadId, runId);
                await Store.AppendMessagesAsync(ThreadId, [persisted], ct);
            }
        }

        lock (_historyLock)
        {
            ConversationHistory.Add(message);
        }
    }

    /// <summary>
    /// Replaces the most recent <see cref="ToolCallResultMessage"/> in history that has the
    /// given <c>ToolCallId</c>, applying <paramref name="updater"/> to compute the new value.
    /// Returns the (old, new) pair so the caller can publish the updated message and persist
    /// the change via <see cref="ReplacePersistedAsync"/>.
    /// </summary>
    /// <remarks>
    /// Idempotency is the updater's responsibility — typically the updater short-circuits and
    /// returns <c>existing</c> unchanged when the resolution has already been applied with the
    /// same content. The base method only enforces "must exist".
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// Thrown when no <see cref="ToolCallResultMessage"/> with the given ToolCallId is in history.
    /// </exception>
    protected (ToolCallResultMessage Old, ToolCallResultMessage New) UpdateToolResultByCallId(
        string toolCallId,
        Func<ToolCallResultMessage, ToolCallResultMessage> updater)
    {
        ArgumentException.ThrowIfNullOrEmpty(toolCallId);
        ArgumentNullException.ThrowIfNull(updater);

        lock (_historyLock)
        {
            var index = ConversationHistory.FindLastIndex(m =>
                m is ToolCallResultMessage tcr && tcr.ToolCallId == toolCallId);
            if (index < 0)
            {
                throw new InvalidOperationException(
                    $"No ToolCallResultMessage with ToolCallId '{toolCallId}' found in history.");
            }

            var old = (ToolCallResultMessage)ConversationHistory[index];
            var updated = updater(old);
            ConversationHistory[index] = updated;
            return (old, updated);
        }
    }

    /// <summary>
    /// Persists the replacement of a previously-appended <see cref="ToolCallResultMessage"/>
    /// in the store, addressing it by its deterministic Id (<c>tcr:{threadId}:{toolCallId}</c>).
    /// Failures are logged and swallowed — in-memory mutation is the source of truth for the
    /// running loop; persistence becomes eventually consistent.
    /// </summary>
    protected async Task ReplacePersistedAsync(
        ToolCallResultMessage old,
        ToolCallResultMessage updated,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(old);
        ArgumentNullException.ThrowIfNull(updated);

        if (Store == null)
        {
            return;
        }

        if (string.IsNullOrEmpty(updated.ToolCallId))
        {
            // Without a ToolCallId we can't construct the deterministic Id. Should not happen
            // for valid tool-call results.
            Logger.LogWarning("Cannot persist replacement: ToolCallId is null/empty");
            return;
        }

        string? runId;
        lock (_stateLock)
        {
            runId = _currentRunId ?? _latestRunId;
        }

        runId ??= old.RunId ?? updated.RunId ?? string.Empty;

        try
        {
            var persisted = MessagePersistenceConverter.ToPersistedMessage(updated, ThreadId, runId);
            await Store.ReplaceMessageAsync(ThreadId, persisted, ct);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(
                ex,
                "Failed to persist deferred-tool resolution for ToolCallId={ToolCallId}",
                updated.ToolCallId);
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

        // Load metadata first. We do NOT mark recovery complete until a load has finished
        // (or is definitively empty); marking it up front would poison _historyRecovered if a
        // transient store/IO/deserialization fault threw here, causing a later retry to skip
        // recovery and start with empty history.
        var metadata = await Store.LoadMetadataAsync(ThreadId, ct);
        if (metadata == null)
        {
            Logger.LogDebug("No stored metadata found for thread {ThreadId}", ThreadId);
            _historyRecovered = true;
            return false;
        }

        // Load messages
        var persistedMessages = await Store.LoadMessagesAsync(ThreadId, ct);
        if (persistedMessages.Count == 0)
        {
            Logger.LogDebug("No stored messages found for thread {ThreadId}", ThreadId);
            _historyRecovered = true;

            // Some recoverable state (e.g. notify_waits) is persisted separately from message
            // history and must be restored even when there are zero message rows for this thread.
            await OnThreadRecoveredAsync(ct);
            return false;
        }

        // Mark recovery complete before restoring so the guard prevents a second recover from
        // appending history twice (RestoreHistory appends). At this point the load has
        // succeeded, so the flag cannot be poisoned by a transient fault.
        _historyRecovered = true;

        // Convert persisted messages back to IMessages
        var messages = MessagePersistenceConverter.FromPersistedMessages(persistedMessages);

        // Restore history
        RestoreHistory(messages);

        // Restore state
        lock (_stateLock)
        {
            _latestRunId = metadata.LatestRunId;
        }

        // Give implementations a chance to seed in-memory state from the restored history
        // (e.g., MultiTurnAgentLoop rebuilds its deferred-tool registry here).
        await OnHistoryRestoredAsync(messages, ct);

        // Restore any other recoverable state that isn't derived from message history (e.g.
        // notify_waits, which are keyed by thread in a separate table). Called exactly once per
        // recovery — this is the non-empty-history counterpart to the call on the early-return
        // branch above.
        await OnThreadRecoveredAsync(ct);

        Logger.LogInformation(
            "Recovered {MessageCount} messages for thread {ThreadId}. LatestRunId: {LatestRunId}",
            messages.Count,
            ThreadId,
            metadata.LatestRunId);

        return true;
    }

    /// <summary>
    /// Called from <see cref="RecoverAsync"/> after history has been restored from the store.
    /// Override to rebuild any in-memory state derived from history (e.g., a deferred-tool
    /// registry on the loop).
    /// </summary>
    /// <param name="messages">The full restored conversation history, in load order.</param>
    /// <param name="ct">Cancellation token.</param>
    protected virtual Task OnHistoryRestoredAsync(IReadOnlyList<IMessage> messages, CancellationToken ct)
    {
        return Task.CompletedTask;
    }

    /// <summary>
    /// Called from <see cref="RecoverAsync"/> exactly once per recovery attempt, after metadata
    /// has been loaded — regardless of whether any message rows exist for this thread. Some
    /// recoverable state (e.g. notify_waits) is persisted separately from message history, keyed
    /// only by thread, so it must not be gated on <c>persistedMessages.Count &gt; 0</c>. Override
    /// to restore that kind of state. Runs after <see cref="OnHistoryRestoredAsync"/> when
    /// messages exist, or in its place when there are none.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    protected virtual Task OnThreadRecoveredAsync(CancellationToken ct)
    {
        return Task.CompletedTask;
    }

    #endregion

    #region Input API

    /// <summary>
    /// Direct access to the input channel reader for implementations that need push-based notification.
    /// </summary>
    protected ChannelReader<QueuedInput> InputReader => _inputChannel.Reader;

    /// <summary>
    /// Posts a pre-built <see cref="QueuedInput"/> directly to the input channel, preserving
    /// any non-default fields (including <see cref="QueuedInput.Resume"/>). Used by
    /// <c>MultiTurnAgentLoop</c> to enqueue internal resume sentinels for deferred-tool
    /// auto-resume; not for general user input — use <see cref="SendAsync"/> for that.
    /// </summary>
    protected ValueTask EnqueueRawAsync(QueuedInput queuedInput, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(queuedInput);
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        return _inputChannel.Writer.TryWrite(queuedInput) ? ValueTask.CompletedTask : _inputChannel.Writer.WriteAsync(queuedInput, ct);
    }

    /// <summary>
    /// Non-blocking counterpart to <see cref="EnqueueRawAsync"/>: attempts to post a pre-built
    /// <see cref="QueuedInput"/> onto the input channel without ever awaiting a full channel.
    /// Used by <c>MultiTurnAgentLoop</c> for restart-recovery notify delivery
    /// (<see cref="AchieveAi.LmDotnetTools.LmMultiTurn.Triggers.TriggerRuntime.RestoreNotifyWaitsAsync"/>),
    /// which can run before the run loop starts reading — blocking there would deadlock startup.
    /// </summary>
    /// <returns>True if the input was accepted into the channel; false if the channel is currently
    /// full (the caller must not treat the input as delivered).</returns>
    protected bool TryEnqueueRaw(QueuedInput queuedInput)
    {
        ArgumentNullException.ThrowIfNull(queuedInput);
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        return _inputChannel.Writer.TryWrite(queuedInput);
    }

    /// <summary>
    /// Throws <see cref="ObjectDisposedException"/> if the agent has been disposed. Public API
    /// methods on subclasses (e.g., <c>MultiTurnAgentLoop.ResolveToolCallAsync</c>) call this
    /// to fail fast instead of mutating disposed state.
    /// </summary>
    protected void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
    }

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
    public virtual async ValueTask<SendReceipt?> TrySendAsync(
        List<IMessage> messages,
        string? inputId = null,
        string? parentRunId = null,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        var receiptId = inputId ?? Guid.NewGuid().ToString("N");
        var queuedAt = DateTimeOffset.UtcNow;

        if (RunLedgerStore != null)
        {
            // Persist acceptance BEFORE attempting to enqueue. A store failure here propagates
            // to the caller (surfaces as an HTTP 500) with no channel write attempted, so an
            // accepted-input record is never left dangling without a corresponding enqueue
            // attempt — see plan-comment-v5.md Approach, TrySendAsync ordering.
            await RunLedgerStore.RecordAcceptedInputAsync(ThreadId, receiptId, queuedAt, ct);
        }

        var input = new UserInput(messages, inputId, parentRunId);
        var queued = new QueuedInput(input, receiptId, queuedAt);

        if (!_inputChannel.Writer.TryWrite(queued))
        {
            Logger.LogWarning("Input channel full, rejecting TrySendAsync. ReceiptId: {ReceiptId}", receiptId);

            if (RunLedgerStore != null)
            {
                // Roll back the acceptance record: the input was never actually queued, so a
                // caller polling by inputId must not see it as durably accepted.
                await RunLedgerStore.RemoveAcceptedInputAsync(ThreadId, receiptId, ct);
            }

            return null;
        }

        Logger.LogDebug("Message queued via TrySendAsync. ReceiptId: {ReceiptId}, InputId: {InputId}", receiptId, inputId);

        return new SendReceipt(receiptId, inputId, queuedAt);
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

            // Receipt-id correlation is the primary signal. The deferred fallback below
            // only engages when an implementation publishes a RunCompletedMessage for a
            // run whose RunAssignmentMessage we observed but did NOT list our receipt
            // (a publisher bug — the concrete production case is a Claude dequeue
            // heuristic that misses, leaving the receipt-correlated assignment never
            // emitted).
            string? targetRunId = null;
            string? pendingFallbackRunId = null;
            var observedAssignmentRunIds = new HashSet<string>(StringComparer.Ordinal);

            // Yield messages until run completes. We use a manual read loop instead of
            // ReadAllAsync because the deferred-fallback path needs a grace period to
            // distinguish "prior in-flight run completed and our run is about to start"
            // (a new RunAssignmentMessage will arrive shortly — abort the fallback)
            // from "publisher bug on our actual run" (no further messages come — fire
            // the fallback). Without this, an immediate fallback on completion would
            // race-terminate the iterator before the caller's run executes.
            while (true)
            {
                bool hasMessage;
                if (pendingFallbackRunId != null)
                {
                    using var graceCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    graceCts.CancelAfter(FallbackGracePeriod);
                    try
                    {
                        hasMessage = await outputChannel.Reader.WaitToReadAsync(graceCts.Token);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        // Grace period elapsed without any further activity — the
                        // pending completion is the terminal one. Fire fallback.
                        Logger.LogWarning(
                            "ExecuteRun terminating on RunId {RunId} via deferred fallback — receipt {ReceiptId} was never observed in a RunAssignmentMessage and no further messages arrived within {GraceMs}ms. "
                            + "This indicates the implementation did not publish a receipt-correlated assignment for this run.",
                            pendingFallbackRunId,
                            receiptId,
                            (int)FallbackGracePeriod.TotalMilliseconds);
                        yield break;
                    }
                }
                else
                {
                    hasMessage = await outputChannel.Reader.WaitToReadAsync(ct);
                }

                if (!hasMessage)
                {
                    // Channel completed — exit cleanly.
                    yield break;
                }

                while (outputChannel.Reader.TryRead(out var msg))
                {
                    yield return msg;

                    if (msg is RunAssignmentMessage assignment)
                    {
                        var runId = assignment.Assignment.RunId;
                        if (!string.IsNullOrEmpty(runId))
                        {
                            _ = observedAssignmentRunIds.Add(runId);
                        }

                        // A new assignment arrived — any pending fallback was for an
                        // earlier run, not ours. Clear it.
                        pendingFallbackRunId = null;

                        if (assignment.Assignment.InputIds?.Contains(receiptId) == true)
                        {
                            targetRunId = runId;
                            Logger.LogDebug("ExecuteRun assigned to RunId: {RunId}", targetRunId);
                        }
                    }

                    if (msg is RunCompletedMessage completed && !string.IsNullOrEmpty(completed.CompletedRunId))
                    {
                        // Primary: receipt-correlated match — exit immediately.
                        if (targetRunId != null && completed.CompletedRunId == targetRunId)
                        {
                            Logger.LogDebug("ExecuteRun completed for RunId: {RunId}", targetRunId);
                            yield break;
                        }

                        // Defer: receipt-id correlation never fired, and this completion
                        // is for a run whose assignment we observed since subscribing.
                        // We don't break yet — a subsequent RunAssignmentMessage would
                        // indicate this completion belonged to an earlier run, not ours.
                        if (targetRunId == null && observedAssignmentRunIds.Contains(completed.CompletedRunId))
                        {
                            pendingFallbackRunId = completed.CompletedRunId;
                        }
                    }
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

        // Atomically register this subscriber AND snapshot the in-flight run's buffered messages,
        // so a message published concurrently is delivered EITHER via this replay snapshot OR via
        // the live channel below — never both, never neither. See `_replayLock` remarks.
        IReadOnlyList<IMessage> replay;
        lock (_replayLock)
        {
            _outputSubscribers[subscriberId] = channel;
            replay = _replayRunActive && _replayBuffer.Count > 0
                ? [.. _replayBuffer]
                : [];
        }

        Logger.LogDebug(
            "Subscriber {SubscriberId} connected (replaying {ReplayCount} in-flight message(s))",
            subscriberId,
            replay.Count);

        try
        {
            // Replay the in-flight run's already-published messages first (so a reconnecting client
            // resumes from the start of the live run), then stream subsequent live messages.
            foreach (var buffered in replay)
            {
                ct.ThrowIfCancellationRequested();
                yield return buffered;
            }

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
    protected ValueTask PublishToAllAsync(IMessage message, CancellationToken ct)
    {
        _ = ct;
        List<KeyValuePair<string, Channel<IMessage>>> targets;
        lock (_replayLock)
        {
            // Maintain the in-flight run's replay buffer. RunAssignmentMessage opens a fresh run;
            // RunCompletedMessage closes it (after which a joining subscriber must NOT replay it —
            // the client already has completed messages via persisted REST history, so replaying
            // would duplicate). Snapshotting subscribers under the SAME lock SubscribeAsync uses to
            // register + snapshot the buffer guarantees this message reaches each subscriber exactly
            // once (replay XOR live).
            if (message is RunAssignmentMessage)
            {
                _replayBuffer.Clear();
                _replayBufferBytes = 0;
                _replayRunActive = true;
                _replayBufferTruncated = false;
            }

            if (_replayRunActive)
            {
                if (_replayBuffer.Count < _maxReplayBufferSize && _replayBufferBytes < _maxReplayBufferBytes)
                {
                    _replayBuffer.Add(message);
                    _replayBufferBytes += EstimateMessageBytes(message);
                }
                else if (!_replayBufferTruncated)
                {
                    _replayBufferTruncated = true;
                    Logger.LogWarning(
                        "In-flight replay buffer hit its cap ({CountCap} messages / {ByteCap} bytes); a client "
                            + "reconnecting mid-run may miss the earliest deltas of this run (persisted history "
                            + "still covers its completed messages).",
                        _maxReplayBufferSize,
                        _maxReplayBufferBytes);
                }
            }

            if (message is RunCompletedMessage)
            {
                _replayRunActive = false;
                // Free the buffered run now that it can no longer be replayed (replay is gated on
                // _replayRunActive). A subscriber joining after completion uses persisted history.
                _replayBuffer.Clear();
                _replayBufferBytes = 0;
            }

            targets = [.. _outputSubscribers];
        }

        foreach (var (subscriberId, channel) in targets)
        {
            PublishToSubscriber(subscriberId, channel, message);
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Delivers <paramref name="message"/> to one subscriber WITHOUT ever blocking the publisher.
    /// A bounded per-subscriber channel means a slow/stalled consumer (classically a reconnecting
    /// client still draining the in-flight run's replay buffer) can fill its channel; awaiting the
    /// write there would put that consumer on the live run's hot path and let it backpressure the
    /// active run and every other subscriber. So we write non-blocking and, when
    /// the channel is full, DROP the subscriber: remove it from the fan-out and complete its channel
    /// so its <see cref="SubscribeAsync"/> enumerator ends. The client can reconnect; resume replays
    /// the in-flight run from the buffer. A reconnecting replay consumer can therefore never block
    /// <see cref="PublishToAllAsync"/>.
    /// </summary>
    private void PublishToSubscriber(string subscriberId, Channel<IMessage> channel, IMessage message)
    {
        // Fast path: succeeds whenever the subscriber is keeping up (the overwhelming common case).
        if (channel.Writer.TryWrite(message))
        {
            return;
        }

        if (_outputSubscribers.TryRemove(subscriberId, out var removed))
        {
            _ = removed.Writer.TryComplete();
            Logger.LogWarning(
                "Dropping slow subscriber {SubscriberId}: output channel full at capacity {Capacity}; "
                    + "the live run is not blocked and the client can reconnect to resume.",
                subscriberId,
                _outputChannelCapacity);
        }
    }

    /// <summary>
    /// Cheap estimate of the heap a buffered message retains, used only to bound total replay memory
    /// against <c>_maxReplayBufferBytes</c>. Dominated by text-ish payloads (≈2 bytes/char); other
    /// shapes fall back to a small base overhead. Intentionally approximate — it caps memory, it is
    /// not exact accounting, and runs under the replay lock so it must stay allocation-free and O(1)
    /// per message (tool-call args are summed, which is bounded by the call count).
    /// </summary>
    private static long EstimateMessageBytes(IMessage message)
    {
        const long baseOverhead = 128;
        switch (message)
        {
            case TextUpdateMessage t:
                return baseOverhead + ((t.Text?.Length ?? 0) * 2L);
            case TextMessage t:
                return baseOverhead + ((t.Text?.Length ?? 0) * 2L);
            case ToolsCallMessage tc:
                {
                    var bytes = baseOverhead;
                    if (tc.ToolCalls is { } calls)
                    {
                        foreach (var call in calls)
                        {
                            bytes += ((call.FunctionName?.Length ?? 0) + (call.FunctionArgs?.Length ?? 0)) * 2L;
                        }
                    }

                    return bytes;
                }

            default:
                return baseOverhead;
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

        // Rehydrate persisted conversation history before the loop processes any input. The agent
        // pool creates a loop and starts it via RunAsync without ever calling RecoverAsync, so
        // without this an agent recreated after a restart (or a mode/provider switch, which also
        // rebuilds the agent) begins with empty history and the LLM loses all prior context even
        // though every message is still in the store. Idempotent via _historyRecovered, so callers
        // that already recovered explicitly are not double-restored.
        if (Store != null && !_historyRecovered)
        {
            // History recovery is best-effort enrichment: a transient store/IO/deserialization
            // fault must degrade to empty history, not crash agent startup. Genuine
            // caller-cancellation still propagates.
            try
            {
                _ = await RecoverAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _historyRecovered = false; // let a later explicit RecoverAsync retry
                Logger.LogWarning(
                    ex,
                    "History recovery failed for thread {ThreadId}; starting with empty history",
                    ThreadId);
            }
        }

        if (RunLedgerStore != null && !_runLedgerReconciled)
        {
            _runLedgerReconciled = true;
            await ReconcileRunLedgerAsync(ct);
        }

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
    /// Resolves the explicit fork parent for a batch of inputs.
    /// Returns the first non-null/non-empty <see cref="UserInput.ParentRunId"/> in the batch and
    /// whether the resolution came from caller input (an explicit fork) vs. no fork at all.
    /// Empty strings are treated as null per the contract on <see cref="UserInput.ParentRunId"/>.
    /// </summary>
    /// <remarks>
    /// When a batch contains more than one distinct non-null <c>ParentRunId</c> (an extremely
    /// rare cross-caller race), the first-encountered value wins and the divergent set is logged
    /// at warning level. Mixed batches still mark <c>IsExplicitFork = true</c> so the
    /// signal is not silently dropped.
    /// </remarks>
    /// <param name="inputs">The queued inputs from the batch.</param>
    /// <returns>
    /// A tuple of (parent run id from caller input or null, whether caller explicitly forked).
    /// </returns>
    protected (string? ParentRunId, bool IsExplicitFork) ResolveBatchParent(
        IReadOnlyList<QueuedInput> inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);

        string? first = null;
        HashSet<string>? distinct = null;

        foreach (var q in inputs)
        {
            var p = q.Input.ParentRunId;
            if (string.IsNullOrEmpty(p))
            {
                continue;
            }

            if (first == null)
            {
                first = p;
                continue;
            }

            if (!string.Equals(p, first, StringComparison.Ordinal))
            {
                distinct ??= new HashSet<string>(StringComparer.Ordinal) { first };
                _ = distinct.Add(p);
            }
        }

        if (distinct != null && distinct.Count > 1)
        {
            Logger.LogWarning(
                "Mixed ParentRunId values in batch ({Count} distinct: {Parents}); using first-encountered '{First}'.",
                distinct.Count,
                string.Join(",", distinct),
                first);
        }

        return (first, first != null);
    }

    /// <summary>
    /// Start a new run for the given inputs. Call this from RunLoopAsync when ready to process.
    /// When run-ledger persistence is enabled, mints the run id and durably records it as
    /// <see cref="RunStatus.Queued"/> in a single step (so a runId is never handed back without
    /// a corresponding ledger row), then immediately transitions the row to
    /// <see cref="RunStatus.InProgress"/> since turn execution begins synchronously after this
    /// returns. These are two separate durable writes — restart reconciliation can therefore
    /// observe either a dangling Queued row (crash between the two writes) or a dangling
    /// InProgress row (crash after, before the terminal write in <see cref="CompleteRunAsync"/>).
    /// </summary>
    /// <param name="inputs">The queued inputs to process in this run</param>
    /// <param name="parentRunId">Optional parent run ID (defaults to latest run)</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>The run assignment</returns>
    protected async Task<RunAssignment> StartRunAsync(
        IReadOnlyList<QueuedInput> inputs,
        string? parentRunId = null,
        CancellationToken ct = default)
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

        if (RunLedgerStore != null)
        {
            var createdAt = DateTimeOffset.UtcNow;
            await RunLedgerStore.UpsertRunLedgerAsync(
                new RunLedgerEntry(ThreadId, runId, RunStatus.Queued, inputIds, createdAt, createdAt),
                ct);
            await RunLedgerStore.UpsertRunLedgerAsync(
                new RunLedgerEntry(ThreadId, runId, RunStatus.InProgress, inputIds, createdAt, DateTimeOffset.UtcNow),
                ct);

            // Now folded into the run's own InputIds above — the pre-run acceptance record has
            // served its purpose (see TrySendAsync) and would otherwise accumulate forever.
            foreach (var inputId in inputIds)
            {
                await RunLedgerStore.RemoveAcceptedInputAsync(ThreadId, inputId, ct);
            }
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
    /// Durably folds newly-injected input receipt ids into the active run's ledger entry.
    /// Called by <c>MultiTurnAgentLoop</c> at its injection point — where a new send that
    /// arrives while a run is still in-flight is folded into that same run
    /// (<see cref="RunAssignment.WasInjected"/>) rather than starting a new one — so the ledger's
    /// <see cref="RunLedgerEntry.InputIds"/> stays the source of truth an injected inputId
    /// resolves through to its shared run. No-op when run-ledger persistence is disabled.
    /// </summary>
    /// <param name="runId">The active run the inputs were injected into.</param>
    /// <param name="injectedInputIds">The newly-injected inputs' receipt ids.</param>
    /// <param name="ct">Cancellation token</param>
    protected async Task RecordInjectedInputsAsync(
        string runId,
        IReadOnlyList<string> injectedInputIds,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(injectedInputIds);

        if (RunLedgerStore == null || injectedInputIds.Count == 0)
        {
            return;
        }

        var existing = await RunLedgerStore.LoadRunLedgerAsync(runId, ct);
        if (existing == null)
        {
            Logger.LogWarning(
                "No run ledger entry found for RunId {RunId} to record injected inputs {InputIds}",
                runId,
                string.Join(",", injectedInputIds));
            return;
        }

        var mergedInputIds = existing.InputIds.Union(injectedInputIds, StringComparer.Ordinal).ToList();
        await RunLedgerStore.UpsertRunLedgerAsync(
            existing with { InputIds = mergedInputIds, UpdatedAt = DateTimeOffset.UtcNow },
            ct);

        // Same cleanup as StartRunAsync: these ids are now covered by the run's InputIds.
        foreach (var injectedInputId in injectedInputIds)
        {
            await RunLedgerStore.RemoveAcceptedInputAsync(ThreadId, injectedInputId, ct);
        }
    }

    /// <summary>
    /// Complete a run: persists the terminal run-ledger status, then publishes the completion
    /// message. The ledger write happens FIRST and is allowed to throw and propagate — the REST
    /// status API treats the ledger as the source of truth, so a subscriber must never observe a
    /// <see cref="RunCompletedMessage"/> for a run whose terminal status failed to persist (which
    /// would otherwise let <c>GET /status</c> keep reporting <see cref="RunStatus.InProgress"/>
    /// after completion was broadcast). Propagating also means a persistence failure here is
    /// caught by the caller's per-run try/catch as a run failure, so at most one terminal outcome
    /// is ever published for a given run — not a Completed publish followed by a later Errored one.
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
        if (RunLedgerStore != null)
        {
            var existing = await RunLedgerStore.LoadRunLedgerAsync(runId, ct);
            if (existing != null)
            {
                var status = isError ? RunStatus.Errored : RunStatus.Completed;
                await RunLedgerStore.UpsertRunLedgerAsync(
                    existing with { Status = status, UpdatedAt = DateTimeOffset.UtcNow },
                    ct);
            }
            else
            {
                Logger.LogWarning(
                    "No run ledger entry found for RunId {RunId} at completion; skipping terminal ledger write",
                    runId);
            }
        }

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

    /// <summary>
    /// Reconciles run-ledger state left behind by a prior process instance. Runs once per
    /// process start (guarded by <c>_runLedgerReconciled</c> in <see cref="RunAsync"/>), never on
    /// an explicit in-process restart. Two kinds of dangling state are resolved, both to
    /// <see cref="RunStatus.Interrupted"/>:
    /// - A <see cref="RunStatus.Queued"/> or <see cref="RunStatus.InProgress"/> ledger row: this
    ///   process just started, so nothing can still be running it.
    /// - An accepted-input record (<see cref="IRunLedgerStore.ListAcceptedInputIdsAsync"/>) whose
    ///   inputId is not covered by any ledger entry's <see cref="RunLedgerEntry.InputIds"/>: the
    ///   input was durably accepted (see <see cref="TrySendAsync"/>) but the process crashed
    ///   before a run was ever assigned to it. A synthetic orphan ledger row is created so
    ///   resolving by that inputId needs no restart-specific branch of its own.
    /// Reconciliation failures are logged and swallowed — a transient store fault here must not
    /// prevent the agent from starting.
    /// </summary>
    private async Task ReconcileRunLedgerAsync(CancellationToken ct)
    {
        if (RunLedgerStore == null)
        {
            return;
        }

        try
        {
            var runs = await RunLedgerStore.ListRunLedgerAsync(ThreadId, ct);
            var coveredInputIds = new HashSet<string>(StringComparer.Ordinal);

            foreach (var run in runs)
            {
                foreach (var id in run.InputIds)
                {
                    _ = coveredInputIds.Add(id);
                }

                if (run.Status is RunStatus.Queued or RunStatus.InProgress)
                {
                    await RunLedgerStore.UpsertRunLedgerAsync(
                        run with { Status = RunStatus.Interrupted, UpdatedAt = DateTimeOffset.UtcNow },
                        ct);
                    Logger.LogWarning(
                        "Marking dangling run {RunId} (status {Status}) Interrupted on restart for thread {ThreadId}",
                        run.RunId,
                        run.Status,
                        ThreadId);
                }
            }

            var acceptedInputIds = await RunLedgerStore.ListAcceptedInputIdsAsync(ThreadId, ct);
            foreach (var inputId in acceptedInputIds)
            {
                if (coveredInputIds.Contains(inputId))
                {
                    continue;
                }

                var orphanRunId = Guid.NewGuid().ToString("N");
                var now = DateTimeOffset.UtcNow;
                await RunLedgerStore.UpsertRunLedgerAsync(
                    new RunLedgerEntry(ThreadId, orphanRunId, RunStatus.Interrupted, [inputId], now, now),
                    ct);
                Logger.LogWarning(
                    "Synthesized orphan Interrupted run {RunId} for accepted-but-never-assigned InputId {InputId} on restart for thread {ThreadId}",
                    orphanRunId,
                    inputId,
                    ThreadId);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Run-ledger reconciliation failed for thread {ThreadId}; continuing without it", ThreadId);
        }
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
    /// - When to start runs (StartRunAsync)
    /// - How to handle mid-run input (poll between turns vs concurrent watching)
    /// - When to complete runs (CompleteRunAsync)
    /// </summary>
    /// <param name="ct">Cancellation token</param>
    protected abstract Task RunLoopAsync(CancellationToken ct);

    #endregion
}
