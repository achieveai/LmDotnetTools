using CodeReviewDaemon.Sample.Persistence.Models;

namespace CodeReviewDaemon.Sample.Workspace.Git;

/// <summary>
/// What preparation could establish about the PR's <c>base...head</c> range — the one thing the diff site
/// cannot work out for itself, because git reports every cause as the same <c>fatal: no merge base</c>.
/// <para>
/// The split that matters is permanent versus recoverable. Only
/// <see cref="UnrelatedHistories"/> is a property of the commit pair rather than of this daemon's
/// configuration or of the network, so only it may be stated to a PR author as a fact about their branch.
/// The rest must stay loud and keep retrying: reporting a ceiling we chose, or a fetch that happened to
/// fail, as "these commits can never be compared" would silently stop reviewing pull requests that a
/// one-line config change or a retry would have covered.
/// </para>
/// <para>
/// <see cref="Indeterminate"/> is that same rule applied one level down, to the probes the search is built
/// out of. A probe has three answers, not two — yes, no, and "it never answered" — and every one of them
/// here runs through a host runner whose watchdog kills a silent command and reports exit 124. Collapsing
/// that into the same arm as git's own "no" is how OUR timeout ends up in a pull request as a fact about
/// SOMEONE ELSE'S branch.
/// </para>
/// </summary>
internal enum MergeBaseOutcome
{
    /// <summary>base and head share a merge base locally; the three-dot diff can be taken.</summary>
    Resolved,

    /// <summary>
    /// No merge base, and no depth can produce one: either the checkout is not shallow at all, or deepening
    /// ran until it stopped extending either commit, meaning both walks reached real roots. A force-push, a
    /// rewritten history, or an imported repository. Permanent — retrying costs a fetch and changes nothing.
    /// </summary>
    UnrelatedHistories,

    /// <summary>Still no merge base when the depth climb hit its ceiling. Recoverable by widening a bound
    /// this daemon chose, so it stays an error rather than becoming a verdict.</summary>
    DepthCeilingReached,

    /// <summary>A deepening fetch failed outright (network, auth, a remote refusing the depth). Says nothing
    /// about whether the commits are related, so it is indeterminate and must be retried.</summary>
    DeepenFailed,

    /// <summary>
    /// A probe the search depends on never produced an answer — killed by the host runner's watchdog, its
    /// output capture abandoned, or failed on its own account. Distinct from <see cref="DeepenFailed"/> only
    /// in which step broke; identical in what it licenses, which is nothing. Nothing was learned about the
    /// commits, so the run must retry rather than report a verdict.
    /// </summary>
    Indeterminate,
}

/// <summary>
/// What <see cref="MergeBaseResolver.ResolveAsync"/> established. <see cref="CommitId"/> is populated only
/// alongside <see cref="MergeBaseOutcome.Resolved"/> — every other outcome is a statement about why there is
/// no merge base to name, so there is no commit for it to carry (issue #647: this is the SHA the context
/// artifact needs to record which commit the diff was actually taken against).
/// </summary>
internal sealed record MergeBaseResolution(MergeBaseOutcome Outcome, string? CommitId);

/// <summary>
/// Establishes whether the PR's base and head share a merge base in a prepared checkout, deepening a shallow
/// clone until they do — and, when they do not, says WHICH kind of "no" it is.
/// </summary>
/// <remarks>
/// Split out of <see cref="ReviewSlotPreparer"/> because the interesting part is not the preparation but the
/// classification: the caller's whole use of this type is to thread <see cref="MergeBaseOutcome"/> onto the
/// prepared checkout so the diff site can tell a fact about the pull request from a fact about this daemon's
/// own machine.
/// </remarks>
internal sealed class MergeBaseResolver
{
    /// <summary>
    /// The exit code <c>git merge-base</c> uses for "these commits share no ancestor", and the only non-zero
    /// exit from it this daemon may read as an answer.
    /// </summary>
    /// <remarks>
    /// Named rather than inlined because the whole distinction lives in this one number. 124 is a watchdog
    /// kill, 125 an abandoned capture, 128 an object the checkout does not hold, 137 a SIGKILL from outside
    /// — all of which would otherwise arrive at the same place as 1 does, and all of which mean the opposite
    /// thing.
    /// </remarks>
    private const int GitNoMergeBaseExitCode = 1;

