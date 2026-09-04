using System.Security.Cryptography;
using CodeReviewDaemon.Sample.Persistence;
using CodeReviewDaemon.Sample.Persistence.Models;

namespace CodeReviewDaemon.Sample.Orchestration;

internal enum MergedCloseSubstep
{
    EvidenceFrozen,
    CandidatesBuilt,
    Classified,
    Verified,
    KnowledgePromoted,
    DeveloperLearningPromoted,
    ReportsWritten,
    Archived,
}

internal enum MergedCloseSubstepOutcome
{
    Completed,
    Retry,
}

/// <summary>
/// The durable artifact one substep produced. It is the checkpoint's content and hash, so a substep
/// that did no work has nothing to write and therefore cannot be checkpointed.
/// </summary>
internal sealed class MergedCloseSubstepReceipt
{
    public MergedCloseSubstepReceipt(ReadOnlyMemory<byte> content)
    {
        if (content.IsEmpty)
        {
            throw new ArgumentException(
                "A merged-close receipt must carry the artifact the substep produced.",
                nameof(content)
            );
        }

        Content = content;
        ContentSha256 = Convert.ToHexString(SHA256.HashData(content.Span)).ToLowerInvariant();
    }

    public ReadOnlyMemory<byte> Content { get; }

    public string ContentSha256 { get; }
}

/// <summary>
/// How one substep ended. <see cref="Completed"/> demands the artifact, so "it worked" and "here is
/// what it produced" cannot come apart.
/// </summary>
internal readonly record struct MergedCloseSubstepResult(
    MergedCloseSubstepOutcome Outcome,
    MergedCloseSubstepReceipt? Receipt
)
{
    /// <summary>The substep did not finish. Nothing is checkpointed and the round retries.</summary>
    public static MergedCloseSubstepResult Retry => new(MergedCloseSubstepOutcome.Retry, null);

    public static MergedCloseSubstepResult Completed(MergedCloseSubstepReceipt receipt) =>
        new(MergedCloseSubstepOutcome.Completed, receipt ?? throw new ArgumentNullException(nameof(receipt)));
}

/// <summary>One rendered report file and the hash the close round recorded for it (spec §9.6).</summary>
internal sealed record MergedCloseReportReceipt(string Path, string ContentSha256);

internal delegate Task<MergedCloseSubstepResult> MergedCloseSubstepHandler(
    EngagementRound round,
    MergedCloseSubstep substep,
    CancellationToken cancellationToken
);

/// <summary>Runs merged-close work in order and checkpoints each completed substep durably.</summary>
internal sealed class DisabledMergedCloseRoundExecutor : IEngagementRoundExecutor
{
    public EngagementRoundIntent Intent => EngagementRoundIntent.MergedClose;

    public bool IsAvailable => false;

    public Task<EngagementRoundStatus> ExecuteAsync(EngagementRound round, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(round);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(EngagementRoundStatus.RetryPending);
    }
}

/// <summary>
/// Decides which merged-close substeps are really done and whether the round may finalize. A substep
/// counts as done only when its durable receipt still holds — the record is complete, its bytes still
/// hash to what was recorded, and whatever it claims to have produced is still there. Presence of a
/// checkpoint alone proves nothing, so a placeholder success cannot advance the chain, and a report
/// deleted after it was written is regenerated rather than assumed (spec §11.4).
/// </summary>
internal sealed class MergedCloseReceiptLedger(ReviewStore store)
{
    private readonly ReviewStore _store = store ?? throw new ArgumentNullException(nameof(store));

    internal static string RecordId(long closeRoundId, MergedCloseSubstep substep) =>
        $"merged-close:{closeRoundId}:{substep}";

    /// <summary>The substeps whose durable receipt currently holds, and which may therefore be skipped.</summary>
    public IReadOnlySet<MergedCloseSubstep> SatisfiedSubsteps(EngagementRound round)
    {
        ArgumentNullException.ThrowIfNull(round);
        return Enum.GetValues<MergedCloseSubstep>().Where(substep => ReceiptHolds(round, substep)).ToHashSet();
    }

