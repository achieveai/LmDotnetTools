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
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Posts <paramref name="body"/> as a review comment on the target PR, embedding
    /// <paramref name="idempotencyKey"/> as a hidden marker so <see cref="FindPostedCommentAsync"/> can
    /// recognize it later. Returns the exact provider-qualified object identity used by engagement snapshots
    /// (for example, <c>issue-comment:123</c> or <c>thread:45:comment:6</c>).
    /// </summary>
    Task<PostedComment> PostReviewCommentAsync(
        ReviewCommentTarget target,
        string idempotencyKey,
        string body,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Lists the review comments/threads ALREADY on the target PR (inline findings + review summaries for
    /// GitHub; thread comments for ADO), so the daemon can tell the reviewer what is already flagged and it
    /// only posts genuinely NEW findings. Best-effort and read-only; a bounded, most-recent-first view is
    /// enough for de-duplication. Returns an empty list when the PR has none.
    /// </summary>
    Task<IReadOnlyList<ExistingReviewComment>> ListExistingReviewCommentsAsync(
        ReviewCommentTarget target,
        CancellationToken cancellationToken
    );

    /// <summary>Submits one atomic GitHub review containing every prevalidated inline finding.</summary>
    Task<PostedComment> SubmitInlineReviewAsync(
        ReviewCommentTarget target,
        string idempotencyKey,
        GitHubInlineReviewRequest review,
        CancellationToken cancellationToken
    ) => throw new NotSupportedException($"{Provider} does not support GitHub inline reviews.");

    /// <summary>Replies to the top-level comment that owns a GitHub inline review thread.</summary>
    Task<PostedComment> ReplyToInlineReviewCommentAsync(
        ReviewCommentTarget target,
        string idempotencyKey,
        GitHubInlineReplyRequest reply,
        CancellationToken cancellationToken
    ) => throw new NotSupportedException($"{Provider} does not support GitHub inline review replies.");

    /// <summary>Posts an explicitly degraded flat GitHub PR-conversation comment.</summary>
    Task<PostedComment> PostFlatConversationCommentAsync(
        ReviewCommentTarget target,
        string idempotencyKey,
        GitHubFlatConversationComment comment,
        CancellationToken cancellationToken
    ) => throw new NotSupportedException($"{Provider} does not support GitHub conversation comments.");

    /// <summary>Creates one located Azure DevOps review thread.</summary>
    Task<PostedComment> PostLocatedThreadAsync(
        ReviewCommentTarget target,
        string idempotencyKey,
        AdoThreadRequest thread,
        CancellationToken cancellationToken
    ) => throw new NotSupportedException($"{Provider} does not support Azure DevOps review threads.");

    /// <summary>Replies inside an existing Azure DevOps review thread.</summary>
    Task<PostedComment> ReplyToThreadAsync(
        ReviewCommentTarget target,
        string idempotencyKey,
        AdoThreadReplyRequest reply,
        CancellationToken cancellationToken
    ) => throw new NotSupportedException($"{Provider} does not support Azure DevOps thread replies.");
}

/// <summary>Where a review comment is posted: the normalized repo and the PR within it.</summary>
internal sealed record ReviewCommentTarget(RepoIdentity Repo, string PrId);

/// <summary>A provider-native file span. Offsets are provider columns and remain distinct from lines.</summary>
internal sealed record ProviderCommentSpan(
    string Path,
    string Side,
    int? StartLine,
    int EndLine,
    int? StartOffset = null,
    int? EndOffset = null
);

/// <summary>One GitHub inline finding inside an atomic review.</summary>
internal sealed record GitHubInlineReviewComment(ProviderCommentSpan Span, string Body);

/// <summary>An atomic GitHub COMMENT review tied to the expected PR head.</summary>
internal sealed record GitHubInlineReviewRequest(string CommitId, IReadOnlyList<GitHubInlineReviewComment> Comments);

/// <summary>A GitHub reply whose thread identity is always the top-level review comment.</summary>
internal sealed record GitHubInlineReplyRequest(
    string TopLevelCommentId,
    string TargetCommentId,
    string? TargetPermalink,
    string Body
);

/// <summary>A flat GitHub PR-conversation comment with the native target link retained in its body.</summary>
internal sealed record GitHubFlatConversationComment(string Body, string? TargetPermalink = null);

/// <summary>The Azure DevOps comparison iterations attached to a review thread.</summary>
internal sealed record AdoIterationContext(int FirstComparingIteration, int SecondComparingIteration);

/// <summary>Azure DevOps change tracking plus comparison-iteration identity.</summary>
internal sealed record AdoPullRequestThreadContext(int ChangeTrackingId, AdoIterationContext IterationContext);

/// <summary>A new located Azure DevOps review thread.</summary>
internal sealed record AdoThreadRequest(
    ProviderCommentSpan Span,
    AdoPullRequestThreadContext PullRequestContext,
    string Body
);

/// <summary>A reply to one comment in an existing Azure DevOps review thread.</summary>
internal sealed record AdoThreadReplyRequest(
    string ThreadId,
    string ParentCommentId,
    string? ThreadStatus,
    ProviderCommentSpan? Span,
    AdoPullRequestThreadContext? PullRequestContext,
    string Body
);

/// <summary>
/// A provider-native publication receipt. <see cref="ProviderResponseId"/> remains the stable compatibility
/// identity while the remaining fields retain native review/thread ancestry and anchoring.
/// </summary>
internal sealed record PostedComment(
    string ProviderResponseId,
    string? ReviewId = null,
    string? ThreadId = null,
    string? CommentId = null,
    string? ParentCommentId = null,
    string? Permalink = null,
    string? Status = null,
    ProviderCommentSpan? Span = null,
    AdoPullRequestThreadContext? IterationContext = null,
    bool RelationshipDegraded = false
);

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
    string? ThreadId = null
);

/// <summary>
/// How a provider renders one fetched comment body into <see cref="ExistingReviewComment.Body"/>: flattened
/// to a single line and capped.
/// <para>
/// It lives here, once, because every provider that lists comments needs it and a per-provider copy is a
/// per-provider cap. The two drifted before (#225 item 4 lists both sites), and a cap that differs by
/// provider means a GitHub review and an ADO review see different amounts of the same conversation while
/// both believe they see all of it.
/// </para>
/// <para>
/// <b>Why the cap is generous and not tight.</b> This text is what the reviewer reads to judge whether a
/// thread was RESOLVED, and the resolution signal is usually at the end: a commit sha, "fixed in abc123", the
/// question that was actually asked. A tight cap removes exactly that and leaves the finding it was attached
/// to, so the reviewer re-raises a settled thread. What stops a large conversation inflating the prompt is
/// no longer this number but the ONE shared character budget the executor applies across the whole
/// already-posted block — which is where a budget belongs, since it is the block, not the comment, that has
/// to fit. This cap only bounds a single pathological comment.
/// </para>
/// </summary>
internal static class ExistingCommentBody
{
    /// <summary>Longest single comment body carried into the review prompt.</summary>
    public const int MaxChars = 2000;

    /// <summary>Flattens <paramref name="body"/> to one line and caps it at <see cref="MaxChars"/>,
    /// marking the cut so the reviewer can tell a truncated comment from a terse one.</summary>
    public static string Summarize(string? body)
    {
        var oneLine = (body ?? string.Empty).ReplaceLineEndings(" ").Trim();
        return oneLine.Length <= MaxChars ? oneLine : oneLine[..MaxChars] + "…";
    }
}