    /// <summary>
    /// The first depth the climb asks for.
    /// </summary>
    /// <remarks>
    /// A fixed ladder is what run 147 died on: it ran both of its steps and base still sat 1054 commits in
    /// with no merge base. The climb instead stops on the real question — a fetch that extends neither commit
    /// means the histories are exhausted — and the ceiling is only a volume bound, so a monorepo whose single
    /// checkout is already a million objects cannot pull full history silently.
    /// </remarks>
    private const int MergeBaseFirstDepth = 100;

    /// <summary>How much deeper each round reaches than the last. Ten keeps the number of network round
    /// trips to four across the whole range rather than trading fetches for precision nothing needs.</summary>
    private const int MergeBaseDepthMultiplier = 10;

    /// <summary>Hard bound on how deep the climb will ask. Past any real default branch, so reaching it means
    /// something is wrong rather than merely deep.</summary>
    private const int MergeBaseDepthCeiling = 100_000;

    private readonly GitRunner _git;
    private readonly ILogger _logger;
    private readonly bool _enableObjectStoreMaintenance;

    public MergeBaseResolver(GitRunner git, ILogger logger, bool enableObjectStoreMaintenance = false)
    {
        _git = git ?? throw new ArgumentNullException(nameof(git));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _enableObjectStoreMaintenance = enableObjectStoreMaintenance;
    }

    /// <summary>
    /// Guarantees the PR's base and head have a merge base locally, deepening a shallow checkout until they do.
    /// </summary>
    /// <remarks>
    /// Every context path diffs the PR three-dot (<c>base...head</c>), which is defined in terms of the merge
    /// base — so without one, the diff does not come back empty, it comes back as <c>fatal: no merge base</c>
    /// and the run dies at ContextReady.
    /// <para>
    /// A store whose <c>.gitmodules</c> says <c>shallow = true</c> gets its submodule cloned at depth 1, and
    /// that lone commit becomes a GRAFT ROOT: git reports it as having no parents at all. The default branch
    /// is what gets cloned and the default branch is what a PR targets, so the truncated commit is routinely
    /// the PR's own base — parentless, with no ancestry to walk, and therefore no merge base with anything.
    /// Fetching the PR commits beforehand does not help: it gives HEAD its history, and leaves base the stub
    /// it already was.
    /// </para>
    /// <para>
    /// Deepening is cheap here in the way that matters. It walks the boundary back along commits whose trees
    /// the head fetch has very likely already brought down, so what crosses the wire is commit and tree
    /// objects rather than another copy of the repository.
    /// </para>
    /// <para>
    /// It re-fetches at an absolute <c>--depth</c> rather than the more targeted <c>--deepen</c>, which asks
    /// for N commits past the CURRENT boundary and would be the better fit. Azure DevOps answers that one with
    /// <c>fatal: Server does not support --deepen</c>: it advertises the <c>shallow</c> capability that the
    /// depth-1 clone rode in on, but not <c>deepen-relative</c>. So depth-from-the-tip it is, on the same
    /// capability that is already known to work against every remote a store's submodules can live on.
    /// </para>
    /// <para>
    /// That swap carries a trap, and it is why the fetch names commits instead of just saying <c>origin</c>.
    /// <c>--depth</c> does not only deepen — the flag is documented to "deepen or shorten", and it shortens
    /// exactly the refs the fetch names. Measured on a lab repo: a head with 160 commits of history, named in
    /// a <c>--depth=100</c> fetch, came back with 100. Here head routinely carries tens of thousands of
    /// commits while base is the truncated one, so a fetch that named both would slice away the very history
    /// the merge base is hiding in — and it would do it silently, leaving the same <c>no merge base</c>
    /// failure with less to work with than before. So each commit is named only while its reachable history
    /// is still shorter than the depth being asked for, which is deepening by construction.
    /// </para>
    /// <para>
    /// If it still does not resolve, this returns quietly and lets the diff fail with git's own message. The
    /// tempting fallback — diffing two-dot — is worse than failing: two-dot against a stale base reports the
    /// TARGET branch's own movement as though the PR had made it, so the reviewer is handed other people's
    /// changes to review as this author's. A review that does not happen is recoverable; a review of the wrong
    /// diff is posted to a PR.
    /// </para>
    /// </remarks>
    public async Task<MergeBaseResolution> ResolveAsync(
        string repoRoot,
        ReviewRun run,
        CancellationToken cancellationToken
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);
        ArgumentNullException.ThrowIfNull(run);

