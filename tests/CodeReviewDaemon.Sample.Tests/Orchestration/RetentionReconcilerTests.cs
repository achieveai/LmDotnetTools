using CodeReviewDaemon.Sample.Configuration;
using CodeReviewDaemon.Sample.Orchestration;
using CodeReviewDaemon.Sample.Persistence;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using CodeReviewDaemon.Sample.Workspace;
using CodeReviewDaemon.Sample.Workspace.Sandbox;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeReviewDaemon.Sample.Tests.Orchestration;

/// <summary>
/// Thread RtEzw — a failed ReviewBot notes push is left as a non-terminal <see cref="OutboxStatus.Pending"/>
/// <c>push-reviewbot</c> row, and <see cref="IReviewStageExecutor.ReconcilePendingRetentionAsync"/> is the
/// consumer that makes the "left non-terminal so reconcile retries" contract real: it rebuilds the push from
/// the durably-persisted review artifact (independent of the pooled slot, which is stripped/returned before
/// the reconcile runs) and terminalizes the row on success. The Posted stage deliberately does NOT throw on a
/// push failure — the review was already delivered — so this out-of-band reconciler, not a stage retry, is
/// what recovers retention.
/// </summary>
public sealed class RetentionReconcilerTests
{
    private const string ReviewBotRepoUrl = "https://github.com/acme/AchieveAiReviews.git";
    private const string ReviewBranch = "review/lmdotnettools-118";

    [Fact]
    public async Task Reconcile_retries_a_failed_push_and_marks_the_row_Posted()
    {
        using var db = new TempSqliteDatabase();
        var store = new ReviewStore(db.ConnectionString);
        var run = SeedRun(store);

        // Phase 1 — the run's Posted stage pushes retention, but the push (and its rebase-retry) fail, so the
        // push-reviewbot row is left Pending. The stage itself does NOT throw (delivery already happened).
        var failingHost = HostRunner()
            .OnArgvContains($"push origin {ReviewBranch}", new SandboxCommandResult(1, string.Empty, "rejected"))
            .OnArgvContains($"pull --rebase origin {ReviewBranch}", new SandboxCommandResult(1, string.Empty, "cannot rebase"));
        await RunThroughPostedAsync(store, run, failingHost, HostFileSystem());

        store.GetPendingOutboxByOperation(DaemonReviewStageExecutor.PushReviewBotOperation)
            .Should().ContainSingle("the failed push left exactly one Pending push-reviewbot row to reconcile")
            .Which.ReviewRunId.Should().Be(run.Id);

        // Phase 2 — a later reconcile pass (a fresh executor over the SAME store, e.g. after a restart) with a
        // healthy remote rebuilds the push from the persisted review artifact and terminalizes the row.
        var healthyHost = HostRunner();
        var reconcileExecutor = HostExecutor(store, healthyHost, HostFileSystem());

        await reconcileExecutor.ReconcilePendingRetentionAsync(CancellationToken.None);

        store.GetPendingOutboxByOperation(DaemonReviewStageExecutor.PushReviewBotOperation)
            .Should().BeEmpty("the reconcile pushed the notes and terminalized the row");
        store.GetOutboxForRun(run.Id)
            .Single(o => o.Operation == DaemonReviewStageExecutor.PushReviewBotOperation)
            .Status.Should().Be(OutboxStatus.Posted);
        healthyHost.Commands.Select(c => string.Join(' ', c.Argv))
            .Should().Contain(c => c.Contains($"push origin {ReviewBranch}"), "the reconcile re-pushed on the host runner");
    }

    [Fact]
    public async Task Reconcile_does_not_throw_and_leaves_the_row_Pending_when_the_artifact_is_unreconstructable()
    {
        using var db = new TempSqliteDatabase();
        var store = new ReviewStore(db.ConnectionString);
        var run = SeedRun(store); // a run with NO persisted review artifact — ReadReviewText will throw

        _ = store.EnqueueOutbox(new OutboxEntry
        {
            IdempotencyKey = "v1:github:push:orphan",
            Provider = "github",
            ReviewRunId = run.Id,
            Operation = DaemonReviewStageExecutor.PushReviewBotOperation,
            ArtifactKind = "review",
            Status = OutboxStatus.Pending,
        });

        var executor = HostExecutor(store, HostRunner(), HostFileSystem());

        // Degrade-not-throw: a per-row failure must not surface out of the sweep-cadence reconcile...
        var act = () => executor.ReconcilePendingRetentionAsync(CancellationToken.None);
        await act.Should().NotThrowAsync();

        // ...and the row stays Pending (it will back off + eventually park, not vanish).
        store.GetPendingOutboxByOperation(DaemonReviewStageExecutor.PushReviewBotOperation)
            .Should().ContainSingle("an unreconstructable row is left Pending, not dropped");
    }

