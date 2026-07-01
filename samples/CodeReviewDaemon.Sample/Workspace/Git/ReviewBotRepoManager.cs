using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Workspace.Sandbox;

namespace CodeReviewDaemon.Sample.Workspace.Git;

/// <summary>A single review artifact file to retain in the ReviewBot repo, relative to its root.</summary>
/// <param name="RelativePath">Path under the ReviewBot checkout (e.g. <c>PRs/github/acme-widgets/42-abcd1234/review.md</c>).</param>
/// <param name="Content">UTF-8 file body.</param>
internal sealed record ReviewArtifactFile(string RelativePath, string Content);

/// <summary>Terminal state of a ReviewBot publish attempt.</summary>
internal enum ReviewBotPublishOutcome
{
    /// <summary>The single retention commit was pushed onto the default branch and the review branch removed.</summary>
    Pushed,

    /// <summary>The push failed after all rebase retries; nothing was deleted and the run must be reconciled.</summary>
    GitSyncFailed,
}

/// <summary>The result of <see cref="ReviewBotRepoManager.PublishAsync"/>.</summary>
/// <param name="Outcome">Whether the retention commit was pushed.</param>
/// <param name="ReviewBranch">The review branch that was created (kept on failure, deleted on success).</param>
/// <param name="PushedSha">The default-branch SHA after the push, or <c>null</c> when the push failed.</param>
/// <param name="ReviewBranchDeleted">Whether the review branch was deleted (only on success).</param>
internal sealed record ReviewBotPublishResult(
    ReviewBotPublishOutcome Outcome,
    string ReviewBranch,
    string? PushedSha,
    bool ReviewBranchDeleted);

/// <summary>Inputs for one ReviewBot retention publish.</summary>
/// <param name="TargetRepo">Identity of the reviewed repository (used to slug the review branch + artifact paths).</param>
/// <param name="PrNumber">The pull-request number under review.</param>
/// <param name="HeadSha">Full head SHA of the reviewed PR.</param>
/// <param name="DefaultBranch">ReviewBot default branch (e.g. <c>main</c>) that artifacts land on.</param>
/// <param name="Files">Primary artifacts (PRs/... + KnowledgeBase/...) to write into the single commit.</param>
internal sealed record ReviewBotPublishRequest(
    RepoIdentity TargetRepo,
    int PrNumber,
    string HeadSha,
    string DefaultBranch,
    IReadOnlyList<ReviewArtifactFile> Files);

/// <summary>
/// Implements the plan §2 durable one-commit retention sequence for the ReviewBot repo. Per primary
/// review, in order: (1) create the review branch from the default; (2) write the <c>PRs/...</c> +
/// <c>KnowledgeBase/...</c> artifacts; (3) commit them as a <b>single</b> commit; (4) fast-forward the
/// default branch onto that commit (so both artifact sets persist on the default, not only on the
/// soon-deleted review branch); (5) push the default with bounded rebase-retry; (6) the caller records
/// <c>reviewbot_push</c>; (7) delete the review branch (local + remote). If the push never succeeds,
/// <b>nothing is deleted</b>, the result is <see cref="ReviewBotPublishOutcome.GitSyncFailed"/>, and the
/// orchestrator's reconcile retries — there is no "KnowledgeBase persisted but artifacts lost" window
/// because step 3 lands both in one commit. Persistence of the push record and any
/// <c>SubmoduleDenied</c> rows is the orchestrator's job; this manager is pure git/fs orchestration.
/// </summary>
internal sealed class ReviewBotRepoManager
{
    /// <summary>Plan §2.5 cap on push retries when the default branch advanced under us.</summary>
    private const int MaxPushAttempts = 3;

    private readonly GitRunner _git;
    private readonly ISandboxFileSystem _fileSystem;
    private readonly string _provider;
    private readonly ILogger<ReviewBotRepoManager> _logger;

