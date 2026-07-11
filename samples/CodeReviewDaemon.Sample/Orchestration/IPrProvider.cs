using CodeReviewDaemon.Sample.Persistence.Models;

namespace CodeReviewDaemon.Sample.Orchestration;

/// <summary>
/// The host-neutral seam over a PR host (GitHub, Azure DevOps). The daemon watches PRs by
/// <em>polling</em>, so the only capability the orchestration layer needs is "list the open PRs for a
/// scope, advancing an opaque cursor." Real GitHub/ADO implementations land in P4.4; tests drive a
/// mock. Neither provider's cursor model leaks across the seam (plan §12) — callers treat
/// <see cref="OpaqueCursor"/> as opaque.
/// </summary>
internal interface IPrProvider
{
    /// <summary>Provider namespace this implementation serves, e.g. <c>github</c>.</summary>
    string Provider { get; }

    /// <summary>
    /// Returns the current open PRs for the requested scope plus the cursor to persist for the next
    /// poll. When <see cref="PrPollRequest.Cursor"/> is <c>null</c> the provider resyncs from scratch.
    /// </summary>
    Task<PullRequestPage> ListOpenPullRequestsAsync(PrPollRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Classifies a single PR's terminal lifecycle (Open, Merged, or Abandoned) for the PR-lifecycle
    /// sweep, which merges a reviewed PR's persistent notes branch once the PR merges and deletes it once
    /// the PR is abandoned. Distinct from the coarser <see cref="PrLifecycleState"/> captured while polling
    /// the open-PR list.
    /// </summary>
    Task<PrLifecycle> GetPrStateAsync(RepoIdentity repo, string prId, CancellationToken cancellationToken);
}

/// <summary>
/// A single PR's Open/Merged/Abandoned classification returned by <see cref="IPrProvider.GetPrStateAsync"/>.
/// Feeds the PR-lifecycle sweep (a later task) that merges a PR's notes branch when merged and deletes it
/// when abandoned (closed without merging). Distinct from the coarser <see cref="PrLifecycleState"/>
/// recorded while polling the open-PR list.
/// </summary>
internal enum PrLifecycle
{
    Open,
    Merged,
    Abandoned,
}

/// <summary>One poll request for a single (repo, scope) target.</summary>
internal sealed record PrPollRequest
{
    public required RepoIdentity Repo { get; init; }

    /// <summary>Repo/query identity the cursor advances (e.g. <c>owner/repo:open-prs</c>).</summary>
    public required string Scope { get; init; }

    /// <summary>Previously persisted cursor, or <c>null</c> to resync from scratch (plan §12).</summary>
    public OpaqueCursor? Cursor { get; init; }

    /// <summary>
    /// The recency-window cutoff (UTC) for this poll, or <c>null</c> when no recency filter is configured.
    /// A provider whose PR list carries a real last-activity timestamp ignores this. A provider whose list
    /// does not (Azure DevOps) may use it to fetch a per-PR activity signal — bounded to only the PRs that
    /// would otherwise be excluded — so "updated since" works there too. Providers must not throw if unset.
    /// </summary>
    public DateTimeOffset? RecencyCutoff { get; init; }
}

/// <summary>The result of one poll: the open PRs plus the cursor to persist.</summary>
internal sealed record PullRequestPage
{
    public required IReadOnlyList<PullRequestDescriptor> PullRequests { get; init; }

    public required OpaqueCursor NextCursor { get; init; }
}

/// <summary>
/// A single observed pull request. <see cref="TriggerWatermark"/> distinguishes re-reviews of the
/// same head SHA (e.g. a new comment/thread that should re-trigger) — it is part of the §6 identity
/// tuple, so a new watermark yields a new <c>review_run</c>.
/// </summary>
internal sealed record PullRequestDescriptor
{
    public required string PrId { get; init; }

    public required string HeadSha { get; init; }

    public required string BaseSha { get; init; }

    public required string TriggerWatermark { get; init; }

    public required PrLifecycleState LifecycleState { get; init; }

    /// <summary>
    /// When the PR was opened, if the provider's list exposes it (GitHub <c>created_at</c>, ADO
    /// <c>creationDate</c>). The recency filter (<see cref="Configuration.CodeReviewDaemonOptions.MaxPrAgeDays"/>)
    /// falls back to this when <see cref="UpdatedAt"/> is null. Null when the provider gives no date.
    /// </summary>
    public DateTimeOffset? CreatedAt { get; init; }

    /// <summary>
    /// The PR's last-activity time, if the provider exposes it (GitHub <c>updated_at</c>). ADO's PR list
    /// has no last-activity field, so it is left null there and the recency filter falls back to
    /// <see cref="CreatedAt"/>. Null when the provider gives no date.
    /// </summary>
    public DateTimeOffset? UpdatedAt { get; init; }
}
