namespace CodeReviewDaemon.Sample.Orchestration;

/// <summary>
/// The operator recency bound (<see cref="PrPollTarget.MaxPrAgeDays"/>), extracted so
/// <see cref="PrPollingService"/>'s polling loop and <see cref="ListCandidatePrsCommand"/>'s read-only
/// proposal apply the IDENTICAL cutoff/filter semantics (second security-review round, item 2) rather than
/// one of them silently reviewing/proposing PRs the other would have excluded.
/// </summary>
internal static class PrRecencyFilter
{
    /// <summary>The recency-window cutoff (UTC) for a target with the given bound, or <c>null</c> when the
    /// bound is off (0 or negative) — same rule <see cref="PrPollTarget.MaxPrAgeDays"/> documents.</summary>
    public static DateTimeOffset? ComputeCutoff(int maxPrAgeDays, TimeProvider timeProvider) =>
        maxPrAgeDays > 0 ? timeProvider.GetUtcNow() - TimeSpan.FromDays(maxPrAgeDays) : null;

    /// <summary>
    /// Drops PRs whose last activity (GitHub <c>updated_at</c>; ADO the source branch's last push, resolved
    /// by the provider) or, as a fallback, opened date is older than <paramref name="cutoff"/>. A PR the
    /// provider gave no date for is kept — the filter never silently drops a PR it can't date. When
    /// <paramref name="cutoff"/> is null the full list passes through unchanged.
    /// </summary>
    public static IReadOnlyList<PullRequestDescriptor> Apply(
        DateTimeOffset? cutoff,
        IReadOnlyList<PullRequestDescriptor> pullRequests
    )
    {
        if (cutoff is null || pullRequests.Count == 0)
        {
            return pullRequests;
        }

        var kept = new List<PullRequestDescriptor>(pullRequests.Count);
        foreach (var pr in pullRequests)
        {
            var activity = pr.UpdatedAt ?? pr.CreatedAt;
            if (activity is null || activity.Value >= cutoff.Value)
            {
                kept.Add(pr);
            }
        }

        return kept;
    }
}
