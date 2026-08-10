using System.Collections.Concurrent;
using System.Globalization;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Workspace.Git;
using CodeReviewDaemon.Sample.Workspace.Sandbox;

namespace CodeReviewDaemon.Sample.Workspace;

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
/// A prepared review checkout. <c>MergeBase</c> carries what preparation established about
/// <c>base...head</c>, and defaults to <see cref="MergeBaseOutcome.Resolved"/> so a caller that never sets
/// it keeps today's behaviour — the diff runs and a failure throws. That default is deliberately the loud
/// one: an unset value can never cause a degraded verdict to be posted to a pull request.
/// </summary>
internal sealed record PreparedCheckout(
    string StoreRoot,
    string TargetDir,
    string NotesDir,
    string Branch,
    MergeBaseOutcome MergeBase = MergeBaseOutcome.Resolved);

internal interface IReviewSlotPreparer
{
    Task EnsureStoreAsync(string storeRoot, string storeUrl, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    Task RecloneStoreAsync(string storeRoot, string storeUrl, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    Task<PreparedCheckout> PrepareAsync(
        ReviewRun run,
        string storeRoot,
        string scratchRoot,
        string storeUrl,
        string submoduleRelPath,
        string branch,
        string defaultBranch,
        string notesRelPath,
        OperationPolicy policy,
        CancellationToken cancellationToken) =>
        PrepareAsync(
            new ReviewSlot(0, "/workspace", storeRoot, scratchRoot),
            run,
            storeUrl,
            submoduleRelPath,
            branch,
            defaultBranch,
            notesRelPath,
            policy,
            cancellationToken);

    // Compatibility seam for existing fakes and the S2S host-owned preparation path while the in-process
    // executor moves to the container-rooted overload above.
    Task<PreparedCheckout> PrepareAsync(
        ReviewSlot slot,
        ReviewRun run,
        string storeUrl,
        string submoduleRelPath,
        string branch,
        string defaultBranch,
        string notesRelPath,
        OperationPolicy policy,
        CancellationToken cancellationToken);
}

/// <summary>
/// Prepares a leased review store through the injected runner/filesystem. On the in-process pooled path these
/// capabilities are the run-bound <see cref="SandboxSessionAdapter"/> over typed <c>SandboxClient</c>.
/// </summary>
internal sealed class ReviewSlotPreparer : IReviewSlotPreparer
{
    internal const string SdkOwnershipMarkerFile = ".git/review-store-sdk-owned";

    /// <summary>
    /// One gate per shared store path, serializing the mutations that land in the shared clone. Static
    /// because the preparer is not: the S2S host path uses one long-lived instance, but the in-process path
    /// builds a fresh preparer per sandbox session, and two of those aimed at the same store must still take
    /// the same lock. Keyed by path for the same reason.
    /// </summary>
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> SharedStoreLocks =
        new(StringComparer.Ordinal);


    private readonly GitRunner _git;
    private readonly ISandboxFileSystem _fileSystem;
    private readonly string _provider;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<ReviewSlotPreparer> _logger;
    private readonly bool _requireSdkOwnershipMarker;

    /// <summary>
    /// <c>CodeReviewDaemon:EnableObjectStoreMaintenance</c>. Defaults to <c>false</c> here as well as in the
    /// options record, so a call site that does not thread the setting through cannot accidentally acquire
    /// the behaviour — the safe value is the one you get by saying nothing.
    /// </summary>
    private readonly bool _enableObjectStoreMaintenance;

    public ReviewSlotPreparer(
        GitRunner git,
        ISandboxFileSystem fileSystem,
        string provider,
        ILoggerFactory loggerFactory,
        bool requireSdkOwnershipMarker = false,
        bool enableObjectStoreMaintenance = false)
    {
        _git = git ?? throw new ArgumentNullException(nameof(git));
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        _provider = provider;
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _logger = loggerFactory.CreateLogger<ReviewSlotPreparer>();
        _requireSdkOwnershipMarker = requireSdkOwnershipMarker;
        _enableObjectStoreMaintenance = enableObjectStoreMaintenance;
    }

    public async Task EnsureStoreAsync(string storeRoot, string storeUrl, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storeRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(storeUrl);

        var probe = await _git
            .RunAsync(["-C", storeRoot, "rev-parse", "--git-dir"], storeRoot, cancellationToken)
            .ConfigureAwait(false);
        if (probe.Succeeded)
        {
            if (!_requireSdkOwnershipMarker)
            {
                return;
            }

            // A PRESENCE check on the marker, so an over-size one still counts as owned: the alternative is
            // re-cloning a store that was never unowned.
            var markerPath = PosixJoin(storeRoot, SdkOwnershipMarkerFile);
            var marker = await _fileSystem
                .ReadFileAsync(markerPath, SandboxReadLimits.RepositoryFileBytes, cancellationToken)
                .ConfigureAwait(false);
            if (marker.Exists)
            {
                return;
            }

            _logger.LogInformation(
                "Review store {StoreRoot} predates SDK ownership; re-cloning once through the run sandbox.",
                storeRoot);
            await RecloneStoreAsync(storeRoot, storeUrl, cancellationToken).ConfigureAwait(false);
            return;
        }

        // A store path that exists but is not a repository is structural corruption, not an absent first-use
        // clone. Do not run `git clone <url> <non-empty-dir>` and misclassify its deterministic failure.
        var entries = await _fileSystem.ListFilesAsync(storeRoot, cancellationToken).ConfigureAwait(false);
        if (entries.Count > 0)
        {
            throw new SlotNeedsRecloneException(
                $"Review store '{storeRoot}' exists but is not a valid git checkout.");
        }

        await CloneStoreAsync(storeRoot, storeUrl, cancellationToken).ConfigureAwait(false);
    }

    public async Task RecloneStoreAsync(string storeRoot, string storeUrl, CancellationToken cancellationToken)
    {
        // The host-backed pooled preparer owns a store on the DAEMON HOST, where the sandbox's POSIX `rm` does
        // not exist — and a git store is full of read-only pack/object files a naive recursive delete refuses.
        // Remove it with the same host filesystem pattern the scratch wipe uses; keep `rm -rf` for the sandbox.
        if (_fileSystem is HostFileSystem)
        {
            DeleteHostDirectory(storeRoot);
        }
        else
        {
            var remove = await _git.CommandRunner
                .RunAsync(new SandboxCommand(["rm", "-rf", "--", storeRoot]), cancellationToken)
                .ConfigureAwait(false);
            if (!remove.Succeeded)
            {
                throw new InvalidOperationException(
                    $"Removing corrupt review store '{storeRoot}' failed (exit {remove.ExitCode}): {remove.Stderr}");
            }
        }

        await CloneStoreAsync(storeRoot, storeUrl, cancellationToken).ConfigureAwait(false);
    }

    private async Task CloneStoreAsync(
        string storeRoot,
        string storeUrl,
        CancellationToken cancellationToken)
    {
        // `/workspace` is the container mount root: real inside the sandbox, absent on the daemon host — and
        // HostGitCommandRunner fails a command whose working directory does not exist, so pinning it there
        // would make the first-use host clone impossible. The clone names an absolute target either way.
        var workingDirectory = _fileSystem is HostFileSystem ? null : "/workspace";
        var clone = await _git
            .RunAsync(["clone", storeUrl, storeRoot], workingDirectory, cancellationToken)
            .ConfigureAwait(false);
        if (!clone.Succeeded)
        {
            throw new InvalidOperationException(
                $"Cloning the review store at '{storeRoot}' failed (exit {clone.ExitCode}): {clone.Stderr}");
        }

        if (_requireSdkOwnershipMarker)
        {
            await _fileSystem
                .WriteFileAsync(PosixJoin(storeRoot, SdkOwnershipMarkerFile), "1\n", cancellationToken)
                .ConfigureAwait(false);
        }
    }

    public Task<PreparedCheckout> PrepareAsync(
        ReviewSlot slot,
        ReviewRun run,
        string storeUrl,
        string submoduleRelPath,
        string branch,
        string defaultBranch,
        string notesRelPath,
        OperationPolicy policy,
        CancellationToken cancellationToken) =>
        slot.UsesSharedStore
            ? PrepareSharedAsync(
                slot, run, storeUrl, submoduleRelPath, branch, defaultBranch, notesRelPath, policy,
                cancellationToken)
            : PrepareAsync(
                run,
                slot.StorePath,
                slot.ScratchPath,
                storeUrl,
                submoduleRelPath,
                branch,
                defaultBranch,
                notesRelPath,
                policy,
                cancellationToken);

    /// <summary>
    /// The per-repository worktree path: one store clone under <see cref="ReviewSlot.SharedStorePath"/> holds
    /// every object, and the leased slot gets two <c>git worktree</c>s of it — the store on this PR's notes
    /// branch, and the reviewed submodule at the PR head. Concurrent reviews of one repository therefore share
    /// a single fetch and a single object database instead of each paying for a full independent clone.
    /// </summary>
    /// <remarks>
    /// Preparation of one repository's shared store is serialized. Slots are otherwise free to run in
    /// parallel, but <c>fetch</c>, <c>submodule update</c>, and <c>worktree add</c> all mutate state in the
    /// shared clone; letting two slots do that at once is how you get half-written refs and a lost race on
    /// the submodule checkout. The lock covers only the shared-store work — the per-slot worktree operations
    /// afterwards touch disjoint paths.
    /// </remarks>
    private async Task<PreparedCheckout> PrepareSharedAsync(
        ReviewSlot slot,
        ReviewRun run,
        string storeUrl,
        string submoduleRelPath,
        string branch,
        string defaultBranch,
        string notesRelPath,
        OperationPolicy policy,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentException.ThrowIfNullOrWhiteSpace(storeUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(submoduleRelPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(branch);
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultBranch);
        ArgumentException.ThrowIfNullOrWhiteSpace(notesRelPath);
        ArgumentNullException.ThrowIfNull(policy);

        var shared = slot.SharedStorePath;
        var gate = SharedStoreLocks.GetOrAdd(shared, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string submoduleRoot;
        // Declared outside the gate so the outcome survives the block that establishes it: the diff site
        // needs to know WHICH merge-base give-up happened, and that is knowable only here.
        MergeBaseOutcome mergeBase;
        try
        {
            await EnsureStoreAsync(shared, storeUrl, cancellationToken).ConfigureAwait(false);
            if (await SlotHygiene
                    .EnsureCleanAsync(_git, shared, cancellationToken, _logger, _fileSystem)
                    .ConfigureAwait(false)
                == HygieneVerdict.NeedsReclone)
            {
                throw new SlotNeedsRecloneException(
                    $"Run {run.Id}: shared review store '{shared}' is structurally unusable; re-clone required.");
            }

            await RunGitOrThrowAsync(
                    ["-C", shared, "fetch", "origin"], shared, run, "fetching origin", cancellationToken)
                .ConfigureAwait(false);

            // The shared clone stays parked on the default branch forever; per-PR branches live in the slot
            // worktrees. Keeping it off the notes branches is what lets an arbitrary number of them exist at
            // once — a branch checked out here would be unavailable to every slot.
            //
            // Parked on the FETCHED default, not the local ref of the same name. `git fetch origin` above
            // advances origin/<default> and deliberately leaves <default> alone, so a checkout by name pins
            // this clone to a ref nothing ever moves. Measured on the live NOVA store: local `main` sat at the
            // initial commit for a day while `origin/main` ran 54 commits ahead of it, and every notes
            // worktree cut from it inherited a Knowledge Base frozen at "empty" — which is why every review
            // brief this daemon had ever assembled reported prior-knowledge=0. Nothing commits to this clone
            // (the sweeper merges in its own checkout), so the reset is a mirror operation, not a discard.
            await RunGitOrThrowAsync(
                    ["-C", shared, "checkout", "--force", defaultBranch],
                    shared,
                    run,
                    $"parking the shared store on '{defaultBranch}'",
                    cancellationToken)
                .ConfigureAwait(false);
            await RunGitOrThrowAsync(
                    ["-C", shared, "reset", "--hard", $"origin/{defaultBranch}"],
                    shared,
                    run,
                    $"advancing '{defaultBranch}' to the fetched origin/{defaultBranch}",
                    cancellationToken)
                .ConfigureAwait(false);

            submoduleRoot = await EnsureSharedSubmodulesAsync(
                    shared, run, storeUrl, submoduleRelPath, policy, cancellationToken)
                .ConfigureAwait(false);

            await RunGitOrThrowAsync(
                    ["-C", submoduleRoot, "fetch", "origin", run.BaseSha, run.HeadSha],
                    submoduleRoot,
                    run,
                    "fetching the PR commits",
                    cancellationToken)
                .ConfigureAwait(false);
            await ConfigureObjectStoreGcAsync(submoduleRoot, cancellationToken).ConfigureAwait(false);
            mergeBase = await EnsureMergeBaseAsync(submoduleRoot, run, cancellationToken)
                .ConfigureAwait(false);
            await AutoGcAsync(submoduleRoot, run, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _ = gate.Release();
        }

        var checkoutSource = await ResolveNotesStartPointAsync(shared, branch, defaultBranch, cancellationToken)
            .ConfigureAwait(false);
        await EnsureWorktreeAsync(
                shared, slot.StorePath, run, ["-B", branch, checkoutSource],
                "the store notes worktree", cancellationToken)
            .ConfigureAwait(false);
        if (!string.Equals(checkoutSource, $"origin/{defaultBranch}", StringComparison.Ordinal))
        {
            // Reused branch: it was cut from the default at some earlier review and has not seen anything
            // merged there since. A freshly cut one is already current, so it is skipped.
            await BringNotesBranchUpToDateAsync(
                    slot.StorePath, branch, defaultBranch, run, cancellationToken)
                .ConfigureAwait(false);
        }

        await EnsureWorktreeAsync(
                submoduleRoot, slot.TargetPath, run, ["--detach", run.HeadSha],
                "the reviewed submodule worktree", cancellationToken)
            .ConfigureAwait(false);
        await EnsureReviewedTreeAsync(slot.TargetPath, run, cancellationToken).ConfigureAwait(false);

        await ClearScratchAsync(scratchRoot: slot.ScratchPath, run, cancellationToken).ConfigureAwait(false);

        return new PreparedCheckout(
            slot.StorePath,
            slot.TargetPath,
            PosixJoin(slot.StorePath, notesRelPath),
            branch,
            mergeBase);
    }

    /// <summary>
    /// Initializes the store's submodules in the shared clone once, and returns the reviewed one's root. The
    /// siblings are initialized too and deliberately left here rather than copied per slot: they are read-only
    /// context, so every concurrent review of this repo can read the same copy.
    /// </summary>
    private async Task<string> EnsureSharedSubmodulesAsync(
        string shared,
        ReviewRun run,
        string storeUrl,
        string submoduleRelPath,
        OperationPolicy policy,
        CancellationToken cancellationToken)
    {
        var initializer = new SubmoduleInitializer(
            _git, _fileSystem, policy, _provider, _loggerFactory.CreateLogger<SubmoduleInitializer>());
        var outcome = await initializer
            .InitializeAsync(shared, GitRemoteUrl.Parse(storeUrl), cancellationToken)
            .ConfigureAwait(false);
        foreach (var denied in outcome.Denied)
        {
            _logger.LogWarning(
                "Run {RunId}: submodule '{Path}' ({Url}) was not initialized: {Reason}",
                run.Id,
                denied.Path,
                denied.Url,
                denied.Reason);
        }

        if (!outcome.InitializedPaths.Contains(submoduleRelPath, StringComparer.Ordinal))
        {
            var reason = outcome.Denied
                .FirstOrDefault(d => string.Equals(d.Path, submoduleRelPath, StringComparison.Ordinal))?.Reason;
            if (GitFailureClassifier.Classify(reason) != GitFailureKind.Corrupt)
            {
                throw new InvalidOperationException(
                    $"Run {run.Id}: reviewed submodule '{submoduleRelPath}' did not initialize (transient/unknown): {reason}");
            }

            throw new SlotCorruptException(
                $"Run {run.Id}: reviewed submodule '{submoduleRelPath}' did not initialize; store needs re-clone. {reason}");
        }

        return PosixJoin(shared, submoduleRelPath);
    }

    /// <summary>
    /// Picks the start point for the PR's notes branch: the published branch when it exists, else the
    /// FETCHED store default. Read from the shared clone, which is the only place the remote refs live.
    /// <para>
    /// The fallback is <c>origin/&lt;default&gt;</c> rather than the bare local ref, and the distinction is
    /// the whole reason reviews saw no prior knowledge. <c>git fetch</c> advances <c>origin/main</c> and
    /// never <c>main</c>, so cutting from the local name gave every new notes branch the store as it looked
    /// when it was first cloned — on the live NOVA store, the initial commit, with an empty
    /// <c>KnowledgeBase/</c>, for every PR including ones first seen today.
    /// </para>
    /// </summary>
    private async Task<string> ResolveNotesStartPointAsync(
        string shared, string branch, string defaultBranch, CancellationToken cancellationToken)
    {
        var verify = await _git
            .RunAsync(["-C", shared, "rev-parse", "--verify", $"origin/{branch}"], shared, cancellationToken)
            .ConfigureAwait(false);
        return verify.Succeeded ? $"origin/{branch}" : $"origin/{defaultBranch}";
    }

    /// <summary>
    /// Brings a REUSED notes branch forward to the fetched default. Reuse is what preserves the PR's own
    /// accumulated notes across re-reviews, but on its own it also freezes the Knowledge Base at whatever
    /// existed the day the branch was cut: the branch is created once per PR and kept for its whole life, so
    /// a PR first seen before any extraction ran would show the reviewer an empty store forever.
    /// <para>
    /// Best-effort by design. A conflict here (the generated <c>_toc.md</c>/<c>_index.jsonl</c> are the
    /// plausible candidates, since extraction rewrites both wholesale on either side) must leave the review
    /// running on slightly stale knowledge rather than not running at all — but it MUST also unwind, because
    /// a half-merged index makes every later git step in this worktree fail and leaves the slot poisoned for
    /// the next lease as well.
    /// </para>
    /// </summary>
    private async Task BringNotesBranchUpToDateAsync(
        string worktreeRoot, string branch, string defaultBranch, ReviewRun run, CancellationToken cancellationToken)
    {
        var merge = await _git
            .RunAsync(
                ["-C", worktreeRoot, "merge", "--no-edit", $"origin/{defaultBranch}"],
                worktreeRoot,
                cancellationToken)
            .ConfigureAwait(false);
        if (merge.Succeeded)
        {
            return;
        }

        _logger.LogWarning(
            "Run {RunId}: could not bring notes branch '{Branch}' up to date with origin/{DefaultBranch} "
                + "(exit {Exit}): {Stderr}. Continuing on the branch as it stands — the review proceeds with "
                + "whatever prior knowledge that branch already carries, which may be less than the store holds.",
            run.Id, branch, defaultBranch, merge.ExitCode, merge.Stderr);
        _ = await _git
            .RunAsync(["-C", worktreeRoot, "merge", "--abort"], worktreeRoot, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The depth climb used to reach a base/head merge base in a shallow checkout. The first step covers an
    /// ordinary PR — a branch tens of commits behind its target — and each later round asks for ten times as
    /// much, so a long-lived branch is reached in a handful of round trips rather than a fixed two.
    /// <para>
    /// A fixed ladder was what run 147 died on: it ran both of its steps and base still sat 1054 commits in
    /// with no merge base. The climb instead stops on the real question — a fetch that extends neither commit
    /// means the histories are exhausted — and the ceiling is only a volume bound, so a monorepo whose single
    /// checkout is already a million objects cannot pull full history silently.
    /// </para>
    /// </summary>
    private const int MergeBaseFirstDepth = 100;

    /// <summary>How much deeper each round reaches than the last. Ten keeps the number of network round
    /// trips to four across the whole range rather than trading fetches for precision nothing needs.</summary>
    private const int MergeBaseDepthMultiplier = 10;

    /// <summary>Hard bound on how deep the climb will ask. Past any real default branch, so reaching it means
    /// something is wrong rather than merely deep.</summary>
    private const int MergeBaseDepthCeiling = 100_000;

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
    private async Task<MergeBaseOutcome> EnsureMergeBaseAsync(
        string repoRoot, ReviewRun run, CancellationToken cancellationToken)
    {
        var mergeBase = await MergeBaseAnswerAsync(repoRoot, run, cancellationToken).ConfigureAwait(false);
        if (mergeBase == GitAnswer.Yes)
        {
            return MergeBaseOutcome.Resolved;
        }

        if (mergeBase == GitAnswer.Unknown)
        {
            // The probe that decides this whole method never answered, so there is nothing to build on. It
            // stops here rather than climbing anyway: every remaining step is a deepening fetch measured in
            // gigabytes on the live store, spent to answer a question we could not even ask, through the same
            // runner that just failed to ask it. A retry re-runs the PR-commit fetch first, which is the fix
            // for the one benign cause (an object this checkout does not hold, exit 128).
            return MergeBaseOutcome.Indeterminate;
        }

        var shallow = await IsShallowAnswerAsync(repoRoot, run, cancellationToken).ConfigureAwait(false);
        if (shallow == GitAnswer.Unknown)
        {
            // Not "therefore not shallow". This branch used to be reached by `!shallow.Succeeded`, which read
            // a probe that never ran as a confirmed full clone and returned UnrelatedHistories from it — a
            // killed `rev-parse` presented to an author as proof their branch descends from nothing.
            return MergeBaseOutcome.Indeterminate;
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
                run.HeadSha);
            return MergeBaseOutcome.UnrelatedHistories;
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

        for (var depth = MergeBaseFirstDepth;
            depth <= MergeBaseDepthCeiling;
            depth *= MergeBaseDepthMultiplier)
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
                // used to arrive here as 0, which is never greater than the previous reading and so read as
                // a flat history every time.
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
                // An unmeasured commit is named, which is what the 0 default achieved and the one thing it
                // got right: the realistic cause of a lost count is an object this checkout does not hold,
                // and a fetch is precisely the fix. Nothing an author sees rests on this decision.
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
                        run.HeadSha);
                    return MergeBaseOutcome.Indeterminate;
                }

                _logger.LogWarning(
                    "Run {RunId}: '{Repo}' deepening no longer extends either commit, so both walks have "
                        + "reached real roots: {Base} and {Head} are on unrelated histories and no depth can "
                        + "produce a merge base. This is a force-push, a rewritten history, or an imported "
                        + "repository — widening the clone depth will not help.",
                    run.Id,
                    repoRoot,
                    run.BaseSha,
                    run.HeadSha);
                return MergeBaseOutcome.UnrelatedHistories;
            }

            if (targets.Count == 0)
            {
                // This step is too small to ask for anything without shortening. That is not a reason to
                // stop — it used to `break` here, which is what silently skipped every deeper step once a
                // commit had passed the last rung — so try a deeper one.
                continue;
            }

            var deepen = await _git
                .RunAsync(
                    ["-C", repoRoot, "fetch", $"--depth={depth}", "origin", .. targets],
                    repoRoot,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!deepen.Succeeded)
            {
                _logger.LogWarning(
                    "Run {RunId}: re-fetching '{Repo}' at depth {Depth} failed (exit {Exit}): {Stderr}",
                    run.Id,
                    repoRoot,
                    depth,
                    deepen.ExitCode,
                    deepen.Stderr);
                return MergeBaseOutcome.DeepenFailed;
            }

            everFetched = true;

            // Collapse the near-copy this round just landed, BEFORE the next round lands another. Doing it
            // here rather than once at the end is what bounds the peak: the climb can issue four fetches, and
            // on the live NOVA store that meant four packs of 7.2-7.7 GB coexisting.
            await CompactObjectStoreAsync(repoRoot, run, depth, cancellationToken).ConfigureAwait(false);

            var answer = await MergeBaseAnswerAsync(repoRoot, run, cancellationToken).ConfigureAwait(false);
            if (answer == GitAnswer.Unknown)
            {
                // Same reasoning as the probe that opened the method, and it matters more here: the next
                // round's exhaustion test reads counts taken through the runner that just stopped answering.
                return MergeBaseOutcome.Indeterminate;
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
                    run.HeadSha);
                return MergeBaseOutcome.Resolved;
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
            MergeBaseDepthCeiling);

        return MergeBaseOutcome.DepthCeilingReached;
    }

    /// <summary>
    /// The answer to a yes/no question put to git — plus the third state, for when git never answered it.
    /// </summary>
    /// <remarks>
    /// The third state is the entire reason this is not a <c>bool</c>. Every probe below runs through
    /// <see cref="HostGitCommandRunner"/>, which returns exit 124 for a command its watchdog killed and 125
    /// for one whose output it could not finish draining — and neither is hypothetical on this runner, whose
    /// idle timeout has already been observed killing healthy multi-gigabyte git operations. A bool has
    /// nowhere to put them, so they land in the same arm as git's own "no", and that arm ends in a sentence
    /// shown to a pull-request author telling them to re-target or rebase their branch.
    /// </remarks>
    private enum GitAnswer
    {
        /// <summary>git ran and said yes.</summary>
        Yes,

        /// <summary>git ran and said no. An ANSWER, not merely the absence of a yes.</summary>
        No,

        /// <summary>git did not answer: killed, timed out, or failed on its own account.</summary>
        Unknown,
    }

    /// <summary>
    /// The exit code <c>git merge-base</c> uses for "these commits share no ancestor", and the only non-zero
    /// exit from it this daemon may read as an answer.
    /// </summary>
    /// <remarks>
    /// Named rather than inlined because the whole distinction lives in this one number. 124 is a watchdog
    /// kill, 125 an abandoned capture, 128 an object the checkout does not hold, 137 a SIGKILL from outside
    /// — all of which used to arrive at the same place as 1 does, and all of which mean the opposite thing.
    /// </remarks>
    private const int GitNoMergeBaseExitCode = 1;

    /// <summary>
    /// How many commits are reachable from <paramref name="sha"/> right now — its history as git can currently
    /// see it, which in a shallow checkout stops at the graft boundary rather than at the repository root.
    /// </summary>
    /// <remarks>
    /// Null when the count could not be taken, and null is not zero. Its two readers want opposite things
    /// from a missing count and used to get the same 0 for both. Naming the commit in the next fetch is the
    /// right answer — the realistic cause is an object this checkout does not hold, and a fetch is precisely
    /// the fix — but reading it as "this round bought no history" is how a killed <c>rev-list</c> turns into
    /// a pull-request comment telling the author to rebase.
    /// </remarks>
    private async Task<int?> ReachableCountAsync(
        string repoRoot, string sha, CancellationToken cancellationToken)
    {
        var result = await _git
            .RunAsync(["-C", repoRoot, "rev-list", "--count", sha], repoRoot, cancellationToken)
            .ConfigureAwait(false);
        return result.Succeeded && int.TryParse(result.Stdout.Trim(), out var count) ? count : null;
    }

    /// <summary>
    /// Whether the PR's base and head share a merge base in this checkout as it currently stands.
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
    private async Task<GitAnswer> MergeBaseAnswerAsync(
        string repoRoot, ReviewRun run, CancellationToken cancellationToken)
    {
        var result = await _git
            .RunAsync(
                ["-C", repoRoot, "merge-base", run.BaseSha, run.HeadSha], repoRoot, cancellationToken)
            .ConfigureAwait(false);
        if (result.Succeeded && !string.IsNullOrWhiteSpace(result.Stdout))
        {
            return GitAnswer.Yes;
        }

        if (result.ExitCode == GitNoMergeBaseExitCode)
        {
            return GitAnswer.No;
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
            result.Stderr);
        return GitAnswer.Unknown;
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
        string repoRoot, ReviewRun run, CancellationToken cancellationToken)
    {
        var result = await _git
            .RunAsync(["-C", repoRoot, "rev-parse", "--is-shallow-repository"], repoRoot, cancellationToken)
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
            result.Stderr);
        return GitAnswer.Unknown;
    }

    /// <summary>
    /// How many packs a submodule object store may accumulate before git's own housekeeping collapses them.
    /// </summary>
    /// <remarks>
    /// Git's default is 50, and that default is exactly why nothing ever fired: the live NOVA submodule store
    /// was measured at 45 packs and 30 GB with <c>gc --auto</c> still correctly reporting no work to do.
    /// Eight bounds the routine drift — one small pack per review from the PR-commit fetch, ~33 MB on that
    /// store — without paying a full repack of a multi-gigabyte object store every second review.
    /// </remarks>
    private const int ObjectStorePackLimit = 8;

    /// <summary>
    /// Hands this object store to git's own housekeeping, overriding the three defaults that are wrong for a
    /// store the daemon re-fetches into for the rest of its life.
    /// </summary>
    /// <remarks>
    /// Written here rather than at clone time because the leaking stores are not the ones
    /// <see cref="CloneStoreAsync"/> creates. The submodule object stores under <c>.git/modules/&lt;path&gt;</c>
    /// are made by <c>submodule update --init</c> inside <see cref="SubmoduleInitializer"/>, and this is the
    /// first place afterwards that names one. The writes are idempotent, so repeating them per prepare costs
    /// three local config writes and no network.
    /// <para>
    /// <c>gc.autoPackLimit</c> is the leak's direct cause — see <see cref="ObjectStorePackLimit"/>.
    /// </para>
    /// <para>
    /// <c>gc.autoDetach=false</c> keeps the collapse INSIDE the per-store lock the caller is holding.
    /// Detaching is the default, and a background gc rewriting the pack directory is precisely the process
    /// that would still be running when the next lease starts fetching into it.
    /// </para>
    /// <para>
    /// <c>gc.cruftPacks=true</c> is a version guard, not a preference. Unreachable objects have to go
    /// somewhere, and before git 2.44 the default was to explode them into LOOSE files. Measured on a lab repo
    /// built to this shape: the same <c>gc --auto</c> that writes one cruft pack with the setting on writes 180
    /// loose objects with it off. On the live NOVA store 94% of the object database — 970,000 of 1,034,930
    /// objects — is unreachable deepening spoil, and the sandbox image runs git 2.39, so accepting that default
    /// would trade a pack leak for a far worse inode one. The setting has existed since 2.37, so both the
    /// daemon host's 2.53 and the sandbox's 2.39 honour it.
    /// </para>
    /// </remarks>
    private async Task ConfigureObjectStoreGcAsync(string repoRoot, CancellationToken cancellationToken)
    {
        if (!_enableObjectStoreMaintenance)
        {
            // Gated with the two commands below, and deliberately not treated as the harmless member of the
            // three. These keys are what git consults when it decides to rewrite packs on its own, so writing
            // them is a durable change to how the user's store behaves long after this process exits — the
            // instruction was to leave local packs alone, and a store left on git's defaults is that.
            return;
        }

        (string Key, string Value)[] settings =
        [
            ("gc.autoPackLimit", ObjectStorePackLimit.ToString(CultureInfo.InvariantCulture)),
            ("gc.autoDetach", "false"),
            ("gc.cruftPacks", "true"),
        ];

        foreach (var (key, value) in settings)
        {
            // Best-effort like every other housekeeping step here: a store that will not take its own gc
            // config still reviews correctly, it just keeps growing, and that is not worth failing a run over.
            _ = await _git
                .RunAsync(["-C", repoRoot, "config", key, value], repoRoot, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Collapses the object store into a single pack, immediately after a deepening fetch has landed a
    /// near-copy of it. Best-effort: housekeeping that fails must cost disk, never a review.
    /// </summary>
    /// <remarks>
    /// This is the fix for the leak. A <c>--depth</c> fetch re-asks from the TIP rather than from the current
    /// boundary, so each round of the climb brings down the tip's tree closure again instead of only the
    /// boundary commits it still lacks — and that closure, not the extra depth, is the bulk. The fingerprint is
    /// unambiguous on the live NOVA store: four packs of 7.2-7.7 GB holding 4,967,095 objects between them but
    /// only 1,034,930 distinct ones, i.e. the same object set roughly four times over, 30 GB of the store's 31.
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
    /// </remarks>
    private async Task CompactObjectStoreAsync(
        string repoRoot, ReviewRun run, int depth, CancellationToken cancellationToken)
    {
        if (!_enableObjectStoreMaintenance)
        {
            // The store keeps the duplicate pack this round just wrote. That is the accepted cost of the
            // instruction not to touch local packs — see CodeReviewDaemonOptions.EnableObjectStoreMaintenance.
            return;
        }

        var repack = await _git
            .RunAsync(
                ["-C", repoRoot, "repack", "-a", "-d", "--keep-unreachable"], repoRoot, cancellationToken)
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
                repack.Stderr);
            return;
        }

        _logger.LogInformation(
            "Run {RunId}: repacked '{Repo}' after deepening to {Depth}, collapsing the pack that fetch "
                + "duplicated.",
            run.Id,
            repoRoot,
            depth);
    }

    /// <summary>
    /// Lets git decide whether the routine drift is worth collapsing — the one small pack per review that the
    /// PR-commit fetch leaves behind, which <see cref="CompactObjectStoreAsync"/> never sees because it only
    /// runs on the rare deepening path. A no-op until <see cref="ObjectStorePackLimit"/> is crossed, and
    /// best-effort for the same reason the repack is.
    /// </summary>
    private async Task AutoGcAsync(string repoRoot, ReviewRun run, CancellationToken cancellationToken)
    {
        if (!_enableObjectStoreMaintenance)
        {
            // Left to git's own defaults, which is the status quo: gc.autoPackLimit defaults to 50, so git's
            // implicit post-fetch auto-gc has been declining to collapse anything all along.
            return;
        }

        var gc = await _git
            .RunAsync(["-C", repoRoot, "gc", "--auto"], repoRoot, cancellationToken)
            .ConfigureAwait(false);
        if (!gc.Succeeded)
        {
            _logger.LogWarning(
                "Run {RunId}: auto-gc of '{Repo}' failed (exit {Exit}): {Stderr}. The review proceeds; the "
                    + "store keeps whatever packs it had.",
                run.Id,
                repoRoot,
                gc.ExitCode,
                gc.Stderr);
        }
    }

    /// <summary>
    /// Brings <paramref name="worktreePath"/> into existence as a worktree of <paramref name="ownerRepo"/> at
    /// the requested position, reusing it in place when it is already one.
    /// </summary>
    /// <param name="ownerRepo">The repository whose object store the worktree hangs off.</param>
    /// <param name="worktreePath">Where the worktree should end up.</param>
    /// <param name="run">The run, for error attribution.</param>
    /// <param name="positionArgs">
    /// Where the worktree should be positioned, spelled the way BOTH <c>worktree add</c> and <c>checkout</c>
    /// read it — <c>-B &lt;branch&gt; &lt;start-point&gt;</c>, or <c>--detach &lt;commit&gt;</c>. One list, not
    /// a create/reuse pair: the two commands share this vocabulary exactly, and a pair invites the halves to
    /// drift. They did. The reuse half was once written as the bare <c>&lt;branch&gt; &lt;start-point&gt;</c>
    /// that reads correctly to a human, but to git <c>checkout &lt;tree-ish&gt; &lt;path&gt;...</c> is the
    /// restore-files-from-a-tree form, so it took both words as PATHSPECS and failed with "did not match any
    /// file(s) known to git" — naming the branch, which of course is not a file. Only the second review of a
    /// PR reached it, because the first created the worktree instead of repositioning one.
    /// </param>
    /// <param name="what">Human-readable name of the worktree, for error messages.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <remarks>
    /// <c>--relative-paths</c> is not optional. It makes git write the worktree's <c>.git</c> file and the
    /// owner's <c>worktrees/&lt;name&gt;/gitdir</c> as paths relative to each other, which is the only reason
    /// this survives at all: the mount is at some host path on the daemon box and at <c>/workspace</c> inside
    /// the sandbox, and absolute pointers written on one side are broken on the other. The extension it
    /// declares along the way is then withdrawn — see
    /// <see cref="DropRelativeWorktreesExtensionAsync"/> for why keeping the paths but not the declaration is
    /// what lets the sandbox's older git read the checkout at all.
    /// <para>
    /// A stale worktree still holding the notes branch is detached first. Git refuses to check a branch out in
    /// two worktrees at once, so a previous run of the same PR that left its worktree parked on that branch
    /// would otherwise block every later run of it — a permanent failure from a finished run's leftovers.
    /// </para>
    /// </remarks>
    private async Task EnsureWorktreeAsync(
        string ownerRepo,
        string worktreePath,
        ReviewRun run,
        IReadOnlyList<string> positionArgs,
        string what,
        CancellationToken cancellationToken)
    {
        // Clears registrations whose directory is gone, so a wiped slot can be re-added at the same path.
        _ = await _git.RunAsync(["-C", ownerRepo, "worktree", "prune"], ownerRepo, cancellationToken)
            .ConfigureAwait(false);

        var branchToClaim = positionArgs.Count > 1 && positionArgs[0] == "-B" ? positionArgs[1] : null;
        if (branchToClaim is not null)
        {
            await DetachOtherWorktreeHoldingAsync(ownerRepo, worktreePath, branchToClaim, cancellationToken)
                .ConfigureAwait(false);
        }

        var probe = await _git
            .RunAsync(["-C", worktreePath, "rev-parse", "--is-inside-work-tree"], worktreePath, cancellationToken)
            .ConfigureAwait(false);
        if (probe.Succeeded)
        {
            await RunGitOrThrowAsync(
                    ["-C", worktreePath, "checkout", "--force", .. positionArgs],
                    worktreePath,
                    run,
                    $"repositioning {what} at '{worktreePath}'",
                    cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            await RunGitOrThrowAsync(
                    ["-C", ownerRepo, "worktree", "add", "--relative-paths", "--force", worktreePath, .. positionArgs],
                    ownerRepo,
                    run,
                    $"creating {what} at '{worktreePath}'",
                    cancellationToken)
                .ConfigureAwait(false);
        }

        await DropRelativeWorktreesExtensionAsync(ownerRepo, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Removes the <c>extensions.relativeWorktrees</c> declaration that <c>worktree add --relative-paths</c>
    /// leaves behind, and returns the repository format to 0 once no extension is left to justify 1.
    /// </summary>
    /// <remarks>
    /// The relative pointers themselves are the point and are kept — this only withdraws the repo's
    /// <i>declaration</i> that it uses them, because that declaration is what the sandbox cannot read.
    /// Setting the extension bumps <c>core.repositoryformatversion</c> to 1, and a format-1 repo naming an
    /// extension git does not recognise is refused outright rather than degraded: the reviewed checkout came
    /// back <c>fatal: unknown repository extension found: relativeworktrees</c> for every git command the
    /// review agent ran. The daemon box is on git 2.53, which writes the extension; the sandbox image is on
    /// 2.39, which predates it (<c>--relative-paths</c> landed in 2.48). We control what gets written and not
    /// what reads it, so the write is what gives.
    /// <para>
    /// Nothing is lost by withdrawing it. Resolving a relative <c>gitdir:</c> against the directory holding
    /// the <c>.git</c> file is behaviour far older than the extension, so old and new git both follow the
    /// pointers either way. Measured on 2.53 with the extension unset: <c>worktree list</c> resolves every
    /// entry, the <c>worktree prune</c> this method's caller runs first deletes nothing, a later
    /// <c>--relative-paths</c> add still writes relative pointers on both sides, and moving the whole tree to
    /// a different absolute path — the host-to-<c>/workspace</c> transition — leaves the worktrees working.
    /// </para>
    /// <para>
    /// The format is only lowered when the extensions section is empty afterwards. Another extension present
    /// means format 1 is genuinely required, and a repo declaring <c>objectFormat = sha256</c> is one old git
    /// really cannot read — turning that into silent misinterpretation would be worse than the honest refusal.
    /// </para>
    /// </remarks>
    private async Task DropRelativeWorktreesExtensionAsync(string ownerRepo, CancellationToken cancellationToken)
    {
        // Unset fails when there is nothing to unset, which is the ordinary case on every call after the
        // first, so its exit code says nothing worth acting on.
        _ = await _git
            .RunAsync(
                ["-C", ownerRepo, "config", "--unset", "extensions.relativeWorktrees"],
                ownerRepo,
                cancellationToken)
            .ConfigureAwait(false);

        var remaining = await _git
            .RunAsync(["-C", ownerRepo, "config", "--get-regexp", "^extensions\\."], ownerRepo, cancellationToken)
            .ConfigureAwait(false);
        if (remaining.Succeeded && !string.IsNullOrWhiteSpace(remaining.Stdout))
        {
            return;
        }

        _ = await _git
            .RunAsync(
                ["-C", ownerRepo, "config", "core.repositoryformatversion", "0"],
                ownerRepo,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task DetachOtherWorktreeHoldingAsync(
        string ownerRepo, string worktreePath, string branch, CancellationToken cancellationToken)
    {
        var list = await _git
            .RunAsync(["-C", ownerRepo, "worktree", "list", "--porcelain"], ownerRepo, cancellationToken)
            .ConfigureAwait(false);
        if (!list.Succeeded)
        {
            return;
        }

        string? current = null;
        foreach (var line in list.Stdout.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("worktree ", StringComparison.Ordinal))
            {
                current = trimmed["worktree ".Length..].Trim();
            }
            else if (trimmed == $"branch refs/heads/{branch}"
                && current is not null
                && !PathsEqual(current, worktreePath))
            {
                _logger.LogInformation(
                    "Detaching stale worktree {Worktree}, which still holds notes branch '{Branch}'.",
                    current,
                    branch);
                _ = await _git
                    .RunAsync(["-C", current, "checkout", "--detach"], current, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(left.TrimEnd('/'), right.TrimEnd('/'), StringComparison.Ordinal);

    /// <summary>
    /// Returns the reviewed checkout to exactly the PR head with nothing else in it, and proves it did.
    /// </summary>
    /// <remarks>
    /// This directory is the one every review agent is told to read, and it is recycled: the same
    /// <c>slot-N/repo</c> serves PR after PR, positioned each time by <c>checkout --force</c>. That restores
    /// every TRACKED file and, by design, leaves untracked ones exactly where they are — so build output,
    /// generated sources and agent byproduct from the PREVIOUS review survive into the next one and read to an
    /// agent as part of the PR in front of it. Nothing else covers this: <see cref="SlotHygiene"/> does the
    /// <c>reset --hard</c> + <c>clean -ffdx</c>, but it is only ever handed a store path, and under the
    /// worktree layout the reviewed checkout is a SIBLING of the store rather than a directory inside it.
    /// <para>
    /// The two probes afterwards are the part that fails loudly. A tree parked on the wrong commit produces a
    /// review whose every finding is attributed to code the agent never saw, and there is nothing downstream
    /// — not the diff, which is commit-to-commit and stays correct regardless, not the notes, not the posted
    /// comment — that can tell that apart from a real review. Refusing to prepare is the only outcome that
    /// does not silently publish it: the caller surfaces the failure, the stage retries with no artifact
    /// persisted, and the retry governor bounds it.
    /// </para>
    /// <para>
    /// A probe that cannot RUN is treated differently from one that runs and disagrees. An unavailable probe
    /// leaves the question unanswered rather than answering it badly, and refusing every review on an
    /// unanswered question turns a transient git hiccup into a review outage — so it warns and proceeds.
    /// Submodule state is excluded from the cleanliness check for the reason
    /// <see cref="SlotHygiene.EnsureCleanAsync"/> excludes it on the store: a moved gitlink is the review's
    /// own to re-establish, not leftover content, and gating on it would fail every submodule-bearing repo.
    /// So is a path whose bytes on disk already equal the blob recorded at the head, which git can still
    /// report as modified — see <see cref="PartitionLeftoversAsync"/>.
    /// </para>
    /// </remarks>
    private async Task EnsureReviewedTreeAsync(
        string targetDir, ReviewRun run, CancellationToken cancellationToken)
    {
        var cleaned = await _git
            .RunAsync(["-C", targetDir, "clean", "-ffdx"], targetDir, cancellationToken)
            .ConfigureAwait(false);
        if (!cleaned.Succeeded)
        {
            _logger.LogWarning(
                "Run {RunId}: clearing untracked leftovers from the reviewed checkout '{TargetDir}' failed "
                    + "(exit {Exit}): {Stderr}. The cleanliness probe below is the gate.",
                run.Id, targetDir, cleaned.ExitCode, cleaned.Stderr);
        }

        var head = await _git
            .RunAsync(["-C", targetDir, "rev-parse", "HEAD"], targetDir, cancellationToken)
            .ConfigureAwait(false);
        var actualHead = head.Stdout?.Trim() ?? string.Empty;
        if (!head.Succeeded || actualHead.Length == 0)
        {
            _logger.LogWarning(
                "Run {RunId}: could not read the reviewed checkout's HEAD at '{TargetDir}' (exit {Exit}): "
                    + "{Stderr}. Proceeding unverified — the review is NOT known to be reading {HeadSha}.",
                run.Id, targetDir, head.ExitCode, head.Stderr, run.HeadSha);
            return;
        }

        if (!string.Equals(actualHead, run.HeadSha, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Run {run.Id}: the reviewed checkout '{targetDir}' is on {actualHead}, not the PR head "
                + $"{run.HeadSha}; refusing to review a tree that is not the pull request.");
        }

        var status = await _git
            .RunAsync(
                ["-C", targetDir, "status", "--porcelain", "-z", "--ignore-submodules=all"],
                targetDir,
                cancellationToken)
            .ConfigureAwait(false);
        if (!status.Succeeded)
        {
            _logger.LogWarning(
                "Run {RunId}: could not probe the reviewed checkout '{TargetDir}' for leftovers (exit {Exit}): "
                    + "{Stderr}. Proceeding on the verified head alone.",
                run.Id, targetDir, status.ExitCode, status.Stderr);
            return;
        }

        var (leftovers, normalized) = await PartitionLeftoversAsync(
                ParsePorcelainZ(status.Stdout ?? string.Empty), targetDir, cancellationToken)
            .ConfigureAwait(false);

        if (normalized.Count > 0)
        {
            _logger.LogInformation(
                "Run {RunId}: {Count} path(s) under '{TargetDir}' report as modified while holding bytes "
                    + "identical to the blob recorded at {HeadSha}. That is the repository's own eol/filter "
                    + "attributes disagreeing with what was committed, not leftover content, so the tree is "
                    + "still the pull request's: {Paths}",
                run.Id, normalized.Count, targetDir, run.HeadSha,
                Truncate(string.Join(", ", normalized)));
        }

        if (leftovers.Count > 0)
        {
            throw new InvalidOperationException(
                $"Run {run.Id}: the reviewed checkout '{targetDir}' is still dirty after cleaning, so it is "
                + $"not the pull request's tree: {Truncate(string.Join("\n", leftovers))}");
        }

        _logger.LogInformation(
            "Run {RunId}: reviewed checkout '{TargetDir}' verified clean at the PR head {HeadSha}.",
            run.Id, targetDir, run.HeadSha);
    }

    /// <summary>Bounds a git status listing so a wholly-unexpected tree cannot produce an unbounded message.</summary>
    private static string Truncate(string text) =>
        text.Length <= 512 ? text : string.Concat(text.AsSpan(0, 512), "… (truncated)");

    /// <summary>
    /// Above this many dirty paths the tree is wrong in bulk, not normalized oddly, so it is refused without
    /// spending a pair of git invocations per path to say so.
    /// </summary>
    private const int MaxClassifiedLeftovers = 25;

    /// <summary>
    /// Splits the surviving dirty paths into ones that are genuinely not the pull request's content and ones
    /// whose worktree bytes already ARE the recorded blob.
    /// </summary>
    /// <remarks>
    /// A repository can commit a blob whose line endings contradict its own <c>.gitattributes</c> — a
    /// <c>text eol=crlf</c> path whose stored blob already holds CRLF is the common shape. Git then runs the
    /// clean filter over the worktree copy on every comparison, converts CRLF to LF, and finds it unequal to
    /// the CRLF blob, so the path reports modified on a checkout nothing has touched. Measured here on
    /// WeveNova: one <c>ServiceConfig.ini</c>, all 91 of its 91 lines "changed", surviving
    /// <c>checkout --force</c>, <c>reset --hard</c> and <c>clean -ffdx</c> alike, because no operation that
    /// writes the worktree can produce bytes the clean filter maps back onto that blob. Gating on it refuses
    /// every review of that repository forever.
    /// <para>
    /// The discriminator is the blob identity of the RAW bytes: <c>hash-object --no-filters</c> bypasses the
    /// clean filter, so it answers "what does this file actually contain" rather than "what would git store
    /// for it". Equal to the index blob means the file on disk is byte-for-byte the content recorded at the
    /// PR head, whatever <c>status</c> says about it. A real edit changes those bytes and so changes that
    /// hash — the check cannot be talked into passing content the PR does not have, which is the whole point
    /// of the guard. Everything else (untracked, deleted, staged, renamed, unmerged) stays a leftover, and any
    /// probe that fails to run leaves the path a leftover too.
    /// </para>
    /// </remarks>
    private async Task<(IReadOnlyList<string> Leftovers, IReadOnlyList<string> Normalized)>
        PartitionLeftoversAsync(
            IReadOnlyList<(string Code, string Path)> entries,
            string targetDir,
            CancellationToken cancellationToken)
    {
        var leftovers = new List<string>();
        var normalized = new List<string>();
        var worthClassifying = entries.Count <= MaxClassifiedLeftovers;

        foreach (var (code, path) in entries)
        {
            if (worthClassifying
                && string.Equals(code, " M", StringComparison.Ordinal)
                && await HoldsRecordedBytesAsync(targetDir, path, cancellationToken).ConfigureAwait(false))
            {
                normalized.Add(path);
                continue;
            }

            leftovers.Add($"{code} {path}");
        }

        return (leftovers, normalized);
    }

    /// <summary>
    /// True when <paramref name="path"/>'s bytes on disk are exactly the blob the index records for it, so the
    /// only thing making it report modified is a filter git applies during comparison.
    /// </summary>
    private async Task<bool> HoldsRecordedBytesAsync(
        string targetDir, string path, CancellationToken cancellationToken)
    {
        var recorded = await _git
            .RunAsync(["-C", targetDir, "rev-parse", $":{path}"], targetDir, cancellationToken)
            .ConfigureAwait(false);
        if (!recorded.Succeeded)
        {
            return false;
        }

        var onDisk = await _git
            .RunAsync(
                ["-C", targetDir, "hash-object", "--no-filters", "--", path],
                targetDir,
                cancellationToken)
            .ConfigureAwait(false);
        if (!onDisk.Succeeded)
        {
            return false;
        }

        var recordedBlob = recorded.Stdout?.Trim() ?? string.Empty;
        return recordedBlob.Length > 0
            && string.Equals(recordedBlob, onDisk.Stdout?.Trim() ?? string.Empty, StringComparison.Ordinal);
    }

    /// <summary>
    /// Parses <c>status --porcelain -z</c> into (two-letter code, path) pairs.
    /// </summary>
    /// <remarks>
    /// The NUL-delimited form is used rather than the newline one because porcelain v1 QUOTES any path with a
    /// space, quote or non-ASCII byte in it, and a path that arrives quoted would never match the index entry
    /// the classifier below looks up. Rename and copy records carry a second path field, which is consumed
    /// with the record it belongs to so it is not mistaken for an entry of its own.
    /// </remarks>
    internal static IReadOnlyList<(string Code, string Path)> ParsePorcelainZ(string stdout)
    {
        var entries = new List<(string, string)>();
        var fields = stdout.Split('\0');
        for (var i = 0; i < fields.Length; i++)
        {
            // "XY path" — anything shorter is the empty tail after the final delimiter.
            if (fields[i].Length < 4)
            {
                continue;
            }

            var code = fields[i][..2];
            entries.Add((code, fields[i][3..]));
            if (code[0] is 'R' or 'C')
            {
                i++;
            }
        }

        return entries;
    }

    private async Task ClearScratchAsync(string scratchRoot, ReviewRun run, CancellationToken cancellationToken)
    {
        if (_fileSystem is HostFileSystem)
        {
            ClearHostScratch(scratchRoot);
            return;
        }

        await RunCommandOrThrowAsync(["rm", "-rf", "--", scratchRoot], run, "clearing scratch", cancellationToken)
            .ConfigureAwait(false);
        await RunCommandOrThrowAsync(["mkdir", "-p", "--", scratchRoot], run, "creating scratch", cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<PreparedCheckout> PrepareAsync(
        ReviewRun run,
        string storeRoot,
        string scratchRoot,
        string storeUrl,
        string submoduleRelPath,
        string branch,
        string defaultBranch,
        string notesRelPath,
        OperationPolicy policy,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentException.ThrowIfNullOrWhiteSpace(storeRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(scratchRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(storeUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(submoduleRelPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(branch);
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultBranch);
        ArgumentException.ThrowIfNullOrWhiteSpace(notesRelPath);
        ArgumentNullException.ThrowIfNull(policy);

        await EnsureStoreAsync(storeRoot, storeUrl, cancellationToken).ConfigureAwait(false);
        if (await SlotHygiene
                .EnsureCleanAsync(_git, storeRoot, cancellationToken, _logger, _fileSystem)
                .ConfigureAwait(false)
            == HygieneVerdict.NeedsReclone)
        {
            throw new SlotNeedsRecloneException(
                $"Run {run.Id}: review store '{storeRoot}' is structurally unusable; re-clone required.");
        }

        await RunGitOrThrowAsync(
                ["-C", storeRoot, "fetch", "origin"], storeRoot, run, "fetching origin", cancellationToken)
            .ConfigureAwait(false);

        var verify = await _git
            .RunAsync(["-C", storeRoot, "rev-parse", "--verify", $"origin/{branch}"], storeRoot, cancellationToken)
            .ConfigureAwait(false);
        // origin/<default>, never the bare local ref — see ResolveNotesStartPointAsync for why the two are
        // not interchangeable after a fetch.
        var checkoutSource = verify.Succeeded ? $"origin/{branch}" : $"origin/{defaultBranch}";
        await RunGitOrThrowAsync(
                ["-C", storeRoot, "checkout", "-B", branch, checkoutSource],
                storeRoot,
                run,
                $"checking out branch '{branch}' from '{checkoutSource}'",
                cancellationToken)
            .ConfigureAwait(false);
        if (verify.Succeeded)
        {
            await BringNotesBranchUpToDateAsync(storeRoot, branch, defaultBranch, run, cancellationToken)
                .ConfigureAwait(false);
        }

        var initializer = new SubmoduleInitializer(
            _git, _fileSystem, policy, _provider, _loggerFactory.CreateLogger<SubmoduleInitializer>());
        var outcome = await initializer
            .InitializeAsync(storeRoot, GitRemoteUrl.Parse(storeUrl), cancellationToken)
            .ConfigureAwait(false);
        foreach (var denied in outcome.Denied)
        {
            _logger.LogWarning(
                "Run {RunId}: submodule '{Path}' ({Url}) was not initialized: {Reason}",
                run.Id,
                denied.Path,
                denied.Url,
                denied.Reason);
        }

        if (!outcome.InitializedPaths.Contains(submoduleRelPath, StringComparer.Ordinal))
        {
            var reason = outcome.Denied
                .FirstOrDefault(d => string.Equals(d.Path, submoduleRelPath, StringComparison.Ordinal))?.Reason;
            if (GitFailureClassifier.Classify(reason) != GitFailureKind.Corrupt)
            {
                throw new InvalidOperationException(
                    $"Run {run.Id}: reviewed submodule '{submoduleRelPath}' did not initialize (transient/unknown): {reason}");
            }

            throw new SlotCorruptException(
                $"Run {run.Id}: reviewed submodule '{submoduleRelPath}' did not initialize; store needs re-clone. {reason}");
        }

        var targetDir = PosixJoin(storeRoot, submoduleRelPath);
        await RunGitOrThrowAsync(
                ["-C", targetDir, "fetch", "origin", run.BaseSha, run.HeadSha],
                targetDir,
                run,
                "fetching the PR commits",
                cancellationToken)
            .ConfigureAwait(false);
        await ConfigureObjectStoreGcAsync(targetDir, cancellationToken).ConfigureAwait(false);
        var mergeBase = await EnsureMergeBaseAsync(targetDir, run, cancellationToken).ConfigureAwait(false);
        await AutoGcAsync(targetDir, run, cancellationToken).ConfigureAwait(false);
        await RunGitOrThrowAsync(
                ["-C", targetDir, "checkout", "--force", run.HeadSha],
                targetDir,
                run,
                "checking out the PR head",
                cancellationToken)
            .ConfigureAwait(false);
        await EnsureReviewedTreeAsync(targetDir, run, cancellationToken).ConfigureAwait(false);

        await ClearScratchAsync(scratchRoot, run, cancellationToken).ConfigureAwait(false);

        return new PreparedCheckout(
            storeRoot,
            targetDir,
            PosixJoin(storeRoot, notesRelPath),
            branch,
            mergeBase);
    }

    private async Task RunGitOrThrowAsync(
        IReadOnlyList<string> gitArgs,
        string workingDirectory,
        ReviewRun run,
        string action,
        CancellationToken cancellationToken)
    {
        var result = await _git.RunAsync(gitArgs, workingDirectory, cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            var message = $"Run {run.Id}: {action} failed (exit {result.ExitCode}): {result.Stderr}";
            throw GitFailureClassifier.Classify(result.Stderr) == GitFailureKind.Corrupt
                ? new SlotCorruptException(message)
                : new InvalidOperationException(message);
        }
    }

    private static void ClearHostScratch(string scratchRoot)
    {
        DeleteHostDirectory(scratchRoot);
        _ = Directory.CreateDirectory(scratchRoot);
    }

    /// <summary>
    /// Recursively deletes a host directory, clearing the read-only attribute first: a git store is full of
    /// read-only pack/object files that <see cref="Directory.Delete(string, bool)"/> otherwise refuses.
    /// </summary>
    private static void DeleteHostDirectory(string root)
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var attributes = File.GetAttributes(file);
            if ((attributes & FileAttributes.ReadOnly) != 0)
            {
                File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
            }
        }

        Directory.Delete(root, recursive: true);
    }

    private async Task RunCommandOrThrowAsync(
        IReadOnlyList<string> argv,
        ReviewRun run,
        string action,
        CancellationToken cancellationToken)
    {
        var result = await _git.CommandRunner
            .RunAsync(new SandboxCommand(argv), cancellationToken)
            .ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                $"Run {run.Id}: {action} failed (exit {result.ExitCode}): {result.Stderr}");
        }
    }

    private static string PosixJoin(string root, string relative) =>
        $"{root.TrimEnd('/')}/{relative.Trim('/')}";
}
