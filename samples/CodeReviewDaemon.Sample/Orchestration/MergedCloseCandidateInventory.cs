using CodeReviewDaemon.Sample.Persistence;
using CodeReviewDaemon.Sample.Persistence.Models;

namespace CodeReviewDaemon.Sample.Orchestration;

/// <summary>
/// One deterministic source item in the merged-close denominator. Identity and evidence come from a
/// persisted row, never from a model, so the same engagement always yields the same candidate set.
/// </summary>
internal sealed record CloseOutcomeCandidate(
    string Id,
    CloseCandidateKind Kind,
    long EngagementRoundId,
    string Summary,
    DateTimeOffset ObservedAtUtc,
    IReadOnlyList<AuditSourceReference> Sources
)
{
    /// <summary>Value equality over the evidence list, so rebuild comparisons are meaningful.</summary>
    public bool Equals(CloseOutcomeCandidate? other) =>
        other is not null
        && StringComparer.Ordinal.Equals(Id, other.Id)
        && Kind == other.Kind
        && EngagementRoundId == other.EngagementRoundId
        && StringComparer.Ordinal.Equals(Summary, other.Summary)
        && ObservedAtUtc == other.ObservedAtUtc
        && Sources.SequenceEqual(other.Sources);

    public override int GetHashCode() => HashCode.Combine(Id, Kind, EngagementRoundId, Summary, ObservedAtUtc);
}

/// <summary>
/// Builds the merged-close denominator from typed evidence rows (spec §9.2). A close analyst may only
/// label the items this inventory produces: it cannot add an item that no row supports, and it cannot
/// drop one that a row requires.
/// </summary>
internal static class MergedCloseCandidateInventory
{
    /// <summary>
    /// Enumerates every candidate for the engagement, ordinal-ordered by candidate ID so the
    /// denominator — and everything derived from it — is reproducible.
    /// </summary>
    public static IReadOnlyList<CloseOutcomeCandidate> Build(ReviewStore store, long engagementId)
    {
        ArgumentNullException.ThrowIfNull(store);

        var candidates = new List<CloseOutcomeCandidate>();
        foreach (var round in store.ListEngagementRounds(engagementId))
        {
            candidates.AddRange(BuildForRound(store, round.Id));
        }

        candidates.Sort(static (left, right) => string.CompareOrdinal(left.Id, right.Id));
        return candidates;
    }

    private static IEnumerable<CloseOutcomeCandidate> BuildForRound(ReviewStore store, long roundId)
    {
        foreach (var question in store.ListAskedClarificationQuestions(roundId))
        {
            yield return new CloseOutcomeCandidate(
                $"question:{question.Id}",
                CloseCandidateKind.Question,
                roundId,
                question.Wording,
                question.AskedAtUtc ?? question.CreatedAtUtc,
                store.ListClarificationQuestionSources(question.Id)
            );
        }

        foreach (var observation in store.ListRoundObservations(roundId))
        {
            if (ClassifyObservation(observation.Kind) is not { } kind)
            {
                continue;
            }

            yield return new CloseOutcomeCandidate(
                $"observation:{observation.Id}",
                kind,
                roundId,
                observation.Summary,
                observation.ObservedAtUtc,
                store.ListRoundObservationSources(observation.Id)
            );
        }

        foreach (var action in store.ListReviewActions(roundId))
        {
            // Only a provider-accepted action carries a thread disposition worth measuring. Planned,
            // collected-only, and rejected actions never reached the discussion.
            if (action.Status != ReviewActionStatus.Accepted)
            {
                continue;
            }

            yield return new CloseOutcomeCandidate(
                $"action:{action.ActionId}",
                CloseCandidateKind.ProviderAction,
                roundId,
                action.Kind.ToString(),
                action.UpdatedAtUtc,
                store.ListReviewActionSources(roundId, action.ActionId)
            );
        }
    }

    /// <summary>
    /// Maps an observation kind to its outcome family. Context claims, deliberate no-actions, judge
    /// results, and capture gaps describe how the review ran rather than what it contributed, so they
    /// are not outcome candidates.
    /// </summary>
    private static CloseCandidateKind? ClassifyObservation(ObservationKind kind) =>
        kind switch
        {
            ObservationKind.Finding => CloseCandidateKind.Finding,
            ObservationKind.Question => CloseCandidateKind.Question,
            ObservationKind.Answer or ObservationKind.Correction or ObservationKind.DiscussionContribution =>
                CloseCandidateKind.SubstantiveContribution,
            _ => null,
        };
}
