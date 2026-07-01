using AchieveAi.LmDotnetTools.LmTestUtils.Logging;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using CodeReviewDaemon.Sample.Workspace.Git;
using CodeReviewDaemon.Sample.Workspace.ReviewBot;
using CodeReviewDaemon.Sample.Workspace.Sandbox;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace CodeReviewDaemon.Sample.Tests.Scenarios;

/// <summary>
/// P3.2 — the idempotent ReviewBot setup decision. An empty checkout is seeded + committed + pushed; a
/// fully-seeded checkout is a no-op; a partially-seeded checkout is reported malformed (with the exact
/// gaps) and is never silently mutated.
/// </summary>
public sealed class ReviewBotInitializerTests : LoggingTestBase
{
    private const string RepoRoot = "/work/reviewbot";
    private const string DefaultBranch = "main";

    public ReviewBotInitializerTests(ITestOutputHelper output)
        : base(output)
    {
    }

    private ReviewBotInitializer CreateInitializer(
        ISandboxCommandRunner runner,
        ISandboxFileSystem fileSystem
    ) =>
        new(new GitRunner(runner), fileSystem, LoggerFactory.CreateLogger<ReviewBotInitializer>());

    [Fact]
    public async Task Seeds_and_pushes_the_skeleton_for_an_empty_checkout()
    {
        var runner = new FakeSandboxCommandRunner();
        var fs = new FakeSandboxFileSystem();

        var result = await CreateInitializer(runner, fs)
            .InitializeAsync(RepoRoot, DefaultBranch, CancellationToken.None);

        result.Outcome.Should().Be(ReviewBotInitOutcome.Created);
        result.MissingPaths.Should().BeEmpty();

        fs.Files.Keys.Should()
            .Contain(
            [
                $"{RepoRoot}/README.md",
                $"{RepoRoot}/KnowledgeBase/_toc.md",
                $"{RepoRoot}/KnowledgeBase/.gitkeep",
                $"{RepoRoot}/PRs/.gitkeep",
            ]);

        var commands = runner.Commands.Select(c => string.Join(' ', c.Argv)).ToList();
        commands.Should().Contain(a => a.Contains($"checkout -B {DefaultBranch}"));
        commands.Should().Contain(a => a.Contains("add -A"));
        commands.Should().Contain(a => a.Contains("commit -m"));
        commands.Should().Contain(a => a.Contains($"push -u origin {DefaultBranch}"));
    }

    [Fact]
    public async Task Is_a_no_op_when_the_repo_is_already_seeded()
    {
        var runner = new FakeSandboxCommandRunner();
        var fs = new FakeSandboxFileSystem();
        SeedAll(fs);

        var result = await CreateInitializer(runner, fs)
            .InitializeAsync(RepoRoot, DefaultBranch, CancellationToken.None);

        result.Outcome.Should().Be(ReviewBotInitOutcome.AlreadySeeded);
        fs.Writes.Should().BeEmpty("a well-formed repo must not be mutated");
        runner.Commands.Should().NotContain(c => string.Join(' ', c.Argv).Contains("commit"));
    }

    [Fact]
    public async Task Reports_malformed_with_the_missing_paths_for_a_partial_checkout()
    {
        var runner = new FakeSandboxCommandRunner();
        var fs = new FakeSandboxFileSystem();
        // Only README + PRs/.gitkeep present; the KnowledgeBase skeleton is missing.
        fs.Files[$"{RepoRoot}/README.md"] = "# ReviewBot";
        fs.Files[$"{RepoRoot}/PRs/.gitkeep"] = string.Empty;

        var result = await CreateInitializer(runner, fs)
            .InitializeAsync(RepoRoot, DefaultBranch, CancellationToken.None);

        result.Outcome.Should().Be(ReviewBotInitOutcome.Malformed);
        result.MissingPaths.Should().Equal("KnowledgeBase/.gitkeep", "KnowledgeBase/_toc.md");
        fs.Writes.Should().BeEmpty("a malformed repo must surface, not be silently repaired");
        runner.Commands.Should().BeEmpty();
    }

    private static void SeedAll(FakeSandboxFileSystem fs)
    {
        fs.Files[$"{RepoRoot}/README.md"] = "# ReviewBot";
        fs.Files[$"{RepoRoot}/PRs/.gitkeep"] = string.Empty;
        fs.Files[$"{RepoRoot}/KnowledgeBase/.gitkeep"] = string.Empty;
        fs.Files[$"{RepoRoot}/KnowledgeBase/_toc.md"] = "# Knowledge Base";
    }
}