        var (mergeBase, commitId) = await MergeBaseAnswerAsync(repoRoot, run, cancellationToken).ConfigureAwait(false);
        if (mergeBase == GitAnswer.Yes)
        {
            return new MergeBaseResolution(MergeBaseOutcome.Resolved, commitId);
        }

        if (mergeBase == GitAnswer.Unknown)
        {
            // The probe that decides this whole method never answered, so there is nothing to build on. It
            // stops here rather than climbing anyway: every remaining step is a deepening fetch measured in
            // gigabytes on the live store, spent to answer a question we could not even ask, through the same
            // runner that just failed to ask it. A retry re-runs the PR-commit fetch first, which is the fix
            // for the one benign cause (an object this checkout does not hold, exit 128).
            return new MergeBaseResolution(MergeBaseOutcome.Indeterminate, null);
        }

        var shallow = await IsShallowAnswerAsync(repoRoot, run, cancellationToken).ConfigureAwait(false);
        if (shallow == GitAnswer.Unknown)
        {
            // Not "therefore not shallow". The obvious way to write this branch is `!shallow.Succeeded ||
            // stdout != "true"`, which reads a probe that never ran as a confirmed full clone and returns
            // UnrelatedHistories from it — a killed `rev-parse` presented to an author as proof their branch
            // descends from nothing.
            return new MergeBaseResolution(MergeBaseOutcome.Indeterminate, null);
        }

        if (shallow == GitAnswer.No)
        {
            // Full history already, and still unrelated: the two commits genuinely do not share an ancestor
            // (a force-pushed rebase, or a base from before a history rewrite). Deepening cannot invent one.
            _logger.LogWarning(
                "Run {RunId}: '{Repo}' has no merge base for {Base}...{Head} and is not shallow; the diff "
                    + "will fail. The base commit is likely orphaned by a force-push or history rewrite.",
                run.Id,
                repoRoot,
                run.BaseSha,
                run.HeadSha
            );
            return new MergeBaseResolution(MergeBaseOutcome.UnrelatedHistories, null);
        }

        // Climb the depth while each round is still buying history, rather than walking a fixed ladder.
        // Measured live on run 147: the old [100, 1000] ladder ran to completion and was not enough — a
        // read-only probe of that store afterwards found base truncated at the graft with 1054 commits
        // reachable, head whole at 34,579, and merge-base still empty. Raising the ladder to a bigger fixed
        // number only moves the wall to the next repository.
        //
        // The loop terminates on the real question instead. Each round records how far each commit reaches;
        // a completed fetch that extends NEITHER means both walks have hit actual roots, so the histories
        // are exhausted rather than merely shallow and no depth can ever produce a merge base. That is a
        // different diagnosis from running out of depth and gets a different message, because it calls for
        // the opposite operator action.
        var lastReach = new Dictionary<string, int>(StringComparer.Ordinal);
        var everFetched = false;

