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

    /// <summary>
    /// Returns the PR's head SHA <em>as the host reports it right now</em>, or <c>null</c> when the
    /// provider's payload carries none. This is the authority a
    /// <see cref="PullRequestDescriptor.HeadSha"/> recorded at poll time is checked against before a review
    /// built on it is allowed to reach the author: a branch can be force-pushed between the poll that
    /// created a run and the review that executes it, and the run row cannot notice — its
    /// <c>head_sha</c> is part of its identity and is written once, so re-reading the daemon's own store
    /// re-reads the very value under suspicion.
    /// <para>
    /// Implementations must issue a LIVE read (no caching) and must not translate a transport failure into
    /// <c>null</c>: <c>null</c> means "this host does not report a head for this PR", which is a different
    /// answer from "the host could not be reached", and only the first is safe to treat as
    /// "nothing contradicts the recorded head."
    /// </para>
    /// </summary>
    Task<string?> GetCurrentHeadShaAsync(RepoIdentity repo, string prId, CancellationToken cancellationToken);
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

    /// <summary>
    /// The identity that OPENED the pull request (GitHub <c>user.login</c>, ADO
    /// <c>createdBy.uniqueName</c>), used to address the per-developer review-feedback record. This is
    /// deliberately the PR author and not the author of any individual commit or comment: the record
    /// answers "what does the person who submitted this work keep getting wrong?".
    /// <para>
    /// Optional, and null whenever the provider payload omits it. A null author is not an error — it
    /// simply means no feedback record is addressable for this PR, and the daemon writes nothing rather
    /// than inventing a placeholder identity. Callers must never substitute a commenter or a bot here.
    /// </para>
    /// </summary>
    public string? Author { get; init; }

    /// <summary>
    /// What the PR SAYS it does (GitHub <c>title</c>, ADO <c>title</c>). Captured at poll time and carried
    /// on the run, because sibling PRs applying one architectural pattern frequently touch entirely
    /// different files — the pattern is named here and nowhere in the changed-path listing.
    /// <para>
    /// Null whenever the provider payload omits it. Consumers must degrade to path-only behaviour rather
    /// than substituting a placeholder.
    /// </para>
    /// </summary>
    public string? Title { get; init; }

    /// <summary>
    /// The PR's description body (GitHub <c>body</c>, ADO <c>description</c>). Fully author-controlled
    /// prose: any consumer that renders it into an agent prompt must frame it as quoted UNTRUSTED DATA.
    /// Null when the provider payload omits it.
    /// </summary>
    public string? Description { get; init; }
}
