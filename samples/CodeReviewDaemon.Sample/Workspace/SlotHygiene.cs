using CodeReviewDaemon.Sample.Workspace.Git;

namespace CodeReviewDaemon.Sample.Workspace;

/// <summary>Result of a clean-on-entry pass over a leased slot's store.</summary>
internal enum HygieneVerdict
{
    /// <summary>Store is usable — stale state was cleared in place.</summary>
    Clean,

    /// <summary>Store is structurally broken — the caller must re-clone it before use.</summary>
    NeedsReclone,
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
        GitRunner git, string storePath, CancellationToken ct)
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

        // 3. Reset + clean the superproject, then every submodule working tree.
        var reset = await git.RunAsync(["-C", storePath, "reset", "--hard"], storePath, ct).ConfigureAwait(false);
        var clean = await git.RunAsync(["-C", storePath, "clean", "-ffdx"], storePath, ct).ConfigureAwait(false);
        var submodules = await git.RunAsync(
                ["-C", storePath, "submodule", "foreach", "--recursive", "git reset --hard && git clean -ffdx"],
                storePath, ct)
            .ConfigureAwait(false);

        // 4. Structural probe — a missing/broken gitdir means the warm store is unusable and must be re-cloned.
        var probe = await git.RunAsync(["-C", storePath, "rev-parse", "--git-dir"], storePath, ct)
            .ConfigureAwait(false);
        if (!probe.Succeeded)
        {
            return HygieneVerdict.NeedsReclone;
        }

        // 5. Cleanliness gate: `rev-parse --git-dir` only proves the repo STRUCTURE is intact, not that the tree
        //    is CLEAN. A cleanup step that reported failure, or a working tree still showing tracked/untracked
        //    changes afterwards, means stale state would cross into the next run — so force a re-clone rather
        //    than reporting a still-dirty slot as Clean (which would let contamination survive the pool).
        if (!reset.Succeeded || !clean.Succeeded || !submodules.Succeeded)
        {
            return HygieneVerdict.NeedsReclone;
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
    public static async Task StripAsync(
        GitRunner git, string storePath, CancellationToken ct, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(git);
        ArgumentException.ThrowIfNullOrWhiteSpace(storePath);

        RemoveStaleLocks(Path.Combine(storePath, ".git"));
        var reset = await git.RunAsync(["-C", storePath, "reset", "--hard"], storePath, ct).ConfigureAwait(false);
        var clean = await git.RunAsync(["-C", storePath, "clean", "-ffdx"], storePath, ct).ConfigureAwait(false);
        var submodules = await git.RunAsync(
                ["-C", storePath, "submodule", "foreach", "--recursive", "git reset --hard && git clean -ffdx"],
                storePath, ct)
            .ConfigureAwait(false);

        // Best-effort strip: a failed reset/clean/submodule cleanup does NOT throw here (that would block the
        // slot's return and leak pool capacity — clean-on-entry is the real durability guarantee). But silently
        // discarding the failed results would leave the next lease to find a dirty store with no breadcrumb, so
        // surface it as a warning the operator (and the next clean-on-entry / reclone policy) can act on.
        if (!reset.Succeeded || !clean.Succeeded || !submodules.Succeeded)
        {
            logger?.LogWarning(
                "Slot strip at {StorePath} left residue (reset={Reset} clean={Clean} submodules={Submodules}); "
                    + "the next lease's clean-on-entry re-covers it.",
                storePath, reset.Succeeded, clean.Succeeded, submodules.Succeeded);
        }
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
