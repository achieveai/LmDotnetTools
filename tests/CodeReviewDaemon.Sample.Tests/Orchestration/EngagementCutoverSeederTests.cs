using CodeReviewDaemon.Sample.Orchestration;
using CodeReviewDaemon.Sample.Persistence;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using Microsoft.Extensions.Time.Testing;

namespace CodeReviewDaemon.Sample.Tests.Orchestration;

public sealed class EngagementCutoverSeederTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Seeds_open_historical_pr_from_review_and_provider_truth()
    {
        using var fixture = new Fixture();
        var run = fixture.SeedReviewedRun("head-reviewed");
        var completedAt = Now.AddMinutes(-30);
        fixture.Store.MarkReviewPosted(run.Id, completedAt);
        fixture.Provider.Snapshot = fixture.Snapshot("head-current", PrLifecycleState.Open);

        var result = await fixture.Seeder.SeedAsync(CancellationToken.None);

        result.Completed.Should().BeTrue();
        result.SeededEngagements.Should().Be(1);
        var engagement = fixture.Store.GetEngagement("github", fixture.RepoId, "118")!;
        engagement.LastReviewedHeadSha.Should().Be("head-reviewed");
        engagement.LastCompletedAt.Should().Be(completedAt);
        engagement.NextEligibleAt.Should().Be(completedAt.AddHours(1));
        engagement.LatestHeadSha.Should().Be("head-current");
        engagement
            .ConsumedActivity.Should()
            .Be(
                engagement.LatestActivity,
                "a poll fallback with no demand-producing activity must not manufacture discussion work"
            );
        engagement.RootSummaryReceiptJson.Should().BeNull("no provider/outbox receipt was seeded");
        fixture.Seeder.IsComplete.Should().BeTrue();
    }

    [Fact]
    public async Task Adopts_root_summary_only_from_a_posted_review_outbox_receipt()
    {
        using var fixture = new Fixture();
        var run = fixture.SeedReviewedRun("head-reviewed");
        fixture.SeedPostedReviewReceipt(run, "issue-comment:777");
        fixture.Provider.Snapshot = fixture.Snapshot("head-current", PrLifecycleState.Open);

        await fixture.Seeder.SeedAsync(CancellationToken.None);

        var engagement = fixture.Store.GetEngagement("github", fixture.RepoId, "118")!;
        engagement.RootSummaryReceiptJson.Should().Contain("issue-comment:777");
        engagement.RootSummaryReceiptJson.Should().Contain("github");
    }

    [Fact]
    public async Task Records_explicit_legacy_source_gaps_on_a_completed_historical_round()
    {
        using var fixture = new Fixture();
        var run = fixture.SeedReviewedRun("head-reviewed");
        fixture.Provider.Snapshot = fixture.Snapshot("head-current", PrLifecycleState.Open);

        await fixture.Seeder.SeedAsync(CancellationToken.None);

        var engagement = fixture.Store.GetEngagement("github", fixture.RepoId, "118")!;
        var round = fixture.Store.ListEngagementRounds(engagement.Id).Should().ContainSingle().Subject;
        round.Status.Should().Be(EngagementRoundStatus.Completed);
        round.Intent.Should().Be(EngagementRoundIntent.CodeReview);
        round.ReviewRunId.Should().Be(run.Id);
        fixture
            .Store.ListAuditRecordsForRound(round.Id)
            .Should()
            .HaveCount(3)
            .And.OnlyContain(record => record.CaptureOutcome == AuditSourceCaptureOutcome.Gap);
    }

    [Fact]
    public async Task Resumes_an_engagement_created_before_the_global_marker()
    {
        using var fixture = new Fixture();
        var run = fixture.SeedReviewedRun("head-reviewed");
        fixture.Provider.Snapshot = fixture.Snapshot("head-current", PrLifecycleState.Open);
        _ = fixture.Store.CreateOrGetEngagement(
            new PrEngagement(
                0,
                fixture.RepoId,
                "github",
                "118",
                PrLifecycleState.Open,
                "head-current",
                "base-1",
                null,
                fixture.Provider.Snapshot.LatestObserved,
                null,
                null,
                null,
                null,
                null,
                null,
                Now.AddMinutes(-1)
            )
        );

        var result = await fixture.Seeder.SeedAsync(CancellationToken.None);

        result.Completed.Should().BeTrue();
        var engagement = fixture.Store.GetEngagement("github", fixture.RepoId, "118")!;
        engagement.LastReviewedHeadSha.Should().Be(run.HeadSha);
        fixture.Store.ListEngagementRounds(engagement.Id).Should().ContainSingle();
        fixture.Store.ListAuditRecordsForRound(engagement.LatestRoundId!.Value).Should().HaveCount(3);
    }

    [Fact]
    public async Task Resumes_after_shadow_admission_without_discarding_historical_evidence()
    {
        using var fixture = new Fixture();
        var run = fixture.SeedReviewedRun("head-reviewed");
        fixture.Provider.Snapshot = fixture.Snapshot("head-current", PrLifecycleState.Open);
        var engagement = fixture.Store.CreateOrGetEngagement(
            new PrEngagement(
                0,
                fixture.RepoId,
                "github",
                "118",
                PrLifecycleState.Open,
                "head-current",
                "base-1",
                null,
                fixture.Provider.Snapshot.LatestObserved,
                null,
                null,
                null,
                null,
                null,
                null,
                Now.AddMinutes(-1)
            )
        );
        _ = fixture.Store.TryAdmitRound(
            new EngagementRound(
                0,
                engagement.Id,
                EngagementRoundIntent.CodeReview,
                EngagementRoundStatus.Pending,
                "head-current",
                "base-1",
                null,
                fixture.Provider.Snapshot.LatestObserved,
                0,
                null,
                0,
                null,
                null,
                null,
                null,
                null
            )
        );

        var result = await fixture.Seeder.SeedAsync(CancellationToken.None);

        result.Completed.Should().BeTrue();
        var reloaded = fixture.Store.GetEngagement(engagement.Id)!;
        reloaded.LastReviewedHeadSha.Should().Be(run.HeadSha);
        fixture.Store.ListEngagementRounds(engagement.Id).Should().Contain(round => round.ReviewRunId == run.Id);
    }

    [Fact]
    public async Task Own_post_is_excluded_and_does_not_become_unconsumed_discussion_demand()
    {
        using var fixture = new Fixture();
        var run = fixture.SeedReviewedRun("head-reviewed");
        fixture.SeedPostedReviewReceipt(run, "issue-comment:777");
        fixture.Provider.Snapshot = fixture.Snapshot("head-reviewed", PrLifecycleState.Open);

        await fixture.Seeder.SeedAsync(CancellationToken.None);

        var engagement = fixture.Store.GetEngagement("github", fixture.RepoId, "118")!;
        fixture.Provider.LastDaemonReceiptIds.Should().ContainSingle("issue-comment:777");
        engagement.ConsumedActivity.Should().Be(engagement.LatestActivity);
        var coordinator = new PrEngagementCoordinator(fixture.Store, new FakeTimeProvider(Now.AddHours(2)));
        var decision = await coordinator.ObserveAsync(
            fixture.RepoId,
            fixture.Repo,
            new PullRequestDescriptor
            {
                PrId = "118",
                HeadSha = "head-reviewed",
                BaseSha = "base-1",
                TriggerWatermark = "poll:2",
                LifecycleState = PrLifecycleState.Open,
            },
            fixture.Provider.Snapshot,
            CancellationToken.None
        );
        decision.Kind.Should().Be(EngagementDecisionKind.None);
    }

    [Fact]
    public async Task Running_seeder_twice_is_state_idempotent_and_preserves_global_marker()
    {
        using var fixture = new Fixture();
        fixture.SeedReviewedRun("head-reviewed");
        fixture.Provider.Snapshot = fixture.Snapshot("head-current", PrLifecycleState.Open);

        var first = await fixture.Seeder.SeedAsync(CancellationToken.None);
        var engagement = fixture.Store.GetEngagement("github", fixture.RepoId, "118")!;
        var rounds = fixture.Store.ListEngagementRounds(engagement.Id);
        var gaps = fixture.Store.ListAuditRecordsForRound(rounds.Single().Id);
        var second = await fixture.Seeder.SeedAsync(CancellationToken.None);
        var reloaded = fixture.Store.GetEngagement(engagement.Id)!;

        first.Completed.Should().BeTrue();
        second.Completed.Should().BeTrue();
        second.SeededEngagements.Should().Be(0);
        reloaded.Should().Be(engagement);
        fixture.Store.ListEngagementRounds(engagement.Id).Should().Equal(rounds);
        fixture.Store.ListAuditRecordsForRound(rounds.Single().Id).Should().Equal(gaps);
        fixture.Seeder.IsComplete.Should().BeTrue();
    }

    [Theory]
    [InlineData("Merged")]
    [InlineData("Abandoned")]
    public async Task Historical_non_open_pr_is_not_seeded(string lifecycleName)
    {
        using var fixture = new Fixture();
        var lifecycle = Enum.Parse<PrLifecycleState>(lifecycleName);
        fixture.SeedReviewedRun("head-reviewed");
        fixture.Provider.Snapshot = fixture.Snapshot("head-current", lifecycle);

        var result = await fixture.Seeder.SeedAsync(CancellationToken.None);

        result.SeededEngagements.Should().Be(0);
        fixture.Store.GetEngagement("github", fixture.RepoId, "118").Should().BeNull();
        result.Completed.Should().BeTrue();
    }

    private sealed class Fixture : IDisposable
    {
        private readonly TempSqliteDatabase _database = new();
        public readonly RepoIdentity Repo = new()
        {
            Provider = "github",
            OrgOrOwner = "achieveai",
            RepoName = "LmDotnetTools",
        };

        public Fixture()
        {
            Store = new ReviewStore(_database.ConnectionString, new FakeTimeProvider(Now));
            RepoId = Store.EnsureRepo(Repo);
            Provider = new SnapshotProvider();
            Seeder = new EngagementCutoverSeeder(Store, [Provider], new FakeTimeProvider(Now));
        }

        public ReviewStore Store { get; }
        public long RepoId { get; }
        public SnapshotProvider Provider { get; }
        public EngagementCutoverSeeder Seeder { get; }

        public ReviewRun SeedReviewedRun(string head) =>
            Store.CreateOrGetReviewRun(
                new ReviewRun
                {
                    RepoId = RepoId,
                    PrId = "118",
                    HeadSha = head,
                    BaseSha = "base-1",
                    TriggerWatermark = "wm-1",
                    ReviewKind = "full",
                    VariantId = "primary",
                    Mode = "collect-only",
                    Stage = ReviewStage.Reviewed,
                    WorkflowStatus = WorkflowStatus.Completed,
                    PrLifecycleState = PrLifecycleState.Open,
                }
            );

        public void SeedPostedReviewReceipt(ReviewRun run, string providerResponseId)
        {
            var entry = Store.EnqueueOutbox(
                new OutboxEntry
                {
                    IdempotencyKey = $"seed-{run.Id}",
                    Provider = "github",
                    ReviewRunId = run.Id,
                    Operation = ReviewPoster.PostReviewCommentOperation,
                    ArtifactKind = DaemonReviewStageExecutor.ReviewArtifactKind,
                    Status = OutboxStatus.Pending,
                }
            );
            Store
                .TryTransitionOutbox(entry.Id, OutboxStatus.Pending, OutboxStatus.Posted, providerResponseId)
                .Should()
                .BeTrue();
        }

        public ProviderEngagementSnapshot Snapshot(string head, PrLifecycleState lifecycle) =>
            ProviderEngagementSnapshot.Create(
                lifecycle,
                head,
                "base-1",
                new ProviderActivityWatermark("github", Now, "review-comment:latest"),
                []
            );

        public void Dispose()
        {
            Store.Dispose();
            _database.Dispose();
        }
    }

    private sealed class SnapshotProvider : IPrProvider
    {
        public string Provider => "github";
        public ProviderEngagementSnapshot Snapshot { get; set; } = null!;
        public IReadOnlySet<string> LastDaemonReceiptIds { get; private set; } = new HashSet<string>();

        public Task<ProviderEngagementSnapshot> GetEngagementSnapshotAsync(
            RepoIdentity repo,
            string prId,
            ProviderActivityWatermark? after,
            IReadOnlySet<string> daemonReceiptIds,
            CancellationToken cancellationToken
        )
        {
            LastDaemonReceiptIds = daemonReceiptIds;
            return Task.FromResult(Snapshot);
        }

        public Task<PullRequestPage> ListOpenPullRequestsAsync(
            PrPollRequest request,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task<PrLifecycle> GetPrStateAsync(RepoIdentity repo, string prId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<string?> GetCurrentHeadShaAsync(
            RepoIdentity repo,
            string prId,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();
    }
}
