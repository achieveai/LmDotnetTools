using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Workspace.Git;
using CodeReviewDaemon.Sample.Workspace.Sandbox;

namespace CodeReviewDaemon.Sample.Workspace;

/// <summary>
/// The resolved layout of a slot's store after <see cref="ReviewSlotPreparer.PrepareAsync"/>:
/// <see cref="TargetDir"/> is the reviewed submodule's working tree (advanced to the PR head),
/// <see cref="NotesDir"/> is where this PR's persistent review notes live under the store, and
/// <see cref="Branch"/> is the persistent notes branch checked out at <see cref="StoreRoot"/>.
/// </summary>
internal sealed record PreparedCheckout(string StoreRoot, string TargetDir, string NotesDir, string Branch);

/// <summary>
/// The narrow prepare seam <see cref="ReviewSlotPreparer"/> exposes to the executor, so the pooled-review
/// wiring can be verified against a fake preparer (mirroring <see cref="IReviewSlotPool"/>).
/// </summary>
internal interface IReviewSlotPreparer
{
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
/// The privileged phase (design task 6) that prepares a leased warm slot (task 5) for one PR review:
/// fetches the store, checks out — or reuses — the PR's <b>persistent</b> notes branch so prior notes
/// survive a re-lease, advances the reviewed submodule to the PR head, and wipes the ephemeral scratch
/// working tree. The submodule-init and head-checkout steps mirror
/// <see cref="Orchestration.DaemonReviewStageExecutor"/>'s existing <c>InitAllowListedSubmodulesAsync</c>/
/// <c>FetchAndCheckoutHeadAsync</c> logic exactly, rooted at the slot's store instead of the single-repo
/// checkout, so the same allow-listed, hardened git sequence governs both paths. No filesystem-perms step
/// here — the spike ruled RO-mount/chmod out; enforcement of what the review agent can write is elsewhere.
/// </summary>
internal sealed class ReviewSlotPreparer : IReviewSlotPreparer
{
    private readonly GitRunner _git;
    private readonly ISandboxFileSystem _fileSystem;
    private readonly string _provider;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<ReviewSlotPreparer> _logger;

    public ReviewSlotPreparer(
        GitRunner git,
        ISandboxFileSystem fileSystem,
        string provider,
        ILoggerFactory loggerFactory)
    {
        _git = git ?? throw new ArgumentNullException(nameof(git));
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        _provider = provider;
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _logger = loggerFactory.CreateLogger<ReviewSlotPreparer>();
    }

