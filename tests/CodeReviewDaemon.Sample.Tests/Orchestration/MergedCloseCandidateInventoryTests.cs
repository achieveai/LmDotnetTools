using CodeReviewDaemon.Sample.Orchestration;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
// Both namespaces declare a review-action type; the persisted rows are the denominator here.
using ReviewAction = CodeReviewDaemon.Sample.Persistence.Models.ReviewAction;
using ReviewActionKind = CodeReviewDaemon.Sample.Persistence.Models.ReviewActionKind;

namespace CodeReviewDaemon.Sample.Tests.Orchestration;

/// <summary>
/// The close denominator is built by code from typed rows. These tests pin that the inventory is
/// complete, stable, and independent of anything a model says.
/// </summary>
public sealed class MergedCloseCandidateInventoryTests
{
    [Fact]
    public void Inventory_enumerates_every_typed_row_across_all_rounds_exactly_once()
    {
        using var data = new MergedCloseTestData();
        var first = data.AddCompletedRound(EngagementRoundIntent.CodeReview);
        var firstSource = data.AddSource(first.Id, "src-finding", "the retry loop drops the last error");
        _ = data.AddObservation(first.Id, "obs-finding", ObservationKind.Finding, "retry drops errors", firstSource, 1);
        _ = data.AddAskedQuestion(first.Id, "q-1", "Is the omission deliberate?", firstSource);

        var second = data.AddCompletedRound(EngagementRoundIntent.DiscussionFollowUp);
        var secondSource = data.AddSource(second.Id, "src-reply", "author explained the omission");
        _ = data.AddObservation(
            second.Id,
            "obs-reply",
            ObservationKind.DiscussionContribution,
            "clarified the omission",
            secondSource,
            1
        );
        _ = data.AddAcceptedAction(second.Id, "reply-1", ReviewActionKind.ReplyToDiscussion, secondSource);

        var candidates = MergedCloseCandidateInventory.Build(data.Store, data.Engagement.Id);

        candidates
            .Select(candidate => candidate.Id)
            .Should()
            .Equal(
                "action:ask-q-1",
                "action:reply-1",
                "observation:obs-finding",
                "observation:obs-reply",
                "question:q-1"
            );
    }

    [Fact]
    public void Unanswered_at_merge_questions_remain_candidates()
    {
        using var data = new MergedCloseTestData();
        var round = data.AddCompletedRound(EngagementRoundIntent.CodeReview);
        var source = data.AddSource(round.Id, "src-q", "no answer arrived before merge");
        _ = data.AddAskedQuestion(
            round.Id,
            "q-open",
            "Was the fallback intentional?",
            source,
            ClarificationQuestionState.UnansweredAtMerge
        );

        var candidates = MergedCloseCandidateInventory.Build(data.Store, data.Engagement.Id);

        candidates
            .Should()
            .ContainSingle(candidate => candidate.Kind == CloseCandidateKind.Question)
            .Which.Id.Should()
            .Be("question:q-open");
    }

    [Fact]
    public void Every_candidate_carries_the_exact_source_references_of_its_row()
    {
        using var data = new MergedCloseTestData();
        var round = data.AddCompletedRound(EngagementRoundIntent.CodeReview);
        var source = data.AddSource(round.Id, "src-finding", "the retry loop drops the last error");
        _ = data.AddObservation(round.Id, "obs-finding", ObservationKind.Finding, "retry drops errors", source, 1);

        var candidate = MergedCloseCandidateInventory
            .Build(data.Store, data.Engagement.Id)
            .Single(item => item.Id == "observation:obs-finding");

        candidate.Sources.Should().Equal(new AuditSourceReference(source.Id, source.ContentSha256));
    }

    [Fact]
    public void Inventory_is_byte_stable_across_rebuilds()
    {
        using var data = new MergedCloseTestData();
        var round = data.AddCompletedRound(EngagementRoundIntent.CodeReview);
        var source = data.AddSource(round.Id, "src-1", "evidence");
        _ = data.AddObservation(round.Id, "obs-b", ObservationKind.Finding, "second", source, 2);
        _ = data.AddObservation(round.Id, "obs-a", ObservationKind.Finding, "first", source, 1);

        var first = MergedCloseCandidateInventory.Build(data.Store, data.Engagement.Id);
        var second = MergedCloseCandidateInventory.Build(data.Store, data.Engagement.Id);

        first.Should().Equal(second);
        first.Select(candidate => candidate.Id).Should().BeInAscendingOrder(StringComparer.Ordinal);
    }

    [Fact]
    public void Planned_but_never_accepted_actions_are_not_candidates()
    {
        using var data = new MergedCloseTestData();
        var round = data.AddCompletedRound(EngagementRoundIntent.CodeReview);
        var source = data.AddSource(round.Id, "src-1", "evidence");
        _ = data.Store.AddOrGetReviewAction(
            new ReviewAction(
                round.Id,
                "never-sent",
                ReviewActionKind.SubmitInlineFindings,
                ReviewActionStatus.Planned,
                MergedCloseTestData.Sha256("payload"u8),
                null,
                null,
                null,
                MergedCloseTestData.Start,
                MergedCloseTestData.Start
            ),
            [MergedCloseTestData.Reference(source)]
        );

        var candidates = MergedCloseCandidateInventory.Build(data.Store, data.Engagement.Id);

        candidates.Should().NotContain(candidate => candidate.Id == "action:never-sent");
    }

    [Fact]
    public void Context_claims_and_gaps_are_not_outcome_candidates()
    {
        using var data = new MergedCloseTestData();
        var round = data.AddCompletedRound(EngagementRoundIntent.CodeReview);
        var source = data.AddSource(round.Id, "src-1", "evidence");
        _ = data.AddObservation(round.Id, "obs-context", ObservationKind.ContextClaim, "read the file", source, 1);
        _ = data.AddObservation(round.Id, "obs-gap", ObservationKind.Gap, "capture severed", source, 2);

        var candidates = MergedCloseCandidateInventory.Build(data.Store, data.Engagement.Id);

        candidates.Should().BeEmpty();
    }
}
