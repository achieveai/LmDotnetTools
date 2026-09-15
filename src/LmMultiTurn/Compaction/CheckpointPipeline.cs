using System.Diagnostics;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Models;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AchieveAi.LmDotnetTools.LmMultiTurn.Compaction;

/// <summary>Everything a checkpoint is built from, once the policy has a legal cut.</summary>
internal sealed record CheckpointBuildRequest
{
    public required string ThreadId { get; init; }

    /// <summary>The run the checkpoint row is persisted under.</summary>
    public required string RunId { get; init; }

    public required string CheckpointId { get; init; }

    /// <summary>Every canonical row of the thread, store-derived (ids are needed for V2 and the boundary).</summary>
    public required IReadOnlyList<SequencedMessage> Rows { get; init; }

    public required CutDecision.Cut Cut { get; init; }

    /// <summary>The active checkpoint, when chaining (§2.5).</summary>
    public CompactionCheckpointMessage? Previous { get; init; }

    public TodoBoardSnapshot? Board { get; init; }

    public IReadOnlyList<AgentRef> Roster { get; init; } = [];

    /// <summary>Ids V5 accepts; defaults to the roster's.</summary>
    public IReadOnlyCollection<string>? KnownAgentIds { get; init; }

    public CompactionTrigger Trigger { get; init; } = CompactionTrigger.Preemptive;

    public string? SummaryModelId { get; init; }

    /// <summary>The agent this thread belongs to (<c>agent-N</c>), or null for the root.</summary>
    public string? FromAgent { get; init; }

    /// <summary>Passed to <see cref="CheckpointSummaryRequest.MaxOutputTokens" />.</summary>
    public int? SummaryMaxOutputTokens { get; init; }

    /// <summary>Passed to <see cref="CheckpointSummaryRequest.RowCharCap" />.</summary>
    public int? SummaryRowCharCap { get; init; }

    /// <summary>Passed to <see cref="CheckpointSummaryRequest.PromptCharBudget" />.</summary>
    public int? SummaryPromptCharBudget { get; init; }

    /// <summary>A manual request's focus: passed to the summarizer and stored on the checkpoint.</summary>
    public string? Focus { get; init; }

    /// <summary>
    ///     The request estimate the compaction decision measured, stored as <see cref="CheckpointStats.EstimatedTokensBefore" />;
    ///     null counts the covered rows instead.
    /// </summary>
    public long? RequestTokensBefore { get; init; }

    /// <summary>Tokens every request carries whatever the cut (system prompt, tool definitions); part of the after-estimate.</summary>
    public long? ViewFixedTokens { get; init; }

    /// <summary>How the view sizes a tail row (cleared and trimmed results); null uses the pipeline's estimator.</summary>
    public Func<IMessage, long>? ViewEstimator { get; init; }

    /// <summary>
    ///     When the summary fails (no answer, a timeout, or a checkpoint that fails validation), build the checkpoint
    ///     without it: the deterministic sections, no model sections, and <see cref="CheckpointPipeline.FallbackNarrative" />
    ///     (§3.4). Only the fit check's escalation re-cut sets this; every other attempt fails honestly.
    /// </summary>
    public bool SummaryFallback { get; init; }

    /// <summary>
    ///     A summary failure this pass already saw. With <see cref="SummaryFallback" /> the model is not called again:
    ///     the fallback is built at once and records this reason.
    /// </summary>
    public string? KnownSummaryFailure { get; init; }
}

/// <summary>What <see cref="CheckpointPipeline.BuildAsync" /> produced before anything touched the store.</summary>
internal sealed record CheckpointBuildResult
{
    /// <summary>The checkpoint, whether or not it validated; null when the summary call failed.</summary>
    public CompactionCheckpointMessage? Checkpoint { get; init; }

    public CheckpointValidationResult? Validation { get; init; }

    /// <summary>The summary pass's usage, stamped with the thread, run and a generation id.</summary>
    public UsageMessage? Usage { get; init; }

    public long LatencyMs { get; init; }

