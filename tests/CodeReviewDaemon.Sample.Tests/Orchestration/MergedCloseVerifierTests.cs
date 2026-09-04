using CodeReviewDaemon.Sample.Orchestration;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Tests.Infrastructure;

namespace CodeReviewDaemon.Sample.Tests.Orchestration;

/// <summary>
/// The verifier re-derives every label from frozen sources, provider discussion, and final code. It
/// never trusts the analyst's prose, and anything it cannot support stays <c>Indeterminate</c>.
/// </summary>
public sealed class MergedCloseVerifierTests
{
    private static readonly DateTimeOffset Merge = MergedCloseTestData.Start.AddHours(4);

    [Fact]
    public void Proposal_for_a_candidate_that_no_row_supports_is_discarded()
    {
        using var context = new VerifierContext();
        var verified = context.Verify([
            Proposal("observation:obs-finding", nameof(CloseOutcomeLabel.NotAddressed)),
            Proposal("observation:invented-by-the-model", nameof(CloseOutcomeLabel.Novel)),
        ]);

        verified.Select(label => label.CandidateId).Should().Equal("observation:obs-finding");
    }

    [Fact]
    public void Candidate_without_any_proposal_is_indeterminate()
    {
        using var context = new VerifierContext();

        var verified = context.Verify([]);

        var only = verified.Should().ContainSingle().Subject;
        only.Label.Should().Be(CloseOutcomeLabel.Indeterminate);
        only.VerificationReasonCode.Should().Be(CloseVerificationReasons.NoProposal);
    }

    [Fact]
    public void Citation_that_no_source_record_supports_becomes_indeterminate()
    {
        using var context = new VerifierContext();

        var verified = context.Verify([
            Proposal(
                "observation:obs-finding",
                nameof(CloseOutcomeLabel.NotAddressed),
                evidence: [new AuditSourceReference("src-that-never-existed", new string('a', 64))]
            ),
        ]);

        var only = verified.Should().ContainSingle().Subject;
        only.Label.Should().Be(CloseOutcomeLabel.Indeterminate);
        only.VerificationReasonCode.Should().Be(CloseVerificationReasons.UnsupportedCitation);
    }

    [Fact]
    public void Mutating_a_cited_source_turns_its_definitive_label_indeterminate()
    {
        using var context = new VerifierContext();
        var honest = context.Verify([context.NotAddressedProposal()]);
        honest.Single().Label.Should().Be(CloseOutcomeLabel.NotAddressed);

        // Re-cite the same record ID under the hash the source would have had if its bytes changed.
        var mutated = context.Verify([
            context.NotAddressedProposal() with
            {
                Evidence =
                [
                    new AuditSourceReference(context.FindingSource.Id, MergedCloseTestData.Sha256("tampered"u8)),
                ],
            },
        ]);

        var only = mutated.Should().ContainSingle().Subject;
        only.Label.Should().Be(CloseOutcomeLabel.Indeterminate);
        only.VerificationReasonCode.Should().Be(CloseVerificationReasons.UnsupportedCitation);
        MergedCloseCounts.From(mutated).Definitive.Should().Be(0);
        MergedCloseCounts.From(honest).Definitive.Should().Be(1);
    }

    [Fact]
    public void Addressed_is_checked_against_final_code_not_thread_status()
    {
        using var context = new VerifierContext();

        // The thread was resolved, but the marker the finding named is still in the merged file.
        var claimedAddressed = context.Verify([
            context.NotAddressedProposal() with
            {
                Label = nameof(CloseOutcomeLabel.Addressed),
            },
        ]);

        var only = claimedAddressed.Should().ContainSingle().Subject;
        only.Label.Should().Be(CloseOutcomeLabel.Indeterminate);
        only.VerificationReasonCode.Should().Be(CloseVerificationReasons.ContradictedByFinalCode);
    }

    [Fact]
    public void Addressed_is_confirmed_when_the_marker_is_gone_from_the_merged_file()
    {
        using var context = new VerifierContext(finalFileBody: "retry loop now rethrows the last error");

        var verified = context.Verify([
            context.NotAddressedProposal() with
            {
                Label = nameof(CloseOutcomeLabel.Addressed),
            },
        ]);

        var only = verified.Should().ContainSingle().Subject;
        only.Label.Should().Be(CloseOutcomeLabel.Addressed);
        only.VerificationReasonCode.Should().Be(CloseVerificationReasons.VerifiedAgainstFinalCode);
    }