        for (var depth = MergeBaseFirstDepth; depth <= MergeBaseDepthCeiling; depth *= MergeBaseDepthMultiplier)
        {
            var targets = new List<string>();
            var grew = false;
            var unmeasured = false;
            foreach (var sha in new[] { run.BaseSha, run.HeadSha })
            {
                var reach = await ReachableCountAsync(repoRoot, sha, cancellationToken).ConfigureAwait(false);

                // Growth is only readable when BOTH ends of the comparison are real counts. A round that
                // could not take one of them did not observe "this bought nothing" — it observed nothing at
                // all — and the arm below is the one that speaks to a pull-request author. A missing count
                // arriving here as 0 is never greater than the previous reading and so reads as a flat
                // history every time.
                if (reach is null || !lastReach.TryGetValue(sha, out var before))
                {
                    unmeasured = true;
                }
                else if (reach > before)
                {
                    grew = true;
                }

                // Only real counts are remembered. A later round comparing against the last count that WAS
                // taken still supports the claim honestly — equal across two fetches means neither bought
                // anything — whereas remembering a failed probe as 0 would manufacture growth on the round
                // that recovers.
                if (reach is not null)
                {
                    lastReach[sha] = reach.Value;
                }

                // Name a commit only while its reachable history is still shorter than the depth being asked
                // for. `--depth` is documented to "deepen or shorten", and it shortens exactly the refs the
                // fetch names — so naming a commit that already reaches further would slice away the very
                // history the merge base is hiding in.
                //
                // An unmeasured commit is named, which is the one thing a 0 default would get right: the
                // realistic cause of a lost count is an object this checkout does not hold, and a fetch is
                // precisely the fix. Nothing an author sees rests on this decision.
                if (reach is null || reach < depth)
                {
                    targets.Add(sha);
                }
            }

            if (everFetched && !grew)
            {
                if (unmeasured)
                {
                    _logger.LogWarning(
                        "Run {RunId}: '{Repo}' could not be measured after deepening — `rev-list --count` "
                            + "did not answer for {Base} or {Head} — so whether that fetch bought any "
                            + "history is UNKNOWN. Giving up indeterminate rather than reading an "
                            + "unmeasured round as an exhausted history, which would report our own failed "
                            + "probe as a permanent fact about the pull request's commits.",
                        run.Id,
                        repoRoot,
                        run.BaseSha,
                        run.HeadSha
                    );
                    return new MergeBaseResolution(MergeBaseOutcome.Indeterminate, null);
                }

                _logger.LogWarning(
                    "Run {RunId}: '{Repo}' deepening no longer extends either commit, so both walks have "
                        + "reached real roots: {Base} and {Head} are on unrelated histories and no depth can "
                        + "produce a merge base. This is a force-push, a rewritten history, or an imported "
                        + "repository — widening the clone depth will not help.",
                    run.Id,
                    repoRoot,
                    run.BaseSha,
                    run.HeadSha
                );
                return new MergeBaseResolution(MergeBaseOutcome.UnrelatedHistories, null);
            }

            if (targets.Count == 0)
            {
                // This step is too small to ask for anything without shortening. That is not a reason to
                // stop — breaking here is what would silently skip every deeper step once a commit had
                // passed the last rung — so try a deeper one.
                continue;
            }

            var deepen = await _git.RunAsync(
                    ["-C", repoRoot, "fetch", $"--depth={depth}", "origin", .. targets],
                    repoRoot,
                    cancellationToken
                )
                .ConfigureAwait(false);
            if (!deepen.Succeeded)
            {
                _logger.LogWarning(
                    "Run {RunId}: re-fetching '{Repo}' at depth {Depth} failed (exit {Exit}): {Stderr}",
                    run.Id,
                    repoRoot,
                    depth,
                    deepen.ExitCode,
                    deepen.Stderr
                );
                return new MergeBaseResolution(MergeBaseOutcome.DeepenFailed, null);
            }

            everFetched = true;

            // Collapse the near-copy this round just landed, BEFORE the next round lands another. Doing it
            // here rather than once at the end is what bounds the peak: the climb can issue four fetches, and
            // on the live NOVA store that meant four packs of 7.2-7.7 GB coexisting.
            await CompactObjectStoreAsync(repoRoot, run, depth, cancellationToken).ConfigureAwait(false);

            var (answer, reAskCommitId) = await MergeBaseAnswerAsync(repoRoot, run, cancellationToken)
                .ConfigureAwait(false);
            if (answer == GitAnswer.Unknown)
            {
                // Same reasoning as the probe that opened the method, and it matters more here: the next
                // round's exhaustion test reads counts taken through the runner that just stopped answering.
                return new MergeBaseResolution(MergeBaseOutcome.Indeterminate, null);
            }

            if (answer == GitAnswer.Yes)
            {
                _logger.LogInformation(
                    "Run {RunId}: re-fetched shallow checkout '{Repo}' at depth {Depth} to reach the merge "
                        + "base of {Base}...{Head}.",
                    run.Id,
                    repoRoot,
                    depth,
                    run.BaseSha,
                    run.HeadSha
                );
                return new MergeBaseResolution(MergeBaseOutcome.Resolved, reAskCommitId);
            }
        }

