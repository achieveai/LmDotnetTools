using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmAgentInfra.Auth;
using CodeReviewDaemon.Sample.Workspace;

namespace CodeReviewDaemon.Sample.Orchestration;

/// <summary>
/// Real <see cref="IReviewCommentPublisher"/> over the GitHub issue-comments API (a PR is an issue for
/// commenting): <c>POST /repos/{owner}/{repo}/issues/{pr}/comments</c> to post and
/// <c>GET …/comments?per_page=100</c> to scan. Every posted body carries the hidden
/// <see cref="IdempotencyMarker"/>, so <see cref="FindPostedCommentAsync"/> can recognize a comment a
/// crashed prior attempt already posted and avoid a duplicate (the §11 exactly-once backstop). Posting
/// is gated upstream by <c>EnableCommentPosting</c>; this type only performs the call when asked.
/// </summary>
internal sealed class GitHubReviewCommentPublisher : IReviewCommentPublisher
{
    private const string BaseUrl = "https://api.github.com";
    private const string UserAgent = "LmDotnetTools-CodeReviewDaemon";

    /// <summary>Items requested per page on every listing call — GitHub's maximum.</summary>
    private const int PageSize = 100;

    /// <summary>
    /// Cap on pages fetched by any one listing (<see cref="PageSize"/> per page). Every listing here is
    /// paginated: a single page silently truncates the discussion the reviewer dedups against, which makes
    /// the daemon repost a finding it already posted or miss a question addressed to it.
    /// <para>
    /// A cap is only safe if it drops the OLDEST items, so every listing here is walked newest-first — by
    /// query where GitHub honours <c>sort</c>/<c>direction</c>, and by following the <c>Link</c> header to
    /// the last page and walking backwards where it does not. Whether an endpoint honours those parameters
    /// is not a detail: reading the cap the wrong way round keeps a window of ancient comments and hides the
    /// bot's own most recent post, which is the one that decides whether to post again.
    /// </para>
    /// </summary>
    private const int MaxListPages = 5;

    private readonly HttpClient _httpClient;
    private readonly IOAuthTokenProvider _tokenProvider;
    private readonly ILogger<GitHubReviewCommentPublisher> _logger;

    public GitHubReviewCommentPublisher(
        HttpClient httpClient,
        IOAuthTokenProvider tokenProvider,
        ILogger<GitHubReviewCommentPublisher> logger
    )
    {
        _httpClient = httpClient;
        _tokenProvider = tokenProvider;
        _logger = logger;
    }

    public string Provider => "github";

    public async Task<PostedComment?> FindPostedCommentAsync(
        ReviewCommentTarget target,
        string idempotencyKey,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(target);

        // Walked from the LAST page backwards: this scan is the exactly-once backstop, so it must be able to
        // see the comment a crashed prior attempt posted. That comment is the NEWEST one, and this endpoint
        // returns oldest-first with no way to ask otherwise, so a forward walk finds it only after paging
        // through the entire conversation — and under the page cap, never. It would then report "not posted"
        // for a comment that exists and the daemon would post a duplicate.
        await foreach (var comment in EnumerateNewestFirstAsync(CommentsUrl(target), cancellationToken))
        {
            var body = GetString(comment, "body");
            if (IdempotencyMarker.Matches(body, idempotencyKey))
            {
                var id = RequiredId(comment, "id");
                return new PostedComment(
                    IssueCommentReceiptId(id),
                    CommentId: id,
                    Permalink: GetString(comment, "html_url")
                );
            }
        }

        // Rich inline effects live on a separate native surface. Scan it after the established flat backstop so
        // legacy fakes/hosts that expose only issue comments keep their existing single-endpoint behavior.
        await foreach (
            var comment in EnumeratePagedAsync(
                $"{PullsUrl(target)}/comments?sort=created&direction=desc",
                cancellationToken
            )
        )
        {
            if (IdempotencyMarker.Matches(GetString(comment, "body"), idempotencyKey))
            {
                return InlineReceipt(comment);
            }
        }

        return null;
    }

