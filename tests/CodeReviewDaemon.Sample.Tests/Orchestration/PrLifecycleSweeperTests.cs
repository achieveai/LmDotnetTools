using System.Text.Json.Nodes;
using AchieveAi.LmDotnetTools.LmWorkflow.Runtime;
using CodeReviewDaemon.Sample.Orchestration;
using CodeReviewDaemon.Sample.Persistence;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using CodeReviewDaemon.Sample.Workspace.Git;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeReviewDaemon.Sample.Tests.Orchestration;

public sealed class PrLifecycleSweeperTests
{
    private static readonly RepoIdentity TargetRepo = new()
    {
        Provider = "github",
        OrgOrOwner = "acme",
        RepoName = "widgets",
        RepoStableId = "repo-1",
    };

    [Fact]
    public async Task Merged_pr_runs_the_authored_merged_route_without_fixed_git_merge()
    {
        using var fixture = new Fixture();
        var pr = Pr("42", "review/widgets-42");
        var repoId = fixture.Store.EnsureRepo(TargetRepo);
        _ = fixture.Store.CreateOrGetReviewRun(NewRun(repoId, pr.PrId));
        var sweeper = fixture.CreateSweeper([pr], (_, _) => Task.FromResult(PrLifecycle.Merged));

        await sweeper.SweepAsync(default);

        fixture.WorkflowRunner.Rounds.Should().ContainSingle();
        fixture.WorkflowRunner.Rounds[0].Kind.Should().Be(WorkflowRoundKind.Merged);
        fixture.WorkflowRunner.Contexts[0]["Merge"]!["ArtifactBranch"]!.GetValue<string>().Should().Be(pr.Branch);
        fixture.Commands.Commands.Should().BeEmpty("the authored merged workflow owns retention and branch closure");
    }

    [Fact]
    public async Task Abandoned_pr_deletes_its_branch_under_the_existing_lifecycle_path()
    {
        using var fixture = new Fixture();
        var pr = Pr("43", "review/widgets-43");
        var sweeper = fixture.CreateSweeper([pr], (_, _) => Task.FromResult(PrLifecycle.Abandoned));

        await sweeper.SweepAsync(default);

        var commands = fixture.Commands.Commands.Select(value => string.Join(' ', value.Argv)).ToList();
        commands.Should().Contain(value => value.Contains($"branch -D {pr.Branch}", StringComparison.Ordinal));
        commands
            .Should()
            .Contain(value => value.Contains($"push origin --delete {pr.Branch}", StringComparison.Ordinal));
        fixture.WorkflowRunner.Rounds.Should().BeEmpty();
    }

