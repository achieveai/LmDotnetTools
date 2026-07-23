using CodeReviewDaemon.Sample.Persistence.Models;

namespace CodeReviewDaemon.Sample.Orchestration;

/// <summary>
/// The focused posting seam consumed only by <see cref="ReviewPoster"/> (plan §11). It is deliberately
/// separate from <see cref="IPrProvider"/> (which only <em>reads</em> open PRs) so the read path carries
/// no posting capability. Real GitHub/ADO implementations land in P4.4; tests drive a fake.
/// <para>
/// <see cref="FindPostedCommentAsync"/> is the provider-side backstop to the outbox: if a previous
/// attempt posted the comment but crashed before recording it, the daemon can still discover it by
/// scanning for the idempotency-key marker rather than posting a duplicate.
/// </para>
/// </summary>
internal interface IReviewCommentPublisher
{
    /// <summary>Provider namespace this publisher serves, e.g. <c>github</c>.</summary>
    string Provider { get; }

    /// <summary>
    /// Scans the target PR for a comment already carrying <paramref name="idempotencyKey"/> and returns
    /// it, or <c>null</c> when none exists. This is the exactly-once backstop for the case where a post
    /// succeeded provider-side but the outbox transition never committed.
    /// </summary>
    Task<PostedComment?> FindPostedCommentAsync(
        ReviewCommentTarget target,
        string idempotencyKey,
        CancellationToken cancellationToken);

    /// <summary>
    /// Posts <paramref name="body"/> as a review comment on the target PR, embedding
    /// <paramref name="idempotencyKey"/> as a hidden marker so <see cref="FindPostedCommentAsync"/> can
    /// recognize it later. Returns the provider's id for the created comment.
    /// </summary>
    Task<PostedComment> PostReviewCommentAsync(
        ReviewCommentTarget target,
        string idempotencyKey,
        string body,
        CancellationToken cancellationToken);

    /// <summary>
    /// Lists the review comments/threads ALREADY on the target PR (inline findings + review summaries for
    /// GitHub; thread comments for ADO), so the daemon can tell the reviewer what is already flagged and it
    /// only posts genuinely NEW findings. Best-effort and read-only; a bounded, most-recent-first view is
    /// enough for de-duplication. Returns an empty list when the PR has none.
    /// </summary>
    Task<IReadOnlyList<ExistingReviewComment>> ListExistingReviewCommentsAsync(
        ReviewCommentTarget target,
        CancellationToken cancellationToken);
}

/// <summary>Where a review comment is posted: the normalized repo and the PR within it.</summary>
internal sealed record ReviewCommentTarget(RepoIdentity Repo, string PrId);

/// <summary>A comment that exists on the provider, identified by the provider's own id.</summary>
internal sealed record PostedComment(string ProviderResponseId);

/// <summary>
/// A review comment already present on a PR. <see cref="Path"/>/<see cref="Line"/> are set for an inline
/// finding and null for a PR-level summary/issue comment. <see cref="Body"/> is the (possibly trimmed)
/// comment text; <see cref="Author"/> is the login/display name that posted it (bot or human).
/// <see cref="IsActive"/> is true when the comment/thread is still OPEN (unresolved / not acted on) — the
/// daemon must not re-post a finding that matches an ACTIVE comment, whereas a RESOLVED one may be re-raised
/// if the issue persists. ADO exposes thread status directly; GitHub's REST list cannot tell resolved from
/// open for review comments, so those default to active (conservative — never re-post a possibly-open one).
/// <see cref="PublishedAt"/> is when the comment was posted (used to split "past reviews" from "new since
/// the last review"); <see cref="ThreadId"/> groups the comments of one thread so the reviewer sees the full
/// conversation (finding + replies) and can judge for itself whether the thread was resolved.
/// </summary>
internal sealed record ExistingReviewComment(
    string? Path,
    string? Line,
    string Body,
    string? Author,
    bool IsActive = true,
    DateTimeOffset? PublishedAt = null,
    string? ThreadId = null);
