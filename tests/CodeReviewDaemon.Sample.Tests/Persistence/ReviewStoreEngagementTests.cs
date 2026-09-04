using CodeReviewDaemon.Sample.Persistence;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Tests.Infrastructure;

namespace CodeReviewDaemon.Sample.Tests.Persistence;

public sealed class ReviewStoreEngagementTests
{
    private static readonly DateTimeOffset ObservedAt = new(2026, 9, 2, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void CreateOrGetEngagement_is_idempotent_for_provider_repo_and_pr()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var repoId = store.EnsureRepo(SampleRepo());

        var first = store.CreateOrGetEngagement(SampleEngagement(repoId));
        var second = store.CreateOrGetEngagement(
            SampleEngagement(repoId) with
            {
                LatestHeadSha = "head-later",
                LatestActivity = Watermark("comment-2", ObservedAt.AddMinutes(1)),
            }
        );

        second.Id.Should().Be(first.Id);
        store
            .GetEngagement(first.Id)!
            .LatestHeadSha.Should()
            .Be("head-1", "creation is idempotent, not an observation update");
    }

    [Fact]
    public void Engagement_watermarks_and_cooldown_round_trip()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var repoId = store.EnsureRepo(SampleRepo());
        var completedAt = ObservedAt.AddHours(-1);
        var eligibleAt = ObservedAt;

        var created = store.CreateOrGetEngagement(
            SampleEngagement(repoId) with
            {
                LastReviewedHeadSha = "head-0",
                ConsumedActivity = Watermark("comment-0", ObservedAt.AddHours(-2)),
                LastCompletedAt = completedAt,
                NextEligibleAt = eligibleAt,
                RootSummaryReceiptJson = "{\"providerResponseId\":\"summary-1\"}",
            }
        );

