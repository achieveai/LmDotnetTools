using AchieveAi.LmDotnetTools.LmTestUtils.Logging;
using CodeReviewDaemon.Sample.Agents;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using CodeReviewDaemon.Sample.Workspace.Git;
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
}
