using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using CodeReviewDaemon.Sample.Workspace.Git;
using CodeReviewDaemon.Sample.Workspace.Sandbox;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeReviewDaemon.Sample.Tests.Scenarios;

/// <summary>
/// P3.3 — the §2 durable one-commit retention sequence. The manager creates a review branch from the
/// default, writes both artifact sets, lands them as a single commit, fast-forwards the default,
/// pushes (with bounded rebase-retry), and only then deletes the review branch. A push that never
/// succeeds must leave the review branch intact and report <see cref="ReviewBotPublishOutcome.GitSyncFailed"/>
/// so the orchestrator can reconcile — there is no window where artifacts are lost.
/// </summary>
public sealed class ReviewBotRepoManagerTests
{
    private const string RepoRoot = "/work/reviewbot";
    private const string DefaultBranch = "main";
    private const string ReviewBranch = "review/github/acme-widgets/42";

    private static readonly RepoIdentity TargetRepo = new()
    {
        Provider = "github",
        OrgOrOwner = "acme",
        RepoName = "widgets",
    };

    private static readonly ReviewBotPublishRequest Request = new(
        TargetRepo,
        PrNumber: 42,
        HeadSha: "abcd1234ef567890",
        DefaultBranch: DefaultBranch,
        Files:
        [
            new ReviewArtifactFile("PRs/github/acme-widgets/42-abcd1234/review.md", "# Review"),
            new ReviewArtifactFile("KnowledgeBase/_toc.md", "# ToC"),
        ]);

    private static ReviewBotRepoManager CreateManager(
        ISandboxCommandRunner runner,
        ISandboxFileSystem fileSystem
    ) =>
        new(
            new GitRunner(runner),
            fileSystem,
            "github",
            NullLogger<ReviewBotRepoManager>.Instance);

    [Fact]
    public async Task Publishes_in_the_documented_order_and_deletes_the_review_branch()
    {
        var runner = new FakeSandboxCommandRunner();
        // rev-parse must yield the pushed SHA the caller records.
        runner.OnArgvContains("rev-parse main", new SandboxCommandResult(0, "f00dcafef00dcafe\n", string.Empty));
        var fs = new FakeSandboxFileSystem();

        var result = await CreateManager(runner, fs).PublishAsync(RepoRoot, Request, CancellationToken.None);

        result.Outcome.Should().Be(ReviewBotPublishOutcome.Pushed);
        result.ReviewBranch.Should().Be(ReviewBranch);
        result.PushedSha.Should().Be("f00dcafef00dcafe");
        result.ReviewBranchDeleted.Should().BeTrue();

        var commands = runner.Commands.Select(c => string.Join(' ', c.Argv)).ToList();

        // The §2 steps must appear in this relative order.
        IndexOf(commands, $"checkout -B {ReviewBranch} {DefaultBranch}")
            .Should()
            .BeLessThan(IndexOf(commands, "add -A"));
        IndexOf(commands, "add -A").Should().BeLessThan(IndexOf(commands, "commit -m"));
        IndexOf(commands, "commit -m").Should().BeLessThan(IndexOf(commands, $"checkout {DefaultBranch}"));
        IndexOf(commands, $"checkout {DefaultBranch}")
            .Should()
            .BeLessThan(IndexOf(commands, $"merge --ff-only {ReviewBranch}"));
        IndexOf(commands, $"merge --ff-only {ReviewBranch}")
            .Should()
            .BeLessThan(IndexOf(commands, "push origin main"));
        IndexOf(commands, "push origin main").Should().BeLessThan(IndexOf(commands, "rev-parse main"));
        IndexOf(commands, "rev-parse main").Should().BeLessThan(IndexOf(commands, $"branch -D {ReviewBranch}"));
        IndexOf(commands, $"branch -D {ReviewBranch}")
            .Should()
            .BeLessThan(IndexOf(commands, $"push origin --delete {ReviewBranch}"));

        // Both artifact sets were written into the checkout before the commit.
        fs.Files.Should().ContainKey($"{RepoRoot}/PRs/github/acme-widgets/42-abcd1234/review.md");
        fs.Files.Should().ContainKey($"{RepoRoot}/KnowledgeBase/_toc.md");

        // Every git invocation carries the hardening flags.
        runner
            .Commands.Should()
            .OnlyContain(c => c.Argv.Contains("core.hooksPath=/dev/null"));
    }

    [Fact]
    public async Task Keeps_the_review_branch_and_reports_GitSyncFailed_when_the_push_never_succeeds()
    {
        var runner = new FakeSandboxCommandRunner();
        runner.OnArgvContains("push origin main", new SandboxCommandResult(1, string.Empty, "rejected"));
        var fs = new FakeSandboxFileSystem();

        var result = await CreateManager(runner, fs).PublishAsync(RepoRoot, Request, CancellationToken.None);

        result.Outcome.Should().Be(ReviewBotPublishOutcome.GitSyncFailed);
        result.PushedSha.Should().BeNull();
        result.ReviewBranchDeleted.Should().BeFalse();

        var commands = runner.Commands.Select(c => string.Join(' ', c.Argv)).ToList();
        commands.Should().NotContain(a => a.Contains($"branch -D {ReviewBranch}"));
        commands.Should().NotContain(a => a.Contains($"push origin --delete {ReviewBranch}"));
    }

    [Fact]
    public async Task Rebases_and_retries_when_the_default_branch_advanced_under_the_push()
    {
        var runner = new FakeSandboxCommandRunner();
        // Push is rejected twice (remote moved), then succeeds on the third attempt.
        runner.OnArgvContainsSequence(
            "push origin main",
            new SandboxCommandResult(1, string.Empty, "rejected"),
            new SandboxCommandResult(1, string.Empty, "rejected"),
            new SandboxCommandResult(0, string.Empty, string.Empty));
        runner.OnArgvContains("rev-parse main", new SandboxCommandResult(0, "deadbeef\n", string.Empty));
        var fs = new FakeSandboxFileSystem();

        var result = await CreateManager(runner, fs).PublishAsync(RepoRoot, Request, CancellationToken.None);

        result.Outcome.Should().Be(ReviewBotPublishOutcome.Pushed);
        result.PushedSha.Should().Be("deadbeef");
        result.ReviewBranchDeleted.Should().BeTrue();

        var commands = runner.Commands.Select(c => string.Join(' ', c.Argv)).ToList();
        commands.Count(a => a.Contains("push origin main")).Should().Be(3);
        commands.Count(a => a.Contains("pull --rebase origin main")).Should().Be(2);
    }

    private static int IndexOf(List<string> commands, string contains) =>
        commands.FindIndex(c => c.Contains(contains, StringComparison.Ordinal));
}
