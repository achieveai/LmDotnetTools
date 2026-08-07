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
/// <para>
/// Both ends delete any <c>*.lock</c> they find, but only ONE of them can call it stale by definition. The
/// pool leases a slot to at most one run at a time, and at <see cref="EnsureCleanAsync"/> the incoming run has
/// not yet adopted the slot, so nothing anywhere holds it and a lock left in it can only be debris from a run
/// that died. That is the durability guarantee, and it is the reason clean-on-entry is the gate that
/// condemns a store rather than the close. Exclusivity says nothing about <see cref="StripAsync"/>: it holds
/// between RUNS, and the run being closed is the one that can still have a git process in the store. What
/// makes that safe is the caller quiescing the slot first, which is the caller's to provide and is not
/// implied by the lease — see <see cref="StripAsync"/>.
/// </para>
/// Lock/abort steps are host filesystem operations (the pooled store lives on the daemon host); reset/clean
/// run through the host <see cref="GitRunner"/>. See the design doc §3–§4.
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

        // A store whose own cleanup cannot be walked is condemned rather than repaired: unlinking a redirected
        // entry would be a write chosen by whoever planted it, re-creating the directory afterwards hands the next
        // one a fresh target, and an entry that cannot be read is not something to make a decision about at all.
        //
        // The re-clone answers a REDIRECTED entry, and it is worth being exact about how, because it does walk the
        // tree: <see cref="HostDirectoryWipe"/> removes each link by NAME as it meets it, so the offending entry is
        // unlinked instead of followed and the fresh clone lands on clean ground.
        //
        // It does NOT answer an UNREADABLE one. That wipe refuses on an entry it cannot classify, for the same
        // reason this gate does, so the re-clone walks into the same wall and the store is never replaced —
        // routing Unreadable here names a repair that cannot happen. What bounds the mis-routing is the typed
        // refusal added alongside this gate: the wipe raises <see cref="SlotHostPathRefusedException"/>, the pooled
        // preparer spends the address rather than returning it to the free stack, and the condition ends in a
        // retired slot instead of a re-clone loop. Fail-closed, but by a mechanism this verdict does not name. A
        // verdict that routes Unreadable somewhere it can actually be handled is a behaviour change, filed
        // separately rather than made here.
        //
        // A second pass cannot change either case, so the gate goes ahead of the retry ladder.
        // See <see cref="HostPathGuard.Check"/>.
        if (pass.Blocked is { } blocked)
        {
            logger?.LogWarning(
                "Slot hygiene at {StorePath}: re-cloning — the stale-state sweep stopped at {BlockedPath} "
                    + "because {Reason}. Refusing to sweep past it, and refusing to remove it.",
                storePath, blocked.Path, blocked.Reason);
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

            // Tolerating the FAILURE is not the same as tolerating what it left behind, and `submodule foreach`
            // is worse than "stops at the first submodule whose command fails": at a registered submodule whose
            // gitdir is gone it ABORTS THE TRAVERSAL, dying while resolving that submodule and before the
            // per-submodule command is invoked at all. Every submodule ORDERED AFTER it was therefore never
            // cleaned — and nothing else in this pass reaches them: the superproject's `clean -ffdx` stops at a
            // tracked gitlink whether or not a repository is present at that path, and
            // `submodule update --checkout --force` only restores TRACKED content. One review's UNTRACKED
            // leftovers therefore survived the lease and crossed into the next review. Sweep the whole list
            // again over an enumeration nothing in the submodule tree can interrupt, and condemn the slot only
            // for what is still there afterwards.
            var residue = await SubmoduleResidueAsync(git, storePath, ct, logger).ConfigureAwait(false);
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
    /// Re-runs the submodule clean over a walk that no broken submodule can cut short, then reports what untracked
    /// content is still present in the submodules it was able to sweep, or <c>null</c> when there is none. The
    /// walk can still be stopped by a failure of its OWN enumeration, and that is reported rather than swallowed —
    /// the guarantee is that it never ends in silence, not that it never ends.
    /// <para>
    /// It does NOT use <c>submodule foreach</c>, and the <c>|| true</c> that used to stand here did not make it
    /// safe to. That guards the per-submodule COMMAND's exit status, and the failure being guarded against is
    /// not the command's: at a registered submodule whose gitdir is gone, git dies RESOLVING the submodule and
    /// never invokes the command, so the traversal ends there and every submodule after it is skipped in silence.
    /// The same is true of <c>submodule status</c> with or without <c>--recursive</c>. The walk here is built
    /// from <c>git ls-files -s</c> filtered to gitlink mode <c>160000</c> instead, which reads the INDEX and never
    /// opens a submodule repository, so a broken entry is listed alongside the healthy ones rather than ending the
    /// list. <c>ls-files</c> reports one repository's own gitlinks, so the walk recurses by re-reading it inside
    /// each submodule it validates — that is what replaces <c>--recursive</c>.
    /// </para>
    /// <para>
    /// Each path is gated on <c>rev-parse --show-toplevel</c> ANSWERING WITH THAT PATH before anything is run in
    /// it, because a path listed in the index is not evidence that a repository is there. A deinitialized
    /// submodule comes in two shapes and <c>--show-toplevel</c> is the one predicate that handles both. Where the
    /// <c>.git</c> FILE is retained but dangling — the shape that ABORTS <c>foreach</c> — every probe fatals
    /// <c>not a git repository</c>. Where the <c>.git</c> file is GONE, <c>-C &lt;path&gt;</c> does not fail at
    /// all: git walks UP and answers about the SUPERPROJECT, so a gate built on <c>--is-inside-work-tree</c> reads
    /// <c>true</c> and the clean that follows runs in superproject context from inside a submodule path (measured:
    /// with <c>-- :/</c> it offers to remove a file above the submodule). <c>--show-toplevel</c> separates them
    /// because it returns the submodule's own root only when the submodule really is one. Both sides of the
    /// comparison are normalized (git answers in forward slashes, the composed path carries the host's separators
    /// and possibly a trailing one) and compared case-insensitively, because an unnormalized mismatch fails toward
    /// SKIP — the same fail-quiet shape as the aborted walk this replaces.
    /// </para>
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
    /// whose clean answer is produced by its own failure is not a check. The old probe had a subtler version of
    /// that defect which no carve-out could have fixed: it was ONE <c>foreach</c> whose stdout aggregated every
    /// submodule, so when the walk aborted that stdout was PARTIAL, and a short or empty one said nothing about
    /// the submodules the walk never reached. Reading it as cleanliness was the same silence as the abort itself.
    /// Here each status is scoped to a single repository that has already answered as its own root, so an empty
    /// stdout means that repository is clean and nothing else — there is no partial aggregate left to misread,
    /// and the deinitialized submodule that used to fail this check is skipped (and logged) before it is asked.
    /// </para>
    /// <para>
    /// CLEANED and SURFACED are not the same set, and this paragraph replaces one that got both of its residuals
    /// wrong. It used to ACCEPT two gaps, each on a mechanism that was reasoned about rather than run.
    /// </para>
    /// <para>
    /// The first — submodules ORDERED AFTER a deinitialized one, described as merely going "unreported" — is
    /// CLOSED, and was never only a reporting gap. `foreach` aborted the traversal there, so those submodules were
    /// not cleaned either, and untracked leftovers crossing a lease is the contamination this function exists to
    /// prevent, not an acceptable price for skipping a clone. The index-driven walk reaches them, so they are now
    /// both cleaned and probed.
    /// </para>
    /// <para>
    /// The second — residue in a submodule whose path holds no repository — survives, but NOT for the reason
    /// given. The old text said the superproject's <c>clean -ffdx</c> takes it "because an uninitialized
    /// submodule's path holds no repository to stop it descending". Measured: it does not. The boundary is the
    /// tracked GITLINK in the INDEX, not what is on disk, so clean declines to descend into an uninitialized
    /// submodule's path exactly as it declines a live one — leaving the file there and exiting 0. The stated
    /// fallback cleans nothing.
    /// </para>
    /// <para>
    /// So a path that fails the gate is neither cleaned nor surfaced, and nothing else in the pass covers it. That
    /// sits against this file's own principle three paragraphs up — untracked residue is a previous lease's
    /// leftovers that a fresh clone is guaranteed to arrive without, which argues FOR condemning the slot. Not
    /// condemning is a deliberate scope choice and not a claim the gap is harmless: reporting it would re-clone on
    /// precisely the deinitialized state <see cref="GitFailureClassifier"/> subtracts from its corruption markers
    /// in order to tolerate, on every lease, which is a verdict change rather than a repair of this walk. What is
    /// NOT deferred is the silence — every skipped path is logged at warning with the gate's own output, so a
    /// store accumulating residue behind a broken gitlink is visible in the daemon log rather than inferred from
    /// this paragraph.
    /// </para>
    /// </summary>
    private static async Task<string?> SubmoduleResidueAsync(
        GitRunner git, string storePath, CancellationToken ct, ILogger? logger)
    {
        var pending = new Queue<string>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var root = NormalizePath(storePath);
        pending.Enqueue(root);
        visited.Add(root);
        string? residue = null;

        // The walk continues past anything it finds. A first residue is enough for the caller's verdict, but the
        // clean is worth finishing anyway: the caller may yet tolerate what is reported (only `??` lines condemn),
        // and stopping early would leave the rest of the tree in the state this sweep exists to remove.
        while (pending.Count > 0)
        {
            var repo = pending.Dequeue();
            var listing = await git.RunAsync(["-C", repo, "ls-files", "-s"], repo, ct).ConfigureAwait(false);
            if (!listing.Succeeded)
            {
                residue ??= $"the submodule listing at {repo} itself failed: {listing.Stderr}";
                continue;
            }

            foreach (var relativePath in GitlinkPaths(listing.Stdout))
            {
                if (relativePath is null)
                {
                    // `ls-files` quotes a path it cannot print literally, and a mis-parsed path would resolve to
                    // nothing, fail the gate, and be skipped — silence in the one place silence is the defect.
                    residue ??= $"the submodule listing at {repo} contains a path this sweep cannot parse";
                    continue;
                }

                var submodule = NormalizePath($"{repo}/{relativePath}");
                if (!visited.Add(submodule))
                {
                    continue;
                }

                var toplevel = await git.RunAsync(
                        ["-C", submodule, "rev-parse", "--show-toplevel"], submodule, ct)
                    .ConfigureAwait(false);
                if (!toplevel.Succeeded
                    || !NormalizePath(toplevel.Stdout ?? string.Empty).Equals(
                        submodule, StringComparison.OrdinalIgnoreCase))
                {
                    // Skipping is the deliberate choice (see the doc); being SILENT about it is not. This path is
                    // neither cleaned nor reported, so without this line the only trace of a store accumulating
                    // residue behind a broken gitlink would be a doc paragraph — and a doc is not an observable.
                    // Logged rather than returned so the verdict is unchanged: reporting it condemns the slot on
                    // exactly the deinitialized state the caller tolerates on purpose.
                    logger?.LogWarning(
                        "Slot hygiene at {StorePath}: NOT sweeping the indexed submodule path {SubmodulePath} — it "
                            + "does not answer as its own repository, so anything untracked under it survives this "
                            + "lease and crosses into the next review. A re-clone would clear it; this pass "
                            + "deliberately does not condemn the slot for it. (`rev-parse --show-toplevel` exit "
                            + "{ExitCode}, stdout {Stdout}, stderr {Stderr}.)",
                        storePath, submodule, toplevel.ExitCode, toplevel.Stdout, toplevel.Stderr);
                    continue;
                }

                await git.RunAsync(["-C", submodule, "clean", "-ffdx"], submodule, ct).ConfigureAwait(false);
                var status = await git.RunAsync(["-C", submodule, "status", "--porcelain"], submodule, ct)
                    .ConfigureAwait(false);
                if (!status.Succeeded)
                {
                    residue ??= $"the submodule status probe at {submodule} itself failed: {status.Stderr}";
                    continue;
                }

                residue ??= status.Stdout
                    ?.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .FirstOrDefault(line => line.StartsWith("??", StringComparison.Ordinal));
                pending.Enqueue(submodule);
            }
        }

        return residue;
    }

    /// <summary>
    /// The gitlink paths in one <c>git ls-files -s</c> listing — <c>&lt;mode&gt; &lt;sha&gt; &lt;stage&gt;\t&lt;path&gt;</c>
    /// rows filtered to mode <c>160000</c>. Yields <c>null</c> for a row whose path git QUOTED (it does that for a
    /// path holding a quote, a newline or a non-ASCII byte), so the caller reports it rather than unquoting it
    /// wrongly and silently skipping a submodule.
    /// </summary>
    private static IEnumerable<string?> GitlinkPaths(string? listing)
    {
        foreach (var line in (listing ?? string.Empty).Split(
                     '\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var tab = line.IndexOf('\t', StringComparison.Ordinal);
            if (tab < 0 || !line.StartsWith("160000 ", StringComparison.Ordinal))
            {
                continue;
            }

            var path = line[(tab + 1)..].Trim();
            yield return path.StartsWith('"') ? null : path;
        }
    }

    /// <summary>
    /// One path in the single spelling both sides of the <c>--show-toplevel</c> gate are compared in: forward
    /// slashes (git's answer is always in them; a composed host path is not), no trailing separator, no
    /// surrounding whitespace (git's stdout ends in a newline). Deliberately NOT <see cref="Path.GetFullPath(string)"/>,
    /// which would rewrite a sandbox-container path like <c>/workspace/store</c> into a host-rooted one and make
    /// every comparison in the container fail.
    /// </summary>
    private static string NormalizePath(string path) =>
        path.Trim().Replace('\\', '/').TrimEnd('/');

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
    /// <paramref name="Blocked"/> is the entry that stopped the stale-state sweep before it ran (see
    /// <see cref="HostPathGuard.Check"/>), or <c>null</c> when the sweep completed.</summary>
    private readonly record struct ResetPass(
        SandboxCommandResult Reset,
        SandboxCommandResult Clean,
        SandboxCommandResult Restore,
        SandboxCommandResult Foreach,
        HostPathRefusal? Blocked);

    /// <summary>
    /// Stands in for a git step that was never run because the sweep ahead of it refused. It reports FAILURE
    /// rather than success so that a reader which never looks at <see cref="ResetPass.Blocked"/> still condemns
    /// the slot: the alternative — an exit-0 placeholder — reports a store nobody swept as a store that came back
    /// clean, which is the one answer that must not be reachable here. The stderr names itself so the re-clone it
    /// produces is not logged as a git failure that never happened.
    /// </summary>
    private static readonly SandboxCommandResult NotRun = new(
        1, "", "not run: the stale-state sweep refused to cross an entry, so nothing in this store was touched");

    /// <summary>
    /// One force-reset pass: clear stale locks and abandoned operation markers, then reset and clean the
    /// superproject, restore every submodule checkout to the recorded gitlink, and clean the submodule working
    /// trees. Idempotent by construction, so the caller can run it twice.
    /// </summary>
    private static async Task<ResetPass> ForceResetAsync(
        GitRunner git, string storePath, CancellationToken ct, ISandboxFileSystem? fileSystem)
    {
        var refusal = await SweepStaleStateAsync(git, storePath, ct, fileSystem).ConfigureAwait(false);

        // A refusal is already the verdict: the caller condemns the slot to a re-clone on it, and no outcome the
        // git steps below could produce would change that. Running them anyway spent four git invocations on a
        // store whose contents are about to be deleted wholesale, and spent them on a store the daemon has just
        // said it cannot establish the shape of — every one of them writes. Stop at the refusal instead.
        if (refusal is { } blocked)
        {
            return new ResetPass(NotRun, NotRun, NotRun, NotRun, blocked);
        }

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

        return new ResetPass(reset, clean, restore, foreachResult, Blocked: null);
    }

    /// <summary>
    /// Clears stale locks and abandoned operation markers THROUGH the injected runner. In production that runner
    /// is SandboxSessionAdapter over typed SandboxClient, so pre-review hygiene never reaches around the mounted
    /// session with host filesystem APIs. Explicit argv keeps every path a distinct token. On the HOST-backed pool
    /// the store is a plain host directory and the runner is a local process runner with no POSIX <c>find</c> (a
    /// Windows daemon host has none) — and this step ignores its result, so there the sweep would fail silently and
    /// leave the wedged store for the reset to trip over. Use the host helpers for that case only.
    /// <para>
    /// Returns the entry that stopped the host sweep, or <c>null</c> when it ran to completion. The
    /// container branch needs no such check: <c>find</c> does not descend through a symlinked directory unless
    /// asked to, and <c>-type f</c>/<c>-type d</c> match the link itself rather than what it points at.
    /// </para>
    /// </summary>
    private static async Task<HostPathRefusal?> SweepStaleStateAsync(
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
    /// Does nothing at all to a store whose stale-state sweep refuses to cross an entry: that store is headed for
    /// a re-clone on its next lease, and pristine is not a state anything here can leave it in.
    /// <para>
    /// This writes to a store its own run may still be holding, so the quiescence it needs is the CALLER'S to
    /// provide. The lease does not provide it — that is exclusivity between runs, and the run in question is
    /// the one closing. The in-process path provides it by destroying the sandbox session just before the
    /// call, which terminates the review's child processes and unmounts. The S2S path cannot: the container
    /// belongs to the review host and is kept alive so the posted comment's deep link stays usable, so the
    /// slot is still mounted into a live container at close. A caller that cannot make that promise must not
    /// call this at all, and the S2S one does not — the sweep below would delete a live <c>*.lock</c>, which
    /// admits a second writer rather than clearing debris. Skipping is safe in the same way a failure here is:
    /// the store stays dirty until the next lease's <see cref="EnsureCleanAsync"/>, which is the durability
    /// guarantee and is why this end is only best-effort tidiness.
    /// </para>
    /// </summary>
    public static async Task StripAsync(GitRunner git, string storePath, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(git);
        ArgumentException.ThrowIfNullOrWhiteSpace(storePath);

        // The strip is best-effort tidiness with no verdict to report, so a store the sweep refuses to cross ends
        // the strip here rather than condemning anything; the next lease's EnsureCleanAsync is the gate that
        // condemns it, and it will, on the same refusal. Everything below writes to the store, and leaving it
        // pristine is the only thing they are for — which is work with no value on a store already booked for a
        // re-clone.
        if (RemoveStaleLocks(Path.Combine(storePath, ".git")) is not null)
        {
            return;
        }

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
    /// the entry that stopped the walk, or <c>null</c> when it finished. See
    /// <see cref="HostPathGuard.Check"/> for why this refuses instead of clearing the link.
    /// </summary>
    private static HostPathRefusal? RemoveStaleLocks(string gitDir)
    {
        if (HostPathGuard.Check(gitDir) is { } rootRefusal)
        {
            return rootRefusal;
        }

        if (!Directory.Exists(gitDir))
        {
            return null; // a submodule's .git is a gitfile; its real dir is reached via the parent .git/modules.
        }

        var pending = new Stack<string>();
        pending.Push(gitDir);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            if (ChildrenOf(directory) is not { } children)
            {
                return new HostPathRefusal(directory, HostPathVerdict.Unreadable);
            }

            foreach (var entry in children)
            {
                if (HostPathGuard.Check(entry) is { } refusal)
                {
                    return refusal;
                }

                if (Directory.Exists(entry))
                {
                    pending.Push(entry);
                }
                // Case-insensitively, because this branch matches a name the filesystem HANDED BACK rather
                // than probing one it built. AbortInProgress two calls down has no such problem: it composes
                // ".git/MERGE_HEAD" and asks the filesystem, which resolves whatever casing is on disk. Here
                // an Ordinal compare walks straight past an "index.LOCK" that git is nonetheless honouring,
                // and the cost is one wasted lease — the reset fails on the lock, the cleanliness gate
                // condemns the store, and the re-clone wipes it, lock included. Self-healing, but a re-clone
                // is the most expensive thing this daemon does.
                //
                // On a case-sensitive host the two names are genuinely different files and INDEX.LOCK blocks
                // nothing, so this deletes something it need not have. That is a name ending in ".lock"
                // directly under .git — git's own namespace, which this sweep is already clearing — so the
                // overreach costs nothing, and it buys the case the host branch actually runs on.
                else if (entry.EndsWith(".lock", StringComparison.OrdinalIgnoreCase))
                {
                    TryDelete(entry);
                }
            }
        }

        return null;
    }

    /// <summary>
    /// One directory's entries, or <c>null</c> when it could not be read.
    /// <para>
    /// The distinction is the whole point of the return type. This used to answer an unreadable directory with
    /// an EMPTY array, which the walk above cannot tell apart from a directory that genuinely holds nothing —
    /// so the sweep ran to completion, returned "finished", and reported a store it had never looked inside as
    /// swept clean. <see cref="HostPathGuard.Check"/> already refuses the same condition one level down: an
    /// entry whose ATTRIBUTES will not read is <see cref="HostPathVerdict.Unreadable"/>, because the walk's job
    /// is to establish containment and "I could not look" is not an establishment. Exactly the same is true of
    /// its CONTENTS, so it returns the same refusal and the caller condemns the store.
    /// </para>
    /// <para>
    /// The reset below does not cover this, which is what the earlier note here claimed. The denial that
    /// produces it is on LISTING, and traversal survives one: git goes on opening
    /// <c>.git/modules/&lt;sub&gt;/…</c> by name and succeeding, so a superproject <c>reset --hard</c> — which
    /// never reads that directory — reports nothing, and a submodule step that did fail is classified
    /// non-fatal by design. A store carrying a stale lock the sweep could not reach was handed to the next
    /// review as Clean.
    /// </para>
    /// </summary>
    private static string[]? ChildrenOf(string directory)
    {
        try
        {
            return Directory.GetFileSystemEntries(directory);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Clears the markers a crashed merge/cherry-pick/revert/rebase leaves behind. Returns the entry that
    /// stopped it, or <c>null</c> when it finished.
    /// </summary>
    private static HostPathRefusal? AbortInProgress(string gitDir)
    {
        foreach (var marker in new[] { "MERGE_HEAD", "CHERRY_PICK_HEAD", "REVERT_HEAD" })
        {
            var path = Path.Combine(gitDir, marker);
            if (HostPathGuard.Check(path) is { } markerRefusal)
            {
                return markerRefusal;
            }

            TryDelete(path);
        }

        foreach (var dir in new[] { "rebase-merge", "rebase-apply" })
        {
            var path = Path.Combine(gitDir, dir);
            if (HostPathGuard.Check(path) is { } dirRefusal)
            {
                return dirRefusal;
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

    /// <summary>Deletes one file. Callers check <see cref="HostPathGuard.Check"/> first.</summary>
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
