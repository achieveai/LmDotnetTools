using System.Diagnostics;
using System.Runtime.Versioning;
using AchieveAi.LmDotnetTools.LmTestUtils;
using AchieveAi.LmDotnetTools.LmTestUtils.Logging;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using CodeReviewDaemon.Sample.Workspace.Sandbox;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeReviewDaemon.Sample.Tests.Workspace;

/// <summary>
/// The sweep that removes the <c>tmp_pack_*</c> files git abandons when a fetch is killed mid-write.
///
/// This exists because those orphans took a machine down: 35 of them, 245.35 GB, in one submodule's pack
/// directory — enough to grow the WSL disk image past the host volume and force a read-only remount. Git
/// never reclaims them inside any useful window, because <c>gc</c> only prunes stale temp files past
/// <c>gc.pruneExpire</c>, which defaults to two weeks.
///
/// The tests that matter here are the NEGATIVE ones. Deleting our own abandoned pack is easy; the way this
/// fix could do real harm is by deleting a pack some other fetch is still writing, or a real pack that
/// merely looks temporary. Those cases carry the weight, not the happy path.
///
/// The open-descriptor half of the guard reads <c>/proc</c>, which exists only on Linux, so those tests are
/// <see cref="LinuxOnlyFactAttribute"/> rather than an early <c>return</c>. That is not cosmetic: an early
/// return reports a PASS, so on a Windows dev box — and on this repository's <c>windows-latest</c> CI leg —
/// the branch's original form claimed coverage it never ran. A skip says so.
/// </summary>
public sealed class OrphanedPackSweeperTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "orphan-pack-sweeper-" + Guid.NewGuid().ToString("N")
    );

    public void Dispose() => RealGitFixture.ForceDelete(_root);

    [Fact]
    public void A_temp_pack_written_after_the_snapshot_is_removed()
    {
        var repo = NewRepo();
        var packDirectory = PackDirectory(repo);

        var before = OrphanedPackSweeper.Snapshot(repo);
        var abandoned = WriteFile(packDirectory, "tmp_pack_ZuFR97", sizeBytes: 4096);

        var (files, bytes) = OrphanedPackSweeper.SweepNew(repo, before, NullLogger.Instance);

        files.Should().Be(1);
        bytes.Should().Be(4096);
        File.Exists(abandoned).Should().BeFalse();
    }

    [Fact]
    public void A_temp_pack_that_existed_before_the_snapshot_is_left_alone()
    {
        // THE SAFETY PROPERTY. A temp pack already on disk when we started is not ours — the likeliest
        // owner is another fetch still writing it. Age cannot make this call: the file we abandon is always
        // the YOUNGEST in the directory, so an "older than N" rule would both spare our orphan and, given a
        // long enough stall, delete a stranger's live write. Membership in the pre-command snapshot is the
        // only discriminator that gets both directions right.
        var repo = NewRepo();
        var packDirectory = PackDirectory(repo);
        var someoneElses = WriteFile(packDirectory, "tmp_pack_LIVE01", sizeBytes: 2048);

        var before = OrphanedPackSweeper.Snapshot(repo);
        var ours = WriteFile(packDirectory, "tmp_pack_OURS01", sizeBytes: 512);

        var (files, bytes) = OrphanedPackSweeper.SweepNew(repo, before, NullLogger.Instance);

        files.Should().Be(1);
        bytes.Should().Be(512);
        File.Exists(ours).Should().BeFalse();
        File.Exists(someoneElses).Should().BeTrue("a temp pack predating the command belongs to someone else");
    }

    [Fact]
    public void Real_packs_are_never_touched()
    {
        // The standing instruction is that local git packs are not to be touched. That is upheld here by
        // construction rather than by care: nothing without the tmp_ prefix is a candidate, even when it is
        // created inside the swept window.
        var repo = NewRepo();
        var packDirectory = PackDirectory(repo);

        var before = OrphanedPackSweeper.Snapshot(repo);
        var pack = WriteFile(packDirectory, "pack-0123456789abcdef.pack", sizeBytes: 1024);
        var index = WriteFile(packDirectory, "pack-0123456789abcdef.idx", sizeBytes: 64);
        var keep = WriteFile(packDirectory, "pack-0123456789abcdef.keep", sizeBytes: 1);

        var (files, _) = OrphanedPackSweeper.SweepNew(repo, before, NullLogger.Instance);

        files.Should().Be(0);
        File.Exists(pack).Should().BeTrue();
        File.Exists(index).Should().BeTrue();
        File.Exists(keep).Should().BeTrue();
    }

    [Fact]
    public void A_read_only_temp_pack_is_removed()
    {
        // git creates these 0444. The surviving orphan measured on the affected machine was `-r--r--r--`,
        // 2.98 GB. On Linux the unlink permission comes from the directory so this passes either way; on
        // Windows it would not, and a sweep that silently skipped every file it was written for is the
        // failure this pins.
        var repo = NewRepo();
        var packDirectory = PackDirectory(repo);

        var before = OrphanedPackSweeper.Snapshot(repo);
        var abandoned = WriteFile(packDirectory, "tmp_pack_RDONLY", sizeBytes: 128);
        new FileInfo(abandoned).IsReadOnly = true;

        var (files, _) = OrphanedPackSweeper.SweepNew(repo, before, NullLogger.Instance);

        files.Should().Be(1);
        File.Exists(abandoned).Should().BeFalse();
    }

    [Fact]
    public void A_submodule_object_store_is_swept()
    {
        // WHERE THE LEAK ACTUALLY WAS. All 245.35 GB sat under `.git/modules/repos/Nova/objects/pack`, not
        // in the top-level store, so a sweep that only looked at the repository's own pack directory would
        // have found nothing and reported success.
        var repo = NewRepo();
        var submodulePack = Path.Combine(repo, ".git", "modules", "repos", "Nova", "objects", "pack");
        _ = Directory.CreateDirectory(submodulePack);

        var before = OrphanedPackSweeper.Snapshot(repo);
        var abandoned = WriteFile(submodulePack, "tmp_pack_NESTED", sizeBytes: 256);

        var (files, bytes) = OrphanedPackSweeper.SweepNew(repo, before, NullLogger.Instance);

        files.Should().Be(1);
        bytes.Should().Be(256);
        File.Exists(abandoned).Should().BeFalse();
    }

    [Fact]
    public void A_git_directory_reached_through_a_dot_git_FILE_is_swept()
    {
        // Submodule working trees carry `.git` as a file containing `gitdir: <path>`, which is the shape the
        // leaking store had. Resolving it without shelling out to git is deliberate — the git process we
        // would ask has just been killed.
        var work = Path.Combine(_root, "worktree");
        var realGitDirectory = Path.Combine(_root, "real-git-dir");
        var packDirectory = Path.Combine(realGitDirectory, "objects", "pack");
        _ = Directory.CreateDirectory(work);
        _ = Directory.CreateDirectory(packDirectory);
        File.WriteAllText(Path.Combine(work, ".git"), $"gitdir: {realGitDirectory}\n");

        var before = OrphanedPackSweeper.Snapshot(work);
        var abandoned = WriteFile(packDirectory, "tmp_pack_VIAFILE", sizeBytes: 32);

        var (files, _) = OrphanedPackSweeper.SweepNew(work, before, NullLogger.Instance);

        files.Should().Be(1);
        File.Exists(abandoned).Should().BeFalse();
    }

    [Fact]
    public void A_temp_file_outside_a_pack_directory_is_left_alone()
    {
        // The walk deliberately never enters the loose-object fan-out — on the live NOVA store that is
        // 970,000 files, read on a failure path. A tmp_-prefixed file sitting there is therefore both out of
        // scope and unreachable, and this pins that it stays unreachable rather than being found by a later
        // "make the search more thorough" change.
        var repo = NewRepo();
        var looseDirectory = Path.Combine(repo, ".git", "objects", "ab");
        _ = Directory.CreateDirectory(looseDirectory);

        var before = OrphanedPackSweeper.Snapshot(repo);
        var stray = WriteFile(looseDirectory, "tmp_pack_STRAY", sizeBytes: 16);

        var (files, _) = OrphanedPackSweeper.SweepNew(repo, before, NullLogger.Instance);

        files.Should().Be(0);
        File.Exists(stray).Should().BeTrue();
    }

    [Fact]
    public void A_working_directory_that_is_not_a_repository_sweeps_nothing()
    {
        var plain = Path.Combine(_root, "not-a-repo");
        _ = Directory.CreateDirectory(plain);

        var before = OrphanedPackSweeper.Snapshot(plain);
        var (files, bytes) = OrphanedPackSweeper.SweepNew(plain, before, NullLogger.Instance);

        before.Should().BeEmpty();
        files.Should().Be(0);
        bytes.Should().Be(0);
    }

    [Fact]
    public void A_missing_working_directory_sweeps_nothing_rather_than_throwing()
    {
        // Housekeeping on a failure path must never introduce a second failure — the command that got us
        // here has already gone wrong, and the sweep runs while a cancellation is in flight.
        var missing = Path.Combine(_root, "gone");

        var before = OrphanedPackSweeper.Snapshot(missing);
        var act = () => OrphanedPackSweeper.SweepNew(missing, before, NullLogger.Instance);

        act.Should().NotThrow();
        act().Files.Should().Be(0);
    }

    [LinuxOnlyFact("the open-handle guard reads /proc/<pid>/fd, which has no equivalent elsewhere")]
    public void A_temp_pack_this_very_process_holds_open_is_never_deleted()
    {
        // The open-handle guard, proved without git and without signals, so the property has one test that
        // cannot be flaky. The file is created AFTER the snapshot, which is precisely the case the snapshot
        // rule alone gets WRONG: on membership it is a deletion candidate, and only the live descriptor
        // saves it. Then the handle is released and the identical file is swept, so this pins the guard
        // rather than merely pinning "nothing gets deleted".
        var repo = NewRepo();
        var packDirectory = PackDirectory(repo);

        var before = OrphanedPackSweeper.Snapshot(repo);
        var path = Path.Combine(packDirectory, "tmp_pack_HELDBY");

        using (var held = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite))
        {
            held.Write(new byte[4096]);
            held.Flush();

            var (files, bytes) = OrphanedPackSweeper.SweepNew(repo, before, NullLogger.Instance);

            files.Should().Be(0, "the file is open, so it is somebody's live write and not our orphan");
            bytes.Should().Be(0);
            File.Exists(path).Should().BeTrue();
        }

        // Handle released — same path, same snapshot, opposite outcome.
        var after = OrphanedPackSweeper.SweepNew(repo, before, NullLogger.Instance);

        after.Files.Should().Be(1, "once nothing holds it, it is an orphan and must go");
        after.Bytes.Should().Be(4096);
        File.Exists(path).Should().BeFalse();
    }

    [LinuxOnlyFact("freezing a real fetch mid-write needs SIGSTOP and a /proc descendant walk")]
    public void A_killed_fetch_leaves_the_pack_directory_exactly_as_it_found_it()
    {
        // END TO END, with a real git killed mid-write — the synthetic tests above all place the temp pack
        // by hand, so none of them proves that what git actually abandons is what this sweep actually
        // matches. The remote is a file:// URL rather than a plain path ON PURPOSE: given a path, git
        // HARDLINKS the object store instead of transferring it, no pack is ever written, and a test built
        // that way passes while exercising nothing.
        using var lab = GitLab.Create(_root);

        var before = OrphanedPackSweeper.Snapshot(lab.Store);
        var beforeCount = TempPackCount(lab.Store);

        using var fetch = lab.StartFetchAndFreezeWhileWriting();
        fetch.KillTree();

        var (files, bytes) = OrphanedPackSweeper.SweepNew(lab.Store, before, NullLogger.Instance);

        files.Should().Be(1, "the killed fetch abandoned exactly one temp pack");
        bytes.Should().BeGreaterThan(0, "a reclaim of zero bytes would mean the pack was never being written");
        TempPackCount(lab.Store).Should().Be(beforeCount, "the pack directory must be returned to its pre-fetch state");
    }

    [LinuxOnlyFact("the live-writer case is only distinguishable through /proc/<pid>/fd")]
    public void A_temp_pack_a_LIVE_fetch_is_still_writing_is_never_deleted()
    {
        // THE TEST THAT MATTERS. Everything else here risks leaving disk behind; this is the case where the
        // fix could do real harm, by deleting a pack a healthy concurrent fetch is in the middle of writing.
        // The live file is created AFTER the snapshot on purpose, so the snapshot rule does NOT spare it: if
        // this passes, it passes because the open descriptor was seen, which is the only thing that can save
        // it. Widen the sweep past that guard and this goes red.
        using var lab = GitLab.Create(_root);

        var before = OrphanedPackSweeper.Snapshot(lab.Store);

        // Frozen mid-write: the file exists, has real bytes, and its writer still holds the descriptor.
        using var live = lab.StartFetchAndFreezeWhileWriting();
        before
            .Should()
            .NotContain(
                live.TempPack,
                "the live pack must post-date the snapshot, or this test proves nothing about the guard"
            );

        var (files, bytes) = OrphanedPackSweeper.SweepNew(lab.Store, before, NullLogger.Instance);

        File.Exists(live.TempPack)
            .Should()
            .BeTrue("a temp pack another process still holds open belongs to a live fetch, not to us");
        files.Should().Be(0);
        bytes.Should().Be(0);
    }

    [LinuxOnlyFact("the paired live/abandoned control needs SIGSTOP, SIGCONT and /proc")]
    public void A_real_temp_pack_becomes_sweepable_only_once_its_writer_dies()
    {
        // The two tests above are only meaningful as a pair, and only if NOTHING but the writer's fate
        // differs between them. Same lab, same fetch, same file, same snapshot — swept twice, with a kill in
        // between. This is what rules out the live case passing for an unrelated reason, such as no temp
        // pack existing at all, which would make that negative assertion vacuous rather than true.
        using var lab = GitLab.Create(_root);

        var before = OrphanedPackSweeper.Snapshot(lab.Store);
        using var fetch = lab.StartFetchAndFreezeWhileWriting();

        new FileInfo(fetch.TempPack)
            .Length.Should()
            .BeGreaterThan(
                0,
                "the fetch must genuinely be writing a pack, or neither this test nor its pair means anything"
            );

        var whileLive = OrphanedPackSweeper.SweepNew(lab.Store, before, NullLogger.Instance);
        whileLive.Files.Should().Be(0, "the frozen writer still holds the descriptor");
        File.Exists(fetch.TempPack).Should().BeTrue();

        fetch.KillTree();
        var whenAbandoned = OrphanedPackSweeper.SweepNew(lab.Store, before, NullLogger.Instance);

        whenAbandoned.Files.Should().Be(1, "the identical file becomes sweepable the moment its writer dies");
        whenAbandoned.Bytes.Should().BeGreaterThan(0);
        File.Exists(fetch.TempPack).Should().BeFalse();
    }

    [LinuxOnlyFact("the descriptor race this pins is only visible through /proc/<pid>/fd")]
    [SupportedOSPlatform("linux")] // The attribute above is what enforces this; CA1416 cannot read it.
    public async Task A_fetch_the_watchdog_kills_sweeps_the_pack_its_dying_index_pack_still_held()
    {
        // THE INTEGRATION CASE, and the one every test above structurally cannot reach: they all call
        // SweepNew directly, after their own kill has been waited out. Production sweeps on the line after
        // Kill(entireProcessTree: true), and a descriptor is released as the kernel tears the process down
        // rather than when the signal lands. index-pack is a GRANDCHILD — reparented to init when git dies,
        // reaped there, not by us — so waiting on our own Process is not enough either. Sweep too early and
        // /proc still shows the holder, the guard spares the file, the sweep reports nothing, and the
        // multi-gigabyte leak looks in the log exactly like the guard doing its job.
        //
        // Driven through RunAsync end to end on purpose: the kill has to come from the watchdog, on the real
        // path, or this proves nothing about production. Nothing here is timing-guessed — the remote stalls
        // and stays stalled, so the idle timeout is what fires.
        using var lab = GitLab.Create(_root);
        lab.StallTheRemoteMidPack();

        var logger = new CapturingLogger<HostGitCommandRunner>();
        var runner = new HostGitCommandRunner(
            _ => Task.FromResult<IReadOnlyList<GitProviderToken>>([]),
            logger,
            adoOrgs: null,
            idleTimeout: TimeSpan.FromSeconds(3),
            maxDuration: TimeSpan.FromSeconds(90)
        );

        var result = await runner.RunAsync(new SandboxCommand(["git", "fetch", "origin"], lab.Store), default);

        result
            .ExitCode.Should()
            .Be(124, "the stalled fetch must be killed by the idle watchdog rather than finish or hang");

        // The load-bearing assertion. "Removed N" can only be logged when the sweep actually took a file,
        // so this fails both ways it can be wrong: it goes red if the guard was consulted too early and
        // spared the orphan, and it goes red if no temp pack was ever written and the whole scenario was
        // vacuous. The alternative log line — "swept NOTHING" — is what the unfixed code produces here.
        logger
            .MessagesAtLevel(LogLevel.Warning)
            .Should()
            .ContainMatch(
                "*had abandoned*temporary pack file(s)*",
                "the killed fetch left a half-written pack and the sweep must have taken it; a "
                    + "'swept NOTHING' line here means the descriptor was still held when the sweep ran"
            );

        Directory
            .GetFiles(PackDirectory(lab.Store), "tmp_pack_*")
            .Should()
            .BeEmpty("the pack directory must be returned to its pre-fetch state");
    }

    [LinuxOnlyFact("holding a descriptor across the kill is only observable through /proc/<pid>/fd")]
    [SupportedOSPlatform("linux")] // The attribute above is what enforces this; CA1416 cannot read it.
    public async Task The_sweep_waits_for_a_pack_still_held_at_kill_time_instead_of_giving_up_on_it()
    {
        // THE MUTATION-PROOF HALF, and the reason it does not simply reuse the test above. What the fix adds
        // is a bounded WAIT between the kill and the sweep, and the test above cannot prove it: measured,
        // that test stays green with the wait collapsed to zero, because at this fixture's scale index-pack
        // is reaped inside the time Kill() itself takes to walk the tree. A green mutation names an untested
        // claim rather than a safe one, so the window is made deterministic here instead of raced for.
        //
        // A descriptor held by THIS process across the kill is that window, at a duration the test chooses:
        // opened while the fetch is still writing, released about a second and a half after the watchdog
        // fires, and well inside the five-second deadline. Sweep without waiting and the pack is spared and
        // reported unswept; wait, and the same pack is taken the moment its holder lets go. The holder is a
        // stand-in for a dying index-pack and is honest about being one — it is indistinguishable to the
        // guard, which knows only "some process has this open".
        using var lab = GitLab.Create(_root);
        lab.StallTheRemoteMidPack();

        var packDirectory = PackDirectory(lab.Store);
        var opened = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var holder = Task.Run(() => HoldTheFirstTempPack(packDirectory, TimeSpan.FromSeconds(4), opened));

        var logger = new CapturingLogger<HostGitCommandRunner>();
        var runner = new HostGitCommandRunner(
            _ => Task.FromResult<IReadOnlyList<GitProviderToken>>([]),
            logger,
            adoOrgs: null,
            idleTimeout: TimeSpan.FromSeconds(3),
            maxDuration: TimeSpan.FromSeconds(90)
        );

        var result = await runner.RunAsync(new SandboxCommand(["git", "fetch", "origin"], lab.Store), default);
        await holder;

        result.ExitCode.Should().Be(124, "the stalled fetch must be killed by the idle watchdog");

        // Non-vacuity: without this the test could pass having held nothing, which is the same run as the
        // one above and proves nothing about waiting.
        var pack = await opened.Task;
        pack.Should().StartWith(packDirectory, "the held file must be the fetch's own temp pack");

        logger
            .MessagesAtLevel(LogLevel.Warning)
            .Should()
            .ContainMatch(
                "*had abandoned*temporary pack file(s)*",
                "the sweep must wait out the descriptor and then take the pack; a 'swept NOTHING' line "
                    + "means it sampled /proc while the holder was still there and gave up"
            );

        File.Exists(pack).Should().BeFalse();
    }

    /// <summary>
    /// Opens the first temp pack to appear in <paramref name="packDirectory"/>, holds it for
    /// <paramref name="hold"/>, and reports which file it took. Polls rather than watches because the file is
    /// created by a process the test does not own and lives for a few seconds at most.
    /// </summary>
    private static void HoldTheFirstTempPack(string packDirectory, TimeSpan hold, TaskCompletionSource<string> opened)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            var candidate = Directory.Exists(packDirectory)
                ? Directory.GetFiles(packDirectory, "tmp_pack_*").FirstOrDefault()
                : null;

            if (candidate is not null)
            {
                try
                {
                    using var stream = new FileStream(
                        candidate,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete
                    );
                    _ = opened.TrySetResult(candidate);
                    Thread.Sleep(hold);
                }
                catch (Exception ex)
                {
                    _ = opened.TrySetException(ex);
                }

                return;
            }

            Thread.Sleep(5);
        }

        _ = opened.TrySetException(
            new InvalidOperationException(
                "the stalled fetch never created a temp pack, so there was nothing to hold and this test "
                    + "would have proved nothing about waiting for one."
            )
        );
    }

    private static int TempPackCount(string repo) =>
        Directory.Exists(PackDirectory(repo)) ? Directory.GetFiles(PackDirectory(repo), "tmp_pack_*").Length : 0;

    private string NewRepo()
    {
        var repo = Path.Combine(_root, "repo");
        _ = Directory.CreateDirectory(Path.Combine(repo, ".git", "objects", "pack"));
        return repo;
    }

    private static string PackDirectory(string repo) => Path.Combine(repo, ".git", "objects", "pack");

    private static string WriteFile(string directory, string name, int sizeBytes)
    {
        var path = Path.Combine(directory, name);
        File.WriteAllBytes(path, new byte[sizeBytes]);
        return path;
    }

    /// <summary>
    /// A throwaway origin/clone pair on disk, used by the tests that need a REAL git fetch rather than a
    /// hand-placed file. Linux-only, and reachable only from a <see cref="LinuxOnlyFactAttribute"/> test:
    /// it drives SIGSTOP/SIGCONT and reads <c>/proc</c>.
    /// <para>
    /// The remote is addressed as <c>file://</c> and not as a plain path, which is the whole reason this
    /// helper exists rather than a one-liner. Given a local PATH git treats the clone as a local one and
    /// HARDLINKS the object store instead of transferring it: no pack is negotiated, no <c>index-pack</c>
    /// runs, no <c>tmp_pack_*</c> is ever created, and every assertion below would hold vacuously against a
    /// fetch that moved nothing. What rules that out is not a link-count check but
    /// <see cref="StartFetchAndFreezeWhileWriting"/>, which THROWS if the fetch ever completes without a temp
    /// pack having appeared — a direct assertion on the thing these tests actually depend on.
    /// </para>
    /// </summary>
    private sealed class GitLab : IDisposable
    {
        /// <summary>
        /// Payload size. Large enough that the transfer lasts seconds — the freeze has to land while
        /// <c>index-pack</c> is mid-write, and the reaction window is sub-millisecond — small enough that
        /// three of these cost a couple of hundred MB.
        /// </summary>
        private const int BlobBytes = 6 * 1024 * 1024;
        private const int BlobCount = 4;

        private readonly string _root;

        private GitLab(string root, string store)
        {
            _root = root;
            Store = store;
        }

        /// <summary>The clone the tests fetch INTO, and the working directory handed to the sweeper.</summary>
        public string Store { get; }

        public static GitLab Create(string rootDirectory)
        {
            var root = Path.Combine(rootDirectory, "lab-" + Guid.NewGuid().ToString("N")[..8]);
            var origin = Path.Combine(root, "origin");
            var store = Path.Combine(root, "store");
            _ = Directory.CreateDirectory(root);

            Git(null, "init", "-q", origin);
            Git(origin, "config", "user.email", "lab@example.com");
            Git(origin, "config", "user.name", "Lab");
            Git(origin, "config", "commit.gpgsign", "false");

            // One tiny commit, cloned cheaply — the bulk arrives later so the FETCH is what has work to do.
            File.WriteAllText(Path.Combine(origin, "seed.txt"), "seed\n");
            Git(origin, "add", "seed.txt");
            Git(origin, "commit", "-qm", "seed");

            Git(null, "clone", "-q", $"file://{origin}", store);

            // Without this the fetch below writes NO PACK AT ALL, and every test depending on one fails for
            // a reason that has nothing to do with the sweeper. Git only runs `index-pack` when a transfer
            // carries more objects than `transfer.unpackLimit` (default 100); under that it explodes them
            // into loose objects instead, and a loose write creates no `tmp_pack_*`. A handful of commits is
            // far under the default, so the honest fix is to lower the limit rather than to manufacture a
            // hundred objects. Found by the fixture's own guard rather than reasoned about — the first run
            // reported "git fetch finished (exit 0) without a temp pack ever appearing", which is exactly
            // the vacuous-pass this fixture refuses to hand back.
            Git(store, "config", "transfer.unpackLimit", "1");
            Git(store, "config", "fetch.unpackLimit", "1");

            // Now the payload, incompressible so it cannot be packed down to nothing.
            var random = new Random(20260810);
            for (var i = 0; i < BlobCount; i++)
            {
                var buffer = new byte[BlobBytes];
                random.NextBytes(buffer);
                File.WriteAllBytes(Path.Combine(origin, $"blob{i}.bin"), buffer);
                Git(origin, "add", $"blob{i}.bin");
                Git(origin, "commit", "-qm", $"blob {i}");
            }

            return new GitLab(root, store);
        }

        /// <summary>
        /// Makes the NEXT fetch stall mid-pack and stay stalled, so a watchdog can kill it at a moment when
        /// <c>index-pack</c> is demonstrably alive and holding a half-written <c>tmp_pack_*</c>.
        /// <para>
        /// SIGSTOP is not usable for this. <see cref="StartFetchAndFreezeWhileWriting"/> owns the process it
        /// freezes; a fetch driven through <c>HostGitCommandRunner.RunAsync</c> is owned by the runner, and
        /// the test never gets a handle on it — the kill has to come from the watchdog or it is not the
        /// production path being tested. So the stall is arranged on the REMOTE side instead, where the test
        /// still has control: <c>upload-pack</c>'s stdout is passed through a reader that takes a fixed
        /// prefix and then stops reading. The pipe fills, the server blocks writing, the client's
        /// <c>index-pack</c> blocks reading with its temp pack open, and the client goes silent — which is
        /// exactly the shape of the production stall (<c>curl 56 Recv failure</c>) the idle timeout exists
        /// to catch. The prefix is large enough that <c>index-pack</c> has certainly created its temp file
        /// and far smaller than the payload, so the transfer cannot slip through and finish.
        /// </para>
        /// </summary>
        [SupportedOSPlatform("linux")]
        public void StallTheRemoteMidPack()
        {
            var script = Path.Combine(_root, "stalling-upload-pack.sh");
            File.WriteAllText(
                script,
                // `stdbuf -o0`, and it is load-bearing rather than defensive. upload-pack is INTERACTIVE
                // before it is bulk: it writes a ~300-byte ref advertisement and then waits on stdin for
                // the client's wants. Any reader that holds those 300 bytes rather than passing them
                // through deadlocks the exchange before a single pack byte exists — the client never gets
                // the advertisement, so it never answers, so no pack is ever sent. Both natural spellings
                // do exactly that, and both were measured doing it here (fetch silent from 0.1s, killed
                // with NO temp pack, so the test failed for a reason unrelated to the sweep):
                // `dd bs=4096 count=64 iflag=fullblock` blocks waiting to fill its first block, and plain
                // `head -c` writes through stdio, which is FULLY buffered when stdout is a pipe. Unbuffered
                // `head` forwards the advertisement immediately and then stops at the byte limit; `sleep`
                // inherits the pipe's read end and holds it open, so upload-pack blocks on its next write
                // once the kernel buffer fills. Verified: a 254 KB temp pack, and the transfer stopped
                // roughly two orders of magnitude short of the payload.
                "#!/bin/sh\n" + "git upload-pack \"$@\" | { stdbuf -o0 head -c 262144; sleep 60; }\n"
            );
            File.SetUnixFileMode(
                script,
                UnixFileMode.UserRead
                    | UnixFileMode.UserWrite
                    | UnixFileMode.UserExecute
                    | UnixFileMode.GroupRead
                    | UnixFileMode.GroupExecute
                    | UnixFileMode.OtherRead
                    | UnixFileMode.OtherExecute
            );

            Git(Store, "config", "remote.origin.uploadpack", script);
        }

        /// <summary>
        /// Starts <c>git fetch</c> against the lab remote and freezes it — and every process it spawned —
        /// the instant its temp pack has real bytes in it. SIGSTOP rather than a sleep or a timing guess:
        /// the file then stays open and half-written for as long as the test needs, so "a fetch is live
        /// right now" is a fact the test establishes rather than a race it hopes to win.
        /// </summary>
        public FrozenFetch StartFetchAndFreezeWhileWriting()
        {
            var packDirectory = Path.Combine(Store, ".git", "objects", "pack");
            var process = StartGit(Store, "fetch", "origin");

            try
            {
                var deadline = DateTime.UtcNow.AddSeconds(60);
                while (DateTime.UtcNow < deadline)
                {
                    var candidate = Directory.Exists(packDirectory)
                        ? Directory.GetFiles(packDirectory, "tmp_pack_*").FirstOrDefault()
                        : null;

                    if (candidate is not null && new FileInfo(candidate).Length > 0)
                    {
                        var frozen = FrozenFetch.Freeze(process, candidate);

                        // Between spotting the file and stopping the tree the fetch could in principle have
                        // finished. Verified rather than assumed: a vanished temp pack here would turn every
                        // assertion that follows into a statement about nothing.
                        File.Exists(candidate)
                            .Should()
                            .BeTrue("the fetch finished before it could be frozen; the payload is too small to catch");
                        return frozen;
                    }

                    if (process.HasExited)
                    {
                        throw new InvalidOperationException(
                            $"git fetch finished (exit {process.ExitCode}) without a temp pack ever appearing — "
                                + "the transfer moved nothing, so this fixture is not testing a real fetch."
                        );
                    }

                    Thread.Sleep(2);
                }

                throw new InvalidOperationException("git fetch never began writing a pack within 60s.");
            }
            catch
            {
                TrySignalTree(process, Signal.Cont);
                TryKill(process);
                throw;
            }
        }

        public void Dispose() => RealGitFixture.ForceDelete(_root);

        private static void Git(string? workingDirectory, params string[] arguments)
        {
            using var process = StartGit(workingDirectory, arguments);
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"git {string.Join(' ', arguments)} failed with {process.ExitCode}: "
                        + process.StandardError.ReadToEnd()
                );
            }
        }

        private static Process StartGit(string? workingDirectory, params string[] arguments)
        {
            var startInfo = new ProcessStartInfo("git")
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            // Keep the lab hermetic: no user config, no credential helper, no prompt to hang on.
            startInfo.Environment["GIT_CONFIG_GLOBAL"] = "/dev/null";
            startInfo.Environment["GIT_CONFIG_SYSTEM"] = "/dev/null";
            startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";

            return Process.Start(startInfo) ?? throw new InvalidOperationException("git could not be started.");
        }

        internal static void TryKill(Process process)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    _ = process.WaitForExit(5000);
                }
            }
            catch (InvalidOperationException)
            {
                // Already reaped.
            }
        }

        /// <summary>
        /// Sends a signal to the process AND every descendant. The parent alone is not enough: <c>git
        /// fetch</c> is not the process writing the pack — its <c>index-pack</c> child is — so stopping only
        /// the parent leaves the child running to completion and the "frozen" file finishes and disappears.
        /// </summary>
        internal static void TrySignalTree(Process process, Signal signal)
        {
            int rootPid;
            try
            {
                rootPid = process.Id;
                if (process.HasExited)
                {
                    return;
                }
            }
            catch (InvalidOperationException)
            {
                return;
            }

            // Deepest-first, so a parent cannot spawn a new child after its children were signalled.
            foreach (var pid in DescendantsOf(rootPid))
            {
                SendSignal(pid, signal);
            }

            SendSignal(rootPid, signal);
        }

        /// <summary>
        /// Sends one POSIX signal. Routed through the shell's <c>kill</c> builtin rather than P/Invoke
        /// because <c>LibraryImport</c> would require <c>AllowUnsafeBlocks</c> across the whole test project
        /// — a real change to every file in it, to gain nothing here. <see cref="Process.Kill()"/> cannot be
        /// used either: it only ever sends SIGKILL, and the entire point of this fixture is SIGSTOP.
        /// </summary>
        private static void SendSignal(int pid, Signal signal)
        {
            try
            {
                using var process = Process.Start(
                    new ProcessStartInfo("/bin/sh")
                    {
                        ArgumentList = { "-c", $"kill -{(int)signal} {pid} 2>/dev/null" },
                        UseShellExecute = false,
                    }
                );
                _ = process?.WaitForExit(5000);
            }
            catch (Exception)
            {
                // The process is already gone, which is the only outcome that matters and needs no action.
            }
        }

        /// <summary>
        /// Every descendant pid of <paramref name="rootPid"/>, read from <c>/proc</c> rather than shelled out
        /// to <c>pgrep</c> so the fixture depends on nothing beyond the kernel.
        /// </summary>
        private static List<int> DescendantsOf(int rootPid)
        {
            var parents = new Dictionary<int, int>();
            foreach (var directory in Directory.GetDirectories("/proc"))
            {
                var name = Path.GetFileName(directory);
                if (!int.TryParse(name, out var pid))
                {
                    continue;
                }

                try
                {
                    var stat = File.ReadAllText(Path.Combine(directory, "stat"));

                    // Field 2 is the executable name in parentheses and may itself contain spaces and
                    // parentheses, so the parse starts after the LAST ')' — splitting on whitespace from the
                    // left mis-reads any process whose name has a space in it.
                    var tail = stat[(stat.LastIndexOf(')') + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (tail.Length >= 2 && int.TryParse(tail[1], out var ppid))
                    {
                        parents[pid] = ppid;
                    }
                }
                catch (Exception)
                {
                    // The process exited mid-read, or is not ours to inspect.
                }
            }

            var found = new List<int>();
            var frontier = new Queue<int>();
            frontier.Enqueue(rootPid);
            while (frontier.Count > 0)
            {
                var current = frontier.Dequeue();
                foreach (var (pid, ppid) in parents)
                {
                    if (ppid == current && !found.Contains(pid) && pid != rootPid)
                    {
                        found.Add(pid);
                        frontier.Enqueue(pid);
                    }
                }
            }

            found.Reverse();
            return found;
        }
    }

    internal enum Signal
    {
        Kill = 9,
        Cont = 18,
        Stop = 19,
    }

    /// <summary>
    /// A fetch held motionless mid-pack, so a test can ask what the sweeper does about a temp pack whose
    /// writer is demonstrably still alive and still holding it open.
    /// </summary>
    private sealed class FrozenFetch : IDisposable
    {
        private readonly Process _process;
        private bool _killed;

        private FrozenFetch(Process process, string tempPack)
        {
            _process = process;
            TempPack = tempPack;
        }

        /// <summary>The in-progress <c>tmp_pack_*</c> the frozen fetch is part way through writing.</summary>
        public string TempPack { get; }

        public static FrozenFetch Freeze(Process process, string tempPack)
        {
            GitLab.TrySignalTree(process, Signal.Stop);
            return new FrozenFetch(process, tempPack);
        }

        /// <summary>
        /// Kills the frozen fetch the way the daemon's watchdog does, abandoning its pack. A stopped process
        /// must be continued first or SIGKILL is merely queued and the descriptor stays open — which would
        /// make the "abandoned" half of every paired test silently measure the "live" case instead.
        /// </summary>
        public void KillTree()
        {
            GitLab.TrySignalTree(_process, Signal.Cont);
            GitLab.TrySignalTree(_process, Signal.Kill);
            GitLab.TryKill(_process);
            _killed = true;

            // The descriptor is released by the kernel as the process is reaped, which is not synchronous
            // with the signal. Waiting here keeps "abandoned" a fact rather than a hope.
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline && StillOpen())
            {
                Thread.Sleep(10);
            }
        }

        private bool StillOpen()
        {
            foreach (var directory in Directory.GetDirectories("/proc"))
            {
                if (!int.TryParse(Path.GetFileName(directory), out _))
                {
                    continue;
                }

                try
                {
                    foreach (var descriptor in new DirectoryInfo(Path.Combine(directory, "fd")).GetFileSystemInfos())
                    {
                        if (string.Equals(descriptor.LinkTarget, TempPack, StringComparison.Ordinal))
                        {
                            return true;
                        }
                    }
                }
                catch (Exception)
                {
                    // Not readable, or gone.
                }
            }

            return false;
        }

        public void Dispose()
        {
            if (!_killed)
            {
                GitLab.TrySignalTree(_process, Signal.Cont);
                GitLab.TryKill(_process);
            }

            _process.Dispose();
        }
    }
}
