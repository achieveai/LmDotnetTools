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
}

/// <summary>Where a review comment is posted: the normalized repo and the PR within it.</summary>
internal sealed record ReviewCommentTarget(RepoIdentity Repo, string PrId);

/// <summary>A comment that exists on the provider, identified by the provider's own id.</summary>
internal sealed record PostedComment(string ProviderResponseId);
