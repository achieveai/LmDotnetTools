using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Workspace.Git;
using CodeReviewDaemon.Sample.Workspace.Sandbox;

namespace CodeReviewDaemon.Sample.Workspace;

/// <summary>
/// A prepared review checkout. <see cref="MergeBase"/> carries what preparation established about
/// <c>base...head</c>, and defaults to <see cref="MergeBaseOutcome.Resolved"/> so a caller that never sets
/// it keeps the pre-existing behaviour — the diff runs and a failure throws. That default is deliberately the
/// loud one: an unset value can never cause a degraded verdict to be posted to a pull request.
/// </summary>
internal sealed record PreparedCheckout(
    string StoreRoot,
    string TargetDir,
    string NotesDir,
    string Branch,
    MergeBaseOutcome MergeBase = MergeBaseOutcome.Resolved);

internal interface IReviewSlotPreparer
{
    /// <summary>
    /// A HOST <paramref name="storeRoot"/> must be established as contained BEFORE this call: the probe below
    /// is <c>git -C storeRoot</c>, which follows a junction standing there as readily as a real directory, and
    /// nothing in this method checks. Today that obligation is discharged by <c>GuardSlotPaths</c> in
    /// <see cref="ReviewSlotPool.LeaseAsync"/>, before the slot escapes its lease; the in-process caller passes
    /// a fixed container path instead. <see cref="RecloneStoreAsync"/> carries no such duty — its wipe guards
    /// the root itself.
    /// </summary>
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

    private readonly GitRunner _git;
    private readonly ISandboxFileSystem _fileSystem;
    private readonly string _provider;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<ReviewSlotPreparer> _logger;
    private readonly bool _requireSdkOwnershipMarker;

    public ReviewSlotPreparer(
        GitRunner git,
        ISandboxFileSystem fileSystem,
        string provider,
        ILoggerFactory loggerFactory,
        bool requireSdkOwnershipMarker = false)
    {
        _git = git ?? throw new ArgumentNullException(nameof(git));
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        _provider = provider;
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _logger = loggerFactory.CreateLogger<ReviewSlotPreparer>();
        _requireSdkOwnershipMarker = requireSdkOwnershipMarker;
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
            HostDirectoryWipe.Delete(storeRoot);
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
        PrepareAsync(
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
        var hygiene = await SlotHygiene
            .EnsureCleanAsync(_git, storeRoot, cancellationToken, _logger, _fileSystem)
            .ConfigureAwait(false);
        switch (hygiene)
        {
            case HygieneVerdict.NeedsReclone:
                throw new SlotNeedsRecloneException(
                    $"Run {run.Id}: review store '{storeRoot}' is structurally unusable; re-clone required.");

            // The store's own cleanup could not be walked past an UNREADABLE entry, and a re-clone is the one
            // repair that must not run: it begins by wiping the store, and the wipe refuses on that same entry
            // (see HostDirectoryWipe). Raise the refusal type DIRECTLY rather than laundering it through
            // SlotNeedsRecloneException — its consumers retire the address, which is the only correct outcome,
            // and the retirement then follows from this decision rather than from the wipe's refusal escaping a
            // catch filter downstream (issue #276).
            case HygieneVerdict.HostPathUnreadable:
                throw new SlotAddressUnusableException(
                    $"Run {run.Id}: review store '{storeRoot}' cannot have its cleanup walked (an entry under it "
                        + "is unreadable); a re-clone would refuse on the same entry, so the slot is retired.");

            // The cleanliness probe did not answer, so the store is neither known clean nor known dirty. Raise
            // a type of its own rather than reusing either neighbour: SlotNeedsRecloneException would spend
            // minutes re-cloning over a question that was never put, and SlotAddressUnusableException would
            // RETIRE a slot whose only fault is one lost answer. Nothing downstream catches this one — the
            // slot is released back to the pool untouched and the next lease probes it again.
            case HygieneVerdict.ProbeUnanswered:
                throw new SlotProbeUnansweredException(
                    $"Run {run.Id}: the cleanliness probe on review store '{storeRoot}' returned no answer, so "
                        + "the store is not known to be clean; releasing the slot to be re-probed on the next "
                        + "lease rather than reviewing an unverified tree or re-cloning a store that may be fine.");

            case HygieneVerdict.Clean:
            default:
                break;
        }

        await RunGitOrThrowAsync(
                ["-C", storeRoot, "fetch", "origin"], storeRoot, run, "fetching origin", cancellationToken)
            .ConfigureAwait(false);

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

        // Deliberately AFTER the PR-commit fetch and BEFORE the head checkout: the fetch is what gives head
        // its history, and the resolver's whole job is to find out whether base has any to meet it with. The
        // outcome is threaded onto the returned checkout rather than thrown on, because "no merge base" has
        // several causes and only one of them is a fact about the pull request — see
        // <see cref="MergeBaseOutcome"/>. Preparation is not the place that decides what to tell an author.
        var mergeBase = await new MergeBaseResolver(_git, _logger)
            .ResolveAsync(targetDir, run, cancellationToken)
            .ConfigureAwait(false);

        await RunGitOrThrowAsync(
                ["-C", targetDir, "checkout", "--force", run.HeadSha],
                targetDir,
                run,
                "checking out the PR head",
                cancellationToken)
            .ConfigureAwait(false);

        if (_fileSystem is HostFileSystem)
        {
            ClearHostScratch(scratchRoot);
        }
        else
        {
            await RunCommandOrThrowAsync(
                    ["rm", "-rf", "--", scratchRoot], run, "clearing scratch", cancellationToken)
                .ConfigureAwait(false);
            await RunCommandOrThrowAsync(
                    ["mkdir", "-p", "--", scratchRoot], run, "creating scratch", cancellationToken)
                .ConfigureAwait(false);
        }

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
        HostDirectoryWipe.Delete(scratchRoot);
        _ = Directory.CreateDirectory(scratchRoot);
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
