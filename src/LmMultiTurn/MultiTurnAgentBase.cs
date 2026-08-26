using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading.Channels;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Models;
using AchieveAi.LmDotnetTools.LmLifecycle;
using AchieveAi.LmDotnetTools.LmLifecycle.Payloads;
using AchieveAi.LmDotnetTools.LmMultiTurn.Delivery;
using AchieveAi.LmDotnetTools.LmMultiTurn.Lifecycle;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using AchieveAi.LmDotnetTools.LmMultiTurn.UsageAccounting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AchieveAi.LmDotnetTools.LmMultiTurn;

/// <summary>
/// Abstract base class for multi-turn agents providing common infrastructure for
/// channel management, subscription handling, and lifecycle management.
/// </summary>
public abstract class MultiTurnAgentBase : IMultiTurnAgent, IAcceptanceReportingAgent
{
    #region Fields

    /// <summary>
    /// Per-turn output-budget floor applied when a loop is constructed without an explicit
    /// <see cref="GenerateReplyOptions.MaxToken"/>. Chosen to match the main agent's own budget so
    /// sub-agents and workflow-controller loops get the same headroom instead of the provider's raw
    /// 4096 default, which truncates tool-call argument JSON at <c>stop_reason=max_tokens</c>. Filling a
    /// null budget only; any explicit MaxToken (including a smaller one) is preserved.
    /// </summary>
    internal const int DefaultMaxTokenFloor = 8192;

    private readonly int _outputChannelCapacity;
    private readonly int _inputChannelCapacity;

    // Channels - _inputChannel is recreatable to support restart
    private Channel<QueuedInput> _inputChannel;
    private readonly object _channelLock = new();
    private readonly ConcurrentDictionary<string, Subscriber> _outputSubscribers = new();

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
    private string? _replayGenerationId;
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

    // Cancelled once, at disposal. Distinct from _internalCts, which is recreated by every
    // RunAsync and cancelled by every StopAsync: work that belongs to the agent rather than to one
    // incarnation of its loop — an internal enqueue that must not be abandoned because whichever
    // caller happened to trigger it cancelled its own token — hangs off this instead.
    private readonly CancellationTokenSource _lifetimeCts = new();

    // Set once history has been (attempted to be) recovered from the store, so RunAsync's
    // startup recovery and any explicit RecoverAsync call never double-restore (RestoreHistory
    // appends). Guards the "recover persisted history on (re)create" path used by the agent pool.
    private bool _historyRecovered;
    private bool _usageHydrated;

    // Serialized/coalesced durable-usage writer, created lazily once both a ledger and store exist. Both
    // the primary loop's own usage and every descendant relay route through it, so writes never interleave
    // and run completion / disposal can await a final flush (#196).
    private UsagePersistenceWriter? _usageWriter;
    private readonly object _usageWriterLock = new();

    // Terminal completeness stamped on the persisted usage aggregate (#196). InProgress while the loop is
    // live; advanced to Complete on a clean terminal flush, or Partial when the run faulted so some incurred
    // usage may not have been captured. Guarded by _usageWriterLock; the writer delegate reads it at write
    // time so the terminal flush persists the terminal state. Merged monotonically by
    // ConversationUsageProjection.MaxCompleteness, so a stale InProgress write can never regress it.
    private UsageCompleteness _usageCompleteness = UsageCompleteness.InProgress;
    private volatile bool _isDisposed;

    // The single teardown every DisposeAsync caller awaits. Assigned under _replayLock together with
    // _isDisposed, so disposal is both idempotent AND awaitable: a second caller can never return
    // "disposed" while the first teardown is still mid-flight.
    private Task? _disposeTask;

    // Set once run-ledger reconciliation has run for this process instance, so RunAsync never
    // re-reconciles on an explicit restart within the same process (only a genuine new process
    // start should treat prior Queued/InProgress rows as dangling).
    private bool _runLedgerReconciled;

    // The same once-per-process guard for lifecycle runs, kept separate because lifecycle
    // persistence and run-ledger persistence are configured independently.
    private bool _lifecycleReconciled;

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
    /// Cancelled when the agent is disposed, and at no other time.
    /// </summary>
    /// <remarks>
    /// For internal work whose lifetime is the agent's, not one caller's and not one run's: an
    /// enqueue the loop owes itself must not be abandoned because the arbitrary thread that
    /// triggered it — a webhook handler, a UI callback — cancelled the token it happened to pass
    /// in. Stays valid after disposal has cancelled it: an already-cancelled token is exactly what
    /// such work should see — which is also why it is captured at construction rather than read
    /// from the source on demand: a disposed source refuses to hand out its token, while the token
    /// itself keeps working and correctly reports cancellation.
    /// </remarks>
    protected CancellationToken LifetimeToken { get; }

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
    /// Conversation-wide usage ledger (#196). Set by a derived loop when usage accounting is enabled; it
    /// is shared with the loop's <c>SubAgentManager</c> so the primary loop's own usage and every
    /// descendant's usage accumulate into one root total. Null disables usage accounting.
    /// </summary>
    protected UsageLedger? UsageLedger { get; set; }

    /// <summary>
    /// Non-null only when the constructor's <c>persistRunLedger</c> flag is set, in which case
    /// <see cref="Store"/> is guaranteed to also implement this interface. All run-ledger
    /// durability (atomic mint+queue write, InProgress/terminal transitions, injected-input
    /// folding, and restart reconciliation) is gated on this being non-null, so it — not a
    /// separate bool — is the single source of truth for whether run-ledger persistence is on.
    /// </summary>
    protected IRunLedgerStore? RunLedgerStore { get; }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Reported to from the two send methods below and nowhere else, because those two are where an
    /// accept's receipt id is minted on the public send path — so every accept that arrives that way
    /// is covered by construction, including the ones that reach a pooled agent from inside this
    /// assembly and could never call a host's pool directly (a sub-agent relaying a descendant's
    /// question to its parent, a sub-agent completion notification, a peer's collaboration message).
    /// A derived loop's internal raw enqueues bypass both mint sites and this observer: the loop wake
    /// sentinel (inert — empty payload, no run content, nothing to record) and the trigger notify (a
    /// real turn, and so genuinely unobserved, but unreachable in this repository's host — it is
    /// gated behind trigger options that only test mode supplies here, and #161 tracks enabling it. A
    /// host outside this repository that enables triggers DOES reach it, and its notify turns are not
    /// covered by this observer).
    /// </para>
    /// <para>
    /// A throwing observer FAILS THE SEND. It is reported to after the durable accepted-input write
    /// and before the channel write, so a throw leaves nothing enqueued and no acceptance recorded
    /// anywhere — the caller gets an error for a turn that genuinely was not taken. Swallowing here
    /// would produce the opposite and much worse outcome: the input sitting in the channel with the
    /// host believing the agent idle, which is the released-with-work-queued case this whole
    /// mechanism exists to prevent.
    /// </para>
    /// </remarks>
    public IInputAcceptanceObserver? InputAcceptanceObserver { get; set; }

