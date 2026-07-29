using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Workspace.Git;
using CodeReviewDaemon.Sample.Workspace.Sandbox;

namespace CodeReviewDaemon.Sample.Workspace;

internal sealed record PreparedCheckout(string StoreRoot, string TargetDir, string NotesDir, string Branch);

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
    internal const string SdkOwnershipMarkerFile = ".review-store-sdk-owned";

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

            var markerPath = PosixJoin(storeRoot, SdkOwnershipMarkerFile);
            if (await _fileSystem.ReadFileAsync(markerPath, cancellationToken).ConfigureAwait(false) is not null)
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
        var remove = await _git.CommandRunner
            .RunAsync(new SandboxCommand(["rm", "-rf", "--", storeRoot]), cancellationToken)
            .ConfigureAwait(false);
        if (!remove.Succeeded)
        {
            throw new InvalidOperationException(
                $"Removing corrupt review store '{storeRoot}' failed (exit {remove.ExitCode}): {remove.Stderr}");
        }

        await CloneStoreAsync(storeRoot, storeUrl, cancellationToken).ConfigureAwait(false);
    }

    private async Task CloneStoreAsync(
        string storeRoot,
        string storeUrl,
        CancellationToken cancellationToken)
    {
        var clone = await _git
            .RunAsync(["clone", storeUrl, storeRoot], "/workspace", cancellationToken)
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
        if (await SlotHygiene.EnsureCleanAsync(_git, storeRoot, cancellationToken, _logger).ConfigureAwait(false)
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
        await RunGitOrThrowAsync(
                ["-C", targetDir, "checkout", "--force", run.HeadSha],
                targetDir,
                run,
                "checking out the PR head",
                cancellationToken)
            .ConfigureAwait(false);

        await RunCommandOrThrowAsync(
                ["rm", "-rf", "--", scratchRoot], run, "clearing scratch", cancellationToken)
            .ConfigureAwait(false);
        await RunCommandOrThrowAsync(
                ["mkdir", "-p", "--", scratchRoot], run, "creating scratch", cancellationToken)
            .ConfigureAwait(false);

        return new PreparedCheckout(
            storeRoot,
            targetDir,
            PosixJoin(storeRoot, notesRelPath),
            branch);
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
