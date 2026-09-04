using CodeReviewDaemon.Sample.Orchestration;
using CodeReviewDaemon.Sample.Persistence;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using Microsoft.Extensions.Time.Testing;

namespace CodeReviewDaemon.Sample.Tests.Orchestration;

public sealed class PrEngagementCoordinatorTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 2, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task First_observation_admits_code_review_immediately()
    {
        using var fixture = new Fixture();

        var decision = await fixture.ObserveAsync(head: "head-1");

        decision.Kind.Should().Be(EngagementDecisionKind.AdmitCodeReview);
        var round = fixture.Store.GetEngagementRound(decision.RoundId!.Value)!;
        round.Intent.Should().Be(EngagementRoundIntent.CodeReview);
        round.Status.Should().Be(EngagementRoundStatus.Pending);
        fixture.Store.GetEngagement(round.PrEngagementId)!.ActiveRoundId.Should().Be(round.Id);
    }

    [Fact]
    public async Task Admission_freezes_the_latest_prior_round_as_the_observation_boundary()
    {
        using var fixture = new Fixture();
        var first = await fixture.ObserveAsync(head: "head-1");
        fixture.Complete(first, Start.AddMinutes(5));
        fixture.Time.SetUtcNow(Start.AddHours(2));

        var admitted = await fixture.ObserveAsync(
            head: "head-1",
            activity: fixture.Comment("review-comment:2", fixture.Time.GetUtcNow())
        );

        admitted.Kind.Should().Be(EngagementDecisionKind.AdmitDiscussion);
        fixture.Store.GetEngagementRound(admitted.RoundId!.Value)!.PriorObservationBoundary.Should().Be(first.RoundId);
    }

    [Fact]
    public async Task Comment_before_cooldown_coalesces_then_admits_at_exact_boundary()
    {
        using var fixture = new Fixture();
        var first = await fixture.ObserveAsync(head: "head-1");
        fixture.Complete(first, Start.AddMinutes(5));
        fixture.Time.SetUtcNow(Start.AddHours(1).AddMinutes(4).AddSeconds(59));

        var coalesced = await fixture.ObserveAsync(
            head: "head-1",
            activity: fixture.Comment("review-comment:2", fixture.Time.GetUtcNow())
        );

        coalesced.Kind.Should().Be(EngagementDecisionKind.Coalesced);
        coalesced.RoundId.Should().BeNull();
        fixture.Time.Advance(TimeSpan.FromSeconds(1));
        var admitted = await fixture.ObserveAsync(head: "head-1");
        admitted.Kind.Should().Be(EngagementDecisionKind.AdmitDiscussion);
        fixture
            .Store.GetEngagementRound(admitted.RoundId!.Value)!
            .Intent.Should()
            .Be(EngagementRoundIntent.DiscussionFollowUp);
    }

    [Fact]
    public async Task New_head_during_cooldown_wins_over_pending_discussion_at_eligibility()
    {
        using var fixture = new Fixture();
        var first = await fixture.ObserveAsync(head: "head-1");
        fixture.Complete(first, Start.AddMinutes(5));
        fixture.Time.SetUtcNow(Start.AddMinutes(25));
        var comment = fixture.Comment("review-comment:2", fixture.Time.GetUtcNow());

        (await fixture.ObserveAsync(head: "head-1", activity: comment))
            .Kind.Should()
            .Be(EngagementDecisionKind.Coalesced);
        (await fixture.ObserveAsync(head: "head-2")).Kind.Should().Be(EngagementDecisionKind.Coalesced);

        fixture.Time.SetUtcNow(Start.AddHours(1).AddMinutes(5));
        var admitted = await fixture.ObserveAsync(head: "head-2");
        admitted.Kind.Should().Be(EngagementDecisionKind.AdmitCodeReview);
        var round = fixture.Store.GetEngagementRound(admitted.RoundId!.Value)!;
        round.HeadSha.Should().Be("head-2");
        round.ActivityUpperBound.Should().Be(comment.Watermark);
    }

    [Fact]
    public async Task Further_comments_extend_pending_discussion_upper_bound()
    {
        using var fixture = new Fixture();
        var first = await fixture.ObserveAsync(head: "head-1");
        fixture.Complete(first, Start.AddMinutes(5));
        fixture.Time.SetUtcNow(Start.AddMinutes(20));
        var firstComment = fixture.Comment("review-comment:2", fixture.Time.GetUtcNow());
        await fixture.ObserveAsync(head: "head-1", activity: firstComment);
        fixture.Time.Advance(TimeSpan.FromMinutes(10));
        var lastComment = fixture.Comment("review-comment:3", fixture.Time.GetUtcNow());
        await fixture.ObserveAsync(head: "head-1", activity: lastComment);
        fixture.Time.SetUtcNow(Start.AddHours(1).AddMinutes(5));

        var admitted = await fixture.ObserveAsync(head: "head-1");

        fixture
            .Store.GetEngagementRound(admitted.RoundId!.Value)!
            .ActivityUpperBound.Should()
            .Be(lastComment.Watermark);
    }

    [Fact]
    public async Task Merge_bypasses_cooldown_and_preempts_running_ordinary_round()
    {
        using var fixture = new Fixture();
        var first = await fixture.ObserveAsync(head: "head-1");
        fixture.Start(first);
        fixture.Time.Advance(TimeSpan.FromMinutes(3));

        var merged = await fixture.ObserveAsync(head: "head-1", lifecycle: PrLifecycleState.Merged);

        fixture.Store.GetEngagementRound(first.RoundId!.Value)!.Status.Should().Be(EngagementRoundStatus.Superseded);
        merged.Kind.Should().Be(EngagementDecisionKind.AdmitMergedClose);
        fixture.Store.GetEngagementRound(merged.RoundId!.Value)!.Intent.Should().Be(EngagementRoundIntent.MergedClose);
    }

    [Theory]
    [InlineData("Closed")]
    [InlineData("Abandoned")]
    public async Task Closed_or_abandoned_selects_cleanup_without_round(string lifecycleName)
    {
        using var fixture = new Fixture();
        var lifecycle = Enum.Parse<PrLifecycleState>(lifecycleName);

        var decision = await fixture.ObserveAsync(head: "head-1", lifecycle: lifecycle);

        decision.Kind.Should().Be(EngagementDecisionKind.CleanupTerminal);
        decision.RoundId.Should().BeNull();
        fixture.Store.ListEngagementRounds(fixture.EngagementId).Should().BeEmpty();
    }

    [Fact]
    public async Task Superseded_round_does_not_start_cooldown()
    {
        using var fixture = new Fixture();
        var first = await fixture.ObserveAsync(head: "head-1");
        fixture.Start(first);
        fixture
            .Store.TryTransitionEngagementRound(
                first.RoundId!.Value,
                EngagementRoundStatus.Running,
                EngagementRoundStatus.Superseded,
                fixture.Time.GetUtcNow()
            )
            .Should()
            .BeTrue();

        var replacement = await fixture.ObserveAsync(head: "head-2");

        replacement.Kind.Should().Be(EngagementDecisionKind.AdmitCodeReview);
        fixture.Store.GetEngagement(fixture.EngagementId)!.NextEligibleAt.Should().BeNull();
    }

    [Fact]
    public async Task Concurrent_observations_admit_only_one_round()
    {
        using var fixture = new Fixture();
        using var start = new Barrier(3);
        var attempts = Enumerable
            .Range(0, 2)
            .Select(_ =>
                Task.Run(async () =>
                {
                    start.SignalAndWait();
                    return await fixture.ObserveAsync(head: "head-1");
                })
            )
            .ToArray();

        start.SignalAndWait();
        var decisions = await Task.WhenAll(attempts);

        decisions.Count(value => value.Kind == EngagementDecisionKind.AdmitCodeReview).Should().Be(1);
        fixture
            .Store.ListEngagementRounds(fixture.EngagementId)
            .Should()
            .ContainSingle(round => round.Status == EngagementRoundStatus.Pending);
    }

    [Fact]
    public async Task New_head_supersedes_a_running_ordinary_round_and_admits_code_review()
    {
        using var fixture = new Fixture();
        var first = await fixture.ObserveAsync(head: "head-1");
        fixture.Start(first);
        fixture.Time.Advance(TimeSpan.FromMinutes(2));

        var replacement = await fixture.ObserveAsync(head: "head-2");

        fixture.Store.GetEngagementRound(first.RoundId!.Value)!.Status.Should().Be(EngagementRoundStatus.Superseded);
        replacement.Kind.Should().Be(EngagementDecisionKind.AdmitCodeReview);
        fixture.Store.GetEngagementRound(replacement.RoundId!.Value)!.HeadSha.Should().Be("head-2");
    }

    [Fact]
    public async Task Restart_reconciles_same_head_running_round_to_retry_without_new_identity()
    {
        using var fixture = new Fixture();
        var first = await fixture.ObserveAsync(head: "head-1");
        fixture.Start(first);

        var retry = await fixture.Coordinator.ReconcileAsync(
            fixture.RepoId,
            fixture.Repo,
            fixture.Descriptor("head-1", PrLifecycleState.Open),
            fixture.Snapshot("head-1", PrLifecycleState.Open),
            CancellationToken.None
        );

        retry.RoundId.Should().Be(first.RoundId);
        retry.Kind.Should().Be(EngagementDecisionKind.AdmitCodeReview);
        fixture.Store.GetEngagementRound(first.RoundId!.Value)!.Status.Should().Be(EngagementRoundStatus.RetryPending);
        fixture.Store.ListEngagementRounds(fixture.EngagementId).Should().ContainSingle();
    }

    [Fact]
    public async Task Restart_reconciles_a_running_merged_close_without_replacing_its_identity()
    {
        using var fixture = new Fixture();
        var first = await fixture.ObserveAsync("head-1", PrLifecycleState.Merged);
        fixture.Start(first);

        var retry = await fixture.Coordinator.ReconcileAsync(
            fixture.RepoId,
            fixture.Repo,
            fixture.Descriptor("head-1", PrLifecycleState.Merged),
            fixture.Snapshot("head-1", PrLifecycleState.Merged),
            CancellationToken.None
        );

        retry.Kind.Should().Be(EngagementDecisionKind.AdmitMergedClose);
        retry.RoundId.Should().Be(first.RoundId);
        retry.ReasonCode.Should().Be("running_round_retry_pending");
        fixture.Store.GetEngagementRound(first.RoundId!.Value)!.Status.Should().Be(EngagementRoundStatus.RetryPending);
        fixture.Store.ListEngagementRounds(fixture.EngagementId).Should().ContainSingle();
    }

    [Fact]
    public async Task Retry_pending_round_is_readmitted_with_the_same_identity()
    {
        using var fixture = new Fixture();
        var first = await fixture.ObserveAsync(head: "head-1");
        fixture.Start(first);
        fixture
            .Store.TryTransitionEngagementRound(
                first.RoundId!.Value,
                EngagementRoundStatus.Running,
                EngagementRoundStatus.RetryPending,
                fixture.Time.GetUtcNow()
            )
            .Should()
            .BeTrue();

        var retry = await fixture.ObserveAsync(head: "head-1");

        retry.Kind.Should().Be(EngagementDecisionKind.AdmitCodeReview);
        retry.RoundId.Should().Be(first.RoundId);
        fixture.Store.ListEngagementRounds(fixture.EngagementId).Should().ContainSingle();
    }

    [Fact]
    public async Task Deleted_and_system_activity_do_not_create_discussion_demand()
    {
        using var fixture = new Fixture();
        var first = await fixture.ObserveAsync(head: "head-1");
        fixture.Complete(first, Start.AddMinutes(5));
        fixture.Time.SetUtcNow(Start.AddHours(2));
        var system = fixture.Comment("review-comment:system", fixture.Time.GetUtcNow(), ProviderDiscussionKind.System);

        var decision = await fixture.ObserveAsync(head: "head-1", activity: system);

        decision.Kind.Should().Be(EngagementDecisionKind.None);
    }

    [Fact]
    public async Task Merged_snapshot_is_authoritative_when_open_listing_races_the_merge()
    {
        using var fixture = new Fixture();
        var descriptor = fixture.Descriptor("head-1", PrLifecycleState.Open);
        var snapshot = fixture.Snapshot("head-1", PrLifecycleState.Merged);

        var decision = await fixture.Coordinator.ObserveAsync(
            fixture.RepoId,
            fixture.Repo,
            descriptor,
            snapshot,
            CancellationToken.None
        );

        decision.Kind.Should().Be(EngagementDecisionKind.AdmitMergedClose);
    }

    [Fact]
    public async Task Base_change_supersedes_a_retry_pending_round_and_admits_replacement()
    {
        using var fixture = new Fixture();
        var first = await fixture.ObserveAsync("head-1");
        fixture.Start(first);
        fixture
            .Store.TryTransitionEngagementRound(
                first.RoundId!.Value,
                EngagementRoundStatus.Running,
                EngagementRoundStatus.RetryPending,
                Start.AddMinutes(1)
            )
            .Should()
            .BeTrue();
        var descriptor = fixture.Descriptor("head-1", PrLifecycleState.Open) with { BaseSha = "base-2" };
        var snapshot = fixture.Snapshot("head-1", PrLifecycleState.Open) with { BaseSha = "base-2" };

        var replacement = await fixture.Coordinator.ObserveAsync(
            fixture.RepoId,
            fixture.Repo,
            descriptor,
            snapshot,
            CancellationToken.None
        );

        fixture.Store.GetEngagementRound(first.RoundId.Value)!.Status.Should().Be(EngagementRoundStatus.Superseded);
        replacement.Kind.Should().Be(EngagementDecisionKind.AdmitCodeReview);
        fixture.Store.GetEngagementRound(replacement.RoundId!.Value)!.BaseSha.Should().Be("base-2");
    }

    [Fact]
    public async Task Parked_merged_close_is_visible_blocked_work_and_is_not_admitted_again()
    {
        using var fixture = new Fixture();
        var first = await fixture.ObserveAsync("head-1", PrLifecycleState.Merged);
        fixture.Start(first);
        fixture
            .Store.TryParkEngagementRound(
                first.RoundId!.Value,
                EngagementRoundStatus.Running,
                Start.AddMinutes(1),
                "merged_close_required_output_failed"
            )
            .Should()
            .BeTrue();

        var replay = await fixture.ObserveAsync("head-1", PrLifecycleState.Merged);

        replay.Kind.Should().Be(EngagementDecisionKind.None);
        replay.RoundId.Should().Be(first.RoundId);
        replay.ReasonCode.Should().Be("merged_close_blocked");
        fixture.Store.ListEngagementRounds(fixture.EngagementId).Should().ContainSingle();
    }

    [Fact]
    public async Task Completed_merged_close_is_not_admitted_again()
    {
        using var fixture = new Fixture();
        var first = await fixture.ObserveAsync("head-1", PrLifecycleState.Merged);
        fixture.Start(first);
        fixture
            .Store.TryTransitionEngagementRound(
                first.RoundId!.Value,
                EngagementRoundStatus.Running,
                EngagementRoundStatus.Completed,
                Start.AddMinutes(1)
            )
            .Should()
            .BeTrue();

        var replay = await fixture.ObserveAsync("head-1", PrLifecycleState.Merged);

        replay.Kind.Should().Be(EngagementDecisionKind.None);
        replay.ReasonCode.Should().Be("merged_close_completed");
        fixture.Store.ListEngagementRounds(fixture.EngagementId).Should().ContainSingle();
    }

    private sealed class Fixture : IDisposable
    {
        private readonly TempSqliteDatabase _database = new();
        public RepoIdentity Repo { get; } =
            new()
            {
                Provider = "github",
                OrgOrOwner = "achieveai",
                RepoName = "LmDotnetTools",
            };

        public Fixture()
        {
            Time = new FakeTimeProvider(PrEngagementCoordinatorTests.Start);
            Store = new ReviewStore(_database.ConnectionString, Time);
            RepoId = Store.EnsureRepo(Repo);
            Coordinator = new PrEngagementCoordinator(Store, Time);
        }

        public ReviewStore Store { get; }
        public FakeTimeProvider Time { get; }
        public PrEngagementCoordinator Coordinator { get; }
        public long RepoId { get; }
        public long EngagementId => Store.CreateOrGetEngagement(Seed()).Id;

        public Task<EngagementDecision> ObserveAsync(
            string head,
            PrLifecycleState lifecycle = PrLifecycleState.Open,
            ProviderDiscussionRef? activity = null
        ) =>
            Coordinator.ObserveAsync(
                RepoId,
                Repo,
                Descriptor(head, lifecycle),
                Snapshot(head, lifecycle, activity),
                CancellationToken.None
            );

        public PullRequestDescriptor Descriptor(string head, PrLifecycleState lifecycle)
        {
            var fallback = FallbackWatermark();
            return new PullRequestDescriptor
            {
                PrId = "118",
                HeadSha = head,
                BaseSha = "base-1",
                TriggerWatermark = fallback.StableObjectId,
                LifecycleState = lifecycle,
            };
        }

        public ProviderEngagementSnapshot Snapshot(
            string head,
            PrLifecycleState lifecycle,
            ProviderDiscussionRef? activity = null
        ) =>
            ProviderEngagementSnapshot.Create(
                lifecycle,
                head,
                "base-1",
                FallbackWatermark(),
                activity is null ? [] : [activity]
            );

        private ProviderActivityWatermark FallbackWatermark() =>
            new("github", Time.GetUtcNow(), $"poll:{Time.GetUtcNow().ToUnixTimeMilliseconds()}");

        public ProviderDiscussionRef Comment(
            string objectId,
            DateTimeOffset publishedAt,
            ProviderDiscussionKind kind = ProviderDiscussionKind.Comment
        ) =>
            new(
                "github",
                "thread-1",
                objectId,
                objectId,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                publishedAt,
                "developer",
                "body",
                kind
            );

        public void Start(EngagementDecision decision) =>
            Store
                .TryTransitionEngagementRound(
                    decision.RoundId!.Value,
                    EngagementRoundStatus.Pending,
                    EngagementRoundStatus.Running,
                    Time.GetUtcNow()
                )
                .Should()
                .BeTrue();

        public void Complete(EngagementDecision decision, DateTimeOffset completedAt)
        {
            Start(decision);
            Store
                .TryTransitionEngagementRound(
                    decision.RoundId!.Value,
                    EngagementRoundStatus.Running,
                    EngagementRoundStatus.Completed,
                    completedAt
                )
                .Should()
                .BeTrue();
        }

        public void Dispose()
        {
            Store.Dispose();
            _database.Dispose();
        }

        private PrEngagement Seed() =>
            new(
                0,
                RepoId,
                "github",
                "118",
                PrLifecycleState.Open,
                "head-1",
                "base-1",
                null,
                new ProviderActivityWatermark("github", PrEngagementCoordinatorTests.Start, "initial"),
                new ProviderActivityWatermark("github", PrEngagementCoordinatorTests.Start, "initial"),
                null,
                null,
                null,
                null,
                null,
                PrEngagementCoordinatorTests.Start
            );
    }
}
