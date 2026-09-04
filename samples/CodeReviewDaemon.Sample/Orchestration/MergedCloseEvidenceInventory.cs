using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodeReviewDaemon.Sample.Persistence;
using CodeReviewDaemon.Sample.Persistence.Models;

namespace CodeReviewDaemon.Sample.Orchestration;

/// <summary>
/// One promotion destination exactly as it stood when close evidence was frozen (spec §9.1). The hash
/// pins the destination's content, so a promotion pass can later prove it appended to the state the
/// close round actually reasoned over.
/// </summary>
internal sealed record MergedClosePromotionDestination(string DestinationKind, string Path, string ContentSha256);

/// <summary>One frozen clarification question with the provider receipt that proves it was asked.</summary>
internal sealed record MergedCloseQuestionRecord(
    string Id,
    long EngagementRoundId,
    string State,
    string? ProviderReceiptSha256,
    IReadOnlyList<string> CandidateAnswerIds
);

/// <summary>One frozen typed action with the provider receipt that proves the provider accepted it.</summary>
internal sealed record MergedCloseActionRecord(
    string ActionId,
    long EngagementRoundId,
    string Kind,
    string Status,
    string? ProviderReceiptSha256
);

/// <summary>
/// The parts of §9.1 evidence the review store does not own: the merge commit and final diff, the
/// final code state, the complete provider discussion, and the current promotion destinations. A
/// reader returns this already frozen — the freezer never reaches a provider or a working tree itself.
/// </summary>
internal sealed record MergedCloseProviderEvidence(
    string MergeCommitSha,
    string FinalDiffSha256,
    string FinalCodeTreeId,
    IReadOnlyList<MergedCloseDiscussionEntry> Discussion,
    IReadOnlyList<MergedClosePromotionDestination> Destinations
);

/// <summary>
/// Reads the provider-side evidence for one merged close. Returns <c>null</c> when any required part
/// is unavailable: an incomplete freeze must retry, never checkpoint a partial inventory.
/// </summary>
internal delegate Task<MergedCloseProviderEvidence?> MergedCloseProviderEvidenceReader(
    PrEngagement engagement,
    RepoIdentity repo,
    EngagementRound round,
    CancellationToken cancellationToken
);

/// <summary>
/// Everything spec §9.1 freezes at merge, as one deterministic manifest. It is the merged-close round's
/// first durable artifact: the analyst and the verifier may only reason over what appears here, and a
/// restart re-reads it rather than re-freezing a moved target.
/// </summary>
internal sealed record MergedCloseEvidenceInventory(
    int SchemaVersion,
    string Provider,
    string RepoKey,
    string PrId,
    long CloseRoundId,
    string MergeCommitSha,
    string FinalDiffSha256,
    string FinalCodeTreeId,
    DateTimeOffset MergedAtUtc,
    IReadOnlyList<long> EngagementRoundIds,
    IReadOnlyList<AuditSourceReference> SourceRecords,
    IReadOnlyList<MergedCloseQuestionRecord> Questions,
    IReadOnlyList<MergedCloseActionRecord> Actions,
    IReadOnlyList<MergedCloseDiscussionEntry> Discussion,
    IReadOnlyList<MergedClosePromotionDestination> Destinations
)
{
    /// <summary>Bumped whenever the frozen shape changes, so an old manifest is recognisably stale.</summary>
    public const int CurrentSchemaVersion = 1;
}

