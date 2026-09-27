using System.Text.Json;
using System.Text.Json.Nodes;
using AchieveAi.LmDotnetTools.LmWorkflow.Runtime;
using CodeReviewDaemon.Sample.Configuration;
using CodeReviewDaemon.Sample.Hosting;
using CodeReviewDaemon.Sample.Orchestration;
using CodeReviewDaemon.Sample.Persistence;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using CodeReviewDaemon.Sample.Workspace;
using CodeReviewDaemon.Sample.Workspace.Git;
using CodeReviewDaemon.Sample.Workspace.Sandbox;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeReviewDaemon.Sample.Tests.Orchestration;

/// <summary>
/// Tasks #81 + #82 composed — the single end-to-end fact neither change can prove on its own: the ONLY
/// thing that ever opens an artifact-branch push is the exact <c>--run-pr</c> command, and the grant it
/// opens with is derived from the identity that command re-read and validated for the second time.
/// <para>
/// The composition mirrors <c>Program.cs</c>'s <c>--run-pr</c> arm exactly: a <c>Denied</c> capability
/// variable, <see cref="RunPrOrchestration.ExecuteAsync"/> driving prepare → lease → host-start →
/// admit+run → host-stop, and the capability assigned from nothing but
/// <see cref="RunSinglePrCommand.AdmitAndRunAsync"/>'s <c>onIdentityRevalidated</c> seam. The captured
/// value is then handed to a real <see cref="WorkflowArtifactOperations"/> for the run that was actually
/// admitted, so "the grant reaches retention" is observed rather than assumed.
/// </para>
/// </summary>
public sealed class RunPrArtifactBranchCompositionTests
{
    private const string RepoKey = "acme/widgets";
    private const string Head = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Base = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    /// <summary>
    /// The whole point: an exact, operator-approved <c>--run-pr</c> mints a grant for precisely the
    /// repo/PR/head it re-validated, and that grant is what lets THIS run's retention push. The push is
    /// observed through a real <see cref="WorkflowArtifactOperations"/> over a fake git runner, so the
    /// assertion is "retention actually committed and pushed the derived branch", not "a boolean was true".
    /// </summary>
    [Fact]
    public async Task An_exact_run_pr_mints_the_grant_that_lets_this_runs_retention_push()
    {
        using var fixture = new Fixture();
        var capability = ReviewArtifactBranchCapability.Denied;

        var result = await fixture.ExecuteRunPrAsync(
            head: Head,
            baseSha: Base,
            onIdentityRevalidated: (target, descriptor) =>
                capability = ReviewArtifactBranchCapability.Grant(
                    target.Repo.DisplayName,
                    descriptor.PrId,
                    descriptor.HeadSha
                )
        );

        result.Admitted.Should().BeTrue();
        capability.IsGranted.Should().BeTrue();
        capability.PrId.Should().Be("7");
        capability.HeadSha.Should().Be(Head, "the grant must name the head the command itself re-read");

        var run = fixture.AdmittedRun();
        var retained = await fixture
            .CreateOperations(run, capability)
            .RetainArtifactsAsync(Fixture.Files(run), default);

        retained["RetainedSha"]!.GetValue<string>().Should().Be(Fixture.PushedSha);
        var receipt = JsonSerializer.Deserialize<ReviewArtifactBranchReceipt>(
            fixture.Store.TryGetLatestArtifact(run.Id, ReviewArtifactKinds.ArtifactBranchKind)!.Payload
        )!;
        receipt.Branch.Should().Be(capability.AuthorizedBranch(fixture.Repo));
        receipt.PrId.Should().Be("7");
        fixture
            .Store.GetOutboxForRun(run.Id)
            .Should()
            .ContainSingle(entry => entry.Operation == WorkflowArtifactOperations.RetentionOperation)
            .Which.Status.Should()
            .Be(OutboxStatus.Posted);
    }

    /// <summary>
    /// The other half of the same fact. A normally-admitted run — the daemon's polling path, which never
    /// goes anywhere near <c>--run-pr</c> and therefore never reaches the mint seam — carries the startup
    /// default, and retention refuses it before it takes the git gate or writes a receipt. The refusal is
    /// structural: there is no flag, config value, or option in this composition that could have changed it.
    /// </summary>
    [Fact]
    public async Task A_normally_polled_run_never_mints_anything_and_its_retention_never_reaches_git()
    {
        using var fixture = new Fixture();
        var capability = ReviewArtifactBranchCapability.Denied;
        var run = fixture.AdmitAsThePollerWould();

        await fixture
            .CreateOperations(run, capability)
            .Invoking(x => x.RetainArtifactsAsync(Fixture.Files(run), default))
            .Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("*capability*");

        capability.IsGranted.Should().BeFalse();
        fixture.Runner.Commands.Should().BeEmpty("a denied retention must leave no git trace at all");
        fixture.Store.GetOutboxForRun(run.Id).Should().BeEmpty("nor a receipt");
    }

