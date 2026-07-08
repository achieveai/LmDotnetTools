using AchieveAi.LmDotnetTools.LmTestUtils.Logging;
using CodeReviewDaemon.Sample.Agents;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using CodeReviewDaemon.Sample.Workspace.Git;
using CodeReviewDaemon.Sample.Workspace.Sandbox;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace CodeReviewDaemon.Sample.Tests.Scenarios;

/// <summary>
/// <see cref="KnowledgeExtractionCommitter"/> is the git plumbing that carries the at-close Knowledge Base
/// write into the store's default branch: it checks the PR's notes branch out, runs the gated extraction,
/// and — only when the extraction actually wrote an entry — commits and pushes <c>KnowledgeBase/</c> onto the
/// notes branch so the sweeper's later <c>MergeToDefaultAsync</c> (a merge of <c>origin/&lt;branch&gt;</c>) carries
/// it into main. Drives a REAL <see cref="GitRunner"/> over a <see cref="FakeSandboxCommandRunner"/> so the
/// recorded git commands prove the add/commit/push happen (or don't) for each extraction outcome.
/// </summary>
public sealed class KnowledgeExtractionCommitterTests : LoggingTestBase
{
    private const string RepoRoot = "/host/sweeper-store";
    private const string Branch = "review/github/acme-widgets/42";
    private const string SourcePrRef = "github/acme/widgets/42";

    public KnowledgeExtractionCommitterTests(ITestOutputHelper output)
        : base(output)
    {
    }

    private KnowledgeExtractionCommitter CreateCommitter(FakeSandboxCommandRunner runner) =>
        new(new GitRunner(runner), RepoRoot, LoggerFactory.CreateLogger<KnowledgeExtractionCommitter>());

    [Fact]
    public async Task RunAsync_commits_and_pushes_the_KB_write_onto_the_notes_branch_when_extraction_writes()
    {
        var runner = new FakeSandboxCommandRunner();
        var committer = CreateCommitter(runner);

        await committer.RunAsync(
            Branch,
            SourcePrRef,
            _ => Task.FromResult<KnowledgeWriteResult?>(new KnowledgeWriteResult("system/x.md", "run-1")),
            CancellationToken.None);

        var commands = runner.Commands.Select(c => string.Join(' ', c.Argv)).ToList();
        // The notes branch is checked out BEFORE the write is committed + pushed so the sweeper's later
        // MergeToDefaultAsync (a merge of origin/<branch>) carries the entry into the default branch.
        commands.Should().Contain(a => a.Contains($"checkout -B {Branch} origin/{Branch}"));
        commands.Should().Contain(a => a.Contains("add -- KnowledgeBase"));
        commands.Should().Contain(a => a.Contains($"commit -m kb: extract from {SourcePrRef}"));
        commands.Should().Contain(a => a.Contains($"push origin {Branch}"));
    }

    [Fact]
    public async Task RunAsync_commits_nothing_when_the_extraction_gate_returns_null()
    {
        var runner = new FakeSandboxCommandRunner();
        var committer = CreateCommitter(runner);

        await committer.RunAsync(
            Branch, SourcePrRef, _ => Task.FromResult<KnowledgeWriteResult?>(null), CancellationToken.None);

        var commands = runner.Commands.Select(c => string.Join(' ', c.Argv)).ToList();
        // The gate wrote nothing durable, so there is nothing to stage, commit, or push.
        commands.Should().NotContain(a => a.Contains("add -- KnowledgeBase"));
        commands.Should().NotContain(a => a.Contains("commit -m"));
        commands.Should().NotContain(a => a.Contains($"push origin {Branch}"));
    }

    [Fact]
    public async Task RunAsync_swallows_an_extraction_failure_and_never_throws()
    {
        var runner = new FakeSandboxCommandRunner();
        var committer = CreateCommitter(runner);

        var act = () => committer.RunAsync(
            Branch,
            SourcePrRef,
            _ => throw new InvalidOperationException("simulated extraction failure"),
            CancellationToken.None);

        // Extraction failure must never block the lifecycle (design §6) — it is logged and swallowed, and
        // no commit is made.
        await act.Should().NotThrowAsync();
        var commands = runner.Commands.Select(c => string.Join(' ', c.Argv)).ToList();
        commands.Should().NotContain(a => a.Contains("commit -m"));
    }

    // ---- Finding 1: git exit codes must be checked (no false success, bounded push retry) -----------

