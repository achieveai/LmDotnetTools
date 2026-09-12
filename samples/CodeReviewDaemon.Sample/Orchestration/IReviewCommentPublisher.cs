using CodeReviewDaemon.Sample.Persistence.Models;

namespace CodeReviewDaemon.Sample.Orchestration;

/// <summary>
/// The focused posting seam consumed only by <see cref="ReviewPoster"/> (plan §11). It is deliberately
/// separate from <see cref="IPrProvider"/> (which only <em>reads</em> open PRs) so the read path carries
/// no posting capability. Real GitHub/ADO implementations land in P4.4; tests drive a fake.
/// <para>
/// <see cref="IReviewCommentPublisher.FindPostedCommentAsync"/> is the provider-side backstop to the outbox: if a previous
/// attempt posted the comment but crashed before recording it, the daemon can still discover it by
/// scanning for the idempotency-key marker rather than posting a duplicate.
/// </para>
/// </summary>
internal interface IReviewCommentReader
{
    /// <summary>Provider namespace this publisher serves, e.g. <c>github</c>.</summary>
    string Provider { get; }

    /// <summary>
    /// Lists all provider comments needed to freeze an exact discussion window.
    /// </summary>
    Task<IReadOnlyList<ExistingReviewComment>> ListExistingReviewCommentsAsync(
        ReviewCommentTarget target,
        CancellationToken cancellationToken
    );
}

/// <summary>Adds scoped publication operations to the provider comment reader.</summary>
internal interface IReviewCommentPublisher : IReviewCommentReader
{
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
    /// recognize it later. Returns the provider's id for the created comment.
    /// </summary>
    Task<PostedComment> PostReviewCommentAsync(
        ReviewCommentTarget target,
        string idempotencyKey,
        string body,
        CancellationToken cancellationToken
    );
}

/// <summary>The exact provider operation requested for a review comment.</summary>
internal enum ReviewCommentKind
{
    Summary,
    Inline,
    Reply,
}

/// <summary>The side of a pull-request diff targeted by an inline comment.</summary>
internal enum ReviewCommentSide
{
    Left,
    Right,
}

/// <summary>
/// Where a review comment is posted. The two-argument form remains a PR-level summary. Inline and reply
/// operations carry only the provider coordinates needed for their exact endpoint.
/// </summary>
internal sealed record ReviewCommentTarget(
    RepoIdentity Repo,
    string PrId,
    ReviewCommentKind Kind = ReviewCommentKind.Summary,
    string? CommitId = null,
    string? Path = null,
    int? Line = null,
    ReviewCommentSide? Side = null,
    string? ProviderThreadId = null,
    string? ReplyToProviderCommentId = null
)
{
    public void ValidateFor(string provider)
    {
        RequirePositiveId(PrId, nameof(PrId));
        if (!Enum.IsDefined(Kind))
        {
            throw new ArgumentOutOfRangeException(nameof(Kind), Kind, "Unknown review comment operation.");
        }

        switch (Kind)
        {
            case ReviewCommentKind.Summary:
                RejectTargetCoordinates();
                break;
            case ReviewCommentKind.Inline:
                if (!IsSafeRelativePath(Path))
                {
                    throw new ArgumentException(
                        "Inline comment path must be a safe slash-delimited relative path.",
                        nameof(Path)
                    );
                }
                if (Line is null or <= 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(Line), Line, "Inline comment line must be positive.");
                }
                if (Side is null || !Enum.IsDefined(Side.Value))
                {
                    throw new ArgumentException("Inline comment side must be Left or Right.", nameof(Side));
                }
                if (ProviderThreadId is not null || ReplyToProviderCommentId is not null)
                {
                    throw new ArgumentException("Inline comments cannot carry reply coordinates.");
                }
                if (string.Equals(provider, "github", StringComparison.Ordinal))
                {
                    RequireCommitId(CommitId);
                }
                else if (CommitId is not null)
                {
                    RequireCommitId(CommitId);
                }
                break;
            case ReviewCommentKind.Reply:
                RequirePositiveId(ProviderThreadId, nameof(ProviderThreadId));
                RequirePositiveId(ReplyToProviderCommentId, nameof(ReplyToProviderCommentId));
                if (CommitId is not null || Path is not null || Line is not null || Side is not null)
                {
                    throw new ArgumentException("Replies cannot carry inline coordinates.");
                }
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(Kind), Kind, "Unknown review comment operation.");
        }
    }

    private void RejectTargetCoordinates()
    {
        if (
            CommitId is not null
            || Path is not null
            || Line is not null
            || Side is not null
            || ProviderThreadId is not null
            || ReplyToProviderCommentId is not null
        )
        {
            throw new ArgumentException("Summary comments cannot carry inline or reply coordinates.");
        }
    }

    private static bool IsSafeRelativePath(string? path) =>
        !string.IsNullOrWhiteSpace(path)
        && !path.StartsWith('/')
        && !path.Contains('\\')
        && !path.Contains(':')
        && !path.Any(char.IsControl)
        && path.Split('/').All(segment => segment.Length > 0 && segment is not "." and not "..");

    private static void RequireCommitId(string? commitId)
    {
        if (commitId is null || commitId.Length is < 7 or > 64 || !commitId.All(char.IsAsciiHexDigit))
        {
            throw new ArgumentException("Commit id must contain 7 to 64 hexadecimal characters.", nameof(CommitId));
        }
    }

    private static void RequirePositiveId(string? value, string parameterName)
    {
        if (
            value is null
            || !long.TryParse(
                value,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var parsed
            )
            || parsed <= 0
        )
        {
            throw new ArgumentException("Provider id must be a positive decimal integer.", parameterName);
        }
    }
}

/// <summary>A comment that exists on the provider, identified by the provider's own id.</summary>
internal sealed record PostedComment(string ProviderResponseId, string? ProviderCommentId = null);

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
    string? ThreadId = null,
    string? ProviderCommentId = null,
    string? ProviderVersion = null
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
