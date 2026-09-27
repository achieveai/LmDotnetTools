using System.Text.Json.Nodes;
using AchieveAi.LmDotnetTools.LmWorkflow.Runtime;
using CodeReviewDaemon.Sample.Configuration;
using CodeReviewDaemon.Sample.Orchestration;
using CodeReviewDaemon.Sample.Persistence;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeReviewDaemon.Sample.Tests.Orchestration;

/// <summary>
/// Task #81, Command B — <c>--run-pr &lt;repoKey&gt; &lt;prId&gt; &lt;approvedHead&gt; &lt;approvedBase&gt;</c>.
/// An operator-approved single-PR pilot run. Every rejection path — outside the allow-list, ambiguous
/// repo-key resolution, PR not found, PR not open, or the approved head/base drifting from a fresh
/// re-read — must fire BEFORE any durable admission or slot allocation: no <c>review_run</c> row and no
/// workflow-runner invocation. The happy path must reuse <see cref="PrOrchestrator.RunAsync"/> exactly as
/// the polling flow does (proven here by the same fake <see cref="IReviewWorkflowRunner"/> idiom
/// <c>PrOrchestratorWorkflowTests</c> uses), and must never touch the poll cursor.
/// </summary>
public sealed class RunSinglePrCommandTests
{
    private const string RepoKey = "acme/widgets";
    private const string Scope = "acme/widgets:open-prs";

    private static CodeReviewDaemonOptions Options(params string[] enabledRepos) =>
        new() { EnabledRepos = enabledRepos };

    private static PullRequestDescriptor OpenPr(string prId = "7", string head = "head-7", string @base = "base-7") =>
        new()
        {
            PrId = prId,
            HeadSha = head,
            BaseSha = @base,
            TriggerWatermark = "watermark-7",
            LifecycleState = PrLifecycleState.Open,
            Author = "alice",
            Title = "Fix the thing",
        };

    private static OpaqueCursor SeedCursor() =>
        new()
        {
            Provider = "github",
            Scope = Scope,
            CursorVersion = PrPollingService.CursorVersion,
            CursorPayload = "{}",
        };

    private static void AssertNothingWasAdmitted(ReviewStore store)
    {
        store.ListReviewRuns(ReviewStage.Discovered, 100).Should().BeEmpty();
        store.ReadCursor("github", Scope, PrPollingService.CursorVersion).ShouldResync.Should().BeTrue();
    }