    /// <summary>
    /// A <c>--run-pr</c> whose head drifted between the operator's approval and the command's own re-read is
    /// rejected, and the mint seam is never reached — so a refused run cannot leave a live grant behind for
    /// anything later in the process to spend.
    /// </summary>
    [Fact]
    public async Task A_run_pr_rejected_on_identity_drift_mints_nothing()
    {
        using var fixture = new Fixture();
        var capability = ReviewArtifactBranchCapability.Denied;

        var result = await fixture.ExecuteRunPrAsync(
            head: "cccccccccccccccccccccccccccccccccccccccc",
            baseSha: Base,
            onIdentityRevalidated: (_, _) => capability = ReviewArtifactBranchCapability.Grant(RepoKey, "7", Head)
        );

        result.Admitted.Should().BeFalse();
        result.RejectionReason.Should().Be(RunSinglePrRejectionReason.IdentityDrift);
        capability.Should().BeSameAs(ReviewArtifactBranchCapability.Denied);
    }

    /// <summary>
    /// Holding the grant opens the artifact branch and nothing else. Source-PR comment posting stays
    /// governed by <c>EnableCommentPosting</c> — off by default, and not an input this capability
    /// participates in — so the pilot run that pushes a branch still cannot write to the pull request.
    /// </summary>
    [Fact]
    public async Task Holding_the_grant_still_leaves_source_pr_comment_posting_denied()
    {
        using var fixture = new Fixture();
        var capability = ReviewArtifactBranchCapability.Denied;

        _ = await fixture.ExecuteRunPrAsync(
            head: Head,
            baseSha: Base,
            onIdentityRevalidated: (target, descriptor) =>
                capability = ReviewArtifactBranchCapability.Grant(
                    target.Repo.DisplayName,
                    descriptor.PrId,
                    descriptor.HeadSha
                )
        );

        capability.IsGranted.Should().BeTrue();
        capability.AuthorizesSourcePrWrite.Should().BeFalse();

        new CodeReviewDaemonOptions()
            .EnableCommentPosting.Should()
            .BeFalse("the pilot command must not flip the daemon's collect-only default");
        var policy = DaemonOperationPolicy.BuildForRun(
            fixture.Repo,
            "https://github.com/acme/reviewbot",
            allowWriteOperations: new CodeReviewDaemonOptions().EnableCommentPosting
        );
        var request = new OperationRequest(
            SandboxOperation.PostReviewComment,
            "github",
            "api.github.com",
            "POST",
            "/repos/acme/widgets/issues/7/comments"
        );
        policy.Decide(request).IsAllowed.Should().BeFalse();
        policy.ShouldInjectCredential(request).Should().BeFalse();
    }

    private sealed class Fixture : IDisposable
    {
        public const string PushedSha = "dddddddddddddddddddddddddddddddddddddddd";

        private readonly TempSqliteDatabase _db = new();
        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            "runpr-artifact-" + Guid.NewGuid().ToString("N")
        );
        private readonly SemaphoreSlim _gitGate = new(1, 1);

        public RepoIdentity Repo { get; } =
            new()
            {
                Provider = "github",
                OrgOrOwner = "acme",
                RepoName = "widgets",
            };

        public ReviewStore Store { get; }

        public FakeSandboxCommandRunner Runner { get; } = new();

        public FakeSandboxFileSystem FileSystem { get; } = new();

        /// <summary>
        /// The fixed metadata summary a collect-only retention is allowed to publish, bound to the run that
        /// is actually being retained — the schema gate runs before the capability gate, so a placeholder
        /// here would prove the wrong refusal.
        /// </summary>
        public static IReadOnlyList<ReviewArtifactFile> Files(ReviewRun run) =>
            [
                new(
                    "PRs/widgets-7/summary.json",
                    new JsonObject
                    {
                        ["SchemaVersion"] = 1,
                        ["ReviewRunId"] = run.Id,
                        ["Route"] = "new_head",
                        ["KnowledgeEntryCount"] = 0,
                    }.ToJsonString()
                ),
            ];

        public Fixture()
        {
            Store = new ReviewStore(_db.ConnectionString);
            Runner.OnArgvContains("rev-parse", new SandboxCommandResult(0, PushedSha, ""));
            Runner.OnArgvContains("ls-remote", new SandboxCommandResult(2, "", ""));
        }

        private string RepoRoot => Path.Combine(_root, "store");

