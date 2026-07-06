using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Workspace.Sandbox;

namespace CodeReviewDaemon.Sample.Workspace.Git;

/// <summary>A single review artifact file to retain in the ReviewBot repo, relative to its root.</summary>
/// <param name="RelativePath">Path under the ReviewBot checkout (e.g. <c>PRs/github/acme-widgets/42-abcd1234/review.md</c>).</param>
/// <param name="Content">UTF-8 file body.</param>
internal sealed record ReviewArtifactFile(string RelativePath, string Content);

/// <summary>Terminal state of a <see cref="ReviewBranchManager.CommitNotesAsync"/> attempt.</summary>
internal enum ReviewBotPublishOutcome
{
    /// <summary>The notes commit was pushed onto the review branch, which is kept for later re-reviews.</summary>
    Pushed,

    /// <summary>The push failed after all rebase retries; the commit stays local and the caller must reconcile.</summary>
    GitSyncFailed,
}

/// <summary>The result of <see cref="ReviewBranchManager.CommitNotesAsync"/>.</summary>
/// <param name="Outcome">Whether the notes commit was pushed to the review branch.</param>
/// <param name="ReviewBranch">
/// The review branch the notes were committed to. <see cref="ReviewBranchManager.CommitNotesAsync"/>
/// never deletes it — the branch persists so a later re-review can commit onto it again; only
/// <see cref="ReviewBranchManager.MergeToDefaultAsync"/> (PR merged) or
/// <see cref="ReviewBranchManager.DeleteBranchAsync"/> (PR abandoned) remove it.
/// </param>
/// <param name="PushedSha">The review-branch SHA after the push, or <c>null</c> when the push failed.</param>
internal sealed record ReviewBotPublishResult(
    ReviewBotPublishOutcome Outcome,
    string ReviewBranch,
    string? PushedSha);

/// <summary>Inputs for one ReviewBot notes commit.</summary>
/// <param name="TargetRepo">Identity of the reviewed repository (used to slug the review branch + artifact paths).</param>
/// <param name="PrNumber">The pull-request number under review.</param>
/// <param name="HeadSha">Full head SHA of the reviewed PR.</param>
/// <param name="DefaultBranch">ReviewBot default branch (e.g. <c>main</c>) a brand-new review branch is created from.</param>
/// <param name="Files">Artifacts (PRs/... + KnowledgeBase/...) to write into the commit.</param>
internal sealed record ReviewBotPublishRequest(
    RepoIdentity TargetRepo,
    int PrNumber,
    string HeadSha,
    string DefaultBranch,
    IReadOnlyList<ReviewArtifactFile> Files);

/// <summary>
/// Deterministic git/fs orchestration for the ReviewBot repo's per-PR review branch
/// (<c>review/{provider}/{owner-repo}/{pr}</c>), split into three independently-callable ops so the
/// branch can persist across re-reviews and be merged-or-deleted only when the PR closes:
/// <list type="bullet">
/// <item><see cref="CommitNotesAsync"/> — create-or-reuse the branch, commit the review's artifacts,
/// push the branch, and KEEP it. Called once per review.</item>
/// <item><see cref="MergeToDefaultAsync"/> — fast-forward (or merge) the branch into the default branch,
/// push the default, then delete the branch. Called when the PR closes merged.</item>
/// <item><see cref="DeleteBranchAsync"/> — delete the branch without merging, idempotently. Called when
/// the PR closes abandoned/declined.</item>
/// </list>
/// Persistence of any outbox/push records is the orchestrator's job; this manager is pure git/fs
/// orchestration.
/// </summary>
internal sealed class ReviewBranchManager
{
    /// <summary>Cap on push retries when the target branch advanced under us.</summary>
    private const int MaxPushAttempts = 3;

    private readonly GitRunner _git;
    private readonly ISandboxFileSystem _fileSystem;
    private readonly string _provider;
    private readonly ILogger<ReviewBranchManager> _logger;

