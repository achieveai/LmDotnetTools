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
    public async Task RecloneStoreAsync_HostPreparer_DoesNotReachThroughALinkOutOfTheStore()
    {
        // This wipe is where a store that redirects its own hygiene sweep gets ROUTED (SlotHygiene condemns one
        // rather than unlinking it), so it is the second walk the same planted link reaches, and it must survive
        // as well as contain it. The read-only-clearing pass ahead of the delete enumerated with AllDirectories,
        // which follows a junction, and wrote to every file it reached; and Directory.Delete's own recursion
        // throws on a junction, so a condemned store would have become an unrecoverable slot. Whatever the link
        // points at comes through untouched, and the store still goes.
        var slot = CreateSlot();
        var outside = Path.Combine(_hostRoot, "outside");
        Directory.CreateDirectory(outside);
        var victim = Path.Combine(outside, "someone-elses.idx");
        await File.WriteAllTextAsync(victim, "idx");
        File.SetAttributes(victim, FileAttributes.ReadOnly);
        DirectoryLink.Create(Path.Combine(slot.StorePath, ".git", "modules"), outside);
        var preparer = new ReviewSlotPreparer(
            new GitRunner(new FakeSandboxCommandRunner()), new HostFileSystem(), "github",
            NullLoggerFactory.Instance);

        await preparer.RecloneStoreAsync(slot.StorePath, StoreUrl, CancellationToken.None);

        File.Exists(victim).Should().BeTrue("the wipe unlinks the junction; it never deletes past it");
        File.GetAttributes(victim).HasFlag(FileAttributes.ReadOnly).Should().BeTrue(
            "clearing read-only outside the store is a write on the daemon host chosen by whoever planted the link");
        Directory.Exists(slot.StorePath).Should().BeFalse("the store itself is still wiped");
        File.SetAttributes(victim, FileAttributes.Normal); // so Dispose's best-effort wipe can reach it
    }

    [Fact]
    public async Task RecloneStoreAsync_HostPreparer_RefusesAStoreRootThatIsItselfRedirected()
    {
        // The walk checked every entry it FOUND and never the root it was HANDED, so a store path that was
        // itself a junction was enumerated straight through: the attribute pass cleared read-only on the
        // target's files, and the delete then unlinked the store the daemon was told to re-clone. The root is
        // the one entry with no ancestor left to catch it, so it is checked here or nowhere.
        var slotPath = Path.Combine(_hostRoot, "slot-0");
        _ = Directory.CreateDirectory(slotPath);
        var outside = Path.Combine(_hostRoot, "outside");
        _ = Directory.CreateDirectory(outside);
        var victim = Path.Combine(outside, "someone-elses.idx");
        await File.WriteAllTextAsync(victim, "idx");
        File.SetAttributes(victim, FileAttributes.ReadOnly);
        var storeRoot = Path.Combine(slotPath, "store");
        DirectoryLink.Create(storeRoot, outside);
        var runner = new FakeSandboxCommandRunner();
        var preparer = new ReviewSlotPreparer(
            new GitRunner(runner), new HostFileSystem(), "github", NullLoggerFactory.Instance);

        var act = async () => await preparer.RecloneStoreAsync(storeRoot, StoreUrl, CancellationToken.None);

        _ = await act.Should().ThrowAsync<SlotAddressUnusableException>();
        File.GetAttributes(victim).HasFlag(FileAttributes.ReadOnly).Should().BeTrue(
            "clearing read-only THROUGH the root is the same write outside the store the child check refuses");
        HostPathGuard.Check(storeRoot).Should().Be(
            new HostPathRefusal(storeRoot, HostPathVerdict.Redirected),
            "refusing means refusing both ways: the link is not followed and it is not removed either");
        runner.Commands.Select(c => string.Join(' ', c.Argv)).Should().NotContain(
            command => command.Contains(" clone ", StringComparison.Ordinal),
            "a clone after the refusal would land the store wherever the link aims");
        File.SetAttributes(victim, FileAttributes.Normal); // so Dispose's best-effort wipe can reach it
    }

    [Fact]
    public async Task PrepareAsync_HostPreparer_RefusesAScratchRootThatIsItselfRedirected()
    {
        // The scratch wipe is the same delete's second caller and the worse of the two, because it re-creates
        // the directory afterwards: a redirected scratch was unlinked and quietly replaced with a real one,
        // which is exactly the "repair" that hands whoever planted the link a fresh target to plant again.
        var slot = CreateSlot();
        var outside = Path.Combine(_hostRoot, "outside");
        _ = Directory.CreateDirectory(outside);
        var victim = Path.Combine(outside, "someone-elses.txt");
        await File.WriteAllTextAsync(victim, "notes");
        File.SetAttributes(victim, FileAttributes.ReadOnly);
        Directory.Delete(slot.ScratchPath);
        DirectoryLink.Create(slot.ScratchPath, outside);
        await File.WriteAllTextAsync(
            Path.Combine(slot.StorePath, ".gitmodules"),
            "[submodule \"LmDotnetTools\"]\n\tpath = repos/LmDotnetTools\n"
                + "\turl = https://github.com/achieveai/LmDotnetTools.git\n");
        var preparer = new ReviewSlotPreparer(
            new GitRunner(new FakeSandboxCommandRunner()), new HostFileSystem(), "github",
            NullLoggerFactory.Instance);

        var act = async () => await preparer.PrepareAsync(
            slot, CreateRun(), StoreUrl, SubmoduleRelPath, Branch, DefaultBranch, NotesRelPath, BuildPolicy(),
            CancellationToken.None);

        _ = await act.Should().ThrowAsync<SlotAddressUnusableException>();
        File.GetAttributes(victim).HasFlag(FileAttributes.ReadOnly).Should().BeTrue();
        HostPathGuard.Check(slot.ScratchPath).Should().Be(
            new HostPathRefusal(slot.ScratchPath, HostPathVerdict.Redirected),
            "the scratch link survives the refusal untouched — removing and re-creating it IS the repair");
        File.SetAttributes(victim, FileAttributes.Normal); // so Dispose's best-effort wipe can reach it
    }

    [RequiresFileSymlinkFact("a junction always reads as a directory, so it cannot stand in for this case")]
    public async Task RecloneStoreAsync_HostPreparer_RefusesAStoreRootThatIsAFileSymlink()
    {
        // Pins the ORDER of the wipe's two opening checks, not just their presence. Every redirected DIRECTORY
        // still reads Directory.Exists as true — a junction and a directory symlink both do, target present or
        // not — so moving the containment check below the existence check keeps catching all of those and looks
        // like a free simplification. A file symlink standing where the store should be is the one redirected
        // root that reads as absent: a guard below the existence check never runs on it, the wipe returns as
        // though there were nothing there, and the re-clone lands on a name that resolves somewhere else.
        var slotPath = Path.Combine(_hostRoot, "slot-0");
        _ = Directory.CreateDirectory(slotPath);
        var outside = Path.Combine(_hostRoot, "outside");
        _ = Directory.CreateDirectory(outside);
        var victim = Path.Combine(outside, "someone-elses.txt");
        await File.WriteAllTextAsync(victim, "notes");
        var storeRoot = Path.Combine(slotPath, "store");
        FileLink.Create(storeRoot, victim);
        var runner = new FakeSandboxCommandRunner();
        var preparer = new ReviewSlotPreparer(
            new GitRunner(runner), new HostFileSystem(), "github", NullLoggerFactory.Instance);

        var act = async () => await preparer.RecloneStoreAsync(storeRoot, StoreUrl, CancellationToken.None);

        _ = await act.Should().ThrowAsync<SlotAddressUnusableException>();
        runner.Commands.Select(c => string.Join(' ', c.Argv)).Should().NotContain(
            command => command.Contains(" clone ", StringComparison.Ordinal),
            "a root that reads as absent is still a root that redirects, and cloning onto it writes outside");
        (await File.ReadAllTextAsync(victim)).Should().Be("notes", "nothing may reach through the link");
        HostPathGuard.Check(storeRoot).Should().Be(
            new HostPathRefusal(storeRoot, HostPathVerdict.Redirected),
            "refusing means refusing both ways: the link is not followed and it is not removed either");
    }

    [RequiresUnreadableEntryFact("a readable entry cannot show the difference between absent and un-inspectable")]
    public async Task RecloneStoreAsync_HostPreparer_RefusesAStoreRootItCannotInspect()
    {
        // The guard answered a plain "is this a link?", and every way of failing to find out was folded into
        // "no". An entry the daemon is denied the attributes of reads exactly like one that is not there —
        // FileSystemInfo.Exists reports false for both — so the wipe returned as though the store were already
        // gone and the re-clone went ahead onto a name nobody had established anything about. The walk's whole
        // job is to establish containment, and "I could not look" is not an establishment: it is refused on the
        // same terms as a link, and for the same reason a link is refused rather than repaired.
        var slotPath = Path.Combine(_hostRoot, "slot-0");
        _ = Directory.CreateDirectory(slotPath);
        using var denied = UnreadableEntry.Create(slotPath);
        var runner = new FakeSandboxCommandRunner();
        var preparer = new ReviewSlotPreparer(
            new GitRunner(runner), new HostFileSystem(), "github", NullLoggerFactory.Instance);

        var act = async () => await preparer.RecloneStoreAsync(denied.Path, StoreUrl, CancellationToken.None);

        _ = await act.Should().ThrowAsync<SlotAddressUnusableException>();
        runner.Commands.Select(c => string.Join(' ', c.Argv)).Should().NotContain(
            command => command.Contains(" clone ", StringComparison.Ordinal),
            "cloning onto a path the daemon could not inspect is the write the whole check exists to prevent");
        HostPathGuard.Check(denied.Path).Should().Be(
            new HostPathRefusal(denied.Path, HostPathVerdict.Unreadable),
            "the refusal has to name what actually stopped it — reporting a link that was never there sends "
                + "the next reader looking for one");
    }

    [RequiresUnreadableEntryFact("a listable directory cannot show the difference between empty and un-listable")]
    public async Task RecloneStoreAsync_HostPreparer_RefusesADirectoryInsideTheStoreItCannotList()
    {
        // The walk decides what to delete from what it enumerates, so a directory whose CONTENTS will not list
        // is the same failure to establish containment as an entry whose attributes will not read — one call
        // later. Returning an empty array there says "nothing inside", and the delete below then removes the
        // directory without ever having looked in it. Only ListDirectory is denied, so TRAVERSAL survives and
        // git goes on opening paths underneath by name: nothing else in the sequence reports a thing.
        var slot = CreateSlot();
        var opaque = Path.Combine(slot.StorePath, ".git", "objects");
        _ = Directory.CreateDirectory(opaque);
        using var denied = UnreadableEntry.UnlistableDirectory(opaque);
        var runner = new FakeSandboxCommandRunner();
        var preparer = new ReviewSlotPreparer(
            new GitRunner(runner), new HostFileSystem(), "github", NullLoggerFactory.Instance);

        var act = async () => await preparer.RecloneStoreAsync(slot.StorePath, StoreUrl, CancellationToken.None);

        var refusal = await act.Should().ThrowAsync<SlotAddressUnusableException>();
        refusal.Which.Message.Should().Contain(opaque, "the message is the operator's only account of what stopped");
        refusal.Which.InnerException.Should().BeOfType<UnauthorizedAccessException>(
            "a denial and a failing device produce the same refusal but not the same operator response");
        Directory.Exists(opaque).Should().BeTrue("refusing means refusing both ways — it is not deleted either");
        runner.Commands.Select(c => string.Join(' ', c.Argv)).Should().NotContain(
            command => command.Contains(" clone ", StringComparison.Ordinal),
            "a clone here would write a fresh store over a directory nobody established anything about");
    }

    [RequiresUnreadableEntryFact("a removable link cannot show what happens when the unlink is refused")]
    public async Task RecloneStoreAsync_HostPreparer_RefusesARedirectedEntryItIsNotPermittedToUnlink()
    {
        // The walk's answer to a redirected entry is to unlink it, and that unlink can be DENIED. Nothing caught
        // it, so the raw I/O exception left the wipe untyped — and the pooled caller routes on TYPE: only
        // SlotAddressUnusableException sets refused=true and retires, everything else returns the slot to a free
        // list that is a STACK. The next run takes the same index, meets the same entry, and is denied again. An
        // entry the daemon is not PERMITTED to remove is not a transient failure the way a busy file is: it fails
        // identically on every lease, forever, which is the exact loop the retire path exists to break.
        //
        // Retire-vs-return is already pinned both ways at DaemonReviewStageExecutorPooledTests, so what is left
        // untested is the TRANSLATION — that a refused unlink reaches that router as a refusal at all. That is a
        // missing catch, one call below the one ChildrenOf already has above.
        var slot = CreateSlot();
        var outside = Path.Combine(_hostRoot, "outside");
        _ = Directory.CreateDirectory(outside);
        var victim = Path.Combine(outside, "someone-elses.txt");
        await File.WriteAllTextAsync(victim, "notes");
        var objects = Path.Combine(slot.StorePath, "objects");
        _ = Directory.CreateDirectory(objects);
        var planted = Path.Combine(objects, "planted");
        using var undeletable = UnreadableEntry.UndeletableLink(planted, outside);
        var runner = new FakeSandboxCommandRunner();
        var preparer = new ReviewSlotPreparer(
            new GitRunner(runner), new HostFileSystem(), "github", NullLoggerFactory.Instance);

        var act = async () => await preparer.RecloneStoreAsync(slot.StorePath, StoreUrl, CancellationToken.None);

        var refusal = await act.Should().ThrowAsync<SlotAddressUnusableException>(
            "an ordinary I/O exception here is returned to the pool and leased again forever");
        refusal.Which.Message.Should().Contain(planted, "the message is the operator's only account of what stopped");
        refusal.Which.Message.Should().Contain(
            "symlink or junction",
            "the verdict is the operator's next move: reporting this as unreadable sends them hunting a read "
                + "permission on an entry whose problem is that it redirects and will not come out");
        (refusal.Which.InnerException is IOException or UnauthorizedAccessException).Should().BeTrue(
            "a denial and a failing device produce the same refusal but not the same operator response, and the "
                + "cause carried here was {0}",
            refusal.Which.InnerException?.GetType().Name ?? "nothing at all");
        Directory.Exists(planted).Should().BeTrue(
            "the entry that stopped the walk is left exactly as found — the wipe refused it, it did not lose a "
                + "race with it");
        (await File.ReadAllTextAsync(victim)).Should().Be("notes", "nothing may reach through the link");
        runner.Commands.Select(c => string.Join(' ', c.Argv)).Should().NotContain(
            command => command.Contains(" clone ", StringComparison.Ordinal),
            "a clone onto a store still holding the entry the wipe could not remove writes into a tree that was "
                + "never actually cleared");
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

    [RequiresUnreadableEntryFact("a listable directory cannot show the difference between empty and un-listable")]
    public async Task PrepareAsync_HostPreparer_StoreCleanupUnreadable_RaisesTheRefusalTypeNotReclone()
    {
        // Issue #276. Clean-on-entry's sweep cannot walk this store: a directory under .git will not list, so
        // it stops at an UNREADABLE entry. That is NOT a re-clone case — the re-clone begins by wiping the
        // store, and the wipe refuses on the same unreadable entry, so escalating there names a repair that
        // cannot run. The preparer must therefore surface the REFUSAL type directly (whose consumers retire the
        // slot), not SlotNeedsRecloneException (which the executor turns into that impossible re-clone). Before
        // the fix, hygiene reported the unreadable sweep as NeedsReclone and this threw the reclone type; the
        // retirement then only happened later, by the wipe's exception escaping a catch filter downstream.
        var slot = CreateSlot();
        var opaque = Path.Combine(slot.StorePath, ".git", "modules");
        _ = Directory.CreateDirectory(opaque);
        using var denied = UnreadableEntry.UnlistableDirectory(opaque);
        await File.WriteAllTextAsync(
            Path.Combine(slot.StorePath, ".gitmodules"),
            "[submodule \"LmDotnetTools\"]\n\tpath = repos/LmDotnetTools\n"
                + "\turl = https://github.com/achieveai/LmDotnetTools.git\n");
        var preparer = new ReviewSlotPreparer(
            new GitRunner(new FakeSandboxCommandRunner()), new HostFileSystem(), "github",
            NullLoggerFactory.Instance);

        var act = async () => await preparer.PrepareAsync(
            slot, CreateRun(), StoreUrl, SubmoduleRelPath, Branch, DefaultBranch, NotesRelPath, BuildPolicy(),
            CancellationToken.None);

        // A single typed assertion is the whole decision: SlotAddressUnusableException and
        // SlotNeedsRecloneException are unrelated sealed types, so before the fix (which threw the reclone type)
        // this line fails, and after it passes.
        await act.Should().ThrowAsync<SlotAddressUnusableException>(
            "an unreadable store cleanup retires the slot; re-cloning it walks the wipe into the same wall");
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

    /// <summary>
    /// A cleanliness probe that returned no answer is mapped to a type of its OWN, and the mapping is the
    /// point of the test. It must not be <see cref="SlotNeedsRecloneException"/> — the executor's recovery
    /// ladder catches that by type and spends minutes re-cloning a store nothing said was broken. It must not
    /// be <see cref="SlotAddressUnusableException"/> either — that RETIRES the slot, and a lost answer belongs
    /// to the attempt rather than to the address. Nothing catches this one, so the slot is released back to
    /// the pool untouched and the next lease probes it again.
    /// </summary>
    [Fact]
    public async Task PrepareAsync_UnansweredHygieneProbe_ThrowsItsOwnType_NotRecloneOrRetire()
    {
        var slot = CreateSlot();
        var runner = new FakeSandboxCommandRunner();
        // Exit 0 with no branch header: `status --porcelain -b` cannot produce that, so the output was lost.
        runner.OnArgvContains(
            "status --porcelain", new SandboxCommandResult(0, string.Empty, string.Empty));
        var preparer = new ReviewSlotPreparer(
            new GitRunner(runner), SeedGitmodules(slot.StorePath), "github", NullLoggerFactory.Instance);

        var act = async () => await preparer.PrepareAsync(
            slot, CreateRun(), StoreUrl, SubmoduleRelPath, Branch, DefaultBranch, NotesRelPath, BuildPolicy(), CancellationToken.None);

        await act.Should().ThrowExactlyAsync<SlotProbeUnansweredException>(
            "a re-clone answers a question that was never put, and retirement condemns the address for it");
        runner.Commands.Select(c => string.Join(' ', c.Argv))
            .Should()
            .NotContain(
                // `fetch origin` is the FIRST step past the hygiene switch, so its absence pins that the
                // throw happened at the gate and not somewhere later that happens to raise the same type.
                a => a.EndsWith("fetch origin", StringComparison.Ordinal),
                "preparation stops at the gate rather than reviewing a tree nothing established the state of");
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
