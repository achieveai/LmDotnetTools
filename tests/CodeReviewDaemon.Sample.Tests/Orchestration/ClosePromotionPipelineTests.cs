using CodeReviewDaemon.Sample.Configuration;
using CodeReviewDaemon.Sample.Orchestration;
using CodeReviewDaemon.Sample.Persistence;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Tests.Infrastructure;

namespace CodeReviewDaemon.Sample.Tests.Orchestration;

/// <summary>
/// Promotion runs Knowledge Base first, then append-only developer learning, and records a durable
/// outcome for every pass — including the ones that decline or fail.
/// </summary>
public sealed class ClosePromotionPipelineTests
{
    [Fact]
    public async Task Both_passes_decline_with_feature_disabled_when_the_flags_are_off()
    {
        using var context = new PipelineContext();

        var outcomes = await context.RunAsync(new CodeReviewDaemonOptions());

        outcomes
            .Should()
            .AllSatisfy(outcome =>
            {
                outcome.Disposition.Should().Be(PromotionDisposition.Declined);
                outcome.ReasonCode.Should().Be(PromotionReasons.FeatureDisabled);
            });
        outcomes
            .Select(outcome => outcome.DestinationKind)
            .Should()
            .Equal(PromotionDestinations.KnowledgeBase, PromotionDestinations.DeveloperLearnings);
        context.KnowledgeCalls.Should().BeEmpty();
        context.DeveloperCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task Knowledge_base_completes_before_developer_promotion_starts()
    {
        using var context = new PipelineContext();

        _ = await context.RunAsync(context.EnabledOptions());

        context.Order.Should().Equal(PromotionDestinations.KnowledgeBase, PromotionDestinations.DeveloperLearnings);
    }

    [Fact]
    public async Task A_failed_knowledge_pass_still_records_both_outcomes_and_blocks_the_developer_write()
    {
        using var context = new PipelineContext(knowledge: _ => PromotionDisposition.Failed);

        var outcomes = await context.RunAsync(context.EnabledOptions());

        outcomes[0].Disposition.Should().Be(PromotionDisposition.Failed);
        outcomes[1].Disposition.Should().Be(PromotionDisposition.Failed);
        outcomes[1].ReasonCode.Should().Be(PromotionReasons.BlockedByEarlierPass);
        context.DeveloperCalls.Should().BeEmpty("developer learning must not run after knowledge failed");
        context.Store.ListPromotionOutcomes(context.CloseRound.Id).Should().HaveCount(2);
    }

    [Fact]
    public async Task A_failed_developer_pass_is_not_dropped_because_knowledge_wrote()
    {
        using var context = new PipelineContext(developer: _ => PromotionDisposition.Failed);

        var outcomes = await context.RunAsync(context.EnabledOptions());

        outcomes[0].Disposition.Should().Be(PromotionDisposition.Written);
        outcomes[1].Disposition.Should().Be(PromotionDisposition.Failed);
        MergedClosePromotionStatus.HasBlockingFailure(outcomes).Should().BeTrue();
    }

    [Fact]
    public async Task Replaying_the_same_source_observation_creates_no_duplicate_contribution()
    {
        using var context = new PipelineContext();

        var first = await context.RunAsync(context.EnabledOptions());
        var second = await context.RunAsync(context.EnabledOptions());

        second
            .Select(outcome => outcome.DestinationContentHash)
            .Should()
            .Equal(first.Select(outcome => outcome.DestinationContentHash));
        context.Store.ListPromotionOutcomes(context.CloseRound.Id).Should().HaveCount(2);
        context.KnowledgeCalls.Should().ContainSingle("an already-written source observation is not rewritten");
    }

    [Fact]
    public void Source_observation_id_is_derived_from_identity_and_source_hashes()
    {
        using var context = new PipelineContext();
        var verified = context.Verified();

        var first = ClosePromotionPipeline.BuildSourceObservationId(
            context.Engagement,
            context.CloseRound.Id,
            verified
        );
        var second = ClosePromotionPipeline.BuildSourceObservationId(
            context.Engagement,
            context.CloseRound.Id,
            verified
        );

        first.Should().Be(second);
        first.Should().NotBeNullOrWhiteSpace();
        ClosePromotionPipeline
            .BuildSourceObservationId(
                context.Engagement,
                context.CloseRound.Id,
                verified with
                {
                    CandidateId = "observation:another",
                }
            )
            .Should()
            .NotBe(first);
    }

    [Fact]
    public async Task Only_confirmed_labels_are_promoted()
    {
        using var context = new PipelineContext();

        var outcomes = await context.RunAsync(
            context.EnabledOptions(),
            [context.Verified() with { Label = CloseOutcomeLabel.Indeterminate }]
        );

        outcomes.Should().AllSatisfy(outcome => outcome.ReasonCode.Should().Be(PromotionReasons.NothingConfirmed));
        context.KnowledgeCalls.Should().BeEmpty();
    }

    private sealed class PipelineContext : IDisposable
    {
        private readonly MergedCloseTestData _data = new();
        private readonly Func<PromotionRequest, PromotionDisposition> _knowledge;
        private readonly Func<PromotionRequest, PromotionDisposition> _developer;

        public PipelineContext(
            Func<PromotionRequest, PromotionDisposition>? knowledge = null,
            Func<PromotionRequest, PromotionDisposition>? developer = null
        )
        {
            _knowledge = knowledge ?? (_ => PromotionDisposition.Written);
            _developer = developer ?? (_ => PromotionDisposition.Written);
            var round = _data.AddCompletedRound(EngagementRoundIntent.CodeReview);
            FindingSource = _data.AddSource(round.Id, "src-finding", "the retry loop swallows the last error");
            _ = _data.AddObservation(
                round.Id,
                "obs-finding",
                ObservationKind.Finding,
                "retry swallows errors",
                FindingSource,
                1
            );
            CloseRound = _data.AddRound(EngagementRoundIntent.MergedClose);
        }

        public ReviewStore Store => _data.Store;

        public PrEngagement Engagement => _data.Engagement;

        public EngagementRound CloseRound { get; }

        public AuditSourceRecord FindingSource { get; }

        public List<string> Order { get; } = [];

        public List<PromotionRequest> KnowledgeCalls { get; } = [];

        public List<PromotionRequest> DeveloperCalls { get; } = [];

        public CodeReviewDaemonOptions EnabledOptions() =>
            new() { EnableMergedCloseReporting = true, EnableMergedLearningPromotion = true };

        public VerifiedCloseLabel Verified() =>
            new(
                "observation:obs-finding",
                CloseOutcomeLabel.Confirmed,
                [MergedCloseTestData.Reference(FindingSource)],
                CloseVerificationReasons.VerifiedAgainstFinalCode
            );

        public Task<IReadOnlyList<PromotionOutcome>> RunAsync(
            CodeReviewDaemonOptions options,
            IReadOnlyList<VerifiedCloseLabel>? verified = null
        ) =>
            new ClosePromotionPipeline(
                _data.Store,
                options,
                (request, _) =>
                {
                    Order.Add(PromotionDestinations.KnowledgeBase);
                    KnowledgeCalls.Add(request);
                    return Task.FromResult(Result(request, _knowledge(request)));
                },
                (request, _) =>
                {
                    Order.Add(PromotionDestinations.DeveloperLearnings);
                    DeveloperCalls.Add(request);
                    return Task.FromResult(Result(request, _developer(request)));
                }
            ).RunAsync(_data.Engagement, CloseRound.Id, verified ?? [Verified()], CancellationToken.None);

        private static PromotionOutcome Result(PromotionRequest request, PromotionDisposition disposition) =>
            disposition == PromotionDisposition.Written
                ? new PromotionOutcome(
                    request.SourceObservationId,
                    request.DestinationKind,
                    disposition,
                    $"{request.DestinationKind}/retry.md",
                    MergedCloseTestData.Sha256(System.Text.Encoding.UTF8.GetBytes(request.SourceObservationId)),
                    null
                )
                : new PromotionOutcome(
                    request.SourceObservationId,
                    request.DestinationKind,
                    disposition,
                    null,
                    null,
                    "write_rejected"
                );

        public void Dispose() => _data.Dispose();
    }
}
