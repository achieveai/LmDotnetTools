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
    public async Task EnsureClean_reports_NeedsReclone_when_foreach_fails_and_submodule_content_is_dirty()
    {
        // When the submodule content cleanup (foreach) FAILS, hygiene must NOT hide submodule state: it falls back
        // to a FULL status (no --ignore-submodules) so leftover DIRTY submodule content the foreach couldn't clean
        // is caught (→ reclone) rather than masked and allowed to cross into the next lease.
        var store = SeedStore();
        var runner = new FakeSandboxCommandRunner();
        runner.OnArgvContains(
            "submodule foreach --recursive",
            new SandboxCommandResult(1, string.Empty, "warning: could not reset submodule (read-only file)"));
        runner.OnArgvContains(
            "status --porcelain",
            new SandboxCommandResult(0, " M repos/X/leftover.cs\n", string.Empty));

        var verdict = await SlotHygiene.EnsureCleanAsync(new GitRunner(runner), store, CancellationToken.None);

        verdict.Should().Be(HygieneVerdict.NeedsReclone);
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

    private string SeedStore()
    {
        var store = Path.Combine(_root, "store");
        Directory.CreateDirectory(Path.Combine(store, ".git"));
        return store;
    }
}