    [Fact]
    public void Finding_about_a_file_missing_from_the_frozen_final_code_is_indeterminate()
    {
        using var context = new VerifierContext();

        var verified = context.Verify([context.NotAddressedProposal() with { FilePath = "src/NotInTheSnapshot.cs" }]);

        var only = verified.Should().ContainSingle().Subject;
        only.Label.Should().Be(CloseOutcomeLabel.Indeterminate);
        only.VerificationReasonCode.Should().Be(CloseVerificationReasons.MissingFinalCode);
    }

    [Fact]
    public void Novelty_compares_against_discussion_as_it_existed_before_the_post()
    {
        using var context = new VerifierContext(
            discussion:
            [
                // Posted before the reviewer's observation, by someone else, naming the same subject.
                new MergedCloseDiscussionEntry(
                    "c-1",
                    "another-human",
                    MergedCloseTestData.Start.AddMinutes(-30),
                    "the retry loop swallows the last error"
                ),
            ]
        );

        var verified = context.Verify([
            context.NotAddressedProposal() with
            {
                Label = nameof(CloseOutcomeLabel.Novel),
            },
        ]);

        var only = verified.Should().ContainSingle().Subject;
        only.Label.Should().Be(CloseOutcomeLabel.Indeterminate);
        only.VerificationReasonCode.Should().Be(CloseVerificationReasons.ContradictedByPriorDiscussion);
    }

    [Fact]
    public void Novelty_survives_an_equivalent_comment_posted_after_the_reviewer()
    {
        using var context = new VerifierContext(
            discussion:
            [
                new MergedCloseDiscussionEntry(
                    "c-1",
                    "another-human",
                    MergedCloseTestData.Start.AddMinutes(30),
                    "the retry loop swallows the last error"
                ),
            ]
        );

        var verified = context.Verify([
            context.NotAddressedProposal() with
            {
                Label = nameof(CloseOutcomeLabel.Novel),
            },
        ]);

        var only = verified.Should().ContainSingle().Subject;
        only.Label.Should().Be(CloseOutcomeLabel.Novel);
        only.VerificationReasonCode.Should().Be(CloseVerificationReasons.VerifiedAgainstDiscussion);
    }

    [Fact]
    public void An_unknown_label_never_becomes_definitive()
    {
        using var context = new VerifierContext();

        var verified = context.Verify([context.NotAddressedProposal() with { Label = "DefinitelyValuable" }]);

        var only = verified.Should().ContainSingle().Subject;
        only.Label.Should().Be(CloseOutcomeLabel.Indeterminate);
        only.VerificationReasonCode.Should().Be(CloseVerificationReasons.UnknownLabel);
    }

    private static ProposedCloseLabel Proposal(
        string candidateId,
        string label,
        IReadOnlyList<AuditSourceReference>? evidence = null
    ) => new(candidateId, label, "src/Retry.cs", "swallows the last error", evidence ?? []);

    private sealed class VerifierContext : IDisposable
    {
        private readonly MergedCloseTestData _data = new();

        public VerifierContext(
            string finalFileBody = "retry loop swallows the last error",
            IReadOnlyList<MergedCloseDiscussionEntry>? discussion = null
        )
        {
            var round = _data.AddCompletedRound(EngagementRoundIntent.CodeReview);
            FindingSource = _data.AddSource(round.Id, "src-finding", "the retry loop swallows the last error");
            _ = _data.AddObservation(
                round.Id,
                "obs-finding",
                ObservationKind.Finding,
                "retry loop swallows the last error",
                FindingSource,
                1
            );
            Evidence = new MergedCloseEvidence(
                _data.Engagement,
                MergedCloseCandidateInventory.Build(_data.Store, _data.Engagement.Id),
                discussion ?? [],
                new MergedCloseFinalCode(
                    "merge-head",
                    new Dictionary<string, string> { ["src/Retry.cs"] = finalFileBody }
                ),
                Merge
            );
        }

        public AuditSourceRecord FindingSource { get; }

        public MergedCloseEvidence Evidence { get; }

        public ProposedCloseLabel NotAddressedProposal() =>
            Proposal(
                "observation:obs-finding",
                nameof(CloseOutcomeLabel.NotAddressed),
                [MergedCloseTestData.Reference(FindingSource)]
            );

        public IReadOnlyList<VerifiedCloseLabel> Verify(IReadOnlyList<ProposedCloseLabel> proposals) =>
            new MergedCloseVerifier(_data.Store).Verify(Evidence, proposals);

        public void Dispose() => _data.Dispose();
    }
}
