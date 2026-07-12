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

    [Fact]
    public async Task PrepareAsync_ClearsAStaleLockLeftInTheStoreByAPriorLease()
    {
        // The 2026-07-12 incident, at the prepare seam: a stale index.lock in the warm store's .git must be
        // cleared on entry so the prepare succeeds instead of wedging on "index.lock: File exists".
        var slot = CreateSlot();
        var staleLock = Path.Combine(slot.StorePath, ".git", "index.lock");
        File.WriteAllText(staleLock, string.Empty);
        var runner = new FakeSandboxCommandRunner();
        var preparer = new ReviewSlotPreparer(
            new GitRunner(runner), SeedGitmodules(slot.StorePath), "github", NullLoggerFactory.Instance);

        _ = await preparer.PrepareAsync(
            slot, CreateRun(), StoreUrl, SubmoduleRelPath, Branch, DefaultBranch, NotesRelPath, BuildPolicy(), CancellationToken.None);

        File.Exists(staleLock).Should().BeFalse("clean-on-entry clears the stale lock before the git steps");
    }

    [Fact]
    public async Task PrepareAsync_StoreWithoutGitDir_ThrowsSlotNeedsReclone()
    {
        var slot = CreateSlot(withGitDir: false);
        var preparer = new ReviewSlotPreparer(
            new GitRunner(new FakeSandboxCommandRunner()), SeedGitmodules(slot.StorePath), "github", NullLoggerFactory.Instance);

        var act = async () => await preparer.PrepareAsync(
            slot, CreateRun(), StoreUrl, SubmoduleRelPath, Branch, DefaultBranch, NotesRelPath, BuildPolicy(), CancellationToken.None);

        await act.Should().ThrowAsync<SlotNeedsRecloneException>("a structurally broken store must escalate to re-clone");
    }

    [Fact]
    public async Task PrepareAsync_ReviewedSubmoduleFailsToInit_ThrowsSlotCorrupt()
    {
        var slot = CreateSlot();
        var runner = new FakeSandboxCommandRunner();
        runner.OnArgvContains(
            "submodule update --init", new SandboxCommandResult(1, string.Empty, "fatal: clone of submodule failed"));
        var preparer = new ReviewSlotPreparer(
            new GitRunner(runner), SeedGitmodules(slot.StorePath), "github", NullLoggerFactory.Instance);

        var act = async () => await preparer.PrepareAsync(
            slot, CreateRun(), StoreUrl, SubmoduleRelPath, Branch, DefaultBranch, NotesRelPath, BuildPolicy(), CancellationToken.None);

        await act.Should().ThrowAsync<SlotCorruptException>("a half-inited reviewed submodule is slot corruption, not a silent proceed");
    }

    [Fact]
    public async Task PrepareAsync_CorruptStderrOnAGitStep_ThrowsSlotCorrupt()
    {
        var slot = CreateSlot();
        var run = CreateRun();
        var runner = new FakeSandboxCommandRunner();
        runner.OnArgvContains(
            $"checkout --force {run.HeadSha}",
            new SandboxCommandResult(128, string.Empty, "fatal: Unable to create '.git/index.lock': File exists."));
        var preparer = new ReviewSlotPreparer(
            new GitRunner(runner), SeedGitmodules(slot.StorePath), "github", NullLoggerFactory.Instance);

        var act = async () => await preparer.PrepareAsync(
            slot, run, StoreUrl, SubmoduleRelPath, Branch, DefaultBranch, NotesRelPath, BuildPolicy(), CancellationToken.None);

        await act.Should().ThrowAsync<SlotCorruptException>("a corrupt-classified git failure drives the re-clone ladder");
    }

    [Fact]
    public async Task PrepareAsync_TransientStderrOnAGitStep_ThrowsInvalidOperation_NotCorrupt()
    {
        var slot = CreateSlot();
        var run = CreateRun();
        var runner = new FakeSandboxCommandRunner();
        runner.OnArgvContains(
            $"fetch origin {run.BaseSha} {run.HeadSha}",
            new SandboxCommandResult(128, string.Empty, "fatal: unable to access 'https://x': Could not resolve host: github.com"));
        var preparer = new ReviewSlotPreparer(
            new GitRunner(runner), SeedGitmodules(slot.StorePath), "github", NullLoggerFactory.Instance);

        var act = async () => await preparer.PrepareAsync(
            slot, run, StoreUrl, SubmoduleRelPath, Branch, DefaultBranch, NotesRelPath, BuildPolicy(), CancellationToken.None);

        // A transient network fault is a normal retry (keep the warm store), NOT a re-clone trigger.
        // SlotCorruptException derives from Exception (not InvalidOperationException), so asserting the exact
        // InvalidOperationException type proves the failure was classified transient, not corrupt.
        await act.Should().ThrowExactlyAsync<InvalidOperationException>();
    }

    private ReviewSlot CreateSlot(bool withGitDir = true)
    {
        var hostPath = Path.Combine(_hostRoot, "slot-0");
        var slot = new ReviewSlot(0, hostPath, Path.Combine(hostPath, "store"), Path.Combine(hostPath, "scratch"));
        Directory.CreateDirectory(slot.StorePath);
        Directory.CreateDirectory(slot.ScratchPath);
        if (withGitDir)
        {
            // A real leased slot always has a cloned store; SlotHygiene.EnsureCleanAsync needs the .git dir.
            Directory.CreateDirectory(Path.Combine(slot.StorePath, ".git"));
        }

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
