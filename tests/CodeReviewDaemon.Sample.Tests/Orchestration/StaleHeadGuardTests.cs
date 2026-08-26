using System.Text.Json;
using AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox;
using CodeReviewDaemon.Sample.Agents;
using CodeReviewDaemon.Sample.Configuration;
using CodeReviewDaemon.Sample.Orchestration;
using CodeReviewDaemon.Sample.Persistence;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using CodeReviewDaemon.Sample.Workspace.Sandbox;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeReviewDaemon.Sample.Tests.Orchestration;

/// <summary>
/// The head-currency guard that stands between a review and the PR author (#331). A run is created from a
/// POLL SNAPSHOT and executes minutes or hours later; in between the branch can be force-pushed, and the
/// commits the run recorded stop being the PR. Reviewing them anyway is not a stale cosmetic — the findings
/// are attributed to a PR that never contained the code, at a severity the author cannot act on.
/// <para>
/// The guard cannot be satisfied by re-reading the daemon's own <c>review_run</c> row: <c>head_sha</c> is
/// part of a run's identity and is INSERTed once and never UPDATEd, so a self-read compares the suspect
/// value with itself and can only ever agree. Only the PR host can contradict it, so these tests pin that
/// the host is actually asked, and that the three answers it can give are told apart — moved means refuse,
/// unchanged means proceed, unreachable means indeterminate rather than stale.
/// </para>
/// </summary>
public sealed class StaleHeadGuardTests
{
    [Fact]
    public async Task HeadMovedSinceThePollThatCreatedTheRun_RefusesToSynthesizeAndPostsNothing()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        // The force-push case exactly: the run holds the pre-push commit, the host reports the post-push one.
        var provider = new MockPrProvider("github", [], Cursor()) { CurrentHeadSha = "head-after-force-push" };
        var executor = BuildExecutor(store, new FakeReviewAgentLoopFactory(), [provider]);
        var run = SeedRunWithContext(store, prId: "325", headSha: "head-before-force-push");

