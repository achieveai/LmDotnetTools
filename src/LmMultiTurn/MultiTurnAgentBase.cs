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
    private string? _replayRunId;
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

    // Cancellation token source for the run currently identified by _currentRunId, linked to (but
    // independently cancellable from) the loop's own lifetime token — see StartRunAsync/
    // CurrentRunToken/CancelCurrentRunAsync/CompleteRunAsync. Guarded by _stateLock alongside
    // _currentRunId so a reader always observes a consistent (runId, cts) pair.
    private CancellationTokenSource? _currentRunCts;

    // Exactly-once terminal arbiter (see CompleteRunAsync/CancelCurrentRunAsync): claims the
    // right to persist/publish the CURRENT run's (_currentRunId) terminal outcome. Bounded to at
    // most one in-flight claim (unlike a per-runId dictionary that would grow for the lifetime of
    // the process) because at most one run is ever "current" at a time. Guarded by _stateLock
    // alongside _currentRunId/_currentRunCts so CancelCurrentRunAsync and CompleteRunAsync race
    // deterministically: whichever acquires the lock first — Stop claiming NoActiveRun/StaleRun,
    // or natural/error completion claiming the terminal outcome — wins. Reset to false whenever a
    // claim attempt fails (e.g. a terminal-ledger write throws) so a subsequent retry for the SAME
    // still-current run (e.g. the caller's error-completion fallback) is not permanently
    // suppressed, and whenever a claim succeeds and the run is cleared (ready for the next run).
    private bool _currentRunTerminalClaimed;

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

    // Optional agent-wide publication observer (see IAgentPublicationObserver) — the single
    // future authority for durable child replay (WI #194 tasks 5-6). Null by default so
    // existing constructors/behavior are completely unaffected.
    private readonly IAgentPublicationObserver? _publicationObserver;

    // Monotonic per-agent sequence assigned to each AgentPublication under `_replayLock` (the SAME
    // lock guarding v1 replay-buffer bookkeeping) — see AgentPublication.Sequence.
    private long _publicationSequence;

    // Bounded-lifetime ordered-dispatch chain for the optional publication observer: the tail Task
    // of a linked list of "dispatch nodes", one per publication, each created under `_replayLock`.
    // A node first awaits the PREVIOUS node (swallowing its exception) so observer calls start in
    // the SAME order publications acquired `_replayLock` (Sequence order) — never concurrently —
    // then awaits ITS OWN publication's "v1 fan-out is done, ready to observe" signal before
    // invoking `_publicationObserver`. This never holds `_replayLock` across an await: nodes are
    // only ever CREATED inside the lock (a call that itself suspends immediately, handing back a
    // Task) — see PublishToAllAsync. One observer failure faults only that node/publication's own
    // publishing caller (who awaits just that node) without poisoning later nodes, and the chain
    // never outlives this agent — DisposeAsync cancels the observer's own cancellation scope
    // (_observerLifetimeCts, deliberately independent of any publish caller's run/request token —
    // see IAgentPublicationObserver remarks) and bounds its wait for the final tail, so disposal
    // can never leak or hang on it.
    private Task _observerDispatchTail = Task.CompletedTask;
    private readonly CancellationTokenSource _observerLifetimeCts = new();

    // Test-only seam (see AgentPublicationObserverTests): when set, PublishToAllAsync awaits this
    // for a publication right after that call's v1 fan-out completes (lock already released) but
    // BEFORE it signals its dispatch node "ready" — letting tests deterministically hold back one
    // publisher there to prove the observer chain's delivery order follows `_replayLock`
    // acquisition (Sequence) order rather than readiness/completion timing. Always null in
    // production.
    internal Func<AgentPublication, Task>? FanOutReadyDelayHookForTests;

    // Test-only seam (see AgentPublicationObserverTests): when set, PublishToAllAsync awaits this
    // BEFORE attempting to acquire `_replayLock` — letting tests deterministically model a
    // publish call (e.g. a ResolveToolCall result) that is blocked at that exact instant
    // DisposeAsync begins tearing down, to prove the disposal-vs-publish race resolves to a
    // predictable ObjectDisposedException from the agent itself (never a lazily-read, disposed
    // `_observerLifetimeCts.Token`) and that no dispatch node is appended after disposal's final
    // tail snapshot. Always null in production, so the hot path below is unaffected.
    internal Func<Task>? PrePublishLockHookForTests;

    /// <summary>
    /// Test-only snapshot of the canonical persistence chain's current tail (see
    /// `_canonicalPersistenceTail`), letting tests await "everything enqueued so far has
    /// finished" deterministically without driving a real <see cref="CompleteRunAsync"/> flush.
    /// Always <see cref="Task.CompletedTask"/> when strict mode is disabled. Internal — never
    /// used by any production code path.
    /// </summary>
    internal Task CanonicalPersistenceTailSnapshotForTests()
    {
        lock (_canonicalPersistenceLock)
        {
            return _canonicalPersistenceTail;
        }
    }

    // Opt-in strict canonical persistence (WI #194 tasks 7-8) — see `strictCanonicalPersistence`
    // constructor parameter. False for every existing caller/behavior.
    private readonly bool _strictCanonicalPersistence;

    // Per-agent ordered persistence chain, active only when `_strictCanonicalPersistence` is
    // true. Every `AddToHistory` canonical append and every `ReplacePersistedAsync` replacement
    // is enqueued here (see `EnqueueCanonicalPersistenceOperation`), one node per operation,
    // guarded by `_canonicalPersistenceLock`. A node awaits the PREVIOUS node first (swallowing
    // its exception — that node's own failure is separately recorded below and, for a direct
    // caller such as `ReplacePersistedAsync`, already propagated to ITS OWN caller) so operations
    // run strictly in enqueue order and never concurrently — in particular, a deferred-tool
    // placeholder append is always enqueued (and therefore always runs) before the
    // `ReplacePersistedAsync` call that later replaces it. Mirrors the existing
    // `_observerDispatchTail` chain pattern above.
    private readonly object _canonicalPersistenceLock = new();
    private Task _canonicalPersistenceTail = Task.CompletedTask;

    // Sticky first failure from any node in the chain above. Once set, it never clears — a
    // canonical-history write failure durably breaks this agent's terminal replayability
    // guarantee, so `FlushCanonicalPersistenceAsync` (awaited by `CompleteRunAsync` before any
    // terminal ledger write/publication) must keep failing, even if a LATER queued operation on
    // its own would have succeeded, and even across that same run's error-completion retry.
    private Exception? _canonicalPersistenceFault;

    // Per-agent durability lifetime token for the canonical persistence chain's ACTUAL store
    // writes (see `EnqueueCanonicalPersistenceOperation`/`RunCanonicalPersistenceNodeAsync`) —
    // deliberately independent of any run/request caller token (a `CancelCurrentRunAsync`, an
    // `AddDeferredToHistoryAsync`/`ReplacePersistedAsync` caller's own `ct`, or an external
    // `ResolveToolCallAsync` request's `ct`). Only `DisposeAsync` ever cancels this, so a normal
    // run cancellation or an external tool-resolution request cancellation can cancel only ITS
    // OWN caller's wait for the queued write (via `Task.WaitAsync(ct)`) — never the underlying
    // store write itself, and never record a sticky `_canonicalPersistenceFault` or permanently
    // brick a future terminal completion. Mirrors `_observerLifetimeCts` above.
    private readonly CancellationTokenSource _canonicalPersistenceLifetimeCts = new();

    // Test-only seam (see StrictCanonicalPersistenceTests): when set,
    // `EnqueueCanonicalPersistenceOperation` awaits this BEFORE acquiring
    // `_canonicalPersistenceLock` — letting tests deterministically model an
    // AddToHistory/AddDeferredToHistoryAsync/ReplacePersistedAsync call that is blocked at that
    // exact instant `DisposeAsync` begins tearing down, to prove the enqueue-vs-disposal race
    // resolves to a predictable `ObjectDisposedException` (never a node silently appended after
    // disposal already snapshotted the chain's tail). Always null in production, so the hot
    // (no-hook) path is unaffected. Mirrors `PrePublishLockHookForTests`.
    internal Func<Task>? PreCanonicalPersistenceLockHookForTests;

    /// <summary>
    /// Test-only accessor for the sticky canonical-persistence fault (see
    /// `_canonicalPersistenceFault`) — lets tests assert that a caller/run-token cancellation of
    /// an `AddDeferredToHistoryAsync`/`ReplacePersistedAsync` wait, or a disposal-driven
    /// durability-token cancellation, never records a fault, without needing to drive a real
    /// `CompleteRunAsync` flush. Always <c>false</c> when strict mode is disabled. Internal —
    /// never used by any production code path.
    /// </summary>
    internal bool HasCanonicalPersistenceFaultForTests => Volatile.Read(ref _canonicalPersistenceFault) != null;

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

    /// <summary>
    /// The cancellation token for the run most recently started by <see cref="StartRunAsync"/>,
    /// linked to (but independently cancellable from) the loop's lifetime token. A concrete
    /// <c>RunLoopAsync</c> implementation should read this immediately after <see
    /// cref="StartRunAsync"/> returns and pass it (instead of the loop's own token) to turn
    /// execution, so a matching <see cref="CancelCurrentRunAsync"/> call cancels only that run —
    /// never the outer loop. Returns <see cref="CancellationToken.None"/> if no run is active.
    /// </summary>
    protected CancellationToken CurrentRunToken
    {
        get
        {
            lock (_stateLock)
            {
                return _currentRunCts?.Token ?? CancellationToken.None;
            }
        }
    }

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
    /// <param name="publicationObserver">
    /// Optional agent-wide hook observing every message published via <see cref="PublishToAllAsync(IMessage, CancellationToken)"/>
    /// (see <see cref="IAgentPublicationObserver"/>). Null (default) preserves existing behavior
    /// exactly — v1 subscriber fan-out is completely unaffected by this parameter either way.
    /// </param>
    /// <param name="strictCanonicalPersistence">
    /// When true, enables strict ordered canonical-history durability, intended for captured
    /// child loops (e.g. a subagent whose durable replay/terminal outcome another component
    /// depends on): every <see cref="AddToHistory(IMessage)"/> canonical append and every
    /// <see cref="ReplacePersistedAsync"/> replacement enters one per-agent ordered persistence
    /// queue (so a replacement always runs after the placeholder append it replaces), a
    /// replacement failure propagates to its caller instead of being logged/swallowed, and
    /// <see cref="CompleteRunAsync"/> awaits that queue's flush — failing closed, with no terminal
    /// success or <see cref="Messages.RunCompletedMessage"/> — when any queued write failed.
    /// Default false preserves today's fire-and-forget append / best-effort-swallowed replacement
    /// behavior and existing call timing exactly.
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
        bool persistRunLedger = false,
        IAgentPublicationObserver? publicationObserver = null,
        bool strictCanonicalPersistence = false)
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
        _publicationObserver = publicationObserver;
        _strictCanonicalPersistence = strictCanonicalPersistence;

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
        AddToHistory(message, runIdOverride: null);
    }

    /// <summary>
    /// Adds a message to the conversation history, persisting it under an explicit run id when the
    /// current run id is unavailable (e.g. an out-of-band notification folded into history while the
    /// conversation is parked on an unresolved deferral, between runs). Falls back to
    /// <c>_currentRunId</c> when <paramref name="runIdOverride"/> is null.
    /// </summary>
    protected void AddToHistory(IMessage message, string? runIdOverride)
    {
        lock (_historyLock)
        {
            ConversationHistory.Add(message);
        }

        // Fire-and-forget persistence
        if (Store != null)
        {
            var runId = runIdOverride;
            if (runId == null)
            {
                lock (_stateLock)
                {
                    runId = _currentRunId;
                }
            }

            if (runId != null)
            {
                if (_strictCanonicalPersistence)
                {
                    // Still fire-and-forget from THIS caller's point of view — the queued node
                    // itself is what gives ordering/failure tracking; CompleteRunAsync's flush
                    // (not this call) is what observes/surfaces a failure. The write itself uses
                    // the per-agent durability token (see `_canonicalPersistenceLifetimeCts`),
                    // never a caller token — there IS no caller token here anyway (this overload
                    // takes none), but strict-mode writes must never depend on one regardless of
                    // call site.
                    try
                    {
                        var node = EnqueueCanonicalPersistenceOperation(
                            dCt => AppendCanonicalMessageAsync(message, runId, dCt));

                        // Observe a hook-delayed (test-only) ObjectDisposedException so a
                        // fire-and-forget call can never surface it as an unobserved task
                        // exception — the production (no-hook) path already throws
                        // synchronously below, so this continuation is a no-op there.
                        _ = node.ContinueWith(
                            static t => _ = t.Exception,
                            CancellationToken.None,
                            TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.OnlyOnFaulted,
                            TaskScheduler.Default);
                    }
                    catch (ObjectDisposedException)
                    {
                        // Disposal already began before this fire-and-forget append could
                        // enqueue — nothing further to do; mirrors this call's fire-and-forget
                        // nature in default mode.
                    }
                }
                else
                {
                    _ = PersistMessageAsync(message, runId, CancellationToken.None);
                }
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
    /// non-deferred <see cref="AddToHistory(IMessage)"/> path remains fire-and-forget; synchronous
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
                if (_strictCanonicalPersistence)
                {
                    // Enqueue into the SAME ordered chain used by AddToHistory/
                    // ReplacePersistedAsync, with the write itself running under the per-agent
                    // durability token (see `_canonicalPersistenceLifetimeCts`) — this
                    // placeholder append's node is what a LATER ReplacePersistedAsync call for
                    // the same ToolCallId is guaranteed to be chained (and therefore run) after.
                    // `ct` (this call's own caller/run token) is used ONLY to bound THIS call's
                    // own wait for that node via `WaitAsync(ct)` below — cancelling `ct` (e.g. a
                    // CancelCurrentRunAsync) cancels only this await, never the underlying store
                    // write, which continues running to completion (or failure) as part of the
                    // ordered chain and remains visible to a later terminal flush.
                    var node = EnqueueCanonicalPersistenceOperation(
                        dCt => AppendCanonicalMessageAsync(message, runId, dCt));
                    await node.WaitAsync(ct).ConfigureAwait(false);
                }
                else
                {
                    var persisted = MessagePersistenceConverter.ToPersistedMessage(message, ThreadId, runId);
                    await Store.AppendMessagesAsync(ThreadId, [persisted], ct);
                }
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
    /// </summary>
    /// <remarks>
    /// Default mode (<c>strictCanonicalPersistence: false</c>, the vast majority of callers):
    /// failures are logged and swallowed — in-memory mutation is the source of truth for the
    /// running loop; persistence becomes eventually consistent. Call timing is unchanged from
    /// before strict mode existed.
    /// Strict mode: the replacement is enqueued into the SAME per-agent ordered persistence
    /// chain used by <see cref="AddToHistory(IMessage)"/> and <see cref="AddDeferredToHistoryAsync"/>
    /// — so it always runs after the placeholder append it replaces — and a store failure
    /// propagates to this call's caller (e.g. <c>MultiTurnAgentLoop.ResolveToolCallAsync</c>)
    /// instead of being swallowed, while also remaining visible to <see cref="CompleteRunAsync"/>'s
    /// terminal flush barrier via the sticky queue fault.
    /// </remarks>
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

        if (_strictCanonicalPersistence)
        {
            // Enqueue with the per-agent durability token (see
            // `_canonicalPersistenceLifetimeCts`) — the underlying store write NEVER observes
            // `ct` (this call's own caller token, e.g. an external ResolveToolCallAsync
            // request's cancellation). `ct` is used ONLY to bound THIS call's own wait for the
            // enqueued node via `WaitAsync(ct)`: cancelling it cancels just this await — a store
            // failure still propagates synchronously to this call's caller when the wait itself
            // completes (not merely enqueues first — see `EnqueueCanonicalPersistenceOperation`),
            // per the strict-mode contract above, but a caller-side cancellation can never abort
            // the write or record a sticky `_canonicalPersistenceFault`.
            var node = EnqueueCanonicalPersistenceOperation(
                dCt => ReplaceCanonicalMessageAsync(updated, runId, dCt));
            await node.WaitAsync(ct).ConfigureAwait(false);
            return;
        }

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

    /// <summary>
    /// Raw (non-swallowing) canonical append used by the strict-mode persistence chain — see
    /// <see cref="EnqueueCanonicalPersistenceOperation"/>. Unlike <see cref="PersistMessageAsync"/>,
    /// exceptions propagate so the chain node (and the sticky <see cref="_canonicalPersistenceFault"/>
    /// it records) can see them.
    /// </summary>
    private async Task AppendCanonicalMessageAsync(IMessage message, string runId, CancellationToken ct)
    {
        if (Store == null)
        {
            return;
        }

        var persisted = MessagePersistenceConverter.ToPersistedMessage(message, ThreadId, runId);
        await Store.AppendMessagesAsync(ThreadId, [persisted], ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Raw (non-swallowing) canonical replacement used by the strict-mode persistence chain —
    /// see <see cref="EnqueueCanonicalPersistenceOperation"/> and <see cref="ReplacePersistedAsync"/>.
    /// </summary>
    private async Task ReplaceCanonicalMessageAsync(
        ToolCallResultMessage updated,
        string runId,
        CancellationToken ct)
    {
        if (Store == null)
        {
            return;
        }

        var persisted = MessagePersistenceConverter.ToPersistedMessage(updated, ThreadId, runId);
        await Store.ReplaceMessageAsync(ThreadId, persisted, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Enqueues <paramref name="operation"/> as the next node of the per-agent ordered canonical
    /// persistence chain (see <c>_canonicalPersistenceTail</c>), active only when
    /// <c>strictCanonicalPersistence</c> was enabled at construction. <paramref name="operation"/>
    /// is invoked with this agent's own durability lifetime token (see
    /// <see cref="_canonicalPersistenceLifetimeCts"/>) — NEVER a run/request caller token — so a
    /// caller-side cancellation (of a separate <c>WaitAsync</c> bound to the returned task) can
    /// never abort the underlying store write; only <see cref="DisposeAsync"/> ever cancels that
    /// token. The returned task completes (or faults) when THIS operation — and every operation
    /// enqueued before it — has run, in enqueue order, never concurrently. A failure is recorded
    /// in <see cref="_canonicalPersistenceFault"/> (sticky; first failure wins) and always
    /// observed via an immediate fire-and-forget continuation attached below, so a node whose
    /// returned task nobody ever awaits (e.g. a plain <c>AddToHistory</c> append) can never
    /// surface as an unobserved task exception — <see cref="FlushCanonicalPersistenceAsync"/> is
    /// the sanctioned way to later observe/propagate that failure. Throws
    /// <see cref="ObjectDisposedException"/> synchronously (in the common, test-hook-free path) —
    /// under <see cref="_canonicalPersistenceLock"/>, before adding any node — if this agent has
    /// been disposed. See <see cref="DisposeAsync"/> remarks: disposal marks <c>_isDisposed</c>
    /// (under <c>_replayLock</c>) and then immediately snapshots this chain's tail (under this
    /// SAME <see cref="_canonicalPersistenceLock"/>), with no intervening await, so an enqueue
    /// call can never race in a node that disposal's snapshot misses.
    /// </summary>
    private Task EnqueueCanonicalPersistenceOperation(Func<CancellationToken, Task> operation)
    {
        var preLockHook = PreCanonicalPersistenceLockHookForTests;
        return preLockHook == null
            ? EnqueueCanonicalPersistenceOperationCore(operation)
            : AwaitPreCanonicalLockHookThenEnqueueAsync(preLockHook, operation);
    }

    /// <summary>
    /// Test-only slow path (see <see cref="PreCanonicalPersistenceLockHookForTests"/>): awaits
    /// the hook, then runs the exact same <see cref="EnqueueCanonicalPersistenceOperationCore"/>
    /// a production call would run. Kept out of the common (hook-null) path so it costs nothing
    /// in production and so the production path's <see cref="ObjectDisposedException"/> stays a
    /// synchronous throw rather than surfacing only once this wrapper task is awaited/observed.
    /// </summary>
    private async Task AwaitPreCanonicalLockHookThenEnqueueAsync(
        Func<Task> preLockHook,
        Func<CancellationToken, Task> operation)
    {
        await preLockHook().ConfigureAwait(false);
        await EnqueueCanonicalPersistenceOperationCore(operation).ConfigureAwait(false);
    }

    private Task EnqueueCanonicalPersistenceOperationCore(Func<CancellationToken, Task> operation)
    {
        Task node;
        lock (_canonicalPersistenceLock)
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);

            var previous = _canonicalPersistenceTail;
            var durabilityToken = _canonicalPersistenceLifetimeCts.Token;
            node = RunCanonicalPersistenceNodeAsync(previous, operation, durabilityToken);
            _canonicalPersistenceTail = node;
        }

        _ = node.ContinueWith(
            static (t, _) => _ = t.Exception,
            null,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);

        return node;
    }

    /// <summary>
    /// One node of the canonical persistence chain — see <see cref="EnqueueCanonicalPersistenceOperation"/>.
    /// </summary>
    private async Task RunCanonicalPersistenceNodeAsync(
        Task previous,
        Func<CancellationToken, Task> operation,
        CancellationToken durabilityToken)
    {
        try
        {
            await previous.ConfigureAwait(false);
        }
        catch
        {
            // Swallowed here purely for sequencing — the previous node's own failure was
            // already recorded in `_canonicalPersistenceFault` and observed by ITS OWN
            // continuation (and, for a directly-awaited node such as a strict-mode
            // ReplacePersistedAsync call, already propagated to that call's caller too).
        }

        try
        {
            await operation(durabilityToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (durabilityToken.IsCancellationRequested)
        {
            // Disposal cancelled the durability lifetime token (see DisposeAsync) to unblock a
            // permanently gated write — an expected shutdown signal, not a store failure, so it
            // must NEVER poison `_canonicalPersistenceFault`: doing so would permanently brick a
            // still-live agent's future CompleteRunAsync flush over something disposal itself
            // caused. Still rethrown so THIS node's own await (if any, e.g. a strict-mode
            // ReplacePersistedAsync call already unwound by its caller's own `ct`) observes it.
            throw;
        }
        catch (Exception ex)
        {
            _ = Interlocked.CompareExchange(ref _canonicalPersistenceFault, ex, null);
            throw;
        }
    }

    /// <summary>
    /// Awaits every canonical append/replacement enqueued so far on the strict-mode persistence
    /// chain, then throws if any of them (or any earlier one already folded into the sticky
    /// fault) failed. A no-op when strict mode is disabled. Called by <see cref="CompleteRunAsync"/>
    /// BEFORE its terminal run-ledger write/<see cref="Messages.RunCompletedMessage"/> publication —
    /// see that method's remarks — so a canonical-history durability failure fails closed: no
    /// terminal success and no terminal message, on either the natural or the error-completion path.
    /// </summary>
    private async Task FlushCanonicalPersistenceAsync(CancellationToken ct)
    {
        if (!_strictCanonicalPersistence)
        {
            return;
        }

        Task tail;
        lock (_canonicalPersistenceLock)
        {
            tail = _canonicalPersistenceTail;
        }

        try
        {
            await tail.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // The specific failing operation's exception is surfaced via the sticky fault
            // below (which may differ from `tail`'s own exception if a LATER node in the
            // chain happened to succeed on its own operation after an earlier one failed).
        }

        var fault = Volatile.Read(ref _canonicalPersistenceFault);
        if (fault != null)
        {
            throw new InvalidOperationException(
                "Canonical child persistence failed for one or more queued appends/replacements; "
                    + "terminal run visibility cannot be published while durability is broken.",
                fault);
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
    /// Publish a message to all subscribers. The publication kind used for the optional
    /// <see cref="IAgentPublicationObserver"/> is inferred purely from <paramref name="message"/>'s
    /// type (<see cref="RunAssignmentMessage"/> → <see cref="AgentPublicationKind.RunAssignment"/>,
    /// <see cref="RunCompletedMessage"/> → <see cref="AgentPublicationKind.RunTerminal"/>, otherwise
    /// <see cref="AgentPublicationKind.Message"/>). Use the explicit-kind overload when the message
    /// TYPE alone cannot disambiguate intent — e.g. a deferred tool call's resolution reuses
    /// <see cref="ToolCallResultMessage"/>, which would otherwise infer as an ordinary message.
    /// </summary>
    /// <param name="message">The message to publish</param>
    /// <param name="ct">Cancellation token</param>
    protected ValueTask PublishToAllAsync(IMessage message, CancellationToken ct) =>
        PublishToAllAsync(message, InferPublicationKind(message), ct);

    /// <summary>
    /// Publish a message to all subscribers with an explicit <see cref="AgentPublicationKind"/> for
    /// the optional <see cref="IAgentPublicationObserver"/>. v1 subscriber fan-out (payload,
    /// instance identity, order, non-blocking delivery) is IDENTICAL regardless of whether an
    /// observer is configured or which kind is passed — <paramref name="kind"/> only affects what a
    /// configured observer is told. When an observer is configured, the publishing caller AWAITS it
    /// (see <see cref="IAgentPublicationObserver"/>); an observer exception propagates to that
    /// caller (the run) rather than being swallowed.
    /// </summary>
    /// <param name="message">The message to publish</param>
    /// <param name="kind">The publication kind to report to a configured observer.</param>
    /// <param name="ct">Cancellation token</param>
    protected ValueTask PublishToAllAsync(IMessage message, AgentPublicationKind kind, CancellationToken ct)
    {
        // Deliberately unused here — see IAgentPublicationObserver remarks: the observer's
        // cancellation scope is this agent's own lifetime (_observerLifetimeCts), never the
        // publishing caller's token, and v1 fan-out below has never used it either.
        _ = ct;

        var prePublishHook = PrePublishLockHookForTests;
        return prePublishHook == null
            ? PublishToAllAsyncCore(message, kind)
            : AwaitPrePublishHookThenPublishAsync(message, kind, prePublishHook);
    }

    /// <summary>
    /// Test-only slow path (see <see cref="PrePublishLockHookForTests"/>): awaits the hook, then
    /// runs the exact same <see cref="PublishToAllAsyncCore"/> a production call would run. Kept
    /// out of the common (hook-null) path so it costs nothing in production.
    /// </summary>
    private async ValueTask AwaitPrePublishHookThenPublishAsync(
        IMessage message,
        AgentPublicationKind kind,
        Func<Task> prePublishHook)
    {
        await prePublishHook().ConfigureAwait(false);
        await PublishToAllAsyncCore(message, kind).ConfigureAwait(false);
    }

    /// <summary>
    /// Core publish implementation shared by every <see cref="PublishToAllAsync(IMessage, CancellationToken)"/>
    /// call. Throws <see cref="ObjectDisposedException"/> synchronously — under `_replayLock`, before
    /// ANY replay-buffer mutation, subscriber fan-out, or observer dispatch-node creation — if this
    /// agent has been disposed. See `_replayLock`/DisposeAsync remarks: disposal marks `_isDisposed`
    /// under this SAME lock, atomically with snapshotting the observer dispatch chain's tail, so a
    /// publish call can never observe "not yet disposed" here and then append a node that
    /// disposal's tail snapshot missed.
    /// </summary>
    private ValueTask PublishToAllAsyncCore(IMessage message, AgentPublicationKind kind)
    {
        List<KeyValuePair<string, Channel<IMessage>>> targets;

        // Populated only when an observer is configured — see the ordered-dispatch remarks on
        // `_observerDispatchTail`. `readyTcs` is signalled by THIS call once its v1 fan-out below
        // is done; `observerTask` is the dispatch node THIS call awaits (its own publication's
        // observer completion) — never the whole chain, so one publication's observer failure only
        // faults its own caller.
        AgentPublication? publication = null;
        TaskCompletionSource? readyTcs = null;
        Task? observerTask = null;

        lock (_replayLock)
        {
            // Publish-vs-DisposeAsync TOCTOU fix (WI #194 quality finding): checked as the FIRST
            // thing under `_replayLock` — the SAME lock DisposeAsync marks `_isDisposed` under
            // while snapshotting the observer dispatch chain's tail — so a call that reaches here
            // strictly before disposal is guaranteed to finish its buffer mutation/fan-out/node
            // creation and be included in that snapshot, and a call that reaches here at or after
            // disposal fails HERE, predictably, before touching the replay buffer, subscribers, or
            // the observer dispatch chain at all.
            ObjectDisposedException.ThrowIf(_isDisposed, this);

            // Maintain the in-flight run's replay buffer. A RunAssignmentMessage for a NEW run opens a
            // fresh buffer; RunCompletedMessage closes it (after which a joining subscriber must NOT
            // replay it — the client already has completed messages via persisted REST history, so
            // replaying would duplicate). A same-run injection assignment (WasInjected, same RunId — e.g.
            // an out-of-band NotifyMessage folded into the active run) must NOT clear the buffer, or a
            // client reconnecting after the injection would lose the run's earlier deltas. Snapshotting
            // subscribers under the SAME lock SubscribeAsync uses to register + snapshot the buffer
            // guarantees this message reaches each subscriber exactly once (replay XOR live).
            if (message is RunAssignmentMessage ram)
            {
                var incomingRunId = ram.Assignment?.RunId;
                if (!_replayRunActive || !string.Equals(incomingRunId, _replayRunId, StringComparison.Ordinal))
                {
                    _replayBuffer.Clear();
                    _replayBufferBytes = 0;
                    _replayRunActive = true;
                    _replayBufferTruncated = false;
                    _replayRunId = incomingRunId;
                }
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

            // Assign this publication's monotonic Sequence and create its dispatch node HERE
            // (still under `_replayLock`), so Sequence order == lock-acquisition order exactly.
            // Creating the node starts DispatchToObserverAsync synchronously, but its first await
            // (on the previous node / the not-yet-signalled `readyTcs` below) is on an incomplete
            // Task, so it suspends and returns immediately — this never blocks while holding the
            // lock, i.e. no monitor is held across a genuine await.
            if (_publicationObserver != null)
            {
                publication = new AgentPublication(ThreadId, message, kind, ++_publicationSequence);
                readyTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                // Capture the observer's token NOW, synchronously, while still holding
                // `_replayLock` — the `ObjectDisposedException.ThrowIf` above guarantees
                // `_observerLifetimeCts` cannot have been disposed yet (disposal only disposes it
                // AFTER taking this same lock to mark `_isDisposed` and snapshot the tail — see
                // DisposeAsync). This is read once here rather than lazily inside
                // DispatchToObserverAsync's continuation, so a dispatch node can never race a
                // disposed CTS and surface an unpredictable ObjectDisposedException from the CTS
                // itself instead of the documented one thrown above.
                var observerToken = _observerLifetimeCts.Token;
                observerTask = DispatchToObserverAsync(_observerDispatchTail, readyTcs.Task, publication, observerToken);
                _observerDispatchTail = observerTask;
            }
        }

        foreach (var (subscriberId, channel) in targets)
        {
            PublishToSubscriber(subscriberId, channel, message);
        }

        // v1 fan-out above is already complete (non-blocking) regardless of what follows.
        if (_publicationObserver == null)
        {
            return ValueTask.CompletedTask;
        }

        return new ValueTask(SignalReadyAndAwaitObserverAsync(publication!, readyTcs!, observerTask!));
    }

    /// <summary>
    /// Runs after this call's v1 fan-out is complete: optionally waits for the test-only fan-out
    /// delay hook, then signals this publication's dispatch node "ready" (see
    /// `_observerDispatchTail`), and finally awaits THAT node — so this method's caller (the
    /// publisher) sees an exception if and only if ITS OWN publication's observer call failed,
    /// never a different (earlier or later) publication's failure.
    /// </summary>
    private async Task SignalReadyAndAwaitObserverAsync(
        AgentPublication publication,
        TaskCompletionSource readyTcs,
        Task observerTask)
    {
        try
        {
            var delayHook = FanOutReadyDelayHookForTests;
            if (delayHook != null)
            {
                await delayHook(publication).ConfigureAwait(false);
            }
        }
        finally
        {
            // Always signal "ready" — even if the test-only hook above threw — so this
            // publication's dispatch node (and therefore every LATER publication's node chained
            // behind it) can never hang/poison waiting on a "ready" signal this call failed to
            // send. A hook exception still propagates to THIS publish call's own caller below,
            // exactly as before this fix; it just no longer blocks the chain from making progress.
            readyTcs.SetResult();
        }

        await observerTask.ConfigureAwait(false);
    }

    /// <summary>
    /// One node in the per-agent observer dispatch chain (see `_observerDispatchTail`). Awaits the
    /// PREVIOUS node first — swallowing its exception, since that node's own publishing caller
    /// already observed/propagated it — so observer calls start in Sequence order and never
    /// concurrently, then awaits THIS publication's "v1 fan-out done, ready to observe" signal
    /// before finally invoking the observer with <paramref name="observerToken"/> — this agent's
    /// OWN cancellation scope (`_observerLifetimeCts`), captured by the caller BEFORE this node was
    /// even created (see `PublishToAllAsyncCore`) rather than read lazily from
    /// `_observerLifetimeCts.Token` here, so this node can never race a disposed CTS — never the
    /// publishing caller's `ct` — see <see cref="IAgentPublicationObserver"/> remarks.
    /// </summary>
    private async Task DispatchToObserverAsync(
        Task previousNode,
        Task ready,
        AgentPublication publication,
        CancellationToken observerToken)
    {
        try
        {
            await previousNode.ConfigureAwait(false);
        }
        catch
        {
            // Swallowed here so ONE publication's observer failure cannot poison delivery to this
            // (later) publication — the earlier publication's own publishing caller already saw
            // (and propagated) this exception via its own dispatch node.
        }

        await ready.ConfigureAwait(false);

        await _publicationObserver!.OnPublishedAsync(publication, observerToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Infers the <see cref="AgentPublicationKind"/> for a publication whose caller did not
    /// explicitly specify one — purely from <paramref name="message"/>'s type, so this never needs
    /// updating at existing <see cref="RunAssignmentMessage"/>/<see cref="RunCompletedMessage"/>
    /// publish call sites.
    /// </summary>
    private static AgentPublicationKind InferPublicationKind(IMessage message) => message switch
    {
        RunAssignmentMessage => AgentPublicationKind.RunAssignment,
        RunCompletedMessage => AgentPublicationKind.RunTerminal,
        _ => AgentPublicationKind.Message,
    };

    /// <summary>
    /// Delivers <paramref name="message"/> to one subscriber WITHOUT ever blocking the publisher.
    /// A bounded per-subscriber channel means a slow/stalled consumer (classically a reconnecting
    /// client still draining the in-flight run's replay buffer) can fill its channel; awaiting the
    /// write there would put that consumer on the live run's hot path and let it backpressure the
    /// active run and every other subscriber. So we write non-blocking and, when
    /// the channel is full, DROP the subscriber: remove it from the fan-out and complete its channel
    /// so its <see cref="SubscribeAsync"/> enumerator ends. The client can reconnect; resume replays
    /// the in-flight run from the buffer. A reconnecting replay consumer can therefore never block
    /// <see cref="PublishToAllAsync(IMessage, CancellationToken)"/>.
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
        // Publish-vs-DisposeAsync TOCTOU fix (WI #194 quality finding): mark `_isDisposed` and
        // snapshot the observer dispatch chain's tail atomically, under the SAME `_replayLock`
        // `PublishToAllAsyncCore` checks `_isDisposed` under and creates dispatch nodes under.
        // This guarantees the tail snapshotted here is the LAST dispatch node any publication
        // could ever append: a publish call whose lock acquisition happens strictly before this
        // one already finished mutating the replay buffer/appending its node before releasing the
        // lock (so it's covered by the snapshot below); a publish call whose lock acquisition
        // happens at or after this one observes `_isDisposed` already true and fails there,
        // predictably, with ObjectDisposedException, before touching the buffer, subscribers, or
        // the observer dispatch chain at all — never both, never neither.
        Task tailSnapshot;
        lock (_replayLock)
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            tailSnapshot = _observerDispatchTail;
        }

        // Enqueue-vs-disposal TOCTOU fix (see EnqueueCanonicalPersistenceOperation's own
        // `ObjectDisposedException.ThrowIf(_isDisposed, this)` remarks): snapshot the canonical
        // persistence chain's tail immediately — no await in between — under the SAME
        // `_canonicalPersistenceLock` every enqueue call takes. `_isDisposed` was just set above
        // with no intervening await, so this is effectively atomic with that write: an enqueue
        // call whose lock acquisition happens strictly before this one already finished adding
        // its node (covered by the snapshot below); one that acquires the lock at or after this
        // point observes `_isDisposed` already true and fails there, predictably, with
        // ObjectDisposedException, before adding a node at all — never both, never neither.
        Task canonicalPersistenceTailSnapshot;
        lock (_canonicalPersistenceLock)
        {
            canonicalPersistenceTailSnapshot = _canonicalPersistenceTail;
        }

        await StopAsync();

        _internalCts?.Dispose();

        // End the optional publication observer's durability scope (see IAgentPublicationObserver
        // remarks and `_observerDispatchTail`): cancel it FIRST so any observer call already in
        // flight is asked to stop promptly, then bound-await the tail snapshotted above so
        // disposal can never leak/hang on it — a still-running/faulted tail after the timeout is
        // abandoned (best-effort; the cancellation above already asked a well-behaved observer to
        // stop).
        await _observerLifetimeCts.CancelAsync();
        try
        {
            await tailSnapshot.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch
        {
            // Best-effort — see remarks above.
        }

        _observerLifetimeCts.Dispose();

        // Bound-await the strict-mode canonical persistence chain (see
        // `_canonicalPersistenceTail`/`EnqueueCanonicalPersistenceOperation`), using the SAME
        // cancel-first-then-drain pattern as the observer teardown above. Cancelling
        // `_canonicalPersistenceLifetimeCts` FIRST asks any write still gated on a slow/hung
        // store call to stop promptly (see that token's remarks: it is the ONLY thing that ever
        // cancels a canonical store write) — a resulting `OperationCanceledException` is NOT
        // recorded as a sticky `_canonicalPersistenceFault` (see
        // `RunCanonicalPersistenceNodeAsync`), so disposal can never itself brick a still-live
        // agent's durability guarantee. The tail was already snapshotted above (before
        // `_isDisposed` could race any enqueue), so no further node can ever be appended to it.
        // A still-running/faulted tail after the timeout is abandoned (best-effort; the
        // cancellation above already asked a well-behaved store call to stop).
        await _canonicalPersistenceLifetimeCts.CancelAsync();
        try
        {
            await canonicalPersistenceTailSnapshot.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch
        {
            // Best-effort — see remarks above.
        }

        _canonicalPersistenceLifetimeCts.Dispose();

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

        // Each run gets its own CTS, linked to (but independently cancellable from) the loop
        // token passed in here — see CurrentRunToken/CancelCurrentRunAsync. Created under the
        // same lock as _currentRunId so CancelCurrentRunAsync always observes a matching pair.
        // Defensively dispose any leftover CTS from a prior run that a caller failed to clear
        // via CompleteRunAsync (should not happen in practice — RunLoopAsync is single-threaded
        // per agent — but avoids leaking a CTS if it ever does).
        var runCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        CancellationTokenSource? staleRunCts;
        lock (_stateLock)
        {
            parentRunId ??= _latestRunId;
            _currentRunId = runId;
            staleRunCts = _currentRunCts;
            _currentRunCts = runCts;
        }

        staleRunCts?.Dispose();

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
    /// after completion was broadcast).
    /// </summary>
    /// <remarks>
    /// Exactly-once terminal arbiter, bounded to the CURRENT run only (<c>_currentRunTerminalClaimed</c>
    /// under <c>_stateLock</c> — see the field comment): the FIRST call for the still-current
    /// <paramref name="runId"/> claims it and proceeds; a later call for a <paramref name="runId"/>
    /// that is no longer current (already resolved and cleared) — e.g. a matching
    /// <c>CancelCurrentRunAsync</c> racing this run's own natural/error completion — is a no-op
    /// that touches neither the ledger nor subscribers. If the claiming call's own terminal-ledger
    /// write throws, the claim is released (while the run is still current) before the exception
    /// propagates, so a caller's retry (e.g. an error-completion fallback after a failed natural
    /// completion) is NOT permanently suppressed and can still durably persist/publish exactly one
    /// terminal outcome. Combined with <see cref="CancelCurrentRunAsync"/> checking the same claim
    /// under the same lock before accepting a Stop, at most one terminal outcome is ever durably
    /// recorded or published for a given run.
    /// </remarks>
    /// <param name="runId">The run ID that completed</param>
    /// <param name="generationId">The generation ID</param>
    /// <param name="wasForked">Whether the run was forked due to new input</param>
    /// <param name="forkedToRunId">The run ID that was forked to (if applicable)</param>
    /// <param name="pendingMessageCount">Number of pending message batches waiting to be processed</param>
    /// <param name="isError">Whether the run completed due to an error</param>
    /// <param name="errorMessage">Error message when isError is true</param>
    /// <param name="isCancelled">
    /// Whether the run ended because a matching <see cref="CancelCurrentRunAsync"/> request was
    /// accepted for it. Mutually exclusive with <paramref name="isError"/> in practice (a run's
    /// per-run wrapper picks exactly one terminal branch).
    /// </param>
    /// <param name="ct">Cancellation token</param>
    /// <remarks>
    /// When this agent was constructed with <c>strictCanonicalPersistence: true</c>, this method
    /// first awaits <see cref="FlushCanonicalPersistenceAsync"/> — every queued canonical
    /// append/replacement — before the terminal run-ledger write or
    /// <see cref="Messages.RunCompletedMessage"/> publication below. A queued-write failure makes
    /// the flush throw, which this method's own <c>catch</c> below treats exactly like a
    /// ledger-write or publish failure: the terminal claim is released (so a caller's
    /// error-completion retry — e.g. <c>MultiTurnAgentLoop.RunLoopAsync</c>'s natural-then-error
    /// fallback — can still attempt its own terminal outcome) and the exception propagates,
    /// meaning this call durably persists/publishes NEITHER the run-ledger terminal status NOR a
    /// terminal message. That failing flush check is unconditional on this same broken agent —
    /// a subsequent error-completion retry for the same run flushes (and fails) again, so an
    /// error terminal can never claim durable success while canonical persistence remains broken.
    /// Default (<c>strictCanonicalPersistence: false</c>) mode is unaffected — the flush is a
    /// no-op and this method's existing best-effort behavior/timing is unchanged.
    /// </remarks>
    protected async Task CompleteRunAsync(
        string runId,
        string generationId,
        bool wasForked = false,
        string? forkedToRunId = null,
        int pendingMessageCount = 0,
        bool isError = false,
        string? errorMessage = null,
        bool isCancelled = false,
        CancellationToken ct = default)
    {
        lock (_stateLock)
        {
            if (!string.Equals(_currentRunId, runId, StringComparison.Ordinal) || _currentRunTerminalClaimed)
            {
                Logger.LogWarning(
                    "Duplicate terminal completion attempt for run {RunId} (isCancelled: {IsCancelled}, isError: {IsError}); ignoring — exactly-once terminal arbiter already resolved this run",
                    runId,
                    isCancelled,
                    isError);
                return;
            }

            _currentRunTerminalClaimed = true;
        }

        try
        {
            // Canonical durability comes first: no terminal ledger write or RunCompletedMessage
            // is ever published while a queued canonical append/replacement has failed — see
            // this method's remarks and FlushCanonicalPersistenceAsync.
            await FlushCanonicalPersistenceAsync(ct);

            if (RunLedgerStore != null)
            {
                var existing = await RunLedgerStore.LoadRunLedgerAsync(runId, ct);
                if (existing != null)
                {
                    var status = isCancelled ? RunStatus.Cancelled : isError ? RunStatus.Errored : RunStatus.Completed;
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
                IsCancelled = isCancelled,
            }, ct);
        }
        catch
        {
            // Release the claim (only if this run is still the current one — a concurrent
            // successful completion may already have cleared it) so a retry for the SAME run —
            // e.g. the caller's error-completion fallback after this failed natural/error
            // completion attempt — is not permanently orphaned by a claim nothing will ever
            // release.
            lock (_stateLock)
            {
                if (string.Equals(_currentRunId, runId, StringComparison.Ordinal))
                {
                    _currentRunTerminalClaimed = false;
                }
            }

            throw;
        }

        CancellationTokenSource? runCtsToDispose;
        lock (_stateLock)
        {
            _latestRunId = runId;
            _currentRunId = null;
            _currentRunTerminalClaimed = false;
            runCtsToDispose = _currentRunCts;
            _currentRunCts = null;
        }

        // Dispose OUTSIDE the lock — Dispose() can run registered callbacks synchronously.
        runCtsToDispose?.Dispose();

        if (isCancelled)
        {
            Logger.LogInformation("Run {RunId} cancelled via expected-run Stop", runId);
        }
        else if (isError)
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

    /// <inheritdoc />
    public Task<RunCancellationResult> CancelCurrentRunAsync(string expectedRunId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(expectedRunId);

        CancellationTokenSource cts;
        lock (_stateLock)
        {
            if (_currentRunId == null || _currentRunCts == null)
            {
                return Task.FromResult(RunCancellationResult.NoActiveRun);
            }

            if (!string.Equals(_currentRunId, expectedRunId, StringComparison.Ordinal))
            {
                Logger.LogInformation(
                    "CancelCurrentRunAsync: expected run {ExpectedRunId} is stale — current run is {CurrentRunId}",
                    expectedRunId,
                    _currentRunId);
                return Task.FromResult(RunCancellationResult.StaleRun);
            }

            if (_currentRunTerminalClaimed)
            {
                // Natural/error completion has already claimed this run's terminal outcome
                // (CompleteRunAsync acquired _stateLock first) — it is terminalizing right now
                // and Stop can no longer influence the outcome. Reporting Accepted here would be
                // a lie: the durable/published outcome will NOT be Cancelled. Report NoActiveRun
                // instead, exactly as if the run had already finished (which, from the caller's
                // perspective, it effectively has).
                Logger.LogInformation(
                    "CancelCurrentRunAsync: run {RunId} is already terminalizing via natural/error completion; rejecting Stop",
                    expectedRunId);
                return Task.FromResult(RunCancellationResult.NoActiveRun);
            }

            cts = _currentRunCts;
        }

        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The run reached its terminal outcome (and disposed its CTS in CompleteRunAsync)
            // between the lock above and this call — treat as "already gone", not an error.
            return Task.FromResult(RunCancellationResult.NoActiveRun);
        }

        Logger.LogInformation("CancelCurrentRunAsync: cancelled run {RunId}", expectedRunId);
        return Task.FromResult(RunCancellationResult.Accepted);
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
