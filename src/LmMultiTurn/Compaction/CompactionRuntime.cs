using System.Runtime.CompilerServices;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Models;
using AchieveAi.LmDotnetTools.LmLifecycle;
using AchieveAi.LmDotnetTools.LmLifecycle.Payloads;
using AchieveAi.LmDotnetTools.LmMultiTurn.Lifecycle;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using AchieveAi.LmDotnetTools.LmMultiTurn.TodoBoard;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AchieveAi.LmDotnetTools.LmMultiTurn.Compaction;

/// <summary>The loop-side facts and hooks the runtime reads and calls; every member is a delegate so the runtime holds no loop reference.</summary>
internal sealed record CompactionRuntimeHost
{
    public required string ThreadId { get; init; }

    public string? SystemPrompt { get; init; }

    public IConversationStore? Store { get; init; }

    public IRunLedgerStore? RunLedgerStore { get; init; }

    public required GenerateReplyOptions DefaultOptions { get; init; }

    public IPricingResolver? Pricing { get; init; }

    /// <summary>The agent id observations and checkpoints are stamped with (<c>root</c> or <c>agent-N</c>).</summary>
    public string AgentId { get; init; } = "root";

    /// <summary>A copy of the in-memory history, in order.</summary>
    public required Func<IReadOnlyList<IMessage>> HistorySnapshot { get; init; }

    /// <summary>Owed continuations the loop still holds; interrupted turns are reported per request.</summary>
    public Func<int> OwedContinuations { get; init; } = () => 0;

    /// <summary>Deferred tool calls the live coordinator still tracks (deferred questions, parked waits).</summary>
    public Func<int> LiveDeferredCount { get; init; } = () => 0;

    /// <summary>The sub-agent roster at cut time.</summary>
    public Func<IReadOnlyList<AgentRef>> Roster { get; init; } = () => [];

    /// <summary>Adds a row to the in-memory history without persisting it (the store already has it).</summary>
    public required Action<IMessage> AppendInMemory { get; init; }

    /// <summary>Sends an activated checkpoint row to live subscribers, so a client need not reload to see it.</summary>
    public Func<IMessage, CancellationToken, ValueTask>? PublishLive { get; init; }

    /// <summary>
    ///     Estimated tokens of the tool definitions every request carries. The policy's estimate includes them;
    ///     the tool list is static for the life of the loop.
    /// </summary>
    public Func<long> ToolSchemaTokens { get; init; } = () => 0;

    /// <summary>Records the summary pass's usage under the compaction execution kind.</summary>
    public Action<UsageMessage, string, string?>? RecordSummaryUsage { get; init; }

    public RunTurnLifecycleFinalizer? Lifecycle { get; init; }

    public ILogger Logger { get; init; } = NullLogger.Instance;
}

/// <summary>What one pre-dispatch policy pass hands back to the turn.</summary>
internal sealed record CompactionPass(
    CompactionDecision Decision,
    IReadOnlyList<IMessage>? View,
    ContextOverflowException? Refusal
);

/// <summary>
/// The per-loop owner of the just-in-time compaction policy (spec 679 §5): builds the execution view,
/// evaluates the decision table once per generation immediately before the provider call, runs the
/// checkpoint pipeline for a compact-worthy answer, tracks the row identities the view needs, and
/// records every decision. It never runs on a timer or on inactivity — the loop calls it, and only from
/// the request path.
/// </summary>
/// <remarks>
/// <para>
/// <b>Row identity.</b> In-memory rows carry no <c>Seq</c>. The loop tells the runtime the persisted id
/// of every row it appends (<see cref="TrackPersisted"/>) and the runtime pairs restored rows with the
/// store's rows on recovery (<see cref="TrackRestoredAsync"/>); a compaction resolves ids to sequence
/// numbers against the rows it loads. A row the runtime cannot place is a reconciliation failure —
/// the pass answers <c>watermark_drift</c> rather than guessing where the boundary falls.
/// </para>
/// <para>
/// <b>Lazy store reads.</b> Rows 1–3, 5, 7 and 8 of the table are answered from the in-memory estimate,
/// the loop's own state and the thread metadata; the message rows are loaded only when the answer is
/// a compaction.
/// </para>
/// </remarks>
internal sealed class CompactionRuntime
{
    private sealed class RowIdentity
    {
        public string? Id { get; set; }
        public long? Seq { get; set; }
    }

    private readonly CompactionSetup _setup;
    private readonly CompactionRuntimeHost _host;
    private readonly CompactionPolicy _policy;
    private readonly ICheckpointSummarizer _summarizer;
    private readonly CheckpointPipeline _pipeline;
    private readonly TimeProvider _clock;
    private readonly ConditionalWeakTable<IMessage, RowIdentity> _identities = [];
    private readonly List<Task> _inFlightPersists = [];
    private readonly object _gate = new();

    private long _generationOrdinal;
    private bool _ordinalSeeded;
    private DateTimeOffset? _lastActivity;
    private bool _activitySeeded;
    private int _compactionsThisRun;
    private long _clearedThroughSeq;
    private ToolResultTightening? _tightened;
    private Measurement? _measurement;
    private readonly HashSet<string> _trimLogged = new(StringComparer.Ordinal);
    private long? _failureBackoffUntilOrdinal;
    private IReadOnlySet<string> _sizeRefusedRunIds = new HashSet<string>(StringComparer.Ordinal);
    private volatile bool _manualPending;
    private int _manualRunning;
    private string? _lastRecutGenerationId;
    private int _fitRecutsThisRun;
    private (string GenerationId, long Ordinal)? _evaluatedGeneration;
    private Task? _reconciled;