    /// <summary>
    /// What the host wired up for lifecycle observation and tool approval.
    /// <see cref="MultiTurnLifecycleServices.Disabled"/> when the host wired up nothing.
    /// </summary>
    /// <remarks>
    /// Subclasses read this to publish the events only they can produce — a provider loop's
    /// per-turn usage, a sandbox-backed loop's session creation — and to derive a spawned
    /// agent's bundle with <c>with { Lineage = ... }</c>.
    /// </remarks>
    protected MultiTurnLifecycleServices LifecycleServices { get; }

    /// <summary>
    /// Owns this thread's run and turn lifecycle: which run is in flight, which caller ends it,
    /// and the events that go out when one starts, turns, or completes.
    /// </summary>
    /// <remarks>
    /// Inert unless the constructor received a bundle that enables something, so subclasses can
    /// call it unconditionally.
    /// </remarks>
    protected RunTurnLifecycleFinalizer Lifecycle { get; }

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
    /// (which must then also implement <see cref="IRunLedgerStore"/>) — enables
    /// <see cref="TrySendAsync(UserInput, CancellationToken)"/>
    /// and restart reconciliation. Default false preserves existing in-memory-only behavior.
    /// </param>
    /// <param name="lifecycleServices">
    /// Lifecycle observation and tool approval for this agent. Omit — or pass
    /// <see cref="MultiTurnLifecycleServices.Disabled"/> — and the loop behaves exactly as it did
    /// before lifecycle hooks existed: nothing is published, nothing extra is persisted, and no
    /// tool call is gated.
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
        MultiTurnLifecycleServices? lifecycleServices = null)
    {
        ArgumentNullException.ThrowIfNull(threadId);

        LifetimeToken = _lifetimeCts.Token;
        ThreadId = threadId;
        SystemPrompt = systemPrompt;
        MaxTurnsPerRun = maxTurnsPerRun;
        _inputChannelCapacity = inputChannelCapacity;
        _outputChannelCapacity = outputChannelCapacity;
        _maxReplayBufferSize = maxReplayBufferSize;
        _maxReplayBufferBytes = maxReplayBufferBytes;

        // Per-turn output-budget floor. When no MaxToken is configured, the provider falls back to its
        // raw 4096 default (AnthropicRequest: MaxTokens = options?.MaxToken ?? 4096). A single turn that
        // emits a real file body (Write.content) or script (Bash.command) as a tool_use argument then
        // exhausts that budget: the provider stops with stop_reason=max_tokens and truncates the streaming
        // tool-call JSON mid-string, so the loop executes corrupt args. The main agent already dodges this
        // by setting MaxToken explicitly (8192); sub-agents and the workflow-controller loop are built with
        // options carrying only a model id, so they inherited the 4096 ceiling and their Write/Bash calls
        // consistently failed. Filling ONLY a null MaxToken here is non-breaking: any explicit budget
        // (including the main agent's) is preserved, and it never touches ModelId (empty ModelId still lets
        // the provider pick its default model — this sets budget only, never clobbers model selection).
        var baseOptions = defaultOptions ?? new GenerateReplyOptions();
        DefaultOptions = baseOptions.MaxToken is null
            ? baseOptions with { MaxToken = DefaultMaxTokenFloor }
            : baseOptions;
        Store = store;
        Logger = logger ?? NullLogger.Instance;

        if (persistRunLedger)
        {
            RunLedgerStore = store as IRunLedgerStore
                ?? throw new ArgumentException(
                    $"{nameof(persistRunLedger)} is true but {nameof(store)} is null or does not implement {nameof(IRunLedgerStore)}.",
                    nameof(store));
        }

        LifecycleServices = lifecycleServices ?? MultiTurnLifecycleServices.Disabled;

        // The conversation store doubles as the lifecycle store when it can, but only for a host
        // that actually asked for lifecycle — persisting to SQLite must not by itself start writing
        // run_lifecycle rows.
        Lifecycle = new RunTurnLifecycleFinalizer(
            threadId,
            LifecycleServices,
            store as IRunLifecycleStore,
            Logger);

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

        // Capture the primary loop's own usage into the conversation-wide ledger (#196). Descendant
        // usage is folded in separately via the SubAgentManager relay into the same ledger instance.
        if (UsageLedger != null && message is UsageMessage usageMessage)
        {
            CaptureAndPersistUsage(usageMessage);
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
                _ = PersistMessageAsync(message, runId, CancellationToken.None);
            }
        }
    }

    /// <summary>
    /// Records the primary loop's usage into the conversation-wide ledger and, when a store is configured,
    /// persists the updated aggregate snapshot (fire-and-forget). The snapshot reflects both this primary
    /// usage and any descendant usage already folded in via the SubAgentManager relay (#196).
    /// </summary>
    private void CaptureAndPersistUsage(UsageMessage usageMessage)
    {
        var ledger = UsageLedger;
        if (ledger is null)
        {
            return;
        }

        var record = UsageRecordMapper.FromUsageMessage(
            usageMessage,
            ThreadId,
            UsageExecutionKind.Primary,
            DefaultOptions.ModelId);
        ledger.RecordUsage(record);

        EnsureUsageWriter()?.Schedule();
    }

    /// <summary>
    /// Schedules a durable write of the current usage ledger snapshot + records for the root conversation
    /// through the serialized writer. Handed to the SubAgentManager so a descendant's usage is made durable
    /// when it is observed — including a late/background descendant that finishes after the root's last
    /// provider call — rather than waiting for a future primary usage event to flush it. Coalesced with the
    /// primary loop's own writes so the two paths cannot interleave or race an older snapshot (#196).
    /// </summary>
    protected Task PersistCurrentUsageAsync()
    {
        EnsureUsageWriter()?.Schedule();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Lazily creates the per-conversation serialized usage writer once both a ledger and store exist. The
    /// persist delegate reads the ledger's latest snapshot at write time, so coalesced writes always persist
    /// the newest state. Returns null when usage accounting is not configured for this agent.
    /// </summary>
    private UsagePersistenceWriter? EnsureUsageWriter()
    {
        var existing = _usageWriter;
        if (existing != null)
        {
            return existing;
        }

        var ledger = UsageLedger;
        var store = Store;
        if (ledger is null || store is null)
        {
            return null;
        }

        lock (_usageWriterLock)
        {
            return _usageWriter ??= new UsagePersistenceWriter(
                    ct => ConversationUsageProjection.SaveAsync(store, ledger.Snapshot(CurrentUsageCompleteness), ledger.SnapshotRecords(), ct),
                    onError: ex => Logger.LogWarning(ex, "Failed to persist usage snapshot for thread {ThreadId}", ThreadId));
        }
    }

    /// <summary>Current terminal-completeness state to stamp on the persisted aggregate (thread-safe read).</summary>
    private UsageCompleteness CurrentUsageCompleteness
    {
        get
        {
            lock (_usageWriterLock)
            {
                return _usageCompleteness;
            }
        }
    }

    /// <summary>
    /// Advances the persisted usage completeness. A terminal run outcome (<paramref name="force"/> = true)
    /// sets it authoritatively — including a fault's Partial, which a later disposal flush must not upgrade.
    /// A best-effort caller (disposal, <paramref name="force"/> = false) only advances it from the live
    /// InProgress default, so it can stamp Complete when no run-level outcome was recorded without clobbering
    /// a run's Partial (#196).
    /// </summary>
    private void SetUsageCompleteness(UsageCompleteness completeness, bool force)
    {
        lock (_usageWriterLock)
        {
            if (force || _usageCompleteness == UsageCompleteness.InProgress)
            {
                _usageCompleteness = completeness;
            }
        }
    }

    /// <summary>
    /// Broadcasts a live conversation-usage frame to the current run's subscribers whenever the folded
    /// aggregate changes, so the client usage banner reflects sub-agent / workflow descendant spend live
    /// rather than only after a reload of the persisted aggregate (#196, BUG 1). Wired as the usage ledger's
    /// aggregate-changed callback. The frame is transient (<see cref="ITransientMessage"/>): never buffered,
    /// added to history, or persisted — a reconnecting client restores the authoritative figure from the
    /// persisted aggregate. Best-effort and non-blocking; a publish fault must never fault the usage path.
    /// </summary>
    protected void PublishUsageAggregateFrame(ConversationUsageAggregate aggregate)
    {
        if (aggregate is null)
        {
            return;
        }

        try
        {
            var frame = ConversationUsageMessage.FromAggregate(aggregate, ThreadId);
            _ = PublishToAllAsync(frame, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Failed to publish live usage frame for thread {ThreadId}", ThreadId);
        }
    }

    /// <summary>
    /// Awaits any pending/in-flight usage write so the latest snapshot is durable before run completion or
    /// disposal proceeds. Best-effort: a persistence fault must not fault the caller's lifecycle (#196).
    /// </summary>
    private async Task FlushUsageAsync()
    {
        var writer = _usageWriter;
        if (writer is null)
        {
            return;
        }

        try
        {
            // Force a fresh write of the latest snapshot — including the terminal completeness just set —
            // rather than only draining an already-pending observation. A terminal flush with nothing
            // pending would otherwise never persist the Complete/Partial state (#196, BUG 2).
            writer.Schedule();
            var durable = await writer.FlushAsync();
            if (!durable)
            {
                Logger.LogError(
                    "Usage flush did not achieve durability for thread {ThreadId}; final usage may remain only in memory",
                    ThreadId);
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Usage flush failed for thread {ThreadId}", ThreadId);
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

            // Load existing metadata to preserve Properties, SessionMappings and ownership
            var existing = await Store.LoadMetadataAsync(ThreadId, ct);

            var metadata = new ThreadMetadata
            {
                ThreadId = ThreadId,
                CurrentRunId = null, // Only save when run is complete
                LatestRunId = latestRun,
                LastUpdated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Properties = existing?.Properties,
                SessionMappings = existing?.SessionMappings,

                // Ownership is CARRIED, never recomputed. This method rebuilds the record from
                // scratch, and SaveMetadataAsync upserts all four owner columns unconditionally,
                // so a field omitted here is a field written back as NULL. Because this runs after
                // every completed run, omitting them un-stamped the conversation on its very first
                // turn - and under Identity:Enforce a null tenant reads as "no such conversation",
                // so the owner lost their own conversation the moment they used it.
                //
                // Recomputing is not an option at this layer even if it were desirable: an agent
                // run is a background task that outlives the HTTP request, so there is no principal
                // here to recompute from. Carrying what creation already established is the only
                // correct behaviour.
                TenantId = existing?.TenantId,
                OwnerUserId = existing?.OwnerUserId,
                OwnerAppId = existing?.OwnerAppId,
                Visibility = existing?.Visibility,
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
    /// Marks conversation-history recovery as already satisfied so <see cref="RunAsync"/> will NOT
    /// auto-recover persisted messages for this thread on startup. Use for a FRESH run that must begin
    /// with empty context even when a prior run persisted messages under the same thread id — e.g. a
    /// StartWorkflowAgent controller launch whose caller-chosen workflow id collides with an earlier
    /// run's thread in a shared conversation store. The deliberate resume path calls
    /// <see cref="RecoverAsync"/> instead (which also sets this flag). Idempotent; call BEFORE
    /// <see cref="RunAsync"/>.
    /// </summary>
    public void SuppressHistoryRecovery() => _historyRecovered = true;

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
    /// Number of queued input batches that have arrived but not yet been assigned to a run. This is
    /// what <see cref="RunCompletedMessage.PendingMessageCount"/> reports, and consumers act on it:
    /// <c>SubAgentManager</c> treats a completion with pending input as NON-terminal and therefore
    /// does NOT dispose the sub-agent's owned provider agent, because the loop is about to start a
    /// follow-on run through that same provider. Reporting 0 while input is queued disposes the
    /// provider out from under the next run (its first request throws
    /// <see cref="ObjectDisposedException"/> on the underlying <c>HttpClient</c>).
    /// </summary>
    protected int PendingInputCount => _inputChannel.Reader.CanCount ? _inputChannel.Reader.Count : 0;

    /// <summary>
    /// Posts a pre-built <see cref="QueuedInput"/> directly to the input channel, preserving
    /// any non-default fields (including <see cref="QueuedInput.Resume"/>). Used by
    /// <c>MultiTurnAgentLoop</c> to enqueue internal resume sentinels for deferred-tool
    /// auto-resume; not for general user input — use <see cref="SendAsync(UserInput, CancellationToken)"/>
    /// for that.
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
        CancellationToken ct = default) =>
        SendAsync(new UserInput(messages, inputId, parentRunId), ct);

    /// <summary>
    /// <see cref="SendAsync(List{IMessage}, string?, string?, CancellationToken)"/> over a full
    /// <see cref="UserInput"/>, so per-input flags (notably
    /// <see cref="UserInput.SuppressSubAgentSpawning"/>) reach the run instead of being rebuilt away.
    /// </summary>
    public virtual ValueTask<SendReceipt> SendAsync(
        UserInput input,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        var inputId = input.InputId;
        var receiptId = inputId ?? Guid.NewGuid().ToString("N");
        var queuedAt = DateTimeOffset.UtcNow;
        var suppressed = WillSuppressSpawning(input);

        // Announce the acceptance BEFORE the enqueue, for the same reason the durable write in
        // TrySendAsync happens first: reporting afterwards leaves a window in which the input is
        // already in the channel and no host knows it — the hole this closes, only narrower. A
        // throwing observer therefore fails the send with nothing queued (see the property's remarks).
        InputAcceptanceObserver?.OnInputAccepted(ThreadId, receiptId, this);

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
                try
                {
                    await _inputChannel.Writer.WriteAsync(queued, ct);
                }
                catch
                {
                    // The mirror of TrySendAsync's refused-enqueue rollback, for the exit this path
                    // has and that one does not. A full channel does not refuse here, it parks — and
                    // that await can still fail (a cancelled token, or a channel completed by
                    // disposal underneath the waiter). The accept was announced before the TryWrite,
                    // so leaving the report standing here leaves an id nothing can ever retire: no
                    // run will name an input the agent never received, and the conversation reads
                    // busy until the host's grace expires.
                    //
                    // Unlike TrySendAsync there is no durable accepted-input write on this path
                    // (SendAsync does not touch RunLedgerStore), so this withdrawal IS the whole
                    // rollback rather than half of it. A throwing observer surfaces here in place of
                    // the write failure; both are failures of the same send with nothing queued, so
                    // what the caller must do about it is the same.
                    InputAcceptanceObserver?.OnInputAcceptanceRescinded(ThreadId, receiptId, this);
                    throw;
                }

                return new SendReceipt(receiptId, inputId, queuedAt, SpawningSuppressed: suppressed);
            }
        }

        Logger.LogDebug("Message queued. ReceiptId: {ReceiptId}, InputId: {InputId}", receiptId, inputId);

        return ValueTask.FromResult(
            new SendReceipt(receiptId, inputId, queuedAt, SpawningSuppressed: suppressed));
    }

    /// <inheritdoc />
    public virtual ValueTask<SendReceipt?> TrySendAsync(
        List<IMessage> messages,
        string? inputId = null,
        string? parentRunId = null,
        CancellationToken ct = default) =>
        TrySendAsync(new UserInput(messages, inputId, parentRunId), ct);

    /// <summary>
    /// Whether THIS agent will actually ENFORCE <see cref="UserInput.SuppressSubAgentSpawning"/> on the run
    /// that consumes an accepted input. It gates <see cref="SendReceipt.SpawningSuppressed"/>, which a host
    /// relays to a caller as a guarantee — so the base reports <c>false</c>: it has no spawn machinery to
    /// police, and an agent that merely accepts the flag must never let a receipt claim the guarantee.
    /// <para>
    /// Public because a host must be able to check it BEFORE enqueuing: declaring
    /// <see cref="SubAgents.ISpawnSuppressingAgent"/> only proves the type accepts a
    /// <see cref="UserInput"/>, and rejecting after the message is already queued would leave an
    /// unsuppressed input in the channel.
    /// </para>
    /// </summary>
    public virtual bool EnforcesSpawnSuppression => false;

    /// <summary>
    /// The value a receipt reports for <paramref name="input"/>. It states ENFORCEMENT, never the request: a
    /// caller reading it needs to know whether the run that consumes this input will genuinely be unable to
    /// spawn, and echoing the flag back would let an agent that ignores it advertise a guarantee nothing is
    /// keeping. Shared by both send paths so the two cannot drift apart.
    /// </summary>
    private bool WillSuppressSpawning(UserInput input) =>
        input.SuppressSubAgentSpawning && EnforcesSpawnSuppression;

    /// <summary>
    /// <see cref="TrySendAsync(List{IMessage}, string?, string?, CancellationToken)"/> over a full
    /// <see cref="UserInput"/>. This is the single enqueue path, so per-input flags — notably
    /// <see cref="UserInput.SuppressSubAgentSpawning"/> — survive as far as the run that consumes them
    /// instead of being rebuilt away from a message list.
    /// </summary>
    public virtual async ValueTask<SendReceipt?> TrySendAsync(
        UserInput input,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        var inputId = input.InputId;
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

        // AFTER the durable write and BEFORE the enqueue. After, because a store failure means the
        // input was never accepted by anyone, so there is nothing to announce and nothing to
        // withdraw. Before, because an in-memory host that learns of the accept only once the input
        // is already queued has the same hole this closes.
        InputAcceptanceObserver?.OnInputAccepted(ThreadId, receiptId, this);

        var queued = new QueuedInput(input, receiptId, queuedAt);

        if (!_inputChannel.Writer.TryWrite(queued))
        {
            Logger.LogWarning("Input channel full, rejecting TrySendAsync. ReceiptId: {ReceiptId}", receiptId);

            // Withdraw the announcement for the same reason the durable record is rolled back below:
            // no run will ever name an input the agent never received, so an announcement left
            // standing here is an id nothing can retire until the host's grace expires.
            InputAcceptanceObserver?.OnInputAcceptanceRescinded(ThreadId, receiptId, this);

            if (RunLedgerStore != null)
            {
                // Roll back the acceptance record: the input was never actually queued, so a
                // caller polling by inputId must not see it as durably accepted.
                await RunLedgerStore.RemoveAcceptedInputAsync(ThreadId, receiptId, ct);
            }

            return null;
        }

        Logger.LogDebug("Message queued via TrySendAsync. ReceiptId: {ReceiptId}, InputId: {InputId}", receiptId, inputId);

        return new SendReceipt(
            receiptId,
            inputId,
            queuedAt,
            SpawningSuppressed: WillSuppressSpawning(input));
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

        var subscriber = new Subscriber { Channel = outputChannel };
        lock (_replayLock)
        {
            // Same admission gate as SubscribeAsync: registering behind disposal's teardown drain
            // would strand this run on a channel nobody will complete.
            ObjectDisposedException.ThrowIf(_isDisposed, this);

            if (!_outputSubscribers.TryAdd(subscriberId, subscriber))
            {
                throw new InvalidOperationException("Failed to create subscriber for ExecuteRun");
            }
        }

        try
        {
            // Send the input and get receipt (non-blocking)
            var receipt = await SendAsync(userInput, ct);
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
                    // Channel completed. An ordinary completion and a slow-consumer eviction look
                    // identical here, so a silent exit would report a run this caller only partly
                    // received as one that finished. Surface the reserved recovery control (set only
                    // by PublishToSubscriber's eviction path) as the terminal message instead.
                    if (subscriber.RecoveryControl.Task.IsCompletedSuccessfully)
                    {
                        yield return subscriber.RecoveryControl.Task.Result;
                    }

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
            if (_outputSubscribers.TryRemove(subscriberId, out var removed))
            {
                _ = removed.Channel.Writer.TryComplete();
            }
        }
    }

    #endregion

    #region Output API

    /// <summary>
    /// Per-subscriber fan-out state. Tracks the bounded output channel alongside the run/generation
    /// identity of the last message actually DELIVERED to this subscriber (see
    /// <see cref="PublishToSubscriber"/>), plus a reserved terminal recovery
    /// control. <see cref="RecoveryControl"/> is completed ONLY when <see cref="PublishToSubscriber"/>
    /// drops this subscriber for being too slow - never on an ordinary unsubscribe (this class's own
    /// <see cref="SubscribeAsync"/> cleanup) or on agent disposal (<see cref="DisposeAsync"/>) - so a
    /// terminal <see cref="StreamRecoveryMessage"/> stays observable even though the bounded
    /// <see cref="Channel"/> it was dropped from is, by definition, full.
    /// </summary>
    private sealed class Subscriber
    {
        public required Channel<IMessage> Channel { get; init; }

        /// <summary>
        /// Guards the write-then-record step in <see cref="PublishToSubscriber"/>. Two publishers
        /// writing to the same subscriber concurrently must not interleave "write B" between
        /// "write A" and "record A", or the recorded resume point can name an older message than
        /// the one actually delivered last.
        /// </summary>
        public object SyncRoot { get; } = new();

        /// <summary>Run/generation of the last message this subscriber ACTUALLY received.</summary>
        public DeliveredIdentity Identity { get; set; }

        public TaskCompletionSource<StreamRecoveryMessage> RecoveryControl { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>
    /// The run/generation pair a subscriber is caught up on. Advanced as one unit so the pair is
    /// always an identity that genuinely occurred, never a mix of two runs' identifiers.
    /// </summary>
    private readonly record struct DeliveredIdentity(string? RunId, string? GenerationId)
    {
        /// <summary>
        /// Folds a just-delivered message into the pair. A message that moves this subscriber onto a
        /// DIFFERENT run adopts that run's generation wholesale — including "none" — rather than
        /// inheriting the previous run's, which would fabricate a pair that never existed. Within one
        /// run, ids are merged forward: several message kinds omit one or both (e.g. a finalized
        /// tool_call arrives without a RunId), and a null must not erase an already-known value.
        /// </summary>
        public DeliveredIdentity Advance(IMessage message) =>
            message.RunId != null && !string.Equals(message.RunId, RunId, StringComparison.Ordinal)
                ? new DeliveredIdentity(message.RunId, message.GenerationId)
                : this with { GenerationId = message.GenerationId ?? GenerationId };
    }

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
        var subscriber = new Subscriber { Channel = channel };
        IReadOnlyList<IMessage> replay;
        StreamRecoveryMessage? truncationAdvisory = null;
        lock (_replayLock)
        {
            // Admission is gated under the SAME lock DisposeAsync marks itself with, so a subscriber
            // can never register behind disposal's teardown drain and be left waiting forever on a
            // channel nobody will complete.
            ObjectDisposedException.ThrowIf(_isDisposed, this);

            if (_replayRunActive && _replayBufferTruncated)
            {
                // The buffer no longer covers the whole run, and a client cannot tell a partial
                // replay from a complete one. Withhold it entirely and advise THIS subscription to
                // resync from authoritative history — every subscription, for itself. The advisory is
                // a LEADING frame, not a terminal one: only the run's already-published PREFIX is
                // missing, so the subscriber is still registered below and goes on to receive the live
                // tail. That is what keeps advising everyone loop-free — no consumer has to reconnect
                // in order to keep following the run, and so no consumer can be advised again by the
                // reconnection landing on the same still-truncated buffer.
                replay = [];
                truncationAdvisory = new StreamRecoveryMessage(
                    ThreadId,
                    _replayRunId,
                    _replayGenerationId,
                    StreamRecoveryReason.ReplayTruncated);
            }
            else
            {
                replay = _replayRunActive && _replayBuffer.Count > 0
                    ? [.. _replayBuffer]
                    : [];
            }

            // A replayed message is delivered as surely as a live one — the loop below yields the whole
            // snapshot (or the advisory that stands in for it) before it ever reads the channel, even for
            // a subscriber that is dropped straight afterwards — so it must advance the delivery cursor
            // too. Seeding it HERE, inside the same critical section that registers the subscriber, is
            // what keeps the cursor monotonic: a publisher snapshots `_outputSubscribers` under this same
            // lock, so it either misses this subscriber entirely or sees it already seeded, and can only
            // move the cursor forward from there. Advancing it from the yield loop instead would race
            // live writes that have already moved it past the (strictly older) snapshot.
            // `SyncRoot` is deliberately NOT taken: nothing else can reach this subscriber yet, and
            // nesting it under `_replayLock` would invert the publish path's lock order.
            if (truncationAdvisory is not null)
            {
                subscriber.Identity = subscriber.Identity.Advance(truncationAdvisory);
            }

            foreach (var buffered in replay)
            {
                subscriber.Identity = subscriber.Identity.Advance(buffered);
            }

            _outputSubscribers[subscriberId] = subscriber;
        }

        if (truncationAdvisory is not null)
        {
            Logger.LogWarning(
                "Subscriber {SubscriberId} joined run {RunId} whose replay buffer is truncated; "
                    + "withholding the partial replay and signalling resync.",
                subscriberId,
                truncationAdvisory.RunId);
        }

        Logger.LogDebug(
            "Subscriber {SubscriberId} connected (replaying {ReplayCount} in-flight message(s))",
            subscriberId,
            replay.Count);

        try
        {
            // Replay the in-flight run's already-published messages first (so a reconnecting client
            // resumes from the start of the live run), then stream subsequent live messages. When the
            // replay was withheld as truncated there is nothing to replay and the advisory takes its
            // place at the head of the stream, ahead of the live tail.
            if (truncationAdvisory is not null)
            {
                ct.ThrowIfCancellationRequested();
                yield return truncationAdvisory;
            }

            foreach (var buffered in replay)
            {
                ct.ThrowIfCancellationRequested();
                yield return buffered;
            }

            await foreach (var message in channel.Reader.ReadAllAsync(ct))
            {
                yield return message;
            }

            // The channel completed without cancellation. If PublishToSubscriber dropped this
            // subscriber for being too slow, its recovery control is already set — surface it
            // as the stream's terminal message so the client can tell "you were disconnected,
            // resync" apart from an ordinary run completion or unsubscribe (which leave it unset).
            if (subscriber.RecoveryControl.Task.IsCompletedSuccessfully)
            {
                yield return subscriber.RecoveryControl.Task.Result;
            }
        }
        finally
        {
            // Cleanup on unsubscribe
            if (_outputSubscribers.TryRemove(subscriberId, out var removed))
            {
                _ = removed.Channel.Writer.TryComplete();
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
        ArgumentNullException.ThrowIfNull(message);
        _ = ct;
        KeyValuePair<string, Subscriber>[] targets;
        lock (_replayLock)
        {
            // Transient live-only frames (e.g. the conversation usage banner frame) are never buffered: a
            // reconnecting client restores their state from an authoritative source, and buffering them would
            // consume the bounded replay buffer and risk evicting the run's real deltas (#196).
            if (message is not ITransientMessage)
            {
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
                        _replayGenerationId = ram.Assignment?.GenerationId;
                    }
                }

                if (_replayRunActive && ReplayMessagePolicy.IsCanonicalOrControl(message))
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
                            "In-flight replay buffer hit its cap ({CountCap} messages / {ByteCap} bytes); the "
                                + "buffered prefix no longer covers this run, so it is withheld from a client "
                                + "reconnecting mid-run — that client is told to resync from persisted history "
                                + "instead of resuming on a silently partial stream.",
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
            }

            // ConcurrentDictionary.ToArray() is the ONLY safe way to copy this map, and the reason is
            // not style. A collection expression (or List<T>'s IEnumerable constructor) reads
            // ICollection.Count first and calls CopyTo second, each acquiring the dictionary's internal
            // locks separately. A subscriber that unsubscribes between the two — an ordinary client
            // disconnect, and also the slow-subscriber drop below — makes CopyTo write FEWER pairs than
            // the length already committed to, leaving default(KeyValuePair) at the tail: a null
            // Channel that the publish loop then dereferences. ToArray() takes every lock once and
            // sizes the result from what it actually copied, so a torn tail cannot exist.
#pragma warning disable IDE0305 // "Simplify" here means reintroducing the torn-snapshot race above.
            targets = _outputSubscribers.ToArray();
#pragma warning restore IDE0305
        }

        foreach (var (subscriberId, subscriber) in targets)
        {
            PublishToSubscriber(subscriberId, subscriber, message);
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
    private void PublishToSubscriber(string subscriberId, Subscriber subscriber, IMessage message)
    {
        // Record the run/generation identity ONLY once the message is genuinely in the subscriber's
        // channel, and under that subscriber's own lock so a concurrent publisher cannot interleave
        // between the write and the record. The identity is the resume point stamped on an eventual
        // StreamRecoveryMessage: recording a message that was never delivered would tell the client
        // it is caught up on content it never saw.
        lock (subscriber.SyncRoot)
        {
            // Fast path: succeeds whenever the subscriber is keeping up (the overwhelming common case).
            if (subscriber.Channel.Writer.TryWrite(message))
            {
                subscriber.Identity = subscriber.Identity.Advance(message);
                return;
            }
        }

        if (_outputSubscribers.TryRemove(subscriberId, out var removed))
        {
            DeliveredIdentity identity;
            lock (removed.SyncRoot)
            {
                identity = removed.Identity;
            }

            // Reserve the terminal recovery control BEFORE completing the channel, so it is
            // observable to SubscribeAsync even though the channel it was dropped from is (by
            // definition) full: SubscribeAsync checks this TCS only after its own read loop ends,
            // never by writing into the now-completed channel.
            _ = removed.RecoveryControl.TrySetResult(new StreamRecoveryMessage(
                ThreadId,
                identity.RunId,
                identity.GenerationId,
                StreamRecoveryReason.SlowConsumer));
            _ = removed.Channel.Writer.TryComplete();
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

        // The same restart argument, one guard of its own: lifecycle persistence is configured
        // independently of the run ledger, so a host can have dangling lifecycle runs to close
        // without having a ledger at all.
        if (!_lifecycleReconciled)
        {
            _lifecycleReconciled = true;
            await Lifecycle.ReconcileInterruptedRunsAsync(ct);
        }

        // Rebuild conversation usage accounting from durable per-attempt records before processing input,
        // so an agent recreated after a restart continues the running total (and dedups already-counted
        // attempts) instead of overwriting the persisted aggregate with only post-restart usage (#196).
        if (UsageLedger != null && Store != null && !_usageHydrated)
        {
            _usageHydrated = true;
            try
            {
                var records = await ConversationUsageProjection.LoadRecordsAsync(Store, ThreadId, ct);
                if (records.Count > 0)
                {
                    var aggregate = await ConversationUsageProjection.LoadAsync(Store, ThreadId, ct);
                    UsageLedger.SeedFromRecords(records, aggregate?.FoldedRevision ?? 0);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Usage rebuild failed for thread {ThreadId}; starting usage empty", ThreadId);
            }
        }

        await OnBeforeRunAsync();

        _internalCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _runTask = RunLoopAsync(_internalCts.Token);

        Logger.LogInformation("{AgentType} started. ThreadId: {ThreadId}", GetType().Name, ThreadId);

        try
        {
            await _runTask;

            // Clean terminal exit (loop returned, incl. a deliberate cancellation): every provider call
            // this loop observed is captured, so the conversation's usage is Complete (#196, BUG 2).
            SetUsageCompleteness(UsageCompleteness.Complete, force: true);
        }
        catch (OperationCanceledException)
        {
            // A deliberate stop/dispose surfaced as cancellation — captured usage is durable; still Complete.
            SetUsageCompleteness(UsageCompleteness.Complete, force: true);
            throw;
        }
        catch
        {
            // The run faulted: usage up to the fault is captured, but some incurred usage may be missing.
            SetUsageCompleteness(UsageCompleteness.Partial, force: true);
            throw;
        }
        finally
        {
            // Guarantee the conversation's final usage snapshot (and its terminal completeness) is durable
            // before the run returns — a completed/cancelled run must not leave the latest aggregate only in
            // memory (#196).
            await FlushUsageAsync();
        }
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

        // A run cancelled mid-flight never reaches CompleteRunAsync — the loops deliberately let
        // OperationCanceledException escape their per-run handler — so its completion has to come
        // from here or a subscriber holds an unpaired start forever. CancellationToken.None: the
        // token that ended the run must not also cancel the event that says so.
        await Lifecycle.TerminalizeOutstandingAsync(LifecycleRunOutcomes.Cancelled, CancellationToken.None);

        Logger.LogInformation("{AgentType} stopped, ready for restart", GetType().Name);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        var owned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task inFlight;
        lock (_replayLock)
        {
            // Marking disposed under the lock that gates subscriber admission closes the window where
            // a subscriber registers behind the teardown drain below. Publishing the teardown task
            // under the same lock makes disposal idempotent AND awaitable: a plain bool guard lets a
            // second caller return "disposed" while the first teardown is still running, handing back
            // a half-disposed agent.
            _isDisposed = true;
            inFlight = _disposeTask ??= owned.Task;
        }

        if (!ReferenceEquals(inFlight, owned.Task))
        {
            return new ValueTask(inFlight);
        }

        // The FIRST caller awaits the very task every later caller awaits, rather than the teardown's
        // own. Returning the latter would give a failing teardown TWO faulted tasks — the one this
        // caller handles, and the published one that only a second caller would ever look at. With a
        // single caller (one `await using`, the common case) nobody observes the published fault, so
        // the task finaliser re-raises it on TaskScheduler.UnobservedTaskException long after the
        // disposal it belongs to was handled correctly. DisposeOnceAsync therefore PUBLISHES its
        // outcome instead of throwing, which leaves its own task always successful and safe to drop.
        _ = DisposeOnceAsync(owned);
        return new ValueTask(owned.Task);
    }

    /// <summary>
    /// Runs the one-and-only teardown and publishes its outcome — success or fault — to the single
    /// task every caller of <see cref="DisposeAsync"/> awaits.
    /// </summary>
    /// <param name="completion">The published completion this teardown's outcome is reported on.</param>
    private async Task DisposeOnceAsync(TaskCompletionSource completion)
    {
        try
        {
            await DisposeCoreAsync();
        }
        catch (Exception ex)
        {
            _ = completion.TrySetException(ex);
            return;
        }

        _ = completion.TrySetResult();
    }

    private async Task DisposeCoreAsync()
    {
        // Channel teardown is what ENDS every live stream, so it cannot be conditional on ANY
        // earlier step succeeding. Guarding only OnDisposeAsync left every step before it — a run
        // loop that faulted and resurfaces through StopAsync, a lifecycle terminalization, a
        // CancellationTokenSource already disposed by a racing caller — able to abandon the
        // teardown outright, leaving every SubscribeAsync/ExecuteRunAsync enumerator parked forever
        // on a channel nobody will ever complete: the agent is gone AND its readers never learn.
        // The finally is therefore OUTERMOST, wrapping the whole body rather than one step of it.
        //
        // It reports no exception of its own — CompleteChannelsOnDisposeAsync RETURNS its failure
        // instead of throwing — because a finally that throws REPLACES the in-flight exception, and
        // a subscriber hook failing during cleanup must never be what the caller sees in place of
        // the real reason disposal failed.
        Exception? cleanupFailure;
        try
        {
            // Before StopAsync, so an internal enqueue still parked on a full channel is released
            // rather than holding the loop's shutdown open behind it.
            await _lifetimeCts.CancelAsync();

            await StopAsync();

            // Disposal is a terminal boundary: if no run-level outcome stamped completeness (e.g. the loop was
            // disposed without RunAsync having reached its finally, or only descendant usage was relayed), mark
            // Complete — but never upgrade a run's Partial. force: false only advances from InProgress (#196).
            SetUsageCompleteness(UsageCompleteness.Complete, force: false);

            // Normally a no-op: StopAsync has already closed whatever was in flight. It matters for an
            // agent disposed without ever having been started-and-stopped, whose lifecycle runs would
            // otherwise never be closed by anyone.
            await Lifecycle.TerminalizeOutstandingAsync(LifecycleRunOutcomes.Interrupted, CancellationToken.None);

            // Final durability boundary: flush any usage write scheduled by a late/background descendant that
            // finished after the run stopped, so it is persisted rather than lost at shutdown (#196).
            await FlushUsageAsync();

            _internalCts?.Dispose();
            _lifetimeCts.Dispose();

            await OnDisposeAsync();
        }
        finally
        {
            cleanupFailure = await CompleteChannelsOnDisposeAsync();

            // Inside the finally so a failed disposal still suppresses finalization: the object is
            // just as disposed either way, and only the reporting differs.
            GC.SuppressFinalize(this);
        }

        // Reached only when the body completed, so there is no earlier failure to preserve and a
        // returned cleanup failure would otherwise be swallowed into a silent success.
        if (cleanupFailure != null)
        {
            ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
        }
    }

    /// <summary>
    /// Ends the input channel and every subscriber's stream, then runs each subscriber's completion
    /// hook. Never throws - it RETURNS the failure instead - so that it is safe to call from the
    /// outermost <see langword="finally"/> of <see cref="DisposeCoreAsync"/> without replacing the
    /// failure that sent us there.
    /// </summary>
    /// <returns>
    /// The hook failure worth reporting, or <see langword="null"/> when every hook succeeded. The
    /// caller decides whether to surface it, because an earlier failure outranks it.
    /// </returns>
    private async Task<Exception?> CompleteChannelsOnDisposeAsync()
    {
        // Complete input channel on disposal (final cleanup - no restart possible)
        _ = _inputChannel.Writer.TryComplete();

        // Close all subscriber channels. Disposal is not a slow-consumer eviction, so it never
        // touches RecoveryControl - a subscriber still reading here simply sees its channel end
        // (no StreamRecoveryMessage), same as any other clean shutdown. Drain the map under
        // _replayLock and remove BEFORE completing (matching PublishToSubscriber's slow-consumer
        // path and SubscribeAsync's own unsubscribe cleanup): once a subscriber is removed here, a
        // concurrent publish's snapshot can no longer also see it, so it can never race this
        // teardown into wrongly setting RecoveryControl on an ordinary disposal. Draining under the
        // lock (which _isDisposed was set beneath) is what makes the drain final — no admission can
        // follow it.
        KeyValuePair<string, Subscriber>[] subscribers;
        lock (_replayLock)
        {
#pragma warning disable IDE0305 // See PublishToAllAsync: ToArray() is the only torn-snapshot-free copy.
            subscribers = _outputSubscribers.ToArray();
#pragma warning restore IDE0305
            _outputSubscribers.Clear();
        }

        // PHASE 1 - end every stream first, in a pass that cannot fail. Completing a writer and
        // running its hook per subscriber in ONE pass made each subscriber's hook a gate on the
        // NEXT subscriber's stream ever ending: a single throwing hook and every client after it in
        // the snapshot hangs forever on a channel that is now unreachable. The ordering is the fix;
        // the error handling below only decides what gets reported.
        foreach (var (_, subscriber) in subscribers)
        {
            _ = subscriber.Channel.Writer.TryComplete();
        }

        // PHASE 2 - notify, each hook independently, so one failure cannot skip the others.
        List<Exception>? failures = null;
        foreach (var (subscriberId, _) in subscribers)
        {
            try
            {
                await OnSubscriberChannelCompletedDuringDisposeAsync(subscriberId);
            }
            catch (Exception ex)
            {
                Logger.LogError(
                    ex,
                    "Subscriber {SubscriberId} teardown hook failed during disposal of thread {ThreadId}",
                    subscriberId,
                    ThreadId);
                (failures ??= []).Add(ex);
            }
        }

        return failures switch
        {
            null => null,
            // A lone failure reports AS ITSELF: the overwhelmingly common case hands the caller the
            // real exception to catch rather than an AggregateException it has to unwrap first.
            [var only] => only,
            _ => new AggregateException(
                "One or more subscriber teardown hooks failed during disposal.", failures),
        };
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
    /// <param name="wasForked">
    /// Whether the caller asked this run to inherit provider-side context from
    /// <paramref name="parentRunId"/>. Reported on the lifecycle <c>run_started</c> event, where it
    /// describes context inheritance only — a run can carry a parent without being a fork.
    /// </param>
    /// <param name="runId">
    /// The id to start the run under, when the caller already committed to one. A delayed tool
    /// result has to name the run its resolution will cause <em>before</em> that run exists, so that
    /// the durable resolution record and the run itself cannot disagree; every other caller leaves
    /// this null and gets a freshly minted id.
    /// </param>
    /// <param name="causeKind">Why the run started. See <c>LifecycleRunCauseKinds</c>.</param>
    /// <param name="causeToolCallId">
    /// The tool call whose result caused the run, for a delayed-result child.
    /// </param>
    /// <returns>The run assignment</returns>
    protected async Task<RunAssignment> StartRunAsync(
        IReadOnlyList<QueuedInput> inputs,
        string? parentRunId = null,
        CancellationToken ct = default,
        bool wasForked = false,
        string? runId = null,
        string? causeKind = null,
        string? causeToolCallId = null)
    {
        ArgumentNullException.ThrowIfNull(inputs);

        runId ??= Guid.NewGuid().ToString("N");
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

        await Lifecycle.RunStartedAsync(
            runId,
            generationId,
            parentRunId,
            causeKind: causeKind,
            causeToolCallId: causeToolCallId,
            wasForked: wasForked,
            ct: ct);

        Logger.LogInformation(
            "Starting run {RunId} (parent: {ParentRunId}, generation: {GenerationId}, inputs: {InputCount})",
            runId,
            parentRunId ?? "none",
            generationId,
            inputs.Count);

        return new RunAssignment(runId, generationId, inputIds, parentRunId);
    }

    /// <summary>
    /// Marks the start of a model turn for lifecycle reporting. Emits nothing on its own.
    /// </summary>
    /// <param name="runId">The run the turn belongs to.</param>
    /// <param name="generationId">The generation id the turn runs under.</param>
    /// <remarks>
    /// Pair every call with <see cref="CompleteTurnAsync"/> on the path where the turn finishes
    /// normally. Abnormal endings need no call: a turn still open when the run terminalizes is
    /// reported by the finalizer with the run's own outcome, which is what keeps error,
    /// cancellation, and teardown from needing a copy of this logic in each loop.
    /// </remarks>
    protected void BeginTurn(string runId, string generationId) =>
        Lifecycle.TurnStarted(runId, generationId);

    /// <summary>
    /// Folds a message the current turn produced into that turn's lifecycle report.
    /// </summary>
    /// <param name="runId">The run the turn belongs to.</param>
    /// <param name="generationId">The generation id the turn runs under.</param>
    /// <param name="message">The message the turn produced.</param>
    /// <remarks>
    /// Call this for every message a turn emits, streaming fragments included — the seam decides
    /// what counts, so <c>message_count</c> means the same thing whichever loop reported it.
    /// </remarks>
    protected void ObserveTurnMessage(string runId, string generationId, IMessage message) =>
        Lifecycle.ObserveTurnMessage(runId, generationId, message);

    /// <summary>
    /// Reports a turn that reached its final state.
    /// </summary>
    /// <param name="runId">The run the turn belongs to.</param>
    /// <param name="generationId">The generation id the turn ran under.</param>
    /// <param name="outcome">How it ended. Defaults to <see cref="LifecycleTurnOutcomes.Completed"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <remarks>
    /// This is the one turn-finalization seam every loop shares. What a "turn" is differs by
    /// provider — the raw loop takes one per model round-trip, while a CLI-backed loop runs its own
    /// agentic loop behind a single generation id and so reports one turn per run — but the event a
    /// subscriber receives is the same shape and the same guarantee either way: one final report per
    /// generation the loop accepted, never a streaming fragment.
    /// </remarks>
    protected Task CompleteTurnAsync(
        string runId,
        string generationId,
        string? outcome = null,
        CancellationToken ct = default) =>
        Lifecycle.TurnCompletedAsync(
            runId,
            generationId,
            outcome ?? LifecycleTurnOutcomes.Completed,
            ct: ct);

    /// <summary>
    /// Reports the discovered context a provider request is about to carry, reading it back out of
    /// the request itself.
    /// </summary>
    /// <param name="runId">The run whose request carries the context.</param>
    /// <param name="generationId">The turn whose request carries it.</param>
    /// <param name="request">The request as it will be dispatched.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <remarks>
    /// <para>
    /// Call this from the dispatch site, on the snapshot that is being sent and after nothing else
    /// will touch it. Reporting from anywhere earlier would describe context that was merely
    /// intended — the discovery that got cancelled, superseded, or dropped between rendering and
    /// sending would be announced as delivered.
    /// </para>
    /// <para>
    /// The scan is skipped entirely when nobody subscribes, so a loop without lifecycle pays nothing
    /// for it.
    /// </para>
    /// </remarks>
    protected Task ReportContextLoadedAsync(
        string runId,
        string generationId,
        IEnumerable<IMessage>? request,
        CancellationToken ct = default) =>
        Lifecycle.PublishesEvents
            ? Lifecycle.ContextLoadedAsync(
                runId,
                generationId,
                RenderedContextBlock.ScanRequest(request),
                ct)
            : Task.CompletedTask;

    /// <summary>
    /// Reports the discovered context a rendered prompt is about to carry, for providers whose
    /// request is a single string rather than a message list.
    /// </summary>
    /// <param name="runId">The run whose prompt carries the context.</param>
    /// <param name="generationId">The turn whose prompt carries it.</param>
    /// <param name="prompt">The prompt as it will be dispatched.</param>
    /// <param name="phase">
    /// How context in this prompt entered the conversation. Defaults to
    /// <see cref="LifecycleContextPhases.MidSession"/>, which is what a per-turn prompt carries — a
    /// CLI provider takes its boot instructions through a separate session-start call, not through
    /// the turn prompt.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    protected Task ReportContextLoadedAsync(
        string runId,
        string generationId,
        string? prompt,
        string? phase = null,
        CancellationToken ct = default) =>
        Lifecycle.PublishesEvents
            ? Lifecycle.ContextLoadedAsync(
                runId,
                generationId,
                RenderedContextBlock.Scan(prompt, phase ?? LifecycleContextPhases.MidSession),
                ct)
            : Task.CompletedTask;

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
    /// <param name="outcome">
    /// The lifecycle outcome to report, overriding the completed/error default. Used by a
    /// delayed-result child that deliberately took no turn, which is a success the plain
    /// <c>completed</c> outcome would misdescribe. The <em>ledger</em> status is unaffected — such a
    /// run really did complete, so a caller polling status must not be told otherwise.
    /// </param>
    /// <param name="ct">Cancellation token</param>
    protected async Task CompleteRunAsync(
        string runId,
        string generationId,
        bool wasForked = false,
        string? forkedToRunId = null,
        int pendingMessageCount = 0,
        bool isError = false,
        string? errorMessage = null,
        string? outcome = null,
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

        // Terminalize before broadcasting, for the same reason the ledger write comes first: the
        // durable CAS is what decides whether this caller is the one that ends the run, and a
        // subscriber must not see a completion that lost that race.
        _ = await Lifecycle.TryCompleteRunAsync(
            runId,
            generationId,
            outcome ?? (isError ? LifecycleRunOutcomes.Error : LifecycleRunOutcomes.Completed),
            isError
                ? new LifecycleError { Message = errorMessage ?? "The run failed." }
                : null,
            ct: ct);

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
    ///   input was durably accepted (see <see cref="TrySendAsync(UserInput, CancellationToken)"/>) but the
    ///   process crashed
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
    /// Test seam only (no-op in production): invoked once per subscriber during
    /// <see cref="DisposeAsync"/>'s teardown, after EVERY subscriber's output channel has been
    /// completed (see <see cref="CompleteChannelsOnDisposeAsync"/>'s two phases - a throwing hook
    /// must not be able to strand a later subscriber's stream). Lets a test deterministically
    /// simulate <see cref="PublishToSubscriber"/> racing this instant - no sleeps, no real thread
    /// timing - to prove ordinary disposal can never set <see cref="Subscriber.RecoveryControl"/>
    /// the way a slow-consumer eviction does.
    /// </summary>
    internal virtual ValueTask OnSubscriberChannelCompletedDuringDisposeAsync(string subscriberId) =>
        ValueTask.CompletedTask;

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