    [Fact]
    public async Task RunAsync_rebases_and_retries_the_push_when_it_is_first_rejected_then_succeeds()
    {
        var runner = new FakeSandboxCommandRunner();
        runner.OnArgvContainsSequence(
            $"push origin {Branch}",
            new SandboxCommandResult(1, string.Empty, "rejected (non-fast-forward)"),
            new SandboxCommandResult(0, string.Empty, string.Empty));
        var logger = new CapturingLogger<KnowledgeExtractionCommitter>();
        var committer = new KnowledgeExtractionCommitter(new GitRunner(runner), RepoRoot, logger);

        await committer.RunAsync(
            Branch,
            SourcePrRef,
            _ => Task.FromResult<KnowledgeWriteResult?>(new KnowledgeWriteResult("system/x.md", "run-1")),
            CancellationToken.None);

        var commands = runner.Commands.Select(c => string.Join(' ', c.Argv)).ToList();
        // Rejected once, rebased onto the moved remote, then retried successfully.
        commands.Count(a => a.Contains($"push origin {Branch}")).Should().Be(2);
        commands.Should().Contain(a => a.Contains($"pull --rebase origin {Branch}"));
        // Success is reported ONLY once the push actually landed.
        logger.CountAtLevel(LogLevel.Information, "committed to notes branch").Should().Be(1);
    }

    [Fact]
    public async Task RunAsync_never_reports_success_and_stops_retrying_when_the_push_always_fails()
    {
        var runner = new FakeSandboxCommandRunner();
        runner.OnArgvContains(
            $"push origin {Branch}",
            new SandboxCommandResult(1, string.Empty, "rejected (non-fast-forward)"));
        var logger = new CapturingLogger<KnowledgeExtractionCommitter>();
        var committer = new KnowledgeExtractionCommitter(new GitRunner(runner), RepoRoot, logger);

        var act = () => committer.RunAsync(
            Branch,
            SourcePrRef,
            _ => Task.FromResult<KnowledgeWriteResult?>(new KnowledgeWriteResult("system/x.md", "run-1")),
            CancellationToken.None);

        // A push that is rejected forever must never throw and — crucially — must never be reported as a
        // success: the sweeper's later merge would fetch origin/<branch> WITHOUT this commit and silently
        // drop the entry while believing it was carried.
        await act.Should().NotThrowAsync();
        var commands = runner.Commands.Select(c => string.Join(' ', c.Argv)).ToList();
        commands.Count(a => a.Contains($"push origin {Branch}")).Should().Be(3); // bounded, not infinite
        logger.CountAtLevel(LogLevel.Information, "committed to notes branch").Should().Be(0);
        logger.WarningCount("could not push").Should().Be(1);
    }

    [Fact]
    public async Task RunAsync_never_runs_extraction_when_the_notes_branch_checkout_fails()
    {
        var runner = new FakeSandboxCommandRunner();
        runner.OnArgvContains(
            $"checkout -B {Branch} origin/{Branch}",
            new SandboxCommandResult(128, string.Empty, "fatal: unknown revision"));
        var committer = CreateCommitter(runner);
        var extractCalled = false;

        await committer.RunAsync(
            Branch,
            SourcePrRef,
            _ =>
            {
                extractCalled = true;
                return Task.FromResult<KnowledgeWriteResult?>(new KnowledgeWriteResult("system/x.md", "run-1"));
            },
            CancellationToken.None);

        // A failed checkout must never let extraction run — HEAD could be left on the wrong branch
        // (e.g. the default branch from a prior sweep), which would commit the KB write there instead.
        extractCalled.Should().BeFalse();
        var commands = runner.Commands.Select(c => string.Join(' ', c.Argv)).ToList();
        commands.Should().NotContain(a => a.Contains("commit -m"));
        commands.Should().NotContain(a => a.Contains("add -- KnowledgeBase"));
    }

    [Fact]
    public async Task RunAsync_aborts_the_rebase_and_stops_retrying_when_the_rebase_fails()
    {
        var runner = new FakeSandboxCommandRunner();
        // The push is rejected (remote moved) and the follow-up rebase then fails (conflict / force-push).
        runner.OnArgvContains(
            $"push origin {Branch}", new SandboxCommandResult(1, string.Empty, "rejected (non-fast-forward)"));
        runner.OnArgvContains(
            $"pull --rebase origin {Branch}", new SandboxCommandResult(1, string.Empty, "CONFLICT: could not apply"));
        var logger = new CapturingLogger<KnowledgeExtractionCommitter>();
        var committer = new KnowledgeExtractionCommitter(new GitRunner(runner), RepoRoot, logger);

        await committer.RunAsync(
            Branch,
            SourcePrRef,
            _ => Task.FromResult<KnowledgeWriteResult?>(new KnowledgeWriteResult("system/x.md", "run-1")),
            CancellationToken.None);

        var commands = runner.Commands.Select(c => string.Join(' ', c.Argv)).ToList();
        // A failed rebase leaves the checkout mid-rebase: it must abort (so the reused sweeper checkout is
        // clean next cycle) and stop retrying — pushing again would just fail and burn the retry budget.
        commands.Count(a => a.Contains($"push origin {Branch}")).Should().Be(1);
        commands.Should().Contain(a => a.Contains("rebase --abort"));
        // The entry could not be carried — never reported as a success.
        logger.CountAtLevel(LogLevel.Information, "committed to notes branch").Should().Be(0);
    }
}
