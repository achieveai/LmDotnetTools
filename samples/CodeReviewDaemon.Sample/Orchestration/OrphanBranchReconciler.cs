using System.Globalization;
using CodeReviewDaemon.Sample.Workspace.Git;

namespace CodeReviewDaemon.Sample.Orchestration;

/// <summary>
/// Reconciles the PR-lifecycle sweep's watch-set against the review store's actual <c>review/*</c> branches so
/// a PR whose review row is missing from this daemon's SQLite DB — a fresh DB after a restart, or DB churn —
/// still gets its notes branch merged-or-deleted when the PR closes. Without this, such a branch is orphaned:
/// the merged PR's notes never reach the store default branch and the abandoned PR's branch is never deleted.
/// <para>
/// The DB-derived set is authoritative and always kept. This only <b>adds</b> orphaned branches whose slug
/// matches a configured poll target — either the new <c>review/{repo}-{pr}</c> form (see
/// <see cref="ReviewBranchManager.TryParseReviewBranch"/>) or the legacy <c>review/{provider}/{owner-repo}/{pr}</c>
/// form (see <see cref="ReviewBranchManager.TryParseLegacyReviewBranch"/>), so orphans from either naming
/// generation get cleaned up. The daemon needs that target's <see cref="PrPollTarget.Repo"/> identity and
/// <see cref="PrPollTarget.Provider"/> to look the PR's lifecycle up. A branch that matches no configured repo,
/// or is unparseable, is logged and skipped: its identity cannot be recovered from the name.
/// </para>
/// </summary>
internal static class OrphanBranchReconciler
{
    public static IReadOnlyList<ReviewedPr> Reconcile(
        IReadOnlyList<ReviewedPr> fromDb,
        IReadOnlyList<string> remoteReviewBranches,
        IReadOnlyList<PrPollTarget> configuredTargets,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(fromDb);
        ArgumentNullException.ThrowIfNull(remoteReviewBranches);
        ArgumentNullException.ThrowIfNull(configuredTargets);
        ArgumentNullException.ThrowIfNull(logger);

        var result = new List<ReviewedPr>(fromDb);
        // Branches already covered by the DB set (or seen earlier in this loop) are not re-added.
        var covered = new HashSet<string>(fromDb.Select(static p => p.Branch), StringComparer.Ordinal);

        var targetsBySlug = new Dictionary<string, PrPollTarget>(StringComparer.OrdinalIgnoreCase);
        foreach (var target in configuredTargets)
        {
            // Index by both the new {repo} slug and the legacy {owner-repo} slug so orphans from either naming
            // generation resolve back to the same configured repo.
            targetsBySlug[ReviewBranchManager.RepoSlug(target.Repo)] = target;
            targetsBySlug[ReviewBranchManager.LegacyRepoSlug(target.Repo)] = target;
        }

        foreach (var branch in remoteReviewBranches)
        {
            if (!covered.Add(branch))
            {
                continue;
            }

            // New scheme first, then the legacy nested form, so both orphan generations get cleaned up.
            if (!ReviewBranchManager.TryParseReviewBranch(branch, out var slug, out var prNumber)
                && !ReviewBranchManager.TryParseLegacyReviewBranch(branch, out slug, out prNumber))
            {
                continue;
            }

            if (!targetsBySlug.TryGetValue(slug, out var target))
            {
                logger.LogWarning(
                    "PR-lifecycle sweep: orphaned notes branch '{Branch}' matches no configured repo; skipping.",
                    branch);
                continue;
            }

            result.Add(new ReviewedPr(
                target.Repo, target.Provider, prNumber.ToString(CultureInfo.InvariantCulture), branch));
        }

        return result;
    }
}
