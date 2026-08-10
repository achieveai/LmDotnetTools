using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using CodeReviewDaemon.Sample.Workspace;
using CodeReviewDaemon.Sample.Workspace.Git;
using CodeReviewDaemon.Sample.Workspace.Sandbox;
using Microsoft.Extensions.Logging;
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

    /// <summary>
    /// What <c>git status --porcelain -b -z</c> actually prepends in the checkout these tests describe.
    /// The daemon pins the reviewed worktree to an exact commit, so it is DETACHED, and git names that
    /// <c>HEAD (no branch)</c> rather than a branch. Verified against git 2.53.0 rather than assumed: a clean
    /// detached checkout answers exactly <c>"## HEAD (no branch)\0"</c>.
    /// <para>
    /// Every status fixture below carries it because real git always would. Leaving it out made these
    /// fixtures describe an output git cannot produce — and once production started requiring the header,
    /// three of these tests stopped reaching the code they were written to exercise while a fourth kept
    /// passing for a reason that had nothing to do with its subject.
    /// </para>
    /// </summary>
    private const string DetachedHeader = "## HEAD (no branch)\0";

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
    public void SdkOwnershipMarker_LivesUnderGitMetadataSoHygieneCleanCannotDeleteIt()
    {
        ReviewSlotPreparer.SdkOwnershipMarkerFile.Should().StartWith(".git/");
    }

    [Fact]
    public async Task EnsureStoreAsync_SdkPreparer_ReclonesAnUnmarkedHostPreparedStoreAndWritesMarker()
    {
        var slot = CreateSlot();
        var runner = new FakeSandboxCommandRunner();
        var fileSystem = SeedGitmodules(slot.StorePath);
        var preparer = new ReviewSlotPreparer(
            new GitRunner(runner),
            fileSystem,
            "ado",
            NullLoggerFactory.Instance,
            requireSdkOwnershipMarker: true);

        await preparer.EnsureStoreAsync(slot.StorePath, StoreUrl, CancellationToken.None);

        runner.Commands.Select(c => string.Join(' ', c.Argv)).Should().Contain(
            command => command == $"rm -rf -- {slot.StorePath}",
            "an unmarked warm store may carry host-git line-ending and ownership state");
        runner.Commands.Select(c => string.Join(' ', c.Argv)).Should().Contain(
            command => command.Contains($"clone {StoreUrl} {slot.StorePath}", StringComparison.Ordinal));
        fileSystem.Files.Should().ContainKey($"{slot.StorePath}/{ReviewSlotPreparer.SdkOwnershipMarkerFile}");
    }

    [Fact]
    public async Task EnsureStoreAsync_SdkPreparer_ReusesAMarkedStore()
    {
        var slot = CreateSlot();
        var runner = new FakeSandboxCommandRunner();
        var fileSystem = SeedGitmodules(slot.StorePath)
            .Seed($"{slot.StorePath}/{ReviewSlotPreparer.SdkOwnershipMarkerFile}", "1\n");
        var preparer = new ReviewSlotPreparer(
            new GitRunner(runner),
            fileSystem,
            "ado",
            NullLoggerFactory.Instance,
            requireSdkOwnershipMarker: true);

        await preparer.EnsureStoreAsync(slot.StorePath, StoreUrl, CancellationToken.None);

        runner.Commands.Select(c => string.Join(' ', c.Argv)).Should().NotContain(
            command => command.StartsWith("rm -rf --", StringComparison.Ordinal));
        runner.Commands.Select(c => string.Join(' ', c.Argv)).Should().NotContain(
            command => command.Contains(" clone ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EnsureStoreAsync_SdkPreparer_ReusesAStoreWhoseMarkerIsTooLargeToRead()
    {
        // The marker read is bounded, and this is a PRESENCE check. Reading the ceiling as "unmarked" would
        // `rm -rf` and re-clone a store that was never unowned — the most expensive way possible to react to
        // a file we simply declined to load.
        var slot = CreateSlot();
        var runner = new FakeSandboxCommandRunner();
        var fileSystem = SeedGitmodules(slot.StorePath)
            .Seed(
                $"{slot.StorePath}/{ReviewSlotPreparer.SdkOwnershipMarkerFile}",
                new string('x', (int)SandboxReadLimits.RepositoryFileBytes + 1));
        var preparer = new ReviewSlotPreparer(
            new GitRunner(runner),
            fileSystem,
            "ado",
            NullLoggerFactory.Instance,
            requireSdkOwnershipMarker: true);

        await preparer.EnsureStoreAsync(slot.StorePath, StoreUrl, CancellationToken.None);

        runner.Commands.Select(c => string.Join(' ', c.Argv)).Should().NotContain(
            command => command.StartsWith("rm -rf --", StringComparison.Ordinal));
        runner.Commands.Select(c => string.Join(' ', c.Argv)).Should().NotContain(
            command => command.Contains(" clone ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RecloneStoreAsync_HostPreparer_DeletesTheStoreWithHostFilesystemApis()
    {
        // The host-backed pooled preparer owns a store on the DAEMON HOST, so removing a corrupt one through
        // the POSIX `rm -rf` the sandbox path uses is wrong twice over: `rm` does not exist on Windows, and a
        // git store is full of read-only pack/object files that a naive recursive delete refuses. Delete it
        // with the same host filesystem pattern the scratch wipe already uses (clear ReadOnly, then delete).
        var slot = CreateSlot();
        var packDir = Path.Combine(slot.StorePath, ".git", "objects", "pack");
        Directory.CreateDirectory(packDir);
        var readOnlyPack = Path.Combine(packDir, "pack-deadbeef.idx");
        File.WriteAllText(readOnlyPack, "idx");
        File.SetAttributes(readOnlyPack, FileAttributes.ReadOnly);
        var runner = new FakeSandboxCommandRunner();
        var preparer = new ReviewSlotPreparer(
            new GitRunner(runner), new HostFileSystem(), "github", NullLoggerFactory.Instance);

        await preparer.RecloneStoreAsync(slot.StorePath, StoreUrl, CancellationToken.None);

        Directory.Exists(slot.StorePath).Should().BeFalse(
            "the corrupt host store is removed outright so the clone below lands in a fresh directory");
        runner.Commands.Select(c => string.Join(' ', c.Argv)).Should().NotContain(
            command => command.StartsWith("rm -rf --", StringComparison.Ordinal),
            "host paths must not be passed to sandbox/POSIX command semantics");
        runner.Commands.Select(c => string.Join(' ', c.Argv)).Should().Contain(
            command => command.Contains($"clone {StoreUrl} {slot.StorePath}", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EnsureStoreAsync_HostPreparer_ClonesWithNoWorkingDirectory()
    {
        // `/workspace` is the CONTAINER mount root; it does not exist on the daemon host, and
        // HostGitCommandRunner refuses a command whose working directory is missing (exit 1) — so the very
        // first host-side store clone can never succeed while that sandbox path is pinned as the cwd. The
        // clone names an absolute target, so it needs no working directory at all.
        var slot = CreateSlot(withGitDir: false);
        var runner = new FakeSandboxCommandRunner();
        var preparer = new ReviewSlotPreparer(
            new GitRunner(runner), new HostFileSystem(), "github", NullLoggerFactory.Instance);

        await preparer.EnsureStoreAsync(slot.StorePath, StoreUrl, CancellationToken.None);

        var clone = runner.Commands.Should().ContainSingle(
            c => string.Join(' ', c.Argv).Contains($"clone {StoreUrl} {slot.StorePath}", StringComparison.Ordinal))
            .Subject;
        clone.WorkingDirectory.Should().BeNull(
            "the daemon host has no /workspace, so pinning it as the clone's cwd fails the first-use clone");
    }

    [Fact]
    public async Task EnsureStoreAsync_SandboxPreparer_ClonesFromTheContainerWorkspaceRoot()
    {
        // Non-regression for the host fix above: inside the sandbox `/workspace` IS a real, mounted directory
        // and stays the clone's working directory.
        var slot = CreateSlot(withGitDir: false);
        var runner = new FakeSandboxCommandRunner();
        var preparer = new ReviewSlotPreparer(
            new GitRunner(runner), new FakeSandboxFileSystem(), "github", NullLoggerFactory.Instance);

        await preparer.EnsureStoreAsync(slot.StorePath, StoreUrl, CancellationToken.None);

        var clone = runner.Commands.Should().ContainSingle(
            c => string.Join(' ', c.Argv).Contains($"clone {StoreUrl} {slot.StorePath}", StringComparison.Ordinal))
            .Subject;
        clone.WorkingDirectory.Should().Be("/workspace");
    }

    [Fact]
    public async Task PrepareAsync_HostPreparer_ClearsStaleStateWithoutPosixFind()
    {
        // Clean-on-entry on the HOST: the daemon host has no POSIX `find`, and hygiene ignores that command's
        // result — so a stale index.lock / MERGE_HEAD / rebase-merge left by a crashed prior lease silently
        // survives and the next prepare wedges on "index.lock: File exists". Model the host deterministically
        // (find fails) and require the stale state to be gone anyway.
        var slot = CreateSlot();
        var gitDir = Path.Combine(slot.StorePath, ".git");
        var staleLock = Path.Combine(gitDir, "index.lock");
        File.WriteAllText(staleLock, string.Empty);
        File.WriteAllText(Path.Combine(gitDir, "MERGE_HEAD"), "deadbeef");
        Directory.CreateDirectory(Path.Combine(gitDir, "rebase-merge"));
        File.WriteAllText(
            Path.Combine(slot.StorePath, ".gitmodules"),
            "[submodule \"LmDotnetTools\"]\n\tpath = repos/LmDotnetTools\n"
                + "\turl = https://github.com/achieveai/LmDotnetTools.git\n");
        var runner = new FakeSandboxCommandRunner();
        runner.OnArgvContains("find ", new SandboxCommandResult(1, string.Empty, "'find' is not recognized"));
        var preparer = new ReviewSlotPreparer(
            new GitRunner(runner), new HostFileSystem(), "github", NullLoggerFactory.Instance);

        _ = await preparer.PrepareAsync(
            slot, CreateRun(), StoreUrl, SubmoduleRelPath, Branch, DefaultBranch, NotesRelPath, BuildPolicy(), CancellationToken.None);

        File.Exists(staleLock).Should().BeFalse("clean-on-entry clears the stale lock with host filesystem APIs");
        File.Exists(Path.Combine(gitDir, "MERGE_HEAD")).Should().BeFalse();
        Directory.Exists(Path.Combine(gitDir, "rebase-merge")).Should().BeFalse();
        runner.Commands.Select(c => string.Join(' ', c.Argv)).Should().NotContain(
            command => command.StartsWith("find ", StringComparison.Ordinal),
            "host stale-state cleanup must not be routed through POSIX find");
    }

    [Fact]
    public async Task EnsureStoreAsync_HostPreparer_ReusesAnUnmarkedStore()
    {
        var slot = CreateSlot();
        var runner = new FakeSandboxCommandRunner();
        var preparer = new ReviewSlotPreparer(
            new GitRunner(runner), SeedGitmodules(slot.StorePath), "github", NullLoggerFactory.Instance);

        await preparer.EnsureStoreAsync(slot.StorePath, StoreUrl, CancellationToken.None);

        runner.Commands.Select(c => string.Join(' ', c.Argv)).Should().NotContain(
            command => command.StartsWith("rm -rf --", StringComparison.Ordinal));
        runner.Commands.Select(c => string.Join(' ', c.Argv)).Should().NotContain(
            command => command.Contains(" clone ", StringComparison.Ordinal));
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
            a => a.Contains($"checkout -B {Branch} origin/{DefaultBranch}"),
            "a brand-new branch is cut from the FETCHED default, not the local ref of the same name — "
                + "`git fetch` advances origin/main but never main, so on the live NOVA store the local ref "
                + "sat at the initial commit while origin/main moved 54 commits ahead of it");
        commands.Should().NotContain(
            a => a.Contains($"checkout -B {Branch} {DefaultBranch}") && !a.Contains($"origin/{DefaultBranch}"),
            "cutting from the never-advanced local ref is what gave every review an empty Knowledge Base");
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

    /// <summary>
    /// Reusing the persistent notes branch preserves the PR's own notes, but on its own it also freezes the
    /// Knowledge Base at whatever existed the day the branch was cut. The notes branch is created once per PR
    /// and kept for every later re-review, so a PR first seen before any extraction ran carries an empty
    /// <c>KnowledgeBase/</c> for the rest of its life — which is what the live daemon does today: the notes
    /// worktree sits on <c>review/nova-5504919</c> at the store's initial commit, holding a <c>.gitkeep</c>
    /// and an empty <c>_toc.md</c>, while <c>origin/main</c> carries eleven extracted lessons. The branch has
    /// to be brought forward, or prior knowledge can never reach a re-review.
    /// </summary>
    [Fact]
    public async Task PrepareAsync_ExistingOriginBranch_BringsItUpToDateWithTheFetchedDefault()
    {
        var slot = CreateSlot();
        var runner = new FakeSandboxCommandRunner();
        runner.OnArgvContains(
            $"rev-parse --verify origin/{Branch}", new SandboxCommandResult(0, "abc123\n", string.Empty));
        var fileSystem = SeedGitmodules(slot.StorePath);
        var preparer = new ReviewSlotPreparer(new GitRunner(runner), fileSystem, "github", NullLoggerFactory.Instance);

        _ = await preparer.PrepareAsync(
            slot, CreateRun(), StoreUrl, SubmoduleRelPath, Branch, DefaultBranch, NotesRelPath, BuildPolicy(), CancellationToken.None);

        var commands = runner.Commands.Select(c => string.Join(' ', c.Argv)).ToList();
        commands.Should().Contain(
            a => a.Contains("merge") && a.Contains($"origin/{DefaultBranch}"),
            "a reused notes branch must pick up everything merged to the default since it was cut — "
                + "otherwise the Knowledge Base it shows the reviewer is frozen at branch-creation time");
    }

    /// <summary>
    /// The bring-forward must never cost a review. A notes branch that conflicts with the default (the
    /// generated <c>_toc.md</c>/<c>_index.jsonl</c> are the plausible candidates, since both sides rewrite
    /// them wholesale) has to leave the worktree usable and the review running on slightly stale knowledge —
    /// the alternative is a half-merged index and a review that cannot start at all, which trades a
    /// degraded brief for no brief.
    /// </summary>
    [Fact]
    public async Task PrepareAsync_ExistingOriginBranch_SurvivesAConflictBringingItForward()
    {
        var slot = CreateSlot();
        var runner = new FakeSandboxCommandRunner();
        runner.OnArgvContains(
            $"rev-parse --verify origin/{Branch}", new SandboxCommandResult(0, "abc123\n", string.Empty));
        runner.OnArgvContains(
            $"merge --no-edit origin/{DefaultBranch}",
            new SandboxCommandResult(1, string.Empty, "CONFLICT (content): Merge conflict in KnowledgeBase/_toc.md"));
        var fileSystem = SeedGitmodules(slot.StorePath);
        var preparer = new ReviewSlotPreparer(new GitRunner(runner), fileSystem, "github", NullLoggerFactory.Instance);

        var act = async () => await preparer.PrepareAsync(
            slot, CreateRun(), StoreUrl, SubmoduleRelPath, Branch, DefaultBranch, NotesRelPath, BuildPolicy(), CancellationToken.None);

        await act.Should().NotThrowAsync("a stale Knowledge Base is a degraded review; a failed prepare is no review");

        var commands = runner.Commands.Select(c => string.Join(' ', c.Argv)).ToList();
        commands.Should().Contain(
            a => a.Contains("merge --abort"),
            "the failed merge must be unwound, or every later git step in this worktree fails on an "
                + "unresolved index and the slot stays poisoned for the next lease too");
    }

    [Fact]
    public async Task PrepareAsync_HostPreparer_WipesScratchWithHostFilesystemApis()
    {
        var slot = CreateSlot();
        var markerFile = Path.Combine(slot.ScratchPath, "stale-host-file.txt");
        File.WriteAllText(markerFile, "leftover");
        File.WriteAllText(
            Path.Combine(slot.StorePath, ".gitmodules"),
            "[submodule \"LmDotnetTools\"]\n\tpath = repos/LmDotnetTools\n"
                + "\turl = https://github.com/achieveai/LmDotnetTools.git\n");
        var runner = new FakeSandboxCommandRunner();
        var preparer = new ReviewSlotPreparer(
            new GitRunner(runner),
            new HostFileSystem(),
            "github",
            NullLoggerFactory.Instance);

        _ = await preparer.PrepareAsync(
            slot, CreateRun(), StoreUrl, SubmoduleRelPath, Branch, DefaultBranch, NotesRelPath, BuildPolicy(), CancellationToken.None);

        Directory.Exists(slot.ScratchPath).Should().BeTrue();
        Directory.EnumerateFileSystemEntries(slot.ScratchPath).Should().BeEmpty();
        runner.Commands.Select(c => string.Join(' ', c.Argv)).Should().NotContain(
            command => command.StartsWith("rm -rf --", StringComparison.Ordinal)
                || command.StartsWith("mkdir -p --", StringComparison.Ordinal),
            "host paths must not be passed to sandbox/POSIX command semantics");
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
            "submodule update --init",
            new SandboxCommandResult(1, string.Empty, "fatal: Unable to create '.git/modules/sub/index.lock': File exists."));
        var preparer = new ReviewSlotPreparer(
            new GitRunner(runner), SeedGitmodules(slot.StorePath), "github", NullLoggerFactory.Instance);

        var act = async () => await preparer.PrepareAsync(
            slot, CreateRun(), StoreUrl, SubmoduleRelPath, Branch, DefaultBranch, NotesRelPath, BuildPolicy(), CancellationToken.None);

        await act.Should().ThrowAsync<SlotCorruptException>("a corrupt reviewed-submodule init failure (a stuck lock) drives the reclone ladder");
    }

    [Fact]
    public async Task PrepareAsync_ReviewedSubmoduleUnknownInitFailure_DoesNotDriveReclone()
    {
        var slot = CreateSlot();
        var runner = new FakeSandboxCommandRunner();
        // An init failure whose stderr matches neither a corrupt nor a transient marker classifies as
        // GitFailureKind.Unknown, which GitFailureClassifier documents as "treated as transient". It must
        // therefore retry the warm store, NOT drive a destructive reclone (matching the store-checkout path,
        // which also reclones only on a definitely-Corrupt classification).
        runner.OnArgvContains(
            "submodule update --init", new SandboxCommandResult(1, string.Empty, "fatal: clone of submodule failed"));
        var preparer = new ReviewSlotPreparer(
            new GitRunner(runner), SeedGitmodules(slot.StorePath), "github", NullLoggerFactory.Instance);

        var act = async () => await preparer.PrepareAsync(
            slot, CreateRun(), StoreUrl, SubmoduleRelPath, Branch, DefaultBranch, NotesRelPath, BuildPolicy(), CancellationToken.None);

        // It still throws (no silent proceed on a half-inited submodule), but the SPECIFIC transient/unknown
        // exception — not the reclone-driving SlotCorruptException, and not some unrelated regression (e.g. an NRE).
        await act.Should().ThrowExactlyAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task PrepareAsync_NonCorruptHygieneRestoreFailure_DoesNotReclone()
    {
        var slot = CreateSlot();
        var runner = new FakeSandboxCommandRunner();
        // Clean-on-entry hygiene's submodule restore (`submodule update --recursive --no-fetch ...`) fails
        // NON-corruptly (a missing local object / deinit'd submodule). EnsureCleanAsync proceeds (Clean) — the
        // review re-establishes submodules with permitted fetches — so PrepareAsync must NOT throw
        // SlotNeedsRecloneException (which the executor turns into a destructive delete + RecloneStoreAsync).
        runner.OnArgvContains(
            "submodule update --recursive --no-fetch",
            new SandboxCommandResult(1, string.Empty, "fatal: Unable to checkout 'deadbeef' in submodule path 'repos/X'"));
        var preparer = new ReviewSlotPreparer(
            new GitRunner(runner), SeedGitmodules(slot.StorePath), "github", NullLoggerFactory.Instance);

        var thrown = await Record.ExceptionAsync(async () => await preparer.PrepareAsync(
            slot, CreateRun(), StoreUrl, SubmoduleRelPath, Branch, DefaultBranch, NotesRelPath, BuildPolicy(), CancellationToken.None));

        // PrepareAsync must complete (hygiene proceeds, then the rest of preparation runs) — NOT throw at all, and
        // in particular NOT the reclone-driving SlotNeedsRecloneException.
        thrown.Should().BeNull("a non-corrupt hygiene restore failure proceeds; preparation completes without a reclone");
    }

    [Fact]
    public async Task PrepareAsync_ReviewedSubmoduleTransientInitFailure_DoesNotDriveReclone()
    {
        var slot = CreateSlot();
        var runner = new FakeSandboxCommandRunner();
        runner.OnArgvContains(
            "submodule update --init",
            new SandboxCommandResult(
                1, string.Empty, "fatal: unable to access 'https://github.com/x': Could not resolve host: github.com"));
        var preparer = new ReviewSlotPreparer(
            new GitRunner(runner), SeedGitmodules(slot.StorePath), "github", NullLoggerFactory.Instance);

        var act = async () => await preparer.PrepareAsync(
            slot, CreateRun(), StoreUrl, SubmoduleRelPath, Branch, DefaultBranch, NotesRelPath, BuildPolicy(), CancellationToken.None);

        // A transient auth/network init failure must retry the warm store, NOT trigger a destructive reclone
        // (which cannot fix it and would loop) — so it throws the SPECIFIC transient exception, not the
        // reclone-driving SlotCorruptException (and not an unrelated regression).
        await act.Should().ThrowExactlyAsync<InvalidOperationException>();
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

    [Fact]
    public async Task PrepareAsync_SharedStore_AddsBothWorktreesWithRelativePathsOffTheOneClone()
    {
        // The whole point of the layout: ONE clone holds every object, and the slot gets two worktrees of it.
        // --relative-paths is what makes that survive the mount — the daemon creates the worktrees at host
        // paths and the agent opens them at /workspace/..., so absolute gitdir pointers (git's default) would
        // name directories that do not exist inside the container and every git call there would fail.
        var slot = CreateSharedSlot();
        var run = CreateRun();
        var runner = new FakeSandboxCommandRunner()
            .OnArgvContains(
                $"-C {slot.StorePath} rev-parse --is-inside-work-tree",
                new SandboxCommandResult(128, string.Empty, "fatal: not a git repository"))
            .OnArgvContains(
                $"-C {slot.TargetPath} rev-parse --is-inside-work-tree",
                new SandboxCommandResult(128, string.Empty, "fatal: not a git repository"))
            .OnArgvContains(
                $"rev-parse --verify origin/{Branch}",
                new SandboxCommandResult(1, string.Empty, "unknown revision"));
        var preparer = new ReviewSlotPreparer(
            new GitRunner(runner), SeedGitmodules(slot.SharedStorePath), "github", NullLoggerFactory.Instance);

        var prepared = await preparer.PrepareAsync(
            slot, run, StoreUrl, SubmoduleRelPath, Branch, DefaultBranch, NotesRelPath, BuildPolicy(),
            CancellationToken.None);

        var commands = runner.Commands.Select(c => string.Join(' ', c.Argv)).ToList();
        var submoduleRoot = $"{slot.SharedStorePath}/{SubmoduleRelPath}";
        commands.Should().Contain(
            command => command.Contains(
                $"-C {slot.SharedStorePath} worktree add --relative-paths --force {slot.StorePath} "
                    + $"-B {Branch} origin/{DefaultBranch}",
                StringComparison.Ordinal),
            "the notes worktree hangs off the STORE clone, on this PR's own notes branch — cut from the "
                + "FETCHED default, since `git fetch` never advances the local ref of the same name");
        commands.Should().Contain(
            command => command.Contains(
                $"-C {submoduleRoot} worktree add --relative-paths --force {slot.TargetPath} "
                    + $"--detach {run.HeadSha}",
                StringComparison.Ordinal),
            "the reviewed checkout hangs off the SUBMODULE's clone, detached at the PR head");
        prepared.StoreRoot.Should().Be(slot.StorePath);
        prepared.TargetDir.Should().Be(slot.TargetPath);
        prepared.NotesDir.Should().Be($"{slot.StorePath}/{NotesRelPath}");
        prepared.Branch.Should().Be(Branch);
    }

    [Fact]
    public async Task PrepareAsync_SharedStore_ParksTheSharedCloneOnTheDefaultBranchAndNeverOnTheNotesBranch()
    {
        // Git allows one branch in exactly one working tree. If the shared clone ever checked out a notes
        // branch, that PR's slot could not create its own worktree on it — and since every slot of the repo
        // shares this clone, one review would deny the branch to itself. Parking on the default branch is the
        // invariant that lets an arbitrary number of concurrent notes worktrees exist.
        var slot = CreateSharedSlot();
        var run = CreateRun();
        var runner = new FakeSandboxCommandRunner();
        var preparer = new ReviewSlotPreparer(
            new GitRunner(runner), SeedGitmodules(slot.SharedStorePath), "github", NullLoggerFactory.Instance);

        _ = await preparer.PrepareAsync(
            slot, run, StoreUrl, SubmoduleRelPath, Branch, DefaultBranch, NotesRelPath, BuildPolicy(),
            CancellationToken.None);

        var commands = runner.Commands.Select(c => string.Join(' ', c.Argv)).ToList();
        commands.Should().Contain(
            command => command.EndsWith(
                $"-C {slot.SharedStorePath} checkout --force {DefaultBranch}", StringComparison.Ordinal));
        commands.Should().NotContain(
            command => command.Contains($"-C {slot.SharedStorePath} checkout", StringComparison.Ordinal)
                && command.Contains(Branch, StringComparison.Ordinal),
            "a notes branch checked out here is a notes branch no slot can claim");
        commands.Should().Contain(
            command => command.EndsWith($"-C {slot.SharedStorePath} fetch origin", StringComparison.Ordinal),
            "one fetch serves every slot");
    }

    [Fact]
    public async Task PrepareAsync_SharedStore_RepositionsAnExistingWorktreeInsteadOfReAddingIt()
    {
        // Slots are warm and recycled. The default fake runner answers the is-inside-work-tree probe with
        // success, i.e. both worktrees already exist — `worktree add` would then fail on an occupied path, so
        // the reuse branch must checkout in place instead.
        var slot = CreateSharedSlot();
        var run = CreateRun();
        var runner = new FakeSandboxCommandRunner()
            .OnArgvContains(
                $"rev-parse --verify origin/{Branch}",
                new SandboxCommandResult(1, string.Empty, "unknown revision"));
        var preparer = new ReviewSlotPreparer(
            new GitRunner(runner), SeedGitmodules(slot.SharedStorePath), "github", NullLoggerFactory.Instance);

        _ = await preparer.PrepareAsync(
            slot, run, StoreUrl, SubmoduleRelPath, Branch, DefaultBranch, NotesRelPath, BuildPolicy(),
            CancellationToken.None);

        var commands = runner.Commands.Select(c => string.Join(' ', c.Argv)).ToList();
        commands.Should().Contain(
            command => command.EndsWith(
                $"-C {slot.StorePath} checkout --force -B {Branch} origin/{DefaultBranch}", StringComparison.Ordinal),
            "the `-B` is load-bearing, not decoration: `checkout <tree-ish> <path>...` is git's "
                + "restore-files-from-a-tree form, so `checkout --force <branch> <start>` takes both words as "
                + "PATHSPECS and dies with 'did not match any file(s) known to git'. Only the SECOND review "
                + "of a PR reaches this line — the first creates the worktree — so the bug hid behind a "
                + "passing first run");
        commands.Should().Contain(
            command => command.EndsWith(
                $"-C {slot.TargetPath} checkout --force --detach {run.HeadSha}", StringComparison.Ordinal));
        commands.Should().NotContain(
            command => command.Contains("worktree add", StringComparison.Ordinal),
            "adding a worktree at a path that already holds one fails outright");
        commands.Should().Contain(
            command => command.Contains("worktree prune", StringComparison.Ordinal),
            "pruning first is what lets a wiped slot be re-added at the same path");
    }

    /// <summary>
    /// The reviewed worktree is the ONE directory every review agent is told to read, and until this test it
    /// was the one directory nothing ever cleaned.
    /// <para>
    /// <c>slot.TargetPath</c> reaches git in exactly two places in the whole workspace layer: the
    /// <c>--detach {HeadSha}</c> positioning above, and the <see cref="PreparedCheckout.TargetDir"/> returned
    /// straight to the agent. <see cref="SlotHygiene"/> — which does the <c>reset --hard</c> +
    /// <c>clean -ffdx</c> — is only ever handed a STORE path, never this one, and under the worktree layout
    /// the reviewed checkout is a sibling of the store, not a directory inside it, so the store's clean does
    /// not reach it. <c>checkout --force</c> then restores every TRACKED file and deliberately leaves
    /// untracked ones alone, which is the whole gap: build output, generated code and agent byproduct from
    /// the previous PR reviewed in this slot survive into the next one and read as part of it. On the nova
    /// daemon that is 138 reviews through a single <c>slot-0/repo</c> with nothing ever removed.
    /// </para>
    /// </summary>
    [Fact]
    public async Task PrepareAsync_SharedStore_ClearsUntrackedLeftoversFromTheReviewedWorktree()
    {
        var slot = CreateSharedSlot();
        var run = CreateRun();
        var runner = new FakeSandboxCommandRunner()
            .OnArgvContains(
                $"rev-parse --verify origin/{Branch}",
                new SandboxCommandResult(1, string.Empty, "unknown revision"))
            .OnArgvContains(
                $"-C {slot.TargetPath} rev-parse HEAD",
                new SandboxCommandResult(0, run.HeadSha + "\n", string.Empty));
        var preparer = new ReviewSlotPreparer(
            new GitRunner(runner), SeedGitmodules(slot.SharedStorePath), "github", NullLoggerFactory.Instance);

        _ = await preparer.PrepareAsync(
            slot, run, StoreUrl, SubmoduleRelPath, Branch, DefaultBranch, NotesRelPath, BuildPolicy(),
            CancellationToken.None);

        var commands = runner.Commands.Select(c => string.Join(' ', c.Argv)).ToList();
        var cleaned = commands.FindIndex(
            c => c.EndsWith($"-C {slot.TargetPath} clean -ffdx", StringComparison.Ordinal));
        var positioned = commands.FindIndex(
            c => c.Contains($"-C {slot.TargetPath} checkout", StringComparison.Ordinal)
                && c.Contains("--detach", StringComparison.Ordinal));
        cleaned.Should().BeGreaterThan(
            -1,
            "the reviewed checkout is recycled across PRs and `checkout --force` leaves untracked files behind");
        cleaned.Should().BeGreaterThan(
            positioned,
            "cleaning has to happen AFTER the checkout lands, or it wipes the previous PR's tree and then "
                + "the checkout repopulates it alongside the same leftovers");
    }

    /// <summary>
    /// Reviewing the wrong commit is worse than not reviewing at all: every finding is attributed to a PR
    /// whose code the agent never saw, and nothing downstream can tell the difference. So the prepared tree's
    /// HEAD is asserted to BE the PR head before it is handed over, and a mismatch fails preparation rather
    /// than quietly proceeding — the stage then retries under the retry governor with no artifact persisted.
    /// </summary>
    [Fact]
    public async Task PrepareAsync_SharedStore_RefusesAReviewedWorktreeParkedOnTheWrongCommit()
    {
        var slot = CreateSharedSlot();
        var run = CreateRun();
        var runner = new FakeSandboxCommandRunner()
            .OnArgvContains(
                $"rev-parse --verify origin/{Branch}",
                new SandboxCommandResult(1, string.Empty, "unknown revision"))
            .OnArgvContains(
                $"-C {slot.TargetPath} rev-parse HEAD",
                new SandboxCommandResult(0, "some-other-sha\n", string.Empty));
        var preparer = new ReviewSlotPreparer(
            new GitRunner(runner), SeedGitmodules(slot.SharedStorePath), "github", NullLoggerFactory.Instance);

        var act = async () => await preparer.PrepareAsync(
            slot, run, StoreUrl, SubmoduleRelPath, Branch, DefaultBranch, NotesRelPath, BuildPolicy(),
            CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("some-other-sha").And.Contain(run.HeadSha);
    }

    /// <summary>
    /// A tree that is still dirty AFTER the clean is contamination the clean could not remove, so it must not
    /// be reviewed either. Submodule pointer state is excluded for the reason
    /// <see cref="SlotHygiene"/> excludes it on the store: a moved gitlink is the review's own to
    /// re-establish, and gating on it would fail every review of a repo that has submodules.
    /// </summary>
    [Fact]
    public async Task PrepareAsync_SharedStore_RefusesAReviewedWorktreeThatIsStillDirtyAfterTheClean()
    {
        var slot = CreateSharedSlot();
        var run = CreateRun();
        var runner = new FakeSandboxCommandRunner()
            .OnArgvContains(
                $"rev-parse --verify origin/{Branch}",
                new SandboxCommandResult(1, string.Empty, "unknown revision"))
            .OnArgvContains(
                $"-C {slot.TargetPath} rev-parse HEAD",
                new SandboxCommandResult(0, run.HeadSha + "\n", string.Empty))
            .OnArgvContains(
                $"-C {slot.TargetPath} status --porcelain",
                new SandboxCommandResult(0, DetachedHeader + " M src/Leftover.cs\0", string.Empty))
            // A genuine edit: the bytes on disk are not the blob the index records for the path.
            .OnArgvContains(
                "rev-parse :src/Leftover.cs",
                new SandboxCommandResult(0, "1111111111111111111111111111111111111111\n", string.Empty))
            .OnArgvContains(
                "hash-object --no-filters -- src/Leftover.cs",
                new SandboxCommandResult(0, "2222222222222222222222222222222222222222\n", string.Empty));
        var preparer = new ReviewSlotPreparer(
            new GitRunner(runner), SeedGitmodules(slot.SharedStorePath), "github", NullLoggerFactory.Instance);

        var act = async () => await preparer.PrepareAsync(
            slot, run, StoreUrl, SubmoduleRelPath, Branch, DefaultBranch, NotesRelPath, BuildPolicy(),
            CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("src/Leftover.cs");
        runner.Commands.Select(c => string.Join(' ', c.Argv)).Should().Contain(
            command => command.EndsWith(
                $"-C {slot.TargetPath} status --porcelain -b -z --ignore-submodules=all", StringComparison.Ordinal),
            "a moved gitlink is not leftover content, and gating on it would fail every submodule-bearing repo");
    }

    /// <summary>
    /// The production failure this check exists to survive. WeveNova declares
    /// <c>sources/dev/WeveNova/services/app/ServiceConfig.ini</c> as <c>text eol=crlf</c> while the blob
    /// committed for it already holds CRLF, so git's clean filter maps the worktree copy to LF, compares it
    /// against a CRLF blob, and reports all 91 of 91 lines modified on a checkout nothing has touched. No
    /// <c>checkout --force</c>, <c>reset --hard</c> or <c>clean -ffdx</c> can settle it — measured — so
    /// gating on <c>status</c> alone refused 100% of that repository's reviews, permanently.
    /// </summary>
    [Fact]
    public async Task PrepareAsync_SharedStore_AcceptsAPathWhoseBytesAreAlreadyTheBlobRecordedAtTheHead()
    {
        const string NormalizedPath = "sources/dev/WeveNova/services/app/ServiceConfig.ini";
        const string RecordedBlob = "4c38366b709654d4e876cb887e5ace15dd67bbeb";
        var slot = CreateSharedSlot();
        var run = CreateRun();
        var runner = new FakeSandboxCommandRunner()
            .OnArgvContains(
                $"rev-parse --verify origin/{Branch}",
                new SandboxCommandResult(1, string.Empty, "unknown revision"))
            .OnArgvContains(
                $"-C {slot.TargetPath} rev-parse HEAD",
                new SandboxCommandResult(0, run.HeadSha + "\n", string.Empty))
            .OnArgvContains(
                $"-C {slot.TargetPath} status --porcelain",
                new SandboxCommandResult(0, DetachedHeader + $" M {NormalizedPath}\0", string.Empty))
            .OnArgvContains(
                $"rev-parse :{NormalizedPath}",
                new SandboxCommandResult(0, RecordedBlob + "\n", string.Empty))
            .OnArgvContains(
                $"hash-object --no-filters -- {NormalizedPath}",
                new SandboxCommandResult(0, RecordedBlob + "\n", string.Empty));
        var preparer = new ReviewSlotPreparer(
            new GitRunner(runner), SeedGitmodules(slot.SharedStorePath), "github", NullLoggerFactory.Instance);

        var prepared = await preparer.PrepareAsync(
            slot, run, StoreUrl, SubmoduleRelPath, Branch, DefaultBranch, NotesRelPath, BuildPolicy(),
            CancellationToken.None);

        prepared.TargetDir.Should().Be(slot.TargetPath);
    }

    /// <summary>
    /// The tolerance is keyed on blob identity, not on the status code, so it cannot be widened into "ignore
    /// modified files". An untracked leftover is contamination whatever bytes it happens to hold, and its
    /// blob is never even consulted.
    /// </summary>
    [Fact]
    public async Task PrepareAsync_SharedStore_RefusesAnUntrackedLeftoverWithoutConsultingBlobIdentity()
    {
        var slot = CreateSharedSlot();
        var run = CreateRun();
        var runner = new FakeSandboxCommandRunner()
            .OnArgvContains(
                $"rev-parse --verify origin/{Branch}",
                new SandboxCommandResult(1, string.Empty, "unknown revision"))
            .OnArgvContains(
                $"-C {slot.TargetPath} rev-parse HEAD",
                new SandboxCommandResult(0, run.HeadSha + "\n", string.Empty))
            .OnArgvContains(
                $"-C {slot.TargetPath} status --porcelain",
                new SandboxCommandResult(0, DetachedHeader + "?? artifacts/agent-scratch.log\0", string.Empty));
        var preparer = new ReviewSlotPreparer(
            new GitRunner(runner), SeedGitmodules(slot.SharedStorePath), "github", NullLoggerFactory.Instance);

        var act = async () => await preparer.PrepareAsync(
            slot, run, StoreUrl, SubmoduleRelPath, Branch, DefaultBranch, NotesRelPath, BuildPolicy(),
            CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("artifacts/agent-scratch.log");
        runner.Commands.Select(c => string.Join(' ', c.Argv)).Should().NotContain(
            // `rev-parse :<path>` is the FIRST of the two probes, so asserting only on `hash-object` would
            // still pass while an untracked path was being classified — the index lookup fails for it and
            // short-circuits before the second probe is ever reached.
            command => command.Contains("hash-object", StringComparison.Ordinal)
                || command.Contains("rev-parse :", StringComparison.Ordinal),
            "blob identity is only ever asked about a tracked modification");
    }

    /// <summary>
    /// The normalization check fails CLOSED. A probe that cannot run leaves the path's identity unknown, and
    /// an unknown path stays a leftover — the opposite of the head probe's fail-open, because here the
    /// unanswered question is "is this the PR's content", not "which commit is this".
    /// </summary>
    [Fact]
    public async Task PrepareAsync_SharedStore_RefusesADirtyPathWhenTheBlobProbeCannotRun()
    {
        var slot = CreateSharedSlot();
        var run = CreateRun();
        var runner = new FakeSandboxCommandRunner()
            .OnArgvContains(
                $"rev-parse --verify origin/{Branch}",
                new SandboxCommandResult(1, string.Empty, "unknown revision"))
            .OnArgvContains(
                $"-C {slot.TargetPath} rev-parse HEAD",
                new SandboxCommandResult(0, run.HeadSha + "\n", string.Empty))
            .OnArgvContains(
                $"-C {slot.TargetPath} status --porcelain",
                new SandboxCommandResult(0, DetachedHeader + " M src/Unreadable.cs\0", string.Empty))
            .OnArgvContains(
                "rev-parse :src/Unreadable.cs",
                new SandboxCommandResult(128, string.Empty, "fatal: path does not exist in the index"));
        var preparer = new ReviewSlotPreparer(
            new GitRunner(runner), SeedGitmodules(slot.SharedStorePath), "github", NullLoggerFactory.Instance);

        var act = async () => await preparer.PrepareAsync(
            slot, run, StoreUrl, SubmoduleRelPath, Branch, DefaultBranch, NotesRelPath, BuildPolicy(),
            CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("src/Unreadable.cs");
    }

    /// <summary>
    /// Porcelain v1 QUOTES any path containing a space, a quote or a non-ASCII byte, and a quoted path would
    /// never match the index entry the blob lookup asks about — so the NUL-delimited form is parsed instead.
    /// </summary>
    [Fact]
    public void ParsePorcelainZ_ReadsPathsThatTheNewlineFormWouldHaveQuoted()
    {
        var entries = ReviewSlotPreparer.ParsePorcelainZ(" M src/My Documents/Ünïcode.cs\0?? build/out.log\0");

        entries.Should().HaveCount(2);
        entries[0].Should().Be((" M", "src/My Documents/Ünïcode.cs"));
        entries[1].Should().Be(("??", "build/out.log"));
    }

    /// <summary>
    /// A rename or copy record carries a SECOND path field. Read as an entry of its own it would be a path
    /// with no status code, and the two-character slice would eat its first characters — so it is consumed
    /// with the record it belongs to.
    /// </summary>
    [Fact]
    public void ParsePorcelainZ_ConsumesTheSecondPathFieldOfARenameWithItsRecord()
    {
        var entries = ReviewSlotPreparer.ParsePorcelainZ("R  src/New.cs\0src/Old.cs\0 M src/Edited.cs\0");

        entries.Should().HaveCount(2);
        entries[0].Should().Be(("R ", "src/New.cs"));
        entries[1].Should().Be((" M", "src/Edited.cs"));
    }

    /// <summary>A clean tree produces no entries — the empty tail after the final delimiter is not one.</summary>
    [Fact]
    public void ParsePorcelainZ_ReadsACleanTreeAsNoEntries()
    {
        ReviewSlotPreparer.ParsePorcelainZ(string.Empty).Should().BeEmpty();
        ReviewSlotPreparer.ParsePorcelainZ("\0").Should().BeEmpty();
    }

    /// <summary>
    /// THE TRAP IN ADDING <c>-b</c>. The branch header arrives as its own NUL-terminated field and is far
    /// longer than the four characters the short-field guard drops, so an unmodified parser reads it as code
    /// <c>##</c> at path <c>HEAD (no branch)</c>: a leftover that is not a file, on every run, in a checkout
    /// the daemon deliberately keeps detached. The probe meant to confirm cleanliness would then refuse every
    /// review instead. The exact string is what git 2.53.0 emits, not an approximation of it.
    /// </summary>
    [Fact]
    public void ParsePorcelainZ_DoesNotReadTheBranchHeaderAsALeftoverPath()
    {
        ReviewSlotPreparer.ParsePorcelainZ(DetachedHeader).Should().BeEmpty(
            "a clean detached checkout answers with the header alone, and that is not a dirty path");

        ReviewSlotPreparer.ParsePorcelainZ("## main...origin/main\0").Should().BeEmpty(
            "the attached form carries upstream tracking info and is just as much not a path");

        var entries = ReviewSlotPreparer.ParsePorcelainZ(DetachedHeader + " M src/Edited.cs\0?? build/out.log\0");

        entries.Should().HaveCount(2, "the header is skipped but everything after it still counts");
        entries[0].Should().Be((" M", "src/Edited.cs"));
        entries[1].Should().Be(("??", "build/out.log"));
    }

    /// <summary>
    /// The header skip is bounded to the leading field, so it cannot be widened by accident into "ignore any
    /// record that looks like a comment". A path is still a path wherever it sits and whatever it is called.
    /// </summary>
    [Fact]
    public void ParsePorcelainZ_StillReadsARecordWhosePathBeginsWithTheHeaderPrefix()
    {
        var entries = ReviewSlotPreparer.ParsePorcelainZ(DetachedHeader + "?? ## odd name.md\0");

        entries.Should().ContainSingle().Which.Should().Be(("??", "## odd name.md"));
    }

    /// <summary>
    /// A rename still consumes its second path field with <c>-b</c> in play — the header must not shift the
    /// pairing by one and turn the rename SOURCE into an entry of its own.
    /// </summary>
    [Fact]
    public void ParsePorcelainZ_ConsumesARenamesSecondFieldEvenBehindTheBranchHeader()
    {
        var entries = ReviewSlotPreparer.ParsePorcelainZ(DetachedHeader + "R  c.txt\0a.txt\0");

        entries.Should().ContainSingle().Which.Should().Be(("R ", "c.txt"));
    }

    /// <summary>
    /// The point of the whole change, stated as a property: a probe that RAN and found nothing must be
    /// distinguishable from a probe whose answer never arrived. Before <c>-b</c> both were the empty string.
    /// </summary>
    [Fact]
    public void ProbeReported_SeparatesACleanAnswerFromNoAnswerAtAll()
    {
        ReviewSlotPreparer.ProbeReported(DetachedHeader).Should().BeTrue(
            "a clean tree still answers, and the header is that answer");
        ReviewSlotPreparer.ProbeReported(DetachedHeader + " M src/Edited.cs\0").Should().BeTrue();

        ReviewSlotPreparer.ProbeReported(string.Empty).Should().BeFalse(
            "git cannot produce an empty answer for `status -b`, so this is lost output, not cleanliness");
        ReviewSlotPreparer.ProbeReported(" M src/Edited.cs\0").Should().BeFalse(
            "output that lost its header is truncated, and a truncated listing cannot be read as complete");
    }

    /// <summary>
    /// The production half of #87, end to end. A git that exits 0 having lost its output is not hypothetical
    /// here — this daemon measured one (run 200, <c>git rev-parse HEAD</c>, exit 0, no stdout, which no real
    /// git invocation produces). Under the old command that answer was the empty string, identical to a clean
    /// tree, so the probe concluded the checkout was verified clean and said so. A dirty tree reaching a
    /// reviewer as "verified" is the failure this pins: the run may continue on the independently verified
    /// head, but it must NOT claim a cleanliness it never observed.
    /// </summary>
    [Fact]
    public async Task PrepareAsync_SharedStore_DoesNotClaimACleanTreeWhenTheProbesOutputWasLost()
    {
        var slot = CreateSharedSlot();
        var run = CreateRun();
        var runner = new FakeSandboxCommandRunner()
            .OnArgvContains(
                $"rev-parse --verify origin/{Branch}",
                new SandboxCommandResult(1, string.Empty, "unknown revision"))
            .OnArgvContains(
                $"-C {slot.TargetPath} rev-parse HEAD",
                new SandboxCommandResult(0, run.HeadSha + "\n", string.Empty))
            // Exit 0, empty stdout: the shape a lost capture takes. Before `-b` this was ALSO the shape of a
            // clean tree, which is exactly why it went unnoticed.
            .OnArgvContains(
                $"-C {slot.TargetPath} status --porcelain",
                new SandboxCommandResult(0, string.Empty, string.Empty));
        using var logs = new CapturingLoggerFactory();
        var preparer = new ReviewSlotPreparer(
            new GitRunner(runner), SeedGitmodules(slot.SharedStorePath), "github", logs);

        var act = async () => await preparer.PrepareAsync(
            slot, run, StoreUrl, SubmoduleRelPath, Branch, DefaultBranch, NotesRelPath, BuildPolicy(),
            CancellationToken.None);

        await act.Should().NotThrowAsync(
            "the head was independently verified, so an unanswered probe is not grounds for refusing the run");

        logs.Capturing.MessagesAtLevel(LogLevel.Information).Should().NotContain(
            m => m.Contains("verified clean", StringComparison.Ordinal),
            "claiming verification from a probe that returned nothing is the defect itself");
        logs.Capturing.MessagesAtLevel(LogLevel.Warning).Should().Contain(
            m => m.Contains("returned no branch header", StringComparison.Ordinal),
            "and the run must say out loud that it could not tell");
    }

    /// <summary>
    /// The other direction, and the one that makes the test above mean something. A probe that genuinely ran
    /// against a genuinely clean tree DOES claim it — so "no clean claim" is evidence about the probe rather
    /// than a message this code never emits.
    /// </summary>
    [Fact]
    public async Task PrepareAsync_SharedStore_ClaimsACleanTreeWhenTheProbeActuallyAnswered()
    {
        var slot = CreateSharedSlot();
        var run = CreateRun();
        var runner = new FakeSandboxCommandRunner()
            .OnArgvContains(
                $"rev-parse --verify origin/{Branch}",
                new SandboxCommandResult(1, string.Empty, "unknown revision"))
            .OnArgvContains(
                $"-C {slot.TargetPath} rev-parse HEAD",
                new SandboxCommandResult(0, run.HeadSha + "\n", string.Empty))
            // A clean tree, as real git reports one under `-b`: the header and nothing else.
            .OnArgvContains(
                $"-C {slot.TargetPath} status --porcelain",
                new SandboxCommandResult(0, DetachedHeader, string.Empty));
        using var logs = new CapturingLoggerFactory();
        var preparer = new ReviewSlotPreparer(
            new GitRunner(runner), SeedGitmodules(slot.SharedStorePath), "github", logs);

        var act = async () => await preparer.PrepareAsync(
            slot, run, StoreUrl, SubmoduleRelPath, Branch, DefaultBranch, NotesRelPath, BuildPolicy(),
            CancellationToken.None);

        await act.Should().NotThrowAsync();
        logs.Capturing.MessagesAtLevel(LogLevel.Information).Should().Contain(
            m => m.Contains("verified clean", StringComparison.Ordinal),
            "a probe that answered 'nothing dirty' is exactly when the clean claim is earned");
    }

    /// <summary>
    /// The command carries <c>-b</c>. Pinned separately from the behaviour above because the behaviour is
    /// reachable from a fixture whatever the real command says — this is what ties the fix to the flag that
    /// makes it work against a real git.
    /// </summary>
    [Fact]
    public async Task PrepareAsync_SharedStore_AsksGitForTheBranchHeaderSoASuccessCannotBeSilent()
    {
        var slot = CreateSharedSlot();
        var run = CreateRun();
        var runner = new FakeSandboxCommandRunner()
            .OnArgvContains(
                $"rev-parse --verify origin/{Branch}",
                new SandboxCommandResult(1, string.Empty, "unknown revision"))
            .OnArgvContains(
                $"-C {slot.TargetPath} rev-parse HEAD",
                new SandboxCommandResult(0, run.HeadSha + "\n", string.Empty))
            .OnArgvContains(
                $"-C {slot.TargetPath} status --porcelain",
                new SandboxCommandResult(0, DetachedHeader, string.Empty));
        var preparer = new ReviewSlotPreparer(
            new GitRunner(runner), SeedGitmodules(slot.SharedStorePath), "github", NullLoggerFactory.Instance);

        await preparer.PrepareAsync(
            slot, run, StoreUrl, SubmoduleRelPath, Branch, DefaultBranch, NotesRelPath, BuildPolicy(),
            CancellationToken.None);

        runner.Commands.Select(c => string.Join(' ', c.Argv)).Should().Contain(
            command => command.EndsWith(
                $"-C {slot.TargetPath} status --porcelain -b -z --ignore-submodules=all",
                StringComparison.Ordinal),
            "without -b a clean tree answers with the empty string and a successful probe carries no evidence");
    }

    /// <summary>
    /// The verification is a safety net, not the mechanism. When the probe itself cannot run there is no
    /// invariant to check — only an unanswered question — and refusing to review on an unanswered question
    /// would turn a transient git hiccup into a review outage. It proceeds, loudly.
    /// </summary>
    [Fact]
    public async Task PrepareAsync_SharedStore_ProceedsWhenTheHeadProbeItselfCannotRun()
    {
        var slot = CreateSharedSlot();
        var run = CreateRun();
        var runner = new FakeSandboxCommandRunner()
            .OnArgvContains(
                $"rev-parse --verify origin/{Branch}",
                new SandboxCommandResult(1, string.Empty, "unknown revision"))
            .OnArgvContains(
                $"-C {slot.TargetPath} rev-parse HEAD",
                new SandboxCommandResult(128, string.Empty, "fatal: ambiguous argument 'HEAD'"));
        var preparer = new ReviewSlotPreparer(
            new GitRunner(runner), SeedGitmodules(slot.SharedStorePath), "github", NullLoggerFactory.Instance);

        var prepared = await preparer.PrepareAsync(
            slot, run, StoreUrl, SubmoduleRelPath, Branch, DefaultBranch, NotesRelPath, BuildPolicy(),
            CancellationToken.None);

        prepared.TargetDir.Should().Be(slot.TargetPath);
    }

    [Fact]
    public async Task PrepareAsync_SharedStore_PositionsAReusedWorktreeTheSameWayItCreatesAFreshOne()
    {
        // The create and reuse paths must speak ONE position vocabulary. They are reached by different runs of
        // the same PR against the same slot, so any divergence produces a checkout that silently disagrees
        // with the one the first run made — or, as it did, an outright failure on review #2 only.
        var run = CreateRun();
        var fresh = new FakeSandboxCommandRunner()
            .OnArgvContains(
                "rev-parse --is-inside-work-tree",
                new SandboxCommandResult(128, string.Empty, "fatal: not a git repository"))
            .OnArgvContains(
                $"rev-parse --verify origin/{Branch}",
                new SandboxCommandResult(1, string.Empty, "unknown revision"));
        var recycled = new FakeSandboxCommandRunner()
            .OnArgvContains(
                $"rev-parse --verify origin/{Branch}",
                new SandboxCommandResult(1, string.Empty, "unknown revision"));

        var positions = new List<(string Add, string Checkout)>();
        foreach (var (runner, isFresh) in new[] { (fresh, true), (recycled, false) })
        {
            var slot = CreateSharedSlot(isFresh ? 0 : 1);
            var preparer = new ReviewSlotPreparer(
                new GitRunner(runner),
                SeedGitmodules(slot.SharedStorePath),
                "github",
                NullLoggerFactory.Instance);
            _ = await preparer.PrepareAsync(
                slot, run, StoreUrl, SubmoduleRelPath, Branch, DefaultBranch, NotesRelPath, BuildPolicy(),
                CancellationToken.None);

            var commands = runner.Commands.Select(c => string.Join(' ', c.Argv)).ToList();
            var verb = isFresh
                ? $"worktree add --relative-paths --force {slot.StorePath} "
                : "checkout --force ";
            var line = commands.Single(
                c => c.Contains(verb, StringComparison.Ordinal)
                    && c.Contains(Branch, StringComparison.Ordinal));
            positions.Add((verb, line[(line.IndexOf(verb, StringComparison.Ordinal) + verb.Length)..]));
        }

        positions[0].Checkout.Should().Be(
            positions[1].Checkout,
            "`worktree add` and `checkout` read `-B <branch> <start>` and `--detach <commit>` identically, "
                + "which is the whole reason one argument list can serve both. If these ever differ, a "
                + "recycled slot lands somewhere its freshly-created twin would not");
    }

    [Fact]
    public async Task PrepareAsync_SharedStore_ReusesThePrsExistingNotesBranchFromOrigin()
    {
        // A PR's notes accumulate across reviews. When origin already carries the branch, the worktree must
        // start FROM it — branching from the default branch instead would silently drop every earlier round's
        // notes and the next review would repeat findings the previous one already filed.
        var slot = CreateSharedSlot();
        var run = CreateRun();
        var runner = new FakeSandboxCommandRunner()
            .OnArgvContains(
                $"-C {slot.StorePath} rev-parse --is-inside-work-tree",
                new SandboxCommandResult(128, string.Empty, "fatal: not a git repository"));
        var preparer = new ReviewSlotPreparer(
            new GitRunner(runner), SeedGitmodules(slot.SharedStorePath), "github", NullLoggerFactory.Instance);

        _ = await preparer.PrepareAsync(
            slot, run, StoreUrl, SubmoduleRelPath, Branch, DefaultBranch, NotesRelPath, BuildPolicy(),
            CancellationToken.None);

        runner.Commands.Select(c => string.Join(' ', c.Argv)).Should().Contain(
            command => command.Contains(
                $"-C {slot.SharedStorePath} worktree add --relative-paths --force {slot.StorePath} "
                    + $"-B {Branch} origin/{Branch}",
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task PrepareAsync_SharedStore_DetachesAStaleWorktreeStillHoldingTheNotesBranch()
    {
        // The failure this prevents: a previous run of the SAME PR left its worktree parked on the notes
        // branch. Git refuses to check one branch out twice, so this run's `worktree add -B` would fail —
        // and it would fail on the review's own prior state, i.e. every second review of a PR.
        var slot = CreateSharedSlot();
        var run = CreateRun();
        var stale = $"{slot.HostPath}/slot-7/notes";
        var runner = new FakeSandboxCommandRunner()
            .OnArgvContains(
                $"-C {slot.SharedStorePath} worktree list --porcelain",
                new SandboxCommandResult(
                    0,
                    $"worktree {slot.SharedStorePath}\nbranch refs/heads/{DefaultBranch}\n\n"
                        + $"worktree {stale}\nbranch refs/heads/{Branch}\n\n",
                    string.Empty))
            .OnArgvContains(
                $"-C {slot.StorePath} rev-parse --is-inside-work-tree",
                new SandboxCommandResult(128, string.Empty, "fatal: not a git repository"));
        var preparer = new ReviewSlotPreparer(
            new GitRunner(runner), SeedGitmodules(slot.SharedStorePath), "github", NullLoggerFactory.Instance);

        _ = await preparer.PrepareAsync(
            slot, run, StoreUrl, SubmoduleRelPath, Branch, DefaultBranch, NotesRelPath, BuildPolicy(),
            CancellationToken.None);

        runner.Commands.Select(c => string.Join(' ', c.Argv)).Should().Contain(
            command => command.Contains($"-C {stale} checkout --detach", StringComparison.Ordinal),
            "the branch is freed by detaching the OTHER worktree, not by deleting the notes it holds");
    }

    [Fact]
    public async Task PrepareAsync_SharedStore_DeepensAShallowCheckoutUntilBaseAndHeadShareAMergeBase()
    {
        // A store that declares `shallow = true` clones its submodule at depth 1, and that lone commit is a
        // GRAFT ROOT — git reports it as parentless. The clone follows the default branch and a PR TARGETS the
        // default branch, so that parentless commit is routinely the PR's own base. Every context path diffs
        // three-dot, which is defined via the merge base, so the run dies at ContextReady on `fatal: no merge
        // base`. Fetching the PR commits does not rescue it: that gives HEAD its history and leaves base a stub.
        //
        // The flag is `--depth`, absolute from the commit named, and not the `--deepen` that reads as the
        // better fit for walking a boundary back. Azure DevOps answers `--deepen` with `fatal: Server does not
        // support --deepen` — it advertises `shallow`, which is how the depth-1 clone got here, but not
        // `deepen-relative`. This assertion is what keeps a future tidy-up from "improving" it back.
        //
        // The counts model what a real store looks like at this point: base truncated a few commits in, head
        // carrying full history from the PR-commit fetch.
        var slot = CreateSharedSlot();
        var run = CreateRun();
        var target = $"{slot.SharedStorePath}/{SubmoduleRelPath}";
        var runner = new FakeSandboxCommandRunner()
            .OnArgvContainsSequence(
                $"-C {target} merge-base",
                new SandboxCommandResult(1, string.Empty, string.Empty),
                new SandboxCommandResult(0, "1a2b3c4d\n", string.Empty))
            .OnArgvContains(
                $"-C {target} rev-parse --is-shallow-repository",
                new SandboxCommandResult(0, "true\n", string.Empty))
            .OnArgvContains(
                $"-C {target} rev-list --count {run.BaseSha}",
                new SandboxCommandResult(0, "7\n", string.Empty))
            .OnArgvContains(
                $"-C {target} rev-list --count {run.HeadSha}",
                new SandboxCommandResult(0, "36063\n", string.Empty));
        var preparer = new ReviewSlotPreparer(
            new GitRunner(runner), SeedGitmodules(slot.SharedStorePath), "github", NullLoggerFactory.Instance);

        _ = await preparer.PrepareAsync(
            slot, run, StoreUrl, SubmoduleRelPath, Branch, DefaultBranch, NotesRelPath, BuildPolicy(),
            CancellationToken.None);

        var commands = runner.Commands.Select(c => string.Join(' ', c.Argv)).ToList();
        commands.Should().Contain(
            command => command.EndsWith(
                $"-C {target} fetch --depth=100 origin {run.BaseSha}", StringComparison.Ordinal),
            "re-fetching at a deeper depth walks the graft boundary back along commits whose trees the head "
                + "fetch already brought down, so what crosses the wire is commits and trees — not another "
                + "copy of a repo whose single checkout is a million objects");
        commands.Should().NotContain(
            command => command.Contains("--depth=1000", StringComparison.Ordinal),
            "the second step is for a long-lived branch; reaching for it after the first already succeeded "
                + "would pay for history no diff in this run needs");
    }

    [Fact]
    public async Task PrepareAsync_SharedStore_NeverNamesACommitDeeperThanTheDepthItIsAskingFor()
    {
        // `--depth` is documented to "deepen OR SHORTEN", and it shortens exactly the refs the fetch names.
        // Measured on a lab repo: a head holding 160 commits, named in a `--depth=100` fetch, came back
        // holding 100. Head here routinely carries tens of thousands of commits while base is the truncated
        // one, so naming both would slice away the very history the merge base is hiding in — silently, and
        // leaving the same `no merge base` failure with less to work with than before. Head is deep at both
        // steps, so it must never be named; base is shorter than both, so it is named at both.
        var slot = CreateSharedSlot();
        var run = CreateRun();
        var target = $"{slot.SharedStorePath}/{SubmoduleRelPath}";
        var runner = new FakeSandboxCommandRunner()
            .OnArgvContains($"-C {target} merge-base", new SandboxCommandResult(1, string.Empty, string.Empty))
            .OnArgvContains(
                $"-C {target} rev-parse --is-shallow-repository",
                new SandboxCommandResult(0, "true\n", string.Empty))
            .OnArgvContainsSequence(
                $"-C {target} rev-list --count {run.BaseSha}",
                new SandboxCommandResult(0, "7\n", string.Empty),
                new SandboxCommandResult(0, "100\n", string.Empty))
            .OnArgvContains(
                $"-C {target} rev-list --count {run.HeadSha}",
                new SandboxCommandResult(0, "36063\n", string.Empty));
        var preparer = new ReviewSlotPreparer(
            new GitRunner(runner), SeedGitmodules(slot.SharedStorePath), "github", NullLoggerFactory.Instance);

        _ = await preparer.PrepareAsync(
            slot, run, StoreUrl, SubmoduleRelPath, Branch, DefaultBranch, NotesRelPath, BuildPolicy(),
            CancellationToken.None);

        var deepening = runner.Commands.Select(c => string.Join(' ', c.Argv))
            .Where(command => command.Contains("--depth=", StringComparison.Ordinal))
            .Select(command => command[command.IndexOf($"-C {target}", StringComparison.Ordinal)..])
            .ToList();
        deepening.Should().BeEquivalentTo(
            [
                $"-C {target} fetch --depth=100 origin {run.BaseSha}",
                $"-C {target} fetch --depth=1000 origin {run.BaseSha}",
            ],
            "base is behind both steps so it is deepened by both, and head is ahead of both so naming it "
                + "could only cut it back");
    }

    [Fact]
    public async Task PrepareAsync_SharedStore_WithdrawsTheRelativeWorktreesExtensionFromEveryWorktreeOwner()
    {
        // `worktree add --relative-paths` sets extensions.relativeWorktrees, which bumps the repo to format 1.
        // A format-1 repo naming an extension git does not recognise is REFUSED, not degraded — and the
        // gateway sandbox runs git 2.39, which predates the 2.48 that introduced the flag. Measured live: the
        // review agent got `fatal: unknown repository extension found: relativeworktrees` from every git
        // command it ran against the reviewed checkout, on a host running git 2.53.
        //
        // The relative POINTERS are kept — they are the whole reason a worktree written at a host path works
        // when mounted at /workspace. Only the declaration is withdrawn, and resolving a relative `gitdir:`
        // against the directory holding the .git file is behaviour far older than the extension, so both git
        // versions still follow them.
        //
        // Both owners are asserted because they are different repos: the store owns the notes worktree and the
        // submodule owns the reviewed checkout. Stripping only the one that happened to fail first would leave
        // the agent able to read the code but not the notes it is supposed to write.
        var slot = CreateSharedSlot();
        var run = CreateRun();
        var target = $"{slot.SharedStorePath}/{SubmoduleRelPath}";
        var runner = new FakeSandboxCommandRunner();
        var preparer = new ReviewSlotPreparer(
            new GitRunner(runner), SeedGitmodules(slot.SharedStorePath), "github", NullLoggerFactory.Instance);

        _ = await preparer.PrepareAsync(
            slot, run, StoreUrl, SubmoduleRelPath, Branch, DefaultBranch, NotesRelPath, BuildPolicy(),
            CancellationToken.None);

        var commands = runner.Commands.Select(c => string.Join(' ', c.Argv)).ToList();
        foreach (var owner in new[] { slot.SharedStorePath, target })
        {
            commands.Should().Contain(
                command => command.EndsWith(
                    $"-C {owner} config --unset extensions.relativeWorktrees", StringComparison.Ordinal),
                $"'{owner}' owns a worktree, so it is the repo carrying the declaration the sandbox cannot read");
            commands.Should().Contain(
                command => command.EndsWith(
                    $"-C {owner} config core.repositoryformatversion 0", StringComparison.Ordinal),
                "format 1 exists to carry extensions; with the last one withdrawn it is what still turns the "
                    + "repo away");
        }
    }

    [Fact]
    public async Task PrepareAsync_SharedStore_LeavesTheRepositoryFormatAloneWhenAnotherExtensionRemains()
    {
        // Lowering the format is only safe because nothing is left to need it. An extension we did not write
        // means format 1 is genuinely required, and the realistic one — objectFormat = sha256 — names a repo
        // old git truly cannot read. Turning that honest refusal into a silent misread of the object store
        // would be a worse failure than the one being fixed here.
        var slot = CreateSharedSlot();
        var run = CreateRun();
        var target = $"{slot.SharedStorePath}/{SubmoduleRelPath}";
        var runner = new FakeSandboxCommandRunner()
            .OnArgvContains(
                $"-C {target} config --get-regexp",
                new SandboxCommandResult(0, "extensions.objectformat sha256\n", string.Empty));
        var preparer = new ReviewSlotPreparer(
            new GitRunner(runner), SeedGitmodules(slot.SharedStorePath), "github", NullLoggerFactory.Instance);

        _ = await preparer.PrepareAsync(
            slot, run, StoreUrl, SubmoduleRelPath, Branch, DefaultBranch, NotesRelPath, BuildPolicy(),
            CancellationToken.None);

        var commands = runner.Commands.Select(c => string.Join(' ', c.Argv)).ToList();
        commands.Should().Contain(
            command => command.EndsWith(
                $"-C {target} config --unset extensions.relativeWorktrees", StringComparison.Ordinal),
            "withdrawing our own declaration is always right; it is only the format that is conditional");
        commands.Should().NotContain(
            command => command.EndsWith(
                $"-C {target} config core.repositoryformatversion 0", StringComparison.Ordinal),
            "sha256 object format needs format 1, and claiming 0 would invite a git that cannot read it to try");
    }

    /// <summary>
    /// Run 147, live: a shallow store where the fixed two-step ladder ran to completion and was not enough.
    /// Probing that store read-only afterwards showed base truncated at the graft with 1054 commits reachable,
    /// head whole at 34,579, and <c>merge-base</c> still empty. So the deepening did not give up early — it
    /// ran out of steps, and the run died at ContextReady on <c>fatal: no merge base</c> having burned a
    /// nine-minute fetch first.
    /// <para>
    /// Raising the ladder to a bigger fixed number would only move the wall. The depth has to keep climbing
    /// while it is still buying history, which is what this pins: base is still short at 1054, so a deeper
    /// step must be attempted rather than the loop ending.
    /// </para>
    /// </summary>
    [Fact]
    public async Task PrepareAsync_SharedStore_KeepsDeepeningWhileEachStepStillBuysHistory()
    {
        var slot = CreateSharedSlot();
        var run = CreateRun();
        var target = $"{slot.SharedStorePath}/{SubmoduleRelPath}";
        var runner = new FakeSandboxCommandRunner()
            .OnArgvContains($"-C {target} merge-base", new SandboxCommandResult(1, string.Empty, string.Empty))
            .OnArgvContains(
                $"-C {target} rev-parse --is-shallow-repository",
                new SandboxCommandResult(0, "true\n", string.Empty))
            // Base grows as each deepening lands — 1 (graft root) → 100 → 1054, the live shape — and head is
            // already whole from the PR-commit fetch, so it is never a fetch target until the depth exceeds it.
            .OnArgvContainsSequence(
                $"-C {target} rev-list --count {run.BaseSha}",
                new SandboxCommandResult(0, "1\n", string.Empty),
                new SandboxCommandResult(0, "100\n", string.Empty),
                new SandboxCommandResult(0, "1054\n", string.Empty),
                new SandboxCommandResult(0, "9000\n", string.Empty))
            .OnArgvContains(
                $"-C {target} rev-list --count {run.HeadSha}",
                new SandboxCommandResult(0, "34579\n", string.Empty));
        var preparer = new ReviewSlotPreparer(
            new GitRunner(runner), SeedGitmodules(slot.SharedStorePath), "github", NullLoggerFactory.Instance);

        _ = await preparer.PrepareAsync(
            slot, run, StoreUrl, SubmoduleRelPath, Branch, DefaultBranch, NotesRelPath, BuildPolicy(),
            CancellationToken.None);

        runner.Commands.Select(c => string.Join(' ', c.Argv)).Should().Contain(
            command => command.Contains("--depth=10000", StringComparison.Ordinal),
            "base sat at 1054 commits and still had no merge base, so there was more history to buy — "
                + "stopping at the old 1000 ceiling is what killed run 147");
    }

    /// <summary>
    /// The pack leak, at its source. A <c>--depth</c> fetch re-asks from the TIP rather than from the current
    /// boundary, so every round of the climb brings the tip's whole tree closure down again instead of the
    /// boundary commits it actually lacks. Measured on the live NOVA submodule store: four packs of 7.2-7.7 GB
    /// holding 4,967,095 objects between them but only 1,034,930 distinct ones — the same object set roughly
    /// four times over, 30 GB of the store's 31, and nothing had ever run a repack against it.
    /// <para>
    /// The repack has to sit INSIDE the loop, not after it. Collapsing once at the end would still let four
    /// near-identical multi-gigabyte packs coexist first, and peak disk is the thing that filled the volume.
    /// </para>
    /// </summary>
    [Fact]
    public async Task PrepareAsync_SharedStore_RepacksAfterEveryDeepeningRoundRatherThanOnceAtTheEnd()
    {
        var slot = CreateSharedSlot();
        var run = CreateRun();
        var target = $"{slot.SharedStorePath}/{SubmoduleRelPath}";
        var runner = new FakeSandboxCommandRunner()
            .OnArgvContains($"-C {target} merge-base", new SandboxCommandResult(1, string.Empty, string.Empty))
            .OnArgvContains(
                $"-C {target} rev-parse --is-shallow-repository",
                new SandboxCommandResult(0, "true\n", string.Empty))
            .OnArgvContainsSequence(
                $"-C {target} rev-list --count {run.BaseSha}",
                new SandboxCommandResult(0, "1\n", string.Empty),
                new SandboxCommandResult(0, "100\n", string.Empty),
                new SandboxCommandResult(0, "1054\n", string.Empty),
                new SandboxCommandResult(0, "9000\n", string.Empty))
            .OnArgvContains(
                $"-C {target} rev-list --count {run.HeadSha}",
                new SandboxCommandResult(0, "34579\n", string.Empty));
        var preparer = MaintainingPreparer(runner, slot.SharedStorePath);

        _ = await preparer.PrepareAsync(
            slot, run, StoreUrl, SubmoduleRelPath, Branch, DefaultBranch, NotesRelPath, BuildPolicy(),
            CancellationToken.None);

        // Reduce the transcript to just the two verbs that matter, in order, so the assertion is about their
        // INTERLEAVING rather than about counts that could both be right and still leak.
        var sequence = runner.Commands
            .Select(c => string.Join(' ', c.Argv))
            .Where(command =>
                command.Contains($"-C {target} fetch --depth=", StringComparison.Ordinal)
                || command.EndsWith($"-C {target} repack -a -d --keep-unreachable", StringComparison.Ordinal))
            .Select(command => command.Contains("--depth=", StringComparison.Ordinal) ? "fetch" : "repack")
            .ToList();

        sequence.Should().Equal(
            ["fetch", "repack", "fetch", "repack", "fetch", "repack", "fetch", "repack"],
            "each deepening round lands a near-copy of the whole object store, so it has to be collapsed "
                + "before the next round lands another — four rounds ran here and four repacks answered them");
    }

    /// <summary>
    /// <c>--keep-unreachable</c> is load-bearing, and a plain <c>repack -a -d</c> here would break every
    /// review rather than merely leak. The PR's base and head arrive by raw SHA, so nothing but
    /// <c>FETCH_HEAD</c> points at them, and repack's reachability walk does not treat FETCH_HEAD as a root.
    /// Measured on a lab repo built to this shape: <c>repack -a -d</c> left the store at 144 KB with the base
    /// commit DROPPED — discarding the very deepening it had just been paid for — while the same repack with
    /// <c>--keep-unreachable</c> kept base and head, preserved the merge base and the shallow boundary, and
    /// still collapsed four packs into one for a 53% saving.
    /// </summary>
    [Fact]
    public async Task PrepareAsync_SharedStore_RepacksWithKeepUnreachableSoTheFetchedPrCommitsSurvive()
    {
        var slot = CreateSharedSlot();
        var run = CreateRun();
        var target = $"{slot.SharedStorePath}/{SubmoduleRelPath}";
        var runner = new FakeSandboxCommandRunner()
            .OnArgvContainsSequence(
                $"-C {target} merge-base",
                new SandboxCommandResult(1, string.Empty, string.Empty),
                new SandboxCommandResult(0, "1a2b3c4d\n", string.Empty))
            .OnArgvContains(
                $"-C {target} rev-parse --is-shallow-repository",
                new SandboxCommandResult(0, "true\n", string.Empty))
            .OnArgvContains(
                $"-C {target} rev-list --count {run.BaseSha}",
                new SandboxCommandResult(0, "1\n", string.Empty))
            .OnArgvContains(
                $"-C {target} rev-list --count {run.HeadSha}",
                new SandboxCommandResult(0, "36063\n", string.Empty));
        var preparer = MaintainingPreparer(runner, slot.SharedStorePath);

        _ = await preparer.PrepareAsync(
            slot, run, StoreUrl, SubmoduleRelPath, Branch, DefaultBranch, NotesRelPath, BuildPolicy(),
            CancellationToken.None);

        var repacks = runner.Commands
            .Select(c => string.Join(' ', c.Argv))
            .Where(command => command.Contains($"-C {target} repack", StringComparison.Ordinal))
            .ToList();

        repacks.Should().NotBeEmpty();
        repacks.Should().OnlyContain(
            command => command.EndsWith(
                $"-C {target} repack -a -d --keep-unreachable", StringComparison.Ordinal),
            "without --keep-unreachable this deletes the base commit the deepening just bought, and the "
                + "review then dies on the `fatal: no merge base` this whole path exists to prevent");
    }

    /// <summary>
    /// The acceptance guard on the fix: adding a repack between the rounds must not cost the climb its
    /// answer. Base sits on the shallow graft root with one commit reachable, head is whole from the
    /// PR-commit fetch, and the merge base appears once the first deepening lands — exactly the shape that
    /// used to die at ContextReady on <c>fatal: no merge base</c>.
    /// </summary>
    [Fact]
    public async Task PrepareAsync_SharedStore_StillResolvesTheMergeBaseFromAGraftRootWithTheRepackInTheLoop()
    {
        var slot = CreateSharedSlot();
        var run = CreateRun();
        var target = $"{slot.SharedStorePath}/{SubmoduleRelPath}";
        var runner = new FakeSandboxCommandRunner()
            .OnArgvContainsSequence(
                $"-C {target} merge-base",
                new SandboxCommandResult(1, string.Empty, string.Empty),
                new SandboxCommandResult(0, "1a2b3c4d\n", string.Empty))
            .OnArgvContains(
                $"-C {target} rev-parse --is-shallow-repository",
                new SandboxCommandResult(0, "true\n", string.Empty))
            .OnArgvContains(
                $"-C {target} rev-list --count {run.BaseSha}",
                new SandboxCommandResult(0, "1\n", string.Empty))
            .OnArgvContains(
                $"-C {target} rev-list --count {run.HeadSha}",
                new SandboxCommandResult(0, "36063\n", string.Empty));
        var preparer = MaintainingPreparer(runner, slot.SharedStorePath);

        var prepared = await preparer.PrepareAsync(
            slot, run, StoreUrl, SubmoduleRelPath, Branch, DefaultBranch, NotesRelPath, BuildPolicy(),
            CancellationToken.None);

        prepared.MergeBase.Should().Be(
            MergeBaseOutcome.Resolved,
            "the deepening still reaches the merge base for a base commit parked on a graft root — the "
                + "repack collapses packs, it does not take history away");
        runner.Commands.Select(c => string.Join(' ', c.Argv)).Should().Contain(
            command => command.EndsWith(
                $"-C {target} fetch --depth=100 origin {run.BaseSha}", StringComparison.Ordinal),
            "and it still deepens by naming only the truncated commit");
    }

    /// <summary>
    /// The routine drift the repack never sees. Deepening is the rare path; the common one leaves a single
    /// small pack per review behind (~33 MB on the live NOVA store), and 45 of those is what the store had
    /// accumulated. Git will collapse them on its own — but only once told, because its stock
    /// <c>gc.autoPackLimit</c> of 50 is why <c>gc --auto</c> had correctly reported nothing to do all along.
    /// </summary>
    [Fact]
    public async Task PrepareAsync_SharedStore_HandsTheObjectStoreToGitsOwnHousekeeping()
    {
        var slot = CreateSharedSlot();
        var run = CreateRun();
        var target = $"{slot.SharedStorePath}/{SubmoduleRelPath}";
        var runner = new FakeSandboxCommandRunner();
        var preparer = MaintainingPreparer(runner, slot.SharedStorePath);

        _ = await preparer.PrepareAsync(
            slot, run, StoreUrl, SubmoduleRelPath, Branch, DefaultBranch, NotesRelPath, BuildPolicy(),
            CancellationToken.None);

        var commands = runner.Commands.Select(c => string.Join(' ', c.Argv)).ToList();
        commands.Should().Contain(
            command => command.EndsWith($"-C {target} config gc.autoPackLimit 8", StringComparison.Ordinal),
            "git's default of 50 is why nothing ever fired on a store measured at 45 packs and 30 GB");
        commands.Should().Contain(
            command => command.EndsWith($"-C {target} config gc.autoDetach false", StringComparison.Ordinal),
            "a detached gc would be rewriting the pack directory while the next lease fetches into it, "
                + "outside the per-store lock this work is deliberately held inside");
        commands.Should().Contain(
            command => command.EndsWith($"-C {target} config gc.cruftPacks true", StringComparison.Ordinal),
            "before git 2.44 the default was to explode unreachable objects into LOOSE files, and 94% of the "
                + "live store is unreachable deepening spoil — on the sandbox's git 2.39 that default trades "
                + "a pack leak for a far worse inode one");

        var configured = commands.FindIndex(
            c => c.EndsWith($"-C {target} config gc.cruftPacks true", StringComparison.Ordinal));
        var collected = commands.FindIndex(
            c => c.EndsWith($"-C {target} gc --auto", StringComparison.Ordinal));
        collected.Should().BeGreaterThan(-1, "git is asked to collect once per prepare, not never");
        collected.Should().BeGreaterThan(
            configured, "gc must run under the settings, not under the defaults they replace");
    }

    /// <summary>
    /// The overwhelmingly common case must stay free. A repo whose merge base is already reachable never
    /// deepens, so it never lands a duplicate pack and must never pay to rewrite a multi-gigabyte object
    /// store — the routine <c>gc --auto</c> is the only housekeeping it gets, and that is a no-op until the
    /// pack count actually drifts.
    /// </summary>
    [Fact]
    public async Task PrepareAsync_SharedStore_DoesNotRepackWhenNoDeepeningWasNeeded()
    {
        var slot = CreateSharedSlot();
        var run = CreateRun();
        var runner = new FakeSandboxCommandRunner()
            .OnArgvContains("merge-base", new SandboxCommandResult(0, "1a2b3c4d\n", string.Empty));
        var preparer = MaintainingPreparer(runner, slot.SharedStorePath);

        _ = await preparer.PrepareAsync(
            slot, run, StoreUrl, SubmoduleRelPath, Branch, DefaultBranch, NotesRelPath, BuildPolicy(),
            CancellationToken.None);

        runner.Commands.Select(c => string.Join(' ', c.Argv)).Should().NotContain(
            command => command.Contains(" repack ", StringComparison.Ordinal),
            "no deepening means no duplicated pack, and an unconditional repack would make every review of "
                + "every healthy repo pay to rewrite the whole object store");
    }

    /// <summary>
    /// Housekeeping is best-effort by construction. A store that will not repack or will not take its gc
    /// config still reviews correctly — it just keeps growing — and turning a disk-hygiene failure into a
    /// failed review would trade a bounded cost for an unbounded one.
    /// </summary>
    [Fact]
    public async Task PrepareAsync_SharedStore_SurvivesAFailingRepack()
    {
        var slot = CreateSharedSlot();
        var run = CreateRun();
        var target = $"{slot.SharedStorePath}/{SubmoduleRelPath}";
        var runner = new FakeSandboxCommandRunner()
            .OnArgvContainsSequence(
                $"-C {target} merge-base",
                new SandboxCommandResult(1, string.Empty, string.Empty),
                new SandboxCommandResult(0, "1a2b3c4d\n", string.Empty))
            .OnArgvContains(
                $"-C {target} rev-parse --is-shallow-repository",
                new SandboxCommandResult(0, "true\n", string.Empty))
            .OnArgvContains(
                $"-C {target} rev-list --count {run.BaseSha}",
                new SandboxCommandResult(0, "1\n", string.Empty))
            .OnArgvContains(
                $"-C {target} rev-list --count {run.HeadSha}",
                new SandboxCommandResult(0, "36063\n", string.Empty))
            .OnArgvContains(
                $"-C {target} repack",
                new SandboxCommandResult(128, string.Empty, "fatal: unable to create pack file"))
            .OnArgvContains(
                $"-C {target} gc --auto",
                new SandboxCommandResult(128, string.Empty, "fatal: gc is already running"));
        var preparer = MaintainingPreparer(runner, slot.SharedStorePath);

        var prepared = await preparer.PrepareAsync(
            slot, run, StoreUrl, SubmoduleRelPath, Branch, DefaultBranch, NotesRelPath, BuildPolicy(),
            CancellationToken.None);

        prepared.MergeBase.Should().Be(
            MergeBaseOutcome.Resolved,
            "a failed repack costs disk, never the review the deepening had already succeeded in enabling");
    }

    /// <summary>
    /// The gate, pinned at its DEFAULT rather than at an explicit <c>false</c>. The owner of these machines
    /// instructed that local git packs not be touched, and every one of these commands rewrites an object
    /// store in place — so the preparer this test builds is the one every other call site builds: no flag
    /// passed, nothing opted into.
    /// <para>
    /// A default that silently flips is the failure mode that matters here, which is why this constructs the
    /// preparer the plain way instead of writing <c>enableObjectStoreMaintenance: false</c>. If someone
    /// later decides the maintenance is worth having on and changes the default, an explicit-false test
    /// would still pass and this one will not.
    /// </para>
    /// <para>
    /// The scenario is the deepening path — the one place that definitely repacks when enabled — so the
    /// silence below is the gate holding, not merely a case that never had work to do. The config writes are
    /// asserted absent too: they are what git consults when deciding to rewrite packs on its own, so writing
    /// them would change how the user's store behaves long after this process exits.
    /// </para>
    /// </summary>
    [Fact]
    public async Task PrepareAsync_SharedStore_ByDefaultTouchesNoPacks_NoRepackNoGcNoGcConfig()
    {
        var slot = CreateSharedSlot();
        var run = CreateRun();
        var target = $"{slot.SharedStorePath}/{SubmoduleRelPath}";
        var runner = new FakeSandboxCommandRunner()
            .OnArgvContains($"-C {target} merge-base", new SandboxCommandResult(1, string.Empty, string.Empty))
            .OnArgvContains(
                $"-C {target} rev-parse --is-shallow-repository",
                new SandboxCommandResult(0, "true\n", string.Empty))
            .OnArgvContainsSequence(
                $"-C {target} rev-list --count {run.BaseSha}",
                new SandboxCommandResult(0, "1\n", string.Empty),
                new SandboxCommandResult(0, "100\n", string.Empty),
                new SandboxCommandResult(0, "1054\n", string.Empty),
                new SandboxCommandResult(0, "9000\n", string.Empty))
            .OnArgvContains(
                $"-C {target} rev-list --count {run.HeadSha}",
                new SandboxCommandResult(0, "34579\n", string.Empty));

        // Constructed exactly as production and every other test constructs it — no maintenance argument.
        var preparer = new ReviewSlotPreparer(
            new GitRunner(runner), SeedGitmodules(slot.SharedStorePath), "github", NullLoggerFactory.Instance);

        _ = await preparer.PrepareAsync(
            slot, run, StoreUrl, SubmoduleRelPath, Branch, DefaultBranch, NotesRelPath, BuildPolicy(),
            CancellationToken.None);

        var commands = runner.Commands.Select(c => string.Join(' ', c.Argv)).ToList();

        commands.Should().Contain(
            command => command.Contains("--depth=", StringComparison.Ordinal),
            "the deepening still runs — this test is about what the daemon does to packs afterwards, and it "
                + "would prove nothing if the scenario never reached the repack in the first place");
        commands.Should().NotContain(
            command => command.Contains(" repack ", StringComparison.Ordinal),
            "rewriting the object store in place is exactly what was ruled out");
        commands.Should().NotContain(
            command => command.Contains(" gc ", StringComparison.Ordinal),
            "gc rewrites packs and prunes objects; it is not the harmless member of the set");
        commands.Should().NotContain(
            command => command.Contains(" config gc.", StringComparison.Ordinal),
            "the gc.* keys are what git consults when it decides to repack on its own, so writing them "
                + "changes the store's behaviour durably — leaving them unset keeps git's defaults, which is "
                + "the status quo being preserved");
    }

    /// <summary>
    /// A preparer with object-store maintenance turned ON. The daemon ships with it OFF — see
    /// <c>CodeReviewDaemonOptions.EnableObjectStoreMaintenance</c> — so every test that exercises the
    /// repack/gc behaviour has to opt in by hand, and the test that pins the default deliberately does not
    /// use this helper.
    /// </summary>
    private static ReviewSlotPreparer MaintainingPreparer(
        FakeSandboxCommandRunner runner, string storeRoot, ILoggerFactory? logs = null) =>
        new(
            new GitRunner(runner),
            SeedGitmodules(storeRoot),
            "github",
            logs ?? NullLoggerFactory.Instance,
            enableObjectStoreMaintenance: true);

    /// <summary>
    /// The other exit, and the one that must not be confused with running out of depth: a deepening fetch that
    /// extends NEITHER commit means both walks have reached real roots. The histories are exhausted rather than
    /// merely shallow, so no depth can ever produce a merge base — a force-push, a rewritten history, or an
    /// imported repository. Continuing to fetch here buys nothing and the operator needs to be told something
    /// entirely different from "widen the depth".
    /// </summary>
    [Fact]
    public async Task PrepareAsync_SharedStore_StopsWhenDeepeningNoLongerExtendsEitherHistory()
    {
        var slot = CreateSharedSlot();
        var run = CreateRun();
        using var logs = new CapturingLoggerFactory();
        var target = $"{slot.SharedStorePath}/{SubmoduleRelPath}";
        var runner = new FakeSandboxCommandRunner()
            .OnArgvContains($"-C {target} merge-base", new SandboxCommandResult(1, string.Empty, string.Empty))
            .OnArgvContains(
                $"-C {target} rev-parse --is-shallow-repository",
                new SandboxCommandResult(0, "true\n", string.Empty))
            // 1 → 100 → 100: the last fetch bought nothing, so base has hit its real root.
            .OnArgvContainsSequence(
                $"-C {target} rev-list --count {run.BaseSha}",
                new SandboxCommandResult(0, "1\n", string.Empty),
                new SandboxCommandResult(0, "100\n", string.Empty),
                new SandboxCommandResult(0, "100\n", string.Empty))
            .OnArgvContains(
                $"-C {target} rev-list --count {run.HeadSha}",
                new SandboxCommandResult(0, "34579\n", string.Empty));
        var preparer = new ReviewSlotPreparer(
            new GitRunner(runner), SeedGitmodules(slot.SharedStorePath), "github", logs);

        _ = await preparer.PrepareAsync(
            slot, run, StoreUrl, SubmoduleRelPath, Branch, DefaultBranch, NotesRelPath, BuildPolicy(),
            CancellationToken.None);

        logs.Capturing.MessagesAtLevel(LogLevel.Warning).Should().Contain(
            m => m.Contains("unrelated histories", StringComparison.Ordinal),
            "'deepen further' and 'these commits can never be compared' call for opposite operator actions, "
                + "and today both produce the same sentence");
        runner.Commands.Select(c => string.Join(' ', c.Argv)).Should().NotContain(
            command => command.Contains("--depth=10000", StringComparison.Ordinal),
            "once a fetch stops extending the history there is nothing deeper to buy");
    }

    /// <summary>
    /// The ceiling still bounds the volume. A monorepo quietly pulling full history is its own outage, and
    /// #28's idle timeout bounds a hang but not a download, so the number of rounds stays finite even when
    /// every step keeps buying history and the merge base never appears.
    /// </summary>
    [Fact]
    public async Task PrepareAsync_SharedStore_StopsAtTheDepthCeilingRatherThanFetchingForever()
    {
        var slot = CreateSharedSlot();
        var run = CreateRun();
        using var logs = new CapturingLoggerFactory();
        var target = $"{slot.SharedStorePath}/{SubmoduleRelPath}";
        var runner = new FakeSandboxCommandRunner()
            .OnArgvContains($"-C {target} merge-base", new SandboxCommandResult(1, string.Empty, string.Empty))
            .OnArgvContains(
                $"-C {target} rev-parse --is-shallow-repository",
                new SandboxCommandResult(0, "true\n", string.Empty))
            // Always growing, never resolving — the pathological case the ceiling exists for.
            .OnArgvContainsSequence(
                $"-C {target} rev-list --count {run.BaseSha}",
                new SandboxCommandResult(0, "1\n", string.Empty),
                new SandboxCommandResult(0, "150\n", string.Empty),
                new SandboxCommandResult(0, "1500\n", string.Empty),
                new SandboxCommandResult(0, "15000\n", string.Empty),
                new SandboxCommandResult(0, "150000\n", string.Empty))
            .OnArgvContains(
                $"-C {target} rev-list --count {run.HeadSha}",
                new SandboxCommandResult(0, "34579\n", string.Empty));
        var preparer = new ReviewSlotPreparer(
            new GitRunner(runner), SeedGitmodules(slot.SharedStorePath), "github", logs);

        _ = await preparer.PrepareAsync(
            slot, run, StoreUrl, SubmoduleRelPath, Branch, DefaultBranch, NotesRelPath, BuildPolicy(),
            CancellationToken.None);

        logs.Capturing.MessagesAtLevel(LogLevel.Warning).Should().Contain(
            m => m.Contains("depth ceiling", StringComparison.Ordinal),
            "hitting the ceiling is a different diagnosis from exhausted history and must read differently");
        runner.Commands
            .Select(c => string.Join(' ', c.Argv))
            .Count(command => command.Contains("--depth=", StringComparison.Ordinal))
            .Should().BeLessThanOrEqualTo(
                4, "the climb is bounded — four rounds from 100 to the ceiling, not an unbounded fetch loop");
    }

    /// <summary>
    /// The seam between deepening and the degraded verdict. Which of the give-ups happened is known here and
    /// nowhere else, and until now it died in a log line — so the diff site could only see
    /// <c>fatal: no merge base</c> and had to treat every cause identically.
    /// <para>
    /// The distinction is the whole point. <see cref="MergeBaseOutcome.UnrelatedHistories"/> is a property of
    /// the commit pair: both walks reached real roots, no depth can ever help, and no operator action fixes
    /// it — so it is safe to state as a verdict and let the run advance. Everything else is recoverable and
    /// must stay loud: a ceiling is a number we chose, and a failed fetch is a transient. Turning either into
    /// a posted verdict would present our own configuration limit to a PR author as a fact about their branch.
    /// </para>
    /// </summary>
    [Fact]
    public async Task PrepareAsync_SharedStore_ReportsUnrelatedHistoriesWhenDeepeningIsExhausted()
    {
        var slot = CreateSharedSlot();
        var run = CreateRun();
        var target = $"{slot.SharedStorePath}/{SubmoduleRelPath}";
        var runner = new FakeSandboxCommandRunner()
            .OnArgvContains($"-C {target} merge-base", new SandboxCommandResult(1, string.Empty, string.Empty))
            .OnArgvContains(
                $"-C {target} rev-parse --is-shallow-repository",
                new SandboxCommandResult(0, "true\n", string.Empty))
            .OnArgvContainsSequence(
                $"-C {target} rev-list --count {run.BaseSha}",
                new SandboxCommandResult(0, "1\n", string.Empty),
                new SandboxCommandResult(0, "100\n", string.Empty),
                new SandboxCommandResult(0, "100\n", string.Empty))
            .OnArgvContains(
                $"-C {target} rev-list --count {run.HeadSha}",
                new SandboxCommandResult(0, "34579\n", string.Empty));
        var preparer = new ReviewSlotPreparer(
            new GitRunner(runner), SeedGitmodules(slot.SharedStorePath), "github", NullLoggerFactory.Instance);

        var prepared = await preparer.PrepareAsync(
            slot, run, StoreUrl, SubmoduleRelPath, Branch, DefaultBranch, NotesRelPath, BuildPolicy(),
            CancellationToken.None);

        prepared.MergeBase.Should().Be(
            MergeBaseOutcome.UnrelatedHistories,
            "the diff site cannot tell a permanent 'these can never be compared' from a recoverable one "
                + "unless this carries it");
    }

    /// <summary>A full clone with no merge base reaches the same conclusion by a different road — the base is
    /// orphaned rather than truncated — and is equally permanent, so it reports the same outcome.</summary>
    [Fact]
    public async Task PrepareAsync_SharedStore_ReportsUnrelatedHistoriesForAFullCloneWithNoMergeBase()
    {
        var slot = CreateSharedSlot();
        var run = CreateRun();
        var runner = new FakeSandboxCommandRunner()
            .OnArgvContains("merge-base", new SandboxCommandResult(1, string.Empty, string.Empty))
            .OnArgvContains(
                "rev-parse --is-shallow-repository", new SandboxCommandResult(0, "false\n", string.Empty));
        var preparer = new ReviewSlotPreparer(
            new GitRunner(runner), SeedGitmodules(slot.SharedStorePath), "github", NullLoggerFactory.Instance);

        var prepared = await preparer.PrepareAsync(
            slot, run, StoreUrl, SubmoduleRelPath, Branch, DefaultBranch, NotesRelPath, BuildPolicy(),
            CancellationToken.None);

        prepared.MergeBase.Should().Be(MergeBaseOutcome.UnrelatedHistories);
    }

    /// <summary>
    /// The recoverable give-ups must NOT read as permanent. A ceiling is a number we chose and a failed fetch
    /// is a transient; either one reported as "unrelated histories" would post a verdict to a PR saying its
    /// commits can never be compared, when a one-line config change or a retry would have reviewed it.
    /// </summary>
    [Fact]
    public async Task PrepareAsync_SharedStore_ReportsTheCeilingAsRecoverableNotAsUnrelated()
    {
        var slot = CreateSharedSlot();
        var run = CreateRun();
        var target = $"{slot.SharedStorePath}/{SubmoduleRelPath}";
        var runner = new FakeSandboxCommandRunner()
            .OnArgvContains($"-C {target} merge-base", new SandboxCommandResult(1, string.Empty, string.Empty))
            .OnArgvContains(
                $"-C {target} rev-parse --is-shallow-repository",
                new SandboxCommandResult(0, "true\n", string.Empty))
            .OnArgvContainsSequence(
                $"-C {target} rev-list --count {run.BaseSha}",
                new SandboxCommandResult(0, "1\n", string.Empty),
                new SandboxCommandResult(0, "150\n", string.Empty),
                new SandboxCommandResult(0, "1500\n", string.Empty),
                new SandboxCommandResult(0, "15000\n", string.Empty),
                new SandboxCommandResult(0, "150000\n", string.Empty))
            .OnArgvContains(
                $"-C {target} rev-list --count {run.HeadSha}",
                new SandboxCommandResult(0, "34579\n", string.Empty));
        var preparer = new ReviewSlotPreparer(
            new GitRunner(runner), SeedGitmodules(slot.SharedStorePath), "github", NullLoggerFactory.Instance);

        var prepared = await preparer.PrepareAsync(
            slot, run, StoreUrl, SubmoduleRelPath, Branch, DefaultBranch, NotesRelPath, BuildPolicy(),
            CancellationToken.None);

        prepared.MergeBase.Should().Be(
            MergeBaseOutcome.DepthCeilingReached,
            "widening the ceiling would fix this, so it must stay loud and keep retrying");
    }

    [Fact]
    public async Task PrepareAsync_SharedStore_ReportsADeepeningFetchFailureAsItsOwnOutcome()
    {
        var slot = CreateSharedSlot();
        var run = CreateRun();
        var target = $"{slot.SharedStorePath}/{SubmoduleRelPath}";
        var runner = new FakeSandboxCommandRunner()
            .OnArgvContains($"-C {target} merge-base", new SandboxCommandResult(1, string.Empty, string.Empty))
            .OnArgvContains(
                $"-C {target} rev-parse --is-shallow-repository",
                new SandboxCommandResult(0, "true\n", string.Empty))
            .OnArgvContains(
                $"-C {target} rev-list --count {run.BaseSha}",
                new SandboxCommandResult(0, "1\n", string.Empty))
            .OnArgvContains(
                $"-C {target} rev-list --count {run.HeadSha}",
                new SandboxCommandResult(0, "34579\n", string.Empty))
            .OnArgvContains(
                $"-C {target} fetch --depth=",
                new SandboxCommandResult(128, string.Empty, "fatal: could not read from remote repository"));
        var preparer = new ReviewSlotPreparer(
            new GitRunner(runner), SeedGitmodules(slot.SharedStorePath), "github", NullLoggerFactory.Instance);

        var prepared = await preparer.PrepareAsync(
            slot, run, StoreUrl, SubmoduleRelPath, Branch, DefaultBranch, NotesRelPath, BuildPolicy(),
            CancellationToken.None);

        prepared.MergeBase.Should().Be(
            MergeBaseOutcome.DeepenFailed,
            "a network or auth failure says nothing about whether the commits are related");
    }

    [Fact]
    public async Task PrepareAsync_SharedStore_ReportsResolvedWhenTheMergeBaseIsReachable()
    {
        var slot = CreateSharedSlot();
        var run = CreateRun();
        var runner = new FakeSandboxCommandRunner()
            .OnArgvContains("merge-base", new SandboxCommandResult(0, "1a2b3c4d\n", string.Empty));
        var preparer = new ReviewSlotPreparer(
            new GitRunner(runner), SeedGitmodules(slot.SharedStorePath), "github", NullLoggerFactory.Instance);

        var prepared = await preparer.PrepareAsync(
            slot, run, StoreUrl, SubmoduleRelPath, Branch, DefaultBranch, NotesRelPath, BuildPolicy(),
            CancellationToken.None);

        prepared.MergeBase.Should().Be(MergeBaseOutcome.Resolved);
    }

    /// <summary>
    /// What <c>HostGitCommandRunner</c> returns for a command its watchdog killed on the idle timeout, and
    /// the stderr it writes with it. Named because the whole point of the tests below is that this arrives
    /// through the same channel as git's own answers and used to be indistinguishable from one.
    /// </summary>
    private const int WatchdogKillExit = 124;

    /// <summary>The kernel's exit code for a process killed by SIGKILL — an OOM killer, a container stop, an
    /// operator. Different cause, same obligation: it is not an answer about anyone's branch.</summary>
    private const int SigkillExit = 137;

    private const string KilledStderr =
        "git merge-base produced no output for 300s (idle timeout) and was killed by the daemon after 300.1s.";

    /// <summary>
    /// The runner shape the three tests below share: a full clone, healthy in every respect except what
    /// <c>merge-base</c> reports. Extracted so that the ONLY difference between those tests is the result
    /// handed to that one command — which is the whole of what separates a fact about the pull request from a
    /// fact about this daemon's own machine.
    /// </summary>
    private static FakeSandboxCommandRunner FullCloneWhoseMergeBaseAnswers(
        string target, SandboxCommandResult mergeBase) =>
        new FakeSandboxCommandRunner()
            .OnArgvContains($"-C {target} merge-base", mergeBase)
            .OnArgvContains(
                $"-C {target} rev-parse --is-shallow-repository",
                new SandboxCommandResult(0, "false\n", string.Empty));

    /// <summary>
    /// The control for every test that follows, and the one case that keeps its licence. <c>git merge-base</c>
    /// is documented to exit 1 with no output when the commits share no ancestor: that is git ANSWERING, not
    /// git failing, and on a clone that is not shallow it is as final as the question gets. So this run may —
    /// and must still — reach the author-facing claim.
    /// <para>
    /// Pinned separately from the outcome tests above because the discriminator moved. It used to be "the
    /// command did not succeed", which any non-zero exit satisfies; it is now the number 1 specifically, and
    /// nothing asserted that 1 still qualifies.
    /// </para>
    /// </summary>
    [Fact]
    public async Task PrepareAsync_SharedStore_MergeBaseExitOneIsAnAnswerAndStillEarnsTheUnrelatedClaim()
    {
        var slot = CreateSharedSlot();
        var run = CreateRun();
        using var logs = new CapturingLoggerFactory();
        var target = $"{slot.SharedStorePath}/{SubmoduleRelPath}";
        var runner = FullCloneWhoseMergeBaseAnswers(
            target, new SandboxCommandResult(1, string.Empty, string.Empty));
        var preparer = new ReviewSlotPreparer(
            new GitRunner(runner), SeedGitmodules(slot.SharedStorePath), "github", logs);

        var prepared = await preparer.PrepareAsync(
            slot, run, StoreUrl, SubmoduleRelPath, Branch, DefaultBranch, NotesRelPath, BuildPolicy(),
            CancellationToken.None);

        prepared.MergeBase.Should().Be(
            MergeBaseOutcome.UnrelatedHistories,
            "exit 1 from merge-base is git's documented 'no common ancestor', and a clone that reports itself "
                + "not shallow has no more history to find — this is the one shape that is genuinely a fact "
                + "about the pull request's commits");
        logs.Capturing.MessagesAtLevel(LogLevel.Warning).Should().Contain(
            m => m.Contains("is not shallow", StringComparison.Ordinal),
            "and it says so in the terms an operator can check, rather than as an indeterminate shrug");
    }

    /// <summary>
    /// The defect this whole three-state change exists for. <c>HostGitCommandRunner</c> kills a command that
    /// has been silent past its idle timeout and returns exit 124 — and that runner has already been observed
    /// killing healthy multi-gigabyte git operations, so this is the failure mode of this exact code path
    /// rather than a hypothetical one.
    /// <para>
    /// Read as a bool, 124 was indistinguishable from git's exit 1, and the run went on to conclude the
    /// commits share no ancestor and to say so in a comment on the pull request telling the author to
    /// re-target or rebase. Our timeout, delivered as a fact about their branch, with no hedge and nothing in
    /// the wording to suggest we had failed rather than they had.
    /// </para>
    /// </summary>
    [Fact]
    public async Task PrepareAsync_SharedStore_AWatchdogKilledMergeBaseIsIndeterminateNotUnrelatedHistories()
    {
        var slot = CreateSharedSlot();
        var run = CreateRun();
        var target = $"{slot.SharedStorePath}/{SubmoduleRelPath}";
        var runner = FullCloneWhoseMergeBaseAnswers(
            target, new SandboxCommandResult(WatchdogKillExit, string.Empty, KilledStderr));
        var preparer = new ReviewSlotPreparer(
            new GitRunner(runner), SeedGitmodules(slot.SharedStorePath), "github", NullLoggerFactory.Instance);

        var prepared = await preparer.PrepareAsync(
            slot, run, StoreUrl, SubmoduleRelPath, Branch, DefaultBranch, NotesRelPath, BuildPolicy(),
            CancellationToken.None);

        prepared.MergeBase.Should().Be(
            MergeBaseOutcome.Indeterminate,
            "the probe never answered, so nothing about these commits was established — and only "
                + "UnrelatedHistories is licensed to become author-facing text");
        prepared.MergeBase.Should().NotBe(
            MergeBaseOutcome.UnrelatedHistories,
            "stated separately because this is the assertion that matters: a killed command must never reach "
                + "the one outcome that tells a pull-request author to re-target or rebase their branch");
    }

    /// <summary>
    /// The same rule for a SIGKILL rather than the daemon's own watchdog — an OOM killer, a container stop, a
    /// stray <c>kill -9</c>. There is deliberately no separate code path for it; production reads every
    /// non-1 exit the same way, and this test exists so the rule is pinned as "only 1 is an answer" rather
    /// than as a list of exit codes someone remembered to enumerate.
    /// </summary>
    [Fact]
    public async Task PrepareAsync_SharedStore_ASigkilledMergeBaseIsIndeterminateNotUnrelatedHistories()
    {
        var slot = CreateSharedSlot();
        var run = CreateRun();
        var target = $"{slot.SharedStorePath}/{SubmoduleRelPath}";
        var runner = FullCloneWhoseMergeBaseAnswers(
            target, new SandboxCommandResult(SigkillExit, string.Empty, string.Empty));
        var preparer = new ReviewSlotPreparer(
            new GitRunner(runner), SeedGitmodules(slot.SharedStorePath), "github", NullLoggerFactory.Instance);

        var prepared = await preparer.PrepareAsync(
            slot, run, StoreUrl, SubmoduleRelPath, Branch, DefaultBranch, NotesRelPath, BuildPolicy(),
            CancellationToken.None);

        prepared.MergeBase.Should().Be(MergeBaseOutcome.Indeterminate);
    }

    /// <summary>
    /// The second of the three sites, and the one whose failure reads most like a success. The shallow probe
    /// used to be consumed as <c>!shallow.Succeeded || stdout != "true"</c> — one expression in which a
    /// command that was KILLED and a command that printed <c>false</c> are the same thing. A killed probe
    /// therefore arrived at the "full history already, and still unrelated" branch, which is exactly the
    /// branch that concludes the base commit was orphaned by the author's force-push.
    /// </summary>
    [Fact]
    public async Task PrepareAsync_SharedStore_AWatchdogKilledShallowProbeIsIndeterminateNotAConfirmedFullClone()
    {
        var slot = CreateSharedSlot();
        var run = CreateRun();
        var target = $"{slot.SharedStorePath}/{SubmoduleRelPath}";
        var runner = new FakeSandboxCommandRunner()
            .OnArgvContains($"-C {target} merge-base", new SandboxCommandResult(1, string.Empty, string.Empty))
            .OnArgvContains(
                $"-C {target} rev-parse --is-shallow-repository",
                new SandboxCommandResult(WatchdogKillExit, string.Empty, KilledStderr));
        var preparer = new ReviewSlotPreparer(
            new GitRunner(runner), SeedGitmodules(slot.SharedStorePath), "github", NullLoggerFactory.Instance);

        var prepared = await preparer.PrepareAsync(
            slot, run, StoreUrl, SubmoduleRelPath, Branch, DefaultBranch, NotesRelPath, BuildPolicy(),
            CancellationToken.None);

        prepared.MergeBase.Should().Be(
            MergeBaseOutcome.Indeterminate,
            "whether the checkout is shallow was never established, and 'we could not ask' is not "
                + "'we asked and it is a full clone'");
        prepared.MergeBase.Should().NotBe(
            MergeBaseOutcome.UnrelatedHistories,
            "merge-base genuinely said there is no ancestor here — but that only becomes permanent once the "
                + "clone is KNOWN to hold all the history there is, and a killed probe knows nothing");
    }

    /// <summary>A SIGKILL at the same site, for the same reason as the merge-base pair: the rule is "true or
    /// false, and nothing else", not an enumeration of the exit codes we happened to think of.</summary>
    [Fact]
    public async Task PrepareAsync_SharedStore_ASigkilledShallowProbeIsIndeterminateNotAConfirmedFullClone()
    {
        var slot = CreateSharedSlot();
        var run = CreateRun();
        var target = $"{slot.SharedStorePath}/{SubmoduleRelPath}";
        var runner = new FakeSandboxCommandRunner()
            .OnArgvContains($"-C {target} merge-base", new SandboxCommandResult(1, string.Empty, string.Empty))
            .OnArgvContains(
                $"-C {target} rev-parse --is-shallow-repository",
                new SandboxCommandResult(SigkillExit, string.Empty, string.Empty));
        var preparer = new ReviewSlotPreparer(
            new GitRunner(runner), SeedGitmodules(slot.SharedStorePath), "github", NullLoggerFactory.Instance);

        var prepared = await preparer.PrepareAsync(
            slot, run, StoreUrl, SubmoduleRelPath, Branch, DefaultBranch, NotesRelPath, BuildPolicy(),
            CancellationToken.None);

        prepared.MergeBase.Should().Be(MergeBaseOutcome.Indeterminate);
    }

    /// <summary>
    /// The runner shape for the third site: a shallow clone whose base sits on the graft root, whose head is
    /// whole, and whose <c>rev-list --count</c> of BASE follows <paramref name="baseCounts"/> round by round.
    /// The climb's give-up test is "did this fetch extend either commit", and that test is computed entirely
    /// from these numbers — so scripting them IS scripting the decision.
    /// </summary>
    private static FakeSandboxCommandRunner ShallowCloneCountingBaseAs(
        string target, string baseSha, string headSha, params SandboxCommandResult[] baseCounts) =>
        new FakeSandboxCommandRunner()
            .OnArgvContains($"-C {target} merge-base", new SandboxCommandResult(1, string.Empty, string.Empty))
            .OnArgvContains(
                $"-C {target} rev-parse --is-shallow-repository",
                new SandboxCommandResult(0, "true\n", string.Empty))
            .OnArgvContainsSequence($"-C {target} rev-list --count {baseSha}", baseCounts)
            .OnArgvContains(
                $"-C {target} rev-list --count {headSha}",
                new SandboxCommandResult(0, "34579\n", string.Empty));

    /// <summary>
    /// The third site, and the least obvious of the three because the corrupted value never leaves the
    /// method it is computed in. <c>ReachableCountAsync</c> reported 0 for a count it could not take, the
    /// climb compared that 0 against the previous round's reading, found it was not larger, and concluded the
    /// fetch had bought no history — which is the loop's definition of "both walks reached real roots" and
    /// its only route to UnrelatedHistories.
    /// <para>
    /// So a <c>rev-list</c> killed by the watchdog produced a permanent verdict about a pull request without
    /// any git command ever having reported anything about it. Zero is not a small number here; it is the
    /// absence of a number, and the two have opposite meanings for the comparison that consumes it.
    /// </para>
    /// </summary>
    [Fact]
    public async Task PrepareAsync_SharedStore_AWatchdogKilledReachableCountIsIndeterminateNotAnExhaustedHistory()
    {
        var slot = CreateSharedSlot();
        var run = CreateRun();
        var target = $"{slot.SharedStorePath}/{SubmoduleRelPath}";
        // Round 1 measures base at 1 and the fetch goes out; round 2's count is killed, so whether that fetch
        // bought anything is unknown — the same rounds as the exhaustion test above, with one probe lost.
        var runner = ShallowCloneCountingBaseAs(
            target,
            run.BaseSha,
            run.HeadSha,
            new SandboxCommandResult(0, "1\n", string.Empty),
            new SandboxCommandResult(WatchdogKillExit, string.Empty, KilledStderr));
        var preparer = new ReviewSlotPreparer(
            new GitRunner(runner), SeedGitmodules(slot.SharedStorePath), "github", NullLoggerFactory.Instance);

        var prepared = await preparer.PrepareAsync(
            slot, run, StoreUrl, SubmoduleRelPath, Branch, DefaultBranch, NotesRelPath, BuildPolicy(),
            CancellationToken.None);

        prepared.MergeBase.Should().Be(
            MergeBaseOutcome.Indeterminate,
            "an unmeasured round did not observe a flat history, it observed nothing");
        prepared.MergeBase.Should().NotBe(
            MergeBaseOutcome.UnrelatedHistories,
            "reading a lost count as 'this fetch bought no history' is how a killed rev-list becomes a "
                + "permanent statement about someone else's commits");
    }

    /// <summary>A SIGKILL at the counting site. Same rule, second exit code — production has one path for
    /// both, and this pins that the rule is about the absence of an answer rather than about 124.</summary>
    [Fact]
    public async Task PrepareAsync_SharedStore_ASigkilledReachableCountIsIndeterminateNotAnExhaustedHistory()
    {
        var slot = CreateSharedSlot();
        var run = CreateRun();
        var target = $"{slot.SharedStorePath}/{SubmoduleRelPath}";
        var runner = ShallowCloneCountingBaseAs(
            target,
            run.BaseSha,
            run.HeadSha,
            new SandboxCommandResult(0, "1\n", string.Empty),
            new SandboxCommandResult(SigkillExit, string.Empty, string.Empty));
        var preparer = new ReviewSlotPreparer(
            new GitRunner(runner), SeedGitmodules(slot.SharedStorePath), "github", NullLoggerFactory.Instance);

        var prepared = await preparer.PrepareAsync(
            slot, run, StoreUrl, SubmoduleRelPath, Branch, DefaultBranch, NotesRelPath, BuildPolicy(),
            CancellationToken.None);

        prepared.MergeBase.Should().Be(MergeBaseOutcome.Indeterminate);
    }

    /// <summary>
    /// The outcome enum is not the deliverable — the sentence is. Both give-ups below stop the climb, and an
    /// operator reading the log has to be able to tell "the pull request's commits are unrelated" from "our
    /// own probe was killed", because those call for opposite actions: the first is answered by re-targeting
    /// the PR, the second by looking at this daemon's box.
    /// <para>
    /// Asserted as a difference between two runs rather than as two independent substring checks, because the
    /// failure being guarded against is the two messages CONVERGING. A pair of tests that each look for their
    /// own phrase both keep passing when one message is quietly reworded into the other's wording, as long as
    /// each phrase survives somewhere in the merged text.
    /// </para>
    /// <para>
    /// The two fixtures differ in exactly one respect: round two's <c>rev-list --count</c> of base returns
    /// <c>100</c> in the first and is killed in the second. Every other command, count and round is identical,
    /// so any difference in what is logged is caused by that and by nothing else.
    /// </para>
    /// </summary>
    [Fact]
    public async Task PrepareAsync_SharedStore_SaysSomethingDifferentWhenTheCountWasKilledThanWhenTheHistoryIsExhausted()
    {
        var exhausted = await GiveUpWarningAsync(new SandboxCommandResult(0, "100\n", string.Empty));
        var killed = await GiveUpWarningAsync(
            new SandboxCommandResult(WatchdogKillExit, string.Empty, KilledStderr));

        exhausted.Should().Contain(
            "unrelated histories",
            "the measured run reached real roots on both walks, which is a fact about the commits and is "
                + "allowed to be stated as one");
        killed.Should().NotContain(
            "unrelated histories",
            "the killed run established nothing about the commits, and this phrase is the one that ends up "
                + "in a pull request telling the author to re-target or rebase");
        killed.Should().NotBe(
            exhausted,
            "if our infrastructure failure and the author's unrelated branch read the same, the distinction "
                + "exists only in an enum nobody reads");
        killed.Should().Contain(
            "UNKNOWN", "and the line has to name what it could not establish, not merely omit the claim");
    }

    /// <summary>
    /// Runs the climb to its give-up and returns the single warning that explains why it stopped. Round one
    /// measures base at 1 and fetches; <paramref name="secondBaseCount"/> is what round two's count of base
    /// comes back as, and head is flat throughout — so round two is where the loop decides, and that result
    /// is the only thing the caller varies.
    /// </summary>
    private async Task<string> GiveUpWarningAsync(SandboxCommandResult secondBaseCount)
    {
        var slot = CreateSharedSlot();
        var run = CreateRun();
        using var logs = new CapturingLoggerFactory();
        var target = $"{slot.SharedStorePath}/{SubmoduleRelPath}";
        var runner = ShallowCloneCountingBaseAs(
            target, run.BaseSha, run.HeadSha, new SandboxCommandResult(0, "1\n", string.Empty), secondBaseCount);
        var preparer = new ReviewSlotPreparer(
            new GitRunner(runner), SeedGitmodules(slot.SharedStorePath), "github", logs);

        _ = await preparer.PrepareAsync(
            slot, run, StoreUrl, SubmoduleRelPath, Branch, DefaultBranch, NotesRelPath, BuildPolicy(),
            CancellationToken.None);

        // "deepening" is the word both give-up lines share and no other warning on this path uses, so it
        // selects the line under test without presuming which of the two was written.
        return logs.Capturing.MessagesAtLevel(LogLevel.Warning)
            .Where(m => m.Contains("deepening", StringComparison.Ordinal))
            .Should().ContainSingle(
                "the climb stops once and says why once; two lines here would mean the assertion below is "
                    + "comparing an arbitrary one of them")
            .Subject;
    }

    [Fact]
    public async Task PrepareAsync_SharedStore_DoesNotDeepenWhenTheMergeBaseIsAlreadyReachable()
    {
        // The overwhelmingly common case — a full clone, or a shallow one deep enough already. Deepening here
        // would be a network round trip per review that buys nothing.
        var slot = CreateSharedSlot();
        var run = CreateRun();
        var runner = new FakeSandboxCommandRunner()
            .OnArgvContains("merge-base", new SandboxCommandResult(0, "1a2b3c4d\n", string.Empty));
        var preparer = new ReviewSlotPreparer(
            new GitRunner(runner), SeedGitmodules(slot.SharedStorePath), "github", NullLoggerFactory.Instance);

        _ = await preparer.PrepareAsync(
            slot, run, StoreUrl, SubmoduleRelPath, Branch, DefaultBranch, NotesRelPath, BuildPolicy(),
            CancellationToken.None);

        runner.Commands.Select(c => string.Join(' ', c.Argv)).Should().NotContain(
            command => command.Contains("--depth", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PrepareAsync_SharedStore_DoesNotDeepenAFullCloneThatGenuinelyHasNoMergeBase()
    {
        // Full history and still unrelated means the base is orphaned — force-pushed over, or stranded by a
        // history rewrite. Deepening cannot invent an ancestor, so the two escalating fetches would be pure
        // cost before the same failure. Preparation still returns: the diff reports git's own message, which
        // says far more about what went wrong than anything this layer could substitute.
        var slot = CreateSharedSlot();
        var run = CreateRun();
        var runner = new FakeSandboxCommandRunner()
            .OnArgvContains("merge-base", new SandboxCommandResult(1, string.Empty, string.Empty))
            .OnArgvContains(
                "rev-parse --is-shallow-repository", new SandboxCommandResult(0, "false\n", string.Empty));
        var preparer = new ReviewSlotPreparer(
            new GitRunner(runner), SeedGitmodules(slot.SharedStorePath), "github", NullLoggerFactory.Instance);

        var prepared = await preparer.PrepareAsync(
            slot, run, StoreUrl, SubmoduleRelPath, Branch, DefaultBranch, NotesRelPath, BuildPolicy(),
            CancellationToken.None);

        prepared.TargetDir.Should().Be(slot.TargetPath);
        runner.Commands.Select(c => string.Join(' ', c.Argv)).Should().NotContain(
            command => command.Contains("--depth", StringComparison.Ordinal));
    }

    /// <summary>
    /// The SHARED-WORKTREE layout, which is the one every live deployment runs and which the flat-slot tests
    /// above do not reach. Here the notes branch is a worktree of one shared clone, and that clone is parked
    /// on the default branch by name. `git fetch origin` advances `origin/main` and leaves `main` untouched,
    /// so parking by name parks on a ref that never moves: measured on the live NOVA store, local `main` sat
    /// at the initial commit `dc5cc82` for a day while `origin/main` reached `86718a9`, 54 commits later.
    /// Every notes worktree cut from it inherited an empty Knowledge Base, which is why every brief this
    /// daemon has ever assembled reported prior-knowledge=0.
    /// </summary>
    [Fact]
    public async Task PrepareAsync_SharedLayout_AdvancesTheLocalDefaultToTheFetchedOrigin()
    {
        var slot = CreateSharedSlot();
        var runner = new FakeSandboxCommandRunner();
        runner.OnArgvContains(
            $"rev-parse --verify origin/{Branch}", new SandboxCommandResult(1, string.Empty, "fatal: unknown revision"));
        var fileSystem = SeedGitmodules(slot.SharedStorePath);
        var preparer = new ReviewSlotPreparer(new GitRunner(runner), fileSystem, "github", NullLoggerFactory.Instance);

        _ = await preparer.PrepareAsync(
            slot, CreateRun(), StoreUrl, SubmoduleRelPath, Branch, DefaultBranch, NotesRelPath, BuildPolicy(), CancellationToken.None);

        var commands = runner.Commands.Select(c => string.Join(' ', c.Argv)).ToList();
        commands.Should().Contain(
            a => a.Contains("reset --hard") && a.Contains($"origin/{DefaultBranch}"),
            "parking the shared clone on the default branch is only meaningful if that branch is the FETCHED "
                + "one — a checkout by name pins it to a local ref that git fetch never advances");
        commands.Should().Contain(
            a => a.Contains($"-B {Branch} origin/{DefaultBranch}"),
            "and the notes worktree is cut from the fetched default for the same reason");
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

    /// <summary>
    /// A slot on the shared-object-store worktree layout: the mount belongs to the REPOSITORY, holds the one
    /// real clone under <c>store/</c>, and gives this slot a directory of its own whose <c>notes/</c> and
    /// <c>repo/</c> are worktrees of that clone. Paths are built with forward slashes because that is what
    /// the pool hands out and what the emitted git argv is asserted against.
    /// </summary>
    private ReviewSlot CreateSharedSlot(int index = 0)
    {
        var mount = Path.Combine(_hostRoot, "review-nova").Replace('\\', '/');
        var slotDir = $"slot-{index}";
        var slot = new ReviewSlot(
            index,
            mount,
            $"{mount}/{slotDir}/notes",
            $"{mount}/{slotDir}/scratch",
            RepoKey: "github.com/gautamb_microsoft/NOVA_reviews",
            SharedStorePath: $"{mount}/store",
            TargetPath: $"{mount}/{slotDir}/repo",
            SlotDirName: slotDir);
        Directory.CreateDirectory(slot.ScratchPath);
        // A real leased mount always has the shared store cloned; hygiene probes its .git.
        Directory.CreateDirectory(Path.Combine(slot.SharedStorePath, ".git"));
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
