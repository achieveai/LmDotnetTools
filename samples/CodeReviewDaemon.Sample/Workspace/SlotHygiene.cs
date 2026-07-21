using CodeReviewDaemon.Sample.Workspace.Git;

namespace CodeReviewDaemon.Sample.Workspace;

/// <summary>Result of a clean-on-entry pass over a leased slot's store.</summary>
internal enum HygieneVerdict
{
    /// <summary>Store is usable — stale state was cleared in place.</summary>
    Clean,

    /// <summary>Store is structurally broken (or its content is corrupt) — the caller must re-clone it.</summary>
    NeedsReclone,

    /// <summary>Hygiene hit a transient/unrecognized failure (e.g. a submodule restore that couldn't complete
    /// locally). NOT confirmed corruption — the caller should retry the warm store rather than destructively
    /// re-cloning it. Mirrors the store-checkout ladder's transient/unknown path.</summary>
    NeedsRetry,
}

/// <summary>
/// Brings a leased pooled slot's store to a pristine state at the START of every prepare (clean-on-entry,
/// the durability guarantee) and strips it back to pristine on a successful close (best-effort tidiness).
/// Safe because the pool leases a slot to at most one run at a time, so a leased slot has no concurrent git
/// process — any <c>*.lock</c> in it is stale by definition and safe to remove. Lock/abort steps are host
/// filesystem operations (the pooled store lives on the daemon host); reset/clean run through the host
/// <see cref="GitRunner"/>. See the design doc §3–§4.
/// </summary>
internal static class SlotHygiene
{
    public static async Task<HygieneVerdict> EnsureCleanAsync(
        GitRunner git, string storePath, CancellationToken ct, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(git);
        ArgumentException.ThrowIfNullOrWhiteSpace(storePath);

        var gitDir = Path.Combine(storePath, ".git");
        if (!Directory.Exists(gitDir) && !File.Exists(gitDir))
        {
            return HygieneVerdict.NeedsReclone; // never cloned, or the store dir was blown away
        }

        // 1. Clear stale locks anywhere under .git (store + every submodule gitdir under .git/modules).
        RemoveStaleLocks(gitDir);

        // 2. Abort any in-progress merge/rebase/cherry-pick left by an interrupted prior lease.
        AbortInProgress(gitDir);

        // 3. Reset + clean the superproject, then restore ALL submodule checkouts (top-level AND nested,
        //    recursively) to the superproject's RECORDED gitlink. Restoring to the gitlink keeps a warm slot
        //    reusable: a prior lease left the reviewed submodule — and, since the review path initializes
        //    submodules recursively, its nested submodules — checked out at PR-head/agent commits, which the
        //    superproject sees as moved pointers (`git status` reports dirty). The `submodule foreach` (step 5)
        //    only resets each submodule to its OWN HEAD, not the recorded (nested) gitlink, so it does NOT fix
        //    this. `--recursive --checkout --force` (NO --init) touches only already-initialized,
        //    .gitmodules-registered submodules at every depth, so it skips a committed embedded gitlink with no
        //    .gitmodules URL (the PR-11182 wedge) and never inits a new/denied submodule.
        //    SECURITY: `--no-fetch` is REQUIRED. Hygiene runs on the host runner with the daemon's broad provider
        //    credentials, BEFORE ReviewSlotPreparer builds this run's policy-enforced SubmoduleInitializer — so a
        //    fetch here would bypass the per-review submodule allow-list and could contact a retained nested
        //    remote outside this review's scope (the recursive expansion widens that surface). `--no-fetch` makes
        //    this a pure LOCAL checkout: a present object checks out, a MISSING object fails with no network
        //    contact, which the step-4 gate then treats as a re-clone condition (the policy-controlled
        //    initializer is the only thing that performs permitted network fetches).
        var reset = await git.RunAsync(["-C", storePath, "reset", "--hard"], storePath, ct).ConfigureAwait(false);
        var clean = await git.RunAsync(["-C", storePath, "clean", "-ffdx"], storePath, ct).ConfigureAwait(false);
        var restore = await git.RunAsync(
                ["-C", storePath, "submodule", "update", "--recursive", "--no-fetch", "--checkout", "--force"],
                storePath, ct)
            .ConfigureAwait(false);

        // 4. Early cleanliness gate. If the superproject reset/clean failed the store is structurally unusable —
        //    re-clone. A submodule RESTORE failure is classified, NOT unconditionally re-cloned: `--no-fetch`
        //    means the restore is a pure local checkout, so a failure is either confirmed corruption (a broken
        //    local object/repo → re-clone fixes it) or a transient/unrecognized/missing-object condition. The
        //    latter must NOT destructively delete + re-clone the persistent store — it should retry the warm
        //    store (and, for a legitimately missing gitlink object, the run's policy-enforced SubmoduleInitializer
        //    performs the PERMITTED fetch). This mirrors the store-checkout ladder (only Corrupt re-clones;
        //    Unknown is "treated as transient"). Either way we do NOT fall through to `status --porcelain`, which
        //    can hide a submodule left off its recorded gitlink under submodule-ignore settings.
        if (!reset.Succeeded || !clean.Succeeded)
        {
            return HygieneVerdict.NeedsReclone;
        }

        if (!restore.Succeeded)
        {
            var corrupt = GitFailureClassifier.Classify(restore.Stderr) == GitFailureKind.Corrupt;
            logger?.LogWarning(
                "Slot hygiene at {StorePath}: `git submodule update --no-fetch` (restore submodules to the "
                    + "recorded gitlink) failed ({Classification}); {Action}: {Stderr}",
                storePath,
                corrupt ? "corrupt" : "transient/unknown",
                corrupt ? "re-cloning" : "retrying the warm store (no destructive reclone)",
                restore.Stderr);
            return corrupt ? HygieneVerdict.NeedsReclone : HygieneVerdict.NeedsRetry;
        }

        // 5. Clean every submodule working tree, then structurally probe. Only reached once reset/clean/restore
        //    succeeded, so these never run on an already-doomed store.
        var submodules = await git.RunAsync(
                ["-C", storePath, "submodule", "foreach", "--recursive", "git reset --hard && git clean -ffdx"],
                storePath, ct)
            .ConfigureAwait(false);

        var probe = await git.RunAsync(["-C", storePath, "rev-parse", "--git-dir"], storePath, ct)
            .ConfigureAwait(false);
        if (!probe.Succeeded)
        {
            return HygieneVerdict.NeedsReclone;
        }

        // A `git submodule foreach` failure is deliberately NOT re-clone-gated: it fatals on a committed
        // embedded gitlink with no .gitmodules URL (an agent-left nested repo — the PR-11182 wedge), and a
        // re-clone reproduces that same committed tree, so gating on it loops forever without ever healing. The
        // superproject reset/clean above and the status probe below already prove the working tree is clean
        // (an uninitialized stray gitlink shows as clean), so log the best-effort cleanup failure and continue.
        if (!submodules.Succeeded)
        {
            logger?.LogWarning(
                "Slot hygiene at {StorePath}: `git submodule foreach` cleanup failed (continuing — a re-clone "
                    + "cannot fix committed content; the status probe still gates cleanliness): {Stderr}",
                storePath, submodules.Stderr);
        }

        var status = await git.RunAsync(["-C", storePath, "status", "--porcelain"], storePath, ct)
            .ConfigureAwait(false);
        return status.Succeeded && string.IsNullOrWhiteSpace(status.Stdout)
            ? HygieneVerdict.Clean
            : HygieneVerdict.NeedsReclone;
    }

