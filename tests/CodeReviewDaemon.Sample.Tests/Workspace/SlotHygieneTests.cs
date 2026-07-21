using CodeReviewDaemon.Sample.Tests.Infrastructure;
using CodeReviewDaemon.Sample.Workspace;
using CodeReviewDaemon.Sample.Workspace.Git;
using CodeReviewDaemon.Sample.Workspace.Sandbox;

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
        // Regression guard for warm-slot re-clone churn: a prior lease leaves the reviewed submodule checked out
        // at the PR head, which the superproject sees as a moved pointer (dirty). Without a `submodule update
        // --force` to restore it to the recorded gitlink, every warm slot looks dirty and is needlessly
        // re-cloned. `submodule foreach reset --hard` alone does NOT fix this — it resets to the submodule's own
        // (PR-head) HEAD, not the superproject's gitlink.
        var store = SeedStore();
        var runner = new FakeSandboxCommandRunner();

        await SlotHygiene.EnsureCleanAsync(new GitRunner(runner), store, CancellationToken.None);

        var commands = runner.Commands.Select(c => string.Join(' ', c.Argv)).ToList();
        commands.Should().Contain(
            a => a.Contains("submodule update --force"),
            "submodule checkouts must be restored to the recorded gitlink so a warm slot is not re-cloned");
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
    public async Task EnsureClean_reports_NeedsReclone_when_submodule_restore_fails()
    {
        // A failed `git submodule update --force` may leave a submodule off its recorded gitlink — which
        // `status --porcelain` can hide (submodule-ignore settings) — so it must NOT be reported as Clean.
        // Re-clone instead of letting stale submodule state cross into the next lease.
        var store = SeedStore();
        var runner = new FakeSandboxCommandRunner();
        runner.OnArgvContains(
            "submodule update --force", new SandboxCommandResult(1, string.Empty, "fatal: could not restore gitlink"));

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
    public async Task StripAsync_issues_reset_and_clean()
    {
        var store = SeedStore();
        var runner = new FakeSandboxCommandRunner();

        await SlotHygiene.StripAsync(new GitRunner(runner), store, CancellationToken.None);

        var commands = runner.Commands.Select(c => string.Join(' ', c.Argv)).ToList();
        commands.Should().Contain(a => a.Contains("reset --hard"));
        commands.Should().Contain(a => a.Contains("clean -ffdx"));
    }

    private string SeedStore()
    {
        var store = Path.Combine(_root, "store");
        Directory.CreateDirectory(Path.Combine(store, ".git"));
        return store;
    }
}
