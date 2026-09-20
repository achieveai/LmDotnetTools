using System.Globalization;
using System.Text.Json.Nodes;
using AchieveAi.LmDotnetTools.LmWorkflow.Runtime;
using CodeReviewDaemon.Sample.Persistence;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Workspace;
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
/// <see cref="ReviewBranchManager.BuildReviewBranchName(RepoIdentity, int)"/> produced for it
/// (precomputed by the caller — the <c>ReviewStore</c> query supplies the rows and the caller derives the
/// branch name via the <c>listReviewedPrsAsync</c> seam so this type stays test-constructible).
/// </summary>
internal sealed record ReviewedPr(
    RepoIdentity Repo,
    string Provider,
    string PrId,
    string Branch,
    string? Author = null
);

/// <summary>
/// Resolves each reviewed PR once it closes: merged PRs enter the authored workflow, abandoned PRs delete
/// their artifact branch, and open PRs remain untouched.
/// <para>
/// <see cref="ReviewBranchManager.DeleteBranchAsync"/> is a git no-op when an abandoned branch is already gone.
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
    private readonly ILogger<PrLifecycleSweeper> _logger;
    private readonly ReviewStore _store;
    private readonly PrOrchestrator _orchestrator;
    private readonly Func<ReviewedPr, CancellationToken, Task<string?>> _getCurrentHeadShaAsync;

    /// <summary>
    /// Notes branches this daemon lifetime has already resolved to a terminal lifecycle (merged-and-swept, or
    /// abandoned-and-deleted). The reviewed-PR list only grows, so without this the sweep would re-run a GitHub
    /// lifecycle lookup plus a git no-op for every PR it has ever closed, on every poll. Not persisted — a
    /// restart re-confirms each branch once and then caches it, so this can never wrongly skip a PR whose
    /// resolution did not actually complete.
    /// </summary>
    private readonly HashSet<string> _terminallyResolved = new(StringComparer.Ordinal);

    public PrLifecycleSweeper(
        Func<CancellationToken, Task<IReadOnlyList<ReviewedPr>>> listReviewedPrsAsync,
        Func<ReviewedPr, CancellationToken, Task<PrLifecycle>> getPrLifecycleAsync,
        ReviewBranchManager branchManager,
        string repoRoot,
        ILogger<PrLifecycleSweeper> logger,
        ReviewStore store,
        PrOrchestrator orchestrator,
        Func<ReviewedPr, CancellationToken, Task<string?>> getCurrentHeadShaAsync
    )
    {
        _listReviewedPrsAsync = listReviewedPrsAsync ?? throw new ArgumentNullException(nameof(listReviewedPrsAsync));
        _getPrLifecycleAsync = getPrLifecycleAsync ?? throw new ArgumentNullException(nameof(getPrLifecycleAsync));
        _branchManager = branchManager ?? throw new ArgumentNullException(nameof(branchManager));
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);
        _repoRoot = repoRoot;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));
        _getCurrentHeadShaAsync =
            getCurrentHeadShaAsync ?? throw new ArgumentNullException(nameof(getCurrentHeadShaAsync));
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
                    pr.PrId
                );
            }
        }
    }

    private async Task ResolveAsync(ReviewedPr pr, CancellationToken cancellationToken)
    {
        // A branch already resolved to a terminal state this lifetime never needs re-resolving; skipping it
        // avoids a per-poll GitHub lifecycle lookup + git no-op for every PR the daemon has ever closed.
        if (_terminallyResolved.Contains(pr.Branch))
        {
            return;
        }

        var lifecycle = await _getPrLifecycleAsync(pr, cancellationToken).ConfigureAwait(false);
        switch (lifecycle)
        {
            case PrLifecycle.Open:
                // Still open: nothing to resolve yet.
                break;

            case PrLifecycle.Merged:
                if (await ResolveMergedAsync(pr, cancellationToken).ConfigureAwait(false))
                {
                    _terminallyResolved.Add(pr.Branch);
                }
                break;

            case PrLifecycle.Abandoned:
                await using (
                    await HostRetentionWorkspace
                        .AcquireRepositoryLockAsync(_repoRoot, cancellationToken)
                        .ConfigureAwait(false)
                )
                {
                    await _branchManager
                        .DeleteBranchAsync(_repoRoot, pr.Branch, cancellationToken)
                        .ConfigureAwait(false);
                }
                _logger.LogInformation(
                    "PR-lifecycle sweep deleted notes branch '{Branch}' for abandoned {Provider} PR {PrId}.",
                    pr.Branch,
                    pr.Provider,
                    pr.PrId
                );
                _terminallyResolved.Add(pr.Branch);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(pr), lifecycle, "Unhandled PrLifecycle value.");
        }
    }

    /// <summary>
    /// Runs the merged route and returns whether the authored workflow reached durable completion.
    /// </summary>
    private async Task<bool> ResolveMergedAsync(ReviewedPr pr, CancellationToken cancellationToken)
    {
        return await RunMergedWorkflowAsync(pr, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> RunMergedWorkflowAsync(ReviewedPr pr, CancellationToken cancellationToken)
    {
        var repoId = _store.EnsureRepo(pr.Repo);
        var run =
            _store.GetLatestReviewRun(repoId, pr.PrId)
            ?? await CreateMergedOrphanRunAsync(_store, repoId, pr, _getCurrentHeadShaAsync, cancellationToken)
                .ConfigureAwait(false);
        if (run.PrLifecycleState != PrLifecycleState.Merged)
        {
            _store.UpdateReviewRunState(run.Id, run.Stage, run.WorkflowStatus, PrLifecycleState.Merged);
            run = run with { PrLifecycleState = PrLifecycleState.Merged };
        }
        var frozen = new JsonObject
        {
            ["PullRequest"] = new JsonObject
            {
                ["PrId"] = run.PrId,
                ["HeadSha"] = run.HeadSha,
                ["BaseSha"] = run.BaseSha,
                ["Author"] = run.PrAuthor,
                ["Title"] = run.PrTitle,
                ["Description"] = run.PrDescription,
            },
            ["Merge"] = new JsonObject { ["HeadSha"] = run.HeadSha, ["ArtifactBranch"] = pr.Branch },
        };
        var round = _store.CreateOrGetWorkflowRound(
            new WorkflowRoundSeed
            {
                RepoId = run.RepoId,
                PrId = run.PrId,
                HeadSha = run.HeadSha,
                Kind = WorkflowRoundKind.Merged,
                EventKey = run.HeadSha,
                FrozenInputJson = frozen.ToJsonString(),
            }
        );
        return await _orchestrator.RunRoundAsync(run, round, cancellationToken).ConfigureAwait(false)
            == WorkflowInvocationStatus.Completed;
    }

    internal static async Task<ReviewRun> CreateMergedOrphanRunAsync(
        ReviewStore store,
        long repoId,
        ReviewedPr pr,
        Func<ReviewedPr, CancellationToken, Task<string?>> readHead,
        CancellationToken cancellationToken
    )
    {
        var headSha = await readHead(pr, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(headSha))
        {
            throw new InvalidDataException(
                "Merged PR has no durable review run and its provider returned no head SHA."
            );
        }

        return store.CreateOrGetReviewRun(
            new ReviewRun
            {
                RepoId = repoId,
                PrId = pr.PrId,
                HeadSha = headSha,
                // The provider seam exposes the authoritative merged head but no historical base. A zero-width
                // range is safe for history preparation: it cannot accidentally review unrelated repository history.
                BaseSha = headSha,
                TriggerWatermark = $"merged:{headSha}",
                ReviewKind = "merged",
                VariantId = "primary",
                Mode = "collect-only",
                Stage = ReviewStage.Discovered,
                WorkflowStatus = WorkflowStatus.Pending,
                PrLifecycleState = PrLifecycleState.Merged,
                PrAuthor = pr.Author,
            }
        );
    }
}