    public ReviewBotRepoManager(
        GitRunner git,
        ISandboxFileSystem fileSystem,
        string provider,
        ILogger<ReviewBotRepoManager> logger
    )
    {
        _git = git ?? throw new ArgumentNullException(nameof(git));
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        _provider = provider;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Publishes the review artifacts into <paramref name="repoRoot"/> (an existing ReviewBot checkout)
    /// following the §2 sequence and returns the outcome.
    /// </summary>
    public async Task<ReviewBotPublishResult> PublishAsync(
        string repoRoot,
        ReviewBotPublishRequest request,
        CancellationToken cancellationToken
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);
        ArgumentNullException.ThrowIfNull(request);

        var reviewBranch = BuildReviewBranchName(request);

        // 1. Create the review branch from the default.
        await RunGitAsync(
                ["checkout", "-B", reviewBranch, request.DefaultBranch],
                repoRoot,
                cancellationToken)
            .ConfigureAwait(false);

        // 2 + 3. Write the PRs/... + KnowledgeBase/... artifacts (single commit's content).
        foreach (var file in request.Files)
        {
            await _fileSystem
                .WriteFileAsync(JoinPath(repoRoot, file.RelativePath), file.Content, cancellationToken)
                .ConfigureAwait(false);
        }

        await RunGitAsync(["add", "-A"], repoRoot, cancellationToken).ConfigureAwait(false);
        await RunGitAsync(
                ["commit", "-m", BuildCommitMessage(request)],
                repoRoot,
                cancellationToken)
            .ConfigureAwait(false);

        // 4. Fast-forward the default branch onto the single retention commit.
        await RunGitAsync(["checkout", request.DefaultBranch], repoRoot, cancellationToken)
            .ConfigureAwait(false);
        await RunGitAsync(["merge", "--ff-only", reviewBranch], repoRoot, cancellationToken)
            .ConfigureAwait(false);

        // 5. Push the default with bounded rebase-retry.
        var pushed = await TryPushWithRebaseAsync(repoRoot, request.DefaultBranch, cancellationToken)
            .ConfigureAwait(false);
        if (!pushed)
        {
            _logger.LogWarning(
                "ReviewBot push of '{Branch}' failed after {Attempts} attempts; keeping review branch '{ReviewBranch}' for reconcile.",
                request.DefaultBranch,
                MaxPushAttempts,
                reviewBranch);
            // Failure handling: nothing is deleted, the run is GitSyncFailed, reconcile retries.
            return new ReviewBotPublishResult(
                ReviewBotPublishOutcome.GitSyncFailed,
                reviewBranch,
                PushedSha: null,
                ReviewBranchDeleted: false);
        }

        // 6. Resolve the pushed SHA (the caller records reviewbot_push).
        var revParse = await RunGitAsync(
                ["rev-parse", request.DefaultBranch],
                repoRoot,
                cancellationToken)
            .ConfigureAwait(false);
        var pushedSha = revParse.Stdout.Trim();

        // 7. Delete the review branch (local + remote) — only after a successful push.
        await RunGitAsync(["branch", "-D", reviewBranch], repoRoot, cancellationToken)
            .ConfigureAwait(false);
        await RunGitAsync(
                ["push", "origin", "--delete", reviewBranch],
                repoRoot,
                cancellationToken)
            .ConfigureAwait(false);

        return new ReviewBotPublishResult(
            ReviewBotPublishOutcome.Pushed,
            reviewBranch,
            pushedSha,
            ReviewBranchDeleted: true);
    }

    /// <summary>
    /// Pushes <paramref name="defaultBranch"/> to <c>origin</c>, rebasing onto the remote and retrying
    /// up to <see cref="MaxPushAttempts"/> times when the branch advanced underneath us (concurrent
    /// review or external push). Returns <c>true</c> on the first successful push.
    /// </summary>
    private async Task<bool> TryPushWithRebaseAsync(
        string repoRoot,
        string defaultBranch,
        CancellationToken cancellationToken
    )
    {
        for (var attempt = 1; attempt <= MaxPushAttempts; attempt++)
        {
            var push = await RunGitAsync(
                    ["push", "origin", defaultBranch],
                    repoRoot,
                    cancellationToken)
                .ConfigureAwait(false);
            if (push.Succeeded)
            {
                return true;
            }

            if (attempt == MaxPushAttempts)
            {
                break;
            }

            // The remote moved; rebase our single commit on top and retry.
            await RunGitAsync(
                    ["pull", "--rebase", "origin", defaultBranch],
                    repoRoot,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return false;
    }

    /// <summary>Runs a git command and throws when a step that must succeed (everything but push) fails.</summary>
    private async Task<SandboxCommandResult> RunGitAsync(
        IReadOnlyList<string> gitArgs,
        string repoRoot,
        CancellationToken cancellationToken
    )
    {
        var result = await _git.RunAsync(gitArgs, repoRoot, cancellationToken).ConfigureAwait(false);

        // push / pull are evaluated by the caller (rebase-retry); every other step is a hard prerequisite.
        var verb = gitArgs[0];
        if (!result.Succeeded && verb is not ("push" or "pull"))
        {
            throw new InvalidOperationException(
                $"git {verb} failed (exit {result.ExitCode}): {result.Stderr}");
        }

        return result;
    }

    /// <summary>
    /// Builds the review branch name <c>review/{provider}/{owner-repo}/{pr}</c> (plan §2.1). The
    /// <c>{owner-repo}</c> segment uses the normalized, slug-escaped target identity so it is stable
    /// across casing drift and safe as a git ref.
    /// </summary>
    private string BuildReviewBranchName(ReviewBotPublishRequest request) =>
        $"review/{_provider}/{SlugifyRepo(request.TargetRepo)}/{request.PrNumber}";

    private string BuildCommitMessage(ReviewBotPublishRequest request) =>
        $"Review {_provider} {request.TargetRepo.DisplayName}#{request.PrNumber} @ {ShortSha(request.HeadSha)}";

    /// <summary>Slugs the target identity into a single ref-safe path segment (lowercased, separators to '-').</summary>
    private static string SlugifyRepo(RepoIdentity repo)
    {
        var parts = new[] { repo.OrgOrOwner, repo.Project, repo.RepoName }
            .Where(static p => !string.IsNullOrEmpty(p))
            .Select(static p => Slug(p!));
        return string.Join('-', parts);
    }

    private static string Slug(string value)
    {
        var chars = value
            .ToLowerInvariant()
            .Select(static c => char.IsLetterOrDigit(c) || c is '.' or '_' ? c : '-')
            .ToArray();
        return new string(chars).Trim('-');
    }

    private static string ShortSha(string sha) => sha.Length <= 8 ? sha : sha[..8];

    private static string JoinPath(string root, string relative) =>
        $"{root.TrimEnd('/')}/{relative.TrimStart('/')}";
}
