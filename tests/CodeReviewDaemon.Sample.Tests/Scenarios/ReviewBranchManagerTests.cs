using AchieveAi.LmDotnetTools.LmTestUtils.Logging;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using CodeReviewDaemon.Sample.Workspace.Git;
using CodeReviewDaemon.Sample.Workspace.Sandbox;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace CodeReviewDaemon.Sample.Tests.Scenarios;

/// <summary>
/// The three independently-callable retention ops the review branch is split into so it can persist
/// across re-reviews and be merged-or-deleted only when the PR closes: <see cref="ReviewBranchManager.CommitNotesAsync"/>
/// commits per review and always keeps the branch; <see cref="ReviewBranchManager.MergeToDefaultAsync"/>
/// lands it on the default branch and deletes it (PR merged); <see cref="ReviewBranchManager.DeleteBranchAsync"/>
/// deletes it without merging (PR abandoned), idempotently.
/// </summary>
public sealed class ReviewBranchManagerTests : LoggingTestBase
{
    private const string RepoRoot = "/work/reviewbot";
    private const string DefaultBranch = "main";
    private const string ReviewBranch = "review/widgets-42";

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
            new ReviewArtifactFile("PRs/widgets-42/review.md", "# Review"),
            new ReviewArtifactFile("KnowledgeBase/_toc.md", "# ToC"),
        ]);

    public ReviewBranchManagerTests(ITestOutputHelper output)
        : base(output)
    {
    }

    private ReviewBranchManager CreateManager(
        ISandboxCommandRunner runner,
        ISandboxFileSystem fileSystem
    ) =>
        new(
            new GitRunner(runner),
            fileSystem,
            LoggerFactory.CreateLogger<ReviewBranchManager>());

    [Fact]
    public async Task CommitNotes_creates_the_branch_from_default_when_it_does_not_exist_yet()
    {
        var runner = new FakeSandboxCommandRunner();
        // A brand-new review branch: the existence probe fails.
        runner.OnArgvContains(
            $"rev-parse --verify {ReviewBranch}",
            new SandboxCommandResult(1, string.Empty, "unknown revision"));
        runner.OnArgvContains($"rev-parse {ReviewBranch}", new SandboxCommandResult(0, "f00dcafef00dcafe\n", string.Empty));
        var fs = new FakeSandboxFileSystem();

        var result = await CreateManager(runner, fs).CommitNotesAsync(RepoRoot, Request, CancellationToken.None);

        result.Outcome.Should().Be(ReviewBotPublishOutcome.Pushed);
        result.ReviewBranch.Should().Be(ReviewBranch);
        result.PushedSha.Should().Be("f00dcafef00dcafe");

        var commands = runner.Commands.Select(c => string.Join(' ', c.Argv)).ToList();
        commands.Should().Contain(a => a.Contains($"checkout -B {ReviewBranch} {DefaultBranch}"));
        commands.Should().Contain(a => a.Contains("commit -m"));
        commands.Should().Contain(a => a.Contains($"push origin {ReviewBranch}"));

        // The branch is never checked out bare (i.e. reused), and never deleted, by CommitNotes.
        commands.Should().NotContain(a => a.Contains($"checkout {ReviewBranch}") && !a.Contains("-B"));
        commands.Should().NotContain(a => a.Contains($"branch -D {ReviewBranch}"));
        commands.Should().NotContain(a => a.Contains($"push origin --delete {ReviewBranch}"));

        // Both artifact sets were written into the checkout before the commit.
        fs.Files.Should().ContainKey($"{RepoRoot}/PRs/widgets-42/review.md");
        fs.Files.Should().ContainKey($"{RepoRoot}/KnowledgeBase/_toc.md");

        // Every git invocation carries the hardening flags.
        runner.Commands.Should().OnlyContain(c => c.Argv.Contains("core.hooksPath=/dev/null"));
    }

    [Fact]
    public async Task CommitNotes_reuses_an_existing_branch_instead_of_recreating_it_from_default()
    {
        var runner = new FakeSandboxCommandRunner();
        // The review branch already exists (a prior review committed onto it) — the probe succeeds.
        runner.OnArgvContains(
            $"rev-parse --verify {ReviewBranch}",
            new SandboxCommandResult(0, ReviewBranch, string.Empty));
        runner.OnArgvContains($"rev-parse {ReviewBranch}", new SandboxCommandResult(0, "deadbeef\n", string.Empty));
        var fs = new FakeSandboxFileSystem();

        var result = await CreateManager(runner, fs).CommitNotesAsync(RepoRoot, Request, CancellationToken.None);

        result.Outcome.Should().Be(ReviewBotPublishOutcome.Pushed);

        var commands = runner.Commands.Select(c => string.Join(' ', c.Argv)).ToList();
        // Reuses the branch as-is; never recreates it from the default.
        commands.Should().Contain(a => a.Contains($"checkout {ReviewBranch}") && !a.Contains("-B"));
        commands.Should().NotContain(a => a.Contains($"checkout -B {ReviewBranch}"));
    }

    [Fact]
    public async Task CommitNotes_rebases_and_retries_the_branch_push_when_it_is_rejected_then_succeeds()
    {
        var runner = new FakeSandboxCommandRunner();
        runner.OnArgvContains(
            $"rev-parse --verify {ReviewBranch}",
            new SandboxCommandResult(1, string.Empty, "unknown revision"));
        // Push is rejected twice (remote moved), then succeeds on the third attempt.
        runner.OnArgvContainsSequence(
            $"push origin {ReviewBranch}",
            new SandboxCommandResult(1, string.Empty, "rejected"),
            new SandboxCommandResult(1, string.Empty, "rejected"),
            new SandboxCommandResult(0, string.Empty, string.Empty));
        runner.OnArgvContains($"rev-parse {ReviewBranch}", new SandboxCommandResult(0, "deadbeef\n", string.Empty));
        var fs = new FakeSandboxFileSystem();

        var result = await CreateManager(runner, fs).CommitNotesAsync(RepoRoot, Request, CancellationToken.None);

        result.Outcome.Should().Be(ReviewBotPublishOutcome.Pushed);
        result.PushedSha.Should().Be("deadbeef");

        var commands = runner.Commands.Select(c => string.Join(' ', c.Argv)).ToList();
        commands.Count(a => a.Contains($"push origin {ReviewBranch}")).Should().Be(3);
        commands.Count(a => a.Contains($"pull --rebase origin {ReviewBranch}")).Should().Be(2);
    }

    [Fact]
    public async Task CommitNotes_aborts_the_rebase_and_stops_retrying_when_the_rebase_fails()
    {
        var runner = new FakeSandboxCommandRunner();
        runner.OnArgvContains(
            $"rev-parse --verify {ReviewBranch}",
            new SandboxCommandResult(1, string.Empty, "unknown revision"));
        // Push is rejected (remote moved) and the rebase onto the moved remote then fails (a conflict).
        runner.OnArgvContains(
            $"push origin {ReviewBranch}", new SandboxCommandResult(1, string.Empty, "rejected"));
        runner.OnArgvContains(
            $"pull --rebase origin {ReviewBranch}",
            new SandboxCommandResult(1, string.Empty, "CONFLICT: could not apply"));
        var fs = new FakeSandboxFileSystem();

        var result = await CreateManager(runner, fs).CommitNotesAsync(RepoRoot, Request, CancellationToken.None);

        // A conflicted rebase cannot make the push land: report the sync failure (keeping the branch),
        // abort the mid-rebase state, and stop retrying rather than pushing again into a doomed rebase.
        result.Outcome.Should().Be(ReviewBotPublishOutcome.GitSyncFailed);
        var commands = runner.Commands.Select(c => string.Join(' ', c.Argv)).ToList();
        commands.Count(a => a.Contains($"push origin {ReviewBranch}")).Should().Be(1);
        commands.Should().Contain(a => a.Contains("rebase --abort"));
    }

    [Fact]
    public async Task CommitNotes_keeps_the_branch_and_reports_GitSyncFailed_when_the_push_never_succeeds()
    {
        var runner = new FakeSandboxCommandRunner();
        runner.OnArgvContains($"push origin {ReviewBranch}", new SandboxCommandResult(1, string.Empty, "rejected"));
        var fs = new FakeSandboxFileSystem();

        var result = await CreateManager(runner, fs).CommitNotesAsync(RepoRoot, Request, CancellationToken.None);

        result.Outcome.Should().Be(ReviewBotPublishOutcome.GitSyncFailed);
        result.PushedSha.Should().BeNull();

        var commands = runner.Commands.Select(c => string.Join(' ', c.Argv)).ToList();
        commands.Should().NotContain(a => a.Contains($"branch -D {ReviewBranch}"));
        commands.Should().NotContain(a => a.Contains($"push origin --delete {ReviewBranch}"));
    }

    [Fact]
    public async Task MergeToDefault_fetches_then_merges_the_remote_tracking_ref_pushes_and_deletes_the_branch()
    {
        var runner = new FakeSandboxCommandRunner();
        var fs = new FakeSandboxFileSystem();

        var result = await CreateManager(runner, fs)
            .MergeToDefaultAsync(RepoRoot, ReviewBranch, DefaultBranch, CancellationToken.None);

        result.Should().BeTrue();

        var commands = runner.Commands.Select(c => string.Join(' ', c.Argv)).ToList();
        // A fresh sweeper clone has no LOCAL notes branch, so the branch must be fetched and merged via its
        // REMOTE-TRACKING ref: fetch first, then merge origin/<branch> — never the bare name (git will not
        // resolve `merge review/...` to origin/review/...).
        IndexOf(commands, "fetch origin")
            .Should()
            .BeLessThan(IndexOf(commands, $"merge --ff-only origin/{ReviewBranch}"));
        IndexOf(commands, $"checkout {DefaultBranch}")
            .Should()
            .BeLessThan(IndexOf(commands, $"merge --ff-only origin/{ReviewBranch}"));
        IndexOf(commands, $"merge --ff-only origin/{ReviewBranch}")
            .Should()
            .BeLessThan(IndexOf(commands, $"push origin {DefaultBranch}"));
        IndexOf(commands, $"push origin {DefaultBranch}")
            .Should()
            .BeLessThan(IndexOf(commands, $"branch -D {ReviewBranch}"));
        IndexOf(commands, $"branch -D {ReviewBranch}")
            .Should()
            .BeLessThan(IndexOf(commands, $"push origin --delete {ReviewBranch}"));

        // The old bug — merging the branch by its bare name — must not happen: no merge references the
        // notes branch except through its origin/ remote-tracking ref.
        commands.Should().NotContain(
            a => a.Contains("merge", StringComparison.Ordinal)
                && a.Contains(ReviewBranch, StringComparison.Ordinal)
                && !a.Contains($"origin/{ReviewBranch}", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MergeToDefault_falls_back_to_a_merge_commit_when_fast_forward_is_not_possible()
    {
        var runner = new FakeSandboxCommandRunner();
        runner.OnArgvContains(
            $"merge --ff-only origin/{ReviewBranch}",
            new SandboxCommandResult(1, string.Empty, "not possible to fast-forward"));
        var fs = new FakeSandboxFileSystem();

        var result = await CreateManager(runner, fs)
            .MergeToDefaultAsync(RepoRoot, ReviewBranch, DefaultBranch, CancellationToken.None);

        result.Should().BeTrue();

        var commands = runner.Commands.Select(c => string.Join(' ', c.Argv)).ToList();
        commands.Should().Contain(a => a.Contains($"merge --no-edit -X theirs origin/{ReviewBranch}"));
    }

    [Fact]
    public async Task MergeToDefault_aborts_any_conflicted_merge_and_resets_before_checking_out_the_default()
    {
        var runner = new FakeSandboxCommandRunner();
        var fs = new FakeSandboxFileSystem();

        var result = await CreateManager(runner, fs)
            .MergeToDefaultAsync(RepoRoot, ReviewBranch, DefaultBranch, CancellationToken.None);

        result.Should().BeTrue();

        var commands = runner.Commands.Select(c => string.Join(' ', c.Argv)).ToList();
        // Clean-on-entry: a PRIOR sweep's non-ff merge could have hit a conflict and left this SHARED retention
        // checkout with an unresolved index — after which every `git checkout` fails "you need to resolve your
        // current index first", wedging the sweep for this PR every cycle. Abort any in-progress merge and
        // hard-reset BEFORE checking out the default so the checkout can't inherit that state.
        IndexOf(commands, "merge --abort").Should().BeGreaterThanOrEqualTo(0);
        IndexOf(commands, "reset --hard").Should().BeGreaterThanOrEqualTo(0);
        IndexOf(commands, "merge --abort").Should().BeLessThan(IndexOf(commands, $"checkout {DefaultBranch}"));
        IndexOf(commands, "reset --hard").Should().BeLessThan(IndexOf(commands, $"checkout {DefaultBranch}"));
    }

    [Fact]
    public async Task MergeToDefault_is_a_noop_when_the_notes_branch_is_already_gone_from_origin()
    {
        var runner = new FakeSandboxCommandRunner();
        // A prior sweep already merged + deleted the branch, so it no longer resolves on origin.
        runner.OnArgvContains(
            $"rev-parse --verify origin/{ReviewBranch}",
            new SandboxCommandResult(1, string.Empty, "fatal: Needed a single revision"));
        var fs = new FakeSandboxFileSystem();

        var result = await CreateManager(runner, fs)
            .MergeToDefaultAsync(RepoRoot, ReviewBranch, DefaultBranch, CancellationToken.None);

        result.Should().BeTrue("an already-resolved branch is an idempotent no-op, not a failure");

        var commands = runner.Commands.Select(c => string.Join(' ', c.Argv)).ToList();
        commands.Should().Contain(a => a.Contains("fetch origin"));
        commands.Should().NotContain(a => a.Contains("merge"));
        commands.Should().NotContain(a => a.Contains($"push origin {DefaultBranch}"));
        commands.Should().NotContain(a => a.Contains($"branch -D {ReviewBranch}"));
    }

    [Fact]
    public async Task MergeToDefault_returns_false_and_keeps_the_branch_when_the_push_never_succeeds()
    {
        var runner = new FakeSandboxCommandRunner();
        runner.OnArgvContains($"push origin {DefaultBranch}", new SandboxCommandResult(1, string.Empty, "rejected"));
        var fs = new FakeSandboxFileSystem();

        var result = await CreateManager(runner, fs)
            .MergeToDefaultAsync(RepoRoot, ReviewBranch, DefaultBranch, CancellationToken.None);

        result.Should().BeFalse();

        var commands = runner.Commands.Select(c => string.Join(' ', c.Argv)).ToList();
        commands.Should().NotContain(a => a.Contains($"branch -D {ReviewBranch}"));
        commands.Should().NotContain(a => a.Contains($"push origin --delete {ReviewBranch}"));
    }

    [Fact]
    public async Task DeleteBranch_deletes_locally_and_remotely()
    {
        var runner = new FakeSandboxCommandRunner();
        var fs = new FakeSandboxFileSystem();

        await CreateManager(runner, fs).DeleteBranchAsync(RepoRoot, ReviewBranch, CancellationToken.None);

        var commands = runner.Commands.Select(c => string.Join(' ', c.Argv)).ToList();
        commands.Should().Contain(a => a.Contains($"branch -D {ReviewBranch}"));
        commands.Should().Contain(a => a.Contains($"push origin --delete {ReviewBranch}"));
    }

    [Fact]
    public async Task DeleteBranch_is_idempotent_when_the_branch_is_already_gone()
    {
        var runner = new FakeSandboxCommandRunner();
        runner.OnArgvContains($"branch -D {ReviewBranch}", new SandboxCommandResult(1, string.Empty, "branch not found"));
        runner.OnArgvContains(
            $"push origin --delete {ReviewBranch}",
            new SandboxCommandResult(1, string.Empty, "remote ref does not exist"));
        var fs = new FakeSandboxFileSystem();

        var act = () => CreateManager(runner, fs).DeleteBranchAsync(RepoRoot, ReviewBranch, CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    private static int IndexOf(List<string> commands, string contains) =>
        commands.FindIndex(c => c.Contains(contains, StringComparison.Ordinal));
}