    /// <summary>
    /// Success-path strip: the caller commits + pushes the notes FIRST, then this returns the slot pristine
    /// (best-effort — if it is skipped by a crash, the next lease's <see cref="EnsureCleanAsync"/> covers it).
    /// </summary>
    public static async Task StripAsync(GitRunner git, string storePath, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(git);
        ArgumentException.ThrowIfNullOrWhiteSpace(storePath);

        RemoveStaleLocks(Path.Combine(storePath, ".git"));
        await git.RunAsync(["-C", storePath, "reset", "--hard"], storePath, ct).ConfigureAwait(false);
        await git.RunAsync(["-C", storePath, "clean", "-ffdx"], storePath, ct).ConfigureAwait(false);
        // Restore ALL submodule checkouts (top-level + nested) to the recorded gitlink (see EnsureCleanAsync
        // step 3) so the slot is left pristine for the next lease instead of pinned at this review's PR head.
        // `--no-fetch` is REQUIRED (same reason as step 3): hygiene must never fetch through the host's broad
        // credentials outside a review's policy-enforced allow-list.
        await git.RunAsync(
                ["-C", storePath, "submodule", "update", "--recursive", "--no-fetch", "--checkout", "--force"],
                storePath, ct)
            .ConfigureAwait(false);
        await git.RunAsync(
                ["-C", storePath, "submodule", "foreach", "--recursive", "git reset --hard && git clean -ffdx"],
                storePath, ct)
            .ConfigureAwait(false);
    }

    private static void RemoveStaleLocks(string gitDir)
    {
        if (!Directory.Exists(gitDir))
        {
            return; // a submodule's .git is a gitfile; its real dir is reached via the parent .git/modules.
        }

        foreach (var lockFile in Directory.EnumerateFiles(gitDir, "*.lock", SearchOption.AllDirectories))
        {
            TryDelete(lockFile);
        }
    }

    private static void AbortInProgress(string gitDir)
    {
        foreach (var marker in new[] { "MERGE_HEAD", "CHERRY_PICK_HEAD", "REVERT_HEAD" })
        {
            TryDelete(Path.Combine(gitDir, marker));
        }

        foreach (var dir in new[] { "rebase-merge", "rebase-apply" })
        {
            var path = Path.Combine(gitDir, dir);
            if (Directory.Exists(path))
            {
                try
                {
                    Directory.Delete(path, recursive: true);
                }
                catch
                {
                    // Best-effort; a leftover rebase dir will not, on its own, block reset --hard.
                }
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best-effort; the next lease retries. A lock we cannot delete surfaces as a corrupt-slot failure.
        }
    }
}
