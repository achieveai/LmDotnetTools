using System.Text.Json;
using CodeReviewDaemon.Sample.Agents;
using CodeReviewDaemon.Sample.Configuration;
using CodeReviewDaemon.Sample.Orchestration;
using CodeReviewDaemon.Sample.Persistence;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeReviewDaemon.Sample.Tests.Orchestration;

/// <summary>
/// Task 9 — the executor builds a <see cref="ReviewToolContext"/> for the primary review only when
/// <see cref="CodeReviewDaemonOptions.EnableToolAssistedReview"/> is on, and DEGRADES to a null context
/// (diff-only) rather than failing the stage when the per-run session cannot be provisioned.
/// </summary>
public sealed class ReviewToolContextBuildTests
{
    [Fact]
    public async Task Reviewed_ProvisionerThrows_DegradesToDiffOnly()
    {
        using var db = new TempSqliteDatabase();
        var store = new ReviewStore(db.ConnectionString);
        var factory = new FakeReviewAgentLoopFactory();
        var provisioner = new ThrowingProvisioner();
        var executor = BuildExecutor(
            store, factory, new CodeReviewDaemonOptions { EnableToolAssistedReview = true }, provisioner);
        var run = SeedRunWithContext(store);

        // The primary review still runs (diff-only) rather than throwing out of the stage.
        await executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);

        // A 'review' artifact was persisted — the run degraded instead of failing.
        store.GetArtifacts(run.Id)
            .Should().Contain(a => a.ArtifactKind == DaemonReviewStageExecutor.ReviewArtifactKind);

        // ...and the loop factory received a null tool context — proving the run actually took the
        // degrade path, not merely that it happened to succeed for an unrelated reason.
        factory.ToolContexts.Should().ContainSingle().Which.Should().BeNull();
    }

    [Fact]
    public async Task Reviewed_ProvisionerSucceeds_PassesAPopulatedToolContextToTheLoopFactory()
    {
        using var db = new TempSqliteDatabase();
        var store = new ReviewStore(db.ConnectionString);
        var factory = new FakeReviewAgentLoopFactory();
        var options = new CodeReviewDaemonOptions { EnableToolAssistedReview = true };
        var provisioner = new FakeReviewSessionProvisioner("session-abc");
        var executor = BuildExecutor(store, factory, options, provisioner);
        var run = SeedRunWithContext(store);

        await executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);

        var toolContext = factory.ToolContexts.Should().ContainSingle().Subject;
        toolContext.Should().NotBeNull();
        toolContext!.SessionId.Should().Be("session-abc");
        toolContext.ReadOnlyToolAllowList.Should().BeEquivalentTo(options.ReadOnlyToolAllowList);
        toolContext.SubAgentOptions.Should().BeNull("sub-agents are attached in a later task");
    }

    [Fact]
    public async Task Reviewed_ToolAssistedReviewDisabled_NeverConsultsTheProvisioner()
    {
        using var db = new TempSqliteDatabase();
        var store = new ReviewStore(db.ConnectionString);
        var factory = new FakeReviewAgentLoopFactory();
        var provisioner = new ThrowingProvisioner();
        var executor = BuildExecutor(
            store, factory, new CodeReviewDaemonOptions { EnableToolAssistedReview = false }, provisioner);
        var run = SeedRunWithContext(store);

        await executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);

        factory.ToolContexts.Should().ContainSingle().Which.Should().BeNull();
    }

    private static DaemonReviewStageExecutor BuildExecutor(
        ReviewStore store,
        FakeReviewAgentLoopFactory factory,
        CodeReviewDaemonOptions options,
        IReviewSessionProvisioner provisioner) =>
        new(
            store,
            factory,
            new FakeSandboxCommandRunner(),
            new FakeSandboxFileSystem(),
            options,
            [new FakeReviewCommentPublisher("github")],
            NullLoggerFactory.Instance,
            provisioner);

    /// <summary>
    /// Seeds a run + the 'review-context' artifact the Reviewed stage reads, so the test can drive
    /// <see cref="ReviewStage.Reviewed"/> directly without first running <see cref="ReviewStage.ContextReady"/>
    /// (which — on the tool-assisted path — would itself consult the provisioner for the checkout git).
    /// </summary>
    private static ReviewRun SeedRunWithContext(ReviewStore store)
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
            ArtifactSchemaVersion = DaemonReviewStageExecutor.ContextArtifactSchemaVersion,
            ArtifactKind = DaemonReviewStageExecutor.ContextArtifactKind,
            Provider = "github",
            Payload = JsonSerializer.Serialize(new ContextArtifactPayload(
                run.PrId, run.BaseSha, run.HeadSha, "diff --git a/Foo.cs b/Foo.cs\n+ var x = bar;")),
        });

        return run;
    }

    /// <summary>Simulates an unreachable sandbox gateway/session (design §7 capability gap).</summary>
    private sealed class ThrowingProvisioner : IReviewSessionProvisioner
    {
        public Task<ReviewRunSession> GetOrCreateAsync(ReviewRun run, CancellationToken ct) =>
            throw new InvalidOperationException("sandbox gateway unreachable");

        public Task DestroyAsync(ReviewRun run, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakeReviewSessionProvisioner(string sessionId) : IReviewSessionProvisioner
    {
        public Task<ReviewRunSession> GetOrCreateAsync(ReviewRun run, CancellationToken ct) =>
            Task.FromResult(new ReviewRunSession(
                sessionId, $"/workspace/{sessionId}", new FakeSandboxCommandRunner(), new FakeSandboxFileSystem()));

        public Task DestroyAsync(ReviewRun run, CancellationToken ct) => Task.CompletedTask;
    }
}
