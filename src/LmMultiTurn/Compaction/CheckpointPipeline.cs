using System.Diagnostics;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Models;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;

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
    private readonly CheckpointPipelineOptions _options = options ?? new CheckpointPipelineOptions();
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>Summarize, assemble and validate; nothing is written.</summary>
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
        };

        var stopwatch = Stopwatch.StartNew();
        CheckpointSummaryResponse response;
        try
        {
            response = await summarizer.SummarizeAsync(summaryRequest, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new CheckpointBuildResult
            {
                LatencyMs = stopwatch.ElapsedMilliseconds,
                Reason = CompactionReasons.SummaryCallFailed,
            };
        }

        stopwatch.Stop();
        if (response?.Summary is null)
        {
            return new CheckpointBuildResult
            {
                LatencyMs = stopwatch.ElapsedMilliseconds,
                Reason = CompactionReasons.SummaryCallFailed,
            };
        }

        var usage = Stamp(response.Usage, request);
        var manifest = ManifestAssembler.Assemble(
            rows,
            cut,
            request.Previous?.Manifest,
            previousBoundary,
            response.Summary,
            request.Board,
            request.Roster,
            _options.Assembler
        );

        var boundaryRow = rows.FirstOrDefault(r => r.Seq == cut.Seq);
        var before = rows.Where(r => r.Seq <= cut.Seq && !r.IsCheckpointRow).Sum(r => _options.Estimator(r.Message));
        var checkpoint = new CompactionCheckpointMessage
        {
            CheckpointId = request.CheckpointId,
            Boundary = new CheckpointBoundary { Seq = cut.Seq, MessageId = boundaryRow?.MessageId ?? string.Empty },
            SupersedesCheckpointId = request.Previous?.CheckpointId,
            Trigger = request.Trigger,
            Manifest = manifest,
            Narrative = response.Summary.Narrative,
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
                SummaryLatencyMs = stopwatch.ElapsedMilliseconds,
            },
        };
        checkpoint = checkpoint with
        {
            Stats = checkpoint.Stats with
            {
                EstimatedTokensAfter = CompactionTokenEstimate.EstimateText(checkpoint.RenderEnvelope(_options.Render)),
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

        return new CheckpointBuildResult
        {
            Checkpoint = checkpoint,
            Validation = validation,
            Usage = usage,
            LatencyMs = stopwatch.ElapsedMilliseconds,
            Reason = validation.Reason,
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

        var build = await BuildAsync(request, ct).ConfigureAwait(false);
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
            .MarkValidatedAsync(store, threadId, checkpointId, _clock.GetUtcNow(), ct)
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