    /// <summary>
    ///     Restart recovery (<see cref="CompactionStateProjection.ReconcileAsync"/>), once per runtime and before anything
    ///     this runtime does can put a checkpoint in flight: an entry in flight at that point was left by a process that
    ///     died. Every path that reads or starts an attempt awaits it first, so a request that reaches a loop before the
    ///     loop restores its thread is not refused by a dead attempt, and a live attempt of this runtime is never
    ///     rejected. A failed reconcile is retried by the next caller.
    /// </summary>
    private async Task ReconcileOnceAsync(IConversationStore store, CancellationToken ct)
    {
        TaskCompletionSource? owner = null;
        Task reconciled;
        lock (_gate)
        {
            if (_reconciled is null || _reconciled.IsFaulted)
            {
                owner = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _reconciled = owner.Task;
            }

            reconciled = _reconciled;
        }

        if (owner is not null)
        {
            try
            {
                _ = await CompactionStateProjection
                    .ReconcileAsync(store, _host.ThreadId, _clock.GetUtcNow(), CancellationToken.None)
                    .ConfigureAwait(false);
                owner.SetResult();
            }
            catch (Exception ex)
            {
                owner.SetException(ex);
            }
        }

        await reconciled.WaitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    ///     The ordinal <see cref="EvaluateAsync"/> gave <paramref name="generationId"/>, or null when that is not
    ///     the generation it last evaluated. The loop observes the generation under the same ordinal, so its
    ///     measurement, the policy's decision and <see cref="CompactionState.LastCheckpointGenerationOrdinal"/>
    ///     all name one generation.
    /// </summary>
    public long? OrdinalOf(string generationId) =>
        _evaluatedGeneration is { } evaluated && evaluated.GenerationId == generationId ? evaluated.Ordinal : null;

    /// <summary>The reason a manual compaction's checkpoint and decision carry.</summary>
    public const string ManualReason = "manual";

    /// <summary>True when a manual request may be waiting: the loop runs it before it next waits for input.</summary>
    public bool HasPendingManual => _manualPending;

    /// <summary>True when this loop would accept a manual compaction request at all.</summary>
    public bool AcceptsManual =>
        Mode == CompactionMode.Compact && _host.Store is not null && !Options.IsKilled(_setup.ReadEnvironment);

    /// <summary>
    ///     Queues an operator's compaction (spec 679 §5.7): refused with a <see cref="ManualCompactionRefusals"/>
    ///     reason, or persisted as <see cref="CompactionState.PendingManual"/> for the loop to run once — before
    ///     the next provider call of the active run, or right away on an idle loop.
    /// </summary>
    public async Task<ManualCompactionResult> RequestManualAsync(string? focus, bool runActive, CancellationToken ct)
    {
        if (!AcceptsManual)
        {
            return ManualCompactionResult.Refused(ManualCompactionRefusals.CompactionOff);
        }

        var store = _host.Store!;
        await ReconcileOnceAsync(store, ct).ConfigureAwait(false);
        var metadata = await store.LoadMetadataAsync(_host.ThreadId, ct).ConfigureAwait(false);
        if (metadata?.SessionMappings is { Count: > 0 })
        {
            return ManualCompactionResult.Refused(ManualCompactionRefusals.ProviderOwnedSession);
        }

        var state = CompactionStateProjection.FromMetadata(metadata);
        if (Volatile.Read(ref _manualRunning) != 0 || state?.InFlight.Any() == true)
        {
            return ManualCompactionResult.Refused(ManualCompactionRefusals.InProgress);
        }

        if (state?.PendingManual is not null)
        {
            return ManualCompactionResult.Refused(ManualCompactionRefusals.AlreadyPending);
        }

        // Durable rows, not the in-memory history: a loop the host just created may not have restored its
        // history yet, and an empty snapshot would refuse a conversation that has plenty to compact.
        var boundary = state?.ActiveBoundarySeq ?? 0;
        var rows = SequencedHistory.FromPersisted(
            await store.LoadMessagesAsync(_host.ThreadId, ct).ConfigureAwait(false)
        );
        if (rows.Count(r => !r.IsCheckpointRow && r.Seq > boundary) < 2)
        {
            return ManualCompactionResult.Refused(ManualCompactionRefusals.NothingToCompact);
        }

        // An idle loop's rows are what the claim will cut, so the cut rules are asked now and a request with no legal
        // cut is refused here rather than accepted and refused later. A running loop's history is still moving. Only
        // a blocker (unsafe state, an open or deferred call, a protected run) is no_safe_boundary; a skip R3 alone
        // caused means every row since the checkpoint is the tail the rules keep, which is nothing to compact yet.
        if (!runActive)
        {
            var cut = await SelectCutAsync(
                    rows,
                    0,
                    ViewEstimator(rows),
                    interruptedTurn: false,
                    state?.ActiveBoundarySeq,
                    minTailTokens: null,
                    ct
                )
                .ConfigureAwait(false);
            if (cut is CutDecision.Skipped skipped)
            {
                return ManualCompactionResult.Refused(
                    skipped.TailOnly
                        ? ManualCompactionRefusals.NothingToCompact
                        : ManualCompactionRefusals.NoSafeBoundary
                );
            }
        }

        var pending = new PendingManualCompaction
        {
            RequestId = "cmp-" + Guid.NewGuid().ToString("N"),
            Focus = ManualCompaction.NormalizeFocus(focus),
            RequestedAt = _clock.GetUtcNow(),
        };
        var written = await CompactionStateProjection
            .UpdateAsync(
                store,
                _host.ThreadId,
                s => s.PendingManual is null ? s with { PendingManual = pending } : s,
                ct
            )
            .ConfigureAwait(false);
        if (written?.PendingManual?.RequestId != pending.RequestId)
        {
            return ManualCompactionResult.Refused(ManualCompactionRefusals.AlreadyPending);
        }

        _manualPending = true;
        _host.Logger.LogInformation(
            "Manual compaction {RequestId} queued for thread {ThreadId} (run active: {RunActive}, focus chars: {FocusChars})",
            pending.RequestId,
            _host.ThreadId,
            runActive,
            pending.Focus?.Length ?? 0
        );
        await PublishStatusAsync(
                CompactionTrigger.Manual,
                CompactionStatusMessage.Phases.Requested,
                null,
                null,
                pending,
                ct
            )
            .ConfigureAwait(false);
        return new ManualCompactionResult
        {
            RequestId = pending.RequestId,
            Status = runActive ? ManualCompaction.StatusRunning : ManualCompaction.StatusQueued,
        };
    }

    /// <summary>
    ///     Runs a pending manual compaction on an idle loop: no provider call. The checkpoint row is stamped with
    ///     <paramref name="runId"/>, the thread's latest run.
    /// </summary>
    public async Task RunPendingManualAsync(string runId, CancellationToken ct)
    {
        _manualPending = false;
        if (!IsEnabled || _host.Store is not { } store)
        {
            return;
        }

        var metadata = await store.LoadMetadataAsync(_host.ThreadId, ct).ConfigureAwait(false);
        var state = CompactionStateProjection.FromMetadata(metadata);
        if (state?.PendingManual is null)
        {
            return;
        }

        _failureBackoffUntilOrdinal = state.FailureBackoffUntilGenerationOrdinal;
        _sizeRefusedRunIds = new HashSet<string>(state.SizeRefusedRunIds ?? [], StringComparer.Ordinal);
        SeedFromMetadata(metadata);
        var killed = Options.IsKilled(_setup.ReadEnvironment);
        if (!killed)
        {
            await AdoptActiveAsync(store, state, _host.HistorySnapshot(), ct).ConfigureAwait(false);
        }

        var generationId = "compaction-" + Guid.NewGuid().ToString("N");
        var request = BuildView() ?? RawRequest();
        var tokens = EstimateRequestTokens(request);
        var outcome = await ExecuteManualAsync(
                runId,
                generationId,
                _generationOrdinal,
                interruptedTurn: false,
                killed,
                metadata?.SessionMappings is { Count: > 0 },
                CharacterEstimate(request),
                ct
            )
            .ConfigureAwait(false);
        if (outcome is not null)
        {
            var decision = outcome.Apply(ManualDecision(tokens));
            // No provider call follows, so this is the generation the panel's last decision names.
            await RecordObservationAsync(runId, generationId, _generationOrdinal, tokens, request.Count, decision, ct)
                .ConfigureAwait(false);
            await PublishAsync(
                    outcome.View is not null ? LifecycleEventTypes.CompactionApplied
                        : outcome.Decision == CompactionDecisionKinds.Failed ? LifecycleEventTypes.CompactionFailed
                        : LifecycleEventTypes.CompactionDecided,
                    runId,
                    generationId,
                    decision,
                    outcome,
                    ct
                )
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     Claims the pending manual request — clearing it in the same write, so it runs once — and compacts
    ///     with thresholds, cooldown, the gain gate, backoff and the rate limit bypassed. Returns null when there
    ///     was nothing to claim or another manual compaction is running. <paramref name="requestTokens"/> is the
    ///     character estimate the checkpoint's before-stat records.
    /// </summary>
    private async Task<CompactionOutcome?> ExecuteManualAsync(
        string runId,
        string generationId,
        long ordinal,
        bool interruptedTurn,
        bool killed,
        bool providerOwned,
        long requestTokens,
        CancellationToken ct
    )
    {
        if (_host.Store is not { } store || Interlocked.CompareExchange(ref _manualRunning, 1, 0) != 0)
        {
            return null;
        }

        try
        {
            _manualPending = false;
            PendingManualCompaction? claimed = null;
            var written = await CompactionStateProjection
                .UpdateAsync(
                    store,
                    _host.ThreadId,
                    s =>
                    {
                        claimed = s.PendingManual;
                        return claimed is null ? s : s with { PendingManual = null };
                    },
                    ct
                )
                .ConfigureAwait(false);
            if (written is null || claimed is null)
            {
                return null;
            }

            var refusal =
                Mode != CompactionMode.Compact || killed ? ManualCompactionRefusals.CompactionOff
                : providerOwned ? ManualCompactionRefusals.ProviderOwnedSession
                : interruptedTurn || _host.OwedContinuations() > 0 || _host.LiveDeferredCount() > 0
                    ? CompactionSkipReasons.UnsafeState
                : null;
            try
            {
                await PublishStatusAsync(
                        CompactionTrigger.Manual,
                        CompactionStatusMessage.Phases.Running,
                        null,
                        null,
                        claimed,
                        ct
                    )
                    .ConfigureAwait(false);
                if (refusal is not null)
                {
                    _host.Logger.LogInformation(
                        "Manual compaction {RequestId} for thread {ThreadId} refused: {CompactionReason}",
                        claimed.RequestId,
                        _host.ThreadId,
                        refusal
                    );
                    await PublishStatusAsync(
                            CompactionTrigger.Manual,
                            CompactionStatusMessage.Phases.Refused,
                            refusal,
                            null,
                            claimed,
                            ct
                        )
                        .ConfigureAwait(false);
                    return new CompactionOutcome(
                        CompactionDecisionKinds.Skipped,
                        refusal,
                        null,
                        null,
                        null,
                        null,
                        null,
                        0,
                        CompactionTrigger.Manual
                    );
                }
            }
            catch (OperationCanceledException)
            {
                // Announced as running: the client must still see where it went — queued again, or ended when refused.
                if (refusal is null)
                {
                    await RequeueManualAsync(store, claimed).ConfigureAwait(false);
                }
                else
                {
                    await PublishCancelledAsync(CompactionTrigger.Manual, claimed).ConfigureAwait(false);
                }

                throw;
            }

            // The operator asked for a compaction, not for room: cover as much as the cut rules allow (a zero
            // target puts the candidate at the newest row; the selector moves it back to a legal boundary).
            var activeBefore = Active;
            CompactionOutcome outcome;
            try
            {
                outcome = await CompactAsync(
                        runId,
                        generationId,
                        CompactionTrigger.Manual,
                        0,
                        ordinal,
                        interruptedTurn,
                        ct,
                        manual: claimed,
                        requestTokens: requestTokens
                    )
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ReferenceEquals(Active, activeBefore))
            {
                // A stop or a disposal ended the pass before its checkpoint activated: the request was accepted, so it is
                // queued again for the next loop over this thread instead of being lost with this one. CompactAsync
                // leaves this frame to us.
                await RequeueManualAsync(store, claimed).ConfigureAwait(false);
                throw;
            }
            _host.Logger.LogInformation(
                "Manual compaction {RequestId} for thread {ThreadId} answered {CompactionDecision} ({CompactionReason}), checkpoint {CheckpointId}",
                claimed.RequestId,
                _host.ThreadId,
                outcome.Decision,
                outcome.Reason,
                outcome.Checkpoint?.CheckpointId
            );
            return outcome;
        }
        finally
        {
            _ = Interlocked.Exchange(ref _manualRunning, 0);
        }
    }

    /// <summary>
    ///     Puts <paramref name="claimed"/> back as the pending manual request, unless a newer one took its place, and tells
    ///     live subscribers: queued again it has not ended, so it is announced as requested again — a failed/cancelled
    ///     frame would be followed by running/applied for the same request id. When it is not queued again it ends
    ///     failed/cancelled. Written and published without the cancelled token; a failure is logged, never thrown over
    ///     the cancellation.
    /// </summary>
    private async Task RequeueManualAsync(IConversationStore store, PendingManualCompaction claimed)
    {
        var requeued = false;
        try
        {
            var written = await CompactionStateProjection
                .UpdateAsync(
                    store,
                    _host.ThreadId,
                    s => s.PendingManual is null ? s with { PendingManual = claimed } : s,
                    CancellationToken.None
                )
                .ConfigureAwait(false);
            _manualPending = written?.PendingManual is not null;
            requeued = written?.PendingManual?.RequestId == claimed.RequestId;
            _host.Logger.LogInformation(
                "Manual compaction {RequestId} for thread {ThreadId} was cancelled before it activated; queued again: {Requeued}",
                claimed.RequestId,
                _host.ThreadId,
                requeued
            );
        }
        catch (Exception ex)
        {
            _host.Logger.LogWarning(
                ex,
                "Could not queue manual compaction {RequestId} for thread {ThreadId} again after it was cancelled",
                claimed.RequestId,
                _host.ThreadId
            );
        }

        if (!requeued)
        {
            await PublishCancelledAsync(CompactionTrigger.Manual, claimed).ConfigureAwait(false);
            return;
        }

        try
        {
            await PublishStatusAsync(
                    CompactionTrigger.Manual,
                    CompactionStatusMessage.Phases.Requested,
                    null,
                    null,
                    claimed,
                    CancellationToken.None
                )
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _host.Logger.LogDebug(
                ex,
                "Could not publish the requeued compaction status for thread {ThreadId}",
                _host.ThreadId
            );
        }
    }

    private CompactionDecision ManualDecision(long tokens) =>
        new()
        {
            Decision = CompactionDecisionKinds.Compact,
            Reason = ManualReason,
            Summary = new CompactionDecisionSummary
            {
                Decision = CompactionDecisionKinds.Compact,
                Reason = ManualReason,
                Tokens = tokens,
                Window = WindowTokens,
                Reserve = ReserveTokens,
            },
        };

    /// <summary>Sends a transient <see cref="CompactionStatusMessage"/> to live subscribers; never fails the caller.</summary>
    private async Task PublishStatusAsync(
        CompactionTrigger trigger,
        string phase,
        string? reason,
        string? checkpointId,
        PendingManualCompaction? manual,
        CancellationToken ct
    )
    {
        if (_host.PublishLive is not { } publish || trigger == CompactionTrigger.Shadow)
        {
            return;
        }

        try
        {
            await publish(
                    new CompactionStatusMessage
                    {
                        ThreadId = _host.ThreadId,
                        AgentId = _host.AgentId,
                        FromAgent = _host.AgentId,
                        RequestId = manual?.RequestId,
                        Trigger = trigger.ToString().ToLowerInvariant(),
                        Phase = phase,
                        Reason = reason,
                        CheckpointId = checkpointId,
                        Focus = manual?.Focus,
                    },
                    ct
                )
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _host.Logger.LogDebug(
                ex,
                "Could not publish compaction status {CompactionPhase} for thread {ThreadId}",
                phase,
                _host.ThreadId
            );
        }
    }

    /// <summary>
    ///     The terminal frame of an announced attempt whose pass was cancelled (a stop or a disposal): phase failed,
    ///     reason <see cref="CompactionReasons.Cancelled"/>. Sent without the cancelled token, and it never throws.
    /// </summary>
    private async Task PublishCancelledAsync(CompactionTrigger trigger, PendingManualCompaction? manual)
    {
        _host.Logger.LogInformation(
            "Compaction for thread {ThreadId} was cancelled before it finished (trigger {CompactionTrigger}, request {RequestId})",
            _host.ThreadId,
            trigger,
            manual?.RequestId
        );
        try
        {
            await PublishStatusAsync(
                    trigger,
                    CompactionStatusMessage.Phases.Failed,
                    CompactionReasons.Cancelled,
                    null,
                    manual,
                    CancellationToken.None
                )
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _host.Logger.LogDebug(
                ex,
                "Could not publish the cancelled compaction status for thread {ThreadId}",
                _host.ThreadId
            );
        }
    }

    /// <summary>The provider's count of one sent request, and the shape of the view it counted.</summary>
    private sealed record Measurement(
        long MeasuredTokens,
        long MessageTokens,
        int Count,
        string? CheckpointId,
        long ClearedThroughSeq,
        ToolResultTightening? Tightened
    );

    public CompactionRuntime(CompactionSetup setup, CompactionRuntimeHost host, IAgent providerAgent)
    {
        ArgumentNullException.ThrowIfNull(setup);
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(providerAgent);
        _setup = setup;
        _host = host;
        _clock = setup.Clock ?? TimeProvider.System;
        var options = setup.Options;
        options.Validate();
        _policy = new CompactionPolicy(options);
        _summarizer =
            setup.Summarizer
            ?? new ProviderCheckpointSummarizer(providerAgent, options.SummaryModelId ?? host.DefaultOptions.ModelId);
        _pipeline = new CheckpointPipeline(
            _summarizer,
            new CheckpointPipelineOptions
            {
                Validation = new CheckpointValidationOptions
                {
                    NarrativeTokenCap = options.NarrativeTokenCap,
                    // V9 scales with the window so a small window can still hold envelope + tail + prefix.
                    CheckpointTokenCap = options.EffectiveCheckpointTokenCap(UsableTokens),
                },
                Render = RenderOptions,
                SummaryTimeout = options.SummaryTimeout,
                SummaryAttempts = options.SummaryAttempts,
                Logger = host.Logger,
            },
            _clock
        );

        // A loop without a store has nowhere to append a checkpoint, so it can observe but never compact.
        var resolved = options.ResolveMode(setup.ProviderId, host.DefaultOptions.ModelId);
        Mode = host.Store is null && resolved > CompactionMode.Warn ? CompactionMode.Warn : resolved;
    }

    /// <summary>Told to the model whenever compaction is on, so it never claims to have compacted the conversation.</summary>
    public const string SystemNote =
        "Context compaction is automatic or user-triggered. You cannot compact the conversation; never claim you did.";

    /// <summary>
    ///     <paramref name="systemPrompt"/> with <see cref="SystemNote"/> appended when <paramref name="setup"/> resolves
    ///     to a mode other than Off for <paramref name="modelId"/>; unchanged otherwise.
    /// </summary>
    internal static string? WithSystemNote(string? systemPrompt, CompactionSetup? setup, string? modelId)
    {
        if (setup is null || setup.Options.ResolveMode(setup.ProviderId, modelId) == CompactionMode.Off)
        {
            return systemPrompt;
        }

        return string.IsNullOrEmpty(systemPrompt) ? SystemNote : systemPrompt + "\n\n" + SystemNote;
    }

    /// <summary>The mode for this loop's route.</summary>
    public CompactionMode Mode { get; }

    /// <summary>False in <see cref="CompactionMode.Off"/>: the loop neither evaluates nor builds a view.</summary>
    public bool IsEnabled => Mode > CompactionMode.Off;

    public CompactionOptions Options => _setup.Options;

    /// <summary>The checkpoint the view is built on, when one is active.</summary>
    public CompactionCheckpointMessage? Active { get; private set; }

    public long? ActiveBoundarySeq => Active?.Boundary.Seq;

    /// <summary>The envelope's recall hint names the tool the loop registers (spec 679 §6).</summary>
    public CheckpointRenderOptions RenderOptions { get; } =
        new() { RecallToolName = RecallConversationToolProvider.ToolName };

    public long ReserveTokens =>
        (_host.DefaultOptions.MaxToken ?? MultiTurnAgentBase.DefaultMaxTokenFloor) + Options.ReserveMarginTokens;

    public long? WindowTokens => _setup.ResolveWindowTokens?.Invoke(_host.DefaultOptions.ModelId);

    /// <summary><c>window − reserve</c>, or null when the window is unknown or smaller than the reserve.</summary>
    public long? UsableTokens => WindowTokens is { } w && w - ReserveTokens > 0 ? w - ReserveTokens : null;

    /// <summary>
    ///     The per-result character cap the view applies, or null when the view is not shaped: only
    ///     <see cref="CompactionMode.Compact"/> changes what the provider sees (Warn and Shadow never do).
    /// </summary>
    public int? ViewCapChars => ShapesView ? Options.ToolResultViewCapChars(UsableTokens) : null;

    private bool ShapesView => Mode == CompactionMode.Compact && !Options.IsKilled(_setup.ReadEnvironment);

    /// <summary>
    ///     Most fit re-cuts one run may make. Each one must advance the active boundary, so this only stops a runaway:
    ///     a run whose every generation outgrows the window by a whole generation.
    /// </summary>
    internal const int MaxFitRecutsPerRun = 16;

    /// <summary>
    ///     Share of the usable window a tightened tool-result trim aims at. The rest is headroom for the rows that follow,
    ///     so they fit without a new trim (new bytes, a prompt cache miss) or a re-cut.
    /// </summary>
    internal const double TightenFitRatio = 0.85;

    /// <summary>Called when a run starts: the per-run compaction budget and the fit check's runaway guard reset.</summary>
    public void OnRunStarted()
    {
        _compactionsThisRun = 0;
        _fitRecutsThisRun = 0;
    }

    /// <summary>Remembers the persisted id of an in-memory row and the append that is still in flight.</summary>
    public void TrackPersisted(IMessage message, string persistedId, Task append)
    {
        lock (_gate)
        {
            _identities.AddOrUpdate(message, new RowIdentity { Id = persistedId });
            _ = _inFlightPersists.RemoveAll(t => t.IsCompleted);
            _inFlightPersists.Add(append);
        }
    }

    /// <summary>
    ///     Pairs the rows the loop restored with the store's rows, in order, so each restored row knows its
    ///     <c>Seq</c>; then reconciles the compaction state and adopts the active checkpoint when its row is
    ///     among the restored ones. Runs once, on recovery.
    /// </summary>
    public async Task TrackRestoredAsync(IReadOnlyList<IMessage> restored, CancellationToken ct)
    {
        if (!IsEnabled || _host.Store is not { } store)
        {
            return;
        }

        var persisted = await store.LoadMessagesAsync(_host.ThreadId, ct).ConfigureAwait(false);
        var cursor = 0;
        foreach (var row in restored)
        {
            for (; cursor < persisted.Count; cursor++)
            {
                var candidate = persisted[cursor];
                IMessage converted;
                try
                {
                    converted = MessagePersistenceConverter.FromPersistedMessage(candidate);
                }
                catch (Exception)
                {
                    continue;
                }

                if (SameRow(converted, row))
                {
                    lock (_gate)
                    {
                        _identities.AddOrUpdate(row, new RowIdentity { Id = candidate.Id, Seq = candidate.Seq });
                    }

                    cursor++;
                    break;
                }
            }
        }

        await ReconcileOnceAsync(store, ct).ConfigureAwait(false);
        var state = await CompactionStateProjection.LoadAsync(store, _host.ThreadId, ct).ConfigureAwait(false);
        _clearedThroughSeq = state?.ToolResultsClearedThroughSeq ?? 0;
        _tightened = state?.ToolResultsTightened;
        // A request queued before an eviction or restart still runs: the loop checks before it waits for input.
        _manualPending = state?.PendingManual is not null;
        await AdoptActiveAsync(store, state, restored, ct).ConfigureAwait(false);
    }

    /// <summary>
    ///     The request as the model should see it, or null when the raw history is the view: no active
    ///     checkpoint and no tool-result shaping (anything but <see cref="CompactionMode.Compact"/>).
    /// </summary>
    public IReadOnlyList<IMessage>? BuildView()
    {
        if (!IsEnabled)
        {
            return null;
        }

        var shaping = ToolResultShaping();
        if (Active is null && shaping is null)
        {
            return null;
        }

        var history = _host.HistorySnapshot();
        if (shaping is not null)
        {
            LogNewlyTrimmed(history, shaping.CapChars);
        }

        return AgentContextProjection.Default.Build(
            _host.SystemPrompt,
            Sequence(history),
            Active,
            RenderOptions,
            shaping
        );
    }

    /// <summary>
    ///     The request-size estimate the policy and the fit check use: every message, the tool definitions,
    ///     and any ephemeral instruction the turn appends. When the provider measured an earlier request of
    ///     the same view shape (same checkpoint, same clear watermark, same tightened trim) and the view only grew
    ///     since, the estimate is at least that measurement plus the estimate of what was appended: the character
    ///     heuristic undercounts, the measurement does not.
    /// </summary>
    public long EstimateRequestTokens(IReadOnlyList<IMessage> request, IReadOnlyList<IMessage>? ephemeral = null) =>
        EstimateRequestTokens(request, ephemeral, _tightened);

    private long EstimateRequestTokens(
        IReadOnlyList<IMessage> request,
        IReadOnlyList<IMessage>? ephemeral,
        ToolResultTightening? tightened
    )
    {
        var messages = Estimate(request) + (ephemeral is null ? 0 : Estimate(ephemeral));
        var estimate = CharacterEstimate(request, ephemeral);
        var count = request.Count + (ephemeral?.Count ?? 0);
        if (
            Volatile.Read(ref _measurement) is { } measured
            && measured.CheckpointId == Active?.CheckpointId
            && measured.ClearedThroughSeq == _clearedThroughSeq
            && measured.Tightened == tightened
            && count >= measured.Count
        )
        {
            estimate = Math.Max(estimate, measured.MeasuredTokens + Math.Max(0, messages - measured.MessageTokens));
        }

        return estimate;
    }

    /// <summary>
    ///     The same request estimate with no calibration: what a checkpoint's before-stat records, because its
    ///     after-stat is the character estimate of the new view and "saved" must compare like with like.
    /// </summary>
    private long CharacterEstimate(IReadOnlyList<IMessage> request, IReadOnlyList<IMessage>? ephemeral = null) =>
        Estimate(request) + (ephemeral is null ? 0 : Estimate(ephemeral)) + _host.ToolSchemaTokens();

    /// <summary>Remembers the provider's input-token count for a request the loop sent, to calibrate the next estimate.</summary>
    public void ObserveMeasuredUsage(IReadOnlyList<IMessage> sent, long measuredTokens)
    {
        if (measuredTokens <= 0)
        {
            return;
        }

        Volatile.Write(
            ref _measurement,
            new Measurement(
                measuredTokens,
                Estimate(sent),
                sent.Count,
                Active?.CheckpointId,
                _clearedThroughSeq,
                _tightened
            )
        );
    }

    /// <summary>
    ///     One policy pass for the request about to be sent (§5.1). Returns the decision, a replacement view
    ///     when a checkpoint was activated, or a refusal when the request still exceeds the reserve after a
    ///     failed compaction in <see cref="CompactionMode.Compact"/>.
    /// </summary>
    public async Task<CompactionPass> EvaluateAsync(
        string runId,
        string generationId,
        IReadOnlyList<IMessage> request,
        bool interruptedTurn,
        CancellationToken ct,
        IReadOnlyList<IMessage>? ephemeral = null
    )
    {
        var store = _host.Store;
        if (IsEnabled && store is not null)
        {
            await ReconcileOnceAsync(store, ct).ConfigureAwait(false);
        }

        var metadata = store is null ? null : await store.LoadMetadataAsync(_host.ThreadId, ct).ConfigureAwait(false);
        var state = CompactionStateProjection.FromMetadata(metadata);
        _failureBackoffUntilOrdinal = state?.FailureBackoffUntilGenerationOrdinal;
        _sizeRefusedRunIds = new HashSet<string>(state?.SizeRefusedRunIds ?? [], StringComparer.Ordinal);
        SeedFromMetadata(metadata);
        if (store is not null)
        {
            await SeedActivityAsync(store, ct).ConfigureAwait(false);
        }

        var ordinal = ++_generationOrdinal;
        _evaluatedGeneration = (generationId, ordinal);
        var killed = Options.IsKilled(_setup.ReadEnvironment);
        IReadOnlyList<IMessage>? replacement = null;
        if (
            (state?.ToolResultsClearedThroughSeq ?? 0) is var watermark
            && (watermark != _clearedThroughSeq || state?.ToolResultsTightened != _tightened)
        )
        {
            // Another loop over this thread moved the watermark or tightened the trim: the view handed in predates it.
            _clearedThroughSeq = watermark;
            _tightened = state?.ToolResultsTightened;
            replacement = BuildView();
        }

        if (killed && store is not null && state?.ActiveCheckpointId is not null)
        {
            // §8.4: kill = Skipped(disabled) for new decisions and Active → RolledBack on the next
            // request. The request handed in was built on the view; the raw history goes out instead.
            _ = await CompactionStateProjection
                .RollBackAsync(store, _host.ThreadId, CompactionFailureReasons.Killed, _clock.GetUtcNow(), ct)
                .ConfigureAwait(false);
            state = state with { ActiveCheckpointId = null, ActiveBoundarySeq = null };
            Active = null;
            replacement = RawRequest();
        }
        else if (store is not null && !killed)
        {
            await AdoptActiveAsync(store, state, _host.HistorySnapshot(), ct).ConfigureAwait(false);
        }

        var view = replacement ?? request;
        var tokens = EstimateRequestTokens(view, ephemeral);
        var window = WindowTokens;
        var cachingEnabled = _host.DefaultOptions.PromptCaching != PromptCachingMode.Off;
        var now = _clock.GetUtcNow();
        var input = new CompactionPolicyInput
        {
            Mode = Mode,
            Killed = killed,
            ProviderOwnedSession = metadata?.SessionMappings is { Count: > 0 },
            EstimatedInputTokens = tokens,
            WindowTokens = window,
            ReserveTokens = ReserveTokens,
            LoopState = new CutBlockingState(_host.OwedContinuations(), interruptedTurn ? 1 : 0),
            LiveDeferredCount = _host.LiveDeferredCount(),
            GenerationOrdinal = ordinal,
            CooldownUntilGenerationOrdinal = state?.CooldownUntilGenerationOrdinal,
            NewTokensSinceCheckpoint = Active is null ? null : Math.Max(0, tokens - Active.Stats.EstimatedTokensAfter),
            CompactionsThisRun = _compactionsThisRun,
            FailureBackoffUntilGenerationOrdinal = _failureBackoffUntilOrdinal,
            CompactionsInThreadWindow = CompactionsWithin(state, now, Options.ThreadCompactionWindow),
            CacheTemperature = ConversationActivity.ResolveCacheTemperature(
                _lastActivity,
                now,
                Options.CacheTtl,
                cachingEnabled
            ),
            Economics = ResolveEconomics(cachingEnabled),
        };
        _lastActivity = now;

        var decision = _policy.Evaluate(input);
        CompactionOutcome? outcome = null;
        var acted = false;
        var manual =
            _manualPending || state?.PendingManual is not null
                ? await ExecuteManualAsync(
                        runId,
                        generationId,
                        ordinal,
                        interruptedTurn,
                        killed,
                        input.ProviderOwnedSession,
                        CharacterEstimate(view, ephemeral),
                        ct
                    )
                    .ConfigureAwait(false)
                : null;
        if (manual is { View: { } manualView })
        {
            // An operator's request applies at this step, whatever the thresholds say.
            outcome = manual;
            acted = true;
            replacement = manualView;
            decision = manual.Apply(ManualDecision(tokens) with { TargetTokens = decision.TargetTokens });
        }
        else if (manual is { Decision: CompactionDecisionKinds.Failed })
        {
            // The manual attempt spent this pass's summary calls on these rows; an automatic attempt would only
            // summarise them again. The fit check below may still clear, and refuses if the request does not fit.
            outcome = manual;
            decision = manual.Apply(ManualDecision(tokens) with { TargetTokens = decision.TargetTokens });
        }
        else if (decision.IsCompact)
        {
            var target = decision.TargetTokens ?? (tokens / 2);

            // Clear older tool results first: no summary call, and often enough on its own (phase 1).
            if (
                Options.ClearToolResultsKeepTurns is { } keepTurns
                && await AdvanceClearingAsync(keepTurns, ct).ConfigureAwait(false) is { } cleared
            )
            {
                acted = true;
                replacement = cleared;
                var clearedTokens = EstimateRequestTokens(cleared, ephemeral);
                if (clearedTokens <= target)
                {
                    outcome = new CompactionOutcome(
                        CompactionDecisionKinds.Compact,
                        CompactionReasons.ToolResultsCleared,
                        cleared,
                        null,
                        null,
                        clearedTokens,
                        null,
                        0,
                        CompactionTrigger.Preemptive
                    );
                }
            }

            if (outcome is null)
            {
                outcome = await CompactAsync(
                        runId,
                        generationId,
                        decision.Decision == CompactionDecisionKinds.Shadow
                            ? CompactionTrigger.Shadow
                            : CompactionTrigger.Preemptive,
                        target,
                        ordinal,
                        interruptedTurn,
                        ct,
                        requireGain: true,
                        requestTokens: CharacterEstimate(view, ephemeral)
                    )
                    .ConfigureAwait(false);
                acted |= outcome.View is not null;
                replacement = outcome.View ?? replacement;
            }

            decision = outcome.Apply(decision);
        }

        await RecordAsync(runId, generationId, ordinal, tokens, view.Count, decision, outcome, ct)
            .ConfigureAwait(false);

        ContextOverflowException? refusal = null;
        var recutRefused = false;
        var usable = window is { } w ? w - ReserveTokens : (long?)null;
        var sentTokens = replacement is null ? tokens : EstimateRequestTokens(replacement, ephemeral);
        if (
            Mode == CompactionMode.Compact
            && usable is { } unsafeLimit
            && sentTokens > unsafeLimit
            && decision.Reason == CompactionSkipReasons.UnsafeState
        )
        {
            // Under unsafe_state nothing may be cut, but clearing old tool results and trimming the shown ones harder are
            // view-only and never split a tool turn: the ladder's first and last steps still run, and the request is
            // refused only if it does not fit afterwards.
            if (
                Options.ClearToolResultsKeepTurns is not null
                && await AdvanceClearingAsync(keepTurns: 1, ct).ConfigureAwait(false) is { } clearedUnsafe
            )
            {
                acted = true;
                replacement = clearedUnsafe;
                sentTokens = EstimateRequestTokens(clearedUnsafe, ephemeral);
            }

            if (
                sentTokens > unsafeLimit
                && await TightenToolResultsAsync(runId, ephemeral, unsafeLimit, unsafeLimit, sentTokens, ct)
                    .ConfigureAwait(false)
                    is { } tightenedUnsafe
            )
            {
                acted = true;
                replacement = tightenedUnsafe.View;
                sentTokens = tightenedUnsafe.Tokens;
            }
        }

        if (
            Mode == CompactionMode.Compact
            && usable is { } fitLimit
            && sentTokens > fitLimit
            && decision.Reason
                is not (
                    CompactionSkipReasons.Disabled
                    or CompactionSkipReasons.ProviderOwnedSession
                    or CompactionSkipReasons.UnsafeState
                )
        )
        {
            // Post-cut fit check: whatever compaction did, the request must fit before it is sent. Escalate
            // from free to costly — clear all but the latest tool turn, then cut again at the smallest legal
            // tail — and refuse only when neither brings the view under the usable window.
            var escalation = await EscalateAsync(
                    runId,
                    generationId,
                    ordinal,
                    interruptedTurn,
                    replacement ?? view,
                    ephemeral,
                    fitLimit,
                    sentTokens,
                    outcome,
                    ct
                )
                .ConfigureAwait(false);
            recutRefused = escalation.RecutRefused;
            if (escalation.View is { } escalated)
            {
                acted = true;
                replacement = escalated;
                sentTokens = escalation.Tokens;
                if (escalation.Outcome is { } recut)
                {
                    decision = recut.Apply(decision);
                    // Same generation: this supersedes the observation recorded above, so the last decision shows the cut.
                    await RecordObservationAsync(runId, generationId, ordinal, tokens, view.Count, decision, ct)
                        .ConfigureAwait(false);
                }
            }
        }

        if (
            Mode == CompactionMode.Compact
            && usable is { } limit
            && sentTokens > limit
            && (
                acted
                || recutRefused
                || decision.Decision is CompactionDecisionKinds.Failed or CompactionDecisionKinds.Skipped
            )
            && decision.Reason is not (CompactionSkipReasons.Disabled or CompactionSkipReasons.ProviderOwnedSession)
        )
        {
            refusal =
                acted || recutRefused
                    ? new ContextOverflowException(
                        CompactionReasons.ViewExceedsWindow,
                        $"after clearing tool results, cutting and trimming tool results harder, the request is still ~{sentTokens} tokens against a usable window of {limit}"
                    )
                    : new ContextOverflowException(
                        CompactionFailureReasons.OverflowAfterCompaction,
                        $"request of ~{sentTokens} tokens exceeds the usable window of {limit} and compaction answered {decision.Reason}"
                    );
            _host.Logger.LogWarning(
                "Compaction refused to send a request for thread {ThreadId} run {RunId}: {Reason}, {EstimatedTokens} estimated tokens against {UsableTokens} usable after {CompactionDecision}",
                _host.ThreadId,
                runId,
                refusal.Reason,
                sentTokens,
                limit,
                decision.Reason
            );
            var failed = decision with
            {
                Decision = CompactionDecisionKinds.Failed,
                Reason = refusal.Reason,
                // The request refused is the one the ladder left, the size the exception and the log name.
                Summary = decision.Summary with
                {
                    Decision = CompactionDecisionKinds.Failed,
                    Reason = refusal.Reason,
                    Tokens = sentTokens,
                    Utilization = limit > 0 ? (double)sentTokens / limit : decision.Summary.Utilization,
                },
            };
            // Same generation: the refusal supersedes the decision recorded above, so the last decision says why the
            // request was not sent.
            await RecordObservationAsync(
                    runId,
                    generationId,
                    ordinal,
                    sentTokens,
                    (replacement ?? view).Count,
                    failed,
                    ct
                )
                .ConfigureAwait(false);
            await RecordSizeRefusalAsync(runId, ct).ConfigureAwait(false);
            await PublishAsync(LifecycleEventTypes.CompactionFailed, runId, generationId, failed, null, ct)
                .ConfigureAwait(false);
        }

        return new CompactionPass(decision, replacement, refusal);
    }

    /// <summary>
    ///     The host's overflow verdict, or the built-in one: the provider said so (<see cref="ContextOverflowVerdict.Overflow"/>).
    ///     No size gate: the fit check has just made the estimate fit, so a provider overflow is the estimate undercounting,
    ///     which is exactly when the reactive ladder is needed. A transport abort never qualifies (spec Q1).
    /// </summary>
    public bool IsContextOverflow(Exception exception, long estimatedTokens) =>
        _setup.IsContextOverflow is { } verdict
            ? verdict(exception)
            : ProviderErrorClassifier.ClassifyContextOverflow(exception, estimatedTokens)
                == ContextOverflowVerdict.Overflow;

    /// <summary>
    ///     The reactive path (§5.1): after the provider refused a request as too large, run the fit check's ladder
    ///     (<see cref="EscalateAsync"/>: clear, re-cut with the fallback checkpoint, tighten the trims) and tell the
    ///     caller whether the view changed, so the same input is retried. The provider counted more than the estimate
    ///     did, so the ladder aims below both the usable window and the refused request's estimate, at the policy's
    ///     target; a trim that misses the target is still retried when it is smaller than both. Any step counts, not only
    ///     a boundary advance: each one the ladder returns moved the view forward (the clear watermark, the boundary, or
    ///     a trim estimated under the refused request). Each overflow in a run gets the ladder while it keeps doing so.
    ///     Refusals of the same input are bounded without a count of their own: with no new rows clearing advances once,
    ///     re-cuts stop at <see cref="MaxFitRecutsPerRun"/>, and each trim lands under the target — under half the
    ///     estimate it started from — or at the floor, after which it cannot shrink.
    /// </summary>
    public async Task<bool> TryReactiveAsync(string runId, string generationId, CancellationToken ct)
    {
        if (Mode != CompactionMode.Compact || _host.Store is null)
        {
            return false;
        }

        var previousBoundary = ActiveBoundarySeq ?? 0;
        // The request that overflowed is the best window estimate there is when the capacity is unknown.
        var overflowed = BuildView() ?? RawRequest();
        var tokens = EstimateRequestTokens(overflowed);
        var usable = WindowTokens is { } w ? w - ReserveTokens : tokens;
        var target = (long)(Options.TargetRatio * Math.Min(usable, tokens));
        // The refused generation may already have re-cut in its fit check; the refusal is new evidence it did not suffice.
        _lastRecutGenerationId = null;
        var escalation = await EscalateAsync(
                runId,
                generationId,
                _generationOrdinal,
                interruptedTurn: false,
                overflowed,
                ephemeral: null,
                target,
                tokens,
                outcome: null,
                ct,
                CompactionTrigger.Reactive,
                ReactiveReason,
                ceiling: Math.Min(usable, tokens) - 1
            )
            .ConfigureAwait(false);
        var changed = escalation.View is not null;
        var summary = new CompactionDecision
        {
            Decision = changed ? CompactionDecisionKinds.Compact : CompactionDecisionKinds.Skipped,
            Reason = ReactiveReason,
            TargetTokens = target,
            Summary = new CompactionDecisionSummary
            {
                Decision = changed ? CompactionDecisionKinds.Compact : CompactionDecisionKinds.Skipped,
                Reason = ReactiveReason,
                Tokens = tokens,
                Window = WindowTokens,
                Reserve = ReserveTokens,
            },
        };
        var decision = escalation.Outcome?.Apply(summary) ?? summary;
        _host.Logger.LogWarning(
            "Reactive compaction for thread {ThreadId} run {RunId} after a provider overflow: {TokensBefore} -> {TokensAfter} estimated tokens against a target of {TargetTokens}, boundary {PreviousBoundarySeq} -> {BoundarySeq}; retrying {Retrying}",
            _host.ThreadId,
            runId,
            tokens,
            escalation.Tokens,
            target,
            previousBoundary,
            ActiveBoundarySeq ?? 0,
            changed
        );
        await RecordObservationAsync(runId, generationId, _generationOrdinal, tokens, 0, decision, ct)
            .ConfigureAwait(false);
        await PublishAsync(LifecycleEventTypes.CompactionDecided, runId, generationId, decision, escalation.Outcome, ct)
            .ConfigureAwait(false);
        return changed;
    }

    /// <summary>The decision reason of the reactive path.</summary>
    internal const string ReactiveReason = "reactive";

    /// <summary>Records the terminal failure of the reactive path before the run is failed.</summary>
    public async Task ReportOverflowAfterCompactionAsync(string runId, string generationId, CancellationToken ct)
    {
        var tokens = EstimateRequestTokens(BuildView() ?? RawRequest());
        var failed = new CompactionDecision
        {
            Decision = CompactionDecisionKinds.Failed,
            Reason = CompactionFailureReasons.OverflowAfterCompaction,
            Summary = new CompactionDecisionSummary
            {
                Decision = CompactionDecisionKinds.Failed,
                Reason = CompactionFailureReasons.OverflowAfterCompaction,
                Tokens = tokens,
                Window = WindowTokens,
                Reserve = ReserveTokens,
            },
        };
        // Supersedes the reactive decision recorded for this generation, so the last decision says how the run ended.
        await RecordObservationAsync(runId, generationId, _generationOrdinal, tokens, 0, failed, ct)
            .ConfigureAwait(false);
        await RecordSizeRefusalAsync(runId, ct).ConfigureAwait(false);
        await PublishAsync(LifecycleEventTypes.CompactionFailed, runId, generationId, failed, null, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    ///     Automatic compaction attempts (every trigger but manual) prepared within <paramref name="window"/> of
    ///     <paramref name="now"/>: the thread-level rate limit's count. A later status change (superseded, rolled
    ///     back) is not a new attempt, so it is never what places an entry in the window.
    /// </summary>
    internal static int CompactionsWithin(CompactionState? state, DateTimeOffset now, TimeSpan window) =>
        state is null || window <= TimeSpan.Zero
            ? 0
            : state.History.Count(e => e.Trigger != CompactionTrigger.Manual && (e.PreparedAt ?? e.At) > now - window);

    /// <summary>Remembers that this run was refused for size, so R4 does not keep the next run whole for it.</summary>
    private async Task RecordSizeRefusalAsync(string runId, CancellationToken ct)
    {
        if (_host.Store is not { } store)
        {
            return;
        }

        try
        {
            _ = await CompactionStateProjection
                .UpdateAsync(
                    store,
                    _host.ThreadId,
                    s =>
                        s.SizeRefusedRunIds?.Contains(runId, StringComparer.Ordinal) == true
                            ? s
                            : s with
                            {
                                SizeRefusedRunIds =
                                [
                                    .. (s.SizeRefusedRunIds ?? []).TakeLast(
                                        CompactionState.SizeRefusedRunIdsLength - 1
                                    ),
                                    runId,
                                ],
                            },
                    ct
                )
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _host.Logger.LogWarning(
                ex,
                "Failed to record the size refusal of run {RunId} for thread {ThreadId}",
                runId,
                _host.ThreadId
            );
        }
    }

    /// <summary>The persisted <c>Seq</c> of an in-memory row, when known.</summary>
    public long? SeqOf(IMessage message) => _identities.TryGetValue(message, out var identity) ? identity.Seq : null;

    private sealed record CompactionOutcome(
        string Decision,
        string? Reason,
        IReadOnlyList<IMessage>? View,
        CompactionCheckpointMessage? Checkpoint,
        long? CutSeq,
        long? TokensAfter,
        long? RowsCovered,
        long LatencyMs,
        CompactionTrigger Trigger
    )
    {
        public CompactionDecision Apply(CompactionDecision decision) =>
            decision with
            {
                Decision = Decision,
                Reason = Reason ?? decision.Reason,
                Summary = decision.Summary with
                {
                    Decision = Decision,
                    Reason = Reason ?? decision.Reason,
                    CutSeq = CutSeq,
                },
            };
    }

    /// <summary>
    ///     What <see cref="EscalateAsync"/> changed: the new view and its size, the re-cut's outcome when one ran, and
    ///     whether a re-cut was needed but its budget was spent.
    /// </summary>
    private sealed record Escalation(
        IReadOnlyList<IMessage>? View,
        long Tokens,
        CompactionOutcome? Outcome,
        bool RecutRefused
    );

    /// <summary>
    ///     The fit check's escalation ladder, cheapest first: clear every tool result but the latest tool turn
    ///     (no summary call), then cut again with the smallest legal tail (R3 floor of one token, R1/R4/R6
    ///     unchanged). An unhealthy summarizer is never why a turn is refused: the re-cut runs through a failure
    ///     backoff, and when its summary fails it activates the fallback checkpoint instead (§3.4) — without asking
    ///     the model at all when the pass's own summary just failed or a failure backoff is in force. The re-cut is
    ///     bounded by progress, not by the run's
    ///     <see cref="CompactionOptions.MaxCompactionsPerRun"/> or the thread rate limit: one per generation, and each
    ///     must advance the active boundary; a skipped one spends nothing. <see cref="MaxFitRecutsPerRun"/> stops a
    ///     runaway. Last, the
    ///     tool results still shown are trimmed harder (<see cref="TightenToolResultsAsync"/>): R1 never splits a tool
    ///     turn and clearing keeps the latest, so one wide parallel turn can outgrow the window on its own. Only when
    ///     that trim at its floor still does not fit is the request refused. Every step aims at <paramref name="limit"/>;
    ///     the trim is kept when it fits <paramref name="ceiling"/> (default: the limit), which the reactive path sets
    ///     above its target.
    /// </summary>
    private async Task<Escalation> EscalateAsync(
        string runId,
        string generationId,
        long ordinal,
        bool interruptedTurn,
        IReadOnlyList<IMessage> current,
        IReadOnlyList<IMessage>? ephemeral,
        long limit,
        long tokens,
        CompactionOutcome? outcome,
        CancellationToken ct,
        CompactionTrigger trigger = CompactionTrigger.Preemptive,
        string recutReason = CompactionPolicy.HardReason,
        long? ceiling = null
    )
    {
        IReadOnlyList<IMessage>? view = null;
        CompactionOutcome? recut = null;
        var recutRefused = false;
        if (
            Options.ClearToolResultsKeepTurns is not null
            && await AdvanceClearingAsync(keepTurns: 1, ct).ConfigureAwait(false) is { } cleared
        )
        {
            view = cleared;
            tokens = EstimateRequestTokens(cleared, ephemeral);
        }

        if (
            tokens > limit
            && _host.Store is not null
            && string.Equals(_lastRecutGenerationId, generationId, StringComparison.Ordinal)
        )
        {
            // This generation already re-cut; a second one in it would cover nothing new.
            recutRefused = true;
        }
        else if (tokens > limit && _host.Store is not null && _fitRecutsThisRun >= MaxFitRecutsPerRun)
        {
            recutRefused = true;
            _host.Logger.LogWarning(
                "Fit escalation re-cut for thread {ThreadId} run {RunId} refused: the run already made {FitRecuts} fit re-cuts, the runaway limit",
                _host.ThreadId,
                runId,
                _fitRecutsThisRun
            );
        }
        else if (tokens > limit && _host.Store is not null)
        {
            var previousBoundary = ActiveBoundarySeq ?? 0;
            var attempt = await CompactAsync(
                    runId,
                    generationId,
                    trigger,
                    targetTokens: 0,
                    ordinal,
                    interruptedTurn,
                    ct,
                    minTailTokens: 1,
                    requestTokens: CharacterEstimate(view ?? current, ephemeral),
                    summaryFallback: true,
                    knownSummaryFailure: outcome is { Decision: CompactionDecisionKinds.Failed, Reason: { } failed }
                        && IsSummaryAttemptFailure(failed)
                            ? failed
                        : _failureBackoffUntilOrdinal > ordinal
                            ? await BackedOffSummaryFailureAsync(ct).ConfigureAwait(false)
                        : null
                )
                .ConfigureAwait(false);
            _host.Logger.LogInformation(
                "Fit escalation re-cut for thread {ThreadId} run {RunId} answered {CompactionDecision} ({CompactionReason}) at cut {CutSeq}",
                _host.ThreadId,
                runId,
                attempt.Decision,
                attempt.Reason,
                attempt.CutSeq
            );

            // Only a selected cut spends anything: a skip (no legal cut, drift) leaves the guard and the generation
            // free. The selector never cuts at or before the active boundary; a cut that did would be no progress.
            if (attempt.Decision != CompactionDecisionKinds.Skipped)
            {
                _lastRecutGenerationId = generationId;
                _fitRecutsThisRun++;
            }

            if (attempt.View is not null && attempt.CutSeq <= previousBoundary)
            {
                recutRefused = true;
            }
            else if (attempt.View is { } smaller)
            {
                recut = attempt;
                view = smaller;
                tokens = EstimateRequestTokens(smaller, ephemeral);
                await PublishAsync(
                        LifecycleEventTypes.CompactionApplied,
                        runId,
                        generationId,
                        attempt.Apply(
                            new CompactionDecision
                            {
                                Decision = CompactionDecisionKinds.Compact,
                                Reason = recutReason,
                                Summary = new CompactionDecisionSummary
                                {
                                    Decision = CompactionDecisionKinds.Compact,
                                    Reason = recutReason,
                                    Tokens = tokens,
                                    Window = WindowTokens,
                                    Reserve = ReserveTokens,
                                },
                            }
                        ),
                        attempt,
                        ct
                    )
                    .ConfigureAwait(false);
            }
        }

        if (
            tokens > limit
            && await TightenToolResultsAsync(runId, ephemeral, limit, ceiling ?? limit, tokens, ct)
                .ConfigureAwait(false)
                is { } tightened
        )
        {
            view = tightened.View;
            tokens = tightened.Tokens;
        }

        return new Escalation(view, tokens, recut, recutRefused);
    }

    /// <summary>
    ///     The summary failure a fit re-cut's fallback names while a failure backoff is in force: the thread's newest
    ///     model failure (<see cref="IsModelFailure"/>), or <see cref="CompactionSkipReasons.FailureBackoff"/> when none
    ///     is recorded. The backoff says the model is not to be asked yet, so the re-cut builds the fallback at once.
    /// </summary>
    private async Task<string> BackedOffSummaryFailureAsync(CancellationToken ct)
    {
        var state = _host.Store is { } store
            ? await CompactionStateProjection.LoadAsync(store, _host.ThreadId, ct).ConfigureAwait(false)
            : null;
        return state
                ?.History.Select(e => e.SummaryFallback ?? e.Reason)
                .LastOrDefault(r => r is not null && IsModelFailure(r))
            ?? CompactionSkipReasons.FailureBackoff;
    }

    /// <summary>
    ///     The fit check's last step: trims the tool results the view still shows (past the clear watermark and the active
    ///     boundary) harder, each to the same share of the length the view cap would show — so the tokens left for them,
    ///     <c>limit − everything else</c>, are shared in proportion to size — and never below
    ///     <see cref="CompactionOptions.ToolResultViewCapMinChars"/>. The share is the largest (to a tenth of a percent)
    ///     whose view fits <see cref="TightenFitRatio"/> of the usable window (or <paramref name="limit"/>, when that is
    ///     lower), found by measuring the view itself, so marker and message overhead count; when even the floor misses
    ///     that, the floor is kept as long as it fits <paramref name="ceiling"/> (the limit, except on the reactive path).
    ///     Null when nothing can be trimmed or even the floor does not fit: then the request is refused as before.
    /// </summary>
    /// <remarks>
    ///     The share is persisted (<see cref="CompactionState.ToolResultsTightened"/>) rather than recomputed each
    ///     generation. Recomputing would move with the tail: every appended row would change the share, so every request
    ///     would re-trim the same results to new bytes and miss the prompt cache. Persisted, later requests rebuild the
    ///     same bytes, a restarted loop reads the same view, and newer results (past its seq) keep the normal cap. A share
    ///     still in force only ever shrinks; once clearing passes its seq it is moot and the next one starts afresh.
    /// </remarks>
    private async Task<(IReadOnlyList<IMessage> View, long Tokens)?> TightenToolResultsAsync(
        string runId,
        IReadOnlyList<IMessage>? ephemeral,
        long limit,
        long ceiling,
        long tokensBefore,
        CancellationToken ct
    )
    {
        if (!ShapesView || _host.Store is not { } store || ToolResultShaping() is not { } shaping)
        {
            return null;
        }

        var (rows, _) = await LoadRowsAsync(store, ct).ConfigureAwait(false);
        if (rows is null)
        {
            return null;
        }

        var shownAfter = Math.Max(_clearedThroughSeq, Active?.Boundary.Seq ?? 0);
        var results = rows.Where(r => r.Seq > shownAfter && !r.IsCheckpointRow)
            .SelectMany(r => TrimmableLengths(r.Message).Select(length => (r.Seq, Length: length)))
            .Where(r => r.Length > shaping.TightenedFloorChars)
            .ToList();
        if (results.Count == 0)
        {
            return null;
        }

        var inForce = _tightened is { } t && t.ThroughSeq > _clearedThroughSeq ? t : null;
        var candidate = new ToolResultTightening
        {
            ThroughSeq = Math.Max(results.Max(r => r.Seq), inForce?.ThroughSeq ?? 0),
            PartsPerMillion = 0,
        };
        var history = Sequence(_host.HistorySnapshot());

        (IReadOnlyList<IMessage> View, long Tokens) Measure(ToolResultTightening tightening)
        {
            var view = AgentContextProjection.Default.Build(
                _host.SystemPrompt,
                history,
                Active,
                RenderOptions,
                ToolResultShaping(tightening)
            );
            return (view, EstimateRequestTokens(view, ephemeral, tightening));
        }

        if (Measure(candidate).Tokens > ceiling)
        {
            return null;
        }

        // Largest share under the headroom target: the floor fits the ceiling and the share in force (or whole) does not
        // fit the limit, since the fit check is here. A floor that misses the target is still kept, as the most the trim
        // can do.
        var target = Math.Min(limit, (long)(TightenFitRatio * (WindowTokens is { } w ? w - ReserveTokens : limit)));
        var fits = 0;
        var tooBig = inForce?.PartsPerMillion ?? ToolResultViewOptions.PartsPerMillion;
        while (tooBig - fits > 1_000)
        {
            var mid = fits + ((tooBig - fits) / 2);
            if (Measure(candidate with { PartsPerMillion = mid }).Tokens <= target)
            {
                fits = mid;
            }
            else
            {
                tooBig = mid;
            }
        }

        var chosen = candidate with { PartsPerMillion = fits };
        var written = await CompactionStateProjection
            .UpdateAsync(store, _host.ThreadId, s => s with { ToolResultsTightened = chosen }, ct)
            .ConfigureAwait(false);
        if (written is null)
        {
            return null; // A newer build owns the state; it decides what the view shows.
        }

        var tightenedShaping = ToolResultShaping(chosen)!;
        var trimmed = results.Where(r => r.Length > tightenedShaping.CapFor(r.Seq, r.Length)).ToList();
        _tightened = written.ToolResultsTightened ?? chosen;
        var tightenedView = BuildView() ?? Measure(chosen).View;
        var tokensAfter = EstimateRequestTokens(tightenedView, ephemeral);
        _host.Logger.LogInformation(
            "Tightened the view trim of {ResultsTrimmed} tool results through seq {ThroughSeq} for thread {ThreadId} run {RunId}: cap {PreviousCapChars} chars -> at most {CapChars} chars ({PartsPerMillion} ppm of the shown length, floor {FloorChars}), {TokensBefore} -> {TokensAfter} estimated tokens; the stored rows are unchanged",
            trimmed.Count,
            chosen.ThroughSeq,
            _host.ThreadId,
            runId,
            results.Max(r => shaping.CapFor(r.Seq, r.Length)),
            trimmed.Count == 0 ? 0 : trimmed.Max(r => tightenedShaping.CapFor(r.Seq, r.Length)),
            chosen.PartsPerMillion,
            tightenedShaping.TightenedFloorChars,
            tokensBefore,
            tokensAfter
        );
        return (tightenedView, tokensAfter);
    }

    /// <summary>The lengths of the tool result texts a view trim may shorten in <paramref name="message"/>.</summary>
    private static IEnumerable<int> TrimmableLengths(IMessage message) =>
        message switch
        {
            ToolCallResultMessage { IsDeferred: false, Result: { } text } single
                when single.ContentBlocks is not { Count: > 0 } => [text.Length],
            ToolsCallResultMessage many =>
            [
                .. many
                    .ToolCallResults.Where(r =>
                        !r.IsDeferred && r.Result is not null && r.ContentBlocks is not { Count: > 0 }
                    )
                    .Select(r => r.Result.Length),
            ],
            _ => [],
        };

    /// <summary>
    ///     Advances the persisted clear watermark so all but the <paramref name="keepTurns"/> most recent tool
    ///     turns show placeholders, and returns the new view; null when nothing new would be cleared, the view
    ///     is not shaped, or the rows cannot be placed.
    /// </summary>
    private async Task<IReadOnlyList<IMessage>?> AdvanceClearingAsync(int keepTurns, CancellationToken ct)
    {
        if (!ShapesView || _host.Store is not { } store)
        {
            return null;
        }

        var (rows, _) = await LoadRowsAsync(store, ct).ConfigureAwait(false);
        if (rows is null)
        {
            return null;
        }

        var through = ToolResultView.ClearedThroughSeq(rows, keepTurns);
        if (through <= _clearedThroughSeq)
        {
            return null;
        }

        var written = await CompactionStateProjection
            .UpdateAsync(
                store,
                _host.ThreadId,
                s => s with { ToolResultsClearedThroughSeq = Math.Max(s.ToolResultsClearedThroughSeq ?? 0, through) },
                ct
            )
            .ConfigureAwait(false);
        if (written is null)
        {
            return null; // A newer build owns the state; it decides what the view clears.
        }

        var previous = _clearedThroughSeq;
        _clearedThroughSeq = written.ToolResultsClearedThroughSeq ?? through;
        _host.Logger.LogInformation(
            "Cleared tool results from the view for thread {ThreadId} through seq {ClearedThroughSeq} (was {PreviousClearedThroughSeq}), keeping {KeepTurns} tool turns",
            _host.ThreadId,
            _clearedThroughSeq,
            previous,
            keepTurns
        );
        return BuildView();
    }

    /// <summary>The store's rows with every in-memory row placed, or the typed reason they cannot be.</summary>
    private async Task<(IReadOnlyList<SequencedMessage>? Rows, string? SkipReason)> LoadRowsAsync(
        IConversationStore store,
        CancellationToken ct
    )
    {
        Task[] pending;
        lock (_gate)
        {
            pending = [.. _inFlightPersists];
        }

        try
        {
            await Task.WhenAll(pending).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // A failed append is a row the store does not have; reconciliation below reports it.
        }

        var persisted = await store.LoadMessagesAsync(_host.ThreadId, ct).ConfigureAwait(false);
        var rows = SequencedHistory.FromPersisted(persisted);
        if (rows.Count == 0 || rows.Count != persisted.Count)
        {
            // Legacy rows without Seq (§8.3): no position to cut at until the store backfills.
            return (null, CompactionSkipReasons.UnsafeState);
        }

        return Reconcile(persisted) ? (rows, null) : (null, CompactionReasons.WatermarkDrift);
    }

    /// <summary>The cut the rules allow for a tail target of <paramref name="tailTokens" />, or why there is none.</summary>
    private async Task<CutDecision> SelectCutAsync(
        IReadOnlyList<SequencedMessage> rows,
        long tailTokens,
        Func<IMessage, long> estimator,
        bool interruptedTurn,
        long? activeBoundarySeq,
        long? minTailTokens,
        CancellationToken ct
    )
    {
        var usable = UsableTokens;
        var runs = _host.RunLedgerStore is { } ledger
            ? await ledger.ListRunLedgerAsync(_host.ThreadId, ct).ConfigureAwait(false)
            : [];
        return CutSelector.Select(
            new CutRequest(
                rows,
                CandidateSeq(rows, tailTokens, estimator),
                new CutBlockingState(_host.OwedContinuations(), interruptedTurn ? 1 : 0),
                runs,
                activeBoundarySeq,
                new CutSelectorOptions
                {
                    // R3/R7 scale with the window so a small window still has a legal fitting cut.
                    MinTailTokens = minTailTokens ?? Options.EffectiveMinTailTokens(usable),
                    MaxTailTokens = Options.EffectiveMaxTailTokens(usable),
                    CorrectionLookbackRuns = Options.CorrectionLookbackRuns,
                    Estimator = estimator,
                    SizeRefusedRunIds = _sizeRefusedRunIds,
                }
            )
        );
    }

    private ToolResultViewOptions? ToolResultShaping() => ToolResultShaping(_tightened);

    private ToolResultViewOptions? ToolResultShaping(ToolResultTightening? tightened) =>
        ShapesView
            ? new ToolResultViewOptions
            {
                CapChars = Options.ToolResultViewCapChars(UsableTokens),
                ClearedThroughSeq = _clearedThroughSeq,
                RecallToolName = RecallConversationToolProvider.ToolName,
                TightenedThroughSeq = tightened?.ThroughSeq ?? 0,
                TightenedPartsPerMillion = tightened?.PartsPerMillion ?? ToolResultViewOptions.PartsPerMillion,
                TightenedFloorChars = Options.ToolResultViewCapMinChars,
            }
            : null;

    /// <summary>
    ///     The estimator the cut rules measure the tail with: in a shaped view a tool result costs what the model
    ///     will be sent (placeholder or trimmed text), not its stored length.
    /// </summary>
    private Func<IMessage, long> ViewEstimator(IReadOnlyList<SequencedMessage> rows)
    {
        if (ToolResultShaping() is not { } shaping)
        {
            return CompactionTokenEstimate.Default;
        }

        var seqs = new Dictionary<IMessage, long>(ReferenceEqualityComparer.Instance);
        foreach (var row in rows)
        {
            seqs[row.Message] = row.Seq;
        }

        return message =>
            CompactionTokenEstimate.Default(
                ToolResultView.Apply(message, seqs.GetValueOrDefault(message, long.MaxValue), shaping)
            );
    }

    private void LogNewlyTrimmed(IReadOnlyList<IMessage> history, int capChars)
    {
        foreach (var message in history)
        {
            if (
                message is ToolCallResultMessage { IsDeferred: false, ToolCallId: { Length: > 0 } id } result
                && result.Result.Length > capChars
                && _trimLogged.Add(id)
            )
            {
                _host.Logger.LogInformation(
                    "Trimming tool result {ToolCallId} of {ResultChars} chars to the view cap of {CapChars} chars for thread {ThreadId}; the stored row is unchanged",
                    id,
                    result.Result.Length,
                    capChars,
                    _host.ThreadId
                );
            }
        }
    }

    /// <summary>
    ///     One compaction attempt, retried once when the store turned out to be ahead of the rows it read
    ///     (<c>watermark_drift</c>): the retry awaits the loop's in-flight appends and reloads before it cuts.
    /// </summary>
    private async Task<CompactionOutcome> CompactAsync(
        string runId,
        string generationId,
        CompactionTrigger trigger,
        long targetTokens,
        long ordinal,
        bool interruptedTurn,
        CancellationToken ct,
        long? minTailTokens = null,
        bool requireGain = false,
        PendingManualCompaction? manual = null,
        long? requestTokens = null,
        bool summaryFallback = false,
        string? knownSummaryFailure = null
    )
    {
        // A manual attempt was announced when it was claimed, so every way it ends is reported. An automatic one
        // is announced only once it is about to spend a summary call; a cut it never tries stays quiet.
        var report = new AttemptReport { Announced = manual is not null };
        try
        {
            var outcome = await CompactOnceAsync(
                    runId,
                    generationId,
                    trigger,
                    targetTokens,
                    ordinal,
                    interruptedTurn,
                    minTailTokens,
                    requireGain,
                    manual,
                    requestTokens,
                    summaryFallback,
                    knownSummaryFailure,
                    lastAttempt: false,
                    report,
                    ct
                )
                .ConfigureAwait(false);
            if (outcome is { Decision: CompactionDecisionKinds.Skipped, Reason: CompactionReasons.WatermarkDrift })
            {
                _host.Logger.LogInformation(
                    "Compaction for thread {ThreadId} run {RunId} read rows behind the store ({CompactionReason}); reloading and retrying once",
                    _host.ThreadId,
                    runId,
                    outcome.Reason
                );
                outcome = await CompactOnceAsync(
                        runId,
                        generationId,
                        trigger,
                        targetTokens,
                        ordinal,
                        interruptedTurn,
                        minTailTokens,
                        requireGain,
                        manual,
                        requestTokens,
                        summaryFallback,
                        knownSummaryFailure,
                        lastAttempt: true,
                        report,
                        ct
                    )
                    .ConfigureAwait(false);
            }

            return outcome;
        }
        catch (OperationCanceledException) when (report is { Announced: true, Ended: false })
        {
            // A stop or a disposal mid-summary: the client saw "running" and must see the attempt end.
            if (report.ActivatedCheckpointId is { } activated)
            {
                await PublishStatusAsync(
                        trigger,
                        CompactionStatusMessage.Phases.Applied,
                        null,
                        activated,
                        manual,
                        CancellationToken.None
                    )
                    .ConfigureAwait(false);
            }
            else if (manual is null)
            {
                await PublishCancelledAsync(trigger, manual).ConfigureAwait(false);
            }

            // A manual pass that did not activate is queued again or ended by TryRunManualAsync, which says which.
            throw;
        }
    }

    /// <summary>How far one attempt got in telling live subscribers about itself, across its drift retry.</summary>
    private sealed class AttemptReport
    {
        public bool Announced { get; set; }

        /// <summary>A terminal frame (refused, failed or applied) was sent.</summary>
        public bool Ended { get; set; }

        public string? ActivatedCheckpointId { get; set; }
    }

    private async Task<CompactionOutcome> CompactOnceAsync(
        string runId,
        string generationId,
        CompactionTrigger trigger,
        long targetTokens,
        long ordinal,
        bool interruptedTurn,
        long? minTailTokens,
        bool requireGain,
        PendingManualCompaction? manual,
        long? requestTokens,
        bool summaryFallback,
        string? knownSummaryFailure,
        bool lastAttempt,
        AttemptReport report,
        CancellationToken ct
    )
    {
        var store = _host.Store!;
        var shadow = trigger == CompactionTrigger.Shadow;
        var kind = shadow ? CompactionDecisionKinds.Shadow : CompactionDecisionKinds.Compact;

        async Task<CompactionOutcome> Skipped(string reason)
        {
            // A drift skip that is about to be retried is not the end of the attempt.
            if (report.Announced && (lastAttempt || reason != CompactionReasons.WatermarkDrift))
            {
                report.Ended = true;
                await PublishStatusAsync(trigger, CompactionStatusMessage.Phases.Refused, reason, null, manual, ct)
                    .ConfigureAwait(false);
            }

            return new(CompactionDecisionKinds.Skipped, reason, null, null, null, null, null, 0, trigger);
        }

        async Task<CompactionOutcome> Failed(string reason, long latency = 0)
        {
            if (report.Announced)
            {
                report.Ended = true;
                await PublishStatusAsync(trigger, CompactionStatusMessage.Phases.Failed, reason, null, manual, ct)
                    .ConfigureAwait(false);
            }

            return new(CompactionDecisionKinds.Failed, reason, null, null, null, null, null, latency, trigger);
        }

        await ReconcileOnceAsync(store, ct).ConfigureAwait(false);
        var (rows, skipReason) = await LoadRowsAsync(store, ct).ConfigureAwait(false);
        if (rows is null)
        {
            return await Skipped(skipReason!).ConfigureAwait(false);
        }

        var usable = UsableTokens;
        var estimator = ViewEstimator(rows);
        // The target is for the whole request; the tool definitions and the system prompt ride along whatever
        // the cut, so only the rest is the tail's to spend.
        var fixedTokens =
            _host.ToolSchemaTokens()
            + (
                _host.SystemPrompt is { } system
                    ? CompactionTokenEstimate.PerMessageOverhead + CompactionTokenEstimate.EstimateText(system)
                    : 0
            );
        var cut = await SelectCutAsync(
                rows,
                Math.Max(0, targetTokens - fixedTokens),
                estimator,
                interruptedTurn,
                ActiveBoundarySeq,
                minTailTokens,
                ct
            )
            .ConfigureAwait(false);
        if (cut is not CutDecision.Cut legal)
        {
            return await Skipped(((CutDecision.Skipped)cut).Reason).ConfigureAwait(false);
        }

        if (requireGain && usable is { } room && Options.MinCompactionGainRatio > 0)
        {
            // A cut that newly covers a sliver buys a summary call, a cache rewrite and a cooldown for nothing.
            var previousBoundary = ActiveBoundarySeq ?? 0;
            var freed = rows.Where(r => !r.IsCheckpointRow && r.Seq > previousBoundary && r.Seq <= legal.Seq)
                .Sum(r => estimator(r.Message));
            var needed = (long)(Options.MinCompactionGainRatio * room);
            if (freed < needed)
            {
                _host.Logger.LogInformation(
                    "Compaction for thread {ThreadId} run {RunId} skipped: cut {CutSeq} frees ~{FreedTokens} tokens, below the minimum gain of {MinGainTokens}",
                    _host.ThreadId,
                    runId,
                    legal.Seq,
                    freed,
                    needed
                );
                return await Skipped(CompactionSkipReasons.InsufficientGain).ConfigureAwait(false);
            }
        }

        var roster = _host.Roster();
        var known = new HashSet<string>(roster.Select(a => a.AgentId), StringComparer.Ordinal);
        if (Active is not null)
        {
            known.UnionWith(Active.Manifest.Agents.Select(a => a.AgentId));
        }

        var checkpointId = "cp-" + Guid.NewGuid().ToString("N");
        var summaryModel = Options.SummaryModelId ?? _host.DefaultOptions.ModelId;
        var request = new CheckpointBuildRequest
        {
            ThreadId = _host.ThreadId,
            RunId = runId,
            CheckpointId = checkpointId,
            Rows = rows,
            Cut = legal,
            Previous = Active,
            Board = await ConversationTodoProjection.LoadAsync(store, _host.ThreadId, ct).ConfigureAwait(false),
            Roster = roster,
            KnownAgentIds = known,
            Trigger = trigger,
            SummaryModelId = summaryModel,
            FromAgent = _host.AgentId,
            SummaryMaxOutputTokens = Options.SummaryMaxOutputTokens,
            SummaryRowCharCap = Options.SummaryRowCharCap,
            SummaryPromptCharBudget = Options.SummaryPromptCharBudget(_setup.ResolveWindowTokens?.Invoke(summaryModel)),
            Focus = manual?.Focus,
            RequestTokensBefore = requestTokens,
            ViewFixedTokens = fixedTokens,
            ViewEstimator = estimator,
            SummaryFallback = summaryFallback,
            KnownSummaryFailure = knownSummaryFailure,
        };

        if (shadow)
        {
            var build = await _pipeline.BuildAsync(request, ct).ConfigureAwait(false);
            var reason = build.IsValid ? null : build.Reason ?? CompactionReasons.SummaryCallFailed;
            await RecordShadowAsync(store, checkpointId, legal.Seq, rows[^1].Seq, reason, ct).ConfigureAwait(false);
            _compactionsThisRun++;
            return build.IsValid
                ? new CompactionOutcome(
                    kind,
                    null,
                    null,
                    build.Checkpoint,
                    legal.Seq,
                    build.Checkpoint!.Stats.EstimatedTokensAfter,
                    build.Checkpoint.Stats.RowsCovered,
                    build.LatencyMs,
                    trigger
                )
                : await Failed(reason!, build.LatencyMs).ConfigureAwait(false);
        }

        if (!report.Announced)
        {
            report.Announced = true;
            await PublishStatusAsync(trigger, CompactionStatusMessage.Phases.Running, null, null, manual, ct)
                .ConfigureAwait(false);
        }

        var result = await _pipeline.RunAsync(store, request, ct).ConfigureAwait(false);
        if (result.Outcome != CheckpointOutcome.Activated || result.Checkpoint is null)
        {
            var reason = result.Reason ?? CompactionReasons.SummaryCallFailed;
            if (result.Outcome == CheckpointOutcome.Skipped)
            {
                return await Skipped(reason).ConfigureAwait(false);
            }

            // A failed attempt spent a summary call, so it counts toward the run. Only a summarizer that failed
            // or produced an invalid manifest backs the next attempts off: a lost race with another writer
            // (stale watermark) or a store fault says nothing about whether the next summary will work. A
            // manual attempt never backs the automatic policy off.
            _compactionsThisRun++;
            if (trigger != CompactionTrigger.Manual && IsModelFailure(reason))
            {
                await RecordFailureAsync(store, runId, ordinal, reason, ct).ConfigureAwait(false);
            }

            return await Failed(reason, result.LatencyMs).ConfigureAwait(false);
        }

        var checkpoint = result.Checkpoint;
        lock (_gate)
        {
            _identities.AddOrUpdate(checkpoint, new RowIdentity { Seq = result.RowSeq });
        }

        _host.AppendInMemory(checkpoint);
        Active = checkpoint;
        report.ActivatedCheckpointId = checkpoint.CheckpointId;
        if (_host.PublishLive is { } publish)
        {
            await publish(checkpoint, ct).ConfigureAwait(false);
        }

        _compactionsThisRun++;
        if (result.Usage is { } usage)
        {
            _host.RecordSummaryUsage?.Invoke(usage, checkpointId, request.SummaryModelId);
        }

        // A fallback checkpoint says the summarizer is still unhealthy: a summary call it just made counts as a
        // failure, and the backoff it earned keeps gating the automatic attempts.
        var fallback = checkpoint.Stats.SummaryFallback;
        if (fallback is not null && knownSummaryFailure is null && IsModelFailure(fallback))
        {
            await RecordFailureAsync(store, runId, ordinal, fallback, ct).ConfigureAwait(false);
        }

        // Cooldown (§5.3 row 5): the next CooldownGenerations generations skip the economic row. The
        // stored ordinal is exclusive, so it sits one past the last cooled generation.
        _ = await CompactionStateProjection
            .UpdateAsync(
                store,
                _host.ThreadId,
                s =>
                    fallback is not null
                        ? s with
                        {
                            LastCheckpointGenerationOrdinal = ordinal,
                            CooldownUntilGenerationOrdinal = ordinal + Options.CooldownGenerations + 1,
                        }
                        : s with
                        {
                            LastCheckpointGenerationOrdinal = ordinal,
                            CooldownUntilGenerationOrdinal = ordinal + Options.CooldownGenerations + 1,
                            ConsecutiveFailures = 0,
                            FailureBackoffUntilGenerationOrdinal = null,
                        },
                ct
            )
            .ConfigureAwait(false);
        if (fallback is null)
        {
            _failureBackoffUntilOrdinal = null;
        }

        report.Ended = true;
        await PublishStatusAsync(
                trigger,
                CompactionStatusMessage.Phases.Applied,
                null,
                checkpoint.CheckpointId,
                manual,
                ct
            )
            .ConfigureAwait(false);

        return new CompactionOutcome(
            kind,
            fallback is not null
                ? CompactionReasons.SummaryFallback
                : trigger switch
                {
                    CompactionTrigger.Reactive => "reactive",
                    CompactionTrigger.Manual => ManualReason,
                    _ => null,
                },
            BuildView(),
            checkpoint,
            legal.Seq,
            checkpoint.Stats.EstimatedTokensAfter,
            checkpoint.Stats.RowsCovered,
            result.LatencyMs,
            trigger
        );
    }

    /// <summary>
    ///     A summarised build that failed: no usable answer, or a checkpoint that failed any validation rule. The fit
    ///     check's re-cut falls back without asking the model again after one of these in the same pass.
    /// </summary>
    private static bool IsSummaryAttemptFailure(string reason) =>
        reason == CompactionReasons.SummaryCallFailed
        || reason.StartsWith(CompactionReasons.ValidationFailedPrefix, StringComparison.Ordinal);

    /// <summary>
    ///     A failure that says the summary model is unhealthy, so it backs the automatic attempts off: no usable answer,
    ///     or a narrative over its cap (V7), the one rule model output alone decides. Every other rule checks what the
    ///     assembler builds from the rows, after it has dropped the model quotes that are not verbatim (V3), and V9 is the
    ///     envelope's size.
    /// </summary>
    private static bool IsModelFailure(string reason) =>
        reason == CompactionReasons.SummaryCallFailed || reason == CompactionReasons.ValidationFailed("V7");

    /// <summary>
    ///     Counts a failed compaction and starts its backoff: <see cref="CompactionOptions.FailureBackoff"/>
    ///     generations for the consecutive-failure count, exclusive like the cooldown.
    /// </summary>
    private async Task RecordFailureAsync(
        IConversationStore store,
        string runId,
        long ordinal,
        string reason,
        CancellationToken ct
    )
    {
        var written = await CompactionStateProjection
            .UpdateAsync(
                store,
                _host.ThreadId,
                s =>
                {
                    var failures = s.ConsecutiveFailures + 1;
                    var backoff = Options.FailureBackoff(failures);
                    return s with
                    {
                        ConsecutiveFailures = failures,
                        FailureBackoffUntilGenerationOrdinal = backoff > 0 ? ordinal + backoff + 1 : null,
                    };
                },
                ct
            )
            .ConfigureAwait(false);
        _failureBackoffUntilOrdinal = written?.FailureBackoffUntilGenerationOrdinal;
        _host.Logger.LogWarning(
            "Compaction for thread {ThreadId} run {RunId} failed with {CompactionReason}: {ConsecutiveFailures} in a row, backing off until generation {FailureBackoffUntilGenerationOrdinal}",
            _host.ThreadId,
            runId,
            reason,
            written?.ConsecutiveFailures,
            _failureBackoffUntilOrdinal
        );
    }

    /// <summary>
    ///     A shadow build leaves a record and no row (§5.2): the entry is written straight to Rejected with
    ///     <see cref="CompactionTrigger.Shadow"/> and the build's verdict as the reason.
    /// </summary>
    private Task RecordShadowAsync(
        IConversationStore store,
        string checkpointId,
        long boundarySeq,
        long watermark,
        string? failure,
        CancellationToken ct
    ) =>
        CompactionStateProjection.UpdateAsync(
            store,
            _host.ThreadId,
            s =>
                s with
                {
                    History =
                    [
                        .. s.History,
                        new CheckpointEntry
                        {
                            CheckpointId = checkpointId,
                            Status = CheckpointStatus.Rejected,
                            BoundarySeq = boundarySeq,
                            WatermarkAtPrepare = watermark,
                            Trigger = CompactionTrigger.Shadow,
                            Reason = failure ?? CompactionDecisionKinds.Shadow,
                            At = _clock.GetUtcNow(),
                            PreparedAt = _clock.GetUtcNow(),
                        },
                    ],
                },
            ct
        );

    private static long CandidateSeq(
        IReadOnlyList<SequencedMessage> rows,
        long targetTokens,
        Func<IMessage, long> estimator
    )
    {
        // The latest completed-generation boundary such that the tail after it fits the target; the cut
        // selector then moves it earlier as its rules require.
        long tail = 0;
        for (var i = rows.Count - 1; i >= 0; i--)
        {
            if (!rows[i].IsCheckpointRow)
            {
                tail += estimator(rows[i].Message);
            }

            if (tail >= targetTokens)
            {
                return rows[i].Seq;
            }
        }

        return 0;
    }

    private bool Reconcile(IReadOnlyList<PersistedMessage> persisted)
    {
        var byId = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var row in persisted)
        {
            if (row.Seq is { } seq)
            {
                byId[row.Id] = seq;
            }
        }

        var drift = false;
        foreach (var message in _host.HistorySnapshot())
        {
            if (message is CompactionCheckpointMessage)
            {
                continue;
            }

            lock (_gate)
            {
                if (!_identities.TryGetValue(message, out var identity))
                {
                    identity = new RowIdentity();
                    if (message is ToolCallResultMessage { ToolCallId: { Length: > 0 } callId })
                    {
                        // A replaced tool result is a new object with the store's deterministic id.
                        identity.Id = MessagePersistenceConverter.BuildToolResultPersistedId(_host.ThreadId, callId);
                    }

                    _identities.AddOrUpdate(message, identity);
                }

                if (identity.Seq is null && identity.Id is { } id && byId.TryGetValue(id, out var seq))
                {
                    identity.Seq = seq;
                }

                if (identity.Seq is null)
                {
                    drift = true;
                }
            }
        }

        return !drift;
    }

    private IReadOnlyList<SequencedMessage> Sequence(IReadOnlyList<IMessage> history)
    {
        var rows = new SequencedMessage[history.Count];
        for (var i = 0; i < history.Count; i++)
        {
            // A row without a known Seq was appended after the last reconciliation, so it is newer than
            // any boundary and belongs to the tail.
            rows[i] = new SequencedMessage(SeqOf(history[i]) ?? long.MaxValue, null, null, history[i]);
        }

        return rows;
    }

    private IReadOnlyList<IMessage> RawRequest() =>
        AgentContextProjection.Default.Build(_host.SystemPrompt, _host.HistorySnapshot(), null, RenderOptions);

    /// <summary>The request-size estimate the policy uses (<see cref="CompactionTokenEstimate.Default"/> summed).</summary>
    public static long EstimateTokens(IReadOnlyList<IMessage> messages) => Estimate(messages);

    private static long Estimate(IReadOnlyList<IMessage> messages)
    {
        long total = 0;
        foreach (var message in messages)
        {
            total += CompactionTokenEstimate.Default(message);
        }

        return total;
    }

    private CompactionEconomics? ResolveEconomics(bool cachingEnabled)
    {
        var modelId = _host.DefaultOptions.ModelId;
        if (_host.Pricing is null || string.IsNullOrEmpty(modelId) || _host.Pricing.Resolve(modelId) is not { } pricing)
        {
            return null;
        }

        return new CompactionEconomics
        {
            InputRatePerMillion = pricing.PromptPerMillion,
            OutputRatePerMillion = pricing.CompletionPerMillion,
            CacheWriteRatePerMillion =
                Options.CacheTtl >= TimeSpan.FromHours(1)
                    ? pricing.CacheWrite1hPerMillion ?? pricing.CacheWrite5mPerMillion
                    : pricing.CacheWrite5mPerMillion,
            CachingEnabled = cachingEnabled,
        };
    }

    private void SeedFromMetadata(ThreadMetadata? metadata)
    {
        if (_ordinalSeeded)
        {
            return;
        }

        _ordinalSeeded = true;
        if (ContextObservationProjection.LatestFromMetadata(metadata) is { } latest)
        {
            _generationOrdinal = Math.Max(_generationOrdinal, latest.GenerationOrdinal);
        }
    }

    private async Task SeedActivityAsync(IConversationStore store, CancellationToken ct)
    {
        if (_activitySeeded)
        {
            return;
        }

        _activitySeeded = true;
        try
        {
            _lastActivity = await ConversationActivity
                .GetLastActivityAsync(store, _host.ThreadId, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _host.Logger.LogDebug(ex, "Could not read last activity for thread {ThreadId}", _host.ThreadId);
        }
    }

    private async Task AdoptActiveAsync(
        IConversationStore store,
        CompactionState? state,
        IReadOnlyList<IMessage> history,
        CancellationToken ct
    )
    {
        var activeId = state?.ActiveCheckpointId;
        if (activeId is null)
        {
            Active = null;
            return;
        }

        if (Active?.CheckpointId == activeId)
        {
            return;
        }

        var row = history.OfType<CompactionCheckpointMessage>().LastOrDefault(c => c.CheckpointId == activeId);
        if (row is not null)
        {
            Active = row;
            return;
        }

        // The state names a checkpoint whose row this process never restored: not a view to trust.
        _ = await CompactionStateProjection
            .RollBackAsync(store, _host.ThreadId, CheckpointReasons.RowMissing, _clock.GetUtcNow(), ct)
            .ConfigureAwait(false);
        Active = null;
    }

    private static bool SameRow(IMessage converted, IMessage restored)
    {
        if (converted.GetType() != restored.GetType())
        {
            return false;
        }

        return (converted, restored) switch
        {
            (ToolCallResultMessage a, ToolCallResultMessage b) => string.Equals(
                a.ToolCallId,
                b.ToolCallId,
                StringComparison.Ordinal
            ),
            (ToolCallMessage a, ToolCallMessage b) => string.Equals(
                a.ToolCallId,
                b.ToolCallId,
                StringComparison.Ordinal
            ),
            (CompactionCheckpointMessage a, CompactionCheckpointMessage b) => a.CheckpointId == b.CheckpointId,
            (ICanGetText a, ICanGetText b) => string.Equals(a.GetText(), b.GetText(), StringComparison.Ordinal),
            _ => true,
        };
    }

    private async Task RecordAsync(
        string runId,
        string generationId,
        long ordinal,
        long tokens,
        int rowsInView,
        CompactionDecision decision,
        CompactionOutcome? outcome,
        CancellationToken ct
    )
    {
        await RecordObservationAsync(runId, generationId, ordinal, tokens, rowsInView, decision, ct)
            .ConfigureAwait(false);
        await PublishAsync(LifecycleEventTypes.CompactionDecided, runId, generationId, decision, outcome, ct)
            .ConfigureAwait(false);
        if (outcome?.View is not null)
        {
            await PublishAsync(LifecycleEventTypes.CompactionApplied, runId, generationId, decision, outcome, ct)
                .ConfigureAwait(false);
        }
        else if (outcome is { Decision: CompactionDecisionKinds.Failed })
        {
            await PublishAsync(LifecycleEventTypes.CompactionFailed, runId, generationId, decision, outcome, ct)
                .ConfigureAwait(false);
        }
    }

    /// <summary>Stamps <paramref name="decision"/> on the generation's observation; a second call for the same generation supersedes it.</summary>
    private async Task RecordObservationAsync(
        string runId,
        string generationId,
        long ordinal,
        long tokens,
        int rowsInView,
        CompactionDecision decision,
        CancellationToken ct
    )
    {
        if (_host.Store is { } store)
        {
            try
            {
                await ContextObservationProjection
                    .RecordAsync(
                        store,
                        new ContextObservation
                        {
                            ThreadId = _host.ThreadId,
                            AgentId = _host.AgentId,
                            RunId = runId,
                            GenerationId = generationId,
                            GenerationOrdinal = ordinal,
                            ObservedAtUtc = _clock.GetUtcNow(),
                            EffectiveModelId = _host.DefaultOptions.ModelId ?? string.Empty,
                            EstimatedInputTokens = tokens,
                            Provenance = MeasurementProvenance.Estimated,
                            WindowTokens = WindowTokens,
                            ReserveTokens = ReserveTokens,
                            ActiveCheckpointId = Active?.CheckpointId,
                            RowsInView = rowsInView,
                            Decision = decision.Summary,
                        },
                        Options.ObservationHistoryLength,
                        ct
                    )
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _host.Logger.LogWarning(
                    ex,
                    "Failed to record context observation for thread {ThreadId}",
                    _host.ThreadId
                );
            }
        }
    }

    private async Task PublishAsync(
        string eventType,
        string runId,
        string generationId,
        CompactionDecision decision,
        CompactionOutcome? outcome,
        CancellationToken ct
    )
    {
        if (_host.Lifecycle is not { } lifecycle)
        {
            return;
        }

        var summary = decision.Summary;
        _ = await lifecycle
            .CompactionAsync(
                eventType,
                runId,
                generationId,
                new CompactionPayload
                {
                    Decision = decision.Decision,
                    Reason = decision.Reason,
                    Trigger = outcome?.Trigger.ToString().ToLowerInvariant(),
                    CheckpointId = outcome?.Checkpoint?.CheckpointId,
                    BoundarySeq = outcome?.Checkpoint?.Boundary.Seq,
                    Utilization = summary.Utilization,
                    Tokens = summary.Tokens,
                    Window = summary.Window,
                    Reserve = summary.Reserve,
                    CacheTemperature = summary.CacheTemperature.ToString().ToLowerInvariant(),
                    CooldownRemaining = summary.CooldownRemaining,
                    PredictedSavingsMicros = summary.PredictedSavingsMicros,
                    CutSeq = summary.CutSeq,
                    TokensAfter = outcome?.TokensAfter,
                    RowsCovered = outcome?.RowsCovered,
                    LatencyMilliseconds = outcome?.LatencyMs,
                },
                ct
            )
            .ConfigureAwait(false);
    }
}
