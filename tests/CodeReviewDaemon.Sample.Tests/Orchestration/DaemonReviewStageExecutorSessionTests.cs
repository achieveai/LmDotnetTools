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
/// Task 7 — the executor's checkout git must route through the per-run sandbox session (Task 6's
/// <see cref="IReviewSessionProvisioner"/>) when <see cref="CodeReviewDaemonOptions.EnableToolAssistedReview"/>
/// is on, and through the injected (boot-lifetime) runner/fs otherwise — the diff-only path must stay
/// exactly as it was before this change.
/// </summary>
public sealed class DaemonReviewStageExecutorSessionTests
{
    [Fact]
    public async Task FetchContext_ToolAssisted_ResolvesPerRunSession()
    {
        using var db = new TempSqliteDatabase();
        var store = new ReviewStore(db.ConnectionString);
        var provisioner = new RecordingProvisioner();
        var executor = BuildExecutor(store, new CodeReviewDaemonOptions { EnableToolAssistedReview = true }, provisioner);
        var run = SeedRun(store);

        await executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);

        provisioner.GetOrCreateCalls.Should().Be(1);
    }

    [Fact]
    public async Task FetchContext_DiffOnly_DoesNotProvisionSession()
    {
        using var db = new TempSqliteDatabase();
        var store = new ReviewStore(db.ConnectionString);
        var provisioner = new RecordingProvisioner();
        var executor = BuildExecutor(store, new CodeReviewDaemonOptions { EnableToolAssistedReview = false }, provisioner);
        var run = SeedRun(store);

        await executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);

        provisioner.GetOrCreateCalls.Should().Be(0);
    }

    private static DaemonReviewStageExecutor BuildExecutor(
        ReviewStore store, CodeReviewDaemonOptions options, IReviewSessionProvisioner provisioner)
    {
        var runner = new FakeSandboxCommandRunner()
            .OnArgvContains("rev-parse --is-inside-work-tree", new SandboxCommandResult(1, string.Empty, "not a git repo"))
            .OnArgvContains("diff", new SandboxCommandResult(0, "diff --git a/Foo.cs b/Foo.cs\n+ var x = bar;", string.Empty));
        var fileSystem = new FakeSandboxFileSystem();

        return new DaemonReviewStageExecutor(
            store,
            new FakeReviewAgentLoopFactory(),
            runner,
            fileSystem,
            options,
            NullLoggerFactory.Instance,
            provisioner);
    }

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
            TriggerWatermark = "wm-1",
            ReviewKind = "full",
            VariantId = "primary",
            Mode = "collect-only",
            Stage = ReviewStage.Discovered,
            WorkflowStatus = WorkflowStatus.Running,
            PrLifecycleState = PrLifecycleState.Open,
        });
    }

    /// <summary>Records <c>GetOrCreateAsync</c> calls and hands back a session whose fake runner/fs
    /// satisfy the same rev-parse/diff scripting the diff-only path relies on. The slot-mount entry point
    /// is unused on this non-pooled path (no lease is ever recorded) but implemented so the fake satisfies
    /// the interface.</summary>
    private sealed class RecordingProvisioner : IReviewSessionProvisioner
    {
        public int GetOrCreateCalls { get; private set; }
        public int GetOrCreateForSlotCalls { get; private set; }

        public Task<ReviewRunSession?> GetOrCreateAsync(ReviewRun run, CancellationToken ct)
        {
            GetOrCreateCalls++;
            var runner = new FakeSandboxCommandRunner()
                .OnArgvContains("rev-parse --is-inside-work-tree", new SandboxCommandResult(1, string.Empty, "not a git repo"))
                .OnArgvContains("diff", new SandboxCommandResult(0, "diff --git a/Foo.cs b/Foo.cs\n+ var x = bar;", string.Empty));
            return Task.FromResult<ReviewRunSession?>(new ReviewRunSession(
                $"session-{run.Id}", $"/workspace/review-run-{run.Id}", runner, new FakeSandboxFileSystem()));
        }

        public Task<ReviewRunSession?> GetOrCreateForSlotAsync(ReviewRun run, ReviewSlot slot, CancellationToken ct)
        {
            GetOrCreateForSlotCalls++;
            return GetOrCreateAsync(run, ct);
        }

        public Task<bool> DestroyAsync(ReviewRun run, CancellationToken ct) => Task.FromResult(true);

        public Task<bool> DestroyAsync(long runId, CancellationToken ct) => Task.FromResult(true);
    }
}
