using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Models;
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

    // The runs this finalizer believes are in flight. Presence is the in-process terminalization
    // token: whoever removes an entry owns ending that run.
    private readonly ConcurrentDictionary<string, RunProgress> _inFlight = new(StringComparer.Ordinal);

    // Context sources already reported for this thread, by dedup identity. Presence is the right
    // NOT to report again: the same block rides in every subsequent request, and re-announcing it
    // each turn would say "new context arrived" once per model round trip forever.
    private readonly ConcurrentDictionary<string, byte> _reportedContext = new(StringComparer.Ordinal);

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
        ILogger? logger = null
    )
    {
        ArgumentNullException.ThrowIfNull(services);

        if (string.IsNullOrWhiteSpace(threadId))
        {
            throw new ArgumentException("A thread id must be non-empty.", nameof(threadId));
        }

        _threadId = threadId;
        _services = services;
        _logger = logger ?? NullLogger.Instance;
        PublishesEvents = services.PublishesEvents;

        var optedIn = PublishesEvents || services.LifecycleStore != null;
        _store = optedIn ? services.LifecycleStore ?? fallbackStore : null;

        _sourceStreamId = LifecycleSourceStream.ForThread(threadId);
    }

    /// <summary>
    /// Whether this finalizer does anything. When false every member returns immediately and the
    /// loop's behavior is byte-for-byte what it was before lifecycle hooks existed.
    /// </summary>
    public bool IsEnabled => PublishesEvents || _store != null;

    /// <summary>
    /// Whether events reach a subscriber. Narrower than <see cref="IsEnabled"/>, for callers that
    /// would have to do real work to build an event nobody would receive.
    /// </summary>
    public bool PublishesEvents { get; }

    /// <summary>
    /// Whether a run this process started is durably recorded as having started.
    /// </summary>
    /// <param name="runId">The run to ask about.</param>
    /// <returns>
    /// <see langword="true"/> when the start reached the store, or when there is no store to reach —
    /// with no store there is no recovery either, so nothing downstream can double-run the work.
    /// <see langword="false"/> when a store is configured and the start did not land in it.
    /// </returns>
    /// <remarks>
    /// The run row is what recovery reads as "this one has begun". A caller whose correctness rests
    /// on that marker — a delayed-result child run, which is re-queued after a restart precisely
    /// because no row names it — must not do irreversible work while the marker is missing, or the
    /// next process will do that work all over again.
    /// </remarks>
    public bool IsRunStartDurable(string runId) =>
        _store == null || (_inFlight.TryGetValue(runId, out var progress) && progress.Durable);

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
        CancellationToken ct = default
    )
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
                await _store
                    .RecordRunStartedAsync(
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
                        ct
                    )
                    .ConfigureAwait(false);
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
                    _threadId
                );
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
                ct
            )
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Registers that a turn has begun, so exactly one <see cref="LifecycleEventTypes.TurnCompleted"/>
    /// can later be emitted for it.
    /// </summary>
    /// <param name="runId">The run the turn belongs to.</param>
    /// <param name="generationId">The generation id the turn was accepted under.</param>
    /// <remarks>
    /// <para>
    /// This deliberately emits nothing. ADR 0002 has no <c>turn_started</c> event — a turn is
    /// reported at its final state or not at all — so the registration exists purely to give
    /// <see cref="TurnCompletedAsync"/> something to consume. Presence in the run's open-turn set is
    /// the terminalization token, exactly as presence in the in-flight table is for a run.
    /// </para>
    /// <para>
    /// What counts as a turn is the caller's judgement, and it differs by provider: the raw loop
    /// mints a fresh generation id per model round-trip, while a CLI-backed loop runs its own
    /// agentic loop behind one generation id and therefore has a single turn per run. Both are
    /// correct — the seam reports the turn its loop actually accepted, so a subscriber can pair
    /// starts with completions without knowing which loop produced them.
    /// </para>
    /// </remarks>
    public void TurnStarted(string runId, string generationId)
    {
        if (!IsEnabled || string.IsNullOrEmpty(generationId))
        {
            return;
        }

        if (_inFlight.TryGetValue(runId, out var progress))
        {
            progress.OpenTurn(generationId);
        }
    }

    /// <summary>
    /// Folds one message a turn produced into that turn's report.
    /// </summary>
    /// <param name="runId">The run the turn belongs to.</param>
    /// <param name="generationId">The turn that produced the message.</param>
    /// <param name="message">The message.</param>
    /// <remarks>
    /// <para>
    /// Counting lives here rather than in each loop so that <c>message_count</c> means the same
    /// thing regardless of which loop produced it. The distinction that matters is complete messages
    /// versus streaming fragments: a CLI-backed loop publishes hundreds of text deltas per turn and
    /// a raw loop publishes none, so a count that included them would say more about the transport
    /// than about the turn.
    /// </para>
    /// <para>
    /// Usage is taken from the last <see cref="UsageMessage"/> the turn reported rather than summed
    /// across them, for the reason given on <see cref="LifecycleUsageMapper"/>.
    /// </para>
    /// <para>
    /// A message for a turn that never began, or has already been reported, is ignored — the turn's
    /// figures are fixed at the moment it is reported and nothing arriving later can revise them.
    /// </para>
    /// </remarks>
    public void ObserveTurnMessage(string runId, string generationId, IMessage message)
    {
        if (!IsEnabled || message == null || string.IsNullOrEmpty(generationId))
        {
            return;
        }

        if (_inFlight.TryGetValue(runId, out var progress))
        {
            progress.ObserveTurnMessage(generationId, message);
        }
    }

    /// <summary>
    /// Emits <see cref="LifecycleEventTypes.TurnCompleted"/> for a turn that reached its final
    /// state, and advances the run's turn count.
    /// </summary>
    /// <param name="runId">The run the turn belongs to.</param>
    /// <param name="generationId">The turn that completed.</param>
    /// <param name="outcome">How it ended. See <see cref="LifecycleTurnOutcomes"/>.</param>
    /// <param name="error">The failure, when the turn did not complete normally.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// <see langword="true"/> when this call ended the turn and published its completion;
    /// <see langword="false"/> when the turn was already reported, was never registered by
    /// <see cref="TurnStarted"/>, or lifecycle is disabled.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <b>Final state only, exactly once.</b> A turn is reported when it stops, never while it
    /// streams, and the open-turn set makes the second report a no-op rather than a duplicate event.
    /// That matters because the normal path and the terminal sweep in
    /// <see cref="TryCompleteRunAsync"/> can both reach a turn — on an ordinary turn the loop gets
    /// there first with real counts, and on an abandoned one the sweep does, with the run's outcome.
    /// Whichever arrives first wins and the other is dropped.
    /// </para>
    /// <para>
    /// The count is kept in memory and handed to the store once, at the terminal boundary. A
    /// durable write per turn would buy nothing a live process needs — it only matters to a process
    /// recovering someone else's abandoned run, which by definition cannot know what happened
    /// anyway. See <see cref="ReconcileInterruptedRunsAsync"/>.
    /// </para>
    /// </remarks>
    public async Task<bool> TurnCompletedAsync(
        string runId,
        string generationId,
        string? outcome = null,
        LifecycleError? error = null,
        CancellationToken ct = default
    )
    {
        if (
            !IsEnabled
            || !_inFlight.TryGetValue(runId, out var progress)
            || !progress.TryCloseTurn(generationId, out var tally)
        )
        {
            return false;
        }

        var turnIndex = progress.NextTurnIndex();

        await PublishAsync(
                LifecycleEventTypes.TurnCompleted,
                new TurnCompletedPayload
                {
                    RunId = runId,
                    GenerationId = generationId,
                    TurnIndex = turnIndex,
                    Outcome = outcome ?? LifecycleTurnOutcomes.Completed,
                    MessageCount = tally.MessageCount,
                    ToolCallCount = tally.ToolCallCount,
                    Usage = LifecycleUsageMapper.ToLifecycleUsage(tally.Usage),
                    Error = error,
                },
                BuildCorrelation(runId, generationId, progress.ParentRunId),
                _services.TimeProvider.GetUtcNow(),
                ct
            )
            .ConfigureAwait(false);

        return true;
    }

    /// <summary>
    /// Emits <see cref="LifecycleEventTypes.ContextLoaded"/> for the context blocks a request is
    /// about to carry to the model for the first time.
    /// </summary>
    /// <param name="runId">The run whose request carries the context.</param>
    /// <param name="generationId">The turn whose request carries it.</param>
    /// <param name="blocks">
    /// The blocks found in the request, in request order — from
    /// <see cref="RenderedContextBlock.ScanRequest"/> or <see cref="RenderedContextBlock.Scan"/>.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// <see langword="true"/> when something was reported; <see langword="false"/> when the request
    /// carried no context the model had not already been given, or nobody is listening.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <b>Delivered, not discovered.</b> The caller reads these off the request it is about to
    /// dispatch, so a discovery that was queued, cancelled, superseded, or rediscovered without ever
    /// reaching a request produces no event — there is no request for it to have been found in.
    /// </para>
    /// <para>
    /// <b>Once per source, per agent.</b> A boot seed sits in the system prompt of every request for
    /// the life of the conversation, so first delivery is the only interesting moment and repeats
    /// are dropped by dedup identity. The set is in memory: a conversation reloaded in a new process
    /// reports its context again on the first dispatch, which is the honest answer — that process
    /// did hand the model the context, and has no record that anyone else already had. A subscriber
    /// that cares can compare <see cref="ContextLoadedPayload.RenderedHash"/>.
    /// </para>
    /// <para>
    /// <b>What the hash covers.</b> When one request first carries several sources, the reported
    /// text is those blocks concatenated in request order. Each part is byte-exact; nothing is
    /// inserted between them, because a separator would be bytes the model was never sent.
    /// </para>
    /// </remarks>
    public async Task<bool> ContextLoadedAsync(
        string runId,
        string generationId,
        IReadOnlyList<RenderedContextBlock> blocks,
        CancellationToken ct = default
    )
    {
        if (!PublishesEvents || blocks == null || blocks.Count == 0)
        {
            return false;
        }

        List<RenderedContextBlock>? fresh = null;
        foreach (var block in blocks)
        {
            if (_reportedContext.TryAdd(block.DedupIdentity, 0))
            {
                (fresh ??= []).Add(block);
            }
        }

        if (fresh == null)
        {
            return false;
        }

        var rendered = fresh.Count == 1 ? fresh[0].Text : string.Concat(fresh.Select(b => b.Text));
        var renderedBytes = Encoding.UTF8.GetBytes(rendered);
        var parentRunId = _inFlight.TryGetValue(runId, out var progress) ? progress.ParentRunId : null;

        await PublishAsync(
                LifecycleEventTypes.ContextLoaded,
                new ContextLoadedPayload
                {
                    RunId = runId,
                    GenerationId = generationId,
                    Sources = [.. fresh.Select(b => b.ToLifecycleSource())],
                    RenderedHash = Convert.ToHexString(SHA256.HashData(renderedBytes)).ToLowerInvariant(),
                    RenderedByteCount = renderedBytes.Length,
                    RenderedText = rendered,
                },
                BuildCorrelation(runId, generationId, parentRunId),
                _services.TimeProvider.GetUtcNow(),
                ct
            )
            .ConfigureAwait(false);

        return true;
    }

    /// <summary>
    /// Emits <see cref="LifecycleEventTypes.ContextMeasured"/> for one context observation (#681).
    /// </summary>
    /// <param name="observation">The observation as the loop recorded it.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><see langword="true"/> when an event went out.</returns>
    /// <remarks>
    /// Emitted twice per generation — estimated, then measured — and stamped with the observation's own
    /// time rather than "now", so the event and the persisted record agree on when the size was taken.
    /// The payload is content-free: counts, ratios and ids only.
    /// </remarks>
    public async Task<bool> ContextMeasuredAsync(ContextObservation observation, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(observation);

        if (!PublishesEvents)
        {
            return false;
        }

        var parentRunId = _inFlight.TryGetValue(observation.RunId, out var progress) ? progress.ParentRunId : null;

        await PublishAsync(
                LifecycleEventTypes.ContextMeasured,
                new ContextMeasuredPayload
                {
                    RunId = observation.RunId,
                    GenerationId = observation.GenerationId,
                    GenerationOrdinal = observation.GenerationOrdinal,
                    AgentId = observation.AgentId,
                    EffectiveModelId = observation.EffectiveModelId,
                    Provenance = observation.Provenance.ToString(),
                    EstimatedInputTokens = observation.EstimatedInputTokens,
                    MeasuredInputTokens = observation.MeasuredInputTokens,
                    WindowTokens = observation.WindowTokens,
                    ReserveTokens = observation.ReserveTokens,
                    Utilization = observation.Utilization,
                    ActiveCheckpointId = observation.ActiveCheckpointId,
                    RowsInView = observation.RowsInView,
                },
                BuildCorrelation(observation.RunId, observation.GenerationId, parentRunId),
                observation.ObservedAtUtc,
                ct
            )
            .ConfigureAwait(false);

        return true;
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
        CancellationToken ct = default
    )
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
                ct
            )
            .ConfigureAwait(false);
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
        CancellationToken ct = default
    )
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
                _threadId
            );
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
        CancellationToken ct = default
    )
    {
        if (_store == null)
        {
            return DeferredResolutionOutcome.Resolved;
        }

        return await _store
            .TryResolveDeferredToolCallAsync(
                _threadId,
                toolCallId,
                resolutionFingerprint,
                childRunId,
                _services.TimeProvider.GetUtcNow(),
                ct
            )
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Durably names the child run that will carry an already-resolved call's result, unless one is
    /// already named.
    /// </summary>
    /// <param name="toolCallId">The resolved call.</param>
    /// <param name="childRunId">The child run the caller proposes.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// The child run id the durable record names once this returns — the caller's, or the one a
    /// previous process already committed to, which the caller must adopt rather than start a second
    /// continuation for the same result. <see langword="null"/> means the record refused the id
    /// because the call is unknown or still unresolved, which no retry will change. Without a store
    /// the caller's own id is returned: there is nothing durable to disagree with.
    /// </returns>
    /// <remarks>
    /// A store failure throws rather than returning <see langword="null"/>, exactly as
    /// <see cref="TryResolveDeferredToolCallAsync"/> does, because what the caller must do about it
    /// depends on where it is: before the resolution touches history the whole resolution can still
    /// be refused and retried, whereas afterwards it can only be reported. Collapsing "the store is
    /// down" into "the record says no" would take that choice away from both.
    /// </remarks>
    public async Task<string?> AttachDeferredChildRunAsync(
        string toolCallId,
        string childRunId,
        CancellationToken ct = default
    )
    {
        if (_store == null)
        {
            return childRunId;
        }

        return await _store
            .AttachDeferredChildRunAsync(_threadId, toolCallId, childRunId, _services.TimeProvider.GetUtcNow(), ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Lists the thread's durable run lifecycle records, or an empty list when no store is
    /// configured or the store cannot be read.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <remarks>
    /// Read-only and best-effort: recovery uses it to find continuations a dead process owed, and a
    /// store that cannot answer must degrade to the pre-existing "recover from history alone"
    /// behaviour rather than fail the recovery.
    /// </remarks>
    public async Task<IReadOnlyList<RunLifecycleState>> ListRunLifecycleAsync(CancellationToken ct = default)
    {
        if (_store == null)
        {
            return [];
        }

        try
        {
            return await _store.ListRunLifecycleAsync(_threadId, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Could not read run lifecycle records for thread {ThreadId}; recovering from "
                    + "persisted history alone",
                _threadId
            );
            return [];
        }
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
        CancellationToken ct = default
    )
    {
        if (!IsEnabled || !_inFlight.TryGetValue(runId, out var progress))
        {
            return false;
        }

        var resolvedOutcome = outcome ?? LifecycleRunOutcomes.Completed;

        // Any turn still open here is one no loop reported: the run is ending through an error,
        // a cancellation, or a teardown that skipped the normal per-turn seam. Report it before the
        // run ends, so a subscriber never sees a run complete having been told nothing about the
        // turn it died in. A turn the loop already reported is not in this set, so the ordinary
        // path is untouched.
        //
        // This runs BEFORE the run is removed from the in-flight table, because
        // TurnCompletedAsync resolves lineage and the turn index through that entry.
        foreach (var openGenerationId in progress.OpenTurnIds())
        {
            _ = await TurnCompletedAsync(
                    runId,
                    openGenerationId,
                    TurnOutcomeForRun(resolvedOutcome),
                    error: error,
                    ct: ct
                )
                .ConfigureAwait(false);
        }

        if (!_inFlight.TryRemove(runId, out _))
        {
            // Another caller terminalized between the lookup above and here. It owns the run.
            return false;
        }

        var terminalAt = _services.TimeProvider.GetUtcNow();

        if (_store != null && progress.Durable)
        {
            try
            {
                var won = await _store
                    .TryMarkRunTerminalAsync(runId, resolvedOutcome, progress.TurnCount, terminalAt, ct)
                    .ConfigureAwait(false);
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
                    _threadId
                );
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
                ct
            )
            .ConfigureAwait(false);

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
            _ = await TryCompleteRunAsync(runId, generationId: string.Empty, outcome, ct: ct).ConfigureAwait(false);
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
                var won = await _store
                    .TryMarkRunTerminalAsync(run.RunId, LifecycleRunOutcomes.Interrupted, run.TurnCount, terminalAt, ct)
                    .ConfigureAwait(false);
                if (!won)
                {
                    continue;
                }

                _logger.LogWarning(
                    "Marking dangling run {RunId} interrupted on restart for thread {ThreadId}",
                    run.RunId,
                    _threadId
                );

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
                        ct
                    )
                    .ConfigureAwait(false);
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
                _threadId
            );
        }
    }

    /// <summary>
    /// How to describe a turn that was still open when its run ended.
    /// </summary>
    /// <remarks>
    /// A run that reached its turn ceiling stopped <em>between</em> turns — the last turn itself
    /// finished normally — so <c>max_turns</c> describes the run and would misdescribe the turn.
    /// Every other terminal run outcome is one the turn shared.
    /// </remarks>
    private static string TurnOutcomeForRun(string runOutcome) =>
        runOutcome switch
        {
            LifecycleRunOutcomes.Error => LifecycleTurnOutcomes.Error,
            LifecycleRunOutcomes.Cancelled => LifecycleTurnOutcomes.Cancelled,
            LifecycleRunOutcomes.Interrupted => LifecycleTurnOutcomes.Interrupted,
            _ => LifecycleTurnOutcomes.Completed,
        };

    private Task PublishRunCompletedAsync(
        string runId,
        string generationId,
        string? parentRunId,
        string outcome,
        int turnCount,
        LifecycleUsage? usage,
        LifecycleError? error,
        DateTimeOffset occurredAt,
        CancellationToken ct
    ) =>
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
            ct
        );

    /// <summary>
    /// Publishes one of the three compaction events (spec 679 §5.5): <c>compaction_decided</c> for every
    /// policy pass, <c>compaction_applied</c> when a checkpoint reaches <c>Active</c>,
    /// <c>compaction_failed</c> when one is rejected or rolled back. Returns false when lifecycle
    /// publishing is off.
    /// </summary>
    public async Task<bool> CompactionAsync(
        string eventType,
        string runId,
        string generationId,
        CompactionPayload payload,
        CancellationToken ct = default
    )
    {
        ArgumentException.ThrowIfNullOrEmpty(eventType);
        ArgumentNullException.ThrowIfNull(payload);
        if (!PublishesEvents)
        {
            return false;
        }

        payload.RunId = runId;
        payload.GenerationId = generationId;
        var parentRunId = _inFlight.TryGetValue(runId, out var progress) ? progress.ParentRunId : null;
        await PublishAsync(
                eventType,
                payload,
                BuildCorrelation(runId, generationId, parentRunId),
                _services.TimeProvider.GetUtcNow(),
                ct
            )
            .ConfigureAwait(false);
        return true;
    }

    private LifecycleCorrelation BuildCorrelation(
        string runId,
        string? generationId,
        string? parentRunId,
        string? toolCallId = null
    )
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
        CancellationToken ct
    )
        where TPayload : class
    {
        if (!PublishesEvents)
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
                correlation
            );

            await _services.Publisher.PublishAsync(envelope, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Including cancellation: a subscriber that could not be reached because the run was
            // torn down is still just a dropped event, and the caller is mid-teardown already.
            _logger.LogWarning(ex, "Dropping lifecycle event {EventType} for thread {ThreadId}", eventType, _threadId);
        }
    }

    /// <summary>
    /// What this process knows about a run it started: enough to end it correctly, and nothing that
    /// would need to be kept in sync with anything else.
    /// </summary>
    private sealed class RunProgress(string generationId, string? parentRunId, bool durable)
    {
        private int _turnCount;

        // Turns that have begun and not yet been reported, with what each has produced so far.
        // Removing one is the right to publish its completion, which is what keeps a turn to a
        // single final event even when the loop and the terminal sweep both reach for it.
        private readonly ConcurrentDictionary<string, TurnTally> _openTurns = new(StringComparer.Ordinal);

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

        /// <summary>Marks a turn as begun and awaiting its final report.</summary>
        public void OpenTurn(string turnGenerationId) => _openTurns[turnGenerationId] = new TurnTally();

        /// <summary>Folds a message into an open turn's figures. Ignores unknown turns.</summary>
        public void ObserveTurnMessage(string turnGenerationId, IMessage message)
        {
            if (_openTurns.TryGetValue(turnGenerationId, out var tally))
            {
                tally.Observe(message);
            }
        }

        /// <summary>
        /// Claims the right to report a turn, handing back what it produced. Returns false when it
        /// was already reported or never begun, which is what makes a second completion a no-op
        /// instead of a duplicate event.
        /// </summary>
        public bool TryCloseTurn(string turnGenerationId, out TurnTally tally) =>
            _openTurns.TryRemove(turnGenerationId, out tally!);

        /// <summary>
        /// The turns still awaiting a report. A snapshot, not a drain: the caller closes each one
        /// through <see cref="TryCloseTurn"/>, so a turn the loop reports concurrently is left alone
        /// rather than reported twice.
        /// </summary>
        public IReadOnlyList<string> OpenTurnIds() => [.. _openTurns.Keys];
    }

    /// <summary>
    /// What one in-flight turn has produced so far.
    /// </summary>
    /// <remarks>
    /// Tool executions run concurrently with streaming in the raw loop, so messages can be folded in
    /// from more than one thread; the counters are interlocked and the usage reference is written
    /// atomically.
    /// </remarks>
    private sealed class TurnTally
    {
        private int _messageCount;
        private int _toolCallCount;
        private Usage? _usage;

        /// <summary>Complete messages observed, excluding streaming fragments.</summary>
        public int MessageCount => Volatile.Read(ref _messageCount);

        /// <summary>Tool calls the turn requested.</summary>
        public int ToolCallCount => Volatile.Read(ref _toolCallCount);

        /// <summary>The last usage the provider reported for the turn, if any.</summary>
        public Usage? Usage => Volatile.Read(ref _usage);

        /// <summary>Folds one message into the figures.</summary>
        public void Observe(IMessage message)
        {
            // Streaming fragments describe how a message arrived, not what the turn produced. The
            // complete message that supersedes them is counted when it lands.
            if (message is TextUpdateMessage or ReasoningUpdateMessage or ToolCallUpdateMessage)
            {
                return;
            }

            _ = Interlocked.Increment(ref _messageCount);

            switch (message)
            {
                case ToolsCallMessage aggregate:
                    // A provider that batches its calls into one message still requested each of
                    // them, and a loop downstream of the transformation middleware sees only the
                    // singular form — so the two cases are counted the same way and never both.
                    _ = Interlocked.Add(ref _toolCallCount, aggregate.ToolCalls?.Count ?? 0);
                    break;

                case ToolCallMessage:
                    _ = Interlocked.Increment(ref _toolCallCount);
                    break;

                case UsageMessage usage:
                    _ = Interlocked.Exchange(ref _usage, usage.Usage);
                    break;

                default:
                    // Text, reasoning, tool results and everything else count toward the message
                    // total and contribute nothing else.
                    break;
            }
        }
    }
}
