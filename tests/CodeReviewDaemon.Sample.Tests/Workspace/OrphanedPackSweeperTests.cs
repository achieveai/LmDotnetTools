using CodeReviewDaemon.Sample.Workspace.Sandbox;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeReviewDaemon.Sample.Tests.Workspace;

/// <summary>
/// The sweep that removes the <c>tmp_pack_*</c> files git abandons when a fetch is killed mid-write.
///
/// This exists because those orphans took the machine down: 35 of them, 245.35 GB, in one submodule's pack
/// directory — enough to grow the WSL disk image past the host volume and force a read-only remount. Git
/// never reclaims them inside any useful window, because <c>gc</c> only prunes stale temp files past
/// <c>gc.pruneExpire</c>, which defaults to two weeks.
///
/// The tests that matter here are the NEGATIVE ones. Deleting our own abandoned pack is easy; the way this
/// fix could do real harm is by deleting a pack some other fetch is still writing, or a real pack that
/// merely looks temporary. Those cases carry the weight, not the happy path.
/// </summary>
public sealed class OrphanedPackSweeperTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "orphan-pack-sweeper-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test run over.
        }
    }

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
        // The standing instruction on this machine is that local git packs are not to be touched. That is
        // upheld here by construction rather than by care: nothing without the tmp_ prefix is a candidate,
        // even when it is created inside the swept window.
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
        // git creates these 0444. The surviving orphan measured on this machine was `-r--r--r--`, 2.98 GB.
        // On Linux the unlink permission comes from the directory so this passes either way; on Windows it
        // would not, and a sweep that silently skipped every file it was written for is the failure this
        // pins.
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
}