    [Fact]
    public async Task Open_pr_has_no_work_and_terminal_success_is_not_repeated()
    {
        using var fixture = new Fixture();
        var open = Pr("44", "review/widgets-44");
        var abandoned = Pr("45", "review/widgets-45");
        var sweeper = fixture.CreateSweeper(
            [open, abandoned],
            (pr, _) => Task.FromResult(pr == open ? PrLifecycle.Open : PrLifecycle.Abandoned)
        );

        await sweeper.SweepAsync(default);
        await sweeper.SweepAsync(default);

        fixture
            .Commands.Commands.Count(value =>
                string.Join(' ', value.Argv)
                    .Contains($"push origin --delete {abandoned.Branch}", StringComparison.Ordinal)
            )
            .Should()
            .Be(1);
        fixture
            .Commands.Commands.Should()
            .NotContain(value => string.Join(' ', value.Argv).Contains(open.Branch, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Merged_orphan_gets_a_durable_merged_only_run_from_the_live_provider_head()
    {
        using var database = new TempSqliteDatabase();
        using var store = new ReviewStore(database.ConnectionString);
        var repoId = store.EnsureRepo(TargetRepo);
        var pr = Pr("50", "review/widgets-50");

        var run = await PrLifecycleSweeper.CreateMergedOrphanRunAsync(
            store,
            repoId,
            pr,
            (_, _) => Task.FromResult<string?>("merged-head"),
            default
        );

        run.HeadSha.Should().Be("merged-head");
        run.BaseSha.Should().Be("merged-head");
        run.TriggerWatermark.Should().Be("merged:merged-head");
        run.ReviewKind.Should().Be("merged");
        run.PrLifecycleState.Should().Be(PrLifecycleState.Merged);
        run.WorkflowStatus.Should().Be(WorkflowStatus.Pending);
    }

    private static ReviewedPr Pr(string prId, string branch) => new(TargetRepo, "github", prId, branch);

    private static ReviewRun NewRun(long repoId, string prId) =>
        new()
        {
            RepoId = repoId,
            PrId = prId,
            HeadSha = "head",
            BaseSha = "base",
            TriggerWatermark = "1",
            ReviewKind = "full",
            VariantId = "primary",
            Mode = "collect-only",
            Stage = ReviewStage.Posted,
            WorkflowStatus = WorkflowStatus.Completed,
            PrLifecycleState = PrLifecycleState.Open,
        };

    private sealed class Fixture : IDisposable
    {
        private readonly TempSqliteDatabase _database = new();
        private readonly string _repoRoot = Path.Combine(
            Path.GetTempPath(),
            "lifecycle-sweeper",
            Guid.NewGuid().ToString("N")
        );

        public Fixture()
        {
            Directory.CreateDirectory(_repoRoot);
            Store = new ReviewStore(_database.ConnectionString);
            Commands = new FakeSandboxCommandRunner();
            BranchManager = new ReviewBranchManager(
                new GitRunner(Commands),
                new FakeSandboxFileSystem(),
                NullLogger<ReviewBranchManager>.Instance
            );
            WorkflowRunner = new RecordingWorkflowRunner();
            Orchestrator = new PrOrchestrator(Store, WorkflowRunner, NullLogger<PrOrchestrator>.Instance);
        }

        public ReviewStore Store { get; }
        public FakeSandboxCommandRunner Commands { get; }
        public ReviewBranchManager BranchManager { get; }
        public RecordingWorkflowRunner WorkflowRunner { get; }
        public PrOrchestrator Orchestrator { get; }

        public PrLifecycleSweeper CreateSweeper(
            IReadOnlyList<ReviewedPr> prs,
            Func<ReviewedPr, CancellationToken, Task<PrLifecycle>> lifecycle
        ) =>
            new(
                _ => Task.FromResult(prs),
                lifecycle,
                BranchManager,
                _repoRoot,
                NullLogger<PrLifecycleSweeper>.Instance,
                Store,
                Orchestrator,
                (_, _) => Task.FromResult<string?>("provider-head")
            );

        public void Dispose()
        {
            Store.Dispose();
            _database.Dispose();
            try
            {
                Directory.Delete(_repoRoot, recursive: true);
            }
            catch (IOException) { }
        }
    }

    private sealed class RecordingWorkflowRunner : IReviewWorkflowRunner
    {
        public List<WorkflowRound> Rounds { get; } = [];
        public List<JsonObject> Contexts { get; } = [];

        public Task<WorkflowInvocationStatus> RunAsync(
            ReviewRun run,
            WorkflowRound? round,
            JsonObject frozenContext,
            CancellationToken cancellationToken
        )
        {
            Rounds.Add(round ?? throw new InvalidOperationException("Expected a merged round."));
            Contexts.Add((JsonObject)frozenContext.DeepClone());
            return Task.FromResult(WorkflowInvocationStatus.Completed);
        }

        public Task<WorkflowInvocationStatus> ResumeAsync(
            ReviewRun run,
            WorkflowRound? round,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

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
        ) => throw new NotSupportedException();

        public Task RecoverActiveWorkspacesAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public bool HasFrozenContext(ReviewRun run, WorkflowRound? round) => false;
    }
}