    /// <summary>
    /// The required outcomes spec §9.6 demands before a merged close may archive, expressed as stable
    /// reason codes. An empty list is the only thing that unlocks archiving.
    /// </summary>
    public IReadOnlyList<string> MissingFinalizationOutcomes(EngagementRound round)
    {
        ArgumentNullException.ThrowIfNull(round);

        var missing = new List<string>();
        foreach (var substep in Enum.GetValues<MergedCloseSubstep>().Where(step => step != MergedCloseSubstep.Archived))
        {
            if (!ReceiptHolds(round, substep))
            {
                missing.Add($"{MergedCloseFinalizationReasons.SubstepIncomplete}:{substep}");
            }
        }

        // Every frozen candidate must have a persisted outcome row: an item the analyst never reached
        // still has to appear in the report as Indeterminate rather than vanish from the denominator.
        var candidates = ReadPayload<string[]>(RecordId(round.Id, MergedCloseSubstep.CandidatesBuilt));
        var labelled = _store.ListCloseOutcomeItems(round.Id).Select(item => item.CandidateId);
        if (candidates is null || !labelled.ToHashSet(StringComparer.Ordinal).SetEquals(candidates))
        {
            missing.Add(MergedCloseFinalizationReasons.OutcomeRowsIncomplete);
        }

        // A promotion that was declined or failed is still a required receipt; only silence is missing.
        var promoted = _store
            .ListPromotionOutcomes(round.Id)
            .Select(outcome => outcome.DestinationKind)
            .ToHashSet(StringComparer.Ordinal);
        foreach (
            var destination in new[] { PromotionDestinations.KnowledgeBase, PromotionDestinations.DeveloperLearnings }
        )
        {
            if (!promoted.Contains(destination))
            {
                missing.Add($"{MergedCloseFinalizationReasons.PromotionOutcomeMissing}:{destination}");
            }
        }

        return missing;
    }

    private bool ReceiptHolds(EngagementRound round, MergedCloseSubstep substep)
    {
        var recordId = RecordId(round.Id, substep);
        var record = _store.GetAuditRecord(recordId);
        if (record?.CompletedAtUtc is null || record.ByteCount == 0)
        {
            return false;
        }

        // The store is the integrity authority: ReadAuditContent re-hashes a record's bytes against the
        // hash recorded with it and refuses to return content that no longer matches. Re-checking that
        // here would duplicate it; letting it surface means a damaged checkpoint faults the round into
        // the governed-failure budget and eventually visible parked work (spec §11.4) instead of being
        // silently accepted as a completed substep.
        var content = _store.ReadAuditContent(recordId);
        if (content.Length == 0)
        {
            return false;
        }

        return substep switch
        {
            MergedCloseSubstep.EvidenceFrozen => MergedCloseEvidenceFreezer.TryDeserialize(content)?.CloseRoundId
                == round.Id,
            MergedCloseSubstep.ReportsWritten => ReportsStillMatchTheirHashes(content),
            _ => true,
        };
    }

    /// <summary>Re-reads each rendered report and compares it to the hash the round recorded for it.</summary>
    private static bool ReportsStillMatchTheirHashes(byte[] content)
    {
        var reports = MergedCloseReceiptPayload.TryDeserialize<MergedCloseReportReceipt[]>(content);
        return reports is { Length: > 0 }
            && reports.All(report =>
                File.Exists(report.Path)
                && StringComparer.Ordinal.Equals(
                    Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(report.Path))).ToLowerInvariant(),
                    report.ContentSha256
                )
            );
    }

    private T? ReadPayload<T>(string recordId)
        where T : class =>
        _store.GetAuditRecord(recordId) is null
            ? null
            : MergedCloseReceiptPayload.TryDeserialize<T>(_store.ReadAuditContent(recordId));
}

/// <summary>Stable reason codes for a merged close that may not finalize yet.</summary>
internal static class MergedCloseFinalizationReasons
{
    public const string SubstepIncomplete = "substep_incomplete";
    public const string OutcomeRowsIncomplete = "close_outcome_rows_incomplete";
    public const string PromotionOutcomeMissing = "promotion_outcome_missing";
}

