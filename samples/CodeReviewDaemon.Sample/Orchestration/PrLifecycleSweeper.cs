using System.Globalization;
using CodeReviewDaemon.Sample.Agents;
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

        var provider = string.Equals(row.Provider, "azure-devops", StringComparison.Ordinal) ? "ado" : row.Provider;
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
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(pr);

        var provider =
            providers.FirstOrDefault(p => string.Equals(p.Provider, pr.Provider, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"No IPrProvider registered for '{pr.Provider}'.");
        return ResolveLifecycleOnlyAsync(provider, pr, cancellationToken);
    }

    private static async Task<PrLifecycle> ResolveLifecycleOnlyAsync(
        IPrProvider provider,
        ReviewedPr pr,
        CancellationToken cancellationToken
    )
    {
        var status = await provider.GetPrStateAsync(pr.Repo, pr.PrId, cancellationToken).ConfigureAwait(false);
        return status.Lifecycle;
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
/// Resolves each reviewed PR's persistent notes branch (<c>review/{repo}-{pr}</c>,
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

    /// <summary>
    /// Optional at-close knowledge-extraction seam (Layer-2, design §1): distills durable knowledge from a
    /// merged PR's accumulated notes into the store's Knowledge Base BEFORE the notes branch merges into the
    /// default branch, so the same merge carries the new/updated entry into <c>main</c>. Wired in
    /// <c>Program.cs</c> only when <c>EnableKnowledgeAgent</c> is set; <c>null</c> leaves the sweep unchanged.
    /// Runs on the Merged path only — never on Open/Abandoned — and its failure never blocks the lifecycle.
    /// <para>
    /// The returned <see cref="KnowledgeExtractionOutcome"/> is what makes a failed extraction recoverable: the
    /// merge deletes the notes branch, so merging over a failure destroys the only input a retry could use.
    /// </para>
    /// </summary>
    private readonly Func<ReviewedPr, CancellationToken, Task<KnowledgeExtractionOutcome>>? _extractKnowledgeAsync;

    /// <summary>
    /// How many sweeps may defer a merged PR's merge waiting for its knowledge extraction to succeed. Extraction
    /// must never block the lifecycle outright (design §6), so the delay is bounded: once a PR has burned this
    /// many attempts the sweep merges anyway and the extraction is lost — loudly, not silently.
    /// </summary>
    private const int MaxExtractionAttempts = 3;

    /// <summary>Extraction attempts spent per notes branch. Not persisted; a restart restarts the budget.</summary>
    private readonly Dictionary<string, int> _extractionAttempts = new(StringComparer.Ordinal);

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
        string defaultBranch,
        bool mergeNotesBranchOnClose,
        ILogger<PrLifecycleSweeper> logger,
        Func<ReviewedPr, CancellationToken, Task<KnowledgeExtractionOutcome>>? extractKnowledgeAsync = null
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
        _extractKnowledgeAsync = extractKnowledgeAsync;
    }

    /// <summary>
    /// Fetches the reviewed-PR list once via <c>listReviewedPrsAsync</c>, then resolves each PR's notes
    /// branch per its lifecycle. Never throws for a single PR's failure — see the class summary.
    /// <para>
    /// Every sweep emits one summary line, because this method is the sole trigger for at-close knowledge
    /// extraction and used to be completely silent. An empty Knowledge Base has three quite different causes
    /// — nothing has merged yet, extraction runs and fails, or the extraction seam was never composed in —
    /// and without the tally below they are indistinguishable from the outside.
    /// </para>
    /// </summary>
    public async Task SweepAsync(CancellationToken cancellationToken)
    {
        var reviewedPrs = await _listReviewedPrsAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogDebug(
            "PR-lifecycle sweep starting over {WatchedCount} reviewed PR(s); {ResolvedCount} already reached a "
                + "terminal lifecycle this daemon lifetime and will be skipped.",
            reviewedPrs.Count,
            _terminallyResolved.Count
        );

        var open = 0;
        var merged = 0;
        var abandoned = 0;
        var alreadyResolved = 0;
        var failed = 0;

        foreach (var pr in reviewedPrs)
        {
            try
            {
                switch (await ResolveAsync(pr, cancellationToken).ConfigureAwait(false))
                {
                    case SweepOutcome.AlreadyResolved:
                        alreadyResolved++;
                        break;
                    case SweepOutcome.Open:
                        open++;
                        break;
                    case SweepOutcome.Merged:
                        merged++;
                        break;
                    case SweepOutcome.Abandoned:
                        abandoned++;
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(
                            nameof(pr),
                            pr,
                            "ResolveAsync returned an unhandled SweepOutcome."
                        );
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Counted, not just logged: a lookup that throws must not be indistinguishable from a PR that
                // is genuinely still open, or the merged population the tally exists to measure is understated
                // by exactly the PRs whose state we failed to read.
                failed++;
                _logger.LogWarning(
                    ex,
                    "PR-lifecycle sweep failed for {Provider} PR {PrId}; will retry on the next sweep.",
                    pr.Provider,
                    pr.PrId
                );
            }
        }

        _logger.LogInformation(
            "PR-lifecycle sweep finished over {WatchedCount} reviewed PR(s): {OpenCount} open, {MergedCount} "
                + "merged, {AbandonedCount} abandoned, {AlreadyResolvedCount} already resolved, {FailedCount} "
                + "failed; knowledge extraction is {KnowledgeExtractionState}. At-close knowledge extraction "
                + "runs on the merged path only, so a merged count of 0 means the Knowledge Base had no "
                + "opportunity to gain an entry this sweep.",
            reviewedPrs.Count,
            open,
            merged,
            abandoned,
            alreadyResolved,
            failed,
            _extractKnowledgeAsync is null ? "not wired" : "wired"
        );
    }

    /// <summary>What one PR's resolution did, so <see cref="SweepAsync"/> can tally a sweep. Deliberately
    /// classified by the lifecycle the provider reported rather than by what the notes branch ended up doing:
    /// a merged PR whose merge is deferred for a knowledge-extraction retry still counts as merged, because
    /// the question this tally answers is "did anything close that extraction could have run on?".</summary>
    private enum SweepOutcome
    {
        AlreadyResolved,
        Open,
        Merged,
        Abandoned,
    }

    private async Task<SweepOutcome> ResolveAsync(ReviewedPr pr, CancellationToken cancellationToken)
    {
        // A branch already resolved to a terminal state this lifetime never needs re-resolving; skipping it
        // avoids a per-poll GitHub lifecycle lookup + git no-op for every PR the daemon has ever closed.
        if (_terminallyResolved.Contains(pr.Branch))
        {
            return SweepOutcome.AlreadyResolved;
        }

        var lifecycle = await _getPrLifecycleAsync(pr, cancellationToken).ConfigureAwait(false);
        switch (lifecycle)
        {
            case PrLifecycle.Open:
                // Still open: nothing to resolve yet.
                return SweepOutcome.Open;

            case PrLifecycle.Merged:
                if (await ResolveMergedAsync(pr, cancellationToken).ConfigureAwait(false))
                {
                    _terminallyResolved.Add(pr.Branch);
                }
                return SweepOutcome.Merged;

            case PrLifecycle.Abandoned:
                await _branchManager.DeleteBranchAsync(_repoRoot, pr.Branch, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation(
                    "PR-lifecycle sweep deleted notes branch '{Branch}' for abandoned {Provider} PR {PrId}.",
                    pr.Branch,
                    pr.Provider,
                    pr.PrId
                );
                _terminallyResolved.Add(pr.Branch);
                return SweepOutcome.Abandoned;

            default:
                throw new ArgumentOutOfRangeException(nameof(pr), lifecycle, "Unhandled PrLifecycle value.");
        }
    }

    /// <summary>
    /// Resolves a merged PR's notes branch (KB extraction, then merge-to-default). Returns <c>true</c> when the
    /// branch reached a terminal state — merged, already gone, or intentionally left because merge-on-close is
    /// disabled — and <c>false</c> when the merge should be retried on the next sweep, either because the merge
    /// push failed or because knowledge extraction failed and still has attempts left.
    /// </summary>
    private async Task<bool> ResolveMergedAsync(ReviewedPr pr, CancellationToken cancellationToken)
    {
        if (!_mergeNotesBranchOnClose)
        {
            _logger.LogInformation(
                "PR-lifecycle sweep left notes branch '{Branch}' for merged {Provider} PR {PrId} (merge-on-close disabled).",
                pr.Branch,
                pr.Provider,
                pr.PrId
            );
            return true;
        }

        // Layer-2 (design §1): distill durable knowledge from the PR's accumulated notes BEFORE the notes
        // branch merges into the default branch, so the same merge carries the new/updated entry into main.
        if (
            _extractKnowledgeAsync is not null
            && !await TryExtractKnowledgeAsync(pr, cancellationToken).ConfigureAwait(false)
        )
        {
            // Extraction failed with attempts left. Returning false leaves the branch uncached AND unmerged, so
            // the next sweep retries against notes that still exist — merging here would delete the only input
            // a retry could use and make the failure permanent (defect D5).
            return false;
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
            merged
        );
        if (merged)
        {
            _extractionAttempts.Remove(pr.Branch);
        }

        return merged;
    }

    /// <summary>
    /// Runs one knowledge-extraction attempt for <paramref name="pr"/>. Returns <c>true</c> when the merge may
    /// proceed — the extraction wrote an entry, legitimately declined, or has now burned every attempt — and
    /// <c>false</c> when it failed with attempts left and the caller should defer the merge for a retry.
    /// Extraction never throws out of here: a capability gap degrades the lifecycle, never fails it (design §6).
    /// </summary>
    private async Task<bool> TryExtractKnowledgeAsync(ReviewedPr pr, CancellationToken cancellationToken)
    {
        KnowledgeExtractionOutcome outcome;
        try
        {
            outcome = await _extractKnowledgeAsync!(pr, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "PR-lifecycle sweep knowledge extraction threw for merged {Provider} PR {PrId}.",
                pr.Provider,
                pr.PrId
            );
            outcome = KnowledgeExtractionOutcome.Failed;
        }

        if (outcome != KnowledgeExtractionOutcome.Failed)
        {
            return true;
        }

        var attempts = _extractionAttempts.GetValueOrDefault(pr.Branch) + 1;
        _extractionAttempts[pr.Branch] = attempts;
        if (attempts < MaxExtractionAttempts)
        {
            _logger.LogWarning(
                "PR-lifecycle sweep knowledge extraction failed for merged {Provider} PR {PrId} "
                    + "(attempt {Attempt} of {MaxAttempts}); holding notes branch '{Branch}' back for a retry.",
                pr.Provider,
                pr.PrId,
                attempts,
                MaxExtractionAttempts,
                pr.Branch
            );
            return false;
        }

        // The delay extraction may impose on the lifecycle is bounded (design §6). Say so loudly: this is the
        // one path where knowledge is genuinely lost, and it must not look like an ordinary merge.
        _logger.LogWarning(
            "PR-lifecycle sweep knowledge extraction failed for merged {Provider} PR {PrId} on all "
                + "{MaxAttempts} attempts; merging notes branch '{Branch}' anyway — this PR's knowledge is lost.",
            pr.Provider,
            pr.PrId,
            MaxExtractionAttempts,
            pr.Branch
        );
        return true;
    }
}
