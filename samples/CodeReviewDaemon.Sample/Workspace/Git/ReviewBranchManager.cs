using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Workspace.Sandbox;

namespace CodeReviewDaemon.Sample.Workspace.Git;

/// <summary>A single review artifact file to retain in the ReviewBot repo, relative to its root.</summary>
/// <param name="RelativePath">Path under the ReviewBot checkout (e.g. <c>PRs/widgets-42/review.md</c>).</param>
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
/// (<c>review/{repo}-{pr}</c>), split into three independently-callable ops so the
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
    private readonly ILogger<ReviewBranchManager> _logger;

    public ReviewBranchManager(
        GitRunner git,
        ISandboxFileSystem fileSystem,
        ILogger<ReviewBranchManager> logger
    )
    {
        _git = git ?? throw new ArgumentNullException(nameof(git));
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
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
    /// <para>
    /// <paramref name="stagePaths"/> scopes what is staged: when supplied, only those repo-relative
    /// paths are <c>git add</c>-ed (the pooled-store commit gate — stage ONLY <c>PRs/&lt;pr&gt;/…</c>, never
    /// the moved code-submodule pointer). When null (the ReviewBot retention path) the whole worktree is
    /// staged via <c>git add -A</c> so a sibling <c>KnowledgeBase/…</c> write is picked up too.
    /// </para>
    /// </summary>
    public async Task<ReviewBotPublishResult> CommitNotesAsync(
        string repoRoot,
        ReviewBotPublishRequest request,
        CancellationToken cancellationToken,
        IReadOnlyList<string>? stagePaths = null
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

        // 3. Stage — scoped to the supplied paths (the commit gate) or the whole worktree by default.
        if (stagePaths is { Count: > 0 })
        {
            foreach (var path in stagePaths)
            {
                await RunGitAsync(["add", "--", path], repoRoot, cancellationToken).ConfigureAwait(false);
            }
        }
        else
        {
            await RunGitAsync(["add", "-A"], repoRoot, cancellationToken).ConfigureAwait(false);
        }

        await RunGitAsync(
                ["commit", "-m", BuildCommitMessage(request)],
                repoRoot,
                cancellationToken)
            .ConfigureAwait(false);

        // 4. Push the review branch (never the default) with bounded rebase-retry, and KEEP the branch —
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
    /// Fetches <c>origin</c>, then merges the remote-tracking <c>origin/{branch}</c> into
    /// <paramref name="defaultBranch"/> (fast-forward when possible, else a merge commit), pushes the
    /// default branch, and — only once that push succeeds — deletes <paramref name="branch"/> (local +
    /// remote). Called when the PR closes merged. Because the sweeper's store clone never creates local
    /// branches, the notes branch is addressed through its remote-tracking ref. Idempotent: when the branch
    /// no longer exists on <c>origin</c> (already merged-and-deleted by a prior sweep) it is a no-op that
    /// returns <c>true</c>. Returns <c>false</c> without deleting the branch when the push never succeeds,
    /// so the caller can retry.
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

        // The sweeper runs against a fresh (or reused-but-never-fetched) store clone that has no LOCAL
        // notes branch — the branch exists only as a remote-tracking ref. Fetch so both the notes branch and
        // the default branch resolve, then merge the REMOTE-TRACKING ref: `git merge <bareName>` does NOT
        // resolve a bare name to origin/<name>, so merging the bare name here would fail every cycle.
        await RunGitAsync(["fetch", "origin"], repoRoot, cancellationToken).ConfigureAwait(false);

        var remoteBranch = $"origin/{branch}";
        var branchExists = await RunGitAsync(
                ["rev-parse", "--verify", remoteBranch],
                repoRoot,
                cancellationToken,
                allowFailure: true)
            .ConfigureAwait(false);
        if (!branchExists.Succeeded)
        {
            // Already merged-and-deleted by a prior sweep (the branch is gone from origin): nothing to
            // resolve. Idempotent no-op rather than a failed merge on a nonexistent ref.
            _logger.LogInformation(
                "ReviewBot merge-to-default: notes branch '{Branch}' no longer exists on origin; nothing to merge.",
                branch);
            return true;
        }

        await RunGitAsync(["checkout", defaultBranch], repoRoot, cancellationToken).ConfigureAwait(false);

        var ffOnly = await RunGitAsync(
                ["merge", "--ff-only", remoteBranch],
                repoRoot,
                cancellationToken,
                allowFailure: true)
            .ConfigureAwait(false);
        if (!ffOnly.Succeeded)
        {
            await RunGitAsync(["merge", "--no-edit", remoteBranch], repoRoot, cancellationToken).ConfigureAwait(false);
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
            var rebased = await RunGitAsync(["pull", "--rebase", "origin", branch], repoRoot, cancellationToken)
                .ConfigureAwait(false);
            if (!rebased.Succeeded)
            {
                // A conflicted/failed rebase leaves the worktree mid-rebase; abort so the reused store
                // checkout starts clean next cycle, and stop retrying — pushing again would only fail into
                // the same rebase and burn the remaining attempts.
                _ = await RunGitAsync(["rebase", "--abort"], repoRoot, cancellationToken, allowFailure: true)
                    .ConfigureAwait(false);
                _logger.LogWarning(
                    "ReviewBot push-with-rebase for '{Branch}' could not rebase onto origin/{Branch} ({Stderr}); aborting retries.",
                    branch,
                    branch,
                    rebased.Stderr);
                break;
            }
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
    /// Builds the review branch name <c>review/{repo}-{pr}</c>. The <c>{repo}</c> segment uses the
    /// normalized, slug-escaped target repo name so it is stable across casing drift and safe as a git ref.
    /// Exposed so callers (and tests) can resolve the branch name for a request.
    /// </summary>
    public string BuildReviewBranchName(ReviewBotPublishRequest request) =>
        BuildReviewBranchName(request.TargetRepo, request.PrNumber);

    /// <summary>
    /// The branch-name builder the executor's commit-notes, the slot preparer, and the PR-lifecycle sweeper
    /// all share so they name a PR's persistent notes branch identically (the sweeper resolves the branch for
    /// a reviewed row without a full request).
    /// </summary>
    public static string BuildReviewBranchName(RepoIdentity repo, int prNumber) =>
        $"review/{RepoSlug(repo)}-{prNumber}";

    private static string BuildCommitMessage(ReviewBotPublishRequest request) =>
        $"Review {request.TargetRepo.RepoName}#{request.PrNumber}";

    /// <summary>Slugs the target repo name into a single ref-safe path segment (lowercased, separators to '-').
    /// Public so the orphan-branch reconciler can match a <c>review/{repo}-{pr}</c> branch back to a configured
    /// repo identity.</summary>
    public static string RepoSlug(RepoIdentity repo) => Slug(repo.RepoName);

    /// <summary>
    /// Reverse of <see cref="BuildReviewBranchName(RepoIdentity, int)"/>: parses a <c>review/{repo}-{pr}</c>
    /// branch into its repo slug and PR number. Returns <c>false</c> for anything that is not a well-formed
    /// new-scheme review branch — including the legacy <c>review/{provider}/{owner-repo}/{pr}</c> form, which
    /// carries embedded '/'.
    /// </summary>
    public static bool TryParseReviewBranch(string branch, out string repoSlug, out int prNumber)
    {
        repoSlug = string.Empty;
        prNumber = 0;
        if (string.IsNullOrEmpty(branch) || !branch.StartsWith("review/", StringComparison.Ordinal))
        {
            return false;
        }

        // The new scheme is a single segment "{slug}-{pr}"; an embedded '/' means the legacy nested form.
        var remainder = branch["review/".Length..];
        if (remainder.Length == 0 || remainder.Contains('/'))
        {
            return false;
        }

        // The PR number is the trailing run of digits after the last '-'; the slug is everything before it
        // (repo slugs may themselves contain '-').
        var lastDash = remainder.LastIndexOf('-');
        if (lastDash <= 0 || lastDash == remainder.Length - 1)
        {
            return false;
        }

        var prPart = remainder[(lastDash + 1)..];
        if (!prPart.All(char.IsAsciiDigit) || !int.TryParse(prPart, out prNumber))
        {
            return false;
        }

        repoSlug = remainder[..lastDash];
        return true;
    }

    /// <summary>Slugs the target identity the LEGACY way — <c>owner[-project]-repo</c> — so the reconciler can
    /// also match pre-<c>{repo}-{pr}</c> branches (<c>review/{provider}/{owner-repo}/{pr}</c>) left over from the
    /// naming change and clean them up when their PR closes.</summary>
    public static string LegacyRepoSlug(RepoIdentity repo)
    {
        var parts = new[] { repo.OrgOrOwner, repo.Project, repo.RepoName }
            .Where(static p => !string.IsNullOrEmpty(p))
            .Select(static p => Slug(p!));
        return string.Join('-', parts);
    }

    /// <summary>
    /// Parses a LEGACY <c>review/{provider}/{owner-repo}/{pr}</c> branch (the pre-<c>{repo}-{pr}</c> form) into
    /// its <c>owner-repo</c> slug and PR number. Returns <c>false</c> for the new single-segment form or anything
    /// malformed. Pairs with <see cref="LegacyRepoSlug"/> so the reconciler can still resolve these orphans.
    /// </summary>
    public static bool TryParseLegacyReviewBranch(string branch, out string repoSlug, out int prNumber)
    {
        repoSlug = string.Empty;
        prNumber = 0;
        if (string.IsNullOrEmpty(branch))
        {
            return false;
        }

        // review / {provider} / {owner-repo} / {pr} — exactly four '/'-separated segments.
        var parts = branch.Split('/');
        if (parts.Length != 4 || !string.Equals(parts[0], "review", StringComparison.Ordinal))
        {
            return false;
        }

        var prPart = parts[3];
        if (parts[1].Length == 0 || parts[2].Length == 0 || prPart.Length == 0
            || !prPart.All(char.IsAsciiDigit) || !int.TryParse(prPart, out prNumber))
        {
            return false;
        }

        repoSlug = parts[2];
        return true;
    }

    private static string Slug(string value)
    {
        var chars = value
            .ToLowerInvariant()
            .Select(static c => char.IsLetterOrDigit(c) || c is '.' or '_' ? c : '-')
            .ToArray();
        return new string(chars).Trim('-');
    }

    private static string JoinPath(string root, string relative) =>
        $"{root.TrimEnd('/')}/{relative.TrimStart('/')}";
}
