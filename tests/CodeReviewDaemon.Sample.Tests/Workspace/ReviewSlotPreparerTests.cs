using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using CodeReviewDaemon.Sample.Workspace;
using CodeReviewDaemon.Sample.Workspace.Git;
using CodeReviewDaemon.Sample.Workspace.Sandbox;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeReviewDaemon.Sample.Tests.Workspace;

/// <summary>
/// Task 6 — <see cref="ReviewSlotPreparer"/> prepares a leased warm slot (Task 5) for one PR review:
/// fetch, an origin-aware branch resolve onto the PR's persistent notes branch (reusing it — and its
/// prior notes — when it already exists on <c>origin</c>, else branching fresh from the default
/// branch), advancing the reviewed submodule to the PR head, and wiping the ephemeral scratchpad. These
/// tests pin the exact git sequence (mirroring <c>DaemonReviewStageExecutor</c>'s
/// <c>InitAllowListedSubmodulesAsync</c>/<c>FetchAndCheckoutHeadAsync</c>) and the returned
/// <see cref="PreparedCheckout"/> paths.
/// </summary>
public sealed class ReviewSlotPreparerTests : IDisposable
{
    private const string StoreUrl = "https://github.com/achieveai/AchieveAiReviews.git";
    private const string SubmoduleRelPath = "repos/LmDotnetTools";
    private const string Branch = "review/github/achieveai-lmdotnettools/151";
    private const string DefaultBranch = "main";
    private const string NotesRelPath = "PRs/github/achieveai-lmdotnettools/151";

