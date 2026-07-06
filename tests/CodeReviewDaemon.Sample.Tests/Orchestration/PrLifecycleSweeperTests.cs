using AchieveAi.LmDotnetTools.LmTestUtils.Logging;
using CodeReviewDaemon.Sample.Orchestration;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using CodeReviewDaemon.Sample.Workspace.Git;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace CodeReviewDaemon.Sample.Tests.Orchestration;

/// <summary>
/// <see cref="PrLifecycleSweeper"/> resolves each reviewed PR's persistent notes branch when the PR
/// closes: merges into the store default branch when merged (if enabled), deletes when abandoned, and
/// leaves an open PR's branch untouched. Drives a REAL <see cref="ReviewBranchManager"/> over a
/// <see cref="FakeSandboxCommandRunner"/> (mirroring <c>ReviewBranchManagerTests</c>) so the recorded git
/// commands prove the sweeper wired the right op for each lifecycle, plus fake <c>Func</c> seams for the
/// PR list and lifecycle lookup the sweeper is composed from.
/// </summary>
public sealed class PrLifecycleSweeperTests : LoggingTestBase
{
    private const string RepoRoot = "/host/reviewbot";
    private const string DefaultBranch = "main";

    private static readonly RepoIdentity TargetRepo = new()
    {
        Provider = "github",
        OrgOrOwner = "acme",
        RepoName = "widgets",
    };

    public PrLifecycleSweeperTests(ITestOutputHelper output)
        : base(output)
    {
    }

    private ReviewBranchManager CreateBranchManager(FakeSandboxCommandRunner runner) =>
        new(
            new GitRunner(runner),
            new FakeSandboxFileSystem(),
            "github",
            LoggerFactory.CreateLogger<ReviewBranchManager>());

    private PrLifecycleSweeper CreateSweeper(
        IReadOnlyList<ReviewedPr> reviewedPrs,
        Func<ReviewedPr, CancellationToken, Task<PrLifecycle>> getPrLifecycleAsync,
        ReviewBranchManager branchManager,
        bool mergeNotesBranchOnClose
    ) =>
        new(
            _ => Task.FromResult(reviewedPrs),
            getPrLifecycleAsync,
            branchManager,
            RepoRoot,
            DefaultBranch,
            mergeNotesBranchOnClose,
            LoggerFactory.CreateLogger<PrLifecycleSweeper>());

    private static ReviewedPr Pr(string prId, string branch) => new(TargetRepo, "github", prId, branch);

    [Fact]
    public async Task Sweep_merges_the_notes_branch_of_a_merged_PR_when_merge_on_close_is_enabled()
    {
        var runner = new FakeSandboxCommandRunner();
        var pr = Pr("42", "review/github/acme-widgets/42");
        var sweeper = CreateSweeper(
            [pr],
            (_, _) => Task.FromResult(PrLifecycle.Merged),
            CreateBranchManager(runner),
            mergeNotesBranchOnClose: true);

        await sweeper.SweepAsync(CancellationToken.None);

        var commands = runner.Commands.Select(c => string.Join(' ', c.Argv)).ToList();
        // The sweeper-store clone has no local notes branch, so the merge must fetch and target the
        // remote-tracking ref (origin/<branch>), not the bare name.
        commands.Should().Contain(a => a.Contains("fetch origin"));
        commands.Should().Contain(a => a.Contains($"merge --ff-only origin/{pr.Branch}"));
        commands.Should().Contain(a => a.Contains($"push origin {DefaultBranch}"));
    }

    [Fact]
    public async Task Sweep_deletes_the_notes_branch_of_an_abandoned_PR_and_never_merges()
    {
        var runner = new FakeSandboxCommandRunner();
        var pr = Pr("43", "review/github/acme-widgets/43");
        var sweeper = CreateSweeper(
            [pr],
            (_, _) => Task.FromResult(PrLifecycle.Abandoned),
            CreateBranchManager(runner),
            mergeNotesBranchOnClose: true);

        await sweeper.SweepAsync(CancellationToken.None);

        var commands = runner.Commands.Select(c => string.Join(' ', c.Argv)).ToList();
        commands.Should().Contain(a => a.Contains($"branch -D {pr.Branch}"));
        commands.Should().Contain(a => a.Contains($"push origin --delete {pr.Branch}"));
        commands.Should().NotContain(a => a.Contains("merge "));
    }

    [Fact]
    public async Task Sweep_takes_no_action_for_an_open_PR()
    {
        var runner = new FakeSandboxCommandRunner();
        var pr = Pr("44", "review/github/acme-widgets/44");
        var sweeper = CreateSweeper(
            [pr],
            (_, _) => Task.FromResult(PrLifecycle.Open),
            CreateBranchManager(runner),
            mergeNotesBranchOnClose: true);

        await sweeper.SweepAsync(CancellationToken.None);

        runner.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task Sweep_leaves_the_notes_branch_of_a_merged_PR_when_merge_on_close_is_disabled()
    {
        var runner = new FakeSandboxCommandRunner();
        var pr = Pr("45", "review/github/acme-widgets/45");
        var sweeper = CreateSweeper(
            [pr],
            (_, _) => Task.FromResult(PrLifecycle.Merged),
            CreateBranchManager(runner),
            mergeNotesBranchOnClose: false);

        await sweeper.SweepAsync(CancellationToken.None);

        runner.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task Sweep_isolates_a_per_PR_lifecycle_lookup_failure_so_the_remaining_PRs_still_resolve()
    {
        var runner = new FakeSandboxCommandRunner();
        var failingPr = Pr("46", "review/github/acme-widgets/46");
        var okPr = Pr("47", "review/github/acme-widgets/47");
        var sweeper = CreateSweeper(
            [failingPr, okPr],
            (pr, _) => pr.PrId == failingPr.PrId
                ? throw new InvalidOperationException("simulated lifecycle lookup failure")
                : Task.FromResult(PrLifecycle.Abandoned),
            CreateBranchManager(runner),
            mergeNotesBranchOnClose: true);

        var act = () => sweeper.SweepAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
        var commands = runner.Commands.Select(c => string.Join(' ', c.Argv)).ToList();
        commands.Should().Contain(a => a.Contains($"branch -D {okPr.Branch}"));
        commands.Should().Contain(a => a.Contains($"push origin --delete {okPr.Branch}"));
    }
}
