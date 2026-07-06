using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Workspace.Git;

namespace CodeReviewDaemon.Sample.Orchestration;

/// <summary>
/// One reviewed PR whose persistent notes branch may need resolving once the PR reaches a terminal
/// lifecycle. <see cref="Branch"/> is the review branch
/// <see cref="ReviewBranchManager.BuildReviewBranchName"/> produced for it (precomputed by the caller —
/// the real <c>ReviewStore</c> query wired in a later task; here it is supplied by the
/// <c>listReviewedPrsAsync</c> seam so this type stays test-constructible).
/// </summary>
internal sealed record ReviewedPr(RepoIdentity Repo, string Provider, string PrId, string Branch);

/// <summary>
/// Resolves each reviewed PR's persistent notes branch (<c>review/{provider}/{owner-repo}/{pr}</c>,
/// created once per PR by <see cref="ReviewBranchManager.CommitNotesAsync"/> and kept across re-reviews)
/// once the PR closes: merges the branch into the store default branch when the PR merged (if enabled),
/// deletes it when the PR was abandoned (closed unmerged), and leaves an open PR's branch untouched.
/// <para>
/// Idempotent for free: <see cref="ReviewBranchManager.MergeToDefaultAsync"/> and
/// <see cref="ReviewBranchManager.DeleteBranchAsync"/> are themselves git no-ops on an already-merged or
/// already-deleted branch, so re-sweeping a PR already handled on a prior run does nothing harmful.
/// </para>
/// <para>
/// Each PR is resolved independently in its own try/catch: a transient git/network failure or PR-provider
/// lookup error for one PR is logged at Warning (with the PR id) and swallowed so the rest of the sweep
/// still runs — one bad PR never aborts the sweep, and the next sweep retries it.
/// </para>
/// </summary>
internal sealed class PrLifecycleSweeper
{
    private readonly Func<CancellationToken, Task<IReadOnlyList<ReviewedPr>>> _listReviewedPrsAsync;
    private readonly Func<ReviewedPr, CancellationToken, Task<PrLifecycle>> _getPrLifecycleAsync;
    private readonly ReviewBranchManager _branchManager;
    private readonly string _repoRoot;
    private readonly string _defaultBranch;
    private readonly bool _mergeNotesBranchOnClose;
    private readonly ILogger<PrLifecycleSweeper> _logger;

    public PrLifecycleSweeper(
        Func<CancellationToken, Task<IReadOnlyList<ReviewedPr>>> listReviewedPrsAsync,
        Func<ReviewedPr, CancellationToken, Task<PrLifecycle>> getPrLifecycleAsync,
        ReviewBranchManager branchManager,
        string repoRoot,
        string defaultBranch,
        bool mergeNotesBranchOnClose,
        ILogger<PrLifecycleSweeper> logger
    )
    {
        _listReviewedPrsAsync = listReviewedPrsAsync ?? throw new ArgumentNullException(nameof(listReviewedPrsAsync));
        _getPrLifecycleAsync = getPrLifecycleAsync ?? throw new ArgumentNullException(nameof(getPrLifecycleAsync));
        _branchManager = branchManager ?? throw new ArgumentNullException(nameof(branchManager));
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);
        _repoRoot = repoRoot;
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultBranch);
        _defaultBranch = defaultBranch;
        _mergeNotesBranchOnClose = mergeNotesBranchOnClose;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Fetches the reviewed-PR list once via <c>listReviewedPrsAsync</c>, then resolves each PR's notes
    /// branch per its lifecycle. Never throws for a single PR's failure — see the class summary.
    /// </summary>
    public async Task SweepAsync(CancellationToken cancellationToken)
    {
        var reviewedPrs = await _listReviewedPrsAsync(cancellationToken).ConfigureAwait(false);

        foreach (var pr in reviewedPrs)
        {
            try
            {
                await ResolveAsync(pr, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(
                    ex,
                    "PR-lifecycle sweep failed for {Provider} PR {PrId}; will retry on the next sweep.",
                    pr.Provider,
                    pr.PrId);
            }
        }
    }

    private async Task ResolveAsync(ReviewedPr pr, CancellationToken cancellationToken)
    {
        var lifecycle = await _getPrLifecycleAsync(pr, cancellationToken).ConfigureAwait(false);
        switch (lifecycle)
        {
            case PrLifecycle.Open:
                // Still open: nothing to resolve yet.
                break;

            case PrLifecycle.Merged:
                await ResolveMergedAsync(pr, cancellationToken).ConfigureAwait(false);
                break;

            case PrLifecycle.Abandoned:
                await _branchManager.DeleteBranchAsync(_repoRoot, pr.Branch, cancellationToken)
                    .ConfigureAwait(false);
                _logger.LogInformation(
                    "PR-lifecycle sweep deleted notes branch '{Branch}' for abandoned {Provider} PR {PrId}.",
                    pr.Branch,
                    pr.Provider,
                    pr.PrId);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(pr), lifecycle, "Unhandled PrLifecycle value.");
        }
    }

    private async Task ResolveMergedAsync(ReviewedPr pr, CancellationToken cancellationToken)
    {
        if (!_mergeNotesBranchOnClose)
        {
            _logger.LogInformation(
                "PR-lifecycle sweep left notes branch '{Branch}' for merged {Provider} PR {PrId} (merge-on-close disabled).",
                pr.Branch,
                pr.Provider,
                pr.PrId);
            return;
        }

        var merged = await _branchManager
            .MergeToDefaultAsync(_repoRoot, pr.Branch, _defaultBranch, cancellationToken)
            .ConfigureAwait(false);
        _logger.LogInformation(
            "PR-lifecycle sweep merge of notes branch '{Branch}' for {Provider} PR {PrId} into '{DefaultBranch}': {Merged}.",
            pr.Branch,
            pr.Provider,
            pr.PrId,
            _defaultBranch,
            merged);
    }
}