    /// <summary>Null when the checkpoint validated; else <c>summary_call_failed</c> or <c>validation_failed:Vn</c>.</summary>
    public string? Reason { get; init; }

    public bool IsValid => Checkpoint is not null && Validation is { IsValid: true };
}

internal enum CheckpointOutcome
{
    /// <summary>The row is appended and the checkpoint is the active one.</summary>
    Activated,

    /// <summary>Nothing was prepared; the reason says why.</summary>
    Skipped,

    /// <summary>Prepared, then rejected; the reason is recorded in <c>compaction.state</c>.</summary>
    Rejected,
}

/// <summary>What <see cref="CheckpointPipeline.RunAsync" /> did (spec 679 §3.5).</summary>
internal sealed record CheckpointRunResult
{
    public required CheckpointOutcome Outcome { get; init; }

    /// <summary>The typed reason for a skip or a rejection; null when activated.</summary>
    public string? Reason { get; init; }

    public CompactionCheckpointMessage? Checkpoint { get; init; }

    /// <summary>The seq the checkpoint row got, once appended.</summary>
    public long? RowSeq { get; init; }

    public UsageMessage? Usage { get; init; }

    public long LatencyMs { get; init; }

    public CheckpointValidationResult? Validation { get; init; }
}

/// <summary>The knobs the pipeline hands to the assembler, validator and estimator.</summary>
internal sealed record CheckpointPipelineOptions
{
    public CheckpointValidationOptions Validation { get; init; } = new();

    public ManifestAssemblerOptions Assembler { get; init; } = new();

    public Func<IMessage, long> Estimator { get; init; } = CompactionTokenEstimate.Default;

    /// <summary>How the envelope is rendered for the size estimate; must match what the projection dispatches.</summary>
    public CheckpointRenderOptions Render { get; init; } = CheckpointRenderOptions.Default;

    /// <summary>How long one summary call may take; null waits as long as the caller's token allows.</summary>
    public TimeSpan? SummaryTimeout { get; init; }

    /// <summary>Summary calls per build: a call that throws, times out or returns nothing is retried until this many were made.</summary>
    public int SummaryAttempts { get; init; } = 1;

    public ILogger Logger { get; init; } = NullLogger.Instance;
}

