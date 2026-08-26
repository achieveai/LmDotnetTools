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
    Task<PrStatus> GetPrStateAsync(RepoIdentity repo, string prId, CancellationToken cancellationToken);
}

/// <summary>A provider-neutral readiness classification. Unknown always fails closed.</summary>
internal enum PrDraftState
{
    Unknown,
    Ready,
    Draft,
}

/// <summary>The independently observed lifecycle and readiness of one PR.</summary>
internal sealed record PrStatus(PrLifecycle Lifecycle, PrDraftState DraftState);

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

    /// <summary>Whether the provider positively reports this PR ready, draft, or cannot tell.</summary>
    public required PrDraftState DraftState { get; init; }

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
    /// The PR's title (GitHub <c>title</c>, ADO <c>title</c>) — the change's stated intent in one line.
    /// Null when the provider payload omits it.
    /// </summary>
    public string? Title { get; init; }

    /// <summary>
    /// The PR's description body (GitHub <c>body</c>, ADO <c>description</c>), as written by its author.
    /// <para>
    /// Author-controlled prose, so it is UNTRUSTED DATA wherever it reaches an agent prompt and must be
    /// framed as quoted content there, never as instructions. Null when the payload omits it.
    /// </para>
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// The branch the PR merges INTO, in short form (GitHub <c>base.ref</c>; ADO <c>targetRefName</c>
    /// with its <c>refs/heads/</c> prefix stripped). Null when the provider payload omits it.
    /// </summary>
    public string? TargetBranch { get; init; }

    // ── Confidentiality trust signal (design §6 Risk B) ──────────────────────────────────────────
    /// <summary>
    /// Whether the PR's head comes from a FORK of the target repo (GitHub: <c>head.repo.full_name</c> differs
    /// from <c>base.repo.full_name</c>; ADO: the payload carries a <c>forkSource</c>). <c>null</c> means the
    /// provider could not determine it from the payload it received.
    /// <para>
    /// Deliberately nullable, and deliberately NOT defaulted here. This is one half of the gate that decides
    /// whether a private sibling repo may be co-located beside an untrusted diff, so "the provider says no"
    /// and "the provider could not tell" must stay distinguishable all the way to the run. <c>PrPollingService</c>
    /// is the single place that collapses <c>null</c> to the fail-closed <c>true</c> — see
    /// <see cref="Persistence.Models.ReviewRun.IsForkPr"/>.
    /// </para>
    /// </summary>
    public bool? IsForkPr { get; init; }

    /// <summary>
    /// Whether the repo the PR targets is publicly visible (GitHub: <c>base.repo.private</c> inverted; ADO:
    /// <c>repository.project.visibility == "public"</c>). <c>null</c> when the payload does not say.
    /// Collapsed to the fail-closed <c>true</c> by <c>PrPollingService</c>, exactly like
    /// <see cref="IsForkPr"/>.
    /// </summary>
    public bool? IsTargetRepoPublic { get; init; }
}