    public async Task<PostedComment> PostReviewCommentAsync(
        ReviewCommentTarget target,
        string idempotencyKey,
        string body,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(target);

        using var request = await BuildRequestAsync(
            HttpMethod.Post,
            CommentsUrl(target),
            SandboxOperation.PostReviewComment,
            cancellationToken
        );
        request.Content = JsonContent.Create(new { body = IdempotencyMarker.Embed(body, idempotencyKey) });

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = document.RootElement;
        var id = RequiredId(root, "id");
        _logger.LogInformation("Posted GitHub review comment {CommentId} on PR {PrId}.", id, target.PrId);
        return new PostedComment(IssueCommentReceiptId(id), CommentId: id, Permalink: GetString(root, "html_url"));
    }

    public async Task<PostedComment> SubmitInlineReviewAsync(
        ReviewCommentTarget target,
        string idempotencyKey,
        GitHubInlineReviewRequest review,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(review);
        ArgumentException.ThrowIfNullOrWhiteSpace(review.CommitId);
        ArgumentNullException.ThrowIfNull(review.Comments);
        if (review.Comments.Count == 0)
        {
            throw new ArgumentException(
                "An atomic GitHub review requires at least one inline comment.",
                nameof(review)
            );
        }

        foreach (var comment in review.Comments)
        {
            ValidateSpan(comment.Span, allowOffsets: false);
            ArgumentException.ThrowIfNullOrWhiteSpace(comment.Body);
        }

        using var request = await BuildRequestAsync(
            HttpMethod.Post,
            $"{PullsUrl(target)}/reviews",
            SandboxOperation.PostReviewComment,
            cancellationToken
        );
        request.Content = JsonContent.Create(
            new
            {
                commit_id = review.CommitId,
                @event = "COMMENT",
                comments = review.Comments.Select(comment => GitHubInlinePayload(comment, idempotencyKey)).ToArray(),
            }
        );
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = await ParseAsync(response, cancellationToken);
        var root = document.RootElement;
        var reviewId = RequiredId(root, "id");
        var firstComment =
            root.TryGetProperty("comments", out var comments)
            && comments.ValueKind == JsonValueKind.Array
            && comments.GetArrayLength() > 0
                ? comments[0]
                : default;
        var commentId = firstComment.ValueKind == JsonValueKind.Object ? OptionalId(firstComment, "id") : null;
        return new PostedComment(
            $"review:{reviewId}",
            ReviewId: reviewId,
            ThreadId: commentId,
            CommentId: commentId,
            Permalink: GetString(root, "html_url"),
            Status: GetString(root, "state")
        );
    }

    public async Task<PostedComment> ReplyToInlineReviewCommentAsync(
        ReviewCommentTarget target,
        string idempotencyKey,
        GitHubInlineReplyRequest reply,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(reply);
        var topLevelId = RequireNumericId(reply.TopLevelCommentId, nameof(reply.TopLevelCommentId));
        _ = RequireNumericId(reply.TargetCommentId, nameof(reply.TargetCommentId));
        ArgumentException.ThrowIfNullOrWhiteSpace(reply.Body);

        using var request = await BuildRequestAsync(
            HttpMethod.Post,
            $"{PullsUrl(target)}/comments/{topLevelId}/replies",
            SandboxOperation.PostReviewComment,
            cancellationToken
        );
        request.Content = JsonContent.Create(new { body = IdempotencyMarker.Embed(reply.Body, idempotencyKey) });
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = await ParseAsync(response, cancellationToken);
        var root = document.RootElement;
        var commentId = RequiredId(root, "id");
        var reviewId = OptionalId(root, "pull_request_review_id");
        return new PostedComment(
            reviewId is null
                ? $"thread:{topLevelId}:comment:{commentId}"
                : $"review:{reviewId}:thread:{topLevelId}:comment:{commentId}",
            ReviewId: reviewId,
            ThreadId: topLevelId,
            CommentId: commentId,
            ParentCommentId: topLevelId,
            Permalink: GetString(root, "html_url")
        );
    }

    public async Task<PostedComment> PostFlatConversationCommentAsync(
        ReviewCommentTarget target,
        string idempotencyKey,
        GitHubFlatConversationComment comment,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(comment);
        ArgumentException.ThrowIfNullOrWhiteSpace(comment.Body);
        var body = string.IsNullOrWhiteSpace(comment.TargetPermalink)
            ? comment.Body
            : $"{comment.Body}\n\nIn reply to: {comment.TargetPermalink}";
        var posted = await PostReviewCommentAsync(target, idempotencyKey, body, cancellationToken);
        return posted with { RelationshipDegraded = true };
    }

