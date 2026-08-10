using AchieveAi.LmDotnetTools.LmTestUtils.Logging;
using CodeReviewDaemon.Sample.Orchestration;
using CodeReviewDaemon.Sample.Persistence;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
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
        : base(output)
    {
    }

    [Fact]
    public async Task A_fresh_run_executes_every_stage_in_order_and_completes()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var executor = new RecordingStageExecutor();
        var orchestrator = new PrOrchestrator(store, executor, LoggerFactory.CreateLogger<PrOrchestrator>());

        var run = await orchestrator.RunAsync(SeedRun(store), CancellationToken.None);

        executor.ExecutedStages.Should().Equal(
            ReviewStage.ContextReady,
            ReviewStage.Reviewed,
            ReviewStage.Judged,
            ReviewStage.Posted);
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
        _ = await new PrOrchestrator(store, first, LoggerFactory.CreateLogger<PrOrchestrator>()).RunAsync(seed, CancellationToken.None);

        var second = new RecordingStageExecutor();
        var run = await new PrOrchestrator(store, second, LoggerFactory.CreateLogger<PrOrchestrator>()).RunAsync(seed, CancellationToken.None);

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
        var crashingOrchestrator = new PrOrchestrator(store, crashing, LoggerFactory.CreateLogger<PrOrchestrator>());

        var act = async () => await crashingOrchestrator.RunAsync(seed, CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>();

        crashing.ExecutedStages.Should().Equal(ReviewStage.ContextReady, ReviewStage.Reviewed);

        // The persisted run records Reviewed as last-completed and RetryPending.
        var persisted = store.GetReviewRun(seed.Id);
        persisted!.Stage.Should().Be(ReviewStage.Reviewed);
        persisted.WorkflowStatus.Should().Be(WorkflowStatus.RetryPending);

        // Second attempt resumes: only Judged + Posted run, no completed stage replays.
        var resuming = new RecordingStageExecutor();
        var run = await new PrOrchestrator(store, resuming, LoggerFactory.CreateLogger<PrOrchestrator>())
            .RunAsync(seed, CancellationToken.None);

        resuming.ExecutedStages.Should().Equal(ReviewStage.Judged, ReviewStage.Posted);
        run.Stage.Should().Be(ReviewStage.Posted);
        run.WorkflowStatus.Should().Be(WorkflowStatus.Completed);
    }

    [Fact]
    public async Task A_pr_no_longer_open_short_circuits_to_completed_without_executing_stages()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var executor = new RecordingStageExecutor();
        var orchestrator = new PrOrchestrator(store, executor, LoggerFactory.CreateLogger<PrOrchestrator>());
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
        store.AddArtifact(new ReviewArtifact
        {
            ReviewRunId = run.Id,
            ArtifactSchemaVersion = 1,
            ArtifactKind = DaemonReviewStageExecutor.ReviewArtifactKind,
            Provider = "github",
            Payload = "{\"ReviewText\":\"No new findings since the last review.\",\"RunId\":\"r\",\"VariantId\":\"primary\"}",
        });
        var orchestrator = new PrOrchestrator(store, new RecordingStageExecutor(), LoggerFactory.CreateLogger<PrOrchestrator>());

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
        store.AddArtifact(new ReviewArtifact
        {
            ReviewRunId = run.Id,
            ArtifactSchemaVersion = 1,
            ArtifactKind = DaemonReviewStageExecutor.ReviewArtifactKind,
            Provider = "github",
            Payload =
                "{\"ReviewText\":\"No new findings in the auth module, but the migration adds a NOT NULL "
                + "column with no default.\",\"RunId\":\"r\",\"VariantId\":\"primary\"}",
        });
        var orchestrator = new PrOrchestrator(store, new RecordingStageExecutor(), LoggerFactory.CreateLogger<PrOrchestrator>());

        orchestrator.ClassifyDeliveryOutcome(run).Should().NotBe(
            "no new findings — nothing posted",
            "this run produced a finding, so whatever its delivery outcome is, it is not 'nothing to post'");
    }

    [Fact]
    public void Delivery_outcome_requires_terminal_comment_outbox_evidence_to_claim_posted()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var run = SeedRun(store) with { Mode = "post" };
        var orchestrator = new PrOrchestrator(store, new RecordingStageExecutor(), LoggerFactory.CreateLogger<PrOrchestrator>());

        orchestrator.ClassifyDeliveryOutcome(run).Should().Be("completed without provider-visible post evidence");

        var entry = store.EnqueueOutbox(new OutboxEntry
        {
            IdempotencyKey = "delivery-proof",
            Provider = "github",
            ReviewRunId = run.Id,
            Operation = ReviewPoster.PostReviewCommentOperation,
            ArtifactKind = DaemonReviewStageExecutor.ReviewArtifactKind,
            Status = OutboxStatus.Pending,
        });
        _ = store.TryTransitionOutbox(entry.Id, OutboxStatus.Pending, OutboxStatus.Posted, "comment-42");

        orchestrator.ClassifyDeliveryOutcome(run).Should().Be("posted");
    }

    [Fact]
    public async Task A_failure_marks_retry_pending_and_rethrows()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var executor = new RecordingStageExecutor(throwAtStage: ReviewStage.ContextReady);
        var orchestrator = new PrOrchestrator(store, executor, LoggerFactory.CreateLogger<PrOrchestrator>());
        var seed = SeedRun(store);

        var act = async () => await orchestrator.RunAsync(seed, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        var persisted = store.GetReviewRun(seed.Id);
        persisted!.WorkflowStatus.Should().Be(WorkflowStatus.RetryPending);
        persisted.Stage.Should().Be(ReviewStage.Discovered, "no stage completed before the failure");
    }

    // ── fixtures ──────────────────────────────────────────────────────────────────────────────────

    private static ReviewRun SeedRun(ReviewStore store)
    {
        var repoId = store.EnsureRepo(SampleRepo());
        return store.CreateOrGetReviewRun(SampleSeed(repoId));
    }

    private static RepoIdentity SampleRepo() => new()
    {
        Provider = "github",
        OrgOrOwner = "achieveai",
        RepoName = "LmDotnetTools",
        RepoStableId = "R_node_123",
    };

    private static ReviewRun SampleSeed(long repoId) => new()
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
