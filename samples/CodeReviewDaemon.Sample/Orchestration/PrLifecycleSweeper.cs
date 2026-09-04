using System.Globalization;
using CodeReviewDaemon.Sample.Persistence;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Workspace.Git;

namespace CodeReviewDaemon.Sample.Orchestration;

/// <summary>
/// The glue the daemon composes the <see cref="PrLifecycleSweeper"/>'s two seams from (kept out of the
/// composition root so it is unit-testable): mapping a reviewed-PR row to a sweep unit and routing a sweep
/// unit's lifecycle lookup to the matching provider.
/// </summary>
internal static class PrLifecycleSweepSeam
{
    /// <summary>
    /// Maps a <see cref="ReviewedPrRow"/> from <see cref="ReviewStore.ListReviewedPrsAsync"/> to a
    /// <see cref="ReviewedPr"/> sweep unit: the storage provider is mapped to the branch/poll namespace
    /// (<c>azure-devops</c> → <c>ado</c>) and the persistent notes branch name is derived the same way the
    /// executor's commit-notes does (<see cref="ReviewBranchManager.BuildReviewBranchName(RepoIdentity, int)"/>),
    /// so the sweep targets the exact branch the reviews pushed to. Returns <c>null</c> for a non-numeric PR
    /// id (which cannot name a branch) so the caller can skip it without aborting the sweep.
    /// </summary>
    public static ReviewedPr? MapReviewedPr(ReviewedPrRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        if (!int.TryParse(row.PrId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var prNumber))
        {
            return null;
        }

        var provider = RepoIdentity.ToPublisherNamespace(row.Provider);
        return new ReviewedPr(
            row.Repo,
            provider,
            row.PrId,
            ReviewBranchManager.BuildReviewBranchName(row.Repo, prNumber),
            row.Author
        );
    }

    /// <summary>
    /// Routes a sweep unit's lifecycle lookup to the <see cref="IPrProvider"/> whose namespace matches the
    /// PR's (mapped) provider, throwing when none is registered — the <c>getPrLifecycleAsync</c> seam.
    /// </summary>
    public static Task<PrLifecycle> ResolveLifecycleAsync(
        IReadOnlyList<IPrProvider> providers,
        ReviewedPr pr,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(pr);
        return ResolveLifecycleAsync(providers, pr.Repo, pr.Provider, pr.PrId, cancellationToken);
    }

    /// <summary>
    /// The same lookup addressed by identity rather than by sweep unit, for callers (the stranded-run
    /// reconciler) that hold a repo and PR id but no notes branch. <paramref name="provider"/> must already be
    /// in the registry's namespace — see <see cref="RepoIdentity.ToPublisherNamespace(string)"/>.
    /// </summary>
    public static Task<PrLifecycle> ResolveLifecycleAsync(
        IReadOnlyList<IPrProvider> providers,
        RepoIdentity repo,
        string provider,
        string prId,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(repo);

        var prProvider =
            providers.FirstOrDefault(p => string.Equals(p.Provider, provider, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"No IPrProvider registered for '{provider}'.");
        return prProvider.GetPrStateAsync(repo, prId, cancellationToken);
    }
}

/// <summary>
/// One reviewed PR whose persistent notes branch may need resolving once the PR reaches a terminal
/// lifecycle. <see cref="Branch"/> is the review branch
/// <see cref="ReviewBranchManager.BuildReviewBranchName(RepoIdentity, int)"/> produced for it.
/// </summary>
internal sealed record ReviewedPr(
    RepoIdentity Repo,
    string Provider,
    string PrId,
    string Branch,
    string? Author = null
);

/// <summary>
/// Looks up authoritative lifecycle for reviewed PRs. Merged PRs are reported to the engagement
/// coordinator; the durable <c>MergedClose</c> round owns all close processing and is the only path allowed
/// to archive the notes branch. Abandoned PRs are reported and then cleaned according to existing policy.
/// </summary>
internal sealed class PrLifecycleSweeper
{
    private readonly Func<CancellationToken, Task<IReadOnlyList<ReviewedPr>>> _listReviewedPrsAsync;
    private readonly Func<ReviewedPr, CancellationToken, Task<PrLifecycle>> _getPrLifecycleAsync;
    private readonly ReviewBranchManager _branchManager;
    private readonly string _repoRoot;
    private readonly ILogger<PrLifecycleSweeper> _logger;
    private readonly Func<ReviewedPr, PrLifecycle, CancellationToken, Task<EngagementDecision>>? _observeTerminalAsync;
    private readonly Func<EngagementDecision, CancellationToken, Task>? _runRoundAsync;
    private readonly Func<
        ReviewedPr,
        PrLifecycle,
        CancellationToken,
        Task<EngagementDecision>
    >? _reconcileTerminalAsync;

