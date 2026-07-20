using System.Text.Json;
using CodeReviewDaemon.Sample.Configuration;
using CodeReviewDaemon.Sample.Orchestration;
using CodeReviewDaemon.Sample.Persistence;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using CodeReviewDaemon.Sample.Workspace;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeReviewDaemon.Sample.Tests.Orchestration;

/// <summary>
/// Task 18 — a run's per-run sandbox session (and its host-dir) must not outlive the run: once a run
/// reaches the terminal <see cref="ReviewStage.Posted"/> stage, the executor calls
/// <see cref="IReviewSessionProvisioner.DestroyAsync(ReviewRun, System.Threading.CancellationToken)"/> so the session is torn down (design §7). The
/// diff-only path (no provisioner consulted at all) must stay unaffected.
/// </summary>
public sealed class RunCleanupTests
{
    [Fact]
    public async Task Posted_TerminalCleanup_DestroysSessionAndRemovesHostDir()
    {
        using var db = new TempSqliteDatabase();
        var store = new ReviewStore(db.ConnectionString);
        var provisioner = new RecordingProvisioner();
        var executor = BuildExecutor(store, new CodeReviewDaemonOptions { EnableToolAssistedReview = true }, provisioner);
        var run = SeedRunReadyToPost(store);

        await executor.ExecuteStageAsync(ReviewStage.Posted, run, CancellationToken.None);

        provisioner.DestroyCalls.Should().Contain(r => r.Id == run.Id);
    }

    [Fact]
    public async Task Posted_DiffOnly_NeverConsultsTheProvisionerForCleanup()
    {
        using var db = new TempSqliteDatabase();
        var store = new ReviewStore(db.ConnectionString);
        var provisioner = new RecordingProvisioner();
        var executor = BuildExecutor(store, new CodeReviewDaemonOptions { EnableToolAssistedReview = false }, provisioner);
        var run = SeedRunReadyToPost(store);

        await executor.ExecuteStageAsync(ReviewStage.Posted, run, CancellationToken.None);

        provisioner.DestroyCalls.Should().BeEmpty();
    }

    private static DaemonReviewStageExecutor BuildExecutor(
        ReviewStore store, CodeReviewDaemonOptions options, IReviewSessionProvisioner provisioner) =>
        new(
            store,
            new FakeReviewAgentLoopFactory(),
            new FakeSandboxCommandRunner(),
            new FakeSandboxFileSystem(),
            options,
            NullLoggerFactory.Instance,
            provisioner);

    /// <summary>
    /// Seeds a run + the 'review' artifact the Posted stage reads, so the test can drive
    /// <see cref="ReviewStage.Posted"/> directly without first running ContextReady/Reviewed (mirrors
    /// the seeding pattern in ReviewToolContextBuildTests).
    /// </summary>
    private static ReviewRun SeedRunReadyToPost(ReviewStore store)
    {
        var repoId = store.EnsureRepo(new RepoIdentity
        {
            Provider = "github",
            OrgOrOwner = "achieveai",
            RepoName = "LmDotnetTools",
            RepoStableId = "repo-stable-1",
        });
        var run = store.CreateOrGetReviewRun(new ReviewRun
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

        _ = store.AddArtifact(new ReviewArtifact
        {
            ReviewRunId = run.Id,
            ArtifactSchemaVersion = DaemonReviewStageExecutor.ReviewArtifactSchemaVersion,
            ArtifactKind = DaemonReviewStageExecutor.ReviewArtifactKind,
            Provider = "github",
            Payload = JsonSerializer.Serialize(new ReviewArtifactPayload("Looks fine.", "run-1", "primary")),
        });

        return run;
    }

    /// <summary>Records <c>DestroyAsync</c> calls for the terminal-cleanup assertion (Task 18).</summary>
    private sealed class RecordingProvisioner : IReviewSessionProvisioner
    {
        public List<ReviewRun> DestroyCalls { get; } = [];

        public Task<ReviewRunSession?> GetOrCreateAsync(ReviewRun run, CancellationToken ct) =>
            Task.FromResult<ReviewRunSession?>(new ReviewRunSession(
                $"session-{run.Id}",
                $"/workspace/review-run-{run.Id}",
                new FakeSandboxCommandRunner(),
                new FakeSandboxFileSystem()));

        public Task<ReviewRunSession?> GetOrCreateForSlotAsync(ReviewRun run, ReviewSlot slot, CancellationToken ct) =>
            GetOrCreateAsync(run, ct);

        public Task<bool> DestroyAsync(ReviewRun run, CancellationToken ct)
        {
            DestroyCalls.Add(run);
            return Task.FromResult(true);
        }

        public Task<bool> DestroyAsync(long runId, CancellationToken ct) => Task.FromResult(true);
    }
}
