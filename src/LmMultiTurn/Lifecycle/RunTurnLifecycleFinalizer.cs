using System.Collections.Concurrent;
using AchieveAi.LmDotnetTools.LmLifecycle;
using AchieveAi.LmDotnetTools.LmLifecycle.Payloads;
using AchieveAi.LmDotnetTools.LmLifecycle.Serialization;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AchieveAi.LmDotnetTools.LmMultiTurn.Lifecycle;

/// <summary>
/// Owns a thread's run and turn lifecycle: it records where a run got to, decides which caller is
/// the one that ended it, and emits the three run-scoped events.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is a collaborator and not more methods on the loop.</b> The rule a run lifecycle has
/// to keep — <i>a run that started completes exactly once</i> — is a rule about a run, not about a
/// loop. Spreading it across <c>StartRunAsync</c>, <c>CompleteRunAsync</c>, the cancellation path,
/// and restart reconciliation would put four copies of "have we already ended this one?" in a class
/// that is already large, and each copy would be a place for the invariant to rot. Here it is one
/// object with one piece of state, and the loop calls through to it.
/// </para>
/// <para>
/// <b>Exactly-once, twice over.</b> Removing a run from the in-flight table is the in-process
/// decision: two callers racing to end the same run, and only one of them removes it. When a
/// durable store is configured the winner then has to win
/// <see cref="IRunLifecycleStore.TryMarkRunTerminalAsync"/> as well, which is what stops a process
/// that restarts mid-run from re-ending a run some other process already ended. Only the caller
/// that wins both publishes.
/// </para>
/// <para>
/// <b>Observation never breaks the thing being observed.</b> Publishing and durable writes are
/// wrapped: a subscriber that throws, a disk that is full, or a store that has closed produces a
/// log line and nothing more. Lifecycle is best-effort by construction (see ADR 0002) — a dropped
/// event shows up as a gap in <c>source_sequence</c>, which is the intended way to notice loss, and
/// is a far better outcome than a failed agent run.
/// </para>
/// </remarks>
public sealed class RunTurnLifecycleFinalizer
{
    private readonly MultiTurnLifecycleServices _services;
    private readonly IRunLifecycleStore? _store;
    private readonly string _threadId;
    private readonly string _sourceStreamId;
    private readonly ILogger _logger;
    private readonly bool _publishes;

    // The runs this finalizer believes are in flight. Presence is the in-process terminalization
    // token: whoever removes an entry owns ending that run.
    private readonly ConcurrentDictionary<string, RunProgress> _inFlight = new(StringComparer.Ordinal);

    /// <summary>
    /// Creates a finalizer for one thread.
    /// </summary>
    /// <param name="threadId">The thread whose runs this finalizer observes.</param>
    /// <param name="services">
    /// What the host wired up. <see cref="MultiTurnLifecycleServices.Disabled"/> leaves the
    /// finalizer inert.
    /// </param>
    /// <param name="fallbackStore">
    /// Used when <see cref="MultiTurnLifecycleServices.LifecycleStore"/> is null and the host's
    /// conversation store also implements <see cref="IRunLifecycleStore"/>. Ignored unless the host
    /// opted into lifecycle, so a loop that merely happens to persist to SQLite does not silently
    /// start writing lifecycle rows.
    /// </param>
    /// <param name="logger">Optional logger for publish and store failures.</param>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="threadId"/> is empty.</exception>
    public RunTurnLifecycleFinalizer(
        string threadId,
        MultiTurnLifecycleServices services,
        IRunLifecycleStore? fallbackStore = null,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (string.IsNullOrWhiteSpace(threadId))
        {
            throw new ArgumentException("A thread id must be non-empty.", nameof(threadId));
        }

        _threadId = threadId;
        _services = services;
        _logger = logger ?? NullLogger.Instance;
        _publishes = services.PublishesEvents;

        var optedIn = _publishes || services.LifecycleStore != null;
        _store = optedIn ? services.LifecycleStore ?? fallbackStore : null;

        _sourceStreamId = LifecycleSourceStream.ForThread(threadId);
    }

    /// <summary>
    /// Whether this finalizer does anything. When false every member returns immediately and the
    /// loop's behavior is byte-for-byte what it was before lifecycle hooks existed.
    /// </summary>
    public bool IsEnabled => _publishes || _store != null;

