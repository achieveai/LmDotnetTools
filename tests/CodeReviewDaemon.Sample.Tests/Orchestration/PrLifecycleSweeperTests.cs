using AchieveAi.LmDotnetTools.LmTestUtils.Logging;
using CodeReviewDaemon.Sample.Configuration;
using CodeReviewDaemon.Sample.Orchestration;
using CodeReviewDaemon.Sample.Persistence;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using CodeReviewDaemon.Sample.Workspace.Git;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Xunit.Abstractions;

namespace CodeReviewDaemon.Sample.Tests.Orchestration;

public sealed class PrLifecycleSweeperTests : LoggingTestBase
{
    private const string RepoRoot = "/host/reviewbot";

    private static readonly RepoIdentity TargetRepo = new()
    {
        Provider = "github",
        OrgOrOwner = "acme",
        RepoName = "widgets",
    };

    public PrLifecycleSweeperTests(ITestOutputHelper output)
        : base(output) { }

    private ReviewBranchManager CreateBranchManager(FakeSandboxCommandRunner runner) =>
        new(new GitRunner(runner), new FakeSandboxFileSystem(), LoggerFactory.CreateLogger<ReviewBranchManager>());

    private PrLifecycleSweeper CreateSweeper(
        IReadOnlyList<ReviewedPr> reviewedPrs,
        Func<ReviewedPr, CancellationToken, Task<PrLifecycle>> getPrLifecycleAsync,
        ReviewBranchManager branchManager,
        Func<ReviewedPr, PrLifecycle, CancellationToken, Task<EngagementDecision>>? observeTerminalAsync = null,
        Func<EngagementDecision, CancellationToken, Task>? runRoundAsync = null,
        Func<ReviewedPr, PrLifecycle, CancellationToken, Task<EngagementDecision>>? reconcileTerminalAsync = null
    ) =>
        new(
            _ => Task.FromResult(reviewedPrs),
            getPrLifecycleAsync,
            branchManager,
            RepoRoot,
            LoggerFactory.CreateLogger<PrLifecycleSweeper>(),
            observeTerminalAsync,
            runRoundAsync,
            reconcileTerminalAsync
        );

    private static ReviewedPr Pr(string prId, string branch) => new(TargetRepo, "github", prId, branch);

    /// <summary>
    /// A stand-in substep success. The executor refuses to checkpoint a completion with no artifact, so
    /// even a test double has to hand back bytes that identify the substep it stands in for.
    /// </summary>
    private static MergedCloseSubstepResult CompletedWithArtifact(MergedCloseSubstep substep) =>
        MergedCloseSubstepResult.Completed(
            new MergedCloseSubstepReceipt(System.Text.Encoding.UTF8.GetBytes($"artifact:{substep}"))
        );

    [Fact]
    public async Task Sweep_reports_merge_to_the_engagement_coordinator_without_merging_the_notes_branch()
    {
        var runner = new FakeSandboxCommandRunner();
        var pr = Pr("42", "review/widgets-42");
        var observations = new List<(string PrId, PrLifecycle Lifecycle)>();
        var sweeper = CreateSweeper(
            [pr],
            (_, _) => Task.FromResult(PrLifecycle.Merged),
            CreateBranchManager(runner),
            (observed, lifecycle, _) =>
            {
                observations.Add((observed.PrId, lifecycle));
                return Task.FromResult(
                    new EngagementDecision(
                        EngagementDecisionKind.AdmitMergedClose,
                        RoundId: 7,
                        "merge_bypasses_cooldown"
                    )
                );
            }
        );

        await sweeper.SweepAsync(CancellationToken.None);

        observations.Should().Equal(("42", PrLifecycle.Merged));
        runner.Commands.Should().BeEmpty("Archived is the only merged-close substep allowed to merge notes");
    }

