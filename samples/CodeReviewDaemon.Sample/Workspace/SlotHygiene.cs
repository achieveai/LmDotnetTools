using CodeReviewDaemon.Sample.Workspace.Git;
using CodeReviewDaemon.Sample.Workspace.Sandbox;

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

    /// <summary>
    /// Clean-on-entry. <paramref name="fileSystem"/> is the filesystem the store lives on: when it is the host
    /// filesystem the stale-lock/abandoned-operation sweep runs through the host helpers below instead of the
    /// container commands (see the step 1-2 comment). Null keeps the container behaviour.
    /// </summary>
    public static async Task<HygieneVerdict> EnsureCleanAsync(
        GitRunner git,
        string storePath,
        CancellationToken ct,
        ILogger? logger = null,
        ISandboxFileSystem? fileSystem = null)
    {
        ArgumentNullException.ThrowIfNull(git);
        ArgumentException.ThrowIfNullOrWhiteSpace(storePath);

        var structuralProbe = await git.RunAsync(["-C", storePath, "rev-parse", "--git-dir"], storePath, ct)
            .ConfigureAwait(false);
        if (!structuralProbe.Succeeded)
        {
            logger?.LogWarning(
                "Slot hygiene at {StorePath}: re-cloning — the store has no readable git dir ({Stderr}).",
                storePath, structuralProbe.Stderr);
            return HygieneVerdict.NeedsReclone;
        }

        // Force-reset ladder. One pass sweeps stale locks/markers and resets the store; if that leaves the
        // SUPERPROJECT unsettled in a way a second attempt can plausibly change, sweep and reset again before
        // condemning the slot. Re-cloning a persistent store costs minutes, so it is the last resort, not the
        // first: a lock that survived one sweep, or a tree still dirty after one reset, is far more often
        // clearable than it is a broken store.
        var pass = await ForceResetAsync(git, storePath, ct, fileSystem).ConfigureAwait(false);

        // A store that redirects its own cleanup is condemned rather than repaired: removing the link would be a
        // write chosen by whoever planted it, and re-creating the directory afterwards hands the next one a fresh
        // target. The re-clone is the safe answer precisely because it does not walk the tree — it wipes the whole
        // store, unlinking the redirected entry instead of following it. A second pass cannot change this, so the
        // gate goes ahead of the retry ladder. See <see cref="HostPathGuard.IsRedirected"/>.
        if (pass.RedirectedPath is { } redirected)
        {
            logger?.LogWarning(
                "Slot hygiene at {StorePath}: re-cloning — {RedirectedPath} is a symlink or junction, so the "
                    + "stale-state sweep would have deleted files outside the store. Refusing to sweep past it, "
                    + "and refusing to remove it.",
                storePath, redirected);
            return HygieneVerdict.NeedsReclone;
        }

        var status = await SuperprojectStatusAsync(git, storePath, ct).ConfigureAwait(false);

        if (ShouldForceResetAgain(pass, status))
        {
            logger?.LogWarning(
                "Slot hygiene at {StorePath}: first pass left the store unsettled (reset: {ResetErr}; clean: "
                    + "{CleanErr}; restore: {RestoreErr}; foreach: {ForeachErr}; status: {Status}); force-resetting "
                    + "once more before condemning it.",
                storePath, pass.Reset.Stderr, pass.Clean.Stderr, pass.Restore.Stderr, pass.Foreach.Stderr,
                status.Stdout);
            pass = await ForceResetAsync(git, storePath, ct, fileSystem).ConfigureAwait(false);
            status = await SuperprojectStatusAsync(git, storePath, ct).ConfigureAwait(false);
        }

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
        if (!pass.Reset.Succeeded || !pass.Clean.Succeeded)
        {
            logger?.LogWarning(
                "Slot hygiene at {StorePath}: re-cloning — the superproject would not reset/clean even after a "
                    + "force reset (reset: {ResetErr}; clean: {CleanErr}).",
                storePath, pass.Reset.Stderr, pass.Clean.Stderr);
            return HygieneVerdict.NeedsReclone;
        }

        if (!pass.Restore.Succeeded)
        {
            if (GitFailureClassifier.Classify(pass.Restore.Stderr) == GitFailureKind.Corrupt)
            {
                logger?.LogWarning(
                    "Slot hygiene at {StorePath}: submodule restore failed with CORRUPTION; re-cloning: {Stderr}",
                    storePath, pass.Restore.Stderr);
                return HygieneVerdict.NeedsReclone;
            }

            logger?.LogInformation(
                "Slot hygiene at {StorePath}: submodule restore did not complete locally ({Stderr}); proceeding — "
                    + "the review re-establishes submodules with permitted fetches.",
                storePath, pass.Restore.Stderr);
        }

        var probe = await git.RunAsync(["-C", storePath, "rev-parse", "--git-dir"], storePath, ct)
            .ConfigureAwait(false);
        if (!probe.Succeeded)
        {
            logger?.LogWarning(
                "Slot hygiene at {StorePath}: re-cloning — the git dir became unreadable while cleaning ({Stderr}).",
                storePath, probe.Stderr);
            return HygieneVerdict.NeedsReclone;
        }

        // A `git submodule foreach` failure re-clones when it classifies as corruption (e.g. a lock that survived
        // two sweeps, a broken object) — that is the one kind a fresh clone actually repairs — or when it left
        // untracked residue behind (the gate below). The failure ITSELF is otherwise left alone on purpose,
        // because a re-clone reproduces the same committed tree and therefore loops forever without ever healing.
        // Two such failures are known and neither is fixable by cloning:
        // a committed embedded gitlink with no .gitmodules URL (the PR-11182 wedge), and a path at or beyond
        // Windows' MAX_PATH inside a nested submodule — `reset --hard` cannot re-create the file ("Filename too
        // long") and a clone cannot check it out either, so the slot was re-cloned on every lease forever.
        // (GitRunner now passes core.longpaths=true, which fixes that class at the source; this gate is the
        // durability guarantee for whatever unrepairable content comes next.)
        if (!pass.Foreach.Succeeded)
        {
            if (GitFailureClassifier.Classify(pass.Foreach.Stderr) == GitFailureKind.Corrupt)
            {
                logger?.LogWarning(
                    "Slot hygiene at {StorePath}: `git submodule foreach` cleanup failed with CORRUPTION that "
                        + "survived a force reset; re-cloning: {Stderr}",
                    storePath, pass.Foreach.Stderr);
                return HygieneVerdict.NeedsReclone;
            }

            // Tolerating the FAILURE is not the same as tolerating what it left behind. `submodule foreach` stops
            // at the first submodule whose command fails, so every submodule after that one was never cleaned —
            // and nothing else in this pass reaches them: the superproject's `clean -ffdx` does not descend into a
            // registered submodule's working tree, and `submodule update --checkout --force` only restores TRACKED
            // content. One review's UNTRACKED leftovers therefore survived the lease and crossed into the next
            // review. Sweep the whole list again with a command that cannot abort the walk, and condemn the slot
            // only for what is still there afterwards.
            var residue = await SubmoduleResidueAsync(git, storePath, ct).ConfigureAwait(false);
            if (residue is { } left)
            {
                logger?.LogWarning(
                    "Slot hygiene at {StorePath}: re-cloning — `git submodule foreach` cleanup failed ({Stderr}) "
                        + "and untracked content is still in a submodule after a second sweep, so it would cross "
                        + "into the next review: {Residue}",
                    storePath, pass.Foreach.Stderr, left);
                return HygieneVerdict.NeedsReclone;
            }

            logger?.LogWarning(
                "Slot hygiene at {StorePath}: `git submodule foreach` cleanup failed but left no untracked residue "
                    + "(continuing — a re-clone cannot fix committed content, and the review overwrites the "
                    + "reviewed submodule with an explicit checkout of the PR head): {Stderr}",
                storePath, pass.Foreach.Stderr);
        }

        // Superproject status gate. Submodule state is deliberately ignored (`--ignore-submodules=all`): a moved
        // or dirty submodule is the REVIEW's to re-establish (see step 4), and gating on it condemns warm slots
        // whose only sin is holding the previous PR's head — or, worse, condemns them over content no clone can
        // reproduce cleanly. Leftover SUPERPROJECT state is the contamination that would actually cross into the
        // next review, and it is the only thing here a fresh clone is guaranteed to fix.
        if (!status.Succeeded || !string.IsNullOrWhiteSpace(status.Stdout))
        {
            logger?.LogWarning(
                "Slot hygiene at {StorePath}: re-cloning — the superproject is still dirty after a force reset "
                    + "({Status}{Stderr}).",
                storePath, status.Stdout, status.Stderr);
            return HygieneVerdict.NeedsReclone;
        }

        return HygieneVerdict.Clean;
    }

    /// <summary>
    /// Reads the SUPERPROJECT's working-tree status, ignoring submodule state (see the gate in
    /// <see cref="EnsureCleanAsync"/> for why submodules are excluded).
    /// </summary>
    private static Task<SandboxCommandResult> SuperprojectStatusAsync(
        GitRunner git, string storePath, CancellationToken ct) =>
        git.RunAsync(["-C", storePath, "status", "--porcelain", "--ignore-submodules=all"], storePath, ct);

    /// <summary>
    /// Re-runs the submodule clean so it CANNOT abort the walk, then reports what untracked content is still
    /// present anywhere in the submodule tree, or <c>null</c> when there is none.
    /// <para>
    /// Only UNTRACKED residue counts, and that narrowness is the point: the restore step already put every
    /// submodule back on its recorded gitlink, and the tracked state that outlives it is exactly the unrepairable
    /// kind the caller tolerates on purpose — the mcqdb MAX_PATH wedge leaves a tracked file DELETED, which a
    /// fresh clone cannot check out either, so condemning on it re-creates the forever-re-clone loop. Untracked
    /// residue is the opposite on both counts: it is a previous lease's leftovers rather than the repo's own
    /// content, and a fresh clone is guaranteed to arrive without it.
    /// </para>
    /// <para>
    /// A probe that could not run reports itself AS residue instead of being read as an empty status — a check
    /// whose clean answer is produced by its own failure is not a check. That cannot wedge the slot the way
    /// failing closed on the foreach itself would: a re-clone leaves the submodules UNINITIALIZED, so
    /// <c>foreach</c> visits nothing on the next lease and there is nothing left to report. The single exception
    /// is a probe that failed because a registered submodule's gitdir is gone
    /// (<see cref="GitFailureClassifier.IsDeinitializedSubmodule"/>), which the caller already tolerates.
    /// </para>
    /// <para>
    /// Two known residuals, both accepted for the same reason — a re-clone does not observe the content either,
    /// so condemning the slot spends a full clone to learn nothing. First, the deinitialized-submodule deferral
    /// above stops the walk at that submodule, so untracked leftovers in submodules ORDERED AFTER it go
    /// unreported. Second, <c>foreach</c> skips uninitialized submodules outright, so it never visits them at
    /// all; there, the superproject's own <c>clean -ffdx</c> is what takes the residue, because an uninitialized
    /// submodule's path holds no repository to stop it descending.
    /// </para>
    /// </summary>
    private static async Task<string?> SubmoduleResidueAsync(GitRunner git, string storePath, CancellationToken ct)
    {
        // `|| true` per submodule, because without it this second sweep aborts at the same submodule the first one
        // did and never reaches the ones holding the residue — which is the whole defect being closed. `--quiet`
        // suppresses the per-submodule "Entering '<path>'" banner so the probe's stdout is status output alone.
        _ = await git.RunAsync(
                ["-C", storePath, "submodule", "--quiet", "foreach", "--recursive", "git clean -ffdx || true"],
                storePath, ct)
            .ConfigureAwait(false);

        var probe = await git.RunAsync(
                ["-C", storePath, "submodule", "--quiet", "foreach", "--recursive",
                    "git status --porcelain || echo '?? (submodule status unavailable)'"],
                storePath, ct)
            .ConfigureAwait(false);
        if (!probe.Succeeded)
        {
            // …unless it failed for the one reason the caller's classifier has ALREADY decided to tolerate. The
            // probe is itself a `submodule foreach`, so a registered submodule whose gitdir is gone fails it with
            // the same stderr that got the sweep tolerated one gate earlier — and reporting that as residue would
            // condemn the slot on exactly the state hygiene just chose to keep, on every lease, forever. Deferring
            // costs less than it looks: a re-clone leaves submodules UNINITIALIZED, so the next lease's `foreach`
            // visits nothing and reports the same nothing. The condemnation would buy no information and spend a
            // full re-clone of the store to do it.
            if (GitFailureClassifier.IsDeinitializedSubmodule(probe.Stderr))
            {
                return null;
            }

            return $"the submodule status probe itself failed: {probe.Stderr}";
        }

        return probe.Stdout
            ?.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(line => line.StartsWith("??", StringComparison.Ordinal));
    }

    /// <summary>
    /// Is a second force-reset pass worth its cost? Only when the failure is one a repeated sweep-and-reset can
    /// plausibly clear: a superproject that would not reset/clean, a superproject still dirty afterwards, or a
    /// submodule step that failed with CORRUPTION (typically a lock that outlived the first sweep). A
    /// deterministic non-corrupt failure — a missing local object, a deinit'd submodule, a committed embedded
    /// gitlink — is excluded on purpose: retrying it only burns a second pass on every lease and never changes
    /// the outcome.
    /// </summary>
    private static bool ShouldForceResetAgain(ResetPass pass, SandboxCommandResult status) =>
        !pass.Reset.Succeeded
        || !pass.Clean.Succeeded
        || !status.Succeeded
        || !string.IsNullOrWhiteSpace(status.Stdout)
        || (!pass.Restore.Succeeded
            && GitFailureClassifier.Classify(pass.Restore.Stderr) == GitFailureKind.Corrupt)
        || (!pass.Foreach.Succeeded
            && GitFailureClassifier.Classify(pass.Foreach.Stderr) == GitFailureKind.Corrupt);

    /// <summary>The git steps of one force-reset pass, kept together so the verdict can classify each one.
    /// <paramref name="RedirectedPath"/> is the entry that stopped the stale-state sweep before it ran (see
    /// <see cref="HostPathGuard.IsRedirected"/>), or <c>null</c> when the sweep completed.</summary>
    private readonly record struct ResetPass(
        SandboxCommandResult Reset,
        SandboxCommandResult Clean,
        SandboxCommandResult Restore,
        SandboxCommandResult Foreach,
        string? RedirectedPath);

    /// <summary>
    /// One force-reset pass: clear stale locks and abandoned operation markers, then reset and clean the
    /// superproject, restore every submodule checkout to the recorded gitlink, and clean the submodule working
    /// trees. Idempotent by construction, so the caller can run it twice.
    /// </summary>
    private static async Task<ResetPass> ForceResetAsync(
        GitRunner git, string storePath, CancellationToken ct, ISandboxFileSystem? fileSystem)
    {
        var redirected = await SweepStaleStateAsync(git, storePath, ct, fileSystem).ConfigureAwait(false);

        // Reset + clean the superproject, then restore ALL submodule checkouts (top-level AND nested,
        // recursively) to the superproject's RECORDED gitlink. Restoring to the gitlink keeps a warm slot
        // reusable: a prior lease left the reviewed submodule — and, since the review path initializes
        // submodules recursively, its nested submodules — checked out at PR-head/agent commits, which the
        // superproject sees as moved pointers (`git status` reports dirty). The `submodule foreach` below
        // only resets each submodule to its OWN HEAD, not the recorded (nested) gitlink, so it does NOT fix
        // this. `--recursive --checkout --force` (NO --init) touches only already-initialized,
        // .gitmodules-registered submodules at every depth, so it skips a committed embedded gitlink with no
        // .gitmodules URL (the PR-11182 wedge) and never inits a new/denied submodule.
        // SECURITY: hygiene runs on the host runner with the daemon's broad provider credentials, BEFORE
        // ReviewSlotPreparer builds this run's policy-enforced SubmoduleInitializer, so it must NEVER touch a
        // remote — otherwise a prior lease that left a submodule registered-but-deinit'd (worktree +
        // `.git/modules/<name>` gitdir removed, URL retained) would make `submodule update` CLONE it through
        // those broad credentials, outside this review's allow-list. `--no-fetch` alone is NOT sufficient: it
        // only suppresses fetch into an existing submodule repo; a missing gitdir still drives
        // `submodule--helper clone` → `git clone` (reproduced on Git 2.53). The hard guard is
        // <see cref="DenyNetworkArgs"/> (explicit per-protocol <c>never</c> for every transport), which denies
        // all clone/fetch (propagated to the internal clone/fetch via GIT_CONFIG_PARAMETERS): a present object
        // is a pure LOCAL checkout, while any clone/fetch fails with no network contact — the caller's gate
        // then classifies that failure (the policy-controlled initializer is the only thing that performs
        // permitted network fetches). `--no-fetch` is kept as belt-and-braces.
        var reset = await git.RunAsync(["-C", storePath, "reset", "--hard"], storePath, ct).ConfigureAwait(false);
        var clean = await git.RunAsync(["-C", storePath, "clean", "-ffdx"], storePath, ct).ConfigureAwait(false);
        var restore = await git.RunAsync(
                ["-C", storePath, .. DenyNetworkArgs,
                    "submodule", "update", "--recursive", "--no-fetch", "--checkout", "--force"],
                storePath, ct)
            .ConfigureAwait(false);
        var foreachResult = await git.RunAsync(
                ["-C", storePath, "submodule", "foreach", "--recursive", "git reset --hard && git clean -ffdx"],
                storePath, ct)
            .ConfigureAwait(false);

        return new ResetPass(reset, clean, restore, foreachResult, redirected);
    }

    /// <summary>
    /// Clears stale locks and abandoned operation markers THROUGH the injected runner. In production that runner
    /// is SandboxSessionAdapter over typed SandboxClient, so pre-review hygiene never reaches around the mounted
    /// session with host filesystem APIs. Explicit argv keeps every path a distinct token. On the HOST-backed pool
    /// the store is a plain host directory and the runner is a local process runner with no POSIX <c>find</c> (a
    /// Windows daemon host has none) — and this step ignores its result, so there the sweep would fail silently and
    /// leave the wedged store for the reset to trip over. Use the host helpers for that case only.
    /// <para>
    /// Returns the redirected entry that stopped the host sweep, or <c>null</c> when it ran to completion. The
    /// container branch needs no such check: <c>find</c> does not descend through a symlinked directory unless
    /// asked to, and <c>-type f</c>/<c>-type d</c> match the link itself rather than what it points at.
    /// </para>
    /// </summary>
    private static async Task<string?> SweepStaleStateAsync(
        GitRunner git, string storePath, CancellationToken ct, ISandboxFileSystem? fileSystem)
    {
        if (fileSystem is HostFileSystem)
        {
            var gitDir = Path.Combine(storePath, ".git");
            return RemoveStaleLocks(gitDir) ?? AbortInProgress(gitDir);
        }

        await git.CommandRunner.RunAsync(
                new SandboxCommand(
                    [
                        "find",
                        $"{storePath}/.git",
                        "-type",
                        "f",
                        "(",
                        "-name",
                        "*.lock",
                        "-o",
                        "-name",
                        "MERGE_HEAD",
                        "-o",
                        "-name",
                        "CHERRY_PICK_HEAD",
                        "-o",
                        "-name",
                        "REVERT_HEAD",
                        ")",
                        "-delete",
                    ]),
                ct)
            .ConfigureAwait(false);
        await git.CommandRunner.RunAsync(
                new SandboxCommand(
                    [
                        "find",
                        $"{storePath}/.git",
                        "-type",
                        "d",
                        "(",
                        "-name",
                        "rebase-merge",
                        "-o",
                        "-name",
                        "rebase-apply",
                        ")",
                        "-prune",
                        "-exec",
                        "rm",
                        "-rf",
                        "--",
                        "{}",
                        "+",
                    ]),
                ct)
            .ConfigureAwait(false);
        return null;
    }

    /// <summary>
    /// Success-path strip: the caller commits + pushes the notes FIRST, then this returns the slot pristine
    /// (best-effort — if it is skipped by a crash, the next lease's <see cref="EnsureCleanAsync"/> covers it).
    /// </summary>
    public static async Task StripAsync(GitRunner git, string storePath, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(git);
        ArgumentException.ThrowIfNullOrWhiteSpace(storePath);

        // The strip is best-effort tidiness with no verdict to report, so a redirected store simply stops the
        // sweep here; the next lease's EnsureCleanAsync is the gate that condemns it.
        _ = RemoveStaleLocks(Path.Combine(storePath, ".git"));
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

    /// <summary>
    /// Deletes every stale <c>*.lock</c> under <paramref name="gitDir"/>, walking the tree by hand so the sweep
    /// never crosses a symlink or a junction — <c>SearchOption.AllDirectories</c> follows one silently. Returns
    /// the redirected entry that stopped the walk, or <c>null</c> when it finished. See
    /// <see cref="HostPathGuard.IsRedirected"/> for why this refuses instead of clearing the link.
    /// </summary>
    private static string? RemoveStaleLocks(string gitDir)
    {
        if (HostPathGuard.IsRedirected(gitDir))
        {
            return gitDir;
        }

        if (!Directory.Exists(gitDir))
        {
            return null; // a submodule's .git is a gitfile; its real dir is reached via the parent .git/modules.
        }

        var pending = new Stack<string>();
        pending.Push(gitDir);
        while (pending.Count > 0)
        {
            foreach (var entry in ChildrenOf(pending.Pop()))
            {
                if (HostPathGuard.IsRedirected(entry))
                {
                    return entry;
                }

                if (Directory.Exists(entry))
                {
                    pending.Push(entry);
                }
                else if (entry.EndsWith(".lock", StringComparison.Ordinal))
                {
                    TryDelete(entry);
                }
            }
        }

        return null;
    }

    /// <summary>
    /// One directory's entries, or none when it cannot be read. A slot the daemon cannot enumerate is not a slot
    /// it can clean either, and the reset below reports that far more usefully than an exception from the sweep.
    /// </summary>
    private static string[] ChildrenOf(string directory)
    {
        try
        {
            return Directory.GetFileSystemEntries(directory);
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// Clears the markers a crashed merge/cherry-pick/revert/rebase leaves behind. Returns the redirected entry
    /// that stopped it, or <c>null</c> when it finished.
    /// </summary>
    private static string? AbortInProgress(string gitDir)
    {
        foreach (var marker in new[] { "MERGE_HEAD", "CHERRY_PICK_HEAD", "REVERT_HEAD" })
        {
            var path = Path.Combine(gitDir, marker);
            if (HostPathGuard.IsRedirected(path))
            {
                return path;
            }

            TryDelete(path);
        }

        foreach (var dir in new[] { "rebase-merge", "rebase-apply" })
        {
            var path = Path.Combine(gitDir, dir);
            if (HostPathGuard.IsRedirected(path))
            {
                return path;
            }

            if (Directory.Exists(path))
            {
                try
                {
                    // Unlike a recursive ENUMERATION, a recursive delete unlinks a nested symlink/junction rather
                    // than descending through it, so this stays inside the store without a walk of its own.
                    Directory.Delete(path, recursive: true);
                }
                catch
                {
                    // Best-effort; a leftover rebase dir will not, on its own, block reset --hard.
                }
            }
        }

        return null;
    }

    /// <summary>Deletes one file. Callers check <see cref="HostPathGuard.IsRedirected"/> first.</summary>
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