    private readonly string _hostRoot =
        Path.Combine(Path.GetTempPath(), "crd-prep-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_hostRoot, recursive: true);
        }
        catch
        {
            // Best-effort cleanup only; leaving a stray temp dir must never fail the test.
        }
    }

    [Fact]
    public async Task PrepareAsync_NewBranch_BranchesFromDefaultBranchAndAdvancesSubmodule()
    {
        var slot = CreateSlot();
        var runner = new FakeSandboxCommandRunner();
        runner.OnArgvContains(
            $"rev-parse --verify origin/{Branch}", new SandboxCommandResult(1, string.Empty, "fatal: unknown revision"));
        var fileSystem = SeedGitmodules(slot.StorePath);
        var preparer = new ReviewSlotPreparer(new GitRunner(runner), fileSystem, "github", NullLoggerFactory.Instance);
        var run = CreateRun();

        var result = await preparer.PrepareAsync(
            slot, run, StoreUrl, SubmoduleRelPath, Branch, DefaultBranch, NotesRelPath, BuildPolicy(), CancellationToken.None);

        var commands = runner.Commands.Select(c => string.Join(' ', c.Argv)).ToList();
        commands.Should().Contain(
            a => a.Contains($"checkout -B {Branch} {DefaultBranch}"),
            "a brand-new branch is cut from the default branch");
        commands.Should().NotContain(
            a => a.Contains($"checkout -B {Branch} origin/{Branch}"),
            "there is no prior origin branch to reuse");
        commands.Should().Contain(
            a => a.Contains("submodule update --init") && a.Contains(SubmoduleRelPath),
            "the reviewed submodule is initialized exactly like InitAllowListedSubmodulesAsync");

        var expectedTargetDir = $"{slot.StorePath}/{SubmoduleRelPath}";
        commands.Should().Contain(
            a => a.Contains($"-C {expectedTargetDir} fetch origin {run.BaseSha} {run.HeadSha}"),
            "the submodule fetches exactly the PR's base+head commits");
        commands.Should().Contain(
            a => a.Contains($"-C {expectedTargetDir} checkout --force {run.HeadSha}"),
            "the submodule working tree is advanced to the PR head");

        result.Branch.Should().Be(Branch);
    }

    [Fact]
    public async Task PrepareAsync_ExistingOriginBranch_ReusesItInsteadOfTheDefaultBranch()
    {
        var slot = CreateSlot();
        var runner = new FakeSandboxCommandRunner();
        runner.OnArgvContains(
            $"rev-parse --verify origin/{Branch}", new SandboxCommandResult(0, "abc123\n", string.Empty));
        var fileSystem = SeedGitmodules(slot.StorePath);
        var preparer = new ReviewSlotPreparer(new GitRunner(runner), fileSystem, "github", NullLoggerFactory.Instance);
        var run = CreateRun();

        _ = await preparer.PrepareAsync(
            slot, run, StoreUrl, SubmoduleRelPath, Branch, DefaultBranch, NotesRelPath, BuildPolicy(), CancellationToken.None);

        var commands = runner.Commands.Select(c => string.Join(' ', c.Argv)).ToList();
        commands.Should().Contain(
            a => a.Contains($"checkout -B {Branch} origin/{Branch}"),
            "the existing remote branch (and its prior notes) is reused");
        commands.Should().NotContain(
            a => a.Contains($"checkout -B {Branch} {DefaultBranch}"),
            "the default branch must not be used when the persistent branch already exists — this would wipe prior notes");
    }

    [Fact]
    public async Task PrepareAsync_WipesTheScratchDirectory()
    {
        var slot = CreateSlot();
        var markerFile = Path.Combine(slot.ScratchPath, "stale-from-prior-review.txt");
        File.WriteAllText(markerFile, "leftover");
        var runner = new FakeSandboxCommandRunner();
        var fileSystem = SeedGitmodules(slot.StorePath);
        var preparer = new ReviewSlotPreparer(new GitRunner(runner), fileSystem, "github", NullLoggerFactory.Instance);

        _ = await preparer.PrepareAsync(
            slot, CreateRun(), StoreUrl, SubmoduleRelPath, Branch, DefaultBranch, NotesRelPath, BuildPolicy(), CancellationToken.None);

        Directory.Exists(slot.ScratchPath).Should().BeTrue("the scratch dir is recreated, not merely left deleted");
        File.Exists(markerFile).Should().BeFalse("a stale file from a prior review must not survive the wipe");
        Directory.EnumerateFileSystemEntries(slot.ScratchPath).Should().BeEmpty("the wiped scratch dir starts empty");
    }

    [Fact]
    public async Task PrepareAsync_ReturnsThePosixJoinedPreparedCheckoutPaths()
    {
        var slot = CreateSlot();
        var runner = new FakeSandboxCommandRunner();
        var fileSystem = SeedGitmodules(slot.StorePath);
        var preparer = new ReviewSlotPreparer(new GitRunner(runner), fileSystem, "github", NullLoggerFactory.Instance);

        var result = await preparer.PrepareAsync(
            slot, CreateRun(), StoreUrl, SubmoduleRelPath, Branch, DefaultBranch, NotesRelPath, BuildPolicy(), CancellationToken.None);

        result.StoreRoot.Should().Be(slot.StorePath);
        result.TargetDir.Should().Be($"{slot.StorePath}/{SubmoduleRelPath}");
        result.NotesDir.Should().Be($"{slot.StorePath}/{NotesRelPath}");
        result.Branch.Should().Be(Branch);
    }

    private ReviewSlot CreateSlot()
    {
        var hostPath = Path.Combine(_hostRoot, "slot-0");
        var slot = new ReviewSlot(0, hostPath, Path.Combine(hostPath, "store"), Path.Combine(hostPath, "scratch"));
        Directory.CreateDirectory(slot.StorePath);
        Directory.CreateDirectory(slot.ScratchPath);
        return slot;
    }

    private static ReviewRun CreateRun() => new()
    {
        RepoId = 1,
        PrId = "151",
        HeadSha = "head-sha",
        BaseSha = "base-sha",
        TriggerWatermark = "wm-1",
        ReviewKind = "full",
        VariantId = "primary",
        Mode = "collect-only",
        Stage = ReviewStage.Discovered,
        WorkflowStatus = WorkflowStatus.Running,
        PrLifecycleState = PrLifecycleState.Open,
    };

    /// <summary>Allows fetching exactly the reviewed submodule declared below, mirroring
    /// <c>DaemonReviewStageExecutor.BuildStoreSubmoduleAllowList</c>'s per-run allow-list shape.</summary>
    private static OperationPolicy BuildPolicy() =>
        DaemonOperationPolicy.BuildForRun(
            new RepoIdentity { Provider = "github", OrgOrOwner = "achieveai", RepoName = "LmDotnetTools" },
            reviewBotRepoUrl: null,
            allowWriteOperations: false,
            allowedSubmodules: [new SubmoduleAllowRule("github.com", "/achieveai/LmDotnetTools")]);

    /// <summary>Seeds a <c>.gitmodules</c> at the store root declaring the reviewed submodule, so
    /// <see cref="ReviewSlotPreparer"/>'s reused <c>SubmoduleInitializer</c> logic inits it.</summary>
    private static FakeSandboxFileSystem SeedGitmodules(string storeRoot)
    {
        var fileSystem = new FakeSandboxFileSystem();
        fileSystem.Seed(
            $"{storeRoot}/.gitmodules",
            "[submodule \"LmDotnetTools\"]\n\tpath = repos/LmDotnetTools\n\turl = https://github.com/achieveai/LmDotnetTools.git\n");
        return fileSystem;
    }
}
