using CodeReviewDaemon.Sample.Workspace.Git;

namespace CodeReviewDaemon.Sample.Workspace;

/// <summary>Result of a clean-on-entry pass over a leased slot's store.</summary>
internal enum HygieneVerdict
{
    /// <summary>Store is usable — stale state was cleared in place (a non-corrupt submodule-restore failure is
    /// non-fatal: the review re-establishes submodules, so the slot still counts as usable).</summary>
    Clean,

    /// <summary>Store is structurally broken (or its content is corrupt) — the caller must re-clone it.</summary>
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
    /// <summary>
    /// git <c>-c</c> args that DENY every network transport, prepended to the hygiene submodule restore so it can
    /// only do a LOCAL checkout and can NEVER clone/fetch through the host's broad credentials (a
    /// registered-but-deinit'd submodule otherwise drives <c>submodule--helper clone</c> — reproduced on Git
    /// 2.53; <c>--no-fetch</c> alone does NOT stop that). A command-line <c>-c protocol.&lt;name&gt;.allow=never</c>
    /// beats any config that tries to <c>allow</c> it (verified), and propagates to the internal clone/fetch via
    /// <c>GIT_CONFIG_PARAMETERS</c>. Explicit per-protocol denials cover the network transports (<see cref="GitRunner"/>
    /// already denies <c>file</c>/<c>ext</c> globally); <c>protocol.allow=never</c> is the catch-all default for
    /// any other/future transport. A present object is unaffected — a local checkout uses no transport.
    /// </summary>
    private static readonly string[] DenyNetworkArgs =
    [
        "-c", "protocol.allow=never",
        "-c", "protocol.http.allow=never",
        "-c", "protocol.https.allow=never",
        "-c", "protocol.ssh.allow=never",
        "-c", "protocol.git.allow=never",
        "-c", "protocol.ftp.allow=never",
        "-c", "protocol.ftps.allow=never",
    ];

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
        //    SECURITY: hygiene runs on the host runner with the daemon's broad provider credentials, BEFORE
        //    ReviewSlotPreparer builds this run's policy-enforced SubmoduleInitializer, so it must NEVER touch a
        //    remote — otherwise a prior lease that left a submodule registered-but-deinit'd (worktree +
        //    `.git/modules/<name>` gitdir removed, URL retained) would make `submodule update` CLONE it through
        //    those broad credentials, outside this review's allow-list. `--no-fetch` alone is NOT sufficient: it
        //    only suppresses fetch into an existing submodule repo; a missing gitdir still drives
        //    `submodule--helper clone` → `git clone` (reproduced on Git 2.53). The hard guard is
        //    <see cref="DenyNetworkArgs"/> (explicit per-protocol <c>never</c> for every transport), which denies
        //    all clone/fetch (propagated to the internal clone/fetch via GIT_CONFIG_PARAMETERS): a present object
        //    is a pure LOCAL checkout, while any clone/fetch fails with no network contact — the step-4 gate then
        //    classifies that failure (the policy-controlled initializer is the only thing that performs permitted
        //    network fetches). `--no-fetch` is kept as belt-and-braces.
        var reset = await git.RunAsync(["-C", storePath, "reset", "--hard"], storePath, ct).ConfigureAwait(false);
        var clean = await git.RunAsync(["-C", storePath, "clean", "-ffdx"], storePath, ct).ConfigureAwait(false);
        var restore = await git.RunAsync(
                ["-C", storePath, .. DenyNetworkArgs,
                    "submodule", "update", "--recursive", "--no-fetch", "--checkout", "--force"],
                storePath, ct)
            .ConfigureAwait(false);

        // 4. Cleanliness gate for the SUPERPROJECT + corruption. If the superproject reset/clean failed the store
        //    is structurally unusable — re-clone. A submodule RESTORE failure is classified: confirmed corruption
        //    (a broken local object/repo) re-clones; a transient/unrecognized/missing-object/deinit'd-submodule
        //    failure is NON-fatal and PROCEEDS. Hygiene never fetches (<see cref="DenyNetworkArgs"/>), so it cannot
        //    fix a missing/stale submodule itself — but it does not need to: the review re-establishes EVERY
        //    submodule downstream (FetchAndCheckoutHead for the reviewed repo + the run's policy-enforced
        //    SubmoduleInitializer for nested submodules, both with PERMITTED fetches), so a stale/missing submodule
        //    between leases is superseded and never contaminates a review. Blocking here would either discard a
        //    healthy warm store (reclone) or loop forever on a deterministic missing-object (retry — which hygiene
        //    can't fetch and which never reaches the initializer). Submodule state is therefore left to the review;
        //    the status probe below ignores submodules for the same reason.
        if (!reset.Succeeded || !clean.Succeeded)
        {
            return HygieneVerdict.NeedsReclone;
        }

        if (!restore.Succeeded)
        {
            if (GitFailureClassifier.Classify(restore.Stderr) == GitFailureKind.Corrupt)
            {
                logger?.LogWarning(
                    "Slot hygiene at {StorePath}: submodule restore failed with CORRUPTION; re-cloning: {Stderr}",
                    storePath, restore.Stderr);
                return HygieneVerdict.NeedsReclone;
            }

            logger?.LogInformation(
                "Slot hygiene at {StorePath}: submodule restore did not complete locally ({Stderr}); proceeding — "
                    + "the review re-establishes submodules with permitted fetches.",
                storePath, restore.Stderr);
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

        // Status probe. When the submodule content cleanup (step 5 foreach) SUCCEEDED, each submodule's working
        // tree is clean, so ignore submodule state here (`--ignore-submodules=all`): the moved/stale POINTER is
        // the review's to re-establish (step 4) and gating on it drives the warm-slot re-clone churn this path
        // avoids. But if the foreach FAILED, do NOT hide submodule state — fall back to a full status so leftover
        // dirty submodule content the foreach couldn't clean is CAUGHT (→ reclone) rather than masked. An
        // uninitialized stray gitlink (the PR-11182 wedge) still shows clean under a full status, so this doesn't
        // reintroduce the wedge reclone-loop. Only leftover SUPERPROJECT state (always checked) means contamination.
        string[] statusArgs = submodules.Succeeded
            ? ["-C", storePath, "status", "--porcelain", "--ignore-submodules=all"]
            : ["-C", storePath, "status", "--porcelain"];
        var status = await git.RunAsync(statusArgs, storePath, ct).ConfigureAwait(false);
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
        // DenyNetworkArgs (+ --no-fetch) is REQUIRED (same reason as step 3): hygiene must never contact a remote
        // through the host's broad credentials outside a review's policy-enforced allow-list.
        await git.RunAsync(
                ["-C", storePath, .. DenyNetworkArgs,
                    "submodule", "update", "--recursive", "--no-fetch", "--checkout", "--force"],
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