    public ReviewBranchManager(
        GitRunner git,
        ISandboxFileSystem fileSystem,
        string provider,
        ILogger<ReviewBranchManager> logger
    )
    {
        _git = git ?? throw new ArgumentNullException(nameof(git));
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        _provider = provider;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Commits <paramref name="request"/>'s artifacts onto its review branch inside
    /// <paramref name="repoRoot"/> (an existing ReviewBot checkout) and pushes it, creating the branch
    /// from <see cref="ReviewBotPublishRequest.DefaultBranch"/> only the first time it is seen and
    /// reusing it (never recreating from the default) on every later call — that is what lets notes
    /// accumulate across re-reviews instead of being wiped. The branch is always kept: it is the
    /// caller's job to eventually call <see cref="MergeToDefaultAsync"/> or
    /// <see cref="DeleteBranchAsync"/> once the PR reaches a terminal state.
    /// </summary>
    public async Task<ReviewBotPublishResult> CommitNotesAsync(
        string repoRoot,
        ReviewBotPublishRequest request,
        CancellationToken cancellationToken
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);
        ArgumentNullException.ThrowIfNull(request);

        var reviewBranch = BuildReviewBranchName(request);

        // 1. Create-or-reuse the branch. Recreating from the default every time would wipe notes
        // accumulated by prior reviews, so only branch from the default when it doesn't exist yet.
        var probe = await RunGitAsync(
                ["rev-parse", "--verify", reviewBranch],
                repoRoot,
                cancellationToken,
                allowFailure: true)
            .ConfigureAwait(false);
        if (probe.Succeeded)
        {
            await RunGitAsync(["checkout", reviewBranch], repoRoot, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await RunGitAsync(
                    ["checkout", "-B", reviewBranch, request.DefaultBranch],
                    repoRoot,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        // 2. Write the PRs/... + KnowledgeBase/... artifacts (this commit's content).
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

        // 3. Push the review branch (never the default) with bounded rebase-retry, and KEEP the branch —
        // no fast-forward of the default and no delete happen here.
        var pushed = await TryPushWithRebaseAsync(repoRoot, reviewBranch, cancellationToken)
            .ConfigureAwait(false);
        if (!pushed)
        {
            _logger.LogWarning(
                "ReviewBot push of review branch '{ReviewBranch}' failed after {Attempts} attempts; the commit stays local for reconcile.",
                reviewBranch,
                MaxPushAttempts);
            return new ReviewBotPublishResult(ReviewBotPublishOutcome.GitSyncFailed, reviewBranch, PushedSha: null);
        }

        var revParse = await RunGitAsync(["rev-parse", reviewBranch], repoRoot, cancellationToken)
            .ConfigureAwait(false);
        var pushedSha = revParse.Stdout.Trim();

        return new ReviewBotPublishResult(ReviewBotPublishOutcome.Pushed, reviewBranch, pushedSha);
    }

    /// <summary>
    /// Merges <paramref name="branch"/> into <paramref name="defaultBranch"/> (fast-forward when
    /// possible, else a merge commit), pushes the default branch, and — only once that push succeeds —
    /// deletes <paramref name="branch"/> (local + remote). Called when the PR closes merged. Returns
    /// <c>false</c> without deleting the branch when the push never succeeds, so the caller can retry.
    /// </summary>
    public async Task<bool> MergeToDefaultAsync(
        string repoRoot,
        string branch,
        string defaultBranch,
        CancellationToken cancellationToken
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(branch);
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultBranch);

        await RunGitAsync(["checkout", defaultBranch], repoRoot, cancellationToken).ConfigureAwait(false);

        var ffOnly = await RunGitAsync(
                ["merge", "--ff-only", branch],
                repoRoot,
                cancellationToken,
                allowFailure: true)
            .ConfigureAwait(false);
        if (!ffOnly.Succeeded)
        {
            await RunGitAsync(["merge", "--no-edit", branch], repoRoot, cancellationToken).ConfigureAwait(false);
        }

        var pushed = await TryPushWithRebaseAsync(repoRoot, defaultBranch, cancellationToken)
            .ConfigureAwait(false);
        if (!pushed)
        {
            _logger.LogWarning(
                "ReviewBot merge-to-default push of '{DefaultBranch}' failed after {Attempts} attempts; keeping review branch '{Branch}' for reconcile.",
                defaultBranch,
                MaxPushAttempts,
                branch);
            return false;
        }

        await DeleteBranchAsync(repoRoot, branch, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Deletes <paramref name="branch"/> (local + remote) without merging it, e.g. when the PR is
    /// abandoned/declined. Idempotent: a missing local or remote branch is a no-op, never an exception.
    /// </summary>
    public async Task DeleteBranchAsync(
        string repoRoot,
        string branch,
        CancellationToken cancellationToken
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(branch);

        await RunGitAsync(["branch", "-D", branch], repoRoot, cancellationToken, allowFailure: true)
            .ConfigureAwait(false);
        await RunGitAsync(
                ["push", "origin", "--delete", branch],
                repoRoot,
                cancellationToken,
                allowFailure: true)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Pushes <paramref name="branch"/> to <c>origin</c>, rebasing onto the remote and retrying up to
    /// <see cref="MaxPushAttempts"/> times when it advanced underneath us (concurrent review or external
    /// push). Returns <c>true</c> on the first successful push.
    /// </summary>
    private async Task<bool> TryPushWithRebaseAsync(
        string repoRoot,
        string branch,
        CancellationToken cancellationToken
    )
    {
        for (var attempt = 1; attempt <= MaxPushAttempts; attempt++)
        {
            var push = await RunGitAsync(["push", "origin", branch], repoRoot, cancellationToken)
                .ConfigureAwait(false);
            if (push.Succeeded)
            {
                return true;
            }

            if (attempt == MaxPushAttempts)
            {
                break;
            }

            // The remote moved; rebase our commit(s) on top and retry.
            await RunGitAsync(["pull", "--rebase", "origin", branch], repoRoot, cancellationToken)
                .ConfigureAwait(false);
        }

        return false;
    }

    /// <summary>Runs a git command, throwing when a step that must succeed fails.</summary>
    /// <param name="gitArgs">The git subcommand and its arguments (without the hardening/identity prefix).</param>
    /// <param name="repoRoot">Working directory the command runs in.</param>
    /// <param name="cancellationToken">Cancellation token forwarded to the underlying runner.</param>
    /// <param name="allowFailure">
    /// When <c>true</c>, a non-zero exit is returned as-is instead of throwing — for existence probes
    /// (<c>rev-parse --verify</c>) and idempotent deletes (<c>branch -D</c>, <c>push --delete</c>) where
    /// a "not found" result is an expected outcome, not a bug.
    /// </param>
    private async Task<SandboxCommandResult> RunGitAsync(
        IReadOnlyList<string> gitArgs,
        string repoRoot,
        CancellationToken cancellationToken,
        bool allowFailure = false
    )
    {
        var result = await _git.RunAsync(gitArgs, repoRoot, cancellationToken).ConfigureAwait(false);

        // push / pull are evaluated by the caller (rebase-retry); every other step that isn't explicitly
        // marked tolerant is a hard prerequisite.
        var verb = gitArgs[0];
        if (!result.Succeeded && !allowFailure && verb is not ("push" or "pull"))
        {
            throw new InvalidOperationException(
                $"git {verb} failed (exit {result.ExitCode}): {result.Stderr}");
        }

        return result;
    }

    /// <summary>
    /// Builds the review branch name <c>review/{provider}/{owner-repo}/{pr}</c>. The <c>{owner-repo}</c>
    /// segment uses the normalized, slug-escaped target identity so it is stable across casing drift and
    /// safe as a git ref. Exposed so callers (and tests) can resolve the branch name for a request.
    /// </summary>
    public string BuildReviewBranchName(ReviewBotPublishRequest request) =>
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