/// <summary>
/// Serializes the payloads merged-close receipts carry. One place owns the options, so a manifest
/// written by one substep is readable by the ledger that later re-validates it.
/// </summary>
internal static class MergedCloseReceiptPayload
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = false };

    public static byte[] Serialize<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, SerializerOptions);

    /// <summary>Reads a payload back, or <c>null</c> when the bytes are not one.</summary>
    public static T? TryDeserialize<T>(ReadOnlySpan<byte> content)
        where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(content, SerializerOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// Freezes merged-close evidence (spec §9.1) from typed rows plus one already-frozen provider read.
/// Every collection is ordinal-ordered, so re-freezing unchanged rows produces byte-identical content
/// and the persisted manifest is stable across a restart.
/// </summary>
internal static class MergedCloseEvidenceFreezer
{
    public static MergedCloseEvidenceInventory Freeze(
        ReviewStore store,
        PrEngagement engagement,
        RepoIdentity repo,
        EngagementRound closeRound,
        MergedCloseProviderEvidence provider
    )
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(engagement);
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentNullException.ThrowIfNull(closeRound);
        ArgumentNullException.ThrowIfNull(provider);

        var rounds = store.ListEngagementRounds(engagement.Id).Select(round => round.Id).Order().ToArray();
        var sources = new List<AuditSourceReference>();
        var questions = new List<MergedCloseQuestionRecord>();
        var actions = new List<MergedCloseActionRecord>();
        foreach (var roundId in rounds)
        {
            sources.AddRange(
                store
                    .ListAuditRecordsForRound(roundId)
                    // The close round's own substep checkpoints are process bookkeeping written AFTER this
                    // freeze, not review evidence. Including them would make the manifest self-referential:
                    // a freeze retried after a partial write would see its own record and produce different
                    // bytes, which the content-addressed store rejects as a conflicting replay.
                    .Where(record =>
                        !StringComparer.Ordinal.Equals(record.RecordType, MergedCloseRoundExecutor.SubstepRecordType)
                    )
                    .Select(record => new AuditSourceReference(record.Id, record.ContentSha256))
            );
            questions.AddRange(
                store
                    .ListAskedClarificationQuestions(roundId)
                    .Select(question => new MergedCloseQuestionRecord(
                        question.Id,
                        question.EngagementRoundId,
                        question.State.ToString(),
                        Sha256OrNull(question.ProviderReceiptJson),
                        [
                            .. store
                                .ListClarificationCandidateAnswers(question.Id)
                                .Select(answer => answer.Id)
                                .Order(StringComparer.Ordinal),
                        ]
                    ))
            );
            actions.AddRange(
                store
                    .ListReviewActions(roundId)
                    .Select(action => new MergedCloseActionRecord(
                        action.ActionId,
                        action.EngagementRoundId,
                        action.Kind.ToString(),
                        action.Status.ToString(),
                        Sha256OrNull(action.ProviderReceiptJson)
                    ))
            );
        }

        return new MergedCloseEvidenceInventory(
            MergedCloseEvidenceInventory.CurrentSchemaVersion,
            engagement.Provider,
            repo.NormalizedKey,
            engagement.PrId,
            closeRound.Id,
            provider.MergeCommitSha,
            provider.FinalDiffSha256,
            provider.FinalCodeTreeId,
            // The merge boundary the round was admitted on, not "now": a restart must freeze the same
            // instant, and the round's upper bound is written once at admission.
            (closeRound.ActivityUpperBound ?? engagement.LatestActivity).PublishedAt,
            rounds,
            [
                .. sources
                    .DistinctBy(source => source.SourceRecordId, StringComparer.Ordinal)
                    .OrderBy(source => source.SourceRecordId, StringComparer.Ordinal),
            ],
            [.. questions.OrderBy(question => question.Id, StringComparer.Ordinal)],
            [
                .. actions.OrderBy(
                    action => $"{action.EngagementRoundId.ToString(CultureInfo.InvariantCulture)}:{action.ActionId}",
                    StringComparer.Ordinal
                ),
            ],
            [.. provider.Discussion.OrderBy(entry => entry.Id, StringComparer.Ordinal)],
            [.. provider.Destinations.OrderBy(destination => destination.DestinationKind, StringComparer.Ordinal)]
        );
    }

    public static byte[] Serialize(MergedCloseEvidenceInventory inventory)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        return MergedCloseReceiptPayload.Serialize(inventory);
    }

    /// <summary>Reads a persisted manifest back, or <c>null</c> when the bytes are not a current one.</summary>
    public static MergedCloseEvidenceInventory? TryDeserialize(ReadOnlySpan<byte> content)
    {
        var inventory = MergedCloseReceiptPayload.TryDeserialize<MergedCloseEvidenceInventory>(content);
        return inventory?.SchemaVersion == MergedCloseEvidenceInventory.CurrentSchemaVersion ? inventory : null;
    }

    private static string? Sha256OrNull(string? content) =>
        content is null
            ? null
            : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
}