        /// <summary>
        /// Program.cs's <c>--run-pr</c> arm, delegate for delegate: the same ordering guarantees
        /// (<see cref="RunPrOrchestration.ExecuteAsync"/>) with the live host, the real coordinator lease
        /// file, and the provider replaced by doubles.
        /// </summary>
        public Task<RunSinglePrResult> ExecuteRunPrAsync(
            string head,
            string baseSha,
            Action<PrPollTarget, PullRequestDescriptor> onIdentityRevalidated
        )
        {
            var provider = new MockPrProvider(
                "github",
                [
                    new PullRequestDescriptor
                    {
                        PrId = "7",
                        HeadSha = Head,
                        BaseSha = Base,
                        TriggerWatermark = "watermark-7",
                        LifecycleState = PrLifecycleState.Open,
                        Author = "alice",
                        Title = "Pilot",
                    },
                ],
                new OpaqueCursor
                {
                    Provider = "github",
                    Scope = RepoKey + ":open-prs",
                    CursorVersion = PrPollingService.CursorVersion,
                    CursorPayload = "{}",
                }
            );
            var options = new CodeReviewDaemonOptions { EnabledRepos = [RepoKey] };
            var orchestrator = new PrOrchestrator(Store, new CompletedRunner(), NullLogger<PrOrchestrator>.Instance);

            return RunPrOrchestration.ExecuteAsync(
                prepareAsync: ct =>
                    RunSinglePrCommand.PrepareAsync(
                        options,
                        [provider],
                        NullLogger.Instance,
                        RepoKey,
                        "7",
                        head,
                        baseSha,
                        ct
                    ),
                acquireLease: () =>
                    RunPrCoordinatorLeaseOutcome.Acquired(
                        new FileStream(Path.GetTempFileName(), FileMode.Open, FileAccess.ReadWrite, FileShare.None)
                    ),
                startHostAsync: () => Task.CompletedTask,
                admitAndRunAsync: (prepared, ct) =>
                    RunSinglePrCommand.AdmitAndRunAsync(
                        prepared,
                        Store,
                        orchestrator,
                        ct,
                        commentReaders: [provider],
                        onIdentityRevalidated: onIdentityRevalidated
                    ),
                stopHostAsync: () => Task.CompletedTask,
                CancellationToken.None
            );
        }

        public ReviewRun AdmittedRun() =>
            Store.GetLatestReviewRun(Store.EnsureRepo(Repo), "7")
            ?? throw new InvalidOperationException("The command admitted no run.");

        /// <summary>A run admitted the way the polling loop admits one — no command, so no mint seam.</summary>
        public ReviewRun AdmitAsThePollerWould() =>
            Store.CreateOrGetReviewRun(
                new ReviewRun
                {
                    RepoId = Store.EnsureRepo(Repo),
                    PrId = "7",
                    HeadSha = Head,
                    BaseSha = Base,
                    TriggerWatermark = "polled",
                    ReviewKind = "code-review",
                    VariantId = "primary",
                    Mode = "auto",
                    Stage = ReviewStage.Discovered,
                    WorkflowStatus = WorkflowStatus.Pending,
                    PrLifecycleState = PrLifecycleState.Open,
                }
            );

        public WorkflowArtifactOperations CreateOperations(ReviewRun run, ReviewArtifactBranchCapability capability) =>
            new(
                Store,
                run,
                Repo,
                RepoRoot,
                "main",
                new ReviewBranchManager(new GitRunner(Runner), FileSystem, NullLogger<ReviewBranchManager>.Instance),
                _gitGate,
                capability,
                new WorkflowKnowledgeEdits(RepoRoot, Repo, FileSystem, NullLogger.Instance)
            );

        public void Dispose()
        {
            Store.Dispose();
            _db.Dispose();
            _gitGate.Dispose();
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
    }

    /// <summary>A workflow runner that reports Completed without doing any work — the same idiom
    /// <c>PrOrchestratorWorkflowTests</c> uses, so the orchestrator path is real and only the workflow is
    /// a double.</summary>
    private sealed class CompletedRunner : IReviewWorkflowRunner
    {
        public Task<WorkflowInvocationStatus> RunAsync(
            ReviewRun run,
            WorkflowRound? round,
            JsonObject frozenContext,
            CancellationToken cancellationToken
        ) => Task.FromResult(WorkflowInvocationStatus.Completed);

        public Task<WorkflowInvocationStatus> ResumeAsync(
            ReviewRun run,
            WorkflowRound? round,
            CancellationToken cancellationToken
        ) => Task.FromResult(WorkflowInvocationStatus.Completed);

        public Task<WorkflowInvocationStatus> RunOrResumeAsync(
            ReviewRun run,
            WorkflowRound? round,
            JsonObject currentContext,
            CancellationToken cancellationToken
        ) => Task.FromResult(WorkflowInvocationStatus.Completed);

        public Task<JsonObject> ReadFrozenContextAsync(
            ReviewRun run,
            WorkflowRound? round,
            CancellationToken cancellationToken
        ) => Task.FromResult(new JsonObject());

        public Task RecoverActiveWorkspacesAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public bool HasFrozenContext(ReviewRun run, WorkflowRound? round) => true;
    }
}
