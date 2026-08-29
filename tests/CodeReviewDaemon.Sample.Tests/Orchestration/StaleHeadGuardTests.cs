using System.Text.Json;
using AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox;
using CodeReviewDaemon.Sample.Agents;
using CodeReviewDaemon.Sample.Configuration;
using CodeReviewDaemon.Sample.Orchestration;
using CodeReviewDaemon.Sample.Persistence;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using CodeReviewDaemon.Sample.Workspace;
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
/// <para>
/// The delivery boundary has a second currency question with the same shape (#430): not "is this still the
/// code?" but "is this still a PR anybody can act on?". A PR that merges or closes between the Reviewed and
/// Posted stages used to be commented on regardless, because the only lifecycle check ran at synthesis and
/// read a persisted column stamped once at discovery. The lifecycle tests below live beside the head ones
/// because both guards stand at the same call site and share the same indeterminate rule — and because each
/// clause needs its own distinguishing case, which two families of tests in two files would not give.
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
            .Contain("head-before-force-push")
            .And.Contain("head-after-force-push");
        // The artifact is the load-bearing assertion, not the throw: the posting arm reads
        // ReviewArtifactKind, so a review that produced one is a review that could reach the author.
        store
            .GetArtifacts(run.Id)
            .Should()
            .NotContain(a => a.ArtifactKind == DaemonReviewStageExecutor.ReviewArtifactKind);
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

        store
            .GetArtifacts(run.Id)
            .Should()
            .Contain(a => a.ArtifactKind == DaemonReviewStageExecutor.ReviewArtifactKind);
        // Non-vacuity: a guard that never calls the host passes this test for the wrong reason, and would
        // pass the moved case too if the refusal came from anywhere else.
        provider.HeadShaCalls.Should().BeGreaterThan(0, "the recorded head is only checkable against the host");
    }

    [Fact]
    public async Task HostUnreachable_IsIndeterminate_NotStale_SoTheReviewStillCompletes()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var executor = BuildExecutor(store, new FakeReviewAgentLoopFactory(), [new UnreachablePrProvider("github")]);
        var run = SeedRunWithContext(store, prId: "325", headSha: "head-325");

        await executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);

        // "The host could not be reached" is not evidence the head moved. Failing the run on it would let a
        // momentary API blip discard a review that took minutes and cost tokens, and would do it on every
        // run for as long as the blip lasted.
        store
            .GetArtifacts(run.Id)
            .Should()
            .Contain(a => a.ArtifactKind == DaemonReviewStageExecutor.ReviewArtifactKind);
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

        store
            .GetArtifacts(run.Id)
            .Should()
            .Contain(a => a.ArtifactKind == DaemonReviewStageExecutor.ReviewArtifactKind);
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
        store
            .GetArtifacts(run.Id)
            .Should()
            .Contain(a => a.ArtifactKind == DaemonReviewStageExecutor.ReviewArtifactKind);
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
            sp,
            default(SandboxCredential),
            "http://localhost:5051"
        );
        var run = SeedRunWithContext(store, prId: "325", headSha: "head-before-force-push");

        var act = () => executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);

        // `prProviders` is an OPTIONAL constructor parameter defaulting to an empty list, so a DI change that
        // stopped filling it would leave the guard silently vacuous again — compiling, passing every other
        // test in this file (they all hand the list in directly), and reviewing stale heads in production.
        // This is the only test that exercises the wiring rather than the logic.
        _ = await act.Should().ThrowAsync<InvalidOperationException>();
        provider.HeadShaCalls.Should().BeGreaterThan(0, "the injected provider must be the one consulted");
    }

    [Fact]
    public async Task AnUncancelledTimeoutReadingTheHead_IsIndeterminate_NotAFaultedReview()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        using var logs = new CapturingLoggerFactory();
        var provider = new TimingOutPrProvider("github");
        var executor = BuildExecutor(store, new FakeReviewAgentLoopFactory(), [provider], logs);
        var run = SeedRunWithContext(store, prId: "325", headSha: "head-325");

        await executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);

        // A slow host is an unreachable host, not a moved head. Classifying by EXCEPTION TYPE instead of by
        // caller-token state sends this TaskCanceledException straight past the catch, and the review the
        // guard promises to continue is discarded over a transport blip.
        provider.HeadShaCalls.Should().Be(1, "the site under test is only reached if the host is consulted");
        logs.Capturing.WarningCount(TimedOutHeadReadLog).Should().Be(1);
        store
            .GetArtifacts(run.Id)
            .Should()
            .Contain(a => a.ArtifactKind == DaemonReviewStageExecutor.ReviewArtifactKind);
    }

    [Fact]
    public async Task CallerRequestedCancellationDuringTheHeadRead_Propagates_RatherThanReadingAsAnOutage()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        using var cts = new CancellationTokenSource();
        using var logs = new CapturingLoggerFactory();
        var provider = new CancellingPrProvider("github", cts);
        var executor = BuildExecutor(store, new FakeReviewAgentLoopFactory(), [provider], logs);
        var run = SeedRunWithContext(store, prId: "325", headSha: "head-325");

        var act = () => executor.ExecuteStageAsync(ReviewStage.Reviewed, run, cts.Token);

        // The other half of the same rule: shutdown must not be swallowed as "no head" and allowed to run a
        // whole review during a cancel.
        _ = await act.Should().ThrowAsync<OperationCanceledException>();
        provider
            .HeadShaCalls.Should()
            .Be(1, "a pre-cancelled token is refused before the read, which would pass this test vacuously");
        // The throw alone proves nothing: once the token is cancelled, a filter that swallowed this
        // cancellation still ends in an OperationCanceledException raised a few lines later, and still writes
        // no artifact. The SWALLOW's own log line is the only observable that tells the two apart.
        logs.Capturing.WarningCount(TimedOutHeadReadLog)
            .Should()
            .Be(0, "a caller-requested cancel is not a timeout and must not be reported as one");
        store
            .GetArtifacts(run.Id)
            .Should()
            .NotContain(a => a.ArtifactKind == DaemonReviewStageExecutor.ReviewArtifactKind);
    }

    /// <summary>The head-read swallow's own log line — the observable that separates it from a real cancel.</summary>
    private const string TimedOutHeadReadLog = "current head from github timed out while the daemon was not";

    [Fact]
    public async Task HeadMovesBetweenReviewedAndPosted_PublishesNothing_AndStillCleansUp()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        // The residual window #414 accepted: synthesis passed the guard, then the branch was force-pushed
        // before the terminal stage ran. Publishing now attributes findings to code the PR no longer contains.
        var provider = new MockPrProvider("github", [], Cursor()) { CurrentHeadSha = "head-after-force-push" };
        var publisher = new FakeReviewCommentPublisher("github");
        var provisioner = new RecordingProvisioner();
        var executor = BuildPostingExecutor(store, publisher, [provider], provisioner);
        var run = SeedRunReadyToPost(store, headSha: "head-before-force-push");

        await executor.ExecuteStageAsync(ReviewStage.Posted, run, CancellationToken.None);

        provider.HeadShaCalls.Should().Be(1, "only the host can contradict the recorded head at this boundary");
        publisher.PostCount.Should().Be(0, "the publisher is the last thing between a stale review and the author");
        store
            .GetOutboxForRun(run.Id)
            .Should()
            .NotContain(
                o => o.Operation == ReviewPoster.PostReviewCommentOperation,
                "a refusal must not leave a delivery row claiming the review reached the PR"
            );
        // A refusal that leaked the sandbox session would trade a stale comment for a stuck pooled slot.
        provisioner.DestroyCalls.Should().Contain(r => r.Id == run.Id, "cleanup runs on the refusal path too");
    }

    [Fact]
    public async Task HeadUnchangedAtThePostingBoundary_PostsExactlyOnce()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var provider = new MockPrProvider("github", [], Cursor()) { CurrentHeadSha = "head-118" };
        var publisher = new FakeReviewCommentPublisher("github");
        var executor = BuildPostingExecutor(store, publisher, [provider], new RecordingProvisioner());
        var run = SeedRunReadyToPost(store, headSha: "head-118");

        await executor.ExecuteStageAsync(ReviewStage.Posted, run, CancellationToken.None);

        // The re-check must not cost a legitimate delivery, and must not double-post by running the poster twice.
        provider.HeadShaCalls.Should().Be(1);
        publisher.PostCount.Should().Be(1);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AnIndeterminateAnswerAtThePostingBoundary_StillPosts(bool hostUnreachable)
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        // Two indeterminate answers, one rule: a failed read is not evidence of a move. Narrowing that at the
        // NEW site would silently discard correct reviews over a transport blip — the rule most easily lost
        // when a guard is copied to a second boundary.
        IPrProvider provider = hostUnreachable
            ? new UnreachablePrProvider("github")
            : new MockPrProvider("github", [], Cursor()) { CurrentHeadSha = null };
        var publisher = new FakeReviewCommentPublisher("github");
        var executor = BuildPostingExecutor(store, publisher, [provider], new RecordingProvisioner());
        var run = SeedRunReadyToPost(store, headSha: "head-118");

        await executor.ExecuteStageAsync(ReviewStage.Posted, run, CancellationToken.None);

        publisher.PostCount.Should().Be(1);
    }

    // ── the delivery boundary's lifecycle sibling (#430) ──────────────────────────────────────────

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PrClosesBetweenReviewedAndPosted_PublishesNothing_AndStillCleansUp(bool merged)
    {
        // Parameterised on a bool rather than on PrLifecycle because that enum is internal and an xUnit theory
        // argument has to be as public as the test method.
        var lifecycle = merged ? PrLifecycle.Merged : PrLifecycle.Abandoned;
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        // The head is DELIBERATELY unchanged. That is what makes this the lifecycle clause's own distinguishing
        // case: the head guard agrees here, so if the review is withheld it can only be because the lifecycle
        // clause withheld it. The synthesis-time check cannot cover this — it reads the run's persisted
        // pr_lifecycle_state, stamped once at discovery and never refreshed while the review ran.
        var provider = new MockPrProvider("github", [], Cursor()) { CurrentHeadSha = "head-118", PrState = lifecycle };
        var publisher = new FakeReviewCommentPublisher("github");
        var provisioner = new RecordingProvisioner();
        var executor = BuildPostingExecutor(store, publisher, [provider], provisioner);
        var run = SeedRunReadyToPost(store, headSha: "head-118");

        await executor.ExecuteStageAsync(ReviewStage.Posted, run, CancellationToken.None);

        provider.PrStateCalls.Should().Be(1, "only the host can contradict the lifecycle the run was discovered with");
        publisher
            .PostCount.Should()
            .Be(
                0,
                "findings on a PR that has already merged or closed read as noise, and the conversation may be locked"
            );
        store
            .GetOutboxForRun(run.Id)
            .Should()
            .NotContain(
                o => o.Operation == ReviewPoster.PostReviewCommentOperation,
                "a refusal must not leave a delivery row claiming the review reached the PR"
            );
        // Merged and Closed are terminal, so the skip must be non-throwing: failing the stage would spin the
        // terminal stage forever waiting for a state that is never coming back.
        provisioner.DestroyCalls.Should().Contain(r => r.Id == run.Id, "cleanup runs on the refusal path too");
    }

    [Fact]
    public async Task AnOpenPrAtThePostingBoundary_IsAskedAboutAndStillPosts()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var provider = new MockPrProvider("github", [], Cursor())
        {
            CurrentHeadSha = "head-118",
            PrState = PrLifecycle.Open,
        };
        var publisher = new FakeReviewCommentPublisher("github");
        var executor = BuildPostingExecutor(store, publisher, [provider], new RecordingProvisioner());
        var run = SeedRunReadyToPost(store, headSha: "head-118");

        await executor.ExecuteStageAsync(ReviewStage.Posted, run, CancellationToken.None);

        // The non-vacuity companion to the theory above: the refusals there would also be produced by a guard
        // that refused EVERYTHING, and by a guard that never asked at all. Both are excluded here.
        provider.PrStateCalls.Should().Be(1);
        publisher.PostCount.Should().Be(1, "an open PR at the boundary is exactly the case that must still deliver");
    }

    [Fact]
    public async Task AnIndeterminateLifecycleAnswerAtThePostingBoundary_StillPosts()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        // The head answer is positive and agrees, so the head guard is out of the way and the ONLY thing being
        // decided is what an unanswerable lifecycle question means. It must mean "post": a failed read is not
        // evidence that the PR closed, and refusing on it would discard a finished review on every API blip —
        // the rule most easily narrowed by accident when a guard is copied to a second boundary.
        var provider = new LifecycleUnreachablePrProvider("github", headSha: "head-118");
        var publisher = new FakeReviewCommentPublisher("github");
        var executor = BuildPostingExecutor(store, publisher, [provider], new RecordingProvisioner());
        var run = SeedRunReadyToPost(store, headSha: "head-118");

        await executor.ExecuteStageAsync(ReviewStage.Posted, run, CancellationToken.None);

        provider.PrStateCalls.Should().Be(1, "the host was asked; the answer just was not one");
        publisher.PostCount.Should().Be(1);
    }

    [Fact]
    public async Task AMovedHeadSpendsNoLifecycleRead()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var provider = new MockPrProvider("github", [], Cursor())
        {
            CurrentHeadSha = "head-after-force-push",
            PrState = PrLifecycle.Open,
        };
        var executor = BuildPostingExecutor(
            store,
            new FakeReviewCommentPublisher("github"),
            [provider],
            new RecordingProvisioner()
        );
        var run = SeedRunReadyToPost(store, headSha: "head-before-force-push");

        await executor.ExecuteStageAsync(ReviewStage.Posted, run, CancellationToken.None);

        // The two guards are separate provider reads, not one. A run already refusing to publish must not spend
        // a second request to reach an answer it cannot act on differently.
        provider.PrStateCalls.Should().Be(0, "the head guard had already withheld the review");
    }

    /// <summary>
    /// A host that answers the head question and fails the lifecycle one, so an indeterminate lifecycle can be
    /// tested without an indeterminate head hiding the result.
    /// </summary>
    private sealed class LifecycleUnreachablePrProvider(string provider, string headSha) : IPrProvider
    {
        public string Provider { get; } = provider;

        public int PrStateCalls { get; private set; }

        public Task<PullRequestPage> ListOpenPullRequestsAsync(
            PrPollRequest request,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException("not part of this test");

        public Task<PrLifecycle> GetPrStateAsync(RepoIdentity repo, string prId, CancellationToken cancellationToken)
        {
            PrStateCalls++;
            throw new HttpRequestException("simulated provider outage");
        }

        public Task<string?> GetCurrentHeadShaAsync(
            RepoIdentity repo,
            string prId,
            CancellationToken cancellationToken
        ) => Task.FromResult<string?>(headSha);
    }

    private static DaemonReviewStageExecutor BuildPostingExecutor(
        ReviewStore store,
        FakeReviewCommentPublisher publisher,
        IReadOnlyList<IPrProvider> prProviders,
        IReviewSessionProvisioner provisioner
    ) =>
        new(
            store,
            new FakeReviewAgentLoopFactory(),
            new FakeSandboxCommandRunner(),
            new FakeSandboxFileSystem(),
            // EnableHostSummaryFallback is what makes the terminal stage post at all; without it the
            // publisher is never called and "posted nothing" would be true for the wrong reason.
            new CodeReviewDaemonOptions
            {
                EnableCommentPosting = true,
                EnableHostSummaryFallback = true,
                EnableToolAssistedReview = true,
            },
            [publisher],
            NullLoggerFactory.Instance,
            provisioner,
            prProviders: prProviders
        );

    /// <summary>Seeds a run plus the <c>review</c> artifact the Posted stage reads, so that stage can be driven
    /// directly. <c>mode: "post"</c> is what authorizes a live delivery — a collect-only run posts nothing
    /// whatever the head says, and would prove nothing here.</summary>
    private static ReviewRun SeedRunReadyToPost(ReviewStore store, string headSha)
    {
        var repoId = store.EnsureRepo(
            new RepoIdentity
            {
                Provider = "github",
                OrgOrOwner = "achieveai",
                RepoName = "LmDotnetTools",
                RepoStableId = "repo-stable-1",
            }
        );
        var run = store.CreateOrGetReviewRun(
            new ReviewRun
            {
                RepoId = repoId,
                PrId = "118",
                HeadSha = headSha,
                BaseSha = "base-118",
                TriggerWatermark = "wm-118",
                ReviewKind = "full",
                VariantId = "primary",
                Mode = "post",
                Stage = ReviewStage.Judged,
                WorkflowStatus = WorkflowStatus.Running,
                PrLifecycleState = PrLifecycleState.Open,
            }
        );

        _ = store.AddArtifact(
            new ReviewArtifact
            {
                ReviewRunId = run.Id,
                ArtifactSchemaVersion = DaemonReviewStageExecutor.ReviewArtifactSchemaVersion,
                ArtifactKind = DaemonReviewStageExecutor.ReviewArtifactKind,
                Provider = "github",
                Payload = JsonSerializer.Serialize(new ReviewArtifactPayload("Found one thing.", "run-1", "primary")),
            }
        );

        return run;
    }

    /// <summary>Records terminal-cleanup calls, so a refusal can be shown not to leak the session.</summary>
    private sealed class RecordingProvisioner : IReviewSessionProvisioner
    {
        public List<ReviewRun> DestroyCalls { get; } = [];

        public Task<ReviewRunSession?> GetOrCreateAsync(ReviewRun run, CancellationToken ct) =>
            Task.FromResult<ReviewRunSession?>(
                new ReviewRunSession(
                    $"session-{run.Id}",
                    $"/workspace/review-run-{run.Id}",
                    new FakeSandboxCommandRunner(),
                    new FakeSandboxFileSystem()
                )
            );

        public Task<ReviewRunSession?> GetOrCreateForSlotAsync(ReviewRun run, ReviewSlot slot, CancellationToken ct) =>
            GetOrCreateAsync(run, ct);

        public Task DestroyAsync(ReviewRun run, CancellationToken ct)
        {
            DestroyCalls.Add(run);
            return Task.CompletedTask;
        }

        public Task DestroyAsync(long runId, CancellationToken ct) => Task.CompletedTask;
    }

    private static OpaqueCursor Cursor() =>
        new()
        {
            Provider = "github",
            Scope = "achieveai/LmDotnetTools:open-prs",
            CursorVersion = PrPollingService.CursorVersion,
            CursorPayload = "{}",
        };

    private static DaemonReviewStageExecutor BuildExecutor(
        ReviewStore store,
        FakeReviewAgentLoopFactory factory,
        IReadOnlyList<IPrProvider> prProviders,
        ILoggerFactory? loggerFactory = null
    ) =>
        new(
            store,
            factory,
            new FakeSandboxCommandRunner(),
            new FakeSandboxFileSystem(),
            new CodeReviewDaemonOptions(),
            [new FakeReviewCommentPublisher("github")],
            loggerFactory ?? NullLoggerFactory.Instance,
            prProviders: prProviders
        );

    /// <summary>Seeds a run plus the <c>review-context</c> artifact the Reviewed stage reads, so the stage
    /// can be driven directly without first running ContextReady.</summary>
    private static ReviewRun SeedRunWithContext(ReviewStore store, string prId, string headSha)
    {
        var repoId = store.EnsureRepo(
            new RepoIdentity
            {
                Provider = "github",
                OrgOrOwner = "achieveai",
                RepoName = "LmDotnetTools",
                RepoStableId = "repo-stable-1",
            }
        );
        var run = store.CreateOrGetReviewRun(
            new ReviewRun
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
            }
        );

        _ = store.AddArtifact(
            new ReviewArtifact
            {
                ReviewRunId = run.Id,
                ArtifactSchemaVersion = DaemonReviewStageExecutor.ContextArtifactSchemaVersion,
                ArtifactKind = DaemonReviewStageExecutor.ContextArtifactKind,
                Provider = "github",
                Payload = JsonSerializer.Serialize(
                    new ContextArtifactPayload(
                        run.PrId,
                        run.BaseSha,
                        run.HeadSha,
                        "diff --git a/Foo.cs b/Foo.cs\n+ var x = bar;"
                    )
                ),
            }
        );

        return run;
    }

    /// <summary>A host that cannot be reached — NOT the same answer as a host reporting a different head.</summary>
    private sealed class UnreachablePrProvider(string provider) : IPrProvider
    {
        public string Provider { get; } = provider;

        public Task<PullRequestPage> ListOpenPullRequestsAsync(
            PrPollRequest request,
            CancellationToken cancellationToken
        ) => throw new HttpRequestException("simulated provider outage");

        public Task<PrLifecycle> GetPrStateAsync(RepoIdentity repo, string prId, CancellationToken cancellationToken) =>
            throw new HttpRequestException("simulated provider outage");

        public Task<string?> GetCurrentHeadShaAsync(
            RepoIdentity repo,
            string prId,
            CancellationToken cancellationToken
        ) => throw new HttpRequestException("simulated provider outage");
    }

    /// <summary>
    /// A host that is merely SLOW. Both real providers reach it through <c>HttpClient</c>, which raises a
    /// <see cref="TaskCanceledException"/> — a subclass of <see cref="OperationCanceledException"/> — on its
    /// own <c>Timeout</c> while the caller's token is still uncancelled. Nobody asked for a cancel, so this
    /// is an outage wearing a cancellation's clothes.
    /// </summary>
    private sealed class TimingOutPrProvider(string provider) : IPrProvider
    {
        public string Provider { get; } = provider;

        public int HeadShaCalls { get; private set; }

        public Task<PullRequestPage> ListOpenPullRequestsAsync(
            PrPollRequest request,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException("not part of this test");

        public Task<PrLifecycle> GetPrStateAsync(RepoIdentity repo, string prId, CancellationToken cancellationToken) =>
            Task.FromResult(PrLifecycle.Open);

        public Task<string?> GetCurrentHeadShaAsync(RepoIdentity repo, string prId, CancellationToken cancellationToken)
        {
            HeadShaCalls++;
            throw new TaskCanceledException(
                "The request was canceled due to the configured HttpClient.Timeout elapsing.",
                new TimeoutException()
            );
        }
    }

    /// <summary>
    /// A host read that is interrupted by the CALLER — daemon shutdown, not a slow API. The cancel is raised
    /// from inside the read rather than before the stage, because
    /// <c>ValidateReviewStillCurrentAsync</c> opens with <c>ThrowIfCancellationRequested</c>: a pre-cancelled
    /// token never reaches the catch these tests exist to pin.
    /// </summary>
    private sealed class CancellingPrProvider(string provider, CancellationTokenSource cts) : IPrProvider
    {
        public string Provider { get; } = provider;

        public int HeadShaCalls { get; private set; }

        public Task<PullRequestPage> ListOpenPullRequestsAsync(
            PrPollRequest request,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException("not part of this test");

        public Task<PrLifecycle> GetPrStateAsync(RepoIdentity repo, string prId, CancellationToken cancellationToken) =>
            Task.FromResult(PrLifecycle.Open);

        public Task<string?> GetCurrentHeadShaAsync(RepoIdentity repo, string prId, CancellationToken cancellationToken)
        {
            HeadShaCalls++;
            cts.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<string?>(null);
        }
    }
}