    private static object GitHubInlinePayload(GitHubInlineReviewComment comment, string idempotencyKey)
    {
        var span = comment.Span;
        return new
        {
            path = span.Path,
            line = span.EndLine,
            side = span.Side,
            start_line = span.StartLine,
            start_side = span.StartLine is null ? null : span.Side,
            body = IdempotencyMarker.Embed(comment.Body, idempotencyKey),
        };
    }

    private static void ValidateSpan(ProviderCommentSpan span, bool allowOffsets)
    {
        ArgumentNullException.ThrowIfNull(span);
        ArgumentException.ThrowIfNullOrWhiteSpace(span.Path);
        if (span.Side is not ("LEFT" or "RIGHT"))
        {
            throw new ArgumentException("Comment side must be LEFT or RIGHT.", nameof(span));
        }

        if (span.EndLine <= 0 || span.StartLine is <= 0 || span.StartLine > span.EndLine)
        {
            throw new ArgumentException("Comment lines must form a positive ascending span.", nameof(span));
        }

        if (!allowOffsets && (span.StartOffset is not null || span.EndOffset is not null))
        {
            throw new ArgumentException("GitHub inline comments do not accept ADO offsets.", nameof(span));
        }
    }

    private static PostedComment InlineReceipt(JsonElement comment)
    {
        var commentId = RequiredId(comment, "id");
        var reviewId = OptionalId(comment, "pull_request_review_id");
        var threadId = OptionalId(comment, "in_reply_to_id") ?? commentId;
        return new PostedComment(
            reviewId is null
                ? $"thread:{threadId}:comment:{commentId}"
                : $"review:{reviewId}:thread:{threadId}:comment:{commentId}",
            ReviewId: reviewId,
            ThreadId: threadId,
            CommentId: commentId,
            ParentCommentId: OptionalId(comment, "in_reply_to_id"),
            Permalink: GetString(comment, "html_url")
        );
    }