    /// <summary>
    /// Round 5 — task #82 owns the producer side of <c>review_rerun_authorization</c> (it records one only
    /// after independently verifying a successful, non-quarantined redo); this raw insert exists purely so
    /// task #81's consumer-side tests can seed one without duplicating that producer method here. Opens its
    /// own connection rather than reaching into <see cref="ReviewStore"/>'s private one.
    /// </summary>
    private static void SeedRerunAuthorization(
        TempSqliteDatabase database,
        long repoId,
        string prId,
        string headSha,
        string baseSha,
        long priorReviewRunId,
        string rerunWatermark
    )
    {
        using var connection = SqliteConnectionFactory.Open(database.ConnectionString);
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO review_rerun_authorization (
                repo_id, pr_id, head_sha, base_sha, prior_review_run_id,
                deleted_branch, deleted_branch_sha, rerun_watermark, created_at)
            VALUES ($repoId, $prId, $head, $base, $priorRunId, $branch, $branchSha, $watermark, $now);
            """;
        _ = command.Parameters.AddWithValue("$repoId", repoId);
        _ = command.Parameters.AddWithValue("$prId", prId);
        _ = command.Parameters.AddWithValue("$head", headSha);
        _ = command.Parameters.AddWithValue("$base", baseSha);
        _ = command.Parameters.AddWithValue("$priorRunId", priorReviewRunId);
        _ = command.Parameters.AddWithValue("$branch", "codereview/redo-" + prId);
        _ = command.Parameters.AddWithValue("$branchSha", "deleted-branch-sha");
        _ = command.Parameters.AddWithValue("$watermark", rerunWatermark);
        _ = command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        _ = command.ExecuteNonQuery();
    }

    [Fact]
    public async Task Rejects_a_repo_key_outside_the_configured_allow_list_before_admitting_anything()
    {
        using var database = new TempSqliteDatabase();
        using var store = new ReviewStore(database.ConnectionString);
        var provider = new MockPrProvider("github", [OpenPr()], SeedCursor());
        var runner = new StatusRunner(WorkflowInvocationStatus.Completed);
        var orchestrator = new PrOrchestrator(store, runner, NullLogger<PrOrchestrator>.Instance);

        var result = await RunSinglePrCommand.RunAsync(
            Options("acme/widgets"),
            [provider],
            store,
            orchestrator,
            NullLogger.Instance,
            repoKey: "someone-else/other-repo",
            prId: "7",
            approvedHeadSha: "head-7",
            approvedBaseSha: "base-7",
            CancellationToken.None
        );

        result.Admitted.Should().BeFalse();
        result.RejectionReason.Should().Be(RunSinglePrRejectionReason.OutsideAllowList);
        runner.Attempts.Should().Be(0);
        AssertNothingWasAdmitted(store);
    }

    [Fact]
    public async Task Rejects_an_ambiguous_repo_key_before_admitting_anything()
    {
        // Two EnabledRepos entries that differ only by casing collapse to the same case-insensitive
        // DisplayName match — the operator's key cannot pick one, so this must refuse rather than guess.
        using var database = new TempSqliteDatabase();
        using var store = new ReviewStore(database.ConnectionString);
        var provider = new MockPrProvider("github", [OpenPr()], SeedCursor());
        var runner = new StatusRunner(WorkflowInvocationStatus.Completed);
        var orchestrator = new PrOrchestrator(store, runner, NullLogger<PrOrchestrator>.Instance);

        var result = await RunSinglePrCommand.RunAsync(
            Options("Acme/Widgets", "acme/widgets"),
            [provider],
            store,
            orchestrator,
            NullLogger.Instance,
            repoKey: "acme/widgets",
            prId: "7",
            approvedHeadSha: "head-7",
            approvedBaseSha: "base-7",
            CancellationToken.None
        );

        result.Admitted.Should().BeFalse();
        result.RejectionReason.Should().Be(RunSinglePrRejectionReason.Ambiguous);
        runner.Attempts.Should().Be(0);
        AssertNothingWasAdmitted(store);
    }

    [Fact]
    public async Task Rejects_when_no_provider_is_registered_for_the_resolved_target()
    {
        using var database = new TempSqliteDatabase();
        using var store = new ReviewStore(database.ConnectionString);
        var runner = new StatusRunner(WorkflowInvocationStatus.Completed);
        var orchestrator = new PrOrchestrator(store, runner, NullLogger<PrOrchestrator>.Instance);

        var result = await RunSinglePrCommand.RunAsync(
            Options(RepoKey),
            [],
            store,
            orchestrator,
            NullLogger.Instance,
            repoKey: RepoKey,
            prId: "7",
            approvedHeadSha: "head-7",
            approvedBaseSha: "base-7",
            CancellationToken.None
        );

        result.Admitted.Should().BeFalse();
        result.RejectionReason.Should().Be(RunSinglePrRejectionReason.OutsideAllowList);
        runner.Attempts.Should().Be(0);
        AssertNothingWasAdmitted(store);
    }

    [Fact]
    public async Task Rejects_a_pr_the_fresh_re_read_cannot_find_before_admitting_anything()
    {
        using var database = new TempSqliteDatabase();
        using var store = new ReviewStore(database.ConnectionString);
        // Seeded with no PRs at all: MockPrProvider.GetPullRequestAsync's FirstOrDefault finds nothing.
        var provider = new MockPrProvider("github", [], SeedCursor());
        var runner = new StatusRunner(WorkflowInvocationStatus.Completed);
        var orchestrator = new PrOrchestrator(store, runner, NullLogger<PrOrchestrator>.Instance);

        var result = await RunSinglePrCommand.RunAsync(
            Options(RepoKey),
            [provider],
            store,
            orchestrator,
            NullLogger.Instance,
            repoKey: RepoKey,
            prId: "7",
            approvedHeadSha: "head-7",
            approvedBaseSha: "base-7",
            CancellationToken.None
        );

        result.Admitted.Should().BeFalse();
        result.RejectionReason.Should().Be(RunSinglePrRejectionReason.NotFound);
        provider.GetPullRequestCalls.Should().Be(1, "the fresh re-read must actually have been attempted");
        runner.Attempts.Should().Be(0);
        AssertNothingWasAdmitted(store);
    }

    [Fact]
    public async Task Rejects_a_pr_that_is_no_longer_open_before_admitting_anything()
    {
        using var database = new TempSqliteDatabase();
        using var store = new ReviewStore(database.ConnectionString);
        var mergedPr = OpenPr() with { LifecycleState = PrLifecycleState.Merged };
        var provider = new MockPrProvider("github", [mergedPr], SeedCursor());
        var runner = new StatusRunner(WorkflowInvocationStatus.Completed);
        var orchestrator = new PrOrchestrator(store, runner, NullLogger<PrOrchestrator>.Instance);

        var result = await RunSinglePrCommand.RunAsync(
            Options(RepoKey),
            [provider],
            store,
            orchestrator,
            NullLogger.Instance,
            repoKey: RepoKey,
            prId: "7",
            approvedHeadSha: "head-7",
            approvedBaseSha: "base-7",
            CancellationToken.None
        );

        result.Admitted.Should().BeFalse();
        result.RejectionReason.Should().Be(RunSinglePrRejectionReason.NotOpen);
        runner.Attempts.Should().Be(0);
        AssertNothingWasAdmitted(store);
    }

    [Theory]
    [InlineData("drifted-head", "base-7")]
    [InlineData("head-7", "drifted-base")]
    public async Task Rejects_when_the_fresh_re_read_disagrees_with_the_approved_identity(
        string approvedHead,
        string approvedBase
    )
    {
        using var database = new TempSqliteDatabase();
        using var store = new ReviewStore(database.ConnectionString);
        var provider = new MockPrProvider("github", [OpenPr()], SeedCursor());
        var runner = new StatusRunner(WorkflowInvocationStatus.Completed);
        var orchestrator = new PrOrchestrator(store, runner, NullLogger<PrOrchestrator>.Instance);

        var result = await RunSinglePrCommand.RunAsync(
            Options(RepoKey),
            [provider],
            store,
            orchestrator,
            NullLogger.Instance,
            repoKey: RepoKey,
            prId: "7",
            approvedHeadSha: approvedHead,
            approvedBaseSha: approvedBase,
            CancellationToken.None
        );

        result.Admitted.Should().BeFalse();
        result.RejectionReason.Should().Be(RunSinglePrRejectionReason.IdentityDrift);
        runner.Attempts.Should().Be(0);
        AssertNothingWasAdmitted(store);
    }

    [Fact]
    public async Task Admits_and_runs_through_the_same_orchestrator_path_polling_uses_when_every_check_passes()
    {
        using var database = new TempSqliteDatabase();
        using var store = new ReviewStore(database.ConnectionString);
        var provider = new MockPrProvider("github", [OpenPr()], SeedCursor());
        var runner = new StatusRunner(WorkflowInvocationStatus.Completed);
        var orchestrator = new PrOrchestrator(store, runner, NullLogger<PrOrchestrator>.Instance);

        var result = await RunSinglePrCommand.RunAsync(
            Options(RepoKey),
            [provider],
            store,
            orchestrator,
            NullLogger.Instance,
            repoKey: RepoKey,
            prId: "7",
            approvedHeadSha: "head-7",
            approvedBaseSha: "base-7",
            CancellationToken.None,
            commentReaders: [provider]
        );

        result.Admitted.Should().BeTrue();
        result.RejectionReason.Should().BeNull();
        result.RepoKey.Should().Be(RepoKey);
        result.Provider.Should().Be("github");
        result.PrId.Should().Be("7");
        result.HeadSha.Should().Be("head-7");
        result.BaseSha.Should().Be("base-7");
        runner.Attempts.Should().Be(1, "the one existing admission path must have been reused, not reimplemented");
        // PrOrchestrator.RunAsync only clears the governed-failure count on a Completed attempt — it does
        // not itself advance stage/workflow_status (the real workflow runner does that); a fake runner like
        // this one therefore leaves the row exactly as CreateOrGetReviewRun seeded it. What matters here is
        // that admission happened at all (the row exists) and the cursor was never touched.
        store
            .ListReviewRuns(ReviewStage.Discovered, 100)
            .Should()
            .ContainSingle("the seed was admitted through the real orchestrator, creating exactly one row");
        store
            .ReadCursor("github", Scope, PrPollingService.CursorVersion)
            .ShouldResync.Should()
            .BeTrue("the polling cursor must never be touched by an operator-triggered single-PR run");
    }

    [Fact]
    public async Task Prepare_needs_no_store_or_orchestrator_and_admit_reuses_its_accepted_result()
    {
        // This is exactly the two-call shape Program.cs uses for --run-pr: PrepareAsync runs BEFORE
        // app.StartAsync() (it never touches ReviewStore/PrOrchestrator/comment readers), and only an
        // Accepted result is ever handed to AdmitAndRunAsync once the host is up.
        using var database = new TempSqliteDatabase();
        using var store = new ReviewStore(database.ConnectionString);
        var provider = new MockPrProvider("github", [OpenPr()], SeedCursor());
        var runner = new StatusRunner(WorkflowInvocationStatus.Completed);
        var orchestrator = new PrOrchestrator(store, runner, NullLogger<PrOrchestrator>.Instance);

        var prepared = await RunSinglePrCommand.PrepareAsync(
            Options(RepoKey),
            [provider],
            NullLogger.Instance,
            repoKey: RepoKey,
            prId: "7",
            approvedHeadSha: "head-7",
            approvedBaseSha: "base-7",
            CancellationToken.None
        );

        prepared.Accepted.Should().BeTrue();
        prepared.RejectionReason.Should().BeNull();
        // Nothing was admitted yet — PrepareAsync alone must never create a review_run row.
        store.ListReviewRuns(ReviewStage.Discovered, 100).Should().BeEmpty();

        var result = await RunSinglePrCommand.AdmitAndRunAsync(
            prepared,
            store,
            orchestrator,
            CancellationToken.None,
            commentReaders: [provider]
        );

        result.Admitted.Should().BeTrue();
        result.PrId.Should().Be("7");
        runner.Attempts.Should().Be(1);
        store.ListReviewRuns(ReviewStage.Discovered, 100).Should().ContainSingle();
    }

    [Fact]
    public async Task Prepare_rejects_before_any_store_or_orchestrator_is_needed()
    {
        // A rejection must be fully determinable from PrepareAsync alone — Program.cs relies on this to
        // decide it can skip app.StartAsync() entirely for a rejected run.
        var prepared = await RunSinglePrCommand.PrepareAsync(
            Options("acme/widgets"),
            [new MockPrProvider("github", [OpenPr()], SeedCursor())],
            NullLogger.Instance,
            repoKey: "someone-else/other-repo",
            prId: "7",
            approvedHeadSha: "head-7",
            approvedBaseSha: "base-7",
            CancellationToken.None
        );

        prepared.Accepted.Should().BeFalse();
        prepared.RejectionReason.Should().Be(RunSinglePrRejectionReason.OutsideAllowList);
        prepared.Target.Should().BeNull();
        prepared.Descriptor.Should().BeNull();
    }

    [Fact]
    public async Task Admit_rejects_when_the_pr_is_no_longer_open_by_the_time_admit_re_reads_it()
    {
        // Round 4, item 1 — the TOCTOU window between PrepareAsync's fresh read and AdmitAndRunAsync's own
        // re-read: an operator approves an open PR, but between the coordinator lease/host startup and the
        // actual admission the PR gets merged. AdmitAndRunAsync must catch this itself — reusing the exact
        // same target/provider PrepareAsync resolved — rather than trusting PrepareAsync's now-stale read.
        using var database = new TempSqliteDatabase();
        using var store = new ReviewStore(database.ConnectionString);
        var provider = new MockPrProvider("github", [OpenPr()], SeedCursor());
        var runner = new StatusRunner(WorkflowInvocationStatus.Completed);
        var orchestrator = new PrOrchestrator(store, runner, NullLogger<PrOrchestrator>.Instance);

        var prepared = await RunSinglePrCommand.PrepareAsync(
            Options(RepoKey),
            [provider],
            NullLogger.Instance,
            repoKey: RepoKey,
            prId: "7",
            approvedHeadSha: "head-7",
            approvedBaseSha: "base-7",
            CancellationToken.None
        );
        prepared.Accepted.Should().BeTrue();

        // Simulate the PR getting merged in the window between Prepare and Admit.
        provider.ReplacePullRequest(OpenPr() with { LifecycleState = PrLifecycleState.Merged });

        var result = await RunSinglePrCommand.AdmitAndRunAsync(
            prepared,
            store,
            orchestrator,
            CancellationToken.None,
            commentReaders: [provider]
        );

        result.Admitted.Should().BeFalse();
        result.RejectionReason.Should().Be(RunSinglePrRejectionReason.NotOpen);
        provider.GetPullRequestCalls.Should().Be(2, "Prepare's read plus Admit's own re-read");
        runner.Attempts.Should().Be(0);
        AssertNothingWasAdmitted(store);
    }

    [Fact]
    public async Task Admit_rejects_when_the_head_sha_drifts_between_prepare_and_admit()
    {
        using var database = new TempSqliteDatabase();
        using var store = new ReviewStore(database.ConnectionString);
        var provider = new MockPrProvider("github", [OpenPr()], SeedCursor());
        var runner = new StatusRunner(WorkflowInvocationStatus.Completed);
        var orchestrator = new PrOrchestrator(store, runner, NullLogger<PrOrchestrator>.Instance);

        var prepared = await RunSinglePrCommand.PrepareAsync(
            Options(RepoKey),
            [provider],
            NullLogger.Instance,
            repoKey: RepoKey,
            prId: "7",
            approvedHeadSha: "head-7",
            approvedBaseSha: "base-7",
            CancellationToken.None
        );
        prepared.Accepted.Should().BeTrue();

        // Simulate a force-push landing in the window between Prepare and Admit.
        provider.ReplacePullRequest(OpenPr(head: "force-pushed-head"));

        var result = await RunSinglePrCommand.AdmitAndRunAsync(
            prepared,
            store,
            orchestrator,
            CancellationToken.None,
            commentReaders: [provider]
        );

        result.Admitted.Should().BeFalse();
        result.RejectionReason.Should().Be(RunSinglePrRejectionReason.IdentityDrift);
        runner.Attempts.Should().Be(0);
        AssertNothingWasAdmitted(store);
    }

    [Fact]
    public async Task Admit_rejects_when_the_pr_disappears_between_prepare_and_admit()
    {
        using var database = new TempSqliteDatabase();
        using var store = new ReviewStore(database.ConnectionString);
        var provider = new MockPrProvider("github", [OpenPr()], SeedCursor());
        var runner = new StatusRunner(WorkflowInvocationStatus.Completed);
        var orchestrator = new PrOrchestrator(store, runner, NullLogger<PrOrchestrator>.Instance);

        var prepared = await RunSinglePrCommand.PrepareAsync(
            Options(RepoKey),
            [provider],
            NullLogger.Instance,
            repoKey: RepoKey,
            prId: "7",
            approvedHeadSha: "head-7",
            approvedBaseSha: "base-7",
            CancellationToken.None
        );
        prepared.Accepted.Should().BeTrue();

        provider.RemovePullRequest("7");

        var result = await RunSinglePrCommand.AdmitAndRunAsync(
            prepared,
            store,
            orchestrator,
            CancellationToken.None,
            commentReaders: [provider]
        );

        result.Admitted.Should().BeFalse();
        result.RejectionReason.Should().Be(RunSinglePrRejectionReason.NotFound);
        runner.Attempts.Should().Be(0);
        AssertNothingWasAdmitted(store);
    }

    [Fact]
    public async Task Admit_reports_already_completed_and_never_invokes_the_orchestrator_for_a_duplicate_exact_run()
    {
        // Round 4, item 4 — an ordinary duplicate exact-run (same repo/pr/head/base/kind/variant identity,
        // already Completed) must report AlreadyCompleted explicitly rather than silently calling the
        // orchestrator again (which would itself no-op, per PrOrchestrator.RunAsync's own Completed check,
        // but that no-op reads as Admitted=true to a caller that never inspected WorkflowStatus).
        using var database = new TempSqliteDatabase();
        using var store = new ReviewStore(database.ConnectionString);
        var provider = new MockPrProvider("github", [OpenPr()], SeedCursor());
        var runner = new StatusRunner(WorkflowInvocationStatus.Completed);
        var orchestrator = new PrOrchestrator(store, runner, NullLogger<PrOrchestrator>.Instance);

        // First run admits normally and (for this test) we mark it Completed directly, the way the real
        // workflow runner would once it finished.
        var first = await RunSinglePrCommand.RunAsync(
            Options(RepoKey),
            [provider],
            store,
            orchestrator,
            NullLogger.Instance,
            repoKey: RepoKey,
            prId: "7",
            approvedHeadSha: "head-7",
            approvedBaseSha: "base-7",
            CancellationToken.None,
            commentReaders: [provider]
        );
        first.Admitted.Should().BeTrue();
        var admittedRun = store.ListReviewRuns(ReviewStage.Discovered, 100).Should().ContainSingle().Subject;
        store.UpdateReviewRunState(admittedRun.Id, ReviewStage.Posted, WorkflowStatus.Completed, PrLifecycleState.Open);

        var second = await RunSinglePrCommand.RunAsync(
            Options(RepoKey),
            [provider],
            store,
            orchestrator,
            NullLogger.Instance,
            repoKey: RepoKey,
            prId: "7",
            approvedHeadSha: "head-7",
            approvedBaseSha: "base-7",
            CancellationToken.None,
            commentReaders: [provider]
        );

        second.Admitted.Should().BeFalse();
        second.RejectionReason.Should().Be(RunSinglePrRejectionReason.AlreadyCompleted);
        runner.Attempts.Should().Be(1, "only the first RunAsync call should ever have reached the orchestrator");
        store
            .ListReviewRuns(ReviewStage.Discovered, 100)
            .Should()
            .ContainSingle("the duplicate call must not have created a second row");
    }

    [Fact]
    public async Task Admit_consumes_an_outstanding_rerun_authorization_and_performs_a_full_review_through_a_fresh_generation_row()
    {
        // Round 5 — once task #82's artifact-branch retention records a single-use
        // ReviewRerunAuthorization for this exact PR (only after a verified, non-quarantined redo), the
        // very next exact --run-pr must consume it, insert a brand new row one generation past the
        // completed one WITHOUT erasing it, and run a FULL review through the new row — not silently no-op
        // like an ordinary duplicate.
        using var database = new TempSqliteDatabase();
        using var store = new ReviewStore(database.ConnectionString);
        var provider = new MockPrProvider("github", [OpenPr()], SeedCursor());
        var runner = new StatusRunner(WorkflowInvocationStatus.Completed);
        var orchestrator = new PrOrchestrator(store, runner, NullLogger<PrOrchestrator>.Instance);

        var first = await RunSinglePrCommand.RunAsync(
            Options(RepoKey),
            [provider],
            store,
            orchestrator,
            NullLogger.Instance,
            repoKey: RepoKey,
            prId: "7",
            approvedHeadSha: "head-7",
            approvedBaseSha: "base-7",
            CancellationToken.None,
            commentReaders: [provider]
        );
        first.Admitted.Should().BeTrue();
        var completedRun = store.ListReviewRuns(ReviewStage.Discovered, 100).Should().ContainSingle().Subject;
        store.UpdateReviewRunState(
            completedRun.Id,
            ReviewStage.Posted,
            WorkflowStatus.Completed,
            PrLifecycleState.Open
        );

        SeedRerunAuthorization(
            database,
            completedRun.RepoId,
            "7",
            "head-7",
            "base-7",
            completedRun.Id,
            "rerun:7:deleted-branch-sha"
        );

        var second = await RunSinglePrCommand.RunAsync(
            Options(RepoKey),
            [provider],
            store,
            orchestrator,
            NullLogger.Instance,
            repoKey: RepoKey,
            prId: "7",
            approvedHeadSha: "head-7",
            approvedBaseSha: "base-7",
            CancellationToken.None,
            commentReaders: [provider]
        );

        second
            .Admitted.Should()
            .BeTrue("a consumed rerun authorization must supersede AlreadyCompleted with a full review");
        runner.Attempts.Should().Be(2, "the redo must actually invoke the orchestrator again, not no-op");

        var rows = store.ListReviewRuns(ReviewStage.Discovered, 100);
        rows.Should().HaveCount(2, "the completed row must survive untouched alongside the new redo row");
        rows.Should().ContainSingle(r => r.Id == completedRun.Id && r.WorkflowStatus == WorkflowStatus.Completed);
        var redoRow = rows.Should().ContainSingle(r => r.Id != completedRun.Id).Subject;
        redoRow.Generation.Should().Be(completedRun.Generation + 1);

        store
            .GetOutstandingRerunAuthorization(completedRun.RepoId, "7")
            .Should()
            .BeNull("the authorization must not be reusable for a further duplicate call");
    }

    [Fact]
    public async Task Admit_falls_back_to_already_completed_when_the_authorization_no_longer_matches_the_fresh_head()
    {
        // An authorization recorded for a head/base the fresh re-read no longer confirms (the PR moved
        // again after the redo was authorized) must never let a rerun through — this is the same
        // fail-closed posture as an unknown/quarantined redo outcome never minting a token at all.
        using var database = new TempSqliteDatabase();
        using var store = new ReviewStore(database.ConnectionString);
        var provider = new MockPrProvider("github", [OpenPr()], SeedCursor());
        var runner = new StatusRunner(WorkflowInvocationStatus.Completed);
        var orchestrator = new PrOrchestrator(store, runner, NullLogger<PrOrchestrator>.Instance);

        var first = await RunSinglePrCommand.RunAsync(
            Options(RepoKey),
            [provider],
            store,
            orchestrator,
            NullLogger.Instance,
            repoKey: RepoKey,
            prId: "7",
            approvedHeadSha: "head-7",
            approvedBaseSha: "base-7",
            CancellationToken.None,
            commentReaders: [provider]
        );
        first.Admitted.Should().BeTrue();
        var completedRun = store.ListReviewRuns(ReviewStage.Discovered, 100).Should().ContainSingle().Subject;
        store.UpdateReviewRunState(
            completedRun.Id,
            ReviewStage.Posted,
            WorkflowStatus.Completed,
            PrLifecycleState.Open
        );

        // The fake provider always re-reads "head-7"/"base-7"; an authorization describing a different
        // head simulates one that has gone stale relative to the PR's current state.
        SeedRerunAuthorization(
            database,
            completedRun.RepoId,
            "7",
            "head-7-stale",
            "base-7",
            completedRun.Id,
            "rerun:7:deleted-branch-sha"
        );

        var second = await RunSinglePrCommand.RunAsync(
            Options(RepoKey),
            [provider],
            store,
            orchestrator,
            NullLogger.Instance,
            repoKey: RepoKey,
            prId: "7",
            approvedHeadSha: "head-7",
            approvedBaseSha: "base-7",
            CancellationToken.None,
            commentReaders: [provider]
        );

        second.Admitted.Should().BeFalse();
        second.RejectionReason.Should().Be(RunSinglePrRejectionReason.AlreadyCompleted);
        runner.Attempts.Should().Be(1, "a stale authorization must never let a rerun through");
        store
            .ListReviewRuns(ReviewStage.Discovered, 100)
            .Should()
            .ContainSingle("no row may be created for a rejected redo attempt");
    }

    [Fact]
    public async Task Never_enables_polling_or_saves_a_cursor_even_when_it_does_not_enable_the_repo_in_options()
    {
        // The command's own EnabledRepos parameter is only ever used to resolve/validate the allow-list; it
        // must never be mutated or persisted, and PrPollingService is never constructed by this path at all.
        using var database = new TempSqliteDatabase();
        using var store = new ReviewStore(database.ConnectionString);
        var provider = new MockPrProvider("github", [OpenPr()], SeedCursor());
        var runner = new StatusRunner(WorkflowInvocationStatus.Completed);
        var orchestrator = new PrOrchestrator(store, runner, NullLogger<PrOrchestrator>.Instance);
        var options = Options(RepoKey);

        _ = await RunSinglePrCommand.RunAsync(
            options,
            [provider],
            store,
            orchestrator,
            NullLogger.Instance,
            repoKey: RepoKey,
            prId: "7",
            approvedHeadSha: "head-7",
            approvedBaseSha: "base-7",
            CancellationToken.None,
            commentReaders: [provider]
        );

        options.EnabledRepos.Should().BeEquivalentTo([RepoKey], "the command must not mutate the allow-list");
    }

    [Fact]
    public async Task Collect_only_admission_defers_the_existing_discussion_until_after_review()
    {
        // Existing comments must not reach an independent reviewer before its findings are frozen.
        using var database = new TempSqliteDatabase();
        using var store = new ReviewStore(database.ConnectionString);
        var provider = new MockPrProvider("github", [OpenPr()], SeedCursor())
        {
            ExistingComments =
            [
                new ExistingReviewComment(
                    Path: "src/Foo.cs",
                    Line: "10",
                    Body: "Pre-existing human comment.",
                    Author: "reviewer",
                    ProviderCommentId: "comment-1",
                    ProviderVersion: "v1"
                ),
            ],
        };
        var runner = new CapturingRunner(WorkflowInvocationStatus.Completed);
        var orchestrator = new PrOrchestrator(store, runner, NullLogger<PrOrchestrator>.Instance);

        var result = await RunSinglePrCommand.RunAsync(
            Options(RepoKey),
            [provider],
            store,
            orchestrator,
            NullLogger.Instance,
            repoKey: RepoKey,
            prId: "7",
            approvedHeadSha: "head-7",
            approvedBaseSha: "base-7",
            CancellationToken.None,
            commentReaders: [provider]
        );

        result.Admitted.Should().BeTrue();
        runner.LastFrozenContext.Should().NotBeNull();
        runner.LastFrozenContext!["DiscussionDeferred"]!.GetValue<bool>().Should().BeTrue();
        runner.LastFrozenContext.Should().NotContainKey("CommentBaseline").And.NotContainKey("CommentWindow");
    }

    private sealed class CapturingRunner(WorkflowInvocationStatus status) : IReviewWorkflowRunner
    {
        public JsonObject? LastFrozenContext { get; private set; }

        public Task<WorkflowInvocationStatus> RunAsync(
            ReviewRun run,
            WorkflowRound? round,
            JsonObject frozenContext,
            CancellationToken cancellationToken
        )
        {
            LastFrozenContext = frozenContext;
            return Task.FromResult(status);
        }

        public Task<WorkflowInvocationStatus> ResumeAsync(
            ReviewRun run,
            WorkflowRound? round,
            CancellationToken cancellationToken
        ) => Task.FromResult(status);

        public Task<WorkflowInvocationStatus> RunOrResumeAsync(
            ReviewRun run,
            WorkflowRound? round,
            JsonObject currentContext,
            CancellationToken cancellationToken
        ) => RunAsync(run, round, currentContext, cancellationToken);

        public Task<JsonObject> ReadFrozenContextAsync(
            ReviewRun run,
            WorkflowRound? round,
            CancellationToken cancellationToken
        ) => Task.FromResult(new JsonObject());

        public Task RecoverActiveWorkspacesAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public bool HasFrozenContext(ReviewRun run, WorkflowRound? round) => true;
    }

    private sealed class StatusRunner(WorkflowInvocationStatus status) : IReviewWorkflowRunner
    {
        public int Attempts { get; private set; }

        public Task<WorkflowInvocationStatus> RunAsync(
            ReviewRun run,
            WorkflowRound? round,
            JsonObject frozenContext,
            CancellationToken cancellationToken
        )
        {
            Attempts++;
            return Task.FromResult(status);
        }

        public Task<WorkflowInvocationStatus> ResumeAsync(
            ReviewRun run,
            WorkflowRound? round,
            CancellationToken cancellationToken
        )
        {
            Attempts++;
            return Task.FromResult(status);
        }

        public Task<WorkflowInvocationStatus> RunOrResumeAsync(
            ReviewRun run,
            WorkflowRound? round,
            JsonObject currentContext,
            CancellationToken cancellationToken
        ) => RunAsync(run, round, currentContext, cancellationToken);

        public Task<JsonObject> ReadFrozenContextAsync(
            ReviewRun run,
            WorkflowRound? round,
            CancellationToken cancellationToken
        ) => Task.FromResult(new JsonObject());

        public Task RecoverActiveWorkspacesAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public bool HasFrozenContext(ReviewRun run, WorkflowRound? round) => true;
    }
}
