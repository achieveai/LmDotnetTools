using CodeReviewDaemon.Sample.Persistence;
using CodeReviewDaemon.Sample.Persistence.Models;

namespace CodeReviewDaemon.Sample.Orchestration;

/// <summary>Stable reason codes explaining why a label was or was not confirmed.</summary>
internal static class CloseVerificationReasons
{
    public const string VerifiedAgainstSources = "verified_against_sources";
    public const string VerifiedAgainstFinalCode = "verified_against_final_code";
    public const string VerifiedAgainstDiscussion = "verified_against_discussion";
    public const string NoProposal = "no_proposal";
    public const string UnknownLabel = "unknown_label";
    public const string UnsupportedCitation = "unsupported_citation";
    public const string MissingFinalCode = "missing_final_code";
    public const string ContradictedByFinalCode = "contradicted_by_final_code";
    public const string ContradictedByPriorDiscussion = "contradicted_by_prior_discussion";
    public const string MissingSemanticSubject = "missing_semantic_subject";
}

/// <summary>A close analyst's semantic claim about one candidate. It is a proposal, never a result.</summary>
internal sealed record ProposedCloseLabel(
    string CandidateId,
    string Label,
    string? FilePath,
    string? Subject,
    IReadOnlyList<AuditSourceReference> Evidence
);

/// <summary>The verifier's independently re-derived result for one candidate.</summary>
internal sealed record VerifiedCloseLabel(
    string CandidateId,
    CloseOutcomeLabel Label,
    IReadOnlyList<AuditSourceReference> Evidence,
    string VerificationReasonCode
);

/// <summary>One provider discussion entry exactly as it stood when close evidence was frozen.</summary>
internal sealed record MergedCloseDiscussionEntry(string Id, string Author, DateTimeOffset PostedAtUtc, string Body);

/// <summary>The frozen merged code the verifier reads to decide whether a finding was addressed.</summary>
internal sealed record MergedCloseFinalCode(string MergeCommitSha, IReadOnlyDictionary<string, string> FilesByPath);

/// <summary>Everything frozen at merge that the analyst and verifier are allowed to reason over.</summary>
internal sealed record MergedCloseEvidence(
    PrEngagement Engagement,
    IReadOnlyList<CloseOutcomeCandidate> Candidates,
    IReadOnlyList<MergedCloseDiscussionEntry> Discussion,
    MergedCloseFinalCode FinalCode,
    DateTimeOffset MergedAtUtc
);

/// <summary>Aggregate measures recomputed from verified rows, never carried over from a render.</summary>
internal sealed record MergedCloseCounts(int Total, int Definitive, int Indeterminate)
{
    public static MergedCloseCounts From(IReadOnlyList<VerifiedCloseLabel> verified)
    {
        ArgumentNullException.ThrowIfNull(verified);
        var indeterminate = verified.Count(label => label.Label == CloseOutcomeLabel.Indeterminate);
        return new MergedCloseCounts(verified.Count, verified.Count - indeterminate, indeterminate);
    }
}

/// <summary>
/// Independently checks every proposed label against immutable source records, the provider discussion
/// as it stood before the reviewer posted, and the merged final code (spec §9.3). It walks the frozen
/// candidate inventory rather than the analyst's list, so a proposal for an item no row supports is
/// simply discarded, and a candidate the analyst ignored still gets an explicit Indeterminate result.
/// </summary>
internal sealed class MergedCloseVerifier(ReviewStore store)
{
    private readonly ReviewStore _store = store ?? throw new ArgumentNullException(nameof(store));