        _logger.LogWarning(
            "Run {RunId}: '{Repo}' still has no merge base for {Base}...{Head} at the {Depth} depth ceiling; "
                + "the diff will fail. The PR's branch is older than the daemon will fetch — widen the "
                + "ceiling only if this repository genuinely needs it, since the bound exists so a monorepo "
                + "cannot pull full history silently.",
            run.Id,
            repoRoot,
            run.BaseSha,
            run.HeadSha,
            MergeBaseDepthCeiling
        );

        return new MergeBaseResolution(MergeBaseOutcome.DepthCeilingReached, null);
    }

    /// <summary>
    /// Collapses the near-duplicate pack the round that just finished wrote, before the next round writes
    /// another. Best-effort: a failure here leaves the store larger and the review proceeds.
    /// </summary>
    /// <remarks>
    /// A <c>--depth</c> fetch re-asks from the TIP rather than from the current boundary, so each round of the
    /// climb brings down the tip's tree closure again instead of only the boundary commits it still lacks —
    /// and that closure, not the extra depth, is the bulk. The fingerprint is unambiguous on the live NOVA
    /// store: four packs of 7.2-7.7 GB holding 4,967,095 objects between them but only 1,034,930 distinct
    /// ones, i.e. the same object set roughly four times over, 30 GB of the store's 31.
    /// <para>
    /// <c>--keep-unreachable</c> is not tidiness — it is required for correctness, and a plain
    /// <c>repack -a -d</c> here would break every review. The PR's base and head arrive by raw SHA, so nothing
    /// but <c>FETCH_HEAD</c> points at them, and repack's reachability walk does not treat FETCH_HEAD as a
    /// root. Measured on a lab repo built to this shape: <c>repack -a -d</c> left the store at 144 KB with the
    /// base commit DROPPED, discarding the deepening that had just been paid for; the same repack with
    /// <c>--keep-unreachable</c> kept base and head, preserved the merge base and the shallow boundary, left
    /// the reviewed worktree's HEAD resolvable, passed <c>fsck</c>, and still collapsed four packs into one
    /// for a 53% saving. Deepening the store again afterwards still worked.
    /// </para>
    /// <para>
    /// Gated off by default. See <c>CodeReviewDaemonOptions.EnableObjectStoreMaintenance</c> — what the flag
    /// governs is not whether this work is correct but whether it is ours to do.
    /// </para>
    /// </remarks>
    private async Task CompactObjectStoreAsync(
        string repoRoot,
        ReviewRun run,
        int depth,
        CancellationToken cancellationToken
    )
    {
        if (!_enableObjectStoreMaintenance)
        {
            // The store keeps the duplicate pack this round just wrote. That is the accepted cost of the
            // instruction not to touch local packs — see CodeReviewDaemonOptions.EnableObjectStoreMaintenance.
            return;
        }

        var repack = await _git.RunAsync(
                ["-C", repoRoot, "repack", "-a", "-d", "--keep-unreachable"],
                repoRoot,
                cancellationToken
            )
            .ConfigureAwait(false);
        if (!repack.Succeeded)
        {
            _logger.LogWarning(
                "Run {RunId}: repacking '{Repo}' after the depth {Depth} fetch failed (exit {Exit}): "
                    + "{Stderr}. The review proceeds; the near-duplicate pack this round wrote stays on disk.",
                run.Id,
                repoRoot,
                depth,
                repack.ExitCode,
                repack.Stderr
            );
            return;
        }

        _logger.LogInformation(
            "Run {RunId}: repacked '{Repo}' after deepening to {Depth}, collapsing the pack that fetch "
                + "duplicated.",
            run.Id,
            repoRoot,
            depth
        );
    }

    /// <summary>
    /// How many commits are reachable from <paramref name="sha"/> right now — its history as git can currently
    /// see it, which in a shallow checkout stops at the graft boundary rather than at the repository root.
    /// </summary>
    /// <remarks>
    /// Null when the count could not be taken, and null is not zero. Its two readers want opposite things
    /// from a missing count and a 0 would give the same answer to both. Naming the commit in the next fetch is
    /// the right answer — the realistic cause is an object this checkout does not hold, and a fetch is
    /// precisely the fix — but reading it as "this round bought no history" is how a killed <c>rev-list</c>
    /// turns into a pull-request comment telling the author to rebase.
    /// </remarks>
    private async Task<int?> ReachableCountAsync(string repoRoot, string sha, CancellationToken cancellationToken)
    {
        var result = await _git.RunAsync(["-C", repoRoot, "rev-list", "--count", sha], repoRoot, cancellationToken)
            .ConfigureAwait(false);
        return result.Succeeded && int.TryParse(result.Stdout.Trim(), out var count) ? count : null;
    }

    /// <summary>
    /// Whether the PR's base and head share a merge base in this checkout as it currently stands, and the
    /// commit id <c>merge-base</c> printed when they do.
    /// </summary>
    /// <remarks>
    /// <c>merge-base</c> exits 1 with no output when the commits are unrelated, and that exit is the answer
    /// — but it is the ONLY non-zero exit that is. See <see cref="GitNoMergeBaseExitCode"/>: everything else
    /// is a command that did not get to answer, including the 128 a missing commit produces, which is
    /// "cannot diff these YET" and is fixed by the fetch a retry re-runs.
    /// <para>
    /// A zero exit with empty stdout is Unknown too. Git does not do that, so something between us and git
    /// did — a truncated capture is exactly what exit 125 exists to flag — and guessing on its behalf is the
    /// habit being removed.
    /// </para>
    /// </remarks>
    private async Task<(GitAnswer Answer, string? CommitId)> MergeBaseAnswerAsync(
        string repoRoot,
        ReviewRun run,
        CancellationToken cancellationToken
    )
    {
        var result = await _git.RunAsync(
                ["-C", repoRoot, "merge-base", run.BaseSha, run.HeadSha],
                repoRoot,
                cancellationToken
            )
            .ConfigureAwait(false);
        if (result.Succeeded && !string.IsNullOrWhiteSpace(result.Stdout))
        {
            return (GitAnswer.Yes, result.Stdout.Trim());
        }

        if (result.ExitCode == GitNoMergeBaseExitCode)
        {
            return (GitAnswer.No, null);
        }

        _logger.LogWarning(
            "Run {RunId}: `git merge-base {Base} {Head}` in '{Repo}' neither found a merge base nor reported "
                + "the exit 1 that means there is none (exit {Exit}): {Stderr}. Whether the two commits "
                + "share an ancestor is UNKNOWN — a probe that was killed or failed is not a finding about "
                + "someone's branch, and this run will retry rather than say it was.",
            run.Id,
            run.BaseSha,
            run.HeadSha,
            repoRoot,
            result.ExitCode,
            result.Stderr
        );
        return (GitAnswer.Unknown, null);
    }

    /// <summary>
    /// Whether this checkout is shallow, which is what decides whether deepening can help at all.
    /// </summary>
    /// <remarks>
    /// <c>rev-parse --is-shallow-repository</c> prints exactly <c>true</c> or <c>false</c> on success, so
    /// anything else — a non-zero exit, or a word neither of those — is a probe that did not answer. The
    /// caller's "not shallow" branch is the one that concludes the histories are genuinely unrelated, and it
    /// must be reachable only by git actually saying <c>false</c>.
    /// </remarks>
    private async Task<GitAnswer> IsShallowAnswerAsync(
        string repoRoot,
        ReviewRun run,
        CancellationToken cancellationToken
    )
    {
        var result = await _git.RunAsync(
                ["-C", repoRoot, "rev-parse", "--is-shallow-repository"],
                repoRoot,
                cancellationToken
            )
            .ConfigureAwait(false);
        if (result.Succeeded)
        {
            var answer = result.Stdout.Trim();
            if (answer.Equals("true", StringComparison.OrdinalIgnoreCase))
            {
                return GitAnswer.Yes;
            }

            if (answer.Equals("false", StringComparison.OrdinalIgnoreCase))
            {
                return GitAnswer.No;
            }
        }

        _logger.LogWarning(
            "Run {RunId}: `git rev-parse --is-shallow-repository` in '{Repo}' answered neither true nor "
                + "false (exit {Exit}): {Stderr}. Whether the checkout is shallow is UNKNOWN, and an "
                + "unanswered probe must not be read as a confirmed full clone — that reading is what would "
                + "turn this into a permanent 'unrelated histories' verdict on the pull request.",
            run.Id,
            repoRoot,
            result.ExitCode,
            result.Stderr
        );
        return GitAnswer.Unknown;
    }
}
