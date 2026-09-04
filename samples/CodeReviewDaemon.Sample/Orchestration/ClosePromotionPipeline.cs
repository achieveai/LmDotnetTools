using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CodeReviewDaemon.Sample.Configuration;
using CodeReviewDaemon.Sample.Persistence;
using CodeReviewDaemon.Sample.Persistence.Models;

namespace CodeReviewDaemon.Sample.Orchestration;

/// <summary>Stable reason codes for promotion passes that did not write.</summary>
internal static class PromotionReasons
{
    public const string FeatureDisabled = "feature_disabled";
    public const string NothingConfirmed = "nothing_confirmed";
    public const string BlockedByEarlierPass = "blocked_by_earlier_pass";
}

/// <summary>One promotion the pipeline asks a destination to perform.</summary>
internal sealed record PromotionRequest(
    string SourceObservationId,
    string DestinationKind,
    string Summary,
    IReadOnlyList<AuditSourceReference> Sources
);

/// <summary>A destination writer. It reports what it did; it never decides whether it should run.</summary>
internal delegate Task<PromotionOutcome> ClosePromotionPass(
    PromotionRequest request,
    CancellationToken cancellationToken
);

/// <summary>Reads promotion results the way close finalization must interpret them.</summary>
internal static class MergedClosePromotionStatus
{
    /// <summary>
    /// True when any pass failed. A failure keeps close visibly pending and retryable even when the
    /// other pass wrote, so no failed pass is dropped merely because its sibling succeeded.
    /// </summary>
    public static bool HasBlockingFailure(IReadOnlyList<PromotionOutcome> outcomes)
    {
        ArgumentNullException.ThrowIfNull(outcomes);
        return outcomes.Any(outcome => outcome.Disposition == PromotionDisposition.Failed);
    }
}

