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

    private CompactionCheckpointMessage? _active;
    private long _generationOrdinal;
    private bool _ordinalSeeded;
    private DateTimeOffset? _lastActivity;
    private bool _activitySeeded;
    private int _compactionsThisRun;
    private bool _reactiveUsed;

    public CompactionRuntime(CompactionSetup setup, CompactionRuntimeHost host, IAgent providerAgent)
    {
        ArgumentNullException.ThrowIfNull(setup);
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(providerAgent);
        _setup = setup;
        _host = host;
        _clock = setup.Clock ?? TimeProvider.System;
        var options = setup.Options;
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
                    CheckpointTokenCap = options.CheckpointTokenCap,
                },
                Render = RenderOptions,
            },
            _clock
        );

        // A loop without a store has nowhere to append a checkpoint, so it can observe but never compact.
        var resolved = options.ResolveMode(setup.ProviderId, host.DefaultOptions.ModelId);
        Mode = host.Store is null && resolved > CompactionMode.Warn ? CompactionMode.Warn : resolved;
    }

    /// <summary>The mode for this loop's route.</summary>
    public CompactionMode Mode { get; }

    /// <summary>False in <see cref="CompactionMode.Off"/>: the loop neither evaluates nor builds a view.</summary>
    public bool IsEnabled => Mode > CompactionMode.Off;

    public CompactionOptions Options => _setup.Options;

    /// <summary>The checkpoint the view is built on, when one is active.</summary>
    public CompactionCheckpointMessage? Active => _active;

    public long? ActiveBoundarySeq => _active?.Boundary.Seq;

    /// <summary>The name the envelope's recall hint and the recall tool share (spec 679 §6).</summary>
    public const string RecallToolName = "RecallConversation";

    public CheckpointRenderOptions RenderOptions { get; } = new() { RecallToolName = RecallToolName };

    public long ReserveTokens =>
        (_host.DefaultOptions.MaxToken ?? MultiTurnAgentBase.DefaultMaxTokenFloor) + Options.ReserveMarginTokens;

    public long? WindowTokens => _setup.ResolveWindowTokens?.Invoke(_host.DefaultOptions.ModelId);

    /// <summary>Called when a run starts: the per-run compaction budget resets.</summary>
    public void OnRunStarted()
    {
        _compactionsThisRun = 0;
        _reactiveUsed = false;
    }

    /// <summary>Remembers the persisted id of an in-memory row and the append that is still in flight.</summary>
    public void TrackPersisted(IMessage message, string persistedId, Task append)
    {
        lock (_gate)
        {
            _identities.AddOrUpdate(message, new RowIdentity { Id = persistedId });
            _inFlightPersists.RemoveAll(t => t.IsCompleted);
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

        var state = await CompactionStateProjection
            .ReconcileAsync(store, _host.ThreadId, _clock.GetUtcNow(), ct)
            .ConfigureAwait(false);
        await AdoptActiveAsync(store, state, restored, ct).ConfigureAwait(false);
    }

    /// <summary>The request as the model should see it, or null when there is no active checkpoint (the raw history is the view).</summary>
    public IReadOnlyList<IMessage>? BuildView()
    {
        if (!IsEnabled || _active is null)
        {
            return null;
        }

        return AgentContextProjection.Default.Build(
            _host.SystemPrompt,
            Sequence(_host.HistorySnapshot()),
            _active,
            RenderOptions
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
        CancellationToken ct
    )
    {
        var store = _host.Store;
        var metadata = store is null ? null : await store.LoadMetadataAsync(_host.ThreadId, ct).ConfigureAwait(false);
        var state = CompactionStateProjection.FromMetadata(metadata);
        SeedFromMetadata(metadata);
        if (store is not null)
        {
            await SeedActivityAsync(store, ct).ConfigureAwait(false);
        }

        var ordinal = ++_generationOrdinal;
        var killed = Options.IsKilled(_setup.ReadEnvironment);
        IReadOnlyList<IMessage>? replacement = null;
        if (killed && store is not null && state?.ActiveCheckpointId is not null)
        {
            // §8.4: kill = Skipped(disabled) for new decisions and Active → RolledBack on the next
            // request. The request handed in was built on the view; the raw history goes out instead.
            _ = await CompactionStateProjection
                .RollBackAsync(store, _host.ThreadId, CompactionFailureReasons.Killed, _clock.GetUtcNow(), ct)
                .ConfigureAwait(false);
            state = state with { ActiveCheckpointId = null, ActiveBoundarySeq = null };
            _active = null;
            replacement = RawRequest();
        }
        else if (store is not null && !killed)
        {
            await AdoptActiveAsync(store, state, _host.HistorySnapshot(), ct).ConfigureAwait(false);
        }

        var view = replacement ?? request;
        var tokens = Estimate(view);
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
            NewTokensSinceCheckpoint = _active is null
                ? null
                : Math.Max(0, tokens - _active.Stats.EstimatedTokensAfter),
            CompactionsThisRun = _compactionsThisRun,
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
        if (decision.IsCompact)
        {
            outcome = await CompactAsync(
                    runId,
                    generationId,
                    decision.Decision == CompactionDecisionKinds.Shadow
                        ? CompactionTrigger.Shadow
                        : CompactionTrigger.Preemptive,
                    decision.TargetTokens ?? (tokens / 2),
                    ordinal,
                    interruptedTurn,
                    ct
                )
                .ConfigureAwait(false);
            decision = outcome.Apply(decision);
            replacement = outcome.View ?? replacement;
        }

        await RecordAsync(runId, generationId, ordinal, tokens, view.Count, decision, outcome, ct)
            .ConfigureAwait(false);

        ContextOverflowException? refusal = null;
        var usable = window is { } w ? w - ReserveTokens : (long?)null;
        if (
            Mode == CompactionMode.Compact
            && usable is { } limit
            && outcome?.View is null
            && tokens > limit
            && decision.Decision is CompactionDecisionKinds.Failed or CompactionDecisionKinds.Skipped
            && decision.Reason is not (CompactionSkipReasons.Disabled or CompactionSkipReasons.ProviderOwnedSession)
        )
        {
            refusal = new ContextOverflowException(
                CompactionFailureReasons.OverflowAfterCompaction,
                $"request of ~{tokens} tokens exceeds the usable window of {limit} and compaction answered {decision.Reason}"
            );
            await PublishAsync(
                    LifecycleEventTypes.CompactionFailed,
                    runId,
                    generationId,
                    decision with
                    {
                        Decision = CompactionDecisionKinds.Failed,
                        Reason = refusal.Reason,
                    },
                    null,
                    ct
                )
                .ConfigureAwait(false);
        }

        return new CompactionPass(decision, replacement, refusal);
    }

    /// <summary>The built-in overflow verdict, or the host's (spec Q1: a transport abort never qualifies).</summary>
    public bool IsContextOverflow(Exception exception, long estimatedTokens)
    {
        if (_setup.IsContextOverflow is { } verdict)
        {
            return verdict(exception);
        }

        var atCapacity = WindowTokens is { } w
            ? estimatedTokens >= w - ReserveTokens
            : estimatedTokens >= Options.WarnAbsoluteTokens;
        if (!atCapacity)
        {
            return false;
        }

        for (var candidate = exception; candidate is not null; candidate = candidate.InnerException)
        {
            if (
                candidate is HttpRequestException
                {
                    StatusCode: System.Net.HttpStatusCode.BadRequest or System.Net.HttpStatusCode.RequestEntityTooLarge
                }
            )
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     The reactive path (§5.1): after an overflow, compact once per run and tell the caller whether a
    ///     checkpoint was activated so the same input can be retried once.
    /// </summary>
    public async Task<bool> TryReactiveAsync(string runId, string generationId, CancellationToken ct)
    {
        if (Mode != CompactionMode.Compact || _reactiveUsed || _host.Store is null)
        {
            return false;
        }

        _reactiveUsed = true;
        // The request that overflowed is the best window estimate there is when the capacity is unknown.
        var tokens = Estimate(BuildView() ?? RawRequest());
        var usable = WindowTokens is { } w ? w - ReserveTokens : tokens;
        var target = (long)(Options.TargetRatio * usable);
        var outcome = await CompactAsync(
                runId,
                generationId,
                CompactionTrigger.Reactive,
                target,
                _generationOrdinal,
                false,
                ct
            )
            .ConfigureAwait(false);
        var decision = outcome.Apply(
            new CompactionDecision
            {
                Decision = CompactionDecisionKinds.Compact,
                Reason = "reactive",
                TargetTokens = target,
                Summary = new CompactionDecisionSummary
                {
                    Decision = CompactionDecisionKinds.Compact,
                    Reason = "reactive",
                    Tokens = tokens,
                    Window = WindowTokens,
                    Reserve = ReserveTokens,
                },
            }
        );
        await RecordAsync(runId, generationId, _generationOrdinal, tokens, 0, decision, outcome, ct)
            .ConfigureAwait(false);
        return outcome.View is not null;
    }

    /// <summary>Records the terminal failure of the reactive path before the run is failed.</summary>
    public Task ReportOverflowAfterCompactionAsync(string runId, string generationId, CancellationToken ct) =>
        PublishAsync(
            LifecycleEventTypes.CompactionFailed,
            runId,
            generationId,
            new CompactionDecision
            {
                Decision = CompactionDecisionKinds.Failed,
                Reason = CompactionFailureReasons.OverflowAfterCompaction,
                Summary = new CompactionDecisionSummary
                {
                    Decision = CompactionDecisionKinds.Failed,
                    Reason = CompactionFailureReasons.OverflowAfterCompaction,
                    Tokens = Estimate(BuildView() ?? RawRequest()),
                    Window = WindowTokens,
                    Reserve = ReserveTokens,
                },
            },
            null,
            ct
        );

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

    private async Task<CompactionOutcome> CompactAsync(
        string runId,
        string generationId,
        CompactionTrigger trigger,
        long targetTokens,
        long ordinal,
        bool interruptedTurn,
        CancellationToken ct
    )
    {
        var store = _host.Store!;
        var shadow = trigger == CompactionTrigger.Shadow;
        var kind = shadow ? CompactionDecisionKinds.Shadow : CompactionDecisionKinds.Compact;

        CompactionOutcome Skipped(string reason) =>
            new(CompactionDecisionKinds.Skipped, reason, null, null, null, null, null, 0, trigger);

        CompactionOutcome Failed(string reason, long latency = 0) =>
            new(CompactionDecisionKinds.Failed, reason, null, null, null, null, null, latency, trigger);

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
            return Skipped(CompactionSkipReasons.UnsafeState);
        }

        if (!Reconcile(persisted))
        {
            return Skipped(CompactionReasons.WatermarkDrift);
        }

        var candidate = CandidateSeq(rows, targetTokens);
        var runs = _host.RunLedgerStore is { } ledger
            ? await ledger.ListRunLedgerAsync(_host.ThreadId, ct).ConfigureAwait(false)
            : [];
        var cut = CutSelector.Select(
            new CutRequest(
                rows,
                candidate,
                new CutBlockingState(_host.OwedContinuations(), interruptedTurn ? 1 : 0),
                runs,
                ActiveBoundarySeq,
                new CutSelectorOptions
                {
                    MinTailTokens = Options.MinTailTokens,
                    MaxTailTokens = Options.MaxTailTokens,
                    CorrectionLookbackRuns = Options.CorrectionLookbackRuns,
                }
            )
        );
        if (cut is not CutDecision.Cut legal)
        {
            return Skipped(((CutDecision.Skipped)cut).Reason);
        }

        var roster = _host.Roster();
        var known = new HashSet<string>(roster.Select(a => a.AgentId), StringComparer.Ordinal);
        if (_active is not null)
        {
            known.UnionWith(_active.Manifest.Agents.Select(a => a.AgentId));
        }

        var checkpointId = "cp-" + Guid.NewGuid().ToString("N");
        var request = new CheckpointBuildRequest
        {
            ThreadId = _host.ThreadId,
            RunId = runId,
            CheckpointId = checkpointId,
            Rows = rows,
            Cut = legal,
            Previous = _active,
            Board = await ConversationTodoProjection.LoadAsync(store, _host.ThreadId, ct).ConfigureAwait(false),
            Roster = roster,
            KnownAgentIds = known,
            Trigger = trigger,
            SummaryModelId = Options.SummaryModelId ?? _host.DefaultOptions.ModelId,
            FromAgent = _host.AgentId,
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
                : Failed(reason!, build.LatencyMs);
        }

        var result = await _pipeline.RunAsync(store, request, ct).ConfigureAwait(false);
        if (result.Outcome != CheckpointOutcome.Activated || result.Checkpoint is null)
        {
            var reason = result.Reason ?? CompactionReasons.SummaryCallFailed;
            return result.Outcome == CheckpointOutcome.Skipped ? Skipped(reason) : Failed(reason, result.LatencyMs);
        }

        var checkpoint = result.Checkpoint;
        lock (_gate)
        {
            _identities.AddOrUpdate(checkpoint, new RowIdentity { Seq = result.RowSeq });
        }

        _host.AppendInMemory(checkpoint);
        _active = checkpoint;
        _compactionsThisRun++;
        if (result.Usage is { } usage)
        {
            _host.RecordSummaryUsage?.Invoke(usage, checkpointId, request.SummaryModelId);
        }

        // Cooldown (§5.3 row 5): the next CooldownGenerations generations skip the economic row. The
        // stored ordinal is exclusive, so it sits one past the last cooled generation.
        _ = await CompactionStateProjection
            .UpdateAsync(
                store,
                _host.ThreadId,
                s =>
                    s with
                    {
                        LastCheckpointGenerationOrdinal = ordinal,
                        CooldownUntilGenerationOrdinal = ordinal + Options.CooldownGenerations + 1,
                    },
                ct
            )
            .ConfigureAwait(false);

        return new CompactionOutcome(
            kind,
            trigger == CompactionTrigger.Reactive ? "reactive" : null,
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
                        },
                    ],
                },
            ct
        );

    private long CandidateSeq(IReadOnlyList<SequencedMessage> rows, long targetTokens)
    {
        // The latest completed-generation boundary such that the tail after it fits the target; the cut
        // selector then moves it earlier as its rules require.
        long tail = 0;
        for (var i = rows.Count - 1; i >= 0; i--)
        {
            if (!rows[i].IsCheckpointRow)
            {
                tail += CompactionTokenEstimate.Default(rows[i].Message);
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
            _active = null;
            return;
        }

        if (_active?.CheckpointId == activeId)
        {
            return;
        }

        var row = history.OfType<CompactionCheckpointMessage>().LastOrDefault(c => c.CheckpointId == activeId);
        if (row is not null)
        {
            _active = row;
            return;
        }

        // The state names a checkpoint whose row this process never restored: not a view to trust.
        _ = await CompactionStateProjection
            .RollBackAsync(store, _host.ThreadId, CheckpointReasons.RowMissing, _clock.GetUtcNow(), ct)
            .ConfigureAwait(false);
        _active = null;
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
                            ActiveCheckpointId = _active?.CheckpointId,
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