    public PrLifecycleSweeper(
        Func<CancellationToken, Task<IReadOnlyList<ReviewedPr>>> listReviewedPrsAsync,
        Func<ReviewedPr, CancellationToken, Task<PrLifecycle>> getPrLifecycleAsync,
        ReviewBranchManager branchManager,
        string repoRoot,
        ILogger<PrLifecycleSweeper> logger,
        Func<ReviewedPr, PrLifecycle, CancellationToken, Task<EngagementDecision>>? observeTerminalAsync = null,
        Func<EngagementDecision, CancellationToken, Task>? runRoundAsync = null,
        Func<ReviewedPr, PrLifecycle, CancellationToken, Task<EngagementDecision>>? reconcileTerminalAsync = null
    )
    {
        _listReviewedPrsAsync = listReviewedPrsAsync ?? throw new ArgumentNullException(nameof(listReviewedPrsAsync));
        _getPrLifecycleAsync = getPrLifecycleAsync ?? throw new ArgumentNullException(nameof(getPrLifecycleAsync));
        _branchManager = branchManager ?? throw new ArgumentNullException(nameof(branchManager));
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);
        _repoRoot = repoRoot;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _observeTerminalAsync = observeTerminalAsync;
        _runRoundAsync = runRoundAsync;
        _reconcileTerminalAsync = reconcileTerminalAsync;
    }

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
                    pr.PrId
                );
            }
        }
    }

    private async Task ResolveAsync(ReviewedPr pr, CancellationToken cancellationToken)
    {
        var lifecycle = await _getPrLifecycleAsync(pr, cancellationToken).ConfigureAwait(false);
        switch (lifecycle)
        {
            case PrLifecycle.Open:
                return;

            case PrLifecycle.Merged:
                if (_observeTerminalAsync is null)
                {
                    _logger.LogWarning(
                        "PR-lifecycle sweep preserved notes branch '{Branch}' for merged {Provider} PR {PrId} because no engagement observer is configured.",
                        pr.Branch,
                        pr.Provider,
                        pr.PrId
                    );
                    return;
                }

                var decision = await _observeTerminalAsync(pr, lifecycle, cancellationToken).ConfigureAwait(false);
                if (decision.ReasonCode == "merged_close_active" && _reconcileTerminalAsync is not null)
                {
                    decision = await _reconcileTerminalAsync(pr, lifecycle, cancellationToken).ConfigureAwait(false);
                }

                if (decision.Kind == EngagementDecisionKind.AdmitMergedClose)
                {
                    if (_runRoundAsync is null)
                    {
                        throw new InvalidOperationException(
                            "The engagement coordinator admitted merged-close work, but no round runner is configured."
                        );
                    }

                    await _runRoundAsync(decision, cancellationToken).ConfigureAwait(false);
                }

                _logger.LogInformation(
                    "PR-lifecycle sweep reported merged {Provider} PR {PrId}: {Decision} ({ReasonCode}).",
                    pr.Provider,
                    pr.PrId,
                    decision.Kind,
                    decision.ReasonCode
                );
                return;

            case PrLifecycle.Abandoned:
                if (_observeTerminalAsync is not null)
                {
                    try
                    {
                        _ = await _observeTerminalAsync(pr, lifecycle, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(
                            ex,
                            "Could not record terminal engagement state for abandoned {Provider} PR {PrId}; branch cleanup continues.",
                            pr.Provider,
                            pr.PrId
                        );
                    }
                }

                await _branchManager.DeleteBranchAsync(_repoRoot, pr.Branch, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation(
                    "PR-lifecycle sweep deleted notes branch '{Branch}' for abandoned {Provider} PR {PrId}.",
                    pr.Branch,
                    pr.Provider,
                    pr.PrId
                );
                return;

            default:
                throw new ArgumentOutOfRangeException(nameof(pr), lifecycle, "Unhandled PrLifecycle value.");
        }
    }
}