    public IReadOnlyList<VerifiedCloseLabel> Verify(
        MergedCloseEvidence evidence,
        IReadOnlyList<ProposedCloseLabel> proposals
    )
    {
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(proposals);

        var byCandidate = proposals
            .GroupBy(proposal => proposal.CandidateId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        return
        [
            .. evidence.Candidates.Select(candidate =>
                byCandidate.TryGetValue(candidate.Id, out var proposal)
                    ? VerifyOne(evidence, candidate, proposal)
                    : new VerifiedCloseLabel(
                        candidate.Id,
                        CloseOutcomeLabel.Indeterminate,
                        candidate.Sources,
                        CloseVerificationReasons.NoProposal
                    )
            ),
        ];
    }

    private VerifiedCloseLabel VerifyOne(
        MergedCloseEvidence evidence,
        CloseOutcomeCandidate candidate,
        ProposedCloseLabel proposal
    )
    {
        VerifiedCloseLabel Undetermined(string reason) =>
            new(candidate.Id, CloseOutcomeLabel.Indeterminate, proposal.Evidence, reason);

        if (!Enum.TryParse<CloseOutcomeLabel>(proposal.Label, ignoreCase: false, out var claimed))
        {
            return Undetermined(CloseVerificationReasons.UnknownLabel);
        }

        // A citation is only support if the exact bytes it names still exist in this engagement. A
        // mutated or removed source therefore fails here rather than silently keeping its label.
        if (proposal.Evidence.Count == 0 || !proposal.Evidence.All(reference => CitationHolds(evidence, reference)))
        {
            return Undetermined(CloseVerificationReasons.UnsupportedCitation);
        }

        return claimed switch
        {
            CloseOutcomeLabel.Indeterminate => Undetermined(CloseVerificationReasons.UnknownLabel),
            CloseOutcomeLabel.Confirmed => new VerifiedCloseLabel(
                candidate.Id,
                CloseOutcomeLabel.Confirmed,
                proposal.Evidence,
                CloseVerificationReasons.VerifiedAgainstSources
            ),
            CloseOutcomeLabel.Addressed or CloseOutcomeLabel.NotAddressed => VerifyAgainstFinalCode(
                evidence,
                candidate,
                proposal,
                claimed
            ),
            _ => VerifyAgainstDiscussion(evidence, candidate, proposal, claimed),
        };
    }

    /// <summary>Decides addressed/not-addressed from the merged file itself, never from thread status.</summary>
    private static VerifiedCloseLabel VerifyAgainstFinalCode(
        MergedCloseEvidence evidence,
        CloseOutcomeCandidate candidate,
        ProposedCloseLabel proposal,
        CloseOutcomeLabel claimed
    )
    {
        VerifiedCloseLabel Undetermined(string reason) =>
            new(candidate.Id, CloseOutcomeLabel.Indeterminate, proposal.Evidence, reason);

        if (string.IsNullOrWhiteSpace(proposal.Subject))
        {
            return Undetermined(CloseVerificationReasons.MissingSemanticSubject);
        }

        if (
            string.IsNullOrWhiteSpace(proposal.FilePath)
            || !evidence.FinalCode.FilesByPath.TryGetValue(proposal.FilePath, out var body)
        )
        {
            return Undetermined(CloseVerificationReasons.MissingFinalCode);
        }

        var stillPresent = body.Contains(proposal.Subject, StringComparison.OrdinalIgnoreCase);
        var supported = claimed == CloseOutcomeLabel.NotAddressed ? stillPresent : !stillPresent;
        return supported
            ? new VerifiedCloseLabel(
                candidate.Id,
                claimed,
                proposal.Evidence,
                CloseVerificationReasons.VerifiedAgainstFinalCode
            )
            : Undetermined(CloseVerificationReasons.ContradictedByFinalCode);
    }

    /// <summary>
    /// Decides novelty against the discussion as it existed strictly before the reviewer posted, so a
    /// later equivalent comment by someone else cannot retroactively remove novelty.
    /// </summary>
    private static VerifiedCloseLabel VerifyAgainstDiscussion(
        MergedCloseEvidence evidence,
        CloseOutcomeCandidate candidate,
        ProposedCloseLabel proposal,
        CloseOutcomeLabel claimed
    )
    {
        VerifiedCloseLabel Undetermined(string reason) =>
            new(candidate.Id, CloseOutcomeLabel.Indeterminate, proposal.Evidence, reason);

        if (string.IsNullOrWhiteSpace(proposal.Subject))
        {
            return Undetermined(CloseVerificationReasons.MissingSemanticSubject);
        }

        var equivalentBefore = evidence.Discussion.Any(entry =>
            entry.PostedAtUtc < candidate.ObservedAtUtc
            && entry.Body.Contains(proposal.Subject, StringComparison.OrdinalIgnoreCase)
        );

        var supported = claimed == CloseOutcomeLabel.Novel ? !equivalentBefore : equivalentBefore;
        return supported
            ? new VerifiedCloseLabel(
                candidate.Id,
                claimed,
                proposal.Evidence,
                CloseVerificationReasons.VerifiedAgainstDiscussion
            )
            : Undetermined(CloseVerificationReasons.ContradictedByPriorDiscussion);
    }

    private bool CitationHolds(MergedCloseEvidence evidence, AuditSourceReference reference) =>
        _store.AuditSourceMatchesEngagement(evidence.Engagement.Id, reference.SourceRecordId, reference.ContentSha256);
}