        var reloaded = store.GetEngagement(created.Id)!;
        reloaded.LastReviewedHeadSha.Should().Be("head-0");
        reloaded.LatestActivity.Should().Be(Watermark("comment-1", ObservedAt));
        reloaded.ConsumedActivity.Should().Be(Watermark("comment-0", ObservedAt.AddHours(-2)));
        reloaded.LastCompletedAt.Should().Be(completedAt);
        reloaded.NextEligibleAt.Should().Be(eligibleAt);
        reloaded.RootSummaryReceiptJson.Should().Be("{\"providerResponseId\":\"summary-1\"}");
    }

    [Fact]
    public void UpdateEngagementObservation_coalesces_latest_state_and_never_regresses_activity()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var engagement = SeedEngagement(store);
        var newer = Watermark("comment-2", ObservedAt.AddMinutes(2));
        var sameTimestampHigherId = Watermark("comment-3", newer.PublishedAt);

        store.UpdateEngagementObservation(
            engagement.Id,
            PrLifecycleState.Open,
            "head-2",
            "base-2",
            newer,
            ObservedAt.AddMinutes(2)
        );
        store.UpdateEngagementObservation(
            engagement.Id,
            PrLifecycleState.Merged,
            "head-merged",
            "base-3",
            sameTimestampHigherId,
            ObservedAt.AddMinutes(3)
        );
        store.UpdateEngagementObservation(
            engagement.Id,
            PrLifecycleState.Merged,
            "head-merged",
            "base-3",
            Watermark("comment-1", newer.PublishedAt),
            ObservedAt.AddMinutes(4)
        );

        var reloaded = store.GetEngagement(engagement.Id)!;
        reloaded.Lifecycle.Should().Be(PrLifecycleState.Merged);
        reloaded.LatestHeadSha.Should().Be("head-merged");
        reloaded.LatestBaseSha.Should().Be("base-3");
        reloaded
            .LatestActivity.Should()
            .Be(sameTimestampHigherId, "the stable object ID orders provider activity with identical timestamps");
        reloaded.UpdatedAt.Should().Be(ObservedAt.AddMinutes(4));
    }

    [Fact]
    public void UpdateEngagementObservation_does_not_regress_state_from_an_older_observation()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var engagement = SeedEngagement(store);
        var mergedAt = ObservedAt.AddMinutes(2);

        store.UpdateEngagementObservation(
            engagement.Id,
            PrLifecycleState.Merged,
            "head-merged",
            "base-2",
            Watermark("comment-2", mergedAt),
            mergedAt
        );
        store.UpdateEngagementObservation(
            engagement.Id,
            PrLifecycleState.Open,
            "head-old",
            "base-old",
            Watermark("comment-old", ObservedAt.AddMinutes(1)),
            ObservedAt.AddMinutes(1)
        );

        var reloaded = store.GetEngagement(engagement.Id)!;
        reloaded.Lifecycle.Should().Be(PrLifecycleState.Merged);
        reloaded.LatestHeadSha.Should().Be("head-merged");
        reloaded.LatestBaseSha.Should().Be("base-2");
        reloaded.LatestActivity.Should().Be(Watermark("comment-2", mergedAt));
        reloaded.UpdatedAt.Should().Be(mergedAt);
    }

    [Fact]
    public void TryAdmitRound_refuses_invalid_metadata_instead_of_reporting_lease_contention()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);

        var missingEngagement = () => store.TryAdmitRound(SampleRound(999));
        var negativeFailureCount = () =>
            store.TryAdmitRound(SampleRound(SeedEngagement(store).Id) with { GovernedFailureCount = -1 });

        missingEngagement.Should().Throw<Microsoft.Data.Sqlite.SqliteException>();
        negativeFailureCount.Should().Throw<Microsoft.Data.Sqlite.SqliteException>();
    }

    [Fact]
    public void TryAdmitRound_allows_only_one_active_round_per_engagement()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var engagement = SeedEngagement(store);

        var first = store.TryAdmitRound(SampleRound(engagement.Id));
        var competing = store.TryAdmitRound(
            SampleRound(engagement.Id) with
            {
                HeadSha = "head-2",
                Intent = EngagementRoundIntent.DiscussionFollowUp,
            }
        );

        first.Should().NotBeNull();
        competing.Should().BeNull("the partial unique index permits one Pending, Running, or RetryPending round");
        store.GetEngagement(engagement.Id)!.ActiveRoundId.Should().Be(first!.Id);
        store.GetEngagement(engagement.Id)!.LatestRoundId.Should().Be(first.Id);
    }

    [Fact]
    public async Task Concurrent_TryAdmitRound_calls_create_one_active_round()
    {
        using var db = new TempSqliteDatabase();
        using var firstStore = new ReviewStore(db.ConnectionString);
        using var secondStore = new ReviewStore(db.ConnectionString);
        var engagement = SeedEngagement(firstStore);
        using var start = new Barrier(3);

        var attempts = new[]
        {
            Task.Run(() =>
            {
                start.SignalAndWait();
                return firstStore.TryAdmitRound(SampleRound(engagement.Id) with { HeadSha = "head-1" });
            }),
            Task.Run(() =>
            {
                start.SignalAndWait();
                return secondStore.TryAdmitRound(SampleRound(engagement.Id) with { HeadSha = "head-2" });
            }),
        };

        start.SignalAndWait();
        var results = await Task.WhenAll(attempts);

        results.Count(round => round != null).Should().Be(1);
        firstStore
            .ListEngagementRounds(engagement.Id)
            .Should()
            .ContainSingle(round => round.Status == EngagementRoundStatus.Pending);
    }

    [Fact]
    public void Pending_round_can_run_and_complete_and_sets_code_review_cooldown()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var engagement = SeedEngagement(store);
        var pending = store.TryAdmitRound(SampleRound(engagement.Id))!;
        var startedAt = ObservedAt.AddMinutes(2);
        var completedAt = ObservedAt.AddMinutes(10);

        store
            .TryTransitionEngagementRound(
                pending.Id,
                EngagementRoundStatus.Pending,
                EngagementRoundStatus.Running,
                startedAt
            )
            .Should()
            .BeTrue();
        store
            .TryTransitionEngagementRound(
                pending.Id,
                EngagementRoundStatus.Running,
                EngagementRoundStatus.Completed,
                completedAt
            )
            .Should()
            .BeTrue();

        var round = store.GetEngagementRound(pending.Id)!;
        round.Status.Should().Be(EngagementRoundStatus.Completed);
        round.StartedAt.Should().Be(startedAt);
        round.CompletedAt.Should().Be(completedAt);
        var reloaded = store.GetEngagement(engagement.Id)!;
        reloaded.ActiveRoundId.Should().BeNull();
        reloaded.LastReviewedHeadSha.Should().Be(pending.HeadSha);
        reloaded.LastCompletedAt.Should().Be(completedAt);
        reloaded.NextEligibleAt.Should().Be(completedAt.AddHours(1));
        pending.ActivityUpperBound.Should().NotBeNull();
        reloaded.ConsumedActivity.Should().Be(pending.ActivityUpperBound!);

        var nextRound = store.TryAdmitRound(
            SampleRound(engagement.Id) with
            {
                Intent = EngagementRoundIntent.DiscussionFollowUp,
                ActivityLowerBound = pending.ActivityUpperBound,
                ActivityUpperBound = Watermark("comment-2", completedAt.AddMinutes(1)),
            }
        );
        nextRound.Should().NotBeNull("a terminal round releases the partial active-round lease");
        nextRound!.Id.Should().NotBe(pending.Id);
        store.ListEngagementRounds(engagement.Id).Should().HaveCount(2);
    }

    [Theory]
    [InlineData("Superseded")]
    [InlineData("Parked")]
    public void A_terminal_noncompleted_round_releases_the_lease(string terminalStatusName)
    {
        var terminalStatus = Enum.Parse<EngagementRoundStatus>(terminalStatusName);
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var engagement = SeedEngagement(store);
        var first = store.TryAdmitRound(SampleRound(engagement.Id))!;
        store
            .TryTransitionEngagementRound(
                first.Id,
                EngagementRoundStatus.Pending,
                EngagementRoundStatus.Running,
                ObservedAt.AddMinutes(1)
            )
            .Should()
            .BeTrue();

        store
            .TryTransitionEngagementRound(
                first.Id,
                EngagementRoundStatus.Running,
                terminalStatus,
                ObservedAt.AddMinutes(2)
            )
            .Should()
            .BeTrue();

        var next = store.TryAdmitRound(SampleRound(engagement.Id) with { HeadSha = "head-2" });
        next.Should().NotBeNull();
        next!.Id.Should().NotBe(first.Id);
    }

    [Fact]
    public void Discussion_completion_sets_one_hour_cooldown_without_changing_last_reviewed_head()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var repoId = store.EnsureRepo(SampleRepo());
        var engagement = store.CreateOrGetEngagement(SampleEngagement(repoId) with { LastReviewedHeadSha = "head-0" });
        var pending = store.TryAdmitRound(
            SampleRound(engagement.Id) with
            {
                Intent = EngagementRoundIntent.DiscussionFollowUp,
            }
        )!;
        var completedAt = ObservedAt.AddMinutes(8);
        store
            .TryTransitionEngagementRound(
                pending.Id,
                EngagementRoundStatus.Pending,
                EngagementRoundStatus.Running,
                ObservedAt.AddMinutes(1)
            )
            .Should()
            .BeTrue();

        store
            .TryTransitionEngagementRound(
                pending.Id,
                EngagementRoundStatus.Running,
                EngagementRoundStatus.Completed,
                completedAt
            )
            .Should()
            .BeTrue();

        var reloaded = store.GetEngagement(engagement.Id)!;
        reloaded.LastReviewedHeadSha.Should().Be("head-0");
        reloaded.LastCompletedAt.Should().Be(completedAt);
        reloaded.NextEligibleAt.Should().Be(completedAt.AddHours(1));
    }

    [Fact]
    public void Merged_close_completion_does_not_start_another_cooldown()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var repoId = store.EnsureRepo(SampleRepo());
        var engagement = store.CreateOrGetEngagement(
            SampleEngagement(repoId) with
            {
                Lifecycle = PrLifecycleState.Merged,
                NextEligibleAt = ObservedAt.AddMinutes(-1),
            }
        );
        var pending = store.TryAdmitRound(
            SampleRound(engagement.Id) with
            {
                Intent = EngagementRoundIntent.MergedClose,
            }
        )!;
        store
            .TryTransitionEngagementRound(
                pending.Id,
                EngagementRoundStatus.Pending,
                EngagementRoundStatus.Running,
                ObservedAt.AddMinutes(1)
            )
            .Should()
            .BeTrue();

        store
            .TryTransitionEngagementRound(
                pending.Id,
                EngagementRoundStatus.Running,
                EngagementRoundStatus.Completed,
                ObservedAt.AddMinutes(5)
            )
            .Should()
            .BeTrue();

        var reloaded = store.GetEngagement(engagement.Id)!;
        reloaded.Lifecycle.Should().Be(PrLifecycleState.Merged);
        reloaded.NextEligibleAt.Should().BeNull("merged-close work bypasses and ends ordinary eligibility");
        reloaded.LastCompletedAt.Should().Be(ObservedAt.AddMinutes(5));
    }

    [Fact]
    public void Running_round_can_be_superseded_without_charging_its_failure_budget()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var engagement = SeedEngagement(store);
        var pending = store.TryAdmitRound(SampleRound(engagement.Id) with { GovernedFailureCount = 2 })!;
        store
            .TryTransitionEngagementRound(
                pending.Id,
                EngagementRoundStatus.Pending,
                EngagementRoundStatus.Running,
                ObservedAt.AddMinutes(1)
            )
            .Should()
            .BeTrue();

        store
            .TryTransitionEngagementRound(
                pending.Id,
                EngagementRoundStatus.Running,
                EngagementRoundStatus.Superseded,
                ObservedAt.AddMinutes(3)
            )
            .Should()
            .BeTrue();

        var round = store.GetEngagementRound(pending.Id)!;
        round.Status.Should().Be(EngagementRoundStatus.Superseded);
        round.SupersededAt.Should().Be(ObservedAt.AddMinutes(3));
        round.GovernedFailureCount.Should().Be(2);
        var reloaded = store.GetEngagement(round.PrEngagementId)!;
        reloaded.ActiveRoundId.Should().BeNull();
        reloaded.NextEligibleAt.Should().BeNull();
        reloaded.LastCompletedAt.Should().BeNull();
        reloaded.ConsumedActivity.Should().BeNull();
    }

    [Fact]
    public void Completed_round_cannot_return_to_running()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var pending = store.TryAdmitRound(SampleRound(SeedEngagement(store).Id))!;
        store
            .TryTransitionEngagementRound(
                pending.Id,
                EngagementRoundStatus.Pending,
                EngagementRoundStatus.Running,
                ObservedAt.AddMinutes(1)
            )
            .Should()
            .BeTrue();
        store
            .TryTransitionEngagementRound(
                pending.Id,
                EngagementRoundStatus.Running,
                EngagementRoundStatus.Completed,
                ObservedAt.AddMinutes(2)
            )
            .Should()
            .BeTrue();

        store
            .TryTransitionEngagementRound(
                pending.Id,
                EngagementRoundStatus.Completed,
                EngagementRoundStatus.Running,
                ObservedAt.AddMinutes(3)
            )
            .Should()
            .BeFalse();
        store.GetEngagementRound(pending.Id)!.Status.Should().Be(EngagementRoundStatus.Completed);
    }

    [Fact]
    public void Transition_compare_and_swap_rejects_a_stale_expected_status()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var engagement = SeedEngagement(store);
        var pending = store.TryAdmitRound(SampleRound(engagement.Id))!;
        var startedAt = ObservedAt.AddMinutes(1);
        store
            .TryTransitionEngagementRound(
                pending.Id,
                EngagementRoundStatus.Pending,
                EngagementRoundStatus.Running,
                startedAt
            )
            .Should()
            .BeTrue();

        store
            .TryTransitionEngagementRound(
                pending.Id,
                EngagementRoundStatus.Pending,
                EngagementRoundStatus.Running,
                ObservedAt.AddMinutes(2)
            )
            .Should()
            .BeFalse();
        store.GetEngagementRound(pending.Id)!.StartedAt.Should().Be(startedAt);

        store
            .TryTransitionEngagementRound(
                pending.Id,
                EngagementRoundStatus.Running,
                EngagementRoundStatus.Superseded,
                ObservedAt.AddMinutes(3)
            )
            .Should()
            .BeTrue();
        store
            .TryTransitionEngagementRound(
                pending.Id,
                EngagementRoundStatus.Running,
                EngagementRoundStatus.Completed,
                ObservedAt.AddMinutes(4)
            )
            .Should()
            .BeFalse();
        store.GetEngagement(engagement.Id)!.NextEligibleAt.Should().BeNull();
    }

    [Fact]
    public void Round_governed_failure_budget_increments_atomically_and_parks_once()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var engagement = SeedEngagement(store);
        var round = store.TryAdmitRound(SampleRound(engagement.Id))!;
        store
            .TryTransitionEngagementRound(
                round.Id,
                EngagementRoundStatus.Pending,
                EngagementRoundStatus.Running,
                ObservedAt
            )
            .Should()
            .BeTrue();

        store.IncrementEngagementRoundFailureCount(round.Id).Should().Be(1);
        store.IncrementEngagementRoundFailureCount(round.Id).Should().Be(2);
        store
            .TryParkEngagementRound(
                round.Id,
                EngagementRoundStatus.Running,
                ObservedAt.AddMinutes(1),
                "executor_failure_budget_exhausted"
            )
            .Should()
            .BeTrue();
        store
            .TryParkEngagementRound(round.Id, EngagementRoundStatus.Running, ObservedAt.AddMinutes(2), "different")
            .Should()
            .BeFalse();

        var parked = store.GetEngagementRound(round.Id)!;
        parked.Status.Should().Be(EngagementRoundStatus.Parked);
        parked.GovernedFailureCount.Should().Be(2);
        parked.ParkReason.Should().Be("executor_failure_budget_exhausted");
        store.GetEngagement(engagement.Id)!.ActiveRoundId.Should().BeNull();
    }

    [Fact]
    public void Review_run_can_link_to_a_code_review_round_without_changing_commit_identity()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var engagement = SeedEngagement(store);
        var round = store.TryAdmitRound(SampleRound(engagement.Id))!;
        var first = store.CreateOrGetReviewRun(SampleRun(engagement.RepoId) with { EngagementRoundId = round.Id });
        var sameCommit = store.CreateOrGetReviewRun(
            SampleRun(engagement.RepoId) with
            {
                EngagementRoundId = null,
                TriggerWatermark = "later",
                Mode = "post",
            }
        );

        sameCommit.Id.Should().Be(first.Id);
        store.GetReviewRun(first.Id)!.EngagementRoundId.Should().Be(round.Id);
        store.GetEngagementRound(round.Id)!.ReviewRunId.Should().Be(first.Id);
    }

    [Fact]
    public void Review_run_link_is_idempotent_for_the_same_code_review_round()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var engagement = SeedEngagement(store);
        var round = store.TryAdmitRound(SampleRound(engagement.Id))!;
        var requestedRun = SampleRun(engagement.RepoId) with { EngagementRoundId = round.Id };

        var first = store.CreateOrGetReviewRun(requestedRun);
        var repeated = store.CreateOrGetReviewRun(requestedRun);

        repeated.Id.Should().Be(first.Id);
        store.GetReviewRun(first.Id)!.EngagementRoundId.Should().Be(round.Id);
        store.GetEngagementRound(round.Id)!.ReviewRunId.Should().Be(first.Id);
    }

    [Fact]
    public void Discussion_round_refuses_a_review_run_link()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var engagement = SeedEngagement(store);
        var round = store.TryAdmitRound(
            SampleRound(engagement.Id) with
            {
                Intent = EngagementRoundIntent.DiscussionFollowUp,
            }
        )!;

        var link = () => store.CreateOrGetReviewRun(SampleRun(engagement.RepoId) with { EngagementRoundId = round.Id });

        link.Should().Throw<InvalidOperationException>().WithMessage("*CodeReview*");
        store.GetEngagementRound(round.Id)!.ReviewRunId.Should().BeNull();
    }

    [Fact]
    public void Review_run_cannot_be_reassigned_to_another_round()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var engagement = SeedEngagement(store);
        var firstRound = store.TryAdmitRound(SampleRound(engagement.Id))!;
        var run = store.CreateOrGetReviewRun(SampleRun(engagement.RepoId) with { EngagementRoundId = firstRound.Id });
        store
            .TryTransitionEngagementRound(
                firstRound.Id,
                EngagementRoundStatus.Pending,
                EngagementRoundStatus.Superseded,
                ObservedAt.AddMinutes(1)
            )
            .Should()
            .BeTrue();
        var secondRound = store.TryAdmitRound(SampleRound(engagement.Id) with { HeadSha = "head-2" })!;

        var reassign = () =>
            store.CreateOrGetReviewRun(SampleRun(engagement.RepoId) with { EngagementRoundId = secondRound.Id });

        reassign.Should().Throw<InvalidOperationException>();
        store.GetReviewRun(run.Id)!.EngagementRoundId.Should().Be(firstRound.Id);
    }

    [Fact]
    public void Engagement_identity_distinguishes_pr_and_repository()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var firstRepo = store.EnsureRepo(SampleRepo());
        var secondRepo = store.EnsureRepo(SampleRepo() with { RepoName = "OtherRepo" });

        var first = store.CreateOrGetEngagement(SampleEngagement(firstRepo));
        var otherPr = store.CreateOrGetEngagement(SampleEngagement(firstRepo) with { PrId = "119" });
        var otherRepo = store.CreateOrGetEngagement(SampleEngagement(secondRepo));

        new[] { first.Id, otherPr.Id, otherRepo.Id }.Should().OnlyHaveUniqueItems();
    }

    private static PrEngagement SeedEngagement(ReviewStore store)
    {
        var repoId = store.EnsureRepo(SampleRepo());
        return store.CreateOrGetEngagement(SampleEngagement(repoId));
    }

    private static PrEngagement SampleEngagement(long repoId) =>
        new(
            Id: 0,
            RepoId: repoId,
            Provider: "github",
            PrId: "118",
            Lifecycle: PrLifecycleState.Open,
            LatestHeadSha: "head-1",
            LatestBaseSha: "base-1",
            LastReviewedHeadSha: null,
            LatestActivity: Watermark("comment-1", ObservedAt),
            ConsumedActivity: null,
            LastCompletedAt: null,
            NextEligibleAt: null,
            ActiveRoundId: null,
            LatestRoundId: null,
            RootSummaryReceiptJson: null,
            UpdatedAt: ObservedAt
        );

    private static EngagementRound SampleRound(long engagementId) =>
        new(
            Id: 0,
            PrEngagementId: engagementId,
            Intent: EngagementRoundIntent.CodeReview,
            Status: EngagementRoundStatus.Pending,
            HeadSha: "head-1",
            BaseSha: "base-1",
            ActivityLowerBound: null,
            ActivityUpperBound: Watermark("comment-1", ObservedAt),
            PriorObservationBoundary: 0,
            ReviewRunId: null,
            GovernedFailureCount: 0,
            StartedAt: null,
            CompletedAt: null,
            SupersededAt: null,
            ParkedAt: null,
            ParkReason: null
        );

    private static ProviderActivityWatermark Watermark(string id, DateTimeOffset publishedAt) =>
        new("github", publishedAt, id);

    private static RepoIdentity SampleRepo() =>
        new()
        {
            Provider = "github",
            OrgOrOwner = "achieveai",
            RepoName = "LmDotnetTools",
        };

    private static ReviewRun SampleRun(long repoId) =>
        new()
        {
            RepoId = repoId,
            PrId = "118",
            HeadSha = "head-1",
            BaseSha = "base-1",
            TriggerWatermark = "wm-1",
            ReviewKind = "full",
            VariantId = "primary",
            Mode = "collect-only",
            Stage = ReviewStage.Discovered,
            WorkflowStatus = WorkflowStatus.Pending,
            PrLifecycleState = PrLifecycleState.Open,
        };
}