    [Fact]
    public void GetPendingOutboxByOperation_returns_only_pending_rows_for_that_operation()
    {
        using var db = new TempSqliteDatabase();
        var store = new ReviewStore(db.ConnectionString);
        var run = SeedRun(store);

        _ = store.EnqueueOutbox(Row("v1:push:pending", DaemonReviewStageExecutor.PushReviewBotOperation, OutboxStatus.Pending, run.Id));
        _ = store.EnqueueOutbox(Row("v1:push:posted", DaemonReviewStageExecutor.PushReviewBotOperation, OutboxStatus.Posted, run.Id));
        _ = store.EnqueueOutbox(Row("v1:post:pending", "post-review-comment", OutboxStatus.Pending, run.Id));

        var pending = store.GetPendingOutboxByOperation(DaemonReviewStageExecutor.PushReviewBotOperation);

        pending.Should().ContainSingle("only the Pending push-reviewbot row matches (not the Posted one, not the other operation)")
            .Which.IdempotencyKey.Should().Be("v1:push:pending");
    }

    private static OutboxEntry Row(string key, string operation, OutboxStatus status, long runId) => new()
    {
        IdempotencyKey = key,
        Provider = "github",
        ReviewRunId = runId,
        Operation = operation,
        ArtifactKind = "review",
        Status = status,
    };

    private static async Task RunThroughPostedAsync(
        ReviewStore store, ReviewRun run, FakeSandboxCommandRunner host, FakeSandboxFileSystem hostFileSystem)
    {
        var executor = HostExecutor(store, host, hostFileSystem);
        await executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);
        await executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);
        await executor.ExecuteStageAsync(ReviewStage.Judged, run, CancellationToken.None);
        await executor.ExecuteStageAsync(ReviewStage.Posted, run, CancellationToken.None);
    }

    private static DaemonReviewStageExecutor HostExecutor(
        ReviewStore store, FakeSandboxCommandRunner host, FakeSandboxFileSystem hostFileSystem)
    {
        // The boot-lifetime sandbox runner fails on anything ReviewBot-shaped, so a regression back onto the
        // sandbox path would fail loudly rather than silently pass (mirrors HostRetentionTests).
        var sandbox = new FakeSandboxCommandRunner()
            .OnArgvContains("rev-parse --is-inside-work-tree", new SandboxCommandResult(1, string.Empty, "not a git repo"))
            .OnArgvContains("diff", new SandboxCommandResult(0, "diff --git a/Foo.cs b/Foo.cs\n+ var x = bar;", string.Empty))
            .OnArgvContains("AchieveAiReviews", new SandboxCommandResult(1, string.Empty, "must not reach the sandbox"));

        return new DaemonReviewStageExecutor(
            store,
            new FakeReviewAgentLoopFactory(),
            sandbox,
            new FakeSandboxFileSystem(),
            new CodeReviewDaemonOptions { ReviewBotRepoUrl = ReviewBotRepoUrl },
            NullLoggerFactory.Instance,
            hostRetention: new HostRetentionWorkspace(host, hostFileSystem, "/host/reviewbot"));
    }

    private static FakeSandboxCommandRunner HostRunner() => new FakeSandboxCommandRunner()
        .OnArgvContains($"rev-parse {ReviewBranch}", new SandboxCommandResult(0, "f00dcafef00dcafe\n", string.Empty));

    private static FakeSandboxFileSystem HostFileSystem() => new FakeSandboxFileSystem()
        .Seed("/host/reviewbot/README.md", "# ReviewBot")
        .Seed("/host/reviewbot/PRs/.gitkeep", string.Empty)
        .Seed("/host/reviewbot/KnowledgeBase/.gitkeep", string.Empty)
        .Seed("/host/reviewbot/KnowledgeBase/_toc.md", "# Knowledge Base");

    private static ReviewRun SeedRun(ReviewStore store)
    {
        var repoId = store.EnsureRepo(new RepoIdentity
        {
            Provider = "github",
            OrgOrOwner = "achieveai",
            RepoName = "LmDotnetTools",
            RepoStableId = "repo-stable-1",
        });
        return store.CreateOrGetReviewRun(new ReviewRun
        {
            RepoId = repoId,
            PrId = "118",
            HeadSha = "head-sha",
            BaseSha = "base-sha",
            TriggerWatermark = "2026-06-29T12:34:56Z",
            ReviewKind = "full",
            VariantId = "primary",
            Mode = "collect-only",
            Stage = ReviewStage.Discovered,
            WorkflowStatus = WorkflowStatus.Running,
            PrLifecycleState = PrLifecycleState.Open,
        });
    }
}