        var act = () => executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should()
            .Contain("head-before-force-push").And.Contain("head-after-force-push");
        // The artifact is the load-bearing assertion, not the throw: the posting arm reads
        // ReviewArtifactKind, so a review that produced one is a review that could reach the author.
        store.GetArtifacts(run.Id)
            .Should().NotContain(a => a.ArtifactKind == DaemonReviewStageExecutor.ReviewArtifactKind);
    }

    [Fact]
    public async Task HeadUnchanged_ReviewProceeds_AndTheHostWasActuallyAsked()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var provider = new MockPrProvider("github", [], Cursor()) { CurrentHeadSha = "head-325" };
        var executor = BuildExecutor(store, new FakeReviewAgentLoopFactory(), [provider]);
        var run = SeedRunWithContext(store, prId: "325", headSha: "head-325");

        await executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);

        store.GetArtifacts(run.Id)
            .Should().Contain(a => a.ArtifactKind == DaemonReviewStageExecutor.ReviewArtifactKind);
        // Non-vacuity: a guard that never calls the host passes this test for the wrong reason, and would
        // pass the moved case too if the refusal came from anywhere else.
        provider.HeadShaCalls.Should().BeGreaterThan(0, "the recorded head is only checkable against the host");
    }

    [Fact]
    public async Task HostUnreachable_IsIndeterminate_NotStale_SoTheReviewStillCompletes()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var executor = BuildExecutor(
            store, new FakeReviewAgentLoopFactory(), [new UnreachablePrProvider("github")]);
        var run = SeedRunWithContext(store, prId: "325", headSha: "head-325");

        await executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);

        // "The host could not be reached" is not evidence the head moved. Failing the run on it would let a
        // momentary API blip discard a review that took minutes and cost tokens, and would do it on every
        // run for as long as the blip lasted.
        store.GetArtifacts(run.Id)
            .Should().Contain(a => a.ArtifactKind == DaemonReviewStageExecutor.ReviewArtifactKind);
    }

    [Fact]
    public async Task HostReportsNoHeadForThePr_IsAlsoIndeterminate_RatherThanComparedAgainstEmpty()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        // A payload with no head SHA at all — distinct from a payload reporting a DIFFERENT one.
        var provider = new MockPrProvider("github", [], Cursor()) { CurrentHeadSha = null };
        var executor = BuildExecutor(store, new FakeReviewAgentLoopFactory(), [provider]);
        var run = SeedRunWithContext(store, prId: "325", headSha: "head-325");

        await executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);

        store.GetArtifacts(run.Id)
            .Should().Contain(a => a.ArtifactKind == DaemonReviewStageExecutor.ReviewArtifactKind);
    }

    [Fact]
    public async Task ProviderRegisteredForADifferentHost_IsNotConsultedForThisRun()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        // An ADO provider must not be asked about a GitHub PR: its answer would be about a different PR 325.
        var ado = new MockPrProvider("ado", [], Cursor()) { CurrentHeadSha = "some-other-repos-head" };
        var executor = BuildExecutor(store, new FakeReviewAgentLoopFactory(), [ado]);
        var run = SeedRunWithContext(store, prId: "325", headSha: "head-325");

        await executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);

        ado.HeadShaCalls.Should().Be(0);
        store.GetArtifacts(run.Id)
            .Should().Contain(a => a.ArtifactKind == DaemonReviewStageExecutor.ReviewArtifactKind);
    }

    [Fact]
    public async Task TheCompositionRootActuallyInjectsTheProviders_NotJustTheOptionalDefault()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var provider = new MockPrProvider("github", [], Cursor()) { CurrentHeadSha = "head-after-force-push" };
        var services = new ServiceCollection();
        _ = services.AddSingleton(store);
        _ = services.AddSingleton<IReviewAgentLoopFactory>(new FakeReviewAgentLoopFactory());
        _ = services.AddSingleton<ISandboxCommandRunner>(new FakeSandboxCommandRunner());
        _ = services.AddSingleton<ISandboxFileSystem>(new FakeSandboxFileSystem());
        _ = services.AddSingleton(new CodeReviewDaemonOptions());
        _ = services.AddSingleton<IReviewCommentPublisher>(new FakeReviewCommentPublisher("github"));
        _ = services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        // The registration Program.cs makes at its own IPrProvider lines — the thing under test is whether it
        // REACHES the executor.
        _ = services.AddSingleton<IPrProvider>(provider);
        using var sp = services.BuildServiceProvider();

        // Exactly how Program.cs builds it: ActivatorUtilities with the credential + gateway url passed
        // explicitly and everything else resolved from DI.
        var executor = ActivatorUtilities.CreateInstance<DaemonReviewStageExecutor>(
            sp, default(SandboxCredential), "http://localhost:5051");
        var run = SeedRunWithContext(store, prId: "325", headSha: "head-before-force-push");

        var act = () => executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);

        // `prProviders` is an OPTIONAL constructor parameter defaulting to an empty list, so a DI change that
        // stopped filling it would leave the guard silently vacuous again — compiling, passing every other
        // test in this file (they all hand the list in directly), and reviewing stale heads in production.
        // This is the only test that exercises the wiring rather than the logic.
        _ = await act.Should().ThrowAsync<InvalidOperationException>();
        provider.HeadShaCalls.Should().BeGreaterThan(0, "the injected provider must be the one consulted");
    }

    private static OpaqueCursor Cursor() => new()
    {
        Provider = "github",
        Scope = "achieveai/LmDotnetTools:open-prs",
        CursorVersion = PrPollingService.CursorVersion,
        CursorPayload = "{}",
    };

    private static DaemonReviewStageExecutor BuildExecutor(
        ReviewStore store,
        FakeReviewAgentLoopFactory factory,
        IReadOnlyList<IPrProvider> prProviders) =>
        new(
            store,
            factory,
            new FakeSandboxCommandRunner(),
            new FakeSandboxFileSystem(),
            new CodeReviewDaemonOptions(),
            [new FakeReviewCommentPublisher("github")],
            NullLoggerFactory.Instance,
            prProviders: prProviders);

    /// <summary>Seeds a run plus the <c>review-context</c> artifact the Reviewed stage reads, so the stage
    /// can be driven directly without first running ContextReady.</summary>
    private static ReviewRun SeedRunWithContext(ReviewStore store, string prId, string headSha)
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
            PrId = prId,
            HeadSha = headSha,
            BaseSha = $"base-{prId}",
            TriggerWatermark = $"wm-{prId}",
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

    /// <summary>A host that cannot be reached — NOT the same answer as a host reporting a different head.</summary>
    private sealed class UnreachablePrProvider(string provider) : IPrProvider
    {
        public string Provider { get; } = provider;

        public Task<PullRequestPage> ListOpenPullRequestsAsync(
            PrPollRequest request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("simulated provider outage");

        public Task<PrLifecycle> GetPrStateAsync(
            RepoIdentity repo, string prId, CancellationToken cancellationToken) =>
            throw new HttpRequestException("simulated provider outage");

        public Task<string?> GetCurrentHeadShaAsync(
            RepoIdentity repo, string prId, CancellationToken cancellationToken) =>
            throw new HttpRequestException("simulated provider outage");
    }
}
