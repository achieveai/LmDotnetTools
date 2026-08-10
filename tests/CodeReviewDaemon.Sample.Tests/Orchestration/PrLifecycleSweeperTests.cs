using AchieveAi.LmDotnetTools.LmTestUtils.Logging;
using CodeReviewDaemon.Sample.Agents;
using CodeReviewDaemon.Sample.Orchestration;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using CodeReviewDaemon.Sample.Workspace.Git;
using CodeReviewDaemon.Sample.Workspace.Sandbox;
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
            LoggerFactory.CreateLogger<ReviewBranchManager>());

    private PrLifecycleSweeper CreateSweeper(
        IReadOnlyList<ReviewedPr> reviewedPrs,
        Func<ReviewedPr, CancellationToken, Task<PrLifecycle>> getPrLifecycleAsync,
        ReviewBranchManager branchManager,
        bool mergeNotesBranchOnClose,
        Func<ReviewedPr, CancellationToken, Task<KnowledgeExtractionOutcome>>? extractKnowledgeAsync = null
    ) =>
        new(
            _ => Task.FromResult(reviewedPrs),
            getPrLifecycleAsync,
            branchManager,
            RepoRoot,
            DefaultBranch,
            mergeNotesBranchOnClose,
            LoggerFactory.CreateLogger<PrLifecycleSweeper>(),
            extractKnowledgeAsync);

    private static ReviewedPr Pr(string prId, string branch) => new(TargetRepo, "github", prId, branch);

    /// <summary>
    /// The sweep must report what it saw, because it is the ONLY place at-close knowledge extraction can run
    /// from — extraction hangs off the Merged path and nowhere else.
    /// <para>
    /// This exists because of a diagnosis that could not be completed. Every review brief on the NOVA store
    /// logged <c>prior-knowledge=0</c> and every knowledge-digest call reported <c>"Listed":0</c>, 120 times
    /// across two days. The sweep was the prime suspect and could be neither convicted nor cleared, because
    /// <c>SweepAsync</c> logged NOTHING unless an individual PR threw: "the Knowledge Base is empty because
    /// no reviewed PR has merged yet" and "…because extraction is broken" produce byte-identical logs. The
    /// merged tally is what separates them, and the extraction-wired flag is what rules out the third
    /// possibility — that the agent was never composed in at all.
    /// </para>
    /// <para>
    /// The failing PR is in the fixture deliberately: a lifecycle lookup that throws must be counted, not
    /// swallowed into the "open" bucket, or the tally would understate the merged population it exists to
    /// measure.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Sweep_reports_the_lifecycle_tally_of_everything_it_swept()
    {
        var runner = new FakeSandboxCommandRunner();
        var capturing = new CapturingLoggerFactory();
        var lifecycles = new Dictionary<string, PrLifecycle>(StringComparer.Ordinal)
        {
            ["open-pr"] = PrLifecycle.Open,
            ["merged-pr"] = PrLifecycle.Merged,
            ["abandoned-pr"] = PrLifecycle.Abandoned,
        };
        var sweeper = new PrLifecycleSweeper(
            _ => Task.FromResult<IReadOnlyList<ReviewedPr>>(
                [Pr("1", "open-pr"), Pr("2", "merged-pr"), Pr("3", "abandoned-pr"), Pr("4", "unreachable-pr")]),
            (pr, _) => lifecycles.TryGetValue(pr.Branch, out var lifecycle)
                ? Task.FromResult(lifecycle)
                : throw new InvalidOperationException("lifecycle lookup failed"),
            CreateBranchManager(runner),
            RepoRoot,
            DefaultBranch,
            mergeNotesBranchOnClose: true,
            capturing.CreateLogger<PrLifecycleSweeper>(),
            (_, _) => Task.FromResult(KnowledgeExtractionOutcome.Declined));

        await sweeper.SweepAsync(CancellationToken.None);

        capturing.Capturing.CountAtLevel(
                LogLevel.Information,
                "1 open, 1 merged, 1 abandoned, 0 already resolved, 1 failed")
            .Should()
            .Be(1, "the tally is the whole point — a merged count of 0 is what would explain an empty KB");
        capturing.Capturing.CountAtLevel(LogLevel.Information, "knowledge extraction is wired")
            .Should()
            .Be(1, "an unwired extraction seam is a third, separately-actionable cause of an empty KB");
    }

    [Fact]
    public async Task Sweep_merges_the_notes_branch_of_a_merged_PR_when_merge_on_close_is_enabled()
    {
        var runner = new FakeSandboxCommandRunner();
        var pr = Pr("42", "review/widgets-42");
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
        var pr = Pr("43", "review/widgets-43");
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
        var pr = Pr("44", "review/widgets-44");
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
        var pr = Pr("45", "review/widgets-45");
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
        var failingPr = Pr("46", "review/widgets-46");
        var okPr = Pr("47", "review/widgets-47");
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

    [Fact]
    public async Task Sweep_runs_knowledge_extraction_before_merging_a_merged_PR()
    {
        var runner = new FakeSandboxCommandRunner();
        var pr = Pr("42", "review/widgets-42");
        var invokedPrs = new List<string>();
        var runnerCommandCountAtInvocation = -1;
        var sweeper = CreateSweeper(
            [pr],
            (_, _) => Task.FromResult(PrLifecycle.Merged),
            CreateBranchManager(runner),
            mergeNotesBranchOnClose: true,
            extractKnowledgeAsync: (p, _) =>
            {
                invokedPrs.Add(p.PrId);
                // MergeToDefaultAsync's first git op is `fetch origin`; an empty command log here proves
                // extraction ran BEFORE the merge (design §1 — extract before the notes branch merges).
                runnerCommandCountAtInvocation = runner.Commands.Count;
                return Task.FromResult(KnowledgeExtractionOutcome.Wrote);
            });

        await sweeper.SweepAsync(CancellationToken.None);

        invokedPrs.Should().Equal("42");
        runnerCommandCountAtInvocation.Should().Be(0);
        var commands = runner.Commands.Select(c => string.Join(' ', c.Argv)).ToList();
        commands.Should().Contain(a => a.Contains($"merge --ff-only origin/{pr.Branch}"));
        commands.Should().Contain(a => a.Contains($"push origin {DefaultBranch}"));
    }

    [Fact]
    public async Task Sweep_does_not_run_knowledge_extraction_for_abandoned_or_open_PRs()
    {
        var runner = new FakeSandboxCommandRunner();
        var abandoned = Pr("43", "review/widgets-43");
        var open = Pr("44", "review/widgets-44");
        var invoked = new List<string>();
        var sweeper = CreateSweeper(
            [abandoned, open],
            (p, _) => Task.FromResult(p.PrId == abandoned.PrId ? PrLifecycle.Abandoned : PrLifecycle.Open),
            CreateBranchManager(runner),
            mergeNotesBranchOnClose: true,
            extractKnowledgeAsync: (p, _) =>
            {
                invoked.Add(p.PrId);
                return Task.FromResult(KnowledgeExtractionOutcome.Wrote);
            });

        await sweeper.SweepAsync(CancellationToken.None);

        invoked.Should().BeEmpty();
    }

    [Fact]
    public async Task Sweep_defers_the_merge_when_knowledge_extraction_throws()
    {
        var runner = new FakeSandboxCommandRunner();
        var pr = Pr("45", "review/widgets-45");
        var sweeper = CreateSweeper(
            [pr],
            (_, _) => Task.FromResult(PrLifecycle.Merged),
            CreateBranchManager(runner),
            mergeNotesBranchOnClose: true,
            extractKnowledgeAsync: (_, _) => throw new InvalidOperationException("simulated extraction failure"));

        var act = () => sweeper.SweepAsync(CancellationToken.None);

        // The throw is contained — it must never abort the sweep (design §6) — but it IS a failure, so the
        // notes branch is held back for a retry instead of being merged and deleted with nothing extracted.
        await act.Should().NotThrowAsync();
        var commands = runner.Commands.Select(c => string.Join(' ', c.Argv)).ToList();
        commands.Should().NotContain(a => a.Contains($"merge --ff-only origin/{pr.Branch}"));
    }

    [Fact]
    public async Task Sweep_leaves_the_notes_branch_intact_when_knowledge_extraction_fails_so_the_next_sweep_retries()
    {
        var runner = new FakeSandboxCommandRunner();
        var pr = Pr("46", "review/widgets-46");
        var lifecycleLookups = 0;
        var attempts = 0;
        var sweeper = CreateSweeper(
            [pr],
            (_, _) =>
            {
                lifecycleLookups++;
                return Task.FromResult(PrLifecycle.Merged);
            },
            CreateBranchManager(runner),
            mergeNotesBranchOnClose: true,
            extractKnowledgeAsync: (_, _) =>
            {
                attempts++;
                return Task.FromResult(KnowledgeExtractionOutcome.Failed);
            });

        await sweeper.SweepAsync(CancellationToken.None);

        var commands = runner.Commands.Select(c => string.Join(' ', c.Argv)).ToList();
        commands.Should().NotContain(
            a => a.Contains($"merge --ff-only origin/{pr.Branch}"),
            "merging deletes the notes branch, which would make this failed extraction permanent (defect D5)");

        await sweeper.SweepAsync(CancellationToken.None);

        lifecycleLookups.Should().Be(2, "a deferred PR is not cached as terminally resolved");
        attempts.Should().Be(2, "the next sweep retries the extraction");
    }

    [Fact]
    public async Task Sweep_merges_anyway_once_knowledge_extraction_has_exhausted_its_retries()
    {
        var runner = new FakeSandboxCommandRunner();
        var pr = Pr("47", "review/widgets-47");
        var attempts = 0;
        var sweeper = CreateSweeper(
            [pr],
            (_, _) => Task.FromResult(PrLifecycle.Merged),
            CreateBranchManager(runner),
            mergeNotesBranchOnClose: true,
            extractKnowledgeAsync: (_, _) =>
            {
                attempts++;
                return Task.FromResult(KnowledgeExtractionOutcome.Failed);
            });

        // Three sweeps: two deferrals, then the cap is reached and the lifecycle proceeds regardless —
        // extraction bounds the delay, it never blocks the lifecycle outright (design §6).
        await sweeper.SweepAsync(CancellationToken.None);
        await sweeper.SweepAsync(CancellationToken.None);
        await sweeper.SweepAsync(CancellationToken.None);

        attempts.Should().Be(3, "extraction is retried up to the cap, then given up on");
        var commands = runner.Commands.Select(c => string.Join(' ', c.Argv)).ToList();
        commands.Should().Contain(a => a.Contains($"merge --ff-only origin/{pr.Branch}"));
        commands.Count(a => a.Contains($"push origin {DefaultBranch}"))
            .Should().Be(1, "the merge happens exactly once, on the sweep that hit the cap");
    }

    [Fact]
    public async Task Sweep_merges_immediately_when_knowledge_extraction_declines()
    {
        var runner = new FakeSandboxCommandRunner();
        var pr = Pr("49", "review/widgets-49");
        var sweeper = CreateSweeper(
            [pr],
            (_, _) => Task.FromResult(PrLifecycle.Merged),
            CreateBranchManager(runner),
            mergeNotesBranchOnClose: true,
            // "This PR carried no durable knowledge" is a valid outcome, not a failure — nothing to retry.
            extractKnowledgeAsync: (_, _) => Task.FromResult(KnowledgeExtractionOutcome.Declined));

        await sweeper.SweepAsync(CancellationToken.None);

        var commands = runner.Commands.Select(c => string.Join(' ', c.Argv)).ToList();
        commands.Should().Contain(a => a.Contains($"merge --ff-only origin/{pr.Branch}"));
        commands.Should().Contain(a => a.Contains($"push origin {DefaultBranch}"));
    }

    [Fact]
    public async Task Sweep_does_not_re_resolve_a_merged_PR_on_a_later_sweep()
    {
        var runner = new FakeSandboxCommandRunner();
        var pr = Pr("42", "review/widgets-42");
        var lifecycleLookups = 0;
        var sweeper = CreateSweeper(
            [pr],
            (_, _) =>
            {
                lifecycleLookups++;
                return Task.FromResult(PrLifecycle.Merged);
            },
            CreateBranchManager(runner),
            mergeNotesBranchOnClose: true);

        await sweeper.SweepAsync(CancellationToken.None);
        await sweeper.SweepAsync(CancellationToken.None);

        lifecycleLookups.Should().Be(1, "a merged-and-swept branch is cached, so later sweeps skip it entirely");
        runner.Commands.Select(c => string.Join(' ', c.Argv))
            .Count(a => a.Contains($"push origin {DefaultBranch}"))
            .Should().Be(1, "the notes branch is merged exactly once, not re-merged every poll");
    }

    [Fact]
    public async Task Sweep_retries_a_merged_PR_whose_merge_push_failed()
    {
        var runner = new FakeSandboxCommandRunner();
        // The push never succeeds, so MergeToDefaultAsync returns false and the branch is NOT cached as done.
        runner.OnArgvContains(
            $"push origin {DefaultBranch}",
            new SandboxCommandResult(1, string.Empty, "rejected: non-fast-forward"));
        var pr = Pr("48", "review/widgets-48");
        var lifecycleLookups = 0;
        var sweeper = CreateSweeper(
            [pr],
            (_, _) =>
            {
                lifecycleLookups++;
                return Task.FromResult(PrLifecycle.Merged);
            },
            CreateBranchManager(runner),
            mergeNotesBranchOnClose: true);

        await sweeper.SweepAsync(CancellationToken.None);
        await sweeper.SweepAsync(CancellationToken.None);

        lifecycleLookups.Should().Be(2, "a failed merge is not cached, so the next sweep retries it");
    }
}