    private static async Task<JsonDocument> ParseAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken
    )
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    private static string RequiredId(JsonElement element, string name) =>
        OptionalId(element, name) ?? throw new JsonException($"Provider response omitted '{name}'.");

    private static string? OptionalId(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.Number => value.GetRawText(),
                JsonValueKind.String => value.GetString(),
                _ => null,
            }
            : null;

    private static string RequireNumericId(string value, string parameterName) =>
        long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out _)
            ? value
            : throw new ArgumentException("Provider comment IDs must be numeric.", parameterName);

    private static string IssueCommentReceiptId(string id) => $"issue-comment:{id}";

    private static string PullsUrl(ReviewCommentTarget target) =>
        $"{BaseUrl}/repos/{target.Repo.OrgOrOwner}/{target.Repo.RepoName}/pulls/{target.PrId}";

    private static string CommentsUrl(ReviewCommentTarget target) =>
        $"{BaseUrl}/repos/{target.Repo.OrgOrOwner}/{target.Repo.RepoName}/issues/{target.PrId}/comments";

    public async Task<IReadOnlyList<ExistingReviewComment>> ListExistingReviewCommentsAsync(
        ReviewCommentTarget target,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(target);

        var results = new List<ExistingReviewComment>();
        var repoBase = $"{BaseUrl}/repos/{target.Repo.OrgOrOwner}/{target.Repo.RepoName}";
        var pullsBase = PullsUrl(target);

        // Review-level summaries (the top-level "Reviewed PR X…" bodies). Fetched FIRST so we can collect the ids of
        // PENDING/unsubmitted drafts before scanning inline comments below. Skip PENDING drafts: a draft is not
        // posted discussion, and treating its body as such lets a stale draft from a failed posting run suppress the
        // valid submitted replacement on the next review. Walked newest-first (see EnumerateNewestFirstAsync — this
        // endpoint takes no sort/direction): missing a recent draft would let its inline comments through and
        // suppress the submitted review they belong to, and the recent drafts are the ones that can still do that.
        var pendingReviewIds = new HashSet<long>();
        await foreach (var review in EnumerateNewestFirstAsync($"{pullsBase}/reviews", cancellationToken))
        {
            if (IsPendingReview(review))
            {
                if (LongOf(review, "id") is { } pendingId)
                {
                    pendingReviewIds.Add(pendingId);
                }

                continue;
            }

            var body = GetString(review, "body");
            if (!string.IsNullOrWhiteSpace(body))
            {
                results.Add(
                    new ExistingReviewComment(
                        null,
                        null,
                        Trim(body),
                        AuthorOf(review),
                        IsActive: true,
                        PublishedAt: TimeOf(review, "submitted_at")
                    )
                );
            }
        }

        // Inline review comments — the actual per-line findings. This is the one listing here that GitHub really
        // does order on request (verified against the live API: sending sort/direction flips the ids, whereas the
        // issue-comment and review endpoints return byte-identical ascending output either way), so it can take
        // the cheap forward walk. Newest-first so that once a PR exceeds the page cap we keep the most RECENT
        // findings/replies (which drive dedup + reply handling) rather than the oldest. A comment whose
        // pull_request_review_id belongs to a PENDING draft (above) is skipped — GitHub still returns the draft's
        // per-line comments to the authenticated author, and letting one seed dedup would suppress its valid
        // submitted replacement.
        await foreach (
            var comment in EnumeratePagedAsync($"{pullsBase}/comments?sort=created&direction=desc", cancellationToken)
        )
        {
            var body = GetString(comment, "body");
            if (string.IsNullOrWhiteSpace(body))
            {
                continue;
            }

            if (LongOf(comment, "pull_request_review_id") is { } reviewId && pendingReviewIds.Contains(reviewId))
            {
                continue; // belongs to an unsubmitted draft review
            }

            results.Add(
                new ExistingReviewComment(
                    GetString(comment, "path"),
                    LineOf(comment),
                    Trim(body),
                    AuthorOf(comment),
                    IsActive: true,
                    PublishedAt: TimeOf(comment, "created_at"),
                    ThreadId: ThreadIdOf(comment)
                )
            );
        }

        // Ordinary PR-conversation (issue) comments — this publisher posts its summaries via /issues/{pr}/comments,
        // so prior summaries AND the human PR-conversation (questions directed at the bot) live here, not on the
        // review-comment endpoints; fold them into the model so dedup/reply handling can see them. PR-level (no
        // path/line). Walked newest-first (see EnumerateNewestFirstAsync — this endpoint ignores sort/direction):
        // this is the listing that carries the bot's own prior summaries and any question addressed to it, so
        // keeping the OLDEST window is what makes the daemon repost a resolved finding or leave a question
        // unanswered — precisely the discussion still under argument is what a forward walk drops.
        await foreach (
            var comment in EnumerateNewestFirstAsync($"{repoBase}/issues/{target.PrId}/comments", cancellationToken)
        )
        {
            var body = GetString(comment, "body");
            if (!string.IsNullOrWhiteSpace(body))
            {
                results.Add(
                    new ExistingReviewComment(
                        null,
                        null,
                        Trim(body),
                        AuthorOf(comment),
                        IsActive: true,
                        PublishedAt: TimeOf(comment, "created_at"),
                        ThreadId: ThreadIdOf(comment)
                    )
                );
            }
        }

        return results;
    }

    /// <summary>True when a review is an unsubmitted PENDING draft (GitHub reports <c>state == "PENDING"</c> and
    /// no <c>submitted_at</c>). A draft is not posted discussion, so it must never seed dedup — otherwise a stale
    /// draft left by a failed posting run could suppress the valid submitted review on the next pass.</summary>
    private static bool IsPendingReview(JsonElement review) =>
        string.Equals(GetString(review, "state"), "PENDING", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Walks a GitHub listing forwards from page 1, yielding every item, and stops at the first short page
    /// (GitHub returns fewer than <see cref="PageSize"/> only on the last one) or at <see cref="MaxListPages"/>.
    /// <para>
    /// The single-request alternative is not a smaller version of this — it is a wrong one. Every caller
    /// here feeds either dedup or the exactly-once posting backstop, and both are decided by <em>absence</em>:
    /// a comment that was not seen is treated as never posted. A listing truncated at one page therefore does
    /// not degrade gracefully, it reports the opposite of the truth, and the daemon reposts a finding or
    /// answers a question it has already answered.
    /// </para>
    /// <para>
    /// The page cap has that same failure mode, so it must drop the OLDEST items — which makes this walk
    /// correct only where the caller can order the listing newest-first in the query. That holds for
    /// <c>GET /pulls/{pr}/comments</c> and nowhere else in this file; the endpoints that ignore
    /// <c>sort</c>/<c>direction</c> use <see cref="EnumerateNewestFirstAsync"/> instead.
    /// </para>
    /// <paramref name="url"/> carries the caller's own query (ordering); paging parameters are appended here.
    /// </summary>
    private async IAsyncEnumerable<JsonElement> EnumeratePagedAsync(
        string url,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        for (var page = 1; page <= MaxListPages; page++)
        {
            using var pageResult = await FetchPageAsync(url, page, cancellationToken);
            var count = 0;
            foreach (var element in pageResult.Document.RootElement.EnumerateArray())
            {
                count++;
                yield return element;
            }

            if (count < PageSize)
            {
                yield break; // a short page is the last page
            }
        }
    }

    /// <summary>
    /// Walks a GitHub listing BACKWARDS from its last page, yielding items newest-first, for at most
    /// <see cref="MaxListPages"/> pages.
    /// <para>
    /// This exists because most of the listings here cannot be ordered by the caller.
    /// <c>GET /issues/{n}/comments</c> and <c>GET /pulls/{n}/reviews</c> return ascending creation order and
    /// silently ignore <c>sort</c>/<c>direction</c> — sending those parameters yields byte-identical ascending
    /// output, so a forward walk under a page cap keeps the oldest window and discards exactly the recent
    /// discussion that dedup and the posting backstop are deciding about. Since every caller decides by
    /// absence, that does not lose detail, it inverts the answer: the newest comment is the bot's own last
    /// post, and not seeing it is what makes the daemon post a duplicate.
    /// </para>
    /// <para>
    /// The tail is located from the <c>Link</c> header's <c>rel="last"</c> rather than by walking forward to
    /// find it, so the cost stays bounded at <c>1 + MaxListPages</c> requests no matter how long the thread is.
    /// Each page's items are reversed on the way out, so the sequence is newest-first across page boundaries
    /// and not merely page-wise.
    /// </para>
    /// </summary>
    private async IAsyncEnumerable<JsonElement> EnumerateNewestFirstAsync(
        string url,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        using var first = await FetchPageAsync(url, 1, cancellationToken);
        var lastPage = first.LastPage;

        if (lastPage <= 1)
        {
            foreach (var element in Reversed(first.Document))
            {
                yield return element;
            }

            yield break;
        }

        // Page 1 is the OLDEST page, so it is the one the cap should drop; it is re-fetched below only if it
        // falls inside the newest MaxListPages window.
        var stopPage = Math.Max(1, lastPage - MaxListPages + 1);
        for (var page = lastPage; page >= stopPage; page--)
        {
            using var current = await FetchPageAsync(url, page, cancellationToken);
            foreach (var element in Reversed(current.Document))
            {
                yield return element;
            }
        }
    }

    private static IEnumerable<JsonElement> Reversed(JsonDocument document) =>
        document.RootElement.EnumerateArray().Reverse();

    /// <summary>One page of a listing, plus the last page number GitHub advertised for it.</summary>
    private sealed record ListPage(JsonDocument Document, int LastPage) : IDisposable
    {
        public void Dispose() => Document.Dispose();
    }

    private async Task<ListPage> FetchPageAsync(string url, int page, CancellationToken cancellationToken)
    {
        var separator = url.Contains('?', StringComparison.Ordinal) ? '&' : '?';
        var pagedUrl = $"{url}{separator}per_page={PageSize}&page={page}";

        using var request = await BuildRequestAsync(
            HttpMethod.Get,
            pagedUrl,
            SandboxOperation.ReadProviderMetadata,
            cancellationToken
        );
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var lastPage = LastPageOf(response);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        // The document is parsed (not streamed) so it outlives the response scope the caller has already left.
        var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return new ListPage(document, lastPage);
    }

    /// <summary>
    /// Reads the last page number from GitHub's <c>Link</c> header, or 1 when the header carries no
    /// <c>rel="last"</c> — which is what GitHub sends when the listing fits on a single page.
    /// </summary>
    private static int LastPageOf(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Link", out var values))
        {
            return 1;
        }

        foreach (var segment in string.Join(',', values).Split(','))
        {
            if (!segment.Contains("rel=\"last\"", StringComparison.Ordinal))
            {
                continue;
            }

            // Anchored on the parameter separator: a bare "page=" also matches inside "per_page=100", which
            // would read the page SIZE as the last page number and truncate the walk to one page.
            var marker = segment.IndexOf("&page=", StringComparison.Ordinal);
            var offset = marker >= 0 ? marker + "&page=".Length : -1;
            if (offset < 0)
            {
                marker = segment.IndexOf("?page=", StringComparison.Ordinal);
                offset = marker >= 0 ? marker + "?page=".Length : -1;
            }

            if (offset < 0)
            {
                continue;
            }

            var end = offset;
            while (end < segment.Length && char.IsAsciiDigit(segment[end]))
            {
                end++;
            }

            if (
                end > offset
                && int.TryParse(segment.AsSpan(offset, end - offset), CultureInfo.InvariantCulture, out var parsed)
                && parsed >= 1
            )
            {
                return parsed;
            }
        }

        return 1;
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.String ? v.GetString() : null;

    /// <summary>Reads a numeric field (e.g. a review's <c>id</c> or a comment's <c>pull_request_review_id</c>) as a
    /// long, so inline comments can be correlated back to the PENDING draft review they belong to.</summary>
    private static long? LongOf(JsonElement element, string name) =>
        element.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.Number && v.TryGetInt64(out var n)
            ? n
            : null;

    private static string? LineOf(JsonElement comment) =>
        comment.TryGetProperty("line", out var l) && l.ValueKind is JsonValueKind.Number
            ? l.GetInt32().ToString(CultureInfo.InvariantCulture)
        : comment.TryGetProperty("original_line", out var ol) && ol.ValueKind is JsonValueKind.Number
            ? ol.GetInt32().ToString(CultureInfo.InvariantCulture)
        : null;

    private static string? AuthorOf(JsonElement element) =>
        element.TryGetProperty("user", out var u) && u.ValueKind is JsonValueKind.Object ? GetString(u, "login") : null;

    /// <summary>Reads an ISO-8601 timestamp field (e.g. <c>created_at</c>/<c>submitted_at</c>) — orders past vs. new.</summary>
    private static DateTimeOffset? TimeOf(JsonElement element, string name) =>
        element.TryGetProperty(name, out var v)
        && v.ValueKind is JsonValueKind.String
        && DateTimeOffset.TryParse(
            v.GetString(),
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out var dt
        )
            ? dt
            : null;

    /// <summary>
    /// The thread a review comment belongs to: its reply-root (<c>in_reply_to_id</c>) if it is a reply,
    /// otherwise its own <c>id</c> — so a finding and its replies group under one conversation.
    /// </summary>
    private static string? ThreadIdOf(JsonElement comment)
    {
        if (comment.TryGetProperty("in_reply_to_id", out var r) && r.ValueKind is JsonValueKind.Number)
        {
            return r.GetInt64().ToString(CultureInfo.InvariantCulture);
        }

        return comment.TryGetProperty("id", out var id) && id.ValueKind is JsonValueKind.Number
            ? id.GetInt64().ToString(CultureInfo.InvariantCulture)
            : null;
    }

    /// <summary>
    /// Per-comment cap, shared with the ADO publisher via <see cref="ExistingCommentBody"/> so the two cannot
    /// drift into showing the reviewer different amounts of the same conversation (#225).
    /// </summary>
    private static string Trim(string body) => ExistingCommentBody.Summarize(body);

    private async Task<HttpRequestMessage> BuildRequestAsync(
        HttpMethod method,
        string url,
        SandboxOperation operation,
        CancellationToken cancellationToken
    )
    {
        var request = new HttpRequestMessage(method, url).WithOperation(operation);
        var token = await _tokenProvider.GetAccessTokenAsync(ct: cancellationToken);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Value);
        request.Headers.UserAgent.ParseAdd(UserAgent);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        return request;
    }
}