/// <summary>
/// Runs the ordered close promotion passes (spec §9.5): confirmed general knowledge first, then
/// append-only developer learning. Every pass records a durable outcome — written, declined, or failed
/// — and an identical replay reuses the outcome it already wrote instead of contributing twice.
/// </summary>
internal sealed class ClosePromotionPipeline(
    ReviewStore store,
    CodeReviewDaemonOptions options,
    ClosePromotionPass promoteKnowledge,
    ClosePromotionPass promoteDeveloperLearning
)
{
    private static readonly string[] DestinationsInOrder =
    [
        PromotionDestinations.KnowledgeBase,
        PromotionDestinations.DeveloperLearnings,
    ];

    /// <summary>
    /// Derives the promotion identity from provider/repository/PR/round/candidate identity plus the
    /// ordinal-sorted source hashes. Two different source observations with identical prose stay two
    /// traceable contributions because their sources differ.
    /// </summary>
    public static string BuildSourceObservationId(PrEngagement engagement, long roundId, VerifiedCloseLabel verified)
    {
        ArgumentNullException.ThrowIfNull(engagement);
        ArgumentNullException.ThrowIfNull(verified);

        var builder = new StringBuilder()
            .Append(engagement.Provider)
            .Append('|')
            .Append(engagement.RepoId.ToString(CultureInfo.InvariantCulture))
            .Append('|')
            .Append(engagement.PrId)
            .Append('|')
            .Append(roundId.ToString(CultureInfo.InvariantCulture))
            .Append('|')
            .Append(verified.CandidateId);
        foreach (var source in verified.Evidence.OrderBy(reference => reference.SourceRecordId, StringComparer.Ordinal))
        {
            _ = builder.Append('|').Append(source.SourceRecordId).Append(':').Append(source.ContentSha256);
        }

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return $"close-obs:{Convert.ToHexString(digest).ToLowerInvariant()[..32]}";
    }

    public async Task<IReadOnlyList<PromotionOutcome>> RunAsync(
        PrEngagement engagement,
        long closeRoundId,
        IReadOnlyList<VerifiedCloseLabel> verified,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(engagement);
        ArgumentNullException.ThrowIfNull(verified);

        // Only what the verifier confirmed is eligible; an indeterminate item is never promoted.
        var confirmed = verified
            .Where(label => label.Label != CloseOutcomeLabel.Indeterminate)
            .OrderBy(label => label.CandidateId, StringComparer.Ordinal)
            .ToList();

        var alreadyWritten = store
            .ListPromotionOutcomes(closeRoundId)
            .Where(outcome => outcome.Disposition == PromotionDisposition.Written)
            .ToDictionary(outcome => Key(outcome.SourceObservationId, outcome.DestinationKind), StringComparer.Ordinal);

        var outcomes = new List<PromotionOutcome>();
        var earlierPassFailed = false;
        foreach (var destination in DestinationsInOrder)
        {
            var destinationOutcomes = await RunDestinationAsync(
                    engagement,
                    closeRoundId,
                    confirmed,
                    destination,
                    earlierPassFailed,
                    alreadyWritten,
                    cancellationToken
                )
                .ConfigureAwait(false);

            // The next destination only starts once this one has completed or explicitly declined.
            earlierPassFailed =
                earlierPassFailed || destinationOutcomes.Any(o => o.Disposition == PromotionDisposition.Failed);
            outcomes.AddRange(destinationOutcomes);
        }

        store.SavePromotionOutcomes(closeRoundId, outcomes);
        return outcomes;
    }

    private async Task<IReadOnlyList<PromotionOutcome>> RunDestinationAsync(
        PrEngagement engagement,
        long closeRoundId,
        IReadOnlyList<VerifiedCloseLabel> confirmed,
        string destination,
        bool earlierPassFailed,
        IReadOnlyDictionary<string, PromotionOutcome> alreadyWritten,
        CancellationToken cancellationToken
    )
    {
        if (confirmed.Count == 0)
        {
            return [Declined(ScopeId(engagement, closeRoundId), destination, PromotionReasons.NothingConfirmed)];
        }

        var outcomes = new List<PromotionOutcome>();
        foreach (var label in confirmed)
        {
            var sourceObservationId = BuildSourceObservationId(engagement, closeRoundId, label);
            if (!options.EnableMergedLearningPromotion)
            {
                outcomes.Add(Declined(sourceObservationId, destination, PromotionReasons.FeatureDisabled));
                continue;
            }

            if (earlierPassFailed)
            {
                outcomes.Add(
                    new PromotionOutcome(
                        sourceObservationId,
                        destination,
                        PromotionDisposition.Failed,
                        null,
                        null,
                        PromotionReasons.BlockedByEarlierPass
                    )
                );
                continue;
            }

            // Idempotent by source observation: a destination that already accepted this exact
            // observation is not asked again, so replay cannot duplicate the contribution.
            if (alreadyWritten.TryGetValue(Key(sourceObservationId, destination), out var previous))
            {
                outcomes.Add(previous);
                continue;
            }

            var pass = destination == PromotionDestinations.KnowledgeBase ? promoteKnowledge : promoteDeveloperLearning;
            outcomes.Add(
                await pass(
                        new PromotionRequest(sourceObservationId, destination, label.CandidateId, label.Evidence),
                        cancellationToken
                    )
                    .ConfigureAwait(false)
            );
        }

        return outcomes;
    }

    private static PromotionOutcome Declined(string sourceObservationId, string destination, string reason) =>
        new(sourceObservationId, destination, PromotionDisposition.Declined, null, null, reason);

    /// <summary>Identity for an outcome that has no confirmed source observation behind it.</summary>
    private static string ScopeId(PrEngagement engagement, long closeRoundId) =>
        $"close-scope:{engagement.Provider}:{engagement.RepoId.ToString(CultureInfo.InvariantCulture)}:{engagement.PrId}:{closeRoundId.ToString(CultureInfo.InvariantCulture)}";

    private static string Key(string sourceObservationId, string destination) => $"{sourceObservationId} {destination}";
}
