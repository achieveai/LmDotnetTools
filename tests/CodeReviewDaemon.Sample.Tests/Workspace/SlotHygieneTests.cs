using AchieveAi.LmDotnetTools.LmTestUtils;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using CodeReviewDaemon.Sample.Workspace;
using CodeReviewDaemon.Sample.Workspace.Git;
using CodeReviewDaemon.Sample.Workspace.Sandbox;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeReviewDaemon.Sample.Tests.Workspace;

/// <summary>
/// <see cref="SlotHygiene"/> is the clean-on-entry durability guarantee: it clears the stale state a crashed
/// prior lease leaves in a persistent pooled slot before the next review uses it. These tests pin the
/// filesystem effects (stale lock + in-progress-op removal — the exact 2026-07-12 incident) with real temp
/// dirs, and the git steps (reset/clean/submodule recursion + the re-clone verdict) via the recording
/// <see cref="FakeSandboxCommandRunner"/>, matching the established Workspace test harness.
/// </summary>
public sealed class SlotHygieneTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "crd-hygiene-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        DirectoryLink.UnlinkAllUnder(_root);
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // Best-effort cleanup only.
        }
    }

    [Fact]
    public async Task EnsureClean_removes_stale_locks_in_store_and_submodule_gitdirs()
    {
        var store = SeedStore();
        var storeLock = Path.Combine(store, ".git", "index.lock");
        var moduleDir = Path.Combine(store, ".git", "modules", "repos", "LmDotnetTools");
        Directory.CreateDirectory(moduleDir);
        var moduleLock = Path.Combine(moduleDir, "index.lock"); // the exact incident's lock location
        File.WriteAllText(storeLock, string.Empty);
        File.WriteAllText(moduleLock, string.Empty);

        var verdict = await SlotHygiene.EnsureCleanAsync(
            new GitRunner(new FakeSandboxCommandRunner()), store, CancellationToken.None);

        File.Exists(storeLock).Should().BeFalse("a stale store index.lock is cleared on entry");
        File.Exists(moduleLock).Should().BeFalse("a stale submodule-gitdir lock is cleared on entry");
        verdict.Should().Be(HygieneVerdict.Clean);
    }

    [Fact]
    public async Task EnsureClean_aborts_in_progress_merge_and_rebase()
    {
        var store = SeedStore();
        var gitDir = Path.Combine(store, ".git");
        File.WriteAllText(Path.Combine(gitDir, "MERGE_HEAD"), "deadbeef");
        Directory.CreateDirectory(Path.Combine(gitDir, "rebase-merge"));

        await SlotHygiene.EnsureCleanAsync(
            new GitRunner(new FakeSandboxCommandRunner()), store, CancellationToken.None);

        File.Exists(Path.Combine(gitDir, "MERGE_HEAD")).Should().BeFalse();
        Directory.Exists(Path.Combine(gitDir, "rebase-merge")).Should().BeFalse();
    }

    [Fact]
    public async Task EnsureClean_on_a_host_backed_store_clears_stale_state_without_posix_find()
    {
        // The host-backed pool keeps its store on the DAEMON HOST, where the runner is a local process runner
        // with no POSIX `find` — and the sweep ignores its result, so a silent failure there would hand the
        // reset a still-wedged store. Passing the host filesystem must route the sweep through the host helpers
        // instead. The runner is scripted to reject `find` so this cannot pass on the fake's POSIX emulation.
        var store = SeedStore();
        var gitDir = Path.Combine(store, ".git");
        var storeLock = Path.Combine(gitDir, "index.lock");
        File.WriteAllText(storeLock, string.Empty);
        File.WriteAllText(Path.Combine(gitDir, "MERGE_HEAD"), "deadbeef");
        Directory.CreateDirectory(Path.Combine(gitDir, "rebase-merge"));
        var runner = new FakeSandboxCommandRunner();
        runner.OnArgvContains("find ", new SandboxCommandResult(1, string.Empty, "'find' is not recognized"));

        var verdict = await SlotHygiene.EnsureCleanAsync(
            new GitRunner(runner), store, CancellationToken.None, NullLogger.Instance, new HostFileSystem());

        File.Exists(storeLock).Should().BeFalse("a stale lock is cleared with host filesystem APIs");
        File.Exists(Path.Combine(gitDir, "MERGE_HEAD")).Should().BeFalse();
        Directory.Exists(Path.Combine(gitDir, "rebase-merge")).Should().BeFalse();
        runner.Commands.Select(c => string.Join(' ', c.Argv)).Should().NotContain(
            command => command.StartsWith("find ", StringComparison.Ordinal),
            "the daemon host has no POSIX find");
        verdict.Should().Be(HygieneVerdict.Clean);
    }

    [Fact]
    public async Task EnsureClean_on_a_host_backed_store_clears_a_lock_whose_name_is_not_lowercase()
    {
        // Git writes `index.lock`, but the name reaching this sweep is whatever the HOST hands back, and on
        // the case-insensitive filesystem the host branch exists for, `index.LOCK` is the very file git is
        // honouring — a tool, an editor or a restored backup is enough to put that casing on disk. A
        // case-sensitive suffix match walks straight past it and the whole lease is wasted: the reset fails
        // on the lock, the cleanliness gate condemns the store, and the re-clone wipes it. It self-heals, and
        // it does so through the most expensive recovery this daemon has.
        var store = SeedStore();
        var gitDir = Path.Combine(store, ".git");
        var shoutingLock = Path.Combine(gitDir, "index.LOCK");
        File.WriteAllText(shoutingLock, string.Empty);
        var runner = new FakeSandboxCommandRunner();
        runner.OnArgvContains("find ", new SandboxCommandResult(1, string.Empty, "'find' is not recognized"));

        var verdict = await SlotHygiene.EnsureCleanAsync(
            new GitRunner(runner), store, CancellationToken.None, NullLogger.Instance, new HostFileSystem());

        File.Exists(shoutingLock)
            .Should()
            .BeFalse("the suffix is matched against a name the filesystem chose, not one git spelled");
        verdict.Should().Be(HygieneVerdict.Clean);
    }

    [Fact]
    public async Task EnsureClean_issues_reset_clean_and_submodule_recursion()
    {
        var store = SeedStore();
        var runner = new FakeSandboxCommandRunner();

        await SlotHygiene.EnsureCleanAsync(new GitRunner(runner), store, CancellationToken.None);

        var commands = runner.Commands.Select(c => string.Join(' ', c.Argv)).ToList();
        commands.Should().Contain(a => a.Contains("reset --hard"));
        commands.Should().Contain(a => a.Contains("clean -ffdx"));
        commands.Should().Contain(a => a.Contains("submodule foreach --recursive"));
    }

    [Fact]
    public async Task EnsureClean_restores_submodules_to_the_recorded_gitlink()
    {
        // Regression guard for warm-slot re-clone churn: a prior lease leaves the reviewed submodule (and its
        // nested submodules) checked out at PR-head commits, which the superproject sees as moved pointers
        // (dirty). Without a recursive `submodule update` to restore them to the recorded gitlink, every warm
        // slot looks dirty and is needlessly re-cloned. `submodule foreach reset --hard` alone does NOT fix this —
        // it resets each to its own (PR-head) HEAD, not the superproject's gitlink. It must also be `--no-fetch`
        // (see the dedicated security test below).
        var store = SeedStore();
        var runner = new FakeSandboxCommandRunner();

        await SlotHygiene.EnsureCleanAsync(new GitRunner(runner), store, CancellationToken.None);

        var commands = runner.Commands.Select(c => string.Join(' ', c.Argv)).ToList();
        commands.Should().Contain(
            a => a.Contains("submodule update") && a.Contains("--recursive") && a.Contains("--force"),
            "submodule checkouts must be restored to the recorded gitlink so a warm slot is not re-cloned");
    }

    [Fact]
    public async Task EnsureClean_restore_never_contacts_a_remote_through_host_credentials()
    {
        // SECURITY: hygiene runs on the host with the daemon's broad provider credentials, BEFORE the run's
        // policy-enforced SubmoduleInitializer, so it must never touch a remote — otherwise a registered-but-
        // deinit'd submodule would be CLONED through those credentials outside the allow-list. `--no-fetch` alone
        // is insufficient (a missing gitdir still drives an implicit clone), so every `submodule update` hygiene
        // issues MUST carry the hard transport guard `-c protocol.allow=never` (which denies all clone/fetch
        // transports) as well as `--no-fetch`.
        var store = SeedStore();
        var runner = new FakeSandboxCommandRunner();

        await SlotHygiene.EnsureCleanAsync(new GitRunner(runner), store, CancellationToken.None);

        var submoduleUpdates = runner.Commands
            .Select(c => string.Join(' ', c.Argv))
            .Where(a => a.Contains("submodule update"))
            .ToList();
        submoduleUpdates.Should().NotBeEmpty();
        submoduleUpdates.Should().OnlyContain(
            a => a.Contains("protocol.https.allow=never")
                && a.Contains("protocol.allow=never")
                && a.Contains("--no-fetch"),
            "hygiene must deny all transports so it cannot clone/fetch through the host's broad credentials");
    }

    [Fact]
    public async Task EnsureClean_reports_NeedsReclone_when_gitdir_missing()
    {
        var store = Path.Combine(_root, "empty");
        Directory.CreateDirectory(store); // no .git — never cloned / blown away

        var verdict = await SlotHygiene.EnsureCleanAsync(
            new GitRunner(new FakeSandboxCommandRunner()), store, CancellationToken.None);

        verdict.Should().Be(HygieneVerdict.NeedsReclone);
    }

    [Fact]
    public async Task EnsureClean_reports_NeedsReclone_when_health_probe_fails()
    {
        var store = SeedStore();
        var runner = new FakeSandboxCommandRunner();
        runner.OnArgvContains(
            "rev-parse --git-dir", new SandboxCommandResult(128, string.Empty, "fatal: not a git repository"));

        var verdict = await SlotHygiene.EnsureCleanAsync(new GitRunner(runner), store, CancellationToken.None);

        verdict.Should().Be(HygieneVerdict.NeedsReclone);
    }

    [Fact]
    public async Task EnsureClean_reports_NeedsReclone_when_the_tree_is_still_dirty_after_cleanup()
    {
        // rev-parse --git-dir succeeds (structure intact) but the working tree is STILL dirty — a partially
        // failed clean must not be reported as Clean, or the contamination crosses into the next review run.
        var store = SeedStore();
        var runner = new FakeSandboxCommandRunner();
        runner.OnArgvContains(
            "status --porcelain", new SandboxCommandResult(0, " M src/Foo.cs\n?? leftover.tmp\n", string.Empty));

        var verdict = await SlotHygiene.EnsureCleanAsync(new GitRunner(runner), store, CancellationToken.None);

        verdict.Should().Be(HygieneVerdict.NeedsReclone);
    }

    [Fact]
    public async Task EnsureClean_reports_NeedsReclone_when_a_cleanup_step_fails()
    {
        // A clean -ffdx that could not remove contamination (e.g. a locked file) is not tolerated: the slot is
        // re-cloned rather than reused with residual state.
        var store = SeedStore();
        var runner = new FakeSandboxCommandRunner();
        runner.OnArgvContains(
            "clean -ffdx", new SandboxCommandResult(1, string.Empty, "warning: failed to remove leftover.tmp"));

        var verdict = await SlotHygiene.EnsureCleanAsync(new GitRunner(runner), store, CancellationToken.None);

        verdict.Should().Be(HygieneVerdict.NeedsReclone);
    }

    [Fact]
    public async Task EnsureClean_proceeds_when_submodule_restore_fails_non_corruptly()
    {
        // A non-corrupt `submodule update` failure (transient/unrecognized/missing-object/deinit'd-submodule) is
        // NON-fatal: it must NOT destructively re-clone the persistent store, and must NOT retry-loop (a
        // deterministic missing object never reaches the initializer). Hygiene proceeds — the review re-establishes
        // submodules with permitted fetches — so with the superproject clean the verdict is Clean.
        var store = SeedStore();
        var runner = new FakeSandboxCommandRunner();
        runner.OnArgvContains(
            "submodule update --recursive",
            new SandboxCommandResult(1, string.Empty, "fatal: Unable to checkout 'deadbeef' in submodule path 'repos/X'"));

        var verdict = await SlotHygiene.EnsureCleanAsync(new GitRunner(runner), store, CancellationToken.None);

        verdict.Should().Be(HygieneVerdict.Clean);
    }

    [Fact]
    public async Task EnsureClean_reports_NeedsReclone_when_submodule_restore_is_corrupt()
    {
        // A CORRUPT restore failure (a genuinely broken local object) IS re-clone-worthy: a fresh clone fixes it,
        // and the submodule can't be confirmed at its recorded gitlink (which status may hide).
        var store = SeedStore();
        var runner = new FakeSandboxCommandRunner();
        runner.OnArgvContains(
            "submodule update --recursive",
            new SandboxCommandResult(1, string.Empty, "error: object file .git/modules/repos/X/objects/de/adbeef is empty"));

        var verdict = await SlotHygiene.EnsureCleanAsync(new GitRunner(runner), store, CancellationToken.None);

        verdict.Should().Be(HygieneVerdict.NeedsReclone);
    }

    [Fact]
    public async Task EnsureClean_tolerates_submodule_cleanup_failure_when_the_tree_is_clean()
    {
        // A `git submodule foreach` that fatals on a committed embedded gitlink with no .gitmodules URL (the
        // PR-11182 wedge) is NOT re-clonable corruption: a re-clone reproduces the same committed tree and
        // loops forever. The superproject reset/clean + the status gate already prove the tree is clean, so
        // the slot stays reusable rather than being driven into an unfixable re-clone.
        var store = SeedStore();
        var runner = new FakeSandboxCommandRunner();
        runner.OnArgvContains(
            "submodule foreach --recursive",
            new SandboxCommandResult(
                1, string.Empty, "fatal: No url found for submodule path 'PRs/x-11182/repo' in .gitmodules"));

        var verdict = await SlotHygiene.EnsureCleanAsync(new GitRunner(runner), store, CancellationToken.None);

        verdict.Should().Be(HygieneVerdict.Clean);
    }

    [Fact]
    public async Task EnsureClean_tolerates_dirty_submodule_content_that_no_reclone_can_repair()
    {
        // The mcqdb wedge. A path at or beyond Windows' 260-char MAX_PATH inside a submodule's own submodule
        // cannot be re-created by `reset --hard` ("Filename too long"), so `submodule foreach` fatals and the
        // submodule stays dirty — and a fresh clone cannot check that path out either. Gating the verdict on
        // submodule state therefore condemned the slot on EVERY lease, forever, for a defect re-cloning is
        // structurally incapable of fixing. The superproject is what a clone repairs, so it alone decides:
        // hygiene must read status with --ignore-submodules=all and never fall back to a full status. The one
        // submodule state that IS allowed to decide is untracked residue, read per-submodule by the residue walk
        // and excluded from the assertion below: it is a previous lease's leftovers rather than the repo's own
        // content, so it is the one thing here a fresh clone is guaranteed to arrive without. That exclusion is
        // spelled as "the probe run AT THE STORE ROOT", not as "the probe that mentions foreach" — the walk no
        // longer runs `foreach`, and a name-based exclusion that stops matching silently widens this assertion
        // into a claim the residue walk is designed to violate.
        var store = SeedStore();
        var runner = new FakeSandboxCommandRunner();
        runner.OnArgvContains(
            "submodule foreach --recursive",
            new SandboxCommandResult(
                1,
                string.Empty,
                "fatal: cannot create directory at 'Samples/2.0/ServiceFabricMesh/VotingApp/VotingWeb/wwwroot/lib'"
                    + ": Filename too long\nfatal: run_command returned non-zero status for repos/MCQdbDEV"));

        var verdict = await SlotHygiene.EnsureCleanAsync(new GitRunner(runner), store, CancellationToken.None);

        verdict.Should().Be(HygieneVerdict.Clean);
        var storeStatusProbes = runner.Commands.Select(c => string.Join(' ', c.Argv))
            .Where(a => a.Contains($"-C {store} status --porcelain"))
            .ToList();
        storeStatusProbes.Should().NotBeEmpty("the superproject status gate is what this test is about");
        storeStatusProbes.Should()
            .OnlyContain(
                a => a.Contains("--ignore-submodules=all"),
                "submodule state must never gate the verdict — a re-clone reproduces it byte for byte");
    }

    [Fact]
    public async Task EnsureClean_reports_NeedsReclone_when_submodule_cleanup_fails_with_corruption()
    {
        // The one submodule-cleanup failure a fresh clone DOES repair: a lock that outlived both sweeps (or a
        // broken object). Unlike unrepairable content, this must still condemn the slot.
        var store = SeedStore();
        var runner = new FakeSandboxCommandRunner();
        runner.OnArgvContains(
            "submodule foreach --recursive",
            new SandboxCommandResult(
                1,
                string.Empty,
                "fatal: Unable to create '/store/.git/modules/repos/X/index.lock': File exists."));

        var verdict = await SlotHygiene.EnsureCleanAsync(new GitRunner(runner), store, CancellationToken.None);

        verdict.Should().Be(HygieneVerdict.NeedsReclone);
    }

    [Fact]
    public async Task EnsureClean_reclones_when_a_failed_submodule_sweep_left_untracked_residue()
    {
        // Tolerating the failure was silently tolerating what it left behind. `submodule foreach` ABORTS the walk
        // at the first submodule it cannot resolve, so every submodule after it kept whatever the previous lease
        // put there — and nothing else in the pass reaches them: the superproject's `clean -ffdx` stops at a
        // tracked gitlink, and `submodule update --checkout --force` only restores TRACKED content. One review's
        // untracked files crossed into the next review's checkout.
        var store = SeedStore();
        var runner = new FakeSandboxCommandRunner();
        runner.OnArgvContains(
            "submodule foreach --recursive",
            new SandboxCommandResult(1, string.Empty, "fatal: run_command returned non-zero status for repos/First"));
        ScriptOneSubmodule(
            runner, store, new SandboxCommandResult(0, "?? notes-from-the-last-review.md\n", string.Empty));

        var verdict = await SlotHygiene.EnsureCleanAsync(new GitRunner(runner), store, CancellationToken.None);

        verdict.Should().Be(HygieneVerdict.NeedsReclone);
        var commands = runner.Commands.Select(c => string.Join(' ', c.Argv)).ToList();
        commands.Should().Contain(
            a => a.Contains("ls-files -s"),
            "the re-sweep must read its list from the INDEX, which no broken submodule can interrupt");
        commands.Should().Contain(
            a => a.Contains($"-C {OnlySubmodulePath(store)} clean -ffdx"),
            "the re-sweep must reach the submodules AFTER the one that aborted the first walk");
    }

    [Fact]
    public async Task EnsureClean_does_not_read_a_failed_residue_probe_as_a_clean_submodule()
    {
        // A check whose clean answer can be produced by its own failure is not a check: reading an empty stdout
        // from a status command that never ran would turn the residue gate into a rubber stamp for exactly the
        // broken submodule states it exists to catch. This is the probe failing at a submodule that has ALREADY
        // answered for itself, which is what separates it from the skipped-path case below.
        var store = SeedStore();
        var runner = new FakeSandboxCommandRunner();
        runner.OnArgvContains(
            "submodule foreach --recursive",
            new SandboxCommandResult(1, string.Empty, "fatal: run_command returned non-zero status for repos/First"));
        ScriptOneSubmodule(
            runner, store, new SandboxCommandResult(1, string.Empty, "fatal: unable to read index file"));

        var verdict = await SlotHygiene.EnsureCleanAsync(new GitRunner(runner), store, CancellationToken.None);

        verdict.Should().Be(HygieneVerdict.NeedsReclone);
    }

    [Fact]
    public async Task EnsureClean_does_not_condemn_a_deinitialized_submodule_through_the_residue_probe()
    {
        // The two gates have to agree about the SAME state, and this is where they nearly didn't.
        // GitFailureClassifier deliberately reads `not a git repository: .../.git/modules/...` as a deinitialized
        // submodule rather than a damaged store, so the sweep tolerates it. The residue walk used to be a second
        // `submodule foreach`, so it failed for the identical reason — and a probe failure is otherwise reported
        // AS residue, which would have condemned the slot on exactly the state the classifier had just decided to
        // keep, re-cloning on every lease.
        //
        // The walk no longer has to recognize that stderr at all: it asks each indexed path to answer for itself
        // with `rev-parse --show-toplevel` FIRST, and a submodule whose gitdir is gone cannot, so it is skipped
        // before anything is run in it. The assertion is therefore not just the verdict — it is that nothing was
        // run in that path, which is what distinguishes a gate that skipped from a probe that was tolerated.
        var store = SeedStore();
        var runner = new FakeSandboxCommandRunner();
        const string deinitialized =
            "fatal: not a git repository: repos/First/../.git/modules/repos/First";
        runner.OnArgvContains(
            "submodule foreach --recursive",
            new SandboxCommandResult(1, string.Empty, deinitialized));
        runner.OnArgvContains(
            "rev-parse --show-toplevel", new SandboxCommandResult(128, string.Empty, deinitialized));
        ScriptOneSubmodule(
            runner, store, new SandboxCommandResult(0, "?? would-have-condemned-the-slot.md\n", string.Empty));
        var logs = new CapturingLoggerFactory();

        var verdict = await SlotHygiene.EnsureCleanAsync(
            new GitRunner(runner), store, CancellationToken.None, logs.Capturing);

        verdict.Should().Be(HygieneVerdict.Clean);
        var commands = runner.Commands.Select(c => string.Join(' ', c.Argv)).ToList();
        commands.Should().Contain(
            a => a.Contains($"-C {OnlySubmodulePath(store)} rev-parse --show-toplevel"),
            "the walk must have REACHED this path — a Clean verdict from a walk that enumerated nothing would"
                + " assert the same thing while proving none of it");
        commands.Should().NotContain(
            a => a.Contains($"-C {OnlySubmodulePath(store)} clean")
                || a.Contains($"-C {OnlySubmodulePath(store)} status"),
            "a path that cannot answer for itself is skipped, not swept and not probed");

        // Skipping is the decision; being silent about it is not. Neither cleaned nor surfaced is the same
        // fail-quiet shape this PR closes, so the only thing standing between an operator and a store quietly
        // accumulating residue behind a broken gitlink is this line — and a line nothing asserts on rots away.
        logs.Capturing.WarningCount(OnlySubmodulePath(store)).Should().Be(
            1, "the skipped path is the one thing the operator cannot work out for themselves");
        logs.Capturing.WarningCount("crosses into the next review").Should().Be(
            1, "the log has to say what the skip COSTS, not merely that a probe returned nothing");
    }

    [Fact]
    public async Task EnsureClean_condemns_when_the_submodule_listing_itself_fails()
    {
        // The enumeration is now the thing the whole re-sweep stands on, so its failure is the new way this check
        // could quietly answer "no residue" without having looked. It is reported for the same reason a failed
        // status probe is: a walk that could not read its own list has established nothing about what is in the
        // submodules, and reading that as a clean store hands the next review whatever is actually there.
        var store = SeedStore();
        var runner = new FakeSandboxCommandRunner();
        runner.OnArgvContains(
            "submodule foreach --recursive",
            new SandboxCommandResult(1, string.Empty, "fatal: run_command returned non-zero status for repos/First"));
        runner.OnArgvContains(
            "ls-files -s", new SandboxCommandResult(128, string.Empty, "fatal: unable to read index file"));

        var verdict = await SlotHygiene.EnsureCleanAsync(new GitRunner(runner), store, CancellationToken.None);

        verdict.Should().Be(HygieneVerdict.NeedsReclone);
    }

    [Fact]
    public async Task EnsureClean_tolerates_a_failed_submodule_sweep_that_left_only_tracked_damage()
    {
        // The over-refusal pin for the gate above, and the reason it looks for `??` alone. The mcqdb wedge leaves
        // a TRACKED file deleted — a state a fresh clone cannot check out either — so condemning the slot on any
        // dirtiness at all would rebuild the forever-re-clone loop this PR exists to remove.
        var store = SeedStore();
        var runner = new FakeSandboxCommandRunner();
        runner.OnArgvContains(
            "submodule foreach --recursive",
            new SandboxCommandResult(
                1, string.Empty, "fatal: cannot create directory at 'VotingApp/VotingWeb/wwwroot/lib'"
                    + ": Filename too long"));
        ScriptOneSubmodule(
            runner,
            store,
            new SandboxCommandResult(0, " D VotingApp/VotingWeb/wwwroot/lib/jquery.js\n", string.Empty));

        var verdict = await SlotHygiene.EnsureCleanAsync(new GitRunner(runner), store, CancellationToken.None);

        verdict.Should().Be(HygieneVerdict.Clean);
        runner.Commands.Select(c => string.Join(' ', c.Argv))
            .Should()
            .Contain(
                a => a.Contains($"-C {OnlySubmodulePath(store)} status --porcelain"),
                "the tracked-damage line has to have been READ and judged harmless — a Clean verdict from a walk"
                    + " that never probed anything would assert the same thing while proving none of it");
    }

    /// <summary>
    /// The one submodule path <see cref="ScriptOneSubmodule"/> puts in the scripted store, spelled the way the
    /// walk composes it: git answers <c>--show-toplevel</c> in forward slashes, so the walk normalizes to them
    /// and passes the normalized path to <c>-C</c>.
    /// </summary>
    private static string OnlySubmodulePath(string store) => $"{store.Replace('\\', '/')}/repos/First";

    /// <summary>
    /// Scripts <paramref name="runner"/> as a store holding exactly one submodule at <c>repos/First</c> that
    /// answers for itself, so the residue walk gets past its gate and reaches <paramref name="status"/>. The
    /// listing is a SEQUENCE — the gitlink once, then nothing — because the walk re-reads <c>ls-files</c> inside
    /// each submodule it validates (that recursion is what replaces <c>foreach --recursive</c>), and a rule that
    /// answered every call with the same gitlink would describe a store that contains itself forever.
    /// </summary>
    private static void ScriptOneSubmodule(
        FakeSandboxCommandRunner runner, string store, SandboxCommandResult status)
    {
        runner.OnArgvContainsSequence(
            "ls-files -s",
            new SandboxCommandResult(0, "160000 deadbeefdeadbeefdeadbeefdeadbeefdeadbeef 0\trepos/First\n", string.Empty),
            new SandboxCommandResult(0, string.Empty, string.Empty));
        runner.OnArgvContains(
            "rev-parse --show-toplevel",
            new SandboxCommandResult(0, OnlySubmodulePath(store) + "\n", string.Empty));

        // The superproject's own status probe carries --ignore-submodules=all and must keep its own answer; only
        // the per-submodule probe is being scripted here.
        runner.On(
            c => string.Join(' ', c.Argv).Contains("status --porcelain", StringComparison.Ordinal)
                && !string.Join(' ', c.Argv).Contains("--ignore-submodules", StringComparison.Ordinal),
            status);
    }

    [Fact]
    public async Task EnsureClean_force_resets_once_before_condemning_a_dirty_store()
    {
        // Re-cloning a persistent store costs minutes, so a store that is still dirty after one pass gets a
        // second sweep-and-reset before being condemned. Here the second pass settles it, so the slot survives.
        var store = SeedStore();
        var runner = new FakeSandboxCommandRunner();
        runner.OnArgvContainsSequence(
            "status --porcelain",
            new SandboxCommandResult(0, " M src/Foo.cs\n", string.Empty),
            new SandboxCommandResult(0, string.Empty, string.Empty));

        var verdict = await SlotHygiene.EnsureCleanAsync(new GitRunner(runner), store, CancellationToken.None);

        verdict.Should().Be(HygieneVerdict.Clean);
        CountStoreResets(runner)
            .Should()
            .Be(2, "the still-dirty first pass must be followed by exactly one force reset");
    }

    [Fact]
    public async Task EnsureClean_does_not_force_reset_a_deterministic_non_corrupt_failure()
    {
        // A missing local object / deinit'd submodule fails the same way every time, so a second pass only burns
        // minutes on every lease and never changes the answer. Retry is reserved for what a re-sweep can clear.
        var store = SeedStore();
        var runner = new FakeSandboxCommandRunner();
        runner.OnArgvContains(
            "submodule update --recursive",
            new SandboxCommandResult(
                1, string.Empty, "fatal: Unable to checkout 'deadbeef' in submodule path 'repos/X'"));

        var verdict = await SlotHygiene.EnsureCleanAsync(new GitRunner(runner), store, CancellationToken.None);

        verdict.Should().Be(HygieneVerdict.Clean);
        CountStoreResets(runner)
            .Should()
            .Be(1, "a deterministic non-corrupt failure must not trigger a second pass");
    }

    /// <summary>
    /// Counts force resets of the STORE itself. Matching the joined argv on "reset --hard" would double-count,
    /// because the submodule cleanup passes <c>git reset --hard &amp;&amp; git clean -ffdx</c> as a single argv
    /// element to <c>submodule foreach</c> — so the number of passes would read as twice its real value and the
    /// assertion would pass for the wrong reason. Match the trailing argv pair instead, which only the store-level
    /// reset has.
    /// </summary>
    private static int CountStoreResets(FakeSandboxCommandRunner runner) =>
        runner.Commands.Count(c =>
            c.Argv.Count >= 2 && c.Argv[^2] == "reset" && c.Argv[^1] == "--hard");

    [WindowsOnlyFact("only Git for Windows refuses to create a path at or beyond MAX_PATH")]
    public async Task EnsureClean_settles_a_store_holding_a_path_beyond_MAX_PATH()
    {
        // REAL-GIT regression for the mcqdb wedge at its source. Git for Windows refuses to create a path at or
        // beyond MAX_PATH unless core.longpaths is set: `reset --hard` fails "Filename too long", the file stays
        // missing, the tree stays dirty, and hygiene condemns the store on every lease — while the re-clone it
        // asks for cannot check that path out either. GitRunner now sets core.longpaths on every invocation, so
        // the reset succeeds and the store settles. An argv assertion cannot catch this; only real git can — and
        // only on Windows: elsewhere the long path checks out whether or not core.longpaths is ever set, so the
        // body would pass without touching the defect and go on passing after the fix was reverted.
        var runner = NewHostGitRunner();
        var store = await SetupStoreWithLongPathAsync(runner);

        var verdict = await SlotHygiene.EnsureCleanAsync(
            new GitRunner(runner), store, CancellationToken.None, NullLogger.Instance, new HostFileSystem());

        verdict.Should().Be(HygieneVerdict.Clean, "a long path must be checked out, not treated as contamination");
    }

    [Fact]
    public async Task EnsureClean_refuses_a_store_whose_git_dir_redirects_the_sweep()
    {
        // The sweep's job is to delete every *.lock under .git, and it found them with a recursive enumeration —
        // which follows a symlinked or junctioned directory without saying so. A single link planted anywhere
        // under a pooled store therefore pointed the delete at an arbitrary path on the daemon host, under the
        // daemon's own account. The store is condemned instead of repaired: unlinking it is a write chosen by
        // whoever planted it, and the re-clone that follows wipes the whole store WITHOUT walking through it.
        var store = SeedStore();
        var outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(outside);
        var victim = Path.Combine(outside, "index.lock");
        await File.WriteAllTextAsync(victim, "not the daemon's to delete");
        DirectoryLink.Create(Path.Combine(store, ".git", "modules"), outside);

        var verdict = await SlotHygiene.EnsureCleanAsync(
            new GitRunner(new FakeSandboxCommandRunner()), store, CancellationToken.None,
            NullLogger.Instance, new HostFileSystem());

        File.Exists(victim).Should().BeTrue("the sweep must not delete through a link out of the store");
        verdict.Should().Be(
            HygieneVerdict.NeedsReclone, "a store that redirects its own cleanup is not a store worth cleaning");
    }

    [Fact]
    public void VerdictForBlockedSweep_maps_a_redirected_refusal_to_reclone()
    {
        // Issue #276, one side of the split, reachable WITHOUT an OS-privilege-gated unreadable entry. A
        // redirected entry is unlinked by the re-clone's wipe (removed by name, never followed), so the fresh
        // clone lands clean — a re-clone genuinely repairs it.
        SlotHygiene.VerdictForBlockedSweep(
                new HostPathRefusal(Path.Combine(_root, ".git", "modules"), HostPathVerdict.Redirected))
            .Should().Be(HygieneVerdict.NeedsReclone);
    }

    [Fact]
    public void VerdictForBlockedSweep_maps_an_unreadable_refusal_to_retire_not_reclone()
    {
        // Issue #276, the side the fix changes. A re-clone begins by wiping the store, and the wipe refuses on
        // the same UNREADABLE entry this sweep stopped at, so a re-clone would walk into the same wall and
        // replace nothing. The verdict must therefore NOT be NeedsReclone: it is HostPathUnreadable, which the
        // preparer raises as a refusal so the address is retired. This is the deciding line, and the mutation
        // that collapses the split (returning NeedsReclone here) turns green only if this assertion is absent.
        SlotHygiene.VerdictForBlockedSweep(
                new HostPathRefusal(Path.Combine(_root, ".git", "objects"), HostPathVerdict.Unreadable))
            .Should().Be(HygieneVerdict.HostPathUnreadable);
    }

    [Fact]
    public async Task EnsureClean_runs_no_git_at_all_on_a_store_that_redirects_the_sweep()
    {
        // The refusal IS the verdict: the slot is condemned the moment the sweep stops, and no outcome the
        // force-reset ladder could produce afterwards would change that. Running it anyway spent four git
        // invocations — every one of them a write — on a store whose entire contents are about to be deleted
        // wholesale by the re-clone, and spent them on the one store the daemon has just said it cannot establish
        // the shape of. The structural probe is the only command that may appear, because it runs BEFORE the
        // sweep and is what decides there is a repository here to sweep at all.
        var store = SeedStore();
        var outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(outside);
        DirectoryLink.Create(Path.Combine(store, ".git", "modules"), outside);
        var runner = new FakeSandboxCommandRunner();

        var verdict = await SlotHygiene.EnsureCleanAsync(
            new GitRunner(runner), store, CancellationToken.None, NullLogger.Instance, new HostFileSystem());

        verdict.Should().Be(HygieneVerdict.NeedsReclone);
        var commands = runner.Commands.Select(c => string.Join(' ', c.Argv)).ToList();
        commands.Should().HaveCount(
            1, "nothing after the sweep can change the verdict, so nothing after the sweep should run");
        commands[0].Should().EndWith(
            "rev-parse --git-dir", "the structural probe precedes the sweep — it is the one command that must run");
    }

    [Fact]
    public async Task Strip_runs_no_git_at_all_on_a_store_that_redirects_the_sweep()
    {
        // Every command the strip issues exists to leave the slot pristine for the next lease. A store the sweep
        // refuses to cross does not GET a next lease in that sense — the next EnsureCleanAsync meets the same
        // refusal and re-clones it — so the whole sequence is writes into a directory about to be deleted. The
        // strip has no verdict to report and condemns nothing; it simply has nothing left worth doing.
        var store = SeedStore();
        var outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(outside);
        DirectoryLink.Create(Path.Combine(store, ".git", "modules"), outside);
        var runner = new FakeSandboxCommandRunner();

        await SlotHygiene.StripAsync(new GitRunner(runner), store, CancellationToken.None);

        runner.Commands.Should().BeEmpty("the strip's only purpose is a pristine store, and this one cannot be one");
    }

    [RequiresUnreadableEntryFact("a listable directory cannot show the difference between empty and unreadable")]
    public async Task EnsureClean_refuses_a_store_whose_git_dir_cannot_be_enumerated()
    {
        // The sibling of the redirected case above, and refused for the same stated reason: the sweep's job is
        // to establish that every *.lock under .git is gone, and "I could not look" is not an establishment.
        // HostPathGuard says exactly that at the ATTRIBUTE level -- an entry whose attributes will not read is
        // Unreadable and stops the walk. One level up, the ENUMERATION had the opposite answer: it returned no
        // entries, which the walk cannot tell apart from an empty directory, so the sweep reported that it had
        // finished having read nothing.
        //
        // Every git step here is scripted to succeed, and that is the realistic case rather than a convenient
        // one: the denial leaves traversal intact (see UnlistableDirectory), a superproject `reset --hard`
        // never reads .git/modules/<sub>/ at all, and a submodule step that did fail is classified non-fatal
        // by design. So nothing downstream reports this store either.
        var store = SeedStore();
        var moduleDir = Path.Combine(store, ".git", "modules", "repos", "LmDotnetTools");
        Directory.CreateDirectory(moduleDir);
        var missedLock = Path.Combine(moduleDir, "index.lock"); // the exact 2026-07-12 incident's lock location
        await File.WriteAllTextAsync(missedLock, string.Empty);
        using var denied = UnreadableEntry.UnlistableDirectory(moduleDir);

        var verdict = await SlotHygiene.EnsureCleanAsync(
            new GitRunner(new FakeSandboxCommandRunner()), store, CancellationToken.None,
            NullLogger.Instance, new HostFileSystem());

        verdict.Should().Be(
            HygieneVerdict.HostPathUnreadable,
            "the sweep could not establish that this store is clean, and a store whose cleanup it cannot walk is "
                + "one a re-clone cannot walk either -- the wipe refuses on the same unreadable entry, so the slot "
                + "is retired rather than re-cloned into the same wall");
        File.Exists(missedLock).Should().BeTrue(
            "the point is that the sweep never reached this lock: it is still there, and every git step "
                + "reported success, so nothing downstream would have reported it either");
    }

    [RequiresUnreadableEntryFact("a listable directory cannot show the difference between empty and unreadable")]
    public async Task EnsureClean_runs_no_git_at_all_on_a_store_it_cannot_enumerate()
    {
        // Same reasoning as the redirected sibling: the refusal IS the verdict, so the four writes of the
        // force-reset ladder would land on a store already booked for wholesale deletion. This is also the
        // assertion that would catch a fix which merely LOGGED the unreadable directory and swept on.
        var store = SeedStore();
        using var denied = UnreadableEntry.UnlistableDirectory(
            Directory.CreateDirectory(Path.Combine(store, ".git", "modules")).FullName);
        var runner = new FakeSandboxCommandRunner();

        var verdict = await SlotHygiene.EnsureCleanAsync(
            new GitRunner(runner), store, CancellationToken.None, NullLogger.Instance, new HostFileSystem());

        verdict.Should().Be(HygieneVerdict.HostPathUnreadable);
        var commands = runner.Commands.Select(c => string.Join(' ', c.Argv)).ToList();
        commands.Should().HaveCount(
            1, "nothing after the sweep can change the verdict, so nothing after the sweep should run");
        commands[0].Should().EndWith("rev-parse --git-dir");
    }

    [RequiresUnreadableEntryFact("a listable directory cannot show the difference between empty and unreadable")]
    public async Task Strip_runs_no_git_at_all_on_a_store_it_cannot_enumerate()
    {
        // The close-side half. The strip has no verdict to report and condemns nothing, but its whole purpose
        // is leaving the slot pristine, and a store whose own .git will not enumerate cannot be established as
        // pristine by anything the strip does.
        var store = SeedStore();
        using var denied = UnreadableEntry.UnlistableDirectory(
            Directory.CreateDirectory(Path.Combine(store, ".git", "modules")).FullName);
        var runner = new FakeSandboxCommandRunner();

        await SlotHygiene.StripAsync(new GitRunner(runner), store, CancellationToken.None);

        runner.Commands.Should().BeEmpty("the strip's only purpose is a pristine store, and this one cannot be one");
    }

    [RequiresUnreadableEntryFact("a listable directory cannot show the difference between empty and unreadable")]
    public async Task EnsureClean_reporting_an_unenumerable_dir_does_not_claim_it_is_a_link()
    {
        // The refusal's VERDICT is not decoration: HostPathRefusal.Reason is derived from it, and that warning
        // is the entire account an operator gets of why a slot was condemned. Labelling this one Redirected
        // still condemns the store -- the behaviour above is unchanged -- while telling whoever reads the log
        // that a symlink or junction was planted under .git, and sending them to look for one that was never
        // there. This is the assertion that makes the verdict load-bearing rather than incidental.
        var store = SeedStore();
        var moduleDir = Directory.CreateDirectory(Path.Combine(store, ".git", "modules")).FullName;
        using var denied = UnreadableEntry.UnlistableDirectory(moduleDir);
        var logs = new CapturingLoggerFactory();

        var verdict = await SlotHygiene.EnsureCleanAsync(
            new GitRunner(new FakeSandboxCommandRunner()), store, CancellationToken.None,
            logs.Capturing, new HostFileSystem());

        verdict.Should().Be(HygieneVerdict.HostPathUnreadable);
        logs.Capturing.WarningCount(moduleDir).Should().Be(
            1, "the address that stopped the sweep is the one thing the operator cannot work out for themselves");
        logs.Capturing.WarningCount("cannot be read well enough to tell").Should().Be(
            1, "the reason has to be the one that is actually true of this entry");
        logs.Capturing.WarningCount("symlink or junction").Should().Be(
            0, "nothing here was redirected, and a refusal naming a link starts a hunt for one nobody planted");
    }

    [Fact]
    public async Task EnsureClean_still_sweeps_a_store_whose_git_dir_holds_an_empty_directory()
    {
        // The non-vacuity companion to the three above. "Could not enumerate" and "enumerated nothing" arrive
        // at this walk as the same empty array, and the fix distinguishes them -- so an empty directory, which
        // is ordinary in a real .git, must still sweep to completion and reach the locks beside it. Without
        // this, a fix that condemned every empty directory would pass all three tests above.
        var store = SeedStore();
        var gitDir = Path.Combine(store, ".git");
        Directory.CreateDirectory(Path.Combine(gitDir, "refs", "heads")); // empty: no branch has been written yet
        var staleLock = Path.Combine(gitDir, "index.lock");
        await File.WriteAllTextAsync(staleLock, string.Empty);

        var verdict = await SlotHygiene.EnsureCleanAsync(
            new GitRunner(new FakeSandboxCommandRunner()), store, CancellationToken.None,
            NullLogger.Instance, new HostFileSystem());

        verdict.Should().Be(HygieneVerdict.Clean);
        File.Exists(staleLock).Should().BeFalse("an empty directory is a readable one, and the sweep goes on");
    }

    [Fact]
    public async Task StripAsync_issues_reset_and_clean()
    {
        var store = SeedStore();
        var runner = new FakeSandboxCommandRunner();

        await SlotHygiene.StripAsync(new GitRunner(runner), store, CancellationToken.None);

        var commands = runner.Commands.Select(c => string.Join(' ', c.Argv)).ToList();
        commands.Should().Contain(a => a.Contains("reset --hard"));
        commands.Should().Contain(a => a.Contains("clean -ffdx"));
    }

    [Fact]
    public async Task EnsureClean_does_not_clone_a_deinitialized_submodule_over_the_network()
    {
        // REAL-GIT regression for the implicit-clone hole (an argv-only test cannot detect a clone): a prior lease
        // can leave a submodule registered (URL in .git/config) with its worktree + .git/modules gitdir removed.
        // `submodule update` (even --no-fetch) then drives `submodule--helper clone` → git clone through the host's
        // broad credentials, outside the review's allow-list. Hygiene must NOT clone it (DenyNetworkArgs denies all
        // transports) — it proceeds (Clean, a non-corrupt restore failure is non-fatal), never cloning.
        var runner = NewHostGitRunner();
        var (super, sub) = await SetupDeinitializedSubmoduleStoreAsync(runner);

        var verdict = await SlotHygiene.EnsureCleanAsync(new GitRunner(runner), super, CancellationToken.None);

        AssertSubmoduleNotCloned(sub);
        verdict.Should().Be(HygieneVerdict.Clean);
    }

    [Fact]
    public async Task Strip_does_not_clone_a_deinitialized_submodule_over_the_network()
    {
        // Same real-git regression for the success-path StripAsync — it uses the SAME DenyNetworkArgs guard as
        // EnsureCleanAsync, and a shared setup (SetupDeinitializedSubmoduleStoreAsync) keeps the two hygiene paths
        // from drifting. StripAsync is best-effort (no verdict); the guarantee is that it never clones/fetches the
        // deinit'd submodule through the host's broad credentials.
        var runner = NewHostGitRunner();
        var (super, sub) = await SetupDeinitializedSubmoduleStoreAsync(runner);

        await SlotHygiene.StripAsync(new GitRunner(runner), super, CancellationToken.None);

        AssertSubmoduleNotCloned(sub);
    }

    [Fact]
    public async Task EnsureClean_sweeps_residue_in_a_submodule_ordered_after_one_whose_gitdir_is_gone()
    {
        // REAL-GIT regression, and it has to be real git: the defect is a property of `submodule foreach`
        // that no argv assertion and no scripted runner reproduces. `foreach` does not SKIP a submodule whose
        // command fails — at a registered submodule whose gitdir is gone it ABORTS the traversal, dying while
        // resolving that submodule and before the per-submodule command is ever invoked. The `|| true` guarding
        // the command's exit status is therefore never reached, and every submodule ORDERED AFTER the broken one
        // is never swept at all. Measured on git 2.53: the sweep prints its banner for `first`, fatals, and never
        // reaches `second`.
        //
        // Nothing else in the pass covers them. The superproject's own `clean -ffdx` stops at a tracked gitlink
        // whether or not a repository is present at that path (measured: it leaves both the broken submodule's
        // worktree and its untracked residue untouched, and still exits 0 reporting a clean superproject), and
        // `submodule update --checkout --force` only restores TRACKED content. So the previous review's untracked
        // file sat in `second` while every gate in EnsureCleanAsync reported the store fit to reuse.
        //
        // The assertion is the FILE, not the verdict: the verdict is Clean either way, which is exactly what made
        // this invisible. On Windows this also pins the path normalization — the store path composed here carries
        // backslashes while git answers `rev-parse --show-toplevel` in forward slashes, and an unnormalized
        // comparison fails toward "skip", reproducing the same silent survival it is meant to end.
        var runner = NewHostGitRunner();
        var (super, residues) = await SetupOrderedSubmoduleResidueStoreAsync(runner);

        var verdict = await SlotHygiene.EnsureCleanAsync(new GitRunner(runner), super, CancellationToken.None);

        File.Exists(residues.AfterBroken).Should().BeFalse(
            "a submodule ordered AFTER the one that stops `foreach` must still be swept");
        File.Exists(residues.NestedAfterBroken).Should().BeFalse(
            "the replacement sweep must keep the `--recursive` reach it replaces, not just the top level");
        verdict.Should().Be(
            HygieneVerdict.Clean,
            "the residue was cleaned rather than merely surfaced, so nothing is left to condemn the slot for");
    }

    /// <summary>
    /// Real-git setup for the ordered-residue regression: a superproject with submodules <c>first</c> and
    /// <c>second</c> (that index order), <c>second</c> carrying a nested submodule of its own, untracked residue
    /// in <c>second</c> and in the nested one, and <c>first</c> left registered-but-DEINIT'd in the shape that
    /// aborts the walk — its worktree and its <c>.git</c> FILE retained, the <c>.git/modules/first</c> gitdir it
    /// points at removed. Returns the store and the two residue paths that must not survive hygiene.
    /// </summary>
    private async Task<(string Super, (string AfterBroken, string NestedAfterBroken) Residues)>
        SetupOrderedSubmoduleResidueStoreAsync(HostGitCommandRunner runner)
    {
        var super = Path.Combine(_root, "super");
        Directory.CreateDirectory(_root);

        async Task Git(string dir, params string[] args)
        {
            Directory.CreateDirectory(dir);
            var r = await runner.RunAsync(new SandboxCommand(["git", .. args], dir), default);
            r.Succeeded.Should().BeTrue($"setup `git {string.Join(' ', args)}` failed: {r.Stderr}");
        }

        // Three sources: `first` (the one that will be broken), `nested`, and `second` — which carries `nested`
        // as a submodule so the replacement sweep's recursion is exercised rather than assumed.
        async Task<string> Source(string name)
        {
            var path = Path.Combine(_root, "src", name);
            await Git(path, "init", "-q", ".");
            await File.WriteAllTextAsync(Path.Combine(path, "f.txt"), name);
            await Git(path, "add", "f.txt");
            await Git(path, "-c", "user.email=a@b", "-c", "user.name=a", "commit", "-q", "-m", "init");
            return path.Replace('\\', '/');
        }

        var firstSrc = await Source("first");
        var nestedSrc = await Source("nested");
        var secondSrc = await Source("second");
        await Git(secondSrc, "-c", "protocol.file.allow=always", "-c", "user.email=a@b", "-c", "user.name=a",
            "submodule", "add", "-q", nestedSrc, "nested");
        await Git(secondSrc, "-c", "user.email=a@b", "-c", "user.name=a", "commit", "-q", "-m", "addnested");

        await Git(super, "init", "-q", ".");
        await File.WriteAllTextAsync(Path.Combine(super, "seed.txt"), "seed\n");
        await Git(super, "add", "seed.txt");
        await Git(super, "-c", "user.email=a@b", "-c", "user.name=a", "commit", "-q", "-m", "seed");
        foreach (var (name, source) in new[] { ("first", firstSrc), ("second", secondSrc) })
        {
            await Git(super, "-c", "protocol.file.allow=always", "-c", "user.email=a@b", "-c", "user.name=a",
                "submodule", "add", "-q", source, name);
        }

        await Git(super, "-c", "protocol.file.allow=always", "submodule", "update", "--init", "--recursive", "-q");
        await Git(super, "-c", "user.email=a@b", "-c", "user.name=a", "commit", "-q", "-m", "addsubs");

        var afterBroken = Path.Combine(super, "second", "notes-from-the-last-review.md");
        var nestedAfterBroken = Path.Combine(super, "second", "nested", "notes-from-the-last-review.md");
        await File.WriteAllTextAsync(afterBroken, "a previous lease's leftovers");
        await File.WriteAllTextAsync(nestedAfterBroken, "a previous lease's leftovers");

        // Break `first`: keep its worktree AND its `.git` file, delete the gitdir that file points at. This is
        // the shape that ABORTS the walk. (Deleting the `.git` file too gives the other deinit shape, which
        // `foreach` merely skips — it would not reproduce this defect.)
        DeleteRecursive(Path.Combine(super, ".git", "modules", "first"));
        File.Exists(Path.Combine(super, "first", ".git")).Should().BeTrue(
            "the aborting shape needs the dangling gitfile — without it `foreach` skips instead of dying");
        return (super, (afterBroken, nestedAfterBroken));
    }

    private static HostGitCommandRunner NewHostGitRunner() =>
        new(_ => Task.FromResult<IReadOnlyList<GitProviderToken>>([]), NullLogger<HostGitCommandRunner>.Instance);

    private static void AssertSubmoduleNotCloned(string sub)
    {
        // The transport guard must have BLOCKED the clone — the submodule was NOT re-created (no .git).
        Directory.Exists(Path.Combine(sub, ".git")).Should().BeFalse("the deinit'd submodule must not be re-cloned");
        File.Exists(Path.Combine(sub, ".git")).Should().BeFalse("the deinit'd submodule must not be re-cloned");
    }

    /// <summary>
    /// Real-git setup shared by both hygiene-path clone-guard tests: a superproject whose (file://) submodule is
    /// registered (URL retained in .git/config) but DEINIT'd — worktree + <c>.git/modules/&lt;name&gt;</c> gitdir
    /// removed. That is the exact state in which <c>submodule update</c> would drive an implicit clone.
    /// </summary>
    private async Task<(string super, string sub)> SetupDeinitializedSubmoduleStoreAsync(HostGitCommandRunner runner)
    {
        var remote = Path.Combine(_root, "remote.git").Replace('\\', '/');
        var seed = Path.Combine(_root, "seed");
        var super = Path.Combine(_root, "super");
        var sub = Path.Combine(super, "sub");
        Directory.CreateDirectory(_root);

        async Task Git(string dir, params string[] args)
        {
            Directory.CreateDirectory(dir);
            var r = await runner.RunAsync(new SandboxCommand(["git", .. args], dir), default);
            r.Succeeded.Should().BeTrue($"setup `git {string.Join(' ', args)}` failed: {r.Stderr}");
        }

        // Bare remote with one commit.
        await Git(_root, "init", "-q", "--bare", "remote.git");
        await Git(_root, "clone", "-q", remote, "seed");
        await Git(seed, "-c", "user.email=a@b", "-c", "user.name=a", "commit", "-q", "--allow-empty", "-m", "init");
        await Git(seed, "push", "-q", "origin", "HEAD:master");
        // Superproject with the remote as a (file://) submodule — setup explicitly allows the local transport.
        await Git(_root, "init", "-q", "super");
        await Git(super, "-c", "protocol.file.allow=always", "-c", "user.email=a@b", "-c", "user.name=a",
            "submodule", "add", "-q", remote, "sub");
        await Git(super, "-c", "user.email=a@b", "-c", "user.name=a", "commit", "-q", "-m", "addsub");

        // DEINIT: remove the submodule worktree + gitdir, KEEP its URL in .git/config — the exploitable state.
        foreach (var e in Directory.GetFileSystemEntries(sub))
        {
            DeleteRecursive(e);
        }

        DeleteRecursive(Path.Combine(super, ".git", "modules", "sub"));
        return (super, sub);
    }

    private static void DeleteRecursive(string path)
    {
        if (Directory.Exists(path))
        {
            // git object/pack files are read-only on Windows; clear the attribute before deleting the tree.
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }
                catch
                {
                    // best-effort
                }
            }

            Directory.Delete(path, recursive: true);
        }
        else if (File.Exists(path))
        {
            try
            {
                File.SetAttributes(path, FileAttributes.Normal);
            }
            catch
            {
                // best-effort
            }

            File.Delete(path);
        }
    }

    /// <summary>
    /// Real-git setup for the MAX_PATH regression: a store whose HEAD commit contains a file at a path past
    /// Windows' 260-character limit, with that file absent from the working tree (exactly the state the mcqdb
    /// slot was stuck in). The entry is written straight into the index with <c>update-index --cacheinfo</c>, so
    /// the setup itself never has to create the long path — only the <c>reset --hard</c> under test does.
    /// </summary>
    private async Task<string> SetupStoreWithLongPathAsync(HostGitCommandRunner runner)
    {
        var store = Path.Combine(_root, "store");
        Directory.CreateDirectory(store);

        async Task<SandboxCommandResult> Git(params string[] args)
        {
            var r = await runner.RunAsync(new SandboxCommand(["git", .. args], store), default);
            r.Succeeded.Should().BeTrue($"setup `git {string.Join(' ', args)}` failed: {r.Stderr}");
            return r;
        }

        await Git("init", "-q", ".");
        await File.WriteAllTextAsync(Path.Combine(store, "seed.txt"), "seed\n");
        await Git("add", "seed.txt");
        await Git("-c", "user.email=a@b", "-c", "user.name=a", "commit", "-q", "-m", "seed");

        var segment = new string('p', 40);
        var longPath = string.Join('/', Enumerable.Repeat(segment, 5)) + "/beyond-max-path.txt";
        (store.Length + 1 + longPath.Length).Should().BeGreaterThan(
            260, "the regression only exists past MAX_PATH — a shorter temp root would silently pass");

        var blob = (await Git("rev-parse", "HEAD:seed.txt")).Stdout.Trim();
        await Git("update-index", "--add", "--cacheinfo", $"100644,{blob},{longPath}");
        await Git("-c", "user.email=a@b", "-c", "user.name=a", "commit", "-q", "-m", "long");
        return store;
    }

    private string SeedStore()
    {
        var store = Path.Combine(_root, "store");
        Directory.CreateDirectory(Path.Combine(store, ".git"));
        return store;
    }
}