    [Fact]
    public async Task Sweep_executes_an_admitted_merged_close_and_persists_its_first_substep()
    {
        using var database = new TempSqliteDatabase();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 2, 10, 0, 0, TimeSpan.Zero));
        using var store = new ReviewStore(database.ConnectionString, time);
        var repoId = store.EnsureRepo(TargetRepo);
        var snapshot = ProviderEngagementSnapshot.Create(
            PrLifecycleState.Merged,
            "head-42",
            "base-42",
            new ProviderActivityWatermark("github", time.GetUtcNow(), "merge:42"),
            []
        );
        var descriptor = new PullRequestDescriptor
        {
            PrId = "42",
            HeadSha = snapshot.HeadSha,
            BaseSha = snapshot.BaseSha,
            TriggerWatermark = snapshot.LatestObserved.StableObjectId,
            LifecycleState = snapshot.Lifecycle,
        };
        var coordinator = new PrEngagementCoordinator(store, time);
        var executor = new MergedCloseRoundExecutor(
            store,
            (_, substep, _) =>
                Task.FromResult(
                    substep == MergedCloseSubstep.CandidatesBuilt
                        ? MergedCloseSubstepResult.Retry
                        : CompletedWithArtifact(substep)
                ),
            time
        );
        var runner = new EngagementRoundRunner(
            store,
            [executor],
            new CodeReviewDaemonOptions { MaxDurableRetryAttempts = 3 },
            time,
            LoggerFactory.CreateLogger<EngagementRoundRunner>()
        );
        var pr = Pr("42", "review/widgets-42");
        var sweeper = CreateSweeper(
            [pr],
            (_, _) => Task.FromResult(PrLifecycle.Merged),
            CreateBranchManager(new FakeSandboxCommandRunner()),
            (_, _, ct) => coordinator.ObserveAsync(repoId, TargetRepo, descriptor, snapshot, ct),
            (decision, ct) => runner.RunAsync(decision, null, ct)
        );

        await sweeper.SweepAsync(CancellationToken.None);

        var round = store.ListEngagementRounds(store.CreateOrGetEngagement(EngagementSeed(repoId, time)).Id).Single();
        round.Status.Should().Be(EngagementRoundStatus.RetryPending);
        round.GovernedFailureCount.Should().Be(1);
        store
            .ListAuditRecordsForRound(round.Id)
            .Where(record => record.RecordType == MergedCloseRoundExecutor.SubstepRecordType)
            .Select(record => record.GenerationId)
            .Should()
            .Equal(MergedCloseSubstep.EvidenceFrozen.ToString());
    }

    [Fact]
    public async Task Sweep_reconciles_a_running_merged_close_after_restart_then_executes_the_same_round()
    {
        using var database = new TempSqliteDatabase();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 2, 10, 0, 0, TimeSpan.Zero));
        using var store = new ReviewStore(database.ConnectionString, time);
        var repoId = store.EnsureRepo(TargetRepo);
        var snapshot = ProviderEngagementSnapshot.Create(
            PrLifecycleState.Merged,
            "head-49",
            "base-49",
            new ProviderActivityWatermark("github", time.GetUtcNow(), "merge:49"),
            []
        );
        var descriptor = new PullRequestDescriptor
        {
            PrId = "49",
            HeadSha = snapshot.HeadSha,
            BaseSha = snapshot.BaseSha,
            TriggerWatermark = snapshot.LatestObserved.StableObjectId,
            LifecycleState = snapshot.Lifecycle,
        };
        var coordinator = new PrEngagementCoordinator(store, time);
        var admitted = await coordinator.ObserveAsync(repoId, TargetRepo, descriptor, snapshot, CancellationToken.None);
        store
            .TryTransitionEngagementRound(
                admitted.RoundId!.Value,
                EngagementRoundStatus.Pending,
                EngagementRoundStatus.Running,
                time.GetUtcNow()
            )
            .Should()
            .BeTrue();
        var calls = 0;
        var executor = new MergedCloseRoundExecutor(
            store,
            (_, substep, _) =>
            {
                calls++;
                return Task.FromResult(
                    substep == MergedCloseSubstep.CandidatesBuilt
                        ? MergedCloseSubstepResult.Retry
                        : CompletedWithArtifact(substep)
                );
            },
            time
        );
        var runner = new EngagementRoundRunner(
            store,
            [executor],
            new CodeReviewDaemonOptions { MaxDurableRetryAttempts = 3 },
            time,
            LoggerFactory.CreateLogger<EngagementRoundRunner>()
        );
        var sweeper = CreateSweeper(
            [Pr("49", "review/widgets-49")],
            (_, _) => Task.FromResult(PrLifecycle.Merged),
            CreateBranchManager(new FakeSandboxCommandRunner()),
            (_, _, ct) => coordinator.ObserveAsync(repoId, TargetRepo, descriptor, snapshot, ct),
            (decision, ct) => runner.RunAsync(decision, null, ct),
            (_, _, ct) => coordinator.ReconcileAsync(repoId, TargetRepo, descriptor, snapshot, ct)
        );

        await sweeper.SweepAsync(CancellationToken.None);

        calls.Should().Be(2);
        var round = store.GetEngagementRound(admitted.RoundId.Value)!;
        round.Status.Should().Be(EngagementRoundStatus.RetryPending);
        round.GovernedFailureCount.Should().Be(1);
        store.ListEngagementRounds(round.PrEngagementId).Should().ContainSingle().Which.Id.Should().Be(round.Id);
    }

    [Fact]
    public async Task Sweep_deletes_an_abandoned_branch_when_terminal_observation_fails()
    {
        var commandRunner = new FakeSandboxCommandRunner();
        var pr = Pr("50", "review/widgets-50");
        var sweeper = CreateSweeper(
            [pr],
            (_, _) => Task.FromResult(PrLifecycle.Abandoned),
            CreateBranchManager(commandRunner),
            (_, _, _) => throw new InvalidOperationException("closed/abandoned snapshot mismatch")
        );

        await sweeper.SweepAsync(CancellationToken.None);

        var commands = commandRunner.Commands.Select(command => string.Join(' ', command.Argv)).ToList();
        commands.Should().Contain(command => command.Contains($"branch -D {pr.Branch}"));
        commands.Should().Contain(command => command.Contains($"push origin --delete {pr.Branch}"));
    }

    [Fact]
    public async Task Sweep_parks_repeated_required_failures_without_archiving_the_notes_branch()
    {
        using var database = new TempSqliteDatabase();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 2, 10, 0, 0, TimeSpan.Zero));
        using var store = new ReviewStore(database.ConnectionString, time);
        var repoId = store.EnsureRepo(TargetRepo);
        var snapshot = ProviderEngagementSnapshot.Create(
            PrLifecycleState.Merged,
            "head-48",
            "base-48",
            new ProviderActivityWatermark("github", time.GetUtcNow(), "merge:48"),
            []
        );
        var descriptor = new PullRequestDescriptor
        {
            PrId = "48",
            HeadSha = snapshot.HeadSha,
            BaseSha = snapshot.BaseSha,
            TriggerWatermark = snapshot.LatestObserved.StableObjectId,
            LifecycleState = snapshot.Lifecycle,
        };
        var coordinator = new PrEngagementCoordinator(store, time);
        var executor = new MergedCloseRoundExecutor(
            store,
            (_, _, _) => Task.FromResult(MergedCloseSubstepResult.Retry),
            time
        );
        var runner = new EngagementRoundRunner(
            store,
            [executor],
            new CodeReviewDaemonOptions { MaxDurableRetryAttempts = 2 },
            time,
            LoggerFactory.CreateLogger<EngagementRoundRunner>()
        );
        var commandRunner = new FakeSandboxCommandRunner();
        var sweeper = CreateSweeper(
            [Pr("48", "review/widgets-48")],
            (_, _) => Task.FromResult(PrLifecycle.Merged),
            CreateBranchManager(commandRunner),
            (_, _, ct) => coordinator.ObserveAsync(repoId, TargetRepo, descriptor, snapshot, ct),
            (decision, ct) => runner.RunAsync(decision, null, ct)
        );

        await sweeper.SweepAsync(CancellationToken.None);
        await sweeper.SweepAsync(CancellationToken.None);

        var engagement = store.CreateOrGetEngagement(
            EngagementSeed(repoId, time) with
            {
                PrId = "48",
                LatestHeadSha = "head-48",
                LatestBaseSha = "base-48",
            }
        );
        var round = store.ListEngagementRounds(engagement.Id).Single();
        round.Status.Should().Be(EngagementRoundStatus.Parked);
        round.GovernedFailureCount.Should().Be(2);
        store
            .ListAuditRecordsForRound(round.Id)
            .Should()
            .NotContain(record => record.GenerationId == MergedCloseSubstep.Archived.ToString());
        commandRunner.Commands.Should().BeEmpty();
    }

    private static PrEngagement EngagementSeed(long repoId, FakeTimeProvider time) =>
        new(
            0,
            repoId,
            "github",
            "42",
            PrLifecycleState.Merged,
            "head-42",
            "base-42",
            "head-42",
            new ProviderActivityWatermark("github", time.GetUtcNow(), "merge:42"),
            null,
            null,
            null,
            null,
            null,
            null,
            time.GetUtcNow()
        );

    [Fact]
    public async Task Sweep_preserves_a_merged_notes_branch_when_no_engagement_observer_is_configured()
    {
        var runner = new FakeSandboxCommandRunner();
        var pr = Pr("45", "review/widgets-45");
        var sweeper = CreateSweeper([pr], (_, _) => Task.FromResult(PrLifecycle.Merged), CreateBranchManager(runner));

        await sweeper.SweepAsync(CancellationToken.None);

        runner.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task Sweep_takes_no_action_for_an_open_PR()
    {
        var runner = new FakeSandboxCommandRunner();
        var pr = Pr("44", "review/widgets-44");
        var observations = 0;
        var sweeper = CreateSweeper(
            [pr],
            (_, _) => Task.FromResult(PrLifecycle.Open),
            CreateBranchManager(runner),
            (_, _, _) =>
            {
                observations++;
                return Task.FromResult(new EngagementDecision(EngagementDecisionKind.None, null, "unexpected"));
            }
        );

        await sweeper.SweepAsync(CancellationToken.None);

        observations.Should().Be(0);
        runner.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task Sweep_reports_abandonment_then_deletes_the_notes_branch_without_merging()
    {
        var runner = new FakeSandboxCommandRunner();
        var pr = Pr("43", "review/widgets-43");
        var observations = new List<PrLifecycle>();
        var closeExecutions = 0;
        var sweeper = CreateSweeper(
            [pr],
            (_, _) => Task.FromResult(PrLifecycle.Abandoned),
            CreateBranchManager(runner),
            (_, lifecycle, _) =>
            {
                observations.Add(lifecycle);
                return Task.FromResult(
                    new EngagementDecision(EngagementDecisionKind.CleanupTerminal, null, "pr_abandoned")
                );
            },
            (_, _) =>
            {
                closeExecutions++;
                return Task.CompletedTask;
            }
        );

        await sweeper.SweepAsync(CancellationToken.None);

        observations.Should().Equal(PrLifecycle.Abandoned);
        closeExecutions.Should().Be(0, "abandoned PRs never enter the merged-close outcome pipeline");
        var commands = runner.Commands.Select(command => string.Join(' ', command.Argv)).ToList();
        commands.Should().Contain(command => command.Contains($"branch -D {pr.Branch}"));
        commands.Should().Contain(command => command.Contains($"push origin --delete {pr.Branch}"));
        commands.Should().NotContain(command => command.Contains("merge "));
    }

    [Fact]
    public async Task Sweep_keeps_a_parked_merged_close_visible_without_merging_or_re_admitting()
    {
        var runner = new FakeSandboxCommandRunner();
        var pr = Pr("47", "review/widgets-47");
        var observations = 0;
        var sweeper = CreateSweeper(
            [pr],
            (_, _) => Task.FromResult(PrLifecycle.Merged),
            CreateBranchManager(runner),
            (_, _, _) =>
            {
                observations++;
                return Task.FromResult(
                    new EngagementDecision(EngagementDecisionKind.None, RoundId: 9, "merged_close_blocked")
                );
            }
        );

        await sweeper.SweepAsync(CancellationToken.None);
        await sweeper.SweepAsync(CancellationToken.None);

        observations.Should().Be(2, "parked work remains durably visible on each authoritative lifecycle sweep");
        runner.Commands.Should().BeEmpty("a parked required close output must preserve the notes branch");
    }

    [Fact]
    public async Task Sweep_isolates_a_per_PR_lifecycle_lookup_failure_so_remaining_PRs_still_resolve()
    {
        var runner = new FakeSandboxCommandRunner();
        var failingPr = Pr("46", "review/widgets-46");
        var okPr = Pr("47", "review/widgets-47");
        var sweeper = CreateSweeper(
            [failingPr, okPr],
            (pr, _) =>
                pr.PrId == failingPr.PrId
                    ? throw new InvalidOperationException("simulated lifecycle lookup failure")
                    : Task.FromResult(PrLifecycle.Abandoned),
            CreateBranchManager(runner)
        );

        var act = () => sweeper.SweepAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
        var commands = runner.Commands.Select(command => string.Join(' ', command.Argv)).ToList();
        commands.Should().Contain(command => command.Contains($"branch -D {okPr.Branch}"));
        commands.Should().Contain(command => command.Contains($"push origin --delete {okPr.Branch}"));
    }
}
