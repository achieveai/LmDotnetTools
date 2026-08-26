using AchieveAi.LmDotnetTools.LmTestUtils.Logging;
using CodeReviewDaemon.Sample.Orchestration;
using CodeReviewDaemon.Sample.Persistence;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace CodeReviewDaemon.Sample.Tests.Scenarios;

/// <summary>
/// P2.2 — the orchestrator drives one run through the stage machine, persisting after every stage so a
/// crash resumes from the first incomplete step (§6). Covers the happy path, idempotent creation,
/// resume-from-mid-pipeline, the merged/closed short-circuit, and the failure→RetryPending contract.
/// </summary>
public sealed class PrOrchestratorTests : LoggingTestBase
{
    public PrOrchestratorTests(ITestOutputHelper output)
        : base(output) { }

    [Fact]
    public async Task A_fresh_run_executes_every_stage_in_order_and_completes()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var executor = new RecordingStageExecutor();
        var orchestrator = new PrOrchestrator(
            store,
            executor,
            LoggerFactory.CreateLogger<PrOrchestrator>(),
            providers: [ReadyProvider()]
        );

        var run = await orchestrator.RunAsync(SeedRun(store), CancellationToken.None);

        executor
            .ExecutedStages.Should()
            .Equal(ReviewStage.ContextReady, ReviewStage.Reviewed, ReviewStage.Judged, ReviewStage.Posted);
        run.Stage.Should().Be(ReviewStage.Posted);
        run.WorkflowStatus.Should().Be(WorkflowStatus.Completed);
    }

    [Fact]
    public async Task Running_the_same_seed_twice_is_idempotent_and_does_no_work_the_second_time()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var first = new RecordingStageExecutor();
        var seed = SeedRun(store);
        _ = await new PrOrchestrator(
            store,
            first,
            LoggerFactory.CreateLogger<PrOrchestrator>(),
            providers: [ReadyProvider()]
        ).RunAsync(seed, CancellationToken.None);

        var second = new RecordingStageExecutor();
        var run = await new PrOrchestrator(
            store,
            second,
            LoggerFactory.CreateLogger<PrOrchestrator>(),
            providers: [ReadyProvider()]
        ).RunAsync(seed, CancellationToken.None);

        second.ExecutedStages.Should().BeEmpty("a run already at the terminal stage has no outstanding work");
        run.Stage.Should().Be(ReviewStage.Posted);
    }

    [Fact]
    public async Task A_crashed_run_resumes_from_the_first_incomplete_stage()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);

        // First attempt fails at Judged: ContextReady + Reviewed complete and persist, then it throws.
        var crashing = new RecordingStageExecutor(throwAtStage: ReviewStage.Judged);
        var seed = SeedRun(store);
        var crashingOrchestrator = new PrOrchestrator(
            store,
            crashing,
            LoggerFactory.CreateLogger<PrOrchestrator>(),
            providers: [ReadyProvider()]
        );

        var act = async () => await crashingOrchestrator.RunAsync(seed, CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>();

        crashing.ExecutedStages.Should().Equal(ReviewStage.ContextReady, ReviewStage.Reviewed);

        // The persisted run records Reviewed as last-completed and RetryPending.
        var persisted = store.GetReviewRun(seed.Id);
        persisted!.Stage.Should().Be(ReviewStage.Reviewed);
        persisted.WorkflowStatus.Should().Be(WorkflowStatus.RetryPending);

        // Second attempt resumes: only Judged + Posted run, no completed stage replays.
        var resuming = new RecordingStageExecutor();
        var run = await new PrOrchestrator(
            store,
            resuming,
            LoggerFactory.CreateLogger<PrOrchestrator>(),
            providers: [ReadyProvider()]
        ).RunAsync(seed, CancellationToken.None);

        resuming.ExecutedStages.Should().Equal(ReviewStage.Judged, ReviewStage.Posted);
        run.Stage.Should().Be(ReviewStage.Posted);
        run.WorkflowStatus.Should().Be(WorkflowStatus.Completed);
    }

    [Theory]
    [InlineData(PrDraftState.Draft)]
    [InlineData(PrDraftState.Unknown)]
    internal async Task Execution_preflight_blocks_non_ready_runs_without_stage_retry_or_lease_leak(PrDraftState state)
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var executor = new RecordingStageExecutor();
        var provider = new MockPrProvider(
            "github",
            [],
            new OpaqueCursor
            {
                Provider = "github",
                Scope = "scope",
                CursorVersion = 1,
                CursorPayload = "{}",
            }
        )
        {
            PrState = new PrStatus(PrLifecycle.Open, state),
        };
        var seed = SeedRun(store);
        store.UpdateReviewRunState(seed.Id, seed.Stage, WorkflowStatus.RetryPending, seed.PrLifecycleState);
        var orchestrator = new PrOrchestrator(
            store,
            executor,
            LoggerFactory.CreateLogger<PrOrchestrator>(),
            providers: [provider]
        );

        var result = await orchestrator.RunAsync(seed, CancellationToken.None);

        executor.ExecutedStages.Should().BeEmpty();
        executor.ReleaseCount.Should().Be(1, "the finally path releases any pooled lease even when preflight blocks");
        store.ReadOwner(result.Id).Should().BeNull();
        store.GetReviewRun(result.Id)!.WorkflowStatus.Should().Be(WorkflowStatus.RetryPending);
        store.GetReviewRun(result.Id)!.PrDraftState.Should().Be(state);
    }

    [Fact]
    public async Task Missing_provider_fails_closed_without_stage_attempt_and_releases_ownership()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var executor = new RecordingStageExecutor();
        var seed = SeedRun(store) with { PrDraftState = PrDraftState.Ready };
        var orchestrator = new PrOrchestrator(store, executor, LoggerFactory.CreateLogger<PrOrchestrator>());

        var result = await orchestrator.ExecuteAsync(seed, CancellationToken.None);

        result.Outcome.Should().Be(PrExecutionOutcome.ReadinessDeferred);
        result.ConsumedReviewAttempt.Should().BeFalse();
        executor.ExecutedStages.Should().BeEmpty();
        store
            .GetReviewRun(seed.Id)!
            .PrDraftState.Should()
            .Be(PrDraftState.Unknown, "persisted Ready is not authority when fresh provider resolution is missing");
        store.ReadOwner(seed.Id).Should().BeNull();
    }

    [Fact]
    public async Task Missing_repository_fails_closed_without_stage_attempt_and_releases_ownership()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var executor = new RecordingStageExecutor();
        var seed = SeedRun(store) with { PrDraftState = PrDraftState.Ready };
        using (var connection = new SqliteConnection(db.ConnectionString))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA foreign_keys = OFF; DELETE FROM repo WHERE id = $repoId;";
            command.Parameters.AddWithValue("$repoId", seed.RepoId);
            _ = command.ExecuteNonQuery();
        }
        var orchestrator = new PrOrchestrator(
            store,
            executor,
            LoggerFactory.CreateLogger<PrOrchestrator>(),
            providers: [ReadyProvider()]
        );

        var result = await orchestrator.ExecuteAsync(seed, CancellationToken.None);

        result.Outcome.Should().Be(PrExecutionOutcome.ReadinessDeferred);
        result.ConsumedReviewAttempt.Should().BeFalse();
        executor.ExecutedStages.Should().BeEmpty();
        store.GetReviewRun(result.Run.Id)!.PrDraftState.Should().Be(PrDraftState.Unknown);
        store.ReadOwner(result.Run.Id).Should().BeNull();
    }

    [Fact]
    public async Task Readiness_change_between_stages_blocks_every_later_dispatch_and_ready_resumes_checkpoint()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var provider = new SequencedStatusProvider(
            new PrStatus(PrLifecycle.Open, PrDraftState.Ready),
            new PrStatus(PrLifecycle.Open, PrDraftState.Ready),
            new PrStatus(PrLifecycle.Open, PrDraftState.Draft)
        );
        var firstExecutor = new RecordingStageExecutor();
        var seed = SeedRun(store);
        var first = new PrOrchestrator(
            store,
            firstExecutor,
            LoggerFactory.CreateLogger<PrOrchestrator>(),
            providers: [provider]
        );

        var deferred = await first.RunAsync(seed, CancellationToken.None);

        firstExecutor.ExecutedStages.Should().Equal(ReviewStage.ContextReady);
        deferred.Stage.Should().Be(ReviewStage.ContextReady);
        store.ReadOwner(deferred.Id).Should().BeNull();

        provider.SetStatuses([.. Enumerable.Repeat(new PrStatus(PrLifecycle.Open, PrDraftState.Ready), 4)]);
        var secondExecutor = new RecordingStageExecutor();
        var resumed = await new PrOrchestrator(
            store,
            secondExecutor,
            LoggerFactory.CreateLogger<PrOrchestrator>(),
            providers: [provider]
        ).RunAsync(seed, CancellationToken.None);

        secondExecutor.ExecutedStages.Should().Equal(ReviewStage.Reviewed, ReviewStage.Judged, ReviewStage.Posted);
        resumed.Stage.Should().Be(ReviewStage.Posted);
    }

    [Fact]
    public async Task A_pr_no_longer_open_short_circuits_to_completed_without_executing_stages()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var executor = new RecordingStageExecutor();
        var orchestrator = new PrOrchestrator(
            store,
            executor,
            LoggerFactory.CreateLogger<PrOrchestrator>(),
            providers: [ReadyProvider()]
        );
        var repoId = store.EnsureRepo(SampleRepo());

        var seed = SampleSeed(repoId) with { PrLifecycleState = PrLifecycleState.Merged };
        var run = await orchestrator.RunAsync(seed, CancellationToken.None);

        executor.ExecutedStages.Should().BeEmpty("a merged PR is not reviewed");
        run.WorkflowStatus.Should().Be(WorkflowStatus.Completed);
        run.PrLifecycleState.Should().Be(PrLifecycleState.Merged);
    }

    [Fact]
    public void Delivery_outcome_reports_no_findings_without_claiming_a_post()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var run = SeedRun(store) with { Mode = "post" };
        store.AddArtifact(
            new ReviewArtifact
            {
                ReviewRunId = run.Id,
                ArtifactSchemaVersion = 1,
                ArtifactKind = DaemonReviewStageExecutor.ReviewArtifactKind,
                Provider = "github",
                Payload =
                    "{\"ReviewText\":\"No new findings since the last review.\",\"RunId\":\"r\",\"VariantId\":\"primary\"}",
            }
        );
        var orchestrator = new PrOrchestrator(
            store,
            new RecordingStageExecutor(),
            LoggerFactory.CreateLogger<PrOrchestrator>(),
            providers: [ReadyProvider()]
        );

        orchestrator.ClassifyDeliveryOutcome(run).Should().Be("no new findings — nothing posted");
    }

    /// <summary>
    /// The same classification, at the third place the daemon asks the question. This one used to hold its
    /// own inlined copy of the rule, so a review that merely OPENED with the exit phrase was reported as
    /// "nothing posted" — an outcome line contradicting both the outbox and the comment on the PR. Two
    /// constructions of one rule drift; this pins them to the one predicate.
    /// </summary>
    [Fact]
    public void Delivery_outcome_does_not_report_nothing_posted_for_a_review_that_carries_findings()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var run = SeedRun(store) with { Mode = "post" };
        store.AddArtifact(
            new ReviewArtifact
            {
                ReviewRunId = run.Id,
                ArtifactSchemaVersion = 1,
                ArtifactKind = DaemonReviewStageExecutor.ReviewArtifactKind,
                Provider = "github",
                Payload =
                    "{\"ReviewText\":\"No new findings in the auth module, but the migration adds a NOT NULL "
                    + "column with no default.\",\"RunId\":\"r\",\"VariantId\":\"primary\"}",
            }
        );
        var orchestrator = new PrOrchestrator(
            store,
            new RecordingStageExecutor(),
            LoggerFactory.CreateLogger<PrOrchestrator>(),
            providers: [ReadyProvider()]
        );

        orchestrator
            .ClassifyDeliveryOutcome(run)
            .Should()
            .NotBe(
                "no new findings — nothing posted",
                "this run produced a finding, so whatever its delivery outcome is, it is not 'nothing to post'"
            );
    }

    [Fact]
    public void Delivery_outcome_requires_terminal_comment_outbox_evidence_to_claim_posted()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var run = SeedRun(store) with { Mode = "post" };
        var orchestrator = new PrOrchestrator(
            store,
            new RecordingStageExecutor(),
            LoggerFactory.CreateLogger<PrOrchestrator>(),
            providers: [ReadyProvider()]
        );

        orchestrator.ClassifyDeliveryOutcome(run).Should().Be("completed without provider-visible post evidence");

        var entry = store.EnqueueOutbox(
            new OutboxEntry
            {
                IdempotencyKey = "delivery-proof",
                Provider = "github",
                ReviewRunId = run.Id,
                Operation = ReviewPoster.PostReviewCommentOperation,
                ArtifactKind = DaemonReviewStageExecutor.ReviewArtifactKind,
                Status = OutboxStatus.Pending,
            }
        );
        _ = store.TryTransitionOutbox(entry.Id, OutboxStatus.Pending, OutboxStatus.Posted, "comment-42");

        orchestrator.ClassifyDeliveryOutcome(run).Should().Be("posted");
    }

    [Fact]
    public async Task A_failure_marks_retry_pending_and_rethrows()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var executor = new RecordingStageExecutor(throwAtStage: ReviewStage.ContextReady);
        var orchestrator = new PrOrchestrator(
            store,
            executor,
            LoggerFactory.CreateLogger<PrOrchestrator>(),
            providers: [ReadyProvider()]
        );
        var seed = SeedRun(store);

        var act = async () => await orchestrator.RunAsync(seed, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        var persisted = store.GetReviewRun(seed.Id);
        persisted!.WorkflowStatus.Should().Be(WorkflowStatus.RetryPending);
        persisted.Stage.Should().Be(ReviewStage.Discovered, "no stage completed before the failure");
    }

    private sealed class SequencedStatusProvider(params PrStatus[] statuses) : IPrProvider
    {
        private Queue<PrStatus> _statuses = new(statuses);

        public string Provider => "github";

        public void SetStatuses(params PrStatus[] next) => _statuses = new Queue<PrStatus>(next);

        public Task<PullRequestPage> ListOpenPullRequestsAsync(
            PrPollRequest request,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task<PrStatus> GetPrStateAsync(RepoIdentity repo, string prId, CancellationToken cancellationToken) =>
            Task.FromResult(_statuses.Count > 1 ? _statuses.Dequeue() : _statuses.Peek());
    }

    // ── fixtures ──────────────────────────────────────────────────────────────────────────────────

    private static ReviewRun SeedRun(ReviewStore store)
    {
        var repoId = store.EnsureRepo(SampleRepo());
        return store.CreateOrGetReviewRun(SampleSeed(repoId));
    }

    private static MockPrProvider ReadyProvider() =>
        new(
            "github",
            [],
            new OpaqueCursor
            {
                Provider = "github",
                Scope = "scope",
                CursorVersion = 1,
                CursorPayload = "{}",
            }
        )
        {
            PrState = new PrStatus(PrLifecycle.Open, PrDraftState.Ready),
        };

    private static RepoIdentity SampleRepo() =>
        new()
        {
            Provider = "github",
            OrgOrOwner = "achieveai",
            RepoName = "LmDotnetTools",
            RepoStableId = "R_node_123",
        };

    private static ReviewRun SampleSeed(long repoId) =>
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