    /// <param name="slot">The leased warm slot (its store already cloned by the pool).</param>
    /// <param name="run">The PR run being prepared for — supplies <c>BaseSha</c>/<c>HeadSha</c>.</param>
    /// <param name="storeUrl">The store remote, used to resolve relative submodule URLs (as
    /// <c>EnsureCheckoutAsync</c> does).</param>
    /// <param name="submoduleRelPath">The reviewed submodule's path under the store, e.g.
    /// <c>repos/LmDotnetTools</c>.</param>
    /// <param name="branch">The PR's persistent review branch, e.g.
    /// <c>review/lmdotnettools-151</c>.</param>
    /// <param name="defaultBranch">The store's default branch, e.g. <c>main</c> — only used when
    /// <paramref name="branch"/> does not already exist on <c>origin</c>.</param>
    /// <param name="notesRelPath">The persistent notes path under the store, e.g.
    /// <c>PRs/lmdotnettools-151</c>.</param>
    /// <param name="policy">The per-run <see cref="OperationPolicy"/> scoping which submodules may be
    /// fetched.</param>
    /// <param name="cancellationToken">Propagated to every git step and the submodule initializer.</param>
    public async Task<PreparedCheckout> PrepareAsync(
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
        ArgumentNullException.ThrowIfNull(slot);
        ArgumentNullException.ThrowIfNull(run);
        ArgumentException.ThrowIfNullOrWhiteSpace(storeUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(submoduleRelPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(branch);
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultBranch);
        ArgumentException.ThrowIfNullOrWhiteSpace(notesRelPath);
        ArgumentNullException.ThrowIfNull(policy);

        var storeRoot = slot.StorePath;

        // 0. Clean-on-entry (the durability guarantee): bring the persistent warm store to a pristine state
        // BEFORE any git step, so a stale lock / dirty tree / half-checked-out submodule left by a crashed
        // prior lease can never wedge or contaminate this one. A structurally broken store is re-cloned by
        // the executor's recovery ladder.
        if (await SlotHygiene.EnsureCleanAsync(_git, storeRoot, cancellationToken).ConfigureAwait(false)
            == HygieneVerdict.NeedsReclone)
        {
            throw new SlotNeedsRecloneException(
                $"Run {run.Id}: slot {slot.Index} store is structurally unusable; re-clone required.");
        }

        // 1. Fetch origin — refreshes the store's remote-tracking refs so the branch-resolve below sees
        // the PR's persistent branch (or the latest default branch) if it moved since the last lease.
        await RunGitOrThrowAsync(
                ["-C", storeRoot, "fetch", "origin"], storeRoot, run, "fetching origin", cancellationToken)
            .ConfigureAwait(false);

        // 2. Branch resolve (origin-aware — fixes the note-wiping risk): reuse the persistent branch's
        // prior notes when it already exists on origin; otherwise branch fresh from the default branch.
        var verify = await _git
            .RunAsync(["-C", storeRoot, "rev-parse", "--verify", $"origin/{branch}"], storeRoot, cancellationToken)
            .ConfigureAwait(false);
        var checkoutSource = verify.Succeeded ? $"origin/{branch}" : defaultBranch;
        await RunGitOrThrowAsync(
                ["-C", storeRoot, "checkout", "-B", branch, checkoutSource],
                storeRoot,
                run,
                $"checking out branch '{branch}' from '{checkoutSource}'",
                cancellationToken)
            .ConfigureAwait(false);

        // 3. Init the reviewed submodule exactly like InitAllowListedSubmodulesAsync: selective,
        // allow-listed, recursive; a denied entry is logged and the walk continues.
        var initializer = new SubmoduleInitializer(
            _git, _fileSystem, policy, _provider, _loggerFactory.CreateLogger<SubmoduleInitializer>());
        var outcome = await initializer
            .InitializeAsync(storeRoot, GitRemoteUrl.Parse(storeUrl), cancellationToken)
            .ConfigureAwait(false);
        foreach (var denied in outcome.Denied)
        {
            _logger.LogWarning(
                "Run {RunId}: submodule '{Path}' ({Url}) was not initialized: {Reason}",
                run.Id, denied.Path, denied.Url, denied.Reason);
        }

        // Post-init verification (the executor's store-checkout path already does this): the REVIEWED
        // submodule must have actually initialized. Without this a denied/failed init silently proceeds to
        // the fetch below, which then fails opaquely. But NOT every failure is slot corruption: a TRANSIENT
        // cause (auth/network/throttle — captured in the denial reason's stderr) must retry the warm store, not
        // trigger a destructive reclone that can't fix it and would loop (review #180). Only corrupt/unrecognized
        // failures drive the reclone ladder.
        if (!outcome.InitializedPaths.Contains(submoduleRelPath, StringComparer.Ordinal))
        {
            var reason = outcome.Denied
                .FirstOrDefault(d => string.Equals(d.Path, submoduleRelPath, StringComparison.Ordinal))?.Reason;
            if (GitFailureClassifier.Classify(reason) == GitFailureKind.Transient)
            {
                throw new InvalidOperationException(
                    $"Run {run.Id}: reviewed submodule '{submoduleRelPath}' did not initialize (transient): {reason}");
            }

            throw new SlotCorruptException(
                $"Run {run.Id}: reviewed submodule '{submoduleRelPath}' did not initialize; slot needs re-clone. {reason}");
        }

        // 4. Advance the reviewed submodule to the PR head exactly like FetchAndCheckoutHeadAsync.
        var targetDir = PosixJoin(storeRoot, submoduleRelPath);
        await RunGitOrThrowAsync(
                ["-C", targetDir, "fetch", "origin", run.BaseSha, run.HeadSha],
                targetDir,
                run,
                "fetching the PR commits",
                cancellationToken)
            .ConfigureAwait(false);
        await RunGitOrThrowAsync(
                ["-C", targetDir, "checkout", "--force", run.HeadSha],
                targetDir,
                run,
                "checking out the PR head",
                cancellationToken)
            .ConfigureAwait(false);

        // 5. Wipe the ephemeral scratchpad (host IO, not git) so the review starts from a clean slate.
        WipeScratch(slot.ScratchPath);

        return new PreparedCheckout(
            StoreRoot: storeRoot,
            TargetDir: targetDir,
            NotesDir: PosixJoin(storeRoot, notesRelPath),
            Branch: branch);
    }

    /// <summary>Runs one git step and throws so the stage retries when it fails — mirrors the executor's
    /// existing helpers (<c>CloneIfMissingAsync</c>/<c>FetchAndCheckoutHeadAsync</c>).</summary>
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
            // A corrupt-slot failure (stale lock that survived cleaning, dirty tree, broken object) drives
            // the executor's re-clone escalation; a transient/unknown failure is a normal retry that keeps
            // the warm store.
            throw GitFailureClassifier.Classify(result.Stderr) == GitFailureKind.Corrupt
                ? new SlotCorruptException(message)
                : new InvalidOperationException(message);
        }
    }

    /// <summary>Wipes and recreates the scratch directory. Robust to read-only files left behind by an
    /// untrusted prior checkout — mirrors <c>ReviewSessionProvisioner.ClearReadOnly</c>.</summary>
    private static void WipeScratch(string scratchPath)
    {
        if (Directory.Exists(scratchPath))
        {
            ClearReadOnly(scratchPath);
            Directory.Delete(scratchPath, recursive: true);
        }

        Directory.CreateDirectory(scratchPath);
    }

    private static void ClearReadOnly(string path)
    {
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            var attributes = File.GetAttributes(file);
            if ((attributes & FileAttributes.ReadOnly) != 0)
            {
                File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
            }
        }
    }

    private static string PosixJoin(string root, string relative) => $"{root.TrimEnd('/')}/{relative.Trim('/')}";
}