/// <summary>
///     Builds, validates and commits one checkpoint (spec 679 §3.2–§3.5). <see cref="BuildAsync" /> is
///     the store-free half — summarize, assemble, validate — so a shadow run can measure what a
///     checkpoint would be without writing one. <see cref="RunAsync" /> wraps it in the #680 state
///     machine: watermark drift skips before anything is prepared; a failed summary or validation is
///     rejected with its typed reason and the view the model sees is unchanged; a row appended by
///     someone else between prepare and commit rejects with <c>stale_watermark</c>; only a checkpoint
///     whose row landed at watermark + 1 activates.
/// </summary>
internal sealed class CheckpointPipeline(
    ICheckpointSummarizer summarizer,
    CheckpointPipelineOptions? options = null,
    TimeProvider? clock = null
)
{
    /// <summary>The whole narrative of a checkpoint built without its summary (§3.4).</summary>
    public const string FallbackNarrative =
        "The turns this checkpoint covers were not summarised: the summary call failed. Call "
        + RecallConversationToolProvider.ToolName
        + " with a seq or tool_call_id from the index to read any of them verbatim.";

    private readonly CheckpointPipelineOptions _options = options ?? new CheckpointPipelineOptions();
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>
    ///     Summarize, assemble and validate; nothing is written. With <see cref="CheckpointBuildRequest.SummaryFallback" />
    ///     a failed summary yields the fallback checkpoint instead of a failure.
    /// </summary>
    public async Task<CheckpointBuildResult> BuildAsync(CheckpointBuildRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var rows = request.Rows;
        var cut = request.Cut;
        var previousBoundary = request.Previous?.Boundary.Seq ?? 0;
        var covered = rows.Where(r => r.Seq > previousBoundary && r.Seq <= cut.Seq).ToList();
        var runIds = covered.Select(r => r.EffectiveRunId).OfType<string>().Distinct(StringComparer.Ordinal).ToList();

        var summaryRequest = new CheckpointSummaryRequest
        {
            ThreadId = request.ThreadId,
            PreviousManifest = request.Previous?.Manifest,
            PreviousNarrative = request.Previous?.Narrative,
            Rows = covered,
            CurrentInstruction = cut.CurrentInstruction,
            Board = request.Board,
            Roster = request.Roster,
            RunIds = runIds,
            NarrativeTokenCap = _options.Validation.NarrativeTokenCap,
            ModelId = request.SummaryModelId,
            MaxOutputTokens = request.SummaryMaxOutputTokens,
            RowCharCap = request.SummaryRowCharCap,
            PromptCharBudget = request.SummaryPromptCharBudget,
            Focus = request.Focus,
        };

        var stopwatch = Stopwatch.StartNew();
        var response = request is { SummaryFallback: true, KnownSummaryFailure: not null }
            ? null
            : await SummarizeAsync(summaryRequest, request, ct).ConfigureAwait(false);
        stopwatch.Stop();
        if (response?.Summary is null)
        {
            return request.SummaryFallback
                ? Fallback(
                    request,
                    request.KnownSummaryFailure ?? CompactionReasons.SummaryCallFailed,
                    stopwatch.ElapsedMilliseconds
                )
                : new CheckpointBuildResult
                {
                    LatencyMs = stopwatch.ElapsedMilliseconds,
                    Reason = CompactionReasons.SummaryCallFailed,
                };
        }

        var build = Assemble(
            request,
            response.Summary,
            Stamp(response.Usage, request),
            stopwatch.ElapsedMilliseconds,
            summaryFallback: null
        );
        return !build.IsValid && request.SummaryFallback
            ? Fallback(request, build.Reason ?? CompactionReasons.SummaryCallFailed, stopwatch.ElapsedMilliseconds)
            : build;
    }

    /// <summary>
    ///     The checkpoint without its summary (§3.4): the same assembly with every model section empty and
    ///     <see cref="FallbackNarrative" />, validated by the same rules.
    /// </summary>
    private CheckpointBuildResult Fallback(CheckpointBuildRequest request, string failure, long latencyMs)
    {
        _options.Logger.LogWarning(
            "Checkpoint {CheckpointId} for thread {ThreadId} run {RunId} is built without its summary after {SummaryFailure}",
            request.CheckpointId,
            request.ThreadId,
            request.RunId,
            failure
        );
        return Assemble(
            request,
            new CheckpointSummary
            {
                Narrative = FallbackNarrativeAfter(
                    request.Previous?.Narrative,
                    _options.Validation.NarrativeTokenCap,
                    _options.Validation.TextEstimator
                ),
            },
            usage: null,
            latencyMs,
            summaryFallback: failure
        );
    }

    /// <summary>
    ///     The previous checkpoint's narrative, the only summary of what it covered, followed by
    ///     <see cref="FallbackNarrative" />. When both break <paramref name="cap" /> the previous narrative loses its
    ///     oldest part first; the fallback line is always whole.
    /// </summary>
    internal static string FallbackNarrativeAfter(string? previous, long cap, Func<string?, long> estimate)
    {
        // A fallback on a fallback says so once.
        if (previous?.EndsWith(FallbackNarrative, StringComparison.Ordinal) == true)
        {
            previous = previous[..^FallbackNarrative.Length].TrimEnd();
        }

        if (string.IsNullOrWhiteSpace(previous))
        {
            return FallbackNarrative;
        }

        // The longest end of the previous narrative that still fits.
        return EnvelopeBudget.KeepEnd(previous + FallbackSuffix, FallbackSuffix, text => estimate(text) <= cap);
    }

    private const string FallbackSuffix = "\n\n" + FallbackNarrative;

    /// <summary>Assembles the manifest around <paramref name="summary" />, sizes the checkpoint and validates it.</summary>
    private CheckpointBuildResult Assemble(
        CheckpointBuildRequest request,
        CheckpointSummary summary,
        UsageMessage? usage,
        long latencyMs,
        string? summaryFallback
    )
    {
        var rows = request.Rows;
        var cut = request.Cut;
        var previousBoundary = request.Previous?.Boundary.Seq ?? 0;
        var manifest = ManifestAssembler.Assemble(
            rows,
            cut,
            request.Previous?.Manifest,
            previousBoundary,
            summary,
            request.Board,
            request.Roster,
            _options.Assembler,
            dropped =>
                _options.Logger.LogInformation(
                    "Checkpoint {CheckpointId} for thread {ThreadId} run {RunId} dropped {DroppedQuotes} model quotes that are not verbatim or repeat a trimmed current instruction, at seqs {DroppedSeqs}",
                    request.CheckpointId,
                    request.ThreadId,
                    request.RunId,
                    dropped.Count,
                    string.Join(", ", dropped.Select(q => q.Seq).Distinct().Order())
                ),
            _options.Validation
        );

        var boundaryRow = rows.FirstOrDefault(r => r.Seq == cut.Seq);
        var before =
            request.RequestTokensBefore
            ?? rows.Where(r => r.Seq <= cut.Seq && !r.IsCheckpointRow).Sum(r => _options.Estimator(r.Message));
        var tailEstimator = request.ViewEstimator ?? _options.Estimator;
        var tail = rows.Where(r => r.Seq > cut.Seq && !r.IsCheckpointRow).Sum(r => tailEstimator(r.Message));
        var checkpoint = new CompactionCheckpointMessage
        {
            CheckpointId = request.CheckpointId,
            Boundary = new CheckpointBoundary { Seq = cut.Seq, MessageId = boundaryRow?.MessageId ?? string.Empty },
            SupersedesCheckpointId = request.Previous?.CheckpointId,
            Trigger = request.Trigger,
            Manifest = manifest,
            Narrative = summary.Narrative,
            Focus = request.Focus,
            CreatedAtUtc = _clock.GetUtcNow(),
            ThreadId = request.ThreadId,
            RunId = request.RunId,
            FromAgent = request.FromAgent,
            GenerationId = usage?.GenerationId,
            Stats = new CheckpointStats
            {
                RowsCovered = cut.Seq,
                EstimatedTokensBefore = before,
                SummaryUsageAttemptId = usage is null ? null : $"{request.ThreadId}:{usage.GenerationId}",
                SummaryLatencyMs = latencyMs,
                SummaryFallback = summaryFallback,
            },
        };
        checkpoint = FitEnvelope(checkpoint, request, summary, summaryFallback is not null);

        // After is the request the new view sends: the fixed prefix, the envelope and the kept tail. Before and after
        // are then the same kind of number, which is what a "before -> after" display implies.
        checkpoint = checkpoint with
        {
            Stats = checkpoint.Stats with
            {
                EstimatedTokensAfter =
                    (request.ViewFixedTokens ?? 0)
                    + CompactionTokenEstimate.PerMessageOverhead
                    + CompactionTokenEstimate.EstimateText(checkpoint.RenderEnvelope(_options.Render))
                    + tail,
            },
        };

        var validation = CheckpointValidator.Validate(
            checkpoint,
            new CheckpointValidationContext(
                rows,
                request.Board,
                request.KnownAgentIds ?? [.. request.Roster.Select(a => a.AgentId)]
            ),
            _options.Validation with
            {
                Render = _options.Render,
            }
        );
        if (!validation.IsValid)
        {
            // The detail names seqs, ids and sizes, never row text.
            _options.Logger.LogWarning(
                "Checkpoint {CheckpointId} for thread {ThreadId} run {RunId} failed validation {ValidationRule}: {ValidationDetail}",
                request.CheckpointId,
                request.ThreadId,
                request.RunId,
                validation.Rule,
                validation.Detail
            );
        }

        return new CheckpointBuildResult
        {
            Checkpoint = checkpoint,
            Validation = validation,
            Usage = usage,
            LatencyMs = latencyMs,
            Reason = validation.Reason,
        };
    }

    /// <summary>
    ///     Shrinks what the chain carries until the envelope fits the V9 cap (<see cref="EnvelopeBudget" />), for the
    ///     summarised build and the fallback alike. Logs what shrank as counts, never text.
    /// </summary>
    private CompactionCheckpointMessage FitEnvelope(
        CompactionCheckpointMessage checkpoint,
        CheckpointBuildRequest request,
        CheckpointSummary summary,
        bool isFallback
    )
    {
        var validation = _options.Validation;

        // V7 is checked before V9 and says the summary model misbehaved: shrinking its narrative would hide that.
        if (validation.TextEstimator(checkpoint.Narrative) > validation.NarrativeTokenCap)
        {
            return checkpoint;
        }

        var bySeq = request.Rows.ToDictionary(r => r.Seq);
        long Measure(ContextManifest m, string n) =>
            validation.TextEstimator((checkpoint with { Manifest = m, Narrative = n }).RenderEnvelope(_options.Render));
        var (manifest, narrative, fit) = EnvelopeBudget.Fit(
            checkpoint.Manifest,
            checkpoint.Narrative,
            request.Previous?.Manifest,
            Measure,
            validation.CheckpointTokenCap,
            seq =>
                bySeq.GetValueOrDefault(seq) switch
                {
                    { IsHumanRow: true, Message: AgentMessage } => InstructionRank.Directive,
                    { IsHumanRow: true } => InstructionRank.User,
                    _ => InstructionRank.Other,
                },
            isFallback ? FallbackNarrative : null,
            seq => bySeq.GetValueOrDefault(seq)?.Text,
            keep => ManifestAssembler.BoundAgents(request.Rows, request.Cut.Seq, request.Roster, summary, keep)
        );
        if (fit.Shrunk)
        {
            _options.Logger.LogInformation(
                "Checkpoint {CheckpointId} for thread {ThreadId} run {RunId} shrank its envelope to the {CheckpointTokenCap}-token cap: {IndexEntriesCoalesced} index merges, {DecisionsDropped} decisions, {GoalsDropped} goals and {ArtifactsDropped} artifacts dropped, {AgentsTrimmed} agents, {NarrativeCharsTrimmed} narrative chars and {InstructionsTrimmed} instructions trimmed, {InstructionsDropped} instructions dropped; fits {EnvelopeFits}",
                request.CheckpointId,
                request.ThreadId,
                request.RunId,
                validation.CheckpointTokenCap,
                fit.IndexEntriesCoalesced,
                fit.DecisionsDropped,
                fit.GoalsDropped,
                fit.ArtifactsDropped,
                fit.AgentsTrimmed,
                fit.NarrativeCharsTrimmed,
                fit.InstructionsTrimmed,
                fit.InstructionsDropped,
                fit.Fits
            );
        }

        if (fit.InstructionsDropped > 0)
        {
            IReadOnlyList<long> Dropped(InstructionRank rank) => fit.DroppedInstructionSeqs.GetValueOrDefault(rank, []);
            _options.Logger.LogWarning(
                "Checkpoint {CheckpointId} for thread {ThreadId} run {RunId} dropped {InstructionsDropped} standing instructions to fit its {CheckpointTokenCap}-token envelope cap, recallable by seq: {DroppedOtherCount} other quotes at seqs {DroppedOtherSeqs}, {DroppedDirectiveCount} agent directives at seqs {DroppedDirectiveSeqs}, {DroppedUserCount} user instructions at seqs {DroppedUserSeqs}",
                request.CheckpointId,
                request.ThreadId,
                request.RunId,
                fit.InstructionsDropped,
                validation.CheckpointTokenCap,
                Dropped(InstructionRank.Other).Count,
                Dropped(InstructionRank.Other),
                Dropped(InstructionRank.Directive).Count,
                Dropped(InstructionRank.Directive),
                Dropped(InstructionRank.User).Count,
                Dropped(InstructionRank.User)
            );
        }

        if (!fit.Fits)
        {
            // Only what the budget may not shrink is left: the current instruction, the newest user instruction, what
            // the model wrote this build, agents at their markers and a one-entry index. V9 rejects the checkpoint next.
            _options.Logger.LogWarning(
                "Checkpoint {CheckpointId} for thread {ThreadId} run {RunId} is over its {CheckpointTokenCap}-token envelope cap with every carried section shrunk; tokens by section: {SectionTokens}",
                request.CheckpointId,
                request.ThreadId,
                request.RunId,
                validation.CheckpointTokenCap,
                EnvelopeBudget.SectionTokens(manifest, narrative, Measure)
            );
        }

        return checkpoint with
        {
            Manifest = manifest,
            Narrative = narrative,
        };
    }

    /// <summary>The whole §3.5 sequence against <paramref name="store" />.</summary>
    public async Task<CheckpointRunResult> RunAsync(
        IConversationStore store,
        CheckpointBuildRequest request,
        CancellationToken ct = default
    )
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(request);
        var threadId = request.ThreadId;
        var checkpointId = request.CheckpointId;

        var watermark = await store.GetMessageWatermarkAsync(threadId, ct).ConfigureAwait(false);
        var lastSeq = request.Rows.Count == 0 ? 0 : request.Rows[^1].Seq;
        if (watermark != lastSeq)
        {
            return new CheckpointRunResult
            {
                Outcome = CheckpointOutcome.Skipped,
                Reason = CompactionReasons.WatermarkDrift,
            };
        }

        _ = await CompactionStateProjection
            .PrepareAsync(
                store,
                threadId,
                checkpointId,
                request.Cut.Seq,
                watermark,
                request.Trigger,
                _clock.GetUtcNow(),
                ct
            )
            .ConfigureAwait(false);

        CheckpointBuildResult build;
        try
        {
            build = await BuildAsync(request, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // A cancelled pass must not leave a Prepared entry that looks in flight to the next one.
            _ = await CompactionStateProjection
                .RejectAsync(
                    store,
                    threadId,
                    checkpointId,
                    CheckpointReasons.Abandoned,
                    _clock.GetUtcNow(),
                    CancellationToken.None
                )
                .ConfigureAwait(false);
            throw;
        }

        if (!build.IsValid)
        {
            var reason = build.Reason ?? CompactionReasons.SummaryCallFailed;
            _ = await CompactionStateProjection
                .RejectAsync(store, threadId, checkpointId, reason, _clock.GetUtcNow(), ct)
                .ConfigureAwait(false);
            return Rejected(build, reason);
        }

        var checkpoint = build.Checkpoint!;
        _ = await CompactionStateProjection
            .MarkValidatedAsync(store, threadId, checkpointId, _clock.GetUtcNow(), checkpoint.Stats.SummaryFallback, ct)
            .ConfigureAwait(false);
        var committed = await CompactionStateProjection
            .TryCommitAsync(store, threadId, checkpointId, _clock.GetUtcNow(), ct)
            .ConfigureAwait(false);
        var entry = committed?.Find(checkpointId);
        if (entry?.Status != CheckpointStatus.Committed)
        {
            return Rejected(build, entry?.Reason ?? CheckpointReasons.StaleWatermark);
        }

        var persisted = MessagePersistenceConverter.ToPersistedMessage(checkpoint, threadId, request.RunId);
        try
        {
            await store.AppendMessagesAsync(threadId, [persisted], ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _ = await CompactionStateProjection
                .RejectAsync(store, threadId, checkpointId, CompactionReasons.PersistFailed, _clock.GetUtcNow(), ct)
                .ConfigureAwait(false);
            return Rejected(build, CompactionReasons.PersistFailed);
        }

        var appended = await store
            .LoadMessageRangeAsync(threadId, watermark + 1, long.MaxValue, limit: 1_000, ct)
            .ConfigureAwait(false);
        var rowSeq = appended.FirstOrDefault(r => string.Equals(r.Id, persisted.Id, StringComparison.Ordinal))?.Seq;
        if (rowSeq is null)
        {
            _ = await CompactionStateProjection
                .RejectAsync(store, threadId, checkpointId, CheckpointReasons.RowMissing, _clock.GetUtcNow(), ct)
                .ConfigureAwait(false);
            return Rejected(build, CheckpointReasons.RowMissing);
        }

        var activated = await CompactionStateProjection
            .ActivateAsync(store, threadId, checkpointId, rowSeq.Value, _clock.GetUtcNow(), ct)
            .ConfigureAwait(false);
        var final = activated?.Find(checkpointId);
        if (final?.Status != CheckpointStatus.Active)
        {
            return Rejected(build, final?.Reason ?? CheckpointReasons.StaleWatermark) with { RowSeq = rowSeq };
        }

        return new CheckpointRunResult
        {
            Outcome = CheckpointOutcome.Activated,
            Checkpoint = checkpoint,
            RowSeq = rowSeq,
            Usage = build.Usage,
            LatencyMs = build.LatencyMs,
            Validation = build.Validation,
        };
    }

    /// <summary>
    ///     The summary call with its timeout and retries: the first response carrying a summary, or null when
    ///     every attempt threw, timed out or came back empty. Only the caller's own cancellation propagates.
    /// </summary>
    private async Task<CheckpointSummaryResponse?> SummarizeAsync(
        CheckpointSummaryRequest summaryRequest,
        CheckpointBuildRequest request,
        CancellationToken ct
    )
    {
        var attempts = Math.Max(1, _options.SummaryAttempts);
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (_options.SummaryTimeout is { } timeout)
            {
                linked.CancelAfter(timeout);
            }

            try
            {
                var call = summarizer.SummarizeAsync(summaryRequest, linked.Token);
                // WaitAsync bounds a summarizer that ignores its token, too.
                var response = await (
                    _options.SummaryTimeout is { } limit ? call.WaitAsync(limit, ct) : call
                ).ConfigureAwait(false);
                if (response?.Summary is not null)
                {
                    return response;
                }

                _options.Logger.LogWarning(
                    "Checkpoint summary attempt {Attempt} of {Attempts} for thread {ThreadId} checkpoint {CheckpointId} returned no summary",
                    attempt,
                    attempts,
                    request.ThreadId,
                    request.CheckpointId
                );
            }
            catch (Exception ex)
                when (ex is TimeoutException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
            {
                _options.Logger.LogWarning(
                    "Checkpoint summary attempt {Attempt} of {Attempts} for thread {ThreadId} checkpoint {CheckpointId} timed out after {SummaryTimeoutMs} ms",
                    attempt,
                    attempts,
                    request.ThreadId,
                    request.CheckpointId,
                    _options.SummaryTimeout?.TotalMilliseconds
                );
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _options.Logger.LogWarning(
                    ex,
                    "Checkpoint summary attempt {Attempt} of {Attempts} for thread {ThreadId} checkpoint {CheckpointId} failed",
                    attempt,
                    attempts,
                    request.ThreadId,
                    request.CheckpointId
                );
            }
        }

        return null;
    }

    private static CheckpointRunResult Rejected(CheckpointBuildResult build, string reason) =>
        new()
        {
            Outcome = CheckpointOutcome.Rejected,
            Reason = reason,
            Checkpoint = build.Checkpoint,
            Usage = build.Usage,
            LatencyMs = build.LatencyMs,
            Validation = build.Validation,
        };

    /// <summary>
    ///     Gives the summary pass's usage the ids the usage projection keys on: the thread, the run, and a
    ///     generation id (<c>{checkpointId}:summary</c> when the provider minted none), so the attempt id
    ///     <c>{threadId}:{generationId}</c> is what <c>UsageRecordMapper</c> derives for the same message.
    /// </summary>
    private static UsageMessage? Stamp(UsageMessage? usage, CheckpointBuildRequest request) =>
        usage is null
            ? null
            : usage with
            {
                ThreadId = usage.ThreadId ?? request.ThreadId,
                RunId = usage.RunId ?? request.RunId,
                GenerationId = usage.GenerationId ?? $"{request.CheckpointId}:summary",
                FromAgent = usage.FromAgent ?? request.FromAgent,
            };
}