    /// <summary>
    /// Records that a run started and emits <see cref="LifecycleEventTypes.RunStarted"/>.
    /// </summary>
    /// <param name="runId">The run that started.</param>
    /// <param name="generationId">Its first turn's generation id.</param>
    /// <param name="parentRunId">The run that caused this one, when one did.</param>
    /// <param name="causeKind">Why it started. See <see cref="LifecycleRunCauseKinds"/>.</param>
    /// <param name="causeToolCallId">
    /// The tool call whose result caused the run, for a delayed-result child or a sub-agent spawn.
    /// </param>
    /// <param name="wasForked">Whether the run inherits provider-side context from its parent.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task RunStartedAsync(
        string runId,
        string generationId,
        string? parentRunId = null,
        string? causeKind = null,
        string? causeToolCallId = null,
        bool wasForked = false,
        CancellationToken ct = default)
    {
        if (!IsEnabled)
        {
            return;
        }

        var startedAt = _services.TimeProvider.GetUtcNow();
        var lineage = _services.Lineage;
        var cause = causeKind ?? LifecycleRunCauseKinds.UserInput;
        var durable = false;

        if (_store != null)
        {
            try
            {
                await _store.RecordRunStartedAsync(
                    new RunLifecycleState
                    {
                        ThreadId = _threadId,
                        RunId = runId,
                        GenerationId = generationId,
                        ParentRunId = parentRunId ?? lineage.ParentRunId,
                        ParentThreadId = lineage.ParentThreadId,
                        SpawningToolCallId = lineage.SpawningToolCallId,
                        SubAgentId = lineage.SubAgentId,
                        CauseKind = cause,
                        CauseToolCallId = causeToolCallId,
                        StartedAt = startedAt,
                    },
                    ct).ConfigureAwait(false);
                durable = true;
            }
            catch (Exception ex)
            {
                // Recorded as not-durable rather than not-started: the event still goes out, so a
                // subscriber sees the run, and the terminal decision falls back to the in-process
                // one instead of consulting a row that was never written.
                _logger.LogWarning(
                    ex,
                    "Could not record lifecycle start for run {RunId} on thread {ThreadId}; "
                        + "continuing with in-process lifecycle only",
                    runId,
                    _threadId);
            }
        }

        _inFlight[runId] = new RunProgress(generationId, parentRunId, durable);

        await PublishAsync(
            LifecycleEventTypes.RunStarted,
            new RunStartedPayload
            {
                RunId = runId,
                GenerationId = generationId,
                Cause = new LifecycleRunCause { Kind = cause, ToolCallId = causeToolCallId },
                WasForked = wasForked,
                AgentKind = _services.AgentKind,
                ModelId = _services.ModelId,
            },
            BuildCorrelation(runId, generationId, parentRunId, causeToolCallId),
            startedAt,
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Emits <see cref="LifecycleEventTypes.TurnCompleted"/> for a turn that reached its final
    /// state, and advances the run's turn count.
    /// </summary>
    /// <param name="runId">The run the turn belongs to.</param>
    /// <param name="generationId">The turn that completed.</param>
    /// <param name="outcome">How it ended. See <see cref="LifecycleTurnOutcomes"/>.</param>
    /// <param name="messageCount">How many complete messages the turn produced.</param>
    /// <param name="toolCallCount">How many tool calls it requested.</param>
    /// <param name="usage">Usage for the turn, when the provider reported any.</param>
    /// <param name="error">The failure, when the turn did not complete normally.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <remarks>
    /// The count is kept in memory and handed to the store once, at the terminal boundary. A
    /// durable write per turn would buy nothing a live process needs — it only matters to a process
    /// recovering someone else's abandoned run, which by definition cannot know what happened
    /// anyway. See <see cref="ReconcileInterruptedRunsAsync"/>.
    /// </remarks>
    public async Task TurnCompletedAsync(
        string runId,
        string generationId,
        string? outcome = null,
        int messageCount = 0,
        int toolCallCount = 0,
        LifecycleUsage? usage = null,
        LifecycleError? error = null,
        CancellationToken ct = default)
    {
        if (!IsEnabled)
        {
            return;
        }

        var turnIndex = _inFlight.TryGetValue(runId, out var progress) ? progress.NextTurnIndex() : 0;

        await PublishAsync(
            LifecycleEventTypes.TurnCompleted,
            new TurnCompletedPayload
            {
                RunId = runId,
                GenerationId = generationId,
                TurnIndex = turnIndex,
                Outcome = outcome ?? LifecycleTurnOutcomes.Completed,
                MessageCount = messageCount,
                ToolCallCount = toolCallCount,
                Usage = usage,
                Error = error,
            },
            BuildCorrelation(runId, generationId, progress?.ParentRunId),
            _services.TimeProvider.GetUtcNow(),
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Emits <see cref="LifecycleEventTypes.ToolCompleted"/> for a tool call that reached its final
    /// state.
    /// </summary>
    /// <param name="runId">The run that requested the call.</param>
    /// <param name="generationId">The turn that requested it, when still attributable.</param>
    /// <param name="toolCallId">The call that completed.</param>
    /// <param name="toolName">The tool's registered name.</param>
    /// <param name="outcome">How it ended. See <see cref="LifecycleToolOutcomes"/>.</param>
    /// <param name="wasDeferred">
    /// Whether it deferred and resolved after its requesting run had already ended.
    /// </param>
    /// <param name="durationMilliseconds">Dispatch to final state, when measured.</param>
    /// <param name="approval">The approval decision, when approval was configured.</param>
    /// <param name="error">The failure, when the outcome is not success.</param>
    /// <param name="toolKind">Where it executed. See <see cref="LifecycleToolKinds"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <remarks>
    /// For a delayed result this is emitted <em>before</em> the child run it causes starts, so a
    /// subscriber reading the stream in order sees the cause ahead of its effect rather than a child
    /// run whose reason has not arrived yet. The requesting run is generally terminal by then, so
    /// unlike the run and turn events this one does not consult the in-flight table for anything
    /// beyond lineage.
    /// </remarks>
    public async Task ToolCompletedAsync(
        string runId,
        string? generationId,
        string toolCallId,
        string toolName,
        string outcome,
        bool wasDeferred = false,
        long? durationMilliseconds = null,
        ToolApprovalSummary? approval = null,
        LifecycleError? error = null,
        string toolKind = LifecycleToolKinds.Host,
        CancellationToken ct = default)
    {
        if (!IsEnabled)
        {
            return;
        }

        var parentRunId = _inFlight.TryGetValue(runId, out var progress) ? progress.ParentRunId : null;

        await PublishAsync(
            LifecycleEventTypes.ToolCompleted,
            new ToolCompletedPayload
            {
                RunId = runId,
                GenerationId = generationId,
                ToolCallId = toolCallId,
                ToolName = toolName,
                ToolKind = toolKind,
                Outcome = outcome,
                WasDeferred = wasDeferred,
                DurationMilliseconds = durationMilliseconds,
                Approval = approval,
                Error = error,
            },
            BuildCorrelation(runId, generationId, parentRunId, toolCallId),
            _services.TimeProvider.GetUtcNow(),
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Durably records a deferral, when a store is configured.
    /// </summary>
    /// <param name="runId">The run that requested the call.</param>
    /// <param name="record">The deferral. The store assigns its ordinal.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <remarks>
    /// Best-effort, like <see cref="RunStartedAsync"/>: a store that cannot take the write leaves
    /// the deferral tracked in memory only, which is exactly the behavior of a loop with no
    /// lifecycle store at all. Failing the run here would make enabling observation able to break
    /// a conversation that works without it.
    /// </remarks>
    public async Task RecordDeferredToolCallAsync(
        string runId,
        DeferredToolCallRecord record,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        if (_store == null)
        {
            return;
        }

        try
        {
            _ = await _store.RecordDeferredToolCallAsync(runId, record, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Could not record deferral of tool call {ToolCallId} for run {RunId} on thread {ThreadId}; "
                    + "continuing with in-process tracking only",
                record.ToolCallId,
                runId,
                _threadId);
        }
    }

    /// <summary>
    /// Durably resolves a deferred call, when a store is configured.
    /// </summary>
    /// <param name="toolCallId">The deferred call to resolve.</param>
    /// <param name="resolutionFingerprint">A stable fingerprint of the resolution content.</param>
    /// <param name="childRunId">The child run this resolution causes, when it causes one.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// What the attempt did, or <see cref="DeferredResolutionOutcome.Resolved"/> when no store is
    /// configured — with nothing durable to disagree with, the in-memory decision stands alone.
    /// </returns>
    /// <remarks>
    /// Unlike the other store call-throughs here, a failure is <b>propagated</b>. This one is not
    /// observation: its answer decides whether a resolution is applied, retried, or refused, and a
    /// caller told "resolved" on a write that never landed would have no way to discover that the
    /// result it delivered is gone.
    /// </remarks>
    public async Task<DeferredResolutionOutcome> TryResolveDeferredToolCallAsync(
        string toolCallId,
        string resolutionFingerprint,
        string? childRunId,
        CancellationToken ct = default)
    {
        if (_store == null)
        {
            return DeferredResolutionOutcome.Resolved;
        }

        return await _store.TryResolveDeferredToolCallAsync(
            _threadId,
            toolCallId,
            resolutionFingerprint,
            childRunId,
            _services.TimeProvider.GetUtcNow(),
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Attempts to end a run, emitting <see cref="LifecycleEventTypes.RunCompleted"/> if this
    /// caller is the one that ended it.
    /// </summary>
    /// <param name="runId">The run to end.</param>
    /// <param name="generationId">
    /// The run's originating generation id. Only used when the run is not tracked in memory.
    /// </param>
    /// <param name="outcome">How it ended. See <see cref="LifecycleRunOutcomes"/>.</param>
    /// <param name="error">The failure, when the outcome is an error.</param>
    /// <param name="usage">Usage accumulated across the run.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// <see langword="true"/> when this call ended the run and published its completion;
    /// <see langword="false"/> when the run was already ended, was never started here, or lifecycle
    /// is disabled.
    /// </returns>
    public async Task<bool> TryCompleteRunAsync(
        string runId,
        string generationId,
        string? outcome = null,
        LifecycleError? error = null,
        LifecycleUsage? usage = null,
        CancellationToken ct = default)
    {
        if (!IsEnabled || !_inFlight.TryRemove(runId, out var progress))
        {
            return false;
        }

        var terminalAt = _services.TimeProvider.GetUtcNow();
        var resolvedOutcome = outcome ?? LifecycleRunOutcomes.Completed;

        if (_store != null && progress.Durable)
        {
            try
            {
                var won = await _store.TryMarkRunTerminalAsync(
                    runId,
                    resolvedOutcome,
                    progress.TurnCount,
                    terminalAt,
                    ct).ConfigureAwait(false);
                if (!won)
                {
                    return false;
                }
            }
            catch (Exception ex)
            {
                // A store that cannot answer offers no cross-process protection either way, and the
                // in-process removal above has already guaranteed at most one publish here. Losing
                // the completion outright would strand the subscriber on an unpaired start forever,
                // so the in-process decision stands.
                _logger.LogWarning(
                    ex,
                    "Could not durably terminalize run {RunId} on thread {ThreadId}; "
                        + "publishing completion on the in-process decision",
                    runId,
                    _threadId);
            }
        }

        await PublishRunCompletedAsync(
            runId,
            string.IsNullOrEmpty(generationId) ? progress.GenerationId : generationId,
            progress.ParentRunId,
            resolvedOutcome,
            progress.TurnCount,
            usage,
            error,
            terminalAt,
            ct).ConfigureAwait(false);

        return true;
    }

    /// <summary>
    /// Ends every run this finalizer still believes is in flight.
    /// </summary>
    /// <param name="outcome">
    /// How they ended — <see cref="LifecycleRunOutcomes.Cancelled"/> when the loop was stopped,
    /// <see cref="LifecycleRunOutcomes.Interrupted"/> when it was torn down.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <remarks>
    /// Called on the loop's stop and dispose paths. A run abandoned because the process is going
    /// away still has to complete, or a subscriber holds an unpaired start until the thread is next
    /// loaded — and if the store was unavailable, forever.
    /// </remarks>
    public async Task TerminalizeOutstandingAsync(string outcome, CancellationToken ct = default)
    {
        if (!IsEnabled)
        {
            return;
        }

        // Snapshot the keys: TryCompleteRunAsync mutates the dictionary as it wins each run.
        foreach (var runId in _inFlight.Keys.ToList())
        {
            _ = await TryCompleteRunAsync(runId, generationId: string.Empty, outcome, ct: ct)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Ends runs left behind by a process that is gone, as
    /// <see cref="LifecycleRunOutcomes.Interrupted"/>.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <remarks>
    /// <para>
    /// Called once per process start, alongside run-ledger reconciliation. Every run
    /// <see cref="IRunLifecycleStore.ListNonTerminalRunsAsync"/> returns belonged to an incarnation
    /// that cannot still be running it, so each is ended and its completion published.
    /// </para>
    /// <para>
    /// The turn count reported is the one that was durable, which for an abandoned run is zero: the
    /// process that performed those turns took the count with it. That is what
    /// <see cref="LifecycleRunOutcomes.Interrupted"/> means here — a recovery, not a report.
    /// </para>
    /// <para>
    /// Correlation comes from the stored record rather than from this agent's lineage. The run
    /// belonged to a different incarnation, and stamping it with the current agent's parentage
    /// would attribute it to a spawn that never happened.
    /// </para>
    /// </remarks>
    public async Task ReconcileInterruptedRunsAsync(CancellationToken ct = default)
    {
        if (_store == null)
        {
            return;
        }

        try
        {
            var dangling = await _store.ListNonTerminalRunsAsync(_threadId, ct).ConfigureAwait(false);
            foreach (var run in dangling)
            {
                var terminalAt = _services.TimeProvider.GetUtcNow();
                var won = await _store.TryMarkRunTerminalAsync(
                    run.RunId,
                    LifecycleRunOutcomes.Interrupted,
                    run.TurnCount,
                    terminalAt,
                    ct).ConfigureAwait(false);
                if (!won)
                {
                    continue;
                }

                _logger.LogWarning(
                    "Marking dangling run {RunId} interrupted on restart for thread {ThreadId}",
                    run.RunId,
                    _threadId);

                await PublishAsync(
                    LifecycleEventTypes.RunCompleted,
                    new RunCompletedPayload
                    {
                        RunId = run.RunId,
                        GenerationId = run.GenerationId,
                        Outcome = LifecycleRunOutcomes.Interrupted,
                        TurnCount = run.TurnCount,
                    },
                    new LifecycleCorrelation
                    {
                        ThreadId = run.ThreadId,
                        RunId = run.RunId,
                        GenerationId = string.IsNullOrEmpty(run.GenerationId) ? null : run.GenerationId,
                        ParentRunId = run.ParentRunId,
                        ParentThreadId = run.ParentThreadId,
                        SpawningToolCallId = run.SpawningToolCallId,
                        SubAgentId = run.SubAgentId,
                        ToolCallId = run.CauseToolCallId,
                    },
                    terminalAt,
                    ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Lifecycle reconciliation failed for thread {ThreadId}; continuing without it",
                _threadId);
        }
    }

    private Task PublishRunCompletedAsync(
        string runId,
        string generationId,
        string? parentRunId,
        string outcome,
        int turnCount,
        LifecycleUsage? usage,
        LifecycleError? error,
        DateTimeOffset occurredAt,
        CancellationToken ct) =>
        PublishAsync(
            LifecycleEventTypes.RunCompleted,
            new RunCompletedPayload
            {
                RunId = runId,
                GenerationId = generationId,
                Outcome = outcome,
                TurnCount = turnCount,
                Usage = usage,
                Error = error,
            },
            BuildCorrelation(runId, generationId, parentRunId),
            occurredAt,
            ct);

    private LifecycleCorrelation BuildCorrelation(
        string runId,
        string? generationId,
        string? parentRunId,
        string? toolCallId = null)
    {
        var lineage = _services.Lineage;
        return new LifecycleCorrelation
        {
            ThreadId = _threadId,
            RunId = runId,
            GenerationId = string.IsNullOrEmpty(generationId) ? null : generationId,
            ParentRunId = parentRunId ?? lineage.ParentRunId,
            ParentThreadId = lineage.ParentThreadId,
            SpawningToolCallId = lineage.SpawningToolCallId,
            SubAgentId = lineage.SubAgentId,
            ToolCallId = toolCallId,
        };
    }

    private async Task PublishAsync<TPayload>(
        string eventType,
        TPayload payload,
        LifecycleCorrelation correlation,
        DateTimeOffset occurredAt,
        CancellationToken ct)
        where TPayload : class
    {
        if (!_publishes)
        {
            return;
        }

        try
        {
            var envelope = LifecycleSerializer.CreateEnvelope(
                eventType,
                payload,
                _sourceStreamId,
                _services.SequenceAllocator,
                occurredAt,
                correlation);

            await _services.Publisher.PublishAsync(envelope, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Including cancellation: a subscriber that could not be reached because the run was
            // torn down is still just a dropped event, and the caller is mid-teardown already.
            _logger.LogWarning(
                ex,
                "Dropping lifecycle event {EventType} for thread {ThreadId}",
                eventType,
                _threadId);
        }
    }

    /// <summary>
    /// What this process knows about a run it started: enough to end it correctly, and nothing that
    /// would need to be kept in sync with anything else.
    /// </summary>
    private sealed class RunProgress(string generationId, string? parentRunId, bool durable)
    {
        private int _turnCount;

        /// <summary>The run's originating generation id.</summary>
        public string GenerationId { get; } = generationId;

        /// <summary>The run that caused this one, when one did.</summary>
        public string? ParentRunId { get; } = parentRunId;

        /// <summary>
        /// Whether the start reached the store. False means the terminal decision cannot consult a
        /// durable row, because there is none to consult.
        /// </summary>
        public bool Durable { get; } = durable;

        /// <summary>Turns completed so far.</summary>
        public int TurnCount => Volatile.Read(ref _turnCount);

        /// <summary>Counts a completed turn and returns its 1-based index.</summary>
        public int NextTurnIndex() => Interlocked.Increment(ref _turnCount);
    }
}