internal sealed class MergedCloseRoundExecutor : IEngagementRoundExecutor
{
    internal const string SubstepRecordType = "merged_close_substep";

    private readonly ReviewStore _store;
    private readonly MergedCloseReceiptLedger _ledger;
    private readonly MergedCloseSubstepHandler _executeSubstepAsync;
    private readonly TimeProvider _timeProvider;

    public MergedCloseRoundExecutor(
        ReviewStore store,
        MergedCloseSubstepHandler executeSubstepAsync,
        TimeProvider timeProvider
    )
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _ledger = new MergedCloseReceiptLedger(_store);
        _executeSubstepAsync = executeSubstepAsync ?? throw new ArgumentNullException(nameof(executeSubstepAsync));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public EngagementRoundIntent Intent => EngagementRoundIntent.MergedClose;

    public async Task<EngagementRoundStatus> ExecuteAsync(EngagementRound round, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(round);
        if (round.Intent != Intent)
        {
            throw new ArgumentException("A merged-close executor requires a MergedClose round.", nameof(round));
        }

        var engagement =
            _store.GetEngagement(round.PrEngagementId)
            ?? throw new InvalidOperationException($"Engagement {round.PrEngagementId} was not found.");

        // Spec §10: a pull request that closed without merging writes no outcome report and promotes
        // nothing. Refusing here is a non-failure disposition, so it neither runs a substep nor spends
        // the round's retry budget, and the immutable evidence it already holds is left untouched.
        if (engagement.Lifecycle != PrLifecycleState.Merged)
        {
            return EngagementRoundStatus.Superseded;
        }

        var satisfied = _ledger.SatisfiedSubsteps(round);
        foreach (var substep in Enum.GetValues<MergedCloseSubstep>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (satisfied.Contains(substep))
            {
                continue;
            }

            // Archiving merges and deletes the notes branch, so it runs only once every required
            // output of spec §9.6 actually exists. Nothing is checkpointed while anything is missing,
            // which keeps this check retryable instead of deadlocking the round.
            if (substep == MergedCloseSubstep.Archived && _ledger.MissingFinalizationOutcomes(round) is { Count: > 0 })
            {
                return EngagementRoundStatus.RetryPending;
            }

            var result = await _executeSubstepAsync(round, substep, cancellationToken).ConfigureAwait(false);

            // A substep that reports success without an artifact has produced no evidence that it ran.
            // Treat it exactly like a retry: never write a checkpoint that later substeps would trust.
            if (result.Outcome != MergedCloseSubstepOutcome.Completed || result.Receipt is null)
            {
                return EngagementRoundStatus.RetryPending;
            }

            RecordCompletedSubstep(round, substep, result.Receipt);
        }

        return EngagementRoundStatus.Completed;
    }

    private void RecordCompletedSubstep(
        EngagementRound round,
        MergedCloseSubstep substep,
        MergedCloseSubstepReceipt receipt
    )
    {
        var recordId = MergedCloseReceiptLedger.RecordId(round.Id, substep);
        var capturedAt = _store.GetAuditRecord(recordId)?.CapturedAtUtc ?? _timeProvider.GetUtcNow();
        _ = _store.StoreAuditRecord(
            new AchieveAi.LmDotnetTools.LmMultiTurn.Audit.ModelTurnAuditRecord(
                recordId,
                new AchieveAi.LmDotnetTools.LmMultiTurn.Audit.MultiTurnAuditScope(
                    round.PrEngagementId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    round.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)
                ),
                $"merged-close:{round.Id}",
                round.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                substep.ToString(),
                null,
                (long)substep,
                SubstepRecordType,
                "system",
                null,
                null,
                receipt.Content,
                receipt.ContentSha256,
                receipt.Content.Length,
                AchieveAi.LmDotnetTools.LmMultiTurn.Audit.AuditCaptureOutcome.Complete,
                null,
                capturedAt
            )
        );
    }
}
