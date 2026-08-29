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

    private readonly string _hostRoot = Path.Combine(Path.GetTempPath(), "crd-prep-" + Guid.NewGuid().ToString("N"));

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
            requireSdkOwnershipMarker: true
        );

        await preparer.EnsureStoreAsync(slot.StorePath, StoreUrl, CancellationToken.None);

        runner
            .Commands.Select(c => string.Join(' ', c.Argv))
            .Should()
            .Contain(
                command => command == $"rm -rf -- {slot.StorePath}",
                "an unmarked warm store may carry host-git line-ending and ownership state"
            );
        runner
            .Commands.Select(c => string.Join(' ', c.Argv))
            .Should()
            .Contain(command => command.Contains($"clone {StoreUrl} {slot.StorePath}", StringComparison.Ordinal));
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
            requireSdkOwnershipMarker: true
        );

        await preparer.EnsureStoreAsync(slot.StorePath, StoreUrl, CancellationToken.None);

        runner
            .Commands.Select(c => string.Join(' ', c.Argv))
            .Should()
            .NotContain(command => command.StartsWith("rm -rf --", StringComparison.Ordinal));
        runner
            .Commands.Select(c => string.Join(' ', c.Argv))
            .Should()
            .NotContain(command => command.Contains(" clone ", StringComparison.Ordinal));
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
                new string('x', (int)SandboxReadLimits.RepositoryFileBytes + 1)
            );
        var preparer = new ReviewSlotPreparer(
            new GitRunner(runner),
            fileSystem,
            "ado",
            NullLoggerFactory.Instance,
            requireSdkOwnershipMarker: true
        );

        await preparer.EnsureStoreAsync(slot.StorePath, StoreUrl, CancellationToken.None);

        runner
            .Commands.Select(c => string.Join(' ', c.Argv))
            .Should()
            .NotContain(command => command.StartsWith("rm -rf --", StringComparison.Ordinal));
        runner
            .Commands.Select(c => string.Join(' ', c.Argv))
            .Should()
            .NotContain(command => command.Contains(" clone ", StringComparison.Ordinal));
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
            new GitRunner(runner),
            new HostFileSystem(),
            "github",
            NullLoggerFactory.Instance
        );

        await preparer.RecloneStoreAsync(slot.StorePath, StoreUrl, CancellationToken.None);

        Directory
            .Exists(slot.StorePath)
            .Should()
            .BeFalse("the corrupt host store is removed outright so the clone below lands in a fresh directory");
        runner
            .Commands.Select(c => string.Join(' ', c.Argv))
            .Should()
            .NotContain(
                command => command.StartsWith("rm -rf --", StringComparison.Ordinal),
                "host paths must not be passed to sandbox/POSIX command semantics"
            );
        runner
            .Commands.Select(c => string.Join(' ', c.Argv))
            .Should()
            .Contain(command => command.Contains($"clone {StoreUrl} {slot.StorePath}", StringComparison.Ordinal));
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
            new GitRunner(new FakeSandboxCommandRunner()),
            new HostFileSystem(),
            "github",
            NullLoggerFactory.Instance
        );

        await preparer.RecloneStoreAsync(slot.StorePath, StoreUrl, CancellationToken.None);

        File.Exists(victim).Should().BeTrue("the wipe unlinks the junction; it never deletes past it");
        File.GetAttributes(victim)
            .HasFlag(FileAttributes.ReadOnly)
            .Should()
            .BeTrue(
                "clearing read-only outside the store is a write on the daemon host chosen by whoever planted the link"
            );
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
            new GitRunner(runner),
            new HostFileSystem(),
            "github",
            NullLoggerFactory.Instance
        );

        var act = async () => await preparer.RecloneStoreAsync(storeRoot, StoreUrl, CancellationToken.None);

        _ = await act.Should().ThrowAsync<SlotAddressUnusableException>();
        File.GetAttributes(victim)
            .HasFlag(FileAttributes.ReadOnly)
            .Should()
            .BeTrue("clearing read-only THROUGH the root is the same write outside the store the child check refuses");
        HostPathGuard
            .Check(storeRoot)
            .Should()
            .Be(
                new HostPathRefusal(storeRoot, HostPathVerdict.Redirected),
                "refusing means refusing both ways: the link is not followed and it is not removed either"
            );
        runner
            .Commands.Select(c => string.Join(' ', c.Argv))
            .Should()
            .NotContain(
                command => command.Contains(" clone ", StringComparison.Ordinal),
                "a clone after the refusal would land the store wherever the link aims"
            );
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
                + "\turl = https://github.com/achieveai/LmDotnetTools.git\n"
        );
        var preparer = new ReviewSlotPreparer(
            new GitRunner(new FakeSandboxCommandRunner()),
            new HostFileSystem(),
            "github",
            NullLoggerFactory.Instance
        );

        var act = async () =>
            await preparer.PrepareAsync(
                slot,
                CreateRun(),
                StoreUrl,
                SubmoduleRelPath,
                Branch,
                DefaultBranch,
                NotesRelPath,
                BuildPolicy(),
                CancellationToken.None
            );

        _ = await act.Should().ThrowAsync<SlotAddressUnusableException>();
        File.GetAttributes(victim).HasFlag(FileAttributes.ReadOnly).Should().BeTrue();
        HostPathGuard
            .Check(slot.ScratchPath)
            .Should()
            .Be(
                new HostPathRefusal(slot.ScratchPath, HostPathVerdict.Redirected),
                "the scratch link survives the refusal untouched — removing and re-creating it IS the repair"
            );
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
            new GitRunner(runner),
            new HostFileSystem(),
            "github",
            NullLoggerFactory.Instance
        );

        var act = async () => await preparer.RecloneStoreAsync(storeRoot, StoreUrl, CancellationToken.None);

        _ = await act.Should().ThrowAsync<SlotAddressUnusableException>();
        runner
            .Commands.Select(c => string.Join(' ', c.Argv))
            .Should()
            .NotContain(
                command => command.Contains(" clone ", StringComparison.Ordinal),
                "a root that reads as absent is still a root that redirects, and cloning onto it writes outside"
            );
        (await File.ReadAllTextAsync(victim)).Should().Be("notes", "nothing may reach through the link");
        HostPathGuard
            .Check(storeRoot)
            .Should()
            .Be(
                new HostPathRefusal(storeRoot, HostPathVerdict.Redirected),
                "refusing means refusing both ways: the link is not followed and it is not removed either"
            );
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
            new GitRunner(runner),
            new HostFileSystem(),
            "github",
            NullLoggerFactory.Instance
        );

        var act = async () => await preparer.RecloneStoreAsync(denied.Path, StoreUrl, CancellationToken.None);

        _ = await act.Should().ThrowAsync<SlotAddressUnusableException>();
        runner
            .Commands.Select(c => string.Join(' ', c.Argv))
            .Should()
            .NotContain(
                command => command.Contains(" clone ", StringComparison.Ordinal),
                "cloning onto a path the daemon could not inspect is the write the whole check exists to prevent"
            );
        HostPathGuard
            .Check(denied.Path)
            .Should()
            .Be(
                new HostPathRefusal(denied.Path, HostPathVerdict.Unreadable),
                "the refusal has to name what actually stopped it — reporting a link that was never there sends "
                    + "the next reader looking for one"
            );
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
            new GitRunner(runner),
            new HostFileSystem(),
            "github",
            NullLoggerFactory.Instance
        );

        var act = async () => await preparer.RecloneStoreAsync(slot.StorePath, StoreUrl, CancellationToken.None);

        var refusal = await act.Should().ThrowAsync<SlotAddressUnusableException>();
        refusal.Which.Message.Should().Contain(opaque, "the message is the operator's only account of what stopped");
        refusal
            .Which.InnerException.Should()
            .BeOfType<UnauthorizedAccessException>(
                "a denial and a failing device produce the same refusal but not the same operator response"
            );
        Directory.Exists(opaque).Should().BeTrue("refusing means refusing both ways — it is not deleted either");
        runner
            .Commands.Select(c => string.Join(' ', c.Argv))
            .Should()
            .NotContain(
                command => command.Contains(" clone ", StringComparison.Ordinal),
                "a clone here would write a fresh store over a directory nobody established anything about"
            );
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
            new GitRunner(runner),
            new HostFileSystem(),
            "github",
            NullLoggerFactory.Instance
        );

        var act = async () => await preparer.RecloneStoreAsync(slot.StorePath, StoreUrl, CancellationToken.None);

        var refusal = await act.Should()
            .ThrowAsync<SlotAddressUnusableException>(
                "an ordinary I/O exception here is returned to the pool and leased again forever"
            );
        refusal.Which.Message.Should().Contain(planted, "the message is the operator's only account of what stopped");
        refusal
            .Which.Message.Should()
            .Contain(
                "symlink or junction",
                "the verdict is the operator's next move: reporting this as unreadable sends them hunting a read "
                    + "permission on an entry whose problem is that it redirects and will not come out"
            );
        (refusal.Which.InnerException is IOException or UnauthorizedAccessException)
            .Should()
            .BeTrue(
                "a denial and a failing device produce the same refusal but not the same operator response, and the "
                    + "cause carried here was {0}",
                refusal.Which.InnerException?.GetType().Name ?? "nothing at all"
            );
        Directory
            .Exists(planted)
            .Should()
            .BeTrue(
                "the entry that stopped the walk is left exactly as found — the wipe refused it, it did not lose a "
                    + "race with it"
            );
        (await File.ReadAllTextAsync(victim)).Should().Be("notes", "nothing may reach through the link");
        runner
            .Commands.Select(c => string.Join(' ', c.Argv))
            .Should()
            .NotContain(
                command => command.Contains(" clone ", StringComparison.Ordinal),
                "a clone onto a store still holding the entry the wipe could not remove writes into a tree that was "
                    + "never actually cleared"
            );
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
            new GitRunner(runner),
            new HostFileSystem(),
            "github",
            NullLoggerFactory.Instance
        );

        await preparer.EnsureStoreAsync(slot.StorePath, StoreUrl, CancellationToken.None);

        var clone = runner
            .Commands.Should()
            .ContainSingle(c =>
                string.Join(' ', c.Argv).Contains($"clone {StoreUrl} {slot.StorePath}", StringComparison.Ordinal)
            )
            .Subject;
        clone
            .WorkingDirectory.Should()
            .BeNull("the daemon host has no /workspace, so pinning it as the clone's cwd fails the first-use clone");
    }

    [Fact]
    public async Task EnsureStoreAsync_SandboxPreparer_ClonesFromTheContainerWorkspaceRoot()
    {
        // Non-regression for the host fix above: inside the sandbox `/workspace` IS a real, mounted directory
        // and stays the clone's working directory.
        var slot = CreateSlot(withGitDir: false);
        var runner = new FakeSandboxCommandRunner();
        var preparer = new ReviewSlotPreparer(
            new GitRunner(runner),
            new FakeSandboxFileSystem(),
            "github",
            NullLoggerFactory.Instance
        );

        await preparer.EnsureStoreAsync(slot.StorePath, StoreUrl, CancellationToken.None);

        var clone = runner
            .Commands.Should()
            .ContainSingle(c =>
                string.Join(' ', c.Argv).Contains($"clone {StoreUrl} {slot.StorePath}", StringComparison.Ordinal)
            )
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
                + "\turl = https://github.com/achieveai/LmDotnetTools.git\n"
        );
        var runner = new FakeSandboxCommandRunner();
        runner.OnArgvContains("find ", new SandboxCommandResult(1, string.Empty, "'find' is not recognized"));
        var preparer = new ReviewSlotPreparer(
            new GitRunner(runner),
            new HostFileSystem(),
            "github",
            NullLoggerFactory.Instance
        );

        _ = await preparer.PrepareAsync(
            slot,
            CreateRun(),
            StoreUrl,
            SubmoduleRelPath,
            Branch,
            DefaultBranch,
            NotesRelPath,
            BuildPolicy(),
            CancellationToken.None
        );

        File.Exists(staleLock).Should().BeFalse("clean-on-entry clears the stale lock with host filesystem APIs");
        File.Exists(Path.Combine(gitDir, "MERGE_HEAD")).Should().BeFalse();
        Directory.Exists(Path.Combine(gitDir, "rebase-merge")).Should().BeFalse();
        runner
            .Commands.Select(c => string.Join(' ', c.Argv))
            .Should()
            .NotContain(
                command => command.StartsWith("find ", StringComparison.Ordinal),
                "host stale-state cleanup must not be routed through POSIX find"
            );
    }

    [Fact]
    public async Task EnsureStoreAsync_HostPreparer_ReusesAnUnmarkedStore()
    {
        var slot = CreateSlot();
        var runner = new FakeSandboxCommandRunner();
        var preparer = new ReviewSlotPreparer(
            new GitRunner(runner),
            SeedGitmodules(slot.StorePath),
            "github",
            NullLoggerFactory.Instance
        );

        await preparer.EnsureStoreAsync(slot.StorePath, StoreUrl, CancellationToken.None);

        runner
            .Commands.Select(c => string.Join(' ', c.Argv))
            .Should()
            .NotContain(command => command.StartsWith("rm -rf --", StringComparison.Ordinal));
        runner
            .Commands.Select(c => string.Join(' ', c.Argv))
            .Should()
            .NotContain(command => command.Contains(" clone ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PrepareAsync_NewBranch_BranchesFromDefaultBranchAndAdvancesSubmodule()
    {
        var slot = CreateSlot();
        var runner = new FakeSandboxCommandRunner();
        runner.OnArgvContains(
            $"rev-parse --verify origin/{Branch}",
            new SandboxCommandResult(1, string.Empty, "fatal: unknown revision")
        );
        var fileSystem = SeedGitmodules(slot.StorePath);
        var preparer = new ReviewSlotPreparer(new GitRunner(runner), fileSystem, "github", NullLoggerFactory.Instance);
        var run = CreateRun();

        var result = await preparer.PrepareAsync(
            slot,
            run,
            StoreUrl,
            SubmoduleRelPath,
            Branch,
            DefaultBranch,
            NotesRelPath,
            BuildPolicy(),
            CancellationToken.None
        );

        var commands = runner.Commands.Select(c => string.Join(' ', c.Argv)).ToList();
        commands
            .Should()
            .Contain(
                a => a.Contains($"checkout -B {Branch} {DefaultBranch}"),
                "a brand-new branch is cut from the default branch"
            );
        commands
            .Should()
            .NotContain(
                a => a.Contains($"checkout -B {Branch} origin/{Branch}"),
                "there is no prior origin branch to reuse"
            );
        commands
            .Should()
            .Contain(
                a => a.Contains("submodule update --init") && a.Contains(SubmoduleRelPath),
                "the reviewed submodule is initialized exactly like InitAllowListedSubmodulesAsync"
            );

        var expectedTargetDir = $"{slot.StorePath}/{SubmoduleRelPath}";
        commands
            .Should()
            .Contain(
                a => a.Contains($"-C {expectedTargetDir} fetch origin {run.BaseSha} {run.HeadSha}"),
                "the submodule fetches exactly the PR's base+head commits"
            );
        commands
            .Should()
            .Contain(
                a => a.Contains($"-C {expectedTargetDir} checkout --force {run.HeadSha}"),
                "the submodule working tree is advanced to the PR head"
            );

        result.Branch.Should().Be(Branch);
    }

    [Fact]
    public async Task PrepareAsync_ExistingOriginBranch_ReusesItInsteadOfTheDefaultBranch()
    {
        var slot = CreateSlot();
        var runner = new FakeSandboxCommandRunner();
        runner.OnArgvContains(
            $"rev-parse --verify origin/{Branch}",
            new SandboxCommandResult(0, "abc123\n", string.Empty)
        );
        var fileSystem = SeedGitmodules(slot.StorePath);
        var preparer = new ReviewSlotPreparer(new GitRunner(runner), fileSystem, "github", NullLoggerFactory.Instance);
        var run = CreateRun();

        _ = await preparer.PrepareAsync(
            slot,
            run,
            StoreUrl,
            SubmoduleRelPath,
            Branch,
            DefaultBranch,
            NotesRelPath,
            BuildPolicy(),
            CancellationToken.None
        );

        var commands = runner.Commands.Select(c => string.Join(' ', c.Argv)).ToList();
        commands
            .Should()
            .Contain(
                a => a.Contains($"checkout -B {Branch} origin/{Branch}"),
                "the existing remote branch (and its prior notes) is reused"
            );
        commands
            .Should()
            .NotContain(
                a => a.Contains($"checkout -B {Branch} {DefaultBranch}"),
                "the default branch must not be used when the persistent branch already exists — this would wipe prior notes"
            );
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
                + "\turl = https://github.com/achieveai/LmDotnetTools.git\n"
        );
        var runner = new FakeSandboxCommandRunner();
        var preparer = new ReviewSlotPreparer(
            new GitRunner(runner),
            new HostFileSystem(),
            "github",
            NullLoggerFactory.Instance
        );

        _ = await preparer.PrepareAsync(
            slot,
            CreateRun(),
            StoreUrl,
            SubmoduleRelPath,
            Branch,
            DefaultBranch,
            NotesRelPath,
            BuildPolicy(),
            CancellationToken.None
        );

        Directory.Exists(slot.ScratchPath).Should().BeTrue();
        Directory.EnumerateFileSystemEntries(slot.ScratchPath).Should().BeEmpty();
        runner
            .Commands.Select(c => string.Join(' ', c.Argv))
            .Should()
            .NotContain(
                command =>
                    command.StartsWith("rm -rf --", StringComparison.Ordinal)
                    || command.StartsWith("mkdir -p --", StringComparison.Ordinal),
                "host paths must not be passed to sandbox/POSIX command semantics"
            );
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
            slot,
            CreateRun(),
            StoreUrl,
            SubmoduleRelPath,
            Branch,
            DefaultBranch,
            NotesRelPath,
            BuildPolicy(),
            CancellationToken.None
        );

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
            slot,
            CreateRun(),
            StoreUrl,
            SubmoduleRelPath,
            Branch,
            DefaultBranch,
            NotesRelPath,
            BuildPolicy(),
            CancellationToken.None
        );

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
            new GitRunner(runner),
            SeedGitmodules(slot.StorePath),
            "github",
            NullLoggerFactory.Instance
        );

        _ = await preparer.PrepareAsync(
            slot,
            CreateRun(),
            StoreUrl,
            SubmoduleRelPath,
            Branch,
            DefaultBranch,
            NotesRelPath,
            BuildPolicy(),
            CancellationToken.None
        );

        File.Exists(staleLock).Should().BeFalse("clean-on-entry clears the stale lock before the git steps");
    }

    [Fact]
    public async Task PrepareAsync_StoreWithoutGitDir_ThrowsSlotNeedsReclone()
    {
        var slot = CreateSlot(withGitDir: false);
        var preparer = new ReviewSlotPreparer(
            new GitRunner(new FakeSandboxCommandRunner()),
            SeedGitmodules(slot.StorePath),
            "github",
            NullLoggerFactory.Instance
        );

        var act = async () =>
            await preparer.PrepareAsync(
                slot,
                CreateRun(),
                StoreUrl,
                SubmoduleRelPath,
                Branch,
                DefaultBranch,
                NotesRelPath,
                BuildPolicy(),
                CancellationToken.None
            );

        await act.Should()
            .ThrowAsync<SlotNeedsRecloneException>("a structurally broken store must escalate to re-clone");
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
                + "\turl = https://github.com/achieveai/LmDotnetTools.git\n"
        );
        var preparer = new ReviewSlotPreparer(
            new GitRunner(new FakeSandboxCommandRunner()),
            new HostFileSystem(),
            "github",
            NullLoggerFactory.Instance
        );

        var act = async () =>
            await preparer.PrepareAsync(
                slot,
                CreateRun(),
                StoreUrl,
                SubmoduleRelPath,
                Branch,
                DefaultBranch,
                NotesRelPath,
                BuildPolicy(),
                CancellationToken.None
            );

        // A single typed assertion is the whole decision: SlotAddressUnusableException and
        // SlotNeedsRecloneException are unrelated sealed types, so before the fix (which threw the reclone type)
        // this line fails, and after it passes.
        await act.Should()
            .ThrowAsync<SlotAddressUnusableException>(
                "an unreadable store cleanup retires the slot; re-cloning it walks the wipe into the same wall"
            );
    }

    [Fact]
    public async Task PrepareAsync_ReviewedSubmoduleFailsToInit_ThrowsSlotCorrupt()
    {
        var slot = CreateSlot();
        var runner = new FakeSandboxCommandRunner();
        runner.OnArgvContains(
            "submodule update --init",
            new SandboxCommandResult(
                1,
                string.Empty,
                "fatal: Unable to create '.git/modules/sub/index.lock': File exists."
            )
        );
        var preparer = new ReviewSlotPreparer(
            new GitRunner(runner),
            SeedGitmodules(slot.StorePath),
            "github",
            NullLoggerFactory.Instance
        );

        var act = async () =>
            await preparer.PrepareAsync(
                slot,
                CreateRun(),
                StoreUrl,
                SubmoduleRelPath,
                Branch,
                DefaultBranch,
                NotesRelPath,
                BuildPolicy(),
                CancellationToken.None
            );

        await act.Should()
            .ThrowAsync<SlotCorruptException>(
                "a corrupt reviewed-submodule init failure (a stuck lock) drives the reclone ladder"
            );
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
            "submodule update --init",
            new SandboxCommandResult(1, string.Empty, "fatal: clone of submodule failed")
        );
        var preparer = new ReviewSlotPreparer(
            new GitRunner(runner),
            SeedGitmodules(slot.StorePath),
            "github",
            NullLoggerFactory.Instance
        );

        var act = async () =>
            await preparer.PrepareAsync(
                slot,
                CreateRun(),
                StoreUrl,
                SubmoduleRelPath,
                Branch,
                DefaultBranch,
                NotesRelPath,
                BuildPolicy(),
                CancellationToken.None
            );

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
            new SandboxCommandResult(
                1,
                string.Empty,
                "fatal: Unable to checkout 'deadbeef' in submodule path 'repos/X'"
            )
        );
        var preparer = new ReviewSlotPreparer(
            new GitRunner(runner),
            SeedGitmodules(slot.StorePath),
            "github",
            NullLoggerFactory.Instance
        );

        var thrown = await Record.ExceptionAsync(async () =>
            await preparer.PrepareAsync(
                slot,
                CreateRun(),
                StoreUrl,
                SubmoduleRelPath,
                Branch,
                DefaultBranch,
                NotesRelPath,
                BuildPolicy(),
                CancellationToken.None
            )
        );

        // PrepareAsync must complete (hygiene proceeds, then the rest of preparation runs) — NOT throw at all, and
        // in particular NOT the reclone-driving SlotNeedsRecloneException.
        thrown
            .Should()
            .BeNull("a non-corrupt hygiene restore failure proceeds; preparation completes without a reclone");
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
        runner.OnArgvContains("status --porcelain", new SandboxCommandResult(0, string.Empty, string.Empty));
        var preparer = new ReviewSlotPreparer(
            new GitRunner(runner),
            SeedGitmodules(slot.StorePath),
            "github",
            NullLoggerFactory.Instance
        );

        var act = async () =>
            await preparer.PrepareAsync(
                slot,
                CreateRun(),
                StoreUrl,
                SubmoduleRelPath,
                Branch,
                DefaultBranch,
                NotesRelPath,
                BuildPolicy(),
                CancellationToken.None
            );

        await act.Should()
            .ThrowExactlyAsync<SlotProbeUnansweredException>(
                "a re-clone answers a question that was never put, and retirement condemns the address for it"
            );
        runner
            .Commands.Select(c => string.Join(' ', c.Argv))
            .Should()
            .NotContain(
                // `fetch origin` is the FIRST step past the hygiene switch, so its absence pins that the
                // throw happened at the gate and not somewhere later that happens to raise the same type.
                a => a.EndsWith("fetch origin", StringComparison.Ordinal),
                "preparation stops at the gate rather than reviewing a tree nothing established the state of"
            );
    }

    [Fact]
    public async Task PrepareAsync_ReviewedSubmoduleTransientInitFailure_DoesNotDriveReclone()
    {
        var slot = CreateSlot();
        var runner = new FakeSandboxCommandRunner();
        runner.OnArgvContains(
            "submodule update --init",
            new SandboxCommandResult(
                1,
                string.Empty,
                "fatal: unable to access 'https://github.com/x': Could not resolve host: github.com"
            )
        );
        var preparer = new ReviewSlotPreparer(
            new GitRunner(runner),
            SeedGitmodules(slot.StorePath),
            "github",
            NullLoggerFactory.Instance
        );

        var act = async () =>
            await preparer.PrepareAsync(
                slot,
                CreateRun(),
                StoreUrl,
                SubmoduleRelPath,
                Branch,
                DefaultBranch,
                NotesRelPath,
                BuildPolicy(),
                CancellationToken.None
            );

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
            new SandboxCommandResult(128, string.Empty, "fatal: Unable to create '.git/index.lock': File exists.")
        );
        var preparer = new ReviewSlotPreparer(
            new GitRunner(runner),
            SeedGitmodules(slot.StorePath),
            "github",
            NullLoggerFactory.Instance
        );

        var act = async () =>
            await preparer.PrepareAsync(
                slot,
                run,
                StoreUrl,
                SubmoduleRelPath,
                Branch,
                DefaultBranch,
                NotesRelPath,
                BuildPolicy(),
                CancellationToken.None
            );

        await act.Should()
            .ThrowAsync<SlotCorruptException>("a corrupt-classified git failure drives the re-clone ladder");
    }

    [Fact]
    public async Task PrepareAsync_TransientStderrOnAGitStep_ThrowsInvalidOperation_NotCorrupt()
    {
        var slot = CreateSlot();
        var run = CreateRun();
        var runner = new FakeSandboxCommandRunner();
        runner.OnArgvContains(
            $"fetch origin {run.BaseSha} {run.HeadSha}",
            new SandboxCommandResult(
                128,
                string.Empty,
                "fatal: unable to access 'https://x': Could not resolve host: github.com"
            )
        );
        var preparer = new ReviewSlotPreparer(
            new GitRunner(runner),
            SeedGitmodules(slot.StorePath),
            "github",
            NullLoggerFactory.Instance
        );

        var act = async () =>
            await preparer.PrepareAsync(
                slot,
                run,
                StoreUrl,
                SubmoduleRelPath,
                Branch,
                DefaultBranch,
                NotesRelPath,
                BuildPolicy(),
                CancellationToken.None
            );

        // A transient network fault is a normal retry (keep the warm store), NOT a re-clone trigger.
        // SlotCorruptException derives from Exception (not InvalidOperationException), so asserting the exact
        // InvalidOperationException type proves the failure was classified transient, not corrupt.
        await act.Should().ThrowExactlyAsync<InvalidOperationException>();
    }

    // --- issue #582: the "not a git repository: .git/modules/<x>" shape must consult the filesystem, not just
    // the message, before deciding whether it is a benign deinit or the mcqdb present-but-corrupt shape. ---

    private const string NestedGitDirCheckoutStderr = "fatal: not a git repository: nested/../.git/modules/nested";

    [Fact]
    public async Task PrepareAsync_GenuinelyAbsentNestedGitDirOnCheckout_StillTakesTheBenignCarveOut()
    {
        var slot = CreateSlot();
        var run = CreateRun();
        var runner = new FakeSandboxCommandRunner();
        runner.OnArgvContains(
            $"checkout --force {run.HeadSha}",
            new SandboxCommandResult(128, string.Empty, NestedGitDirCheckoutStderr)
        );
        // No file seeded under the resolved nested-gitdir candidate — the fake's ListFilesAsync therefore
        // answers "nothing there", exactly what a genuinely deinit'd submodule (#279's tolerated case) looks
        // like on disk.
        var fileSystem = SeedGitmodules(slot.StorePath);
        var preparer = new ReviewSlotPreparer(new GitRunner(runner), fileSystem, "github", NullLoggerFactory.Instance);

        var act = async () =>
            await preparer.PrepareAsync(
                slot,
                run,
                StoreUrl,
                SubmoduleRelPath,
                Branch,
                DefaultBranch,
                NotesRelPath,
                BuildPolicy(),
                CancellationToken.None
            );

        // Unchanged from pre-#582 behavior: a confirmed-absent gitdir stays on the benign path, so this is a
        // plain retry-worthy failure, NOT a reclone trigger.
        await act.Should()
            .ThrowExactlyAsync<InvalidOperationException>(
                "a genuinely absent nested gitdir is still the tolerated #279 deinit shape"
            );
    }

    [Fact]
    public async Task PrepareAsync_PresentButCorruptNestedGitDirOnCheckout_ThrowsSlotCorrupt()
    {
        var slot = CreateSlot();
        var run = CreateRun();
        var runner = new FakeSandboxCommandRunner();
        runner.OnArgvContains(
            $"checkout --force {run.HeadSha}",
            new SandboxCommandResult(128, string.Empty, NestedGitDirCheckoutStderr)
        );
        var fileSystem = SeedGitmodules(slot.StorePath);
        var targetDir = $"{slot.StorePath.TrimEnd('/', '\\')}/{SubmoduleRelPath}";
        var candidate = GitFailureClassifier.ResolveNestedGitDirPath(targetDir, NestedGitDirCheckoutStderr);
        candidate.Should().NotBeNull("the stderr above must match the shape this fix resolves a path from");
        // The mcqdb shape: `.git/modules/<name>` is genuinely THERE (a NUL-corrupted HEAD inside it, here
        // simulated by any seeded entry under the directory) — present, just unable to answer for itself.
        fileSystem.Seed($"{candidate}/HEAD", "\0\0\0\0");
        var preparer = new ReviewSlotPreparer(new GitRunner(runner), fileSystem, "github", NullLoggerFactory.Instance);

        var act = async () =>
            await preparer.PrepareAsync(
                slot,
                run,
                StoreUrl,
                SubmoduleRelPath,
                Branch,
                DefaultBranch,
                NotesRelPath,
                BuildPolicy(),
                CancellationToken.None
            );

        await act.Should()
            .ThrowAsync<SlotCorruptException>(
                "a confirmed-present nested gitdir is corruption, not a deinit, and must drive the reclone ladder"
            );
    }

    /// <summary>
    /// What the host git runner returns for a command its watchdog killed on the idle timeout, and the stderr
    /// it writes with it. Named because the whole point of the tests below is that this arrives through the
    /// same channel as git's own answers and would otherwise be indistinguishable from one.
    /// </summary>
    private const int WatchdogKillExit = 124;

    /// <summary>The kernel's exit code for a process killed by SIGKILL — an OOM killer, a container stop, an
    /// operator. Different cause, same obligation: it is not an answer about anyone's branch.</summary>
    private const int SigkillExit = 137;

    private const string KilledStderr =
        "git merge-base produced no output for 300s (idle timeout) and was killed by the daemon after 300.1s.";

    /// <summary>
    /// The runner shape the merge-base tests share: a full clone, healthy in every respect except what
    /// <c>merge-base</c> reports. Extracted so that the ONLY difference between those tests is the result
    /// handed to that one command — which is the whole of what separates a fact about the pull request from a
    /// fact about this daemon's own machine.
    /// </summary>
    private static FakeSandboxCommandRunner FullCloneWhoseMergeBaseAnswers(
        string target,
        SandboxCommandResult mergeBase
    ) =>
        new FakeSandboxCommandRunner()
            .OnArgvContains($"-C {target} merge-base", mergeBase)
            .OnArgvContains(
                $"-C {target} rev-parse --is-shallow-repository",
                new SandboxCommandResult(0, "false\n", string.Empty)
            );

    /// <summary>
    /// The control for every test that follows, and the one case that keeps its licence. <c>git merge-base</c>
    /// is documented to exit 1 with no output when the commits share no ancestor: that is git ANSWERING, not
    /// git failing, and on a clone that is not shallow it is as final as the question gets. So this run may —
    /// and must still — reach the author-facing claim.
    /// <para>
    /// Pinned explicitly because the discriminator is a single number. The obvious way to write the probe is
    /// "the command did not succeed", which any non-zero exit satisfies; it is the number 1 specifically, and
    /// nothing else here asserts that 1 still qualifies.
    /// </para>
    /// </summary>
    [Fact]
    public async Task PrepareAsync_MergeBaseExitOneIsAnAnswerAndStillEarnsTheUnrelatedClaim()
    {
        var slot = CreateSlot();
        var run = CreateRun();
        using var logs = new CapturingLoggerFactory();
        var target = $"{slot.StorePath}/{SubmoduleRelPath}";
        var runner = FullCloneWhoseMergeBaseAnswers(target, new SandboxCommandResult(1, string.Empty, string.Empty));
        var preparer = new ReviewSlotPreparer(new GitRunner(runner), SeedGitmodules(slot.StorePath), "github", logs);

        var prepared = await preparer.PrepareAsync(
            slot,
            run,
            StoreUrl,
            SubmoduleRelPath,
            Branch,
            DefaultBranch,
            NotesRelPath,
            BuildPolicy(),
            CancellationToken.None
        );

        prepared
            .MergeBase.Should()
            .Be(
                MergeBaseOutcome.UnrelatedHistories,
                "exit 1 from merge-base is git's documented 'no common ancestor', and a clone that reports itself "
                    + "not shallow has no more history to find — this is the one shape that is genuinely a fact "
                    + "about the pull request's commits"
            );
        logs.Capturing.MessagesAtLevel(LogLevel.Warning)
            .Should()
            .Contain(
                m => m.Contains("is not shallow", StringComparison.Ordinal),
                "and it says so in the terms an operator can check, rather than as an indeterminate shrug"
            );
    }

    /// <summary>
    /// The defect the three-state probe exists for. The host git runner kills a command that has been silent
    /// past its idle timeout and returns exit 124 — and that runner has already been observed killing healthy
    /// multi-gigabyte git operations, so this is the failure mode of this exact code path rather than a
    /// hypothetical one.
    /// <para>
    /// Read as a bool, 124 is indistinguishable from git's exit 1, and the run goes on to conclude the commits
    /// share no ancestor and to say so in a comment on the pull request telling the author to re-target or
    /// rebase. Our timeout, delivered as a fact about their branch, with no hedge and nothing in the wording
    /// to suggest we had failed rather than they had.
    /// </para>
    /// </summary>
    [Fact]
    public async Task PrepareAsync_AWatchdogKilledMergeBaseIsIndeterminateNotUnrelatedHistories()
    {
        var slot = CreateSlot();
        var run = CreateRun();
        var target = $"{slot.StorePath}/{SubmoduleRelPath}";
        var runner = FullCloneWhoseMergeBaseAnswers(
            target,
            new SandboxCommandResult(WatchdogKillExit, string.Empty, KilledStderr)
        );
        var preparer = new ReviewSlotPreparer(
            new GitRunner(runner),
            SeedGitmodules(slot.StorePath),
            "github",
            NullLoggerFactory.Instance
        );

        var prepared = await preparer.PrepareAsync(
            slot,
            run,
            StoreUrl,
            SubmoduleRelPath,
            Branch,
            DefaultBranch,
            NotesRelPath,
            BuildPolicy(),
            CancellationToken.None
        );

        prepared
            .MergeBase.Should()
            .Be(
                MergeBaseOutcome.Indeterminate,
                "the probe never answered, so nothing about these commits was established — and only "
                    + "UnrelatedHistories is licensed to become author-facing text"
            );
        prepared
            .MergeBase.Should()
            .NotBe(
                MergeBaseOutcome.UnrelatedHistories,
                "stated separately because this is the assertion that matters: a killed command must never reach "
                    + "the one outcome that tells a pull-request author to re-target or rebase their branch"
            );
    }

    /// <summary>
    /// The same rule for a SIGKILL rather than the daemon's own watchdog — an OOM killer, a container stop, a
    /// stray <c>kill -9</c>. There is deliberately no separate code path for it; production reads every
    /// non-1 exit the same way, and this test exists so the rule is pinned as "only 1 is an answer" rather
    /// than as a list of exit codes someone remembered to enumerate.
    /// </summary>
    [Fact]
    public async Task PrepareAsync_ASigkilledMergeBaseIsIndeterminateNotUnrelatedHistories()
    {
        var slot = CreateSlot();
        var run = CreateRun();
        var target = $"{slot.StorePath}/{SubmoduleRelPath}";
        var runner = FullCloneWhoseMergeBaseAnswers(
            target,
            new SandboxCommandResult(SigkillExit, string.Empty, string.Empty)
        );
        var preparer = new ReviewSlotPreparer(
            new GitRunner(runner),
            SeedGitmodules(slot.StorePath),
            "github",
            NullLoggerFactory.Instance
        );

        var prepared = await preparer.PrepareAsync(
            slot,
            run,
            StoreUrl,
            SubmoduleRelPath,
            Branch,
            DefaultBranch,
            NotesRelPath,
            BuildPolicy(),
            CancellationToken.None
        );

        prepared.MergeBase.Should().Be(MergeBaseOutcome.Indeterminate);
    }

    /// <summary>
    /// The second of the three sites, and the one whose failure reads most like a success. The obvious way to
    /// consume the shallow probe is <c>!shallow.Succeeded || stdout != "true"</c> — one expression in which a
    /// command that was KILLED and a command that printed <c>false</c> are the same thing. A killed probe then
    /// arrives at the "full history already, and still unrelated" branch, which is exactly the branch that
    /// concludes the base commit was orphaned by the author's force-push.
    /// </summary>
    [Fact]
    public async Task PrepareAsync_AWatchdogKilledShallowProbeIsIndeterminateNotAConfirmedFullClone()
    {
        var slot = CreateSlot();
        var run = CreateRun();
        var target = $"{slot.StorePath}/{SubmoduleRelPath}";
        var runner = new FakeSandboxCommandRunner()
            .OnArgvContains($"-C {target} merge-base", new SandboxCommandResult(1, string.Empty, string.Empty))
            .OnArgvContains(
                $"-C {target} rev-parse --is-shallow-repository",
                new SandboxCommandResult(WatchdogKillExit, string.Empty, KilledStderr)
            );
        var preparer = new ReviewSlotPreparer(
            new GitRunner(runner),
            SeedGitmodules(slot.StorePath),
            "github",
            NullLoggerFactory.Instance
        );

        var prepared = await preparer.PrepareAsync(
            slot,
            run,
            StoreUrl,
            SubmoduleRelPath,
            Branch,
            DefaultBranch,
            NotesRelPath,
            BuildPolicy(),
            CancellationToken.None
        );

        prepared
            .MergeBase.Should()
            .Be(
                MergeBaseOutcome.Indeterminate,
                "whether the checkout is shallow was never established, and 'we could not ask' is not "
                    + "'we asked and it is a full clone'"
            );
        prepared
            .MergeBase.Should()
            .NotBe(
                MergeBaseOutcome.UnrelatedHistories,
                "merge-base genuinely said there is no ancestor here — but that only becomes permanent once the "
                    + "clone is KNOWN to hold all the history there is, and a killed probe knows nothing"
            );
    }

    /// <summary>A SIGKILL at the same site, for the same reason as the merge-base pair: the rule is "true or
    /// false, and nothing else", not an enumeration of the exit codes we happened to think of.</summary>
    [Fact]
    public async Task PrepareAsync_ASigkilledShallowProbeIsIndeterminateNotAConfirmedFullClone()
    {
        var slot = CreateSlot();
        var run = CreateRun();
        var target = $"{slot.StorePath}/{SubmoduleRelPath}";
        var runner = new FakeSandboxCommandRunner()
            .OnArgvContains($"-C {target} merge-base", new SandboxCommandResult(1, string.Empty, string.Empty))
            .OnArgvContains(
                $"-C {target} rev-parse --is-shallow-repository",
                new SandboxCommandResult(SigkillExit, string.Empty, string.Empty)
            );
        var preparer = new ReviewSlotPreparer(
            new GitRunner(runner),
            SeedGitmodules(slot.StorePath),
            "github",
            NullLoggerFactory.Instance
        );

        var prepared = await preparer.PrepareAsync(
            slot,
            run,
            StoreUrl,
            SubmoduleRelPath,
            Branch,
            DefaultBranch,
            NotesRelPath,
            BuildPolicy(),
            CancellationToken.None
        );

        prepared.MergeBase.Should().Be(MergeBaseOutcome.Indeterminate);
    }

    /// <summary>
    /// The runner shape for the third site: a shallow clone whose base sits on the graft root, whose head is
    /// whole, and whose <c>rev-list --count</c> of BASE follows <paramref name="baseCounts"/> round by round.
    /// The climb's give-up test is "did this fetch extend either commit", and that test is computed entirely
    /// from these numbers — so scripting them IS scripting the decision.
    /// </summary>
    private static FakeSandboxCommandRunner ShallowCloneCountingBaseAs(
        string target,
        string baseSha,
        string headSha,
        params SandboxCommandResult[] baseCounts
    ) =>
        new FakeSandboxCommandRunner()
            .OnArgvContains($"-C {target} merge-base", new SandboxCommandResult(1, string.Empty, string.Empty))
            .OnArgvContains(
                $"-C {target} rev-parse --is-shallow-repository",
                new SandboxCommandResult(0, "true\n", string.Empty)
            )
            .OnArgvContainsSequence($"-C {target} rev-list --count {baseSha}", baseCounts)
            .OnArgvContains(
                $"-C {target} rev-list --count {headSha}",
                new SandboxCommandResult(0, "34579\n", string.Empty)
            );

    /// <summary>
    /// The third site, and the least obvious of the three because the corrupted value never leaves the method
    /// it is computed in. Reporting 0 for a count that could not be taken lets the climb compare that 0
    /// against the previous round's reading, find it is not larger, and conclude the fetch bought no history
    /// — which is the loop's definition of "both walks reached real roots" and its only route to
    /// UnrelatedHistories.
    /// <para>
    /// So a <c>rev-list</c> killed by the watchdog would produce a permanent verdict about a pull request
    /// without any git command ever having reported anything about it. Zero is not a small number here; it is
    /// the absence of a number, and the two have opposite meanings for the comparison that consumes it.
    /// </para>
    /// </summary>
    [Fact]
    public async Task PrepareAsync_AWatchdogKilledReachableCountIsIndeterminateNotAnExhaustedHistory()
    {
        var slot = CreateSlot();
        var run = CreateRun();
        var target = $"{slot.StorePath}/{SubmoduleRelPath}";
        // Round 1 measures base at 1 and the fetch goes out; round 2's count is killed, so whether that fetch
        // bought anything is unknown — the same rounds as the exhaustion case, with one probe lost.
        var runner = ShallowCloneCountingBaseAs(
            target,
            run.BaseSha,
            run.HeadSha,
            new SandboxCommandResult(0, "1\n", string.Empty),
            new SandboxCommandResult(WatchdogKillExit, string.Empty, KilledStderr)
        );
        var preparer = new ReviewSlotPreparer(
            new GitRunner(runner),
            SeedGitmodules(slot.StorePath),
            "github",
            NullLoggerFactory.Instance
        );

        var prepared = await preparer.PrepareAsync(
            slot,
            run,
            StoreUrl,
            SubmoduleRelPath,
            Branch,
            DefaultBranch,
            NotesRelPath,
            BuildPolicy(),
            CancellationToken.None
        );

        prepared
            .MergeBase.Should()
            .Be(
                MergeBaseOutcome.Indeterminate,
                "an unmeasured round did not observe a flat history, it observed nothing"
            );
        prepared
            .MergeBase.Should()
            .NotBe(
                MergeBaseOutcome.UnrelatedHistories,
                "reading a lost count as 'this fetch bought no history' is how a killed rev-list becomes a "
                    + "permanent statement about someone else's commits"
            );
    }

    /// <summary>A SIGKILL at the counting site. Same rule, second exit code — production has one path for
    /// both, and this pins that the rule is about the absence of an answer rather than about 124.</summary>
    [Fact]
    public async Task PrepareAsync_ASigkilledReachableCountIsIndeterminateNotAnExhaustedHistory()
    {
        var slot = CreateSlot();
        var run = CreateRun();
        var target = $"{slot.StorePath}/{SubmoduleRelPath}";
        var runner = ShallowCloneCountingBaseAs(
            target,
            run.BaseSha,
            run.HeadSha,
            new SandboxCommandResult(0, "1\n", string.Empty),
            new SandboxCommandResult(SigkillExit, string.Empty, string.Empty)
        );
        var preparer = new ReviewSlotPreparer(
            new GitRunner(runner),
            SeedGitmodules(slot.StorePath),
            "github",
            NullLoggerFactory.Instance
        );

        var prepared = await preparer.PrepareAsync(
            slot,
            run,
            StoreUrl,
            SubmoduleRelPath,
            Branch,
            DefaultBranch,
            NotesRelPath,
            BuildPolicy(),
            CancellationToken.None
        );

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
    public async Task PrepareAsync_SaysSomethingDifferentWhenTheCountWasKilledThanWhenTheHistoryIsExhausted()
    {
        var exhausted = await GiveUpWarningAsync(new SandboxCommandResult(0, "100\n", string.Empty));
        var killed = await GiveUpWarningAsync(new SandboxCommandResult(WatchdogKillExit, string.Empty, KilledStderr));

        exhausted
            .Should()
            .Contain(
                "unrelated histories",
                "the measured run reached real roots on both walks, which is a fact about the commits and is "
                    + "allowed to be stated as one"
            );
        killed
            .Should()
            .NotContain(
                "unrelated histories",
                "the killed run established nothing about the commits, and this phrase is the one that ends up "
                    + "in a pull request telling the author to re-target or rebase"
            );
        killed
            .Should()
            .NotBe(
                exhausted,
                "if our infrastructure failure and the author's unrelated branch read the same, the distinction "
                    + "exists only in an enum nobody reads"
            );
        killed
            .Should()
            .Contain("UNKNOWN", "and the line has to name what it could not establish, not merely omit the claim");
    }

    /// <summary>
    /// Runs the climb to its give-up and returns the single warning that explains why it stopped. Round one
    /// measures base at 1 and fetches; <paramref name="secondBaseCount"/> is what round two's count of base
    /// comes back as, and head is flat throughout — so round two is where the loop decides, and that result
    /// is the only thing the caller varies.
    /// </summary>
    private async Task<string> GiveUpWarningAsync(SandboxCommandResult secondBaseCount)
    {
        var slot = CreateSlot();
        var run = CreateRun();
        using var logs = new CapturingLoggerFactory();
        var target = $"{slot.StorePath}/{SubmoduleRelPath}";
        var runner = ShallowCloneCountingBaseAs(
            target,
            run.BaseSha,
            run.HeadSha,
            new SandboxCommandResult(0, "1\n", string.Empty),
            secondBaseCount
        );
        var preparer = new ReviewSlotPreparer(new GitRunner(runner), SeedGitmodules(slot.StorePath), "github", logs);

        _ = await preparer.PrepareAsync(
            slot,
            run,
            StoreUrl,
            SubmoduleRelPath,
            Branch,
            DefaultBranch,
            NotesRelPath,
            BuildPolicy(),
            CancellationToken.None
        );

        // "deepening" is the word both give-up lines share and no other warning on this path uses, so it
        // selects the line under test without presuming which of the two was written.
        return logs
            .Capturing.MessagesAtLevel(LogLevel.Warning)
            .Where(m => m.Contains("deepening", StringComparison.Ordinal))
            .Should()
            .ContainSingle(
                "the climb stops once and says why once; two lines here would mean the assertion below is "
                    + "comparing an arbitrary one of them"
            )
            .Subject;
    }

    /// <summary>
    /// The happy path, and the proof that the climb is not merely a way of arriving at a verdict. A shallow
    /// checkout whose base sits on the graft root has no merge base until history is fetched for it; one
    /// deepening round buys that history, and the RE-ASK after the fetch is what turns it into a reviewable
    /// pull request. Without that re-ask the loop runs to the ceiling and the PR is never reviewed at all.
    /// </summary>
    [Fact]
    public async Task PrepareAsync_DeepensAShallowCheckoutAndResolvesOnceTheFetchBuysTheHistory()
    {
        var slot = CreateSlot();
        var run = CreateRun();
        var target = $"{slot.StorePath}/{SubmoduleRelPath}";
        var runner = new FakeSandboxCommandRunner()
            .OnArgvContainsSequence(
                $"-C {target} merge-base",
                new SandboxCommandResult(1, string.Empty, string.Empty),
                new SandboxCommandResult(0, "d34db33f\n", string.Empty)
            )
            .OnArgvContains(
                $"-C {target} rev-parse --is-shallow-repository",
                new SandboxCommandResult(0, "true\n", string.Empty)
            )
            .OnArgvContains(
                $"-C {target} rev-list --count {run.BaseSha}",
                new SandboxCommandResult(0, "1\n", string.Empty)
            )
            .OnArgvContains(
                $"-C {target} rev-list --count {run.HeadSha}",
                new SandboxCommandResult(0, "34579\n", string.Empty)
            );
        var preparer = new ReviewSlotPreparer(
            new GitRunner(runner),
            SeedGitmodules(slot.StorePath),
            "github",
            NullLoggerFactory.Instance
        );

        var prepared = await preparer.PrepareAsync(
            slot,
            run,
            StoreUrl,
            SubmoduleRelPath,
            Branch,
            DefaultBranch,
            NotesRelPath,
            BuildPolicy(),
            CancellationToken.None
        );

        prepared
            .MergeBase.Should()
            .Be(
                MergeBaseOutcome.Resolved,
                "the deepening fetch brought base its ancestry, and the re-ask after the fetch is what observes it"
            );
        runner
            .Commands.Select(c => string.Join(' ', c.Argv))
            .Should()
            .Contain(
                a => a.Contains($"-C {target} fetch --depth=100 origin {run.BaseSha}", StringComparison.Ordinal),
                "only the truncated commit is named: `--depth` shortens exactly the refs a fetch names, so naming "
                    + "the whole head would slice away the history the merge base is hiding in"
            );
    }

    /// <summary>
    /// The other end of the same guarantee: a checkout that already has a merge base is not deepened at all.
    /// A fetch here is not merely wasted — on the live store it is gigabytes, and it is issued on the one path
    /// where nothing was wrong.
    /// </summary>
    [Fact]
    public async Task PrepareAsync_DoesNotDeepenWhenTheMergeBaseIsAlreadyReachable()
    {
        var slot = CreateSlot();
        var run = CreateRun();
        var target = $"{slot.StorePath}/{SubmoduleRelPath}";
        var runner = new FakeSandboxCommandRunner().OnArgvContains(
            $"-C {target} merge-base",
            new SandboxCommandResult(0, "d34db33f\n", string.Empty)
        );
        var preparer = new ReviewSlotPreparer(
            new GitRunner(runner),
            SeedGitmodules(slot.StorePath),
            "github",
            NullLoggerFactory.Instance
        );

        var prepared = await preparer.PrepareAsync(
            slot,
            run,
            StoreUrl,
            SubmoduleRelPath,
            Branch,
            DefaultBranch,
            NotesRelPath,
            BuildPolicy(),
            CancellationToken.None
        );

        prepared.MergeBase.Should().Be(MergeBaseOutcome.Resolved);
        runner
            .Commands.Select(c => string.Join(' ', c.Argv))
            .Should()
            .NotContain(
                a => a.Contains("fetch --depth=", StringComparison.Ordinal),
                "the question was already answered, so no history needed buying"
            );
    }

    /// <summary>
    /// The runner shape the object-store-maintenance pair shares: a shallow checkout whose base sits on the
    /// graft root, so the climb issues exactly one deepening fetch and then resolves on the re-ask. Extracted
    /// so the ONLY difference between the two tests below is the flag passed to the preparer.
    /// </summary>
    private static FakeSandboxCommandRunner ShallowCloneResolvingAfterOneDeepening(string target, ReviewRun run) =>
        new FakeSandboxCommandRunner()
            .OnArgvContainsSequence(
                $"-C {target} merge-base",
                new SandboxCommandResult(1, string.Empty, string.Empty),
                new SandboxCommandResult(0, "d34db33f\n", string.Empty)
            )
            .OnArgvContains(
                $"-C {target} rev-parse --is-shallow-repository",
                new SandboxCommandResult(0, "true\n", string.Empty)
            )
            .OnArgvContains(
                $"-C {target} rev-list --count {run.BaseSha}",
                new SandboxCommandResult(0, "1\n", string.Empty)
            )
            .OnArgvContains(
                $"-C {target} rev-list --count {run.HeadSha}",
                new SandboxCommandResult(0, "34579\n", string.Empty)
            );

    /// <summary>
    /// A <c>--depth</c> fetch re-asks from the TIP, so each round of the climb brings the tip's whole tree
    /// closure down again — measured live as four packs of 7.2-7.7 GB holding the same object set four times
    /// over. The repack that collapses it must run INSIDE the loop, between this round's fetch and the next
    /// one, or the peak it is meant to bound has already been reached by the time it runs.
    /// <para>
    /// <c>--keep-unreachable</c> is the correctness half and is asserted literally. The PR's base and head
    /// arrive by raw SHA with nothing but <c>FETCH_HEAD</c> pointing at them, and repack's reachability walk
    /// does not treat <c>FETCH_HEAD</c> as a root — a plain <c>repack -a -d</c> here DELETES the base commit
    /// the deepening was just paid for, and every subsequent review of that store fails to diff.
    /// </para>
    /// </summary>
    [Fact]
    public async Task PrepareAsync_RepacksKeepingUnreachableBetweenTheDeepeningFetchAndTheReAsk()
    {
        var slot = CreateSlot();
        var run = CreateRun();
        var target = $"{slot.StorePath}/{SubmoduleRelPath}";
        var runner = ShallowCloneResolvingAfterOneDeepening(target, run);
        var preparer = new ReviewSlotPreparer(
            new GitRunner(runner),
            SeedGitmodules(slot.StorePath),
            "github",
            NullLoggerFactory.Instance,
            enableObjectStoreMaintenance: true
        );

        var prepared = await preparer.PrepareAsync(
            slot,
            run,
            StoreUrl,
            SubmoduleRelPath,
            Branch,
            DefaultBranch,
            NotesRelPath,
            BuildPolicy(),
            CancellationToken.None
        );

        prepared.MergeBase.Should().Be(MergeBaseOutcome.Resolved);
        var argv = runner.Commands.Select(c => string.Join(' ', c.Argv)).ToList();

        var repackIndex = argv.FindIndex(a =>
            a.Contains($"-C {target} repack -a -d --keep-unreachable", StringComparison.Ordinal)
        );
        repackIndex
            .Should()
            .BeGreaterThan(
                -1,
                "without --keep-unreachable the repack drops the PR's base commit outright, so the flag is not a "
                    + "tuning detail that may drift — it is the whole reason this command is safe to issue"
            );

        var fetchIndex = argv.FindIndex(a => a.Contains($"-C {target} fetch --depth=100", StringComparison.Ordinal));
        fetchIndex.Should().BeGreaterThan(-1, "the fixture is a shallow clone that needs one deepening round");
        repackIndex
            .Should()
            .BeGreaterThan(fetchIndex, "there is nothing to collapse until the fetch that duplicated the pack has run");

        var reAskIndex = argv.FindIndex(
            fetchIndex + 1,
            a => a.Contains($"-C {target} merge-base", StringComparison.Ordinal)
        );
        reAskIndex.Should().BeGreaterThan(-1, "the climb re-asks after every deepening round");
        repackIndex
            .Should()
            .BeLessThan(
                reAskIndex,
                "the collapse belongs inside the round that created the duplicate — hoisted out to after the "
                    + "climb it would run only once the four coexisting packs it exists to prevent are already "
                    + "on disk"
            );
    }

    /// <summary>
    /// The default, and the one that is not a tuning choice: the owner of these machines instructed that local
    /// git packs not be touched, and <c>repack</c> rewrites an object store in place under a directory the
    /// daemon does not own. Off means no repack is issued at all — not a smaller one.
    /// </summary>
    [Fact]
    public async Task PrepareAsync_DoesNotTouchLocalPacksWhenObjectStoreMaintenanceIsOff()
    {
        var slot = CreateSlot();
        var run = CreateRun();
        var target = $"{slot.StorePath}/{SubmoduleRelPath}";
        var runner = ShallowCloneResolvingAfterOneDeepening(target, run);
        var preparer = new ReviewSlotPreparer(
            new GitRunner(runner),
            SeedGitmodules(slot.StorePath),
            "github",
            NullLoggerFactory.Instance
        );

        var prepared = await preparer.PrepareAsync(
            slot,
            run,
            StoreUrl,
            SubmoduleRelPath,
            Branch,
            DefaultBranch,
            NotesRelPath,
            BuildPolicy(),
            CancellationToken.None
        );

        var argv = runner.Commands.Select(c => string.Join(' ', c.Argv)).ToList();
        argv.Should()
            .Contain(
                a => a.Contains($"-C {target} fetch --depth=100", StringComparison.Ordinal),
                "the deepening itself is not gated — only the housekeeping that follows it is, so this asserts "
                    + "the absence below is a decision rather than a path that never ran"
            );
        prepared.MergeBase.Should().Be(MergeBaseOutcome.Resolved);
        argv.Should()
            .NotContain(
                a => a.Contains("repack", StringComparison.Ordinal),
                "default-off is the requirement, and the accepted cost is that the store keeps the duplicate pack"
            );
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

    private static ReviewRun CreateRun() =>
        new()
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
            new RepoIdentity
            {
                Provider = "github",
                OrgOrOwner = "achieveai",
                RepoName = "LmDotnetTools",
            },
            reviewBotRepoUrl: null,
            allowWriteOperations: false,
            allowedSubmodules: [new SubmoduleAllowRule("github.com", "/achieveai/LmDotnetTools")]
        );

    /// <summary>Seeds a <c>.gitmodules</c> at the store root declaring the reviewed submodule, so
    /// <see cref="ReviewSlotPreparer"/>'s reused <c>SubmoduleInitializer</c> logic inits it.</summary>
    private static FakeSandboxFileSystem SeedGitmodules(string storeRoot)
    {
        var fileSystem = new FakeSandboxFileSystem();
        fileSystem.Seed(
            $"{storeRoot}/.gitmodules",
            "[submodule \"LmDotnetTools\"]\n\tpath = repos/LmDotnetTools\n\turl = https://github.com/achieveai/LmDotnetTools.git\n"
        );
        return fileSystem;
    }
}
