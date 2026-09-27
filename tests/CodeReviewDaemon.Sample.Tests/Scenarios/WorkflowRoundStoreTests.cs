using CodeReviewDaemon.Sample.Persistence;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Tests.Infrastructure;

namespace CodeReviewDaemon.Sample.Tests.Scenarios;

public sealed class WorkflowRoundStoreTests
{
    [Fact]
    public void Repeated_discussion_event_reuses_the_immutable_frozen_window()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var repoId = store.EnsureRepo(SampleRepo());

        var first = store.CreateOrGetWorkflowRound(
            Discussion(repoId, "messages-108-112", "{\"through\":\"112\",\"messages\":[108,112]}")
        );
        var repeated = store.CreateOrGetWorkflowRound(
            Discussion(repoId, "messages-108-112", "{\"through\":\"113\",\"messages\":[108,112,113]}")
        );

        repeated.Id.Should().Be(first.Id);
        repeated.FrozenInputJson.Should().Be("{\"through\":\"112\",\"messages\":[108,112]}");
    }

    [Fact]
    public void Later_discussion_window_on_the_same_head_is_a_distinct_round()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var repoId = store.EnsureRepo(SampleRepo());

        var first = store.CreateOrGetWorkflowRound(Discussion(repoId, "messages-108-112", "{\"through\":112}"));
        var later = store.CreateOrGetWorkflowRound(Discussion(repoId, "messages-113-115", "{\"through\":115}"));

        later.Id.Should().NotBe(first.Id);
    }

    [Fact]
    public void Stable_merge_event_reuses_one_merged_round_but_does_not_collide_with_discussion()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var repoId = store.EnsureRepo(SampleRepo());

        var first = store.CreateOrGetWorkflowRound(Merged(repoId, "merge-sha-1"));
        var repeated = store.CreateOrGetWorkflowRound(Merged(repoId, "merge-sha-1"));
        var discussion = store.CreateOrGetWorkflowRound(
            Discussion(repoId, "merge-sha-1", "{\"window\":\"merge-sha-1\"}")
        );

        repeated.Id.Should().Be(first.Id);
        discussion.Id.Should().NotBe(first.Id);
    }

    [Fact]
    public void Round_and_workflow_binding_survive_reopen_and_conflicting_binding_is_rejected()
    {
        using var db = new TempSqliteDatabase();
        long roundId;
        using (var store = new ReviewStore(db.ConnectionString))
        {
            var repoId = store.EnsureRepo(SampleRepo());
            roundId = store.CreateOrGetWorkflowRound(Discussion(repoId, "window-1", "{\"window\":1}")).Id;
            store.TryBindWorkflowInstance(roundId, "workflow-1").Should().BeTrue();
            store.TryBindWorkflowInstance(roundId, "workflow-1").Should().BeTrue("replay is idempotent");
            store.TryBindWorkflowInstance(roundId, "workflow-2").Should().BeFalse();
        }

        using var reopened = new ReviewStore(db.ConnectionString);
        var round = reopened.GetWorkflowRound(roundId);
        round.Should().NotBeNull();
        round!.WorkflowInstanceId.Should().Be("workflow-1");
        round.FrozenInputJson.Should().Be("{\"window\":1}");
    }

    [Fact]
    public void Failed_and_unknown_outcomes_remain_pending_until_success_completes_the_round()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var repoId = store.EnsureRepo(SampleRepo());
        var round = store.CreateOrGetWorkflowRound(Discussion(repoId, "window-1", "{\"window\":1}"));
        store.TryBindWorkflowInstance(round.Id, "workflow-1").Should().BeTrue();

        store.RecordWorkflowOutcome(round.Id, "workflow-1", WorkflowRoundOutcome.Running).Should().BeTrue();
        store.RecordWorkflowOutcome(round.Id, "workflow-1", WorkflowRoundOutcome.Unknown).Should().BeTrue();
        store.GetWorkflowRound(round.Id)!.Outcome.Should().Be(WorkflowRoundOutcome.Unknown);
        store.GetPendingWorkflowRounds().Select(item => item.Id).Should().Contain(round.Id);

        store.RecordWorkflowOutcome(round.Id, "workflow-1", WorkflowRoundOutcome.Failed).Should().BeTrue();
        store.GetWorkflowRound(round.Id)!.Outcome.Should().Be(WorkflowRoundOutcome.Failed);
        store.GetPendingWorkflowRounds(repoId).Select(item => item.Id).Should().Contain(round.Id);

        store.RecordWorkflowOutcome(round.Id, "workflow-1", WorkflowRoundOutcome.Succeeded).Should().BeTrue();
        store.GetWorkflowRound(round.Id)!.CompletedAt.Should().NotBeNull();
        store.GetPendingWorkflowRounds().Select(item => item.Id).Should().NotContain(round.Id);

        store
            .RecordWorkflowOutcome(round.Id, "workflow-1", WorkflowRoundOutcome.Succeeded)
            .Should()
            .BeTrue("success replay is idempotent");
        store.RecordWorkflowOutcome(round.Id, "workflow-1", WorkflowRoundOutcome.Unknown).Should().BeFalse();
        store.GetWorkflowRound(round.Id)!.Outcome.Should().Be(WorkflowRoundOutcome.Succeeded);
    }

    [Fact]
    public void Pending_round_listing_is_ordered_and_can_be_scoped_to_one_repository()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var firstRepo = store.EnsureRepo(SampleRepo());
        var secondRepo = store.EnsureRepo(SampleRepo() with { RepoName = "OtherRepo", RepoStableId = "R_other" });

        var first = store.CreateOrGetWorkflowRound(Discussion(firstRepo, "window-1", "{\"window\":1}"));
        var second = store.CreateOrGetWorkflowRound(Discussion(firstRepo, "window-2", "{\"window\":2}"));
        _ = store.CreateOrGetWorkflowRound(Discussion(secondRepo, "window-3", "{\"window\":3}"));

        store.GetPendingWorkflowRounds(firstRepo).Select(item => item.Id).Should().Equal(first.Id, second.Id);
    }

    [Fact]
    public void Latest_round_lookup_includes_completed_rounds_and_respects_kind()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var repoId = store.EnsureRepo(SampleRepo());
        var first = store.CreateOrGetWorkflowRound(Discussion(repoId, "window-1", "{\"window\":1}"));
        store.TryBindWorkflowInstance(first.Id, "workflow-1").Should().BeTrue();
        store.RecordWorkflowOutcome(first.Id, "workflow-1", WorkflowRoundOutcome.Succeeded).Should().BeTrue();
        var latest = store.CreateOrGetWorkflowRound(Discussion(repoId, "window-2", "{\"window\":2}"));
        _ = store.CreateOrGetWorkflowRound(Merged(repoId, "merge-1"));

        store
            .GetLatestWorkflowRound(repoId, "118", "head-sha", WorkflowRoundKind.Discussion)!
            .Id.Should()
            .Be(latest.Id);
        store.GetLatestWorkflowRound(repoId, "118", "other-head", WorkflowRoundKind.Discussion).Should().BeNull();
    }

    [Fact]
    public void Latest_run_and_artifact_owner_queries_cover_discovered_runs()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var repoId = store.EnsureRepo(SampleRepo());
        var first = store.CreateOrGetReviewRun(SampleRun(repoId));
        var latest = store.CreateOrGetReviewRun(SampleRun(repoId) with { HeadSha = "new-head", BaseSha = "head-sha" });
        store.AddArtifact(
            new ReviewArtifact
            {
                ReviewRunId = first.Id,
                ArtifactSchemaVersion = 1,
                ArtifactKind = "assignment",
                Provider = "github",
                Payload = "{}",
            }
        );

        store.GetLatestReviewRun(repoId, "118")!.Id.Should().Be(latest.Id);
        store.ListReviewRunsWithArtifact("assignment").Select(value => value.Id).Should().Equal(first.Id);
    }

    [Fact]
    public void Own_post_lookup_returns_only_confirmed_ids_for_allowed_operations_on_this_pr()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var repoId = store.EnsureRepo(SampleRepo());
        var run = store.CreateOrGetReviewRun(SampleRun(repoId));
        var otherPr = store.CreateOrGetReviewRun(SampleRun(repoId) with { PrId = "119" });

        AddReceipt(store, run.Id, "post-summary", "summary-1", OutboxStatus.Posted);
        AddReceipt(store, run.Id, "reply-thread", "reply-1", OutboxStatus.Sent);
        AddReceipt(store, run.Id, "push-artifacts", "commit-sha", OutboxStatus.Posted);
        AddReceipt(store, run.Id, "post-summary", "unconfirmed-1", OutboxStatus.Sending);
        AddReceipt(store, otherPr.Id, "post-summary", "other-pr-1", OutboxStatus.Posted);

        var ids = store.GetConfirmedProviderResponseIds(repoId, "118", ["post-summary", "reply-thread"]);

        ids.Should().BeEquivalentTo("summary-1", "reply-1");
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-json")]
    [InlineData("[]")]
    public void Frozen_input_must_be_a_json_object(string frozenInput)
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var repoId = store.EnsureRepo(SampleRepo());

        var act = () => store.CreateOrGetWorkflowRound(Discussion(repoId, "window-1", frozenInput));

        act.Should().Throw<ArgumentException>().WithMessage("*frozen*JSON object*");
    }

    private static WorkflowRoundSeed Discussion(long repoId, string eventKey, string frozenInput) =>
        new()
        {
            RepoId = repoId,
            PrId = "118",
            HeadSha = "head-sha",
            Kind = WorkflowRoundKind.Discussion,
            EventKey = eventKey,
            FrozenInputJson = frozenInput,
        };

    private static WorkflowRoundSeed Merged(long repoId, string mergeSha) =>
        new()
        {
            RepoId = repoId,
            PrId = "118",
            HeadSha = "head-sha",
            Kind = WorkflowRoundKind.Merged,
            EventKey = mergeSha,
            FrozenInputJson = $"{{\"mergeSha\":\"{mergeSha}\"}}",
        };

    private static void AddReceipt(
        ReviewStore store,
        long reviewRunId,
        string operation,
        string providerResponseId,
        OutboxStatus status
    )
    {
        _ = store.EnqueueOutbox(
            new OutboxEntry
            {
                IdempotencyKey = $"test:{reviewRunId}:{operation}:{providerResponseId}",
                Provider = "github",
                ReviewRunId = reviewRunId,
                Operation = operation,
                ArtifactKind = "test",
                Status = status,
                ProviderResponseId = providerResponseId,
            }
        );
    }

    private static RepoIdentity SampleRepo() =>
        new()
        {
            Provider = "github",
            OrgOrOwner = "achieveai",
            RepoName = "LmDotNetTools",
            RepoStableId = "R_node_123",
        };

    private static ReviewRun SampleRun(long repoId) =>
        new()
        {
            RepoId = repoId,
            PrId = "118",
            HeadSha = "head-sha",
            BaseSha = "base-sha",
            TriggerWatermark = "wm-1",
            ReviewKind = "full",
            VariantId = "primary",
            Mode = "collect-only",
            Stage = ReviewStage.Discovered,
            WorkflowStatus = WorkflowStatus.Pending,
            PrLifecycleState = PrLifecycleState.Open,
        };
}
