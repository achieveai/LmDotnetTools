using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmTestUtils.Logging;
using CodeReviewDaemon.Sample.Orchestration;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace CodeReviewDaemon.Sample.Tests.Scenarios;

/// <summary>
/// P4.4 — the real <see cref="GitHubReviewCommentPublisher"/> posts and scans PR comments via the GitHub
/// issues-comments API. These tests pin: the post embeds the hidden idempotency marker (so the backstop
/// scan can find it), the scan recognizes a previously-posted comment by that marker, and the request
/// shape (bearer auth, the comments endpoint).
/// </summary>
public sealed class GitHubReviewCommentPublisherTests : LoggingTestBase
{
    private const string Key = "v1:github:acme::R_node_123:7:post-review-comment:review:summary:wm-1:primary";

    private static readonly ReviewCommentTarget Target = new(
        new RepoIdentity
        {
            Provider = "github",
            OrgOrOwner = "acme",
            RepoName = "widgets",
            RepoStableId = "R_node_123",
        },
        "7");

    public GitHubReviewCommentPublisherTests(ITestOutputHelper output)
        : base(output)
    {
    }

    private GitHubReviewCommentPublisher Publisher(FakeHttpMessageHandler handler) =>
        new(
            new HttpClient(handler),
            new FakeOAuthTokenProvider("github", "gh-token-xyz"),
            LoggerFactory.CreateLogger<GitHubReviewCommentPublisher>());

    [Fact]
    public void Provider_id_is_github()
    {
        Publisher(new FakeHttpMessageHandler()).Provider.Should().Be("github");
    }

    [Fact]
    public async Task PostReviewComment_posts_to_the_comments_endpoint_embedding_the_marker()
    {
        var handler = new FakeHttpMessageHandler().OnJson(
            HttpMethod.Post, "/issues/7/comments", """{"id":555}""", HttpStatusCode.Created);

        var posted = await Publisher(handler).PostReviewCommentAsync(Target, Key, "## Review\nLGTM", CancellationToken.None);

        posted.ProviderResponseId.Should().Be("555");
        var request = handler.Requests.Should().ContainSingle().Subject;
        request.Method.Should().Be(HttpMethod.Post);
        request.Uri.ToString().Should().Be("https://api.github.com/repos/acme/widgets/issues/7/comments");
        request.Authorization.Should().Be("Bearer gh-token-xyz");

        var body = JsonDocument.Parse(request.Body!).RootElement.GetProperty("body").GetString();
        body.Should().Contain("## Review\nLGTM");
        body.Should().Contain($"<!-- idempotency-key:{Key} -->", "the marker makes the post discoverable on replay");
    }

    [Fact]
    public async Task FindPostedComment_returns_the_comment_carrying_the_marker()
    {
        var listJson = JsonSerializer.Serialize(new[]
        {
            new { id = 100, body = "unrelated comment" },
            new { id = 200, body = $"## Review\nLGTM\n\n<!-- idempotency-key:{Key} -->" },
        });
        var handler = new FakeHttpMessageHandler().OnJson(HttpMethod.Get, "/issues/7/comments", listJson);

        var found = await Publisher(handler).FindPostedCommentAsync(Target, Key, CancellationToken.None);

        found.Should().NotBeNull();
        found!.ProviderResponseId.Should().Be("200");
    }

    [Fact]
    public async Task FindPostedComment_returns_null_when_no_comment_carries_the_marker()
    {
        var listJson = JsonSerializer.Serialize(new[] { new { id = 100, body = "nothing here" } });
        var handler = new FakeHttpMessageHandler().OnJson(HttpMethod.Get, "/issues/7/comments", listJson);

        var found = await Publisher(handler).FindPostedCommentAsync(Target, Key, CancellationToken.None);

        found.Should().BeNull();
    }

    [Fact]
    public async Task PostReviewComment_throws_on_a_non_success_status()
    {
        var handler = new FakeHttpMessageHandler().OnJson(
            HttpMethod.Post, "/issues/7/comments", """{"message":"forbidden"}""", HttpStatusCode.Forbidden);

        var act = () => Publisher(handler).PostReviewCommentAsync(Target, Key, "body", CancellationToken.None);

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task PostReviewComment_never_uses_the_empty_review_spawning_review_comment_endpoints()
    {
        // API-level contract (regression, live #224): GitHub wraps EVERY write to a review-comment endpoint —
        // standalone POST /pulls/{pr}/comments AND POST /pulls/{pr}/comments/{id}/replies — in its own submitted,
        // empty-bodied COMMENTED review (six #224 replies produced six empty reviews). The host publisher must
        // therefore post ONLY through the wrapper-free issue-comments endpoint. Prompt-text assertions cannot
        // catch a regression here; this pins the actual HTTP the publisher emits.
        var handler = new FakeHttpMessageHandler().OnJson(
            HttpMethod.Post, "/issues/7/comments", """{"id":777}""", HttpStatusCode.Created);

        await Publisher(handler).PostReviewCommentAsync(Target, Key, "## Review\nfinding", CancellationToken.None);

        var post = handler.Requests.Should().ContainSingle(r => r.Method == HttpMethod.Post).Subject;
        post.Uri.ToString().Should().Be(
            "https://api.github.com/repos/acme/widgets/issues/7/comments",
            "the only wrapper-free write is the issue-comments endpoint");
        handler.Requests.Should().NotContain(
            r => r.Uri.ToString().Contains("/replies", StringComparison.Ordinal),
            "POST .../comments/{id}/replies wraps each reply in its own empty review — the #224 spam");
        handler.Requests.Should().NotContain(
            r => r.Uri.ToString().Contains("/pulls/", StringComparison.Ordinal)
                && r.Uri.ToString().Contains("/comments", StringComparison.Ordinal),
            "standalone POST /pulls/{pr}/comments also wraps each write in an empty review");
    }

    [Fact]
    public async Task ListExisting_returns_inline_findings_and_review_summaries()
    {
        var comments = JsonSerializer.Serialize(new object[]
        {
            new { path = "src/Foo.cs", line = 42, body = "Must — null deref here", user = new { login = "revobot" } },
            new { path = "src/Bar.cs", original_line = 7, body = "Should — extract this", user = new { login = "alice" } },
            new { path = "src/Baz.cs", line = 3, body = "   ", user = new { login = "revobot" } }, // blank → skipped
        });
        var reviews = JsonSerializer.Serialize(new object[]
        {
            new { body = "Reviewed PR 7 — 1 Must, 1 Should", user = new { login = "revobot" }, state = "COMMENTED" },
            new { body = "", user = new { login = "revobot" }, state = "COMMENTED" }, // empty body → skipped
        });
        var handler = new FakeHttpMessageHandler()
            .OnJson(HttpMethod.Get, "/pulls/7/comments", comments)
            .OnJson(HttpMethod.Get, "/pulls/7/reviews", reviews)
            .OnJson(HttpMethod.Get, "/issues/7/comments", "[]");

        var existing = await Publisher(handler).ListExistingReviewCommentsAsync(Target, CancellationToken.None);

        existing.Should().HaveCount(3, "two non-blank inline comments + one non-empty review summary");
        existing.Should().ContainSingle(e =>
            e.Path == "src/Foo.cs" && e.Line == "42" && e.Body.Contains("null deref") && e.Author == "revobot");
        existing.Should().ContainSingle(e =>
            e.Path == "src/Bar.cs" && e.Line == "7" && e.Author == "alice"); // original_line fallback when line is absent
        existing.Should().ContainSingle(e => e.Path == null && e.Body.Contains("Reviewed PR 7"));
    }

    [Fact]
    public async Task ListExisting_excludes_pending_draft_reviews()
    {
        // GitHub's reviews list includes PENDING (unsubmitted) drafts. Treating a draft body as posted discussion
        // lets a stale draft from a failed posting run suppress the valid submitted replacement — so it is skipped.
        var reviews = JsonSerializer.Serialize(new object[]
        {
            new { body = "Reviewed PR 7 — submitted", user = new { login = "revobot" }, state = "COMMENTED", submitted_at = "2026-07-20T10:00:00Z" },
            new { body = "draft in progress — do not dedup", user = new { login = "revobot" }, state = "PENDING" },
        });
        var handler = new FakeHttpMessageHandler()
            .OnJson(HttpMethod.Get, "/pulls/7/comments", "[]")
            .OnJson(HttpMethod.Get, "/pulls/7/reviews", reviews)
            .OnJson(HttpMethod.Get, "/issues/7/comments", "[]");

        var existing = await Publisher(handler).ListExistingReviewCommentsAsync(Target, CancellationToken.None);

        existing.Should().ContainSingle(e => e.Body.Contains("submitted"));
        existing.Should().NotContain(
            e => e.Body.Contains("draft in progress"), "a PENDING/unsubmitted draft must not seed dedup");
    }

    [Fact]
    public async Task ListExisting_folds_in_pr_conversation_issue_comments()
    {
        // The publisher posts its SUMMARY via /issues/{pr}/comments, and humans ask questions there; those must
        // reach dedup/reply handling, so the scan now merges that endpoint (PR-level, no path/line).
        var issueComments = JsonSerializer.Serialize(new object[]
        {
            new { id = 900, body = "## Re-Review Summary: PR #7", user = new { login = "revobot" }, created_at = "2026-07-20T10:00:00Z" },
            new { id = 901, body = "@revobot is this still needed?", user = new { login = "alice" }, created_at = "2026-07-21T09:00:00Z" },
        });
        var handler = new FakeHttpMessageHandler()
            .OnJson(HttpMethod.Get, "/pulls/7/comments", "[]")
            .OnJson(HttpMethod.Get, "/pulls/7/reviews", "[]")
            .OnJson(HttpMethod.Get, "/issues/7/comments", issueComments);

        var existing = await Publisher(handler).ListExistingReviewCommentsAsync(Target, CancellationToken.None);

        existing.Should().ContainSingle(e => e.Path == null && e.Body.Contains("Re-Review Summary") && e.Author == "revobot");
        existing.Should().ContainSingle(e => e.Path == null && e.Body.Contains("still needed") && e.Author == "alice");
    }

    [Fact]
    public async Task ListExisting_excludes_inline_comments_belonging_to_a_pending_review()
    {
        // A failed posting run can leave a PENDING (draft) review whose inline comments are still returned to the
        // authenticated author by GET /pulls/{pr}/comments. Excluding only the draft's summary body is not enough —
        // its per-line drafts must also be dropped, else a stale draft finding suppresses its valid submitted
        // replacement. Correlate pull_request_review_id against the PENDING review ids and drop matching inline
        // comments.
        var reviews = JsonSerializer.Serialize(new object[]
        {
            new { id = 5001, body = "", user = new { login = "revobot" }, state = "PENDING" },
            new { id = 5002, body = "submitted review", user = new { login = "revobot" }, state = "COMMENTED", submitted_at = "2026-07-20T10:00:00Z" },
        });
        var comments = JsonSerializer.Serialize(new object[]
        {
            new { body = "DRAFT-inline-finding", path = "src/Foo.cs", line = 3, pull_request_review_id = 5001, user = new { login = "revobot" }, created_at = "2026-07-20T09:00:00Z" },
            new { body = "SUBMITTED-inline-finding", path = "src/Foo.cs", line = 9, pull_request_review_id = 5002, user = new { login = "revobot" }, created_at = "2026-07-20T10:00:00Z" },
        });
        var handler = new FakeHttpMessageHandler()
            .OnJson(HttpMethod.Get, "/pulls/7/comments", comments)
            .OnJson(HttpMethod.Get, "/pulls/7/reviews", reviews)
            .OnJson(HttpMethod.Get, "/issues/7/comments", "[]");

        var existing = await Publisher(handler).ListExistingReviewCommentsAsync(Target, CancellationToken.None);

        existing.Should().ContainSingle(e => e.Body.Contains("SUBMITTED-inline-finding"));
        existing.Should().NotContain(
            e => e.Body.Contains("DRAFT-inline-finding"),
            "inline comments belonging to a PENDING draft review must not seed dedup");
    }

    [Fact]
    public async Task ListExisting_requests_inline_comments_newest_first()
    {
        var handler = new FakeHttpMessageHandler()
            .OnJson(HttpMethod.Get, "/pulls/7/comments", "[]")
            .OnJson(HttpMethod.Get, "/pulls/7/reviews", "[]")
            .OnJson(HttpMethod.Get, "/issues/7/comments", "[]");

        await Publisher(handler).ListExistingReviewCommentsAsync(Target, CancellationToken.None);

        handler.Requests.Should().Contain(
            r => r.Uri.ToString().Contains("/pulls/7/comments") && r.Uri.ToString().Contains("direction=desc"),
            "inline comments must be fetched newest-first so the page cap keeps recent findings, not the oldest");
    }

    [Fact]
    public async Task ListExisting_returns_empty_for_a_pr_with_no_comments()
    {
        var handler = new FakeHttpMessageHandler()
            .OnJson(HttpMethod.Get, "/pulls/7/comments", "[]")
            .OnJson(HttpMethod.Get, "/pulls/7/reviews", "[]")
            .OnJson(HttpMethod.Get, "/issues/7/comments", "[]");

        var existing = await Publisher(handler).ListExistingReviewCommentsAsync(Target, CancellationToken.None);

        existing.Should().BeEmpty();
    }

    // ---- Pagination -------------------------------------------------------------------------------------
    // Every listing below feeds either dedup or the exactly-once posting backstop, and both are decided by
    // ABSENCE: an item that was not seen is treated as never posted. Truncating one therefore does not
    // degrade, it inverts.
    //
    // The fake below serves these listings the way GitHub really does — ASCENDING creation order, sort and
    // direction IGNORED, tail advertised only through the Link header. That is the whole point: a test whose
    // fake honours sort=direction, or hands page 2 the item page 1 lacks, agrees with whatever the code asks
    // for and can never catch the code asking for the wrong thing. Here the item that matters sits at the
    // TAIL, so a forward walk under the page cap provably cannot reach it.

    private const int PageSize = 100;

    /// <summary>Pages past the cap, so pages 1-2 lie outside the newest-<c>MaxListPages</c> window.</summary>
    private const int PagesBeyondTheCap = 7;

    /// <summary>Ascending creation times — position in a GitHub listing and age agree.</summary>
    private static string Timestamp(int index) =>
        new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            .AddMinutes(index)
            .ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    private static object Comment(int id, string body, int age) =>
        new { id, body, user = new { login = "alice" }, created_at = Timestamp(age) };

    /// <summary>Filler ahead of the item under test, oldest first, exactly as GitHub returns it.</summary>
    private static List<object> Filler(int count, string bodyPrefix, int startId) =>
        [.. Enumerable.Range(0, count).Select(i => Comment(startId + i, $"{bodyPrefix}-{i}", i))];

    /// <summary>Filler occupying every page but the last, so the item appended after it lands alone on the tail.</summary>
    private static List<object> FillerToTheTail(string bodyPrefix, int startId) =>
        Filler(PageSize * (PagesBeyondTheCap - 1), bodyPrefix, startId);

    /// <summary>
    /// Routes a listing URL the way GitHub serves <c>/issues/{n}/comments</c> and <c>/pulls/{n}/reviews</c>:
    /// ascending creation order, paged by <c>page=</c>, tail reachable only via the <c>Link</c> header — and
    /// <c>sort</c>/<c>direction</c> silently ignored, which is the behaviour these tests exist to pin.
    /// </summary>
    private static FakeHttpMessageHandler OnAscendingListing(
        FakeHttpMessageHandler handler, string pathContains, IReadOnlyList<object> itemsOldestFirst)
    {
        var pageCount = Math.Max(1, (itemsOldestFirst.Count + PageSize - 1) / PageSize);
        return handler.On(
            req => req.Method == HttpMethod.Get
                && req.RequestUri is not null
                && req.RequestUri.ToString().Contains(pathContains, StringComparison.Ordinal),
            req =>
            {
                var page = PageOf(req.RequestUri!);
                var slice = itemsOldestFirst.Skip((page - 1) * PageSize).Take(PageSize).ToArray();
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        JsonSerializer.Serialize(slice), Encoding.UTF8, "application/json"),
                };

                if (pageCount > 1)
                {
                    // per_page precedes page, as GitHub emits it — the shape that punishes reading the last
                    // page out of a bare "page=" search, which would find per_page's value instead.
                    var links = new List<string>
                    {
                        $"<https://api.github.com{pathContains}?per_page={PageSize}&page={pageCount}>; rel=\"last\"",
                    };
                    if (page < pageCount)
                    {
                        links.Insert(
                            0,
                            $"<https://api.github.com{pathContains}?per_page={PageSize}&page={page + 1}>; rel=\"next\"");
                    }

                    response.Headers.TryAddWithoutValidation("Link", string.Join(", ", links));
                }

                return response;
            });
    }

    private static int PageOf(Uri uri)
    {
        foreach (var pair in uri.Query.TrimStart('?').Split('&'))
        {
            if (pair.StartsWith("page=", StringComparison.Ordinal)
                && int.TryParse(pair.AsSpan("page=".Length), CultureInfo.InvariantCulture, out var page))
            {
                return page;
            }
        }

        return 1;
    }

    /// <summary>The <c>page=</c> values requested against one path, in the order they were requested.</summary>
    private static int[] PagesRequested(FakeHttpMessageHandler handler, string pathContains) =>
        [.. handler.Requests
            .Where(r => r.Uri.ToString().Contains(pathContains, StringComparison.Ordinal))
            .Select(r => PageOf(r.Uri))];

    [Fact]
    public async Task ListExisting_reads_a_question_at_the_tail_that_a_forward_walk_cannot_reach()
    {
        // The bot's own prior summaries AND questions addressed to it live on /issues/{pr}/comments, which
        // GitHub returns oldest-first and will not reorder. The newest comment is therefore on the LAST page,
        // and a capped forward walk keeps a window of ancient chatter and never sees the live question.
        var conversation = FillerToTheTail("older-chatter", 8000);
        conversation.Add(Comment(9001, "@revobot is this still needed?", conversation.Count));

        var handler = new FakeHttpMessageHandler();
        OnAscendingListing(handler, "/issues/7/comments", conversation);
        handler
            .OnJson(HttpMethod.Get, "/pulls/7/comments", "[]")
            .OnJson(HttpMethod.Get, "/pulls/7/reviews", "[]");

        var existing = await Publisher(handler).ListExistingReviewCommentsAsync(Target, CancellationToken.None);

        existing.Should().ContainSingle(
            e => e.Body.Contains("still needed"),
            "the newest comment sits on the last page, which is where the reader must start");
    }

    [Fact]
    public async Task ListExisting_reads_a_pending_draft_at_the_tail_of_the_reviews_listing()
    {
        // /pulls/{pr}/reviews is not merely about returning more rows: the ids collected there are what
        // suppress a stale draft's inline comments. A draft left by a failed posting run is the NEWEST review,
        // so it is exactly what a forward walk drops — and its finding then seeds dedup and suppresses the
        // valid submitted replacement, the very failure the PENDING filter exists to prevent.
        List<object> reviews =
        [
            .. Enumerable.Range(0, PageSize * (PagesBeyondTheCap - 1))
                .Select(i => (object)new
                {
                    id = 1000 + i,
                    body = $"older-review-{i}",
                    user = new { login = "alice" },
                    state = "COMMENTED",
                    submitted_at = Timestamp(i),
                }),
            new { id = 5001, body = "", user = new { login = "revobot" }, state = "PENDING" },
        ];

        var handler = new FakeHttpMessageHandler();
        OnAscendingListing(handler, "/pulls/7/reviews", reviews);
        handler
            .OnJson(HttpMethod.Get, "/pulls/7/comments", JsonSerializer.Serialize(new object[]
            {
                new { body = "DRAFT-inline-finding", path = "src/Foo.cs", line = 3, pull_request_review_id = 5001, user = new { login = "revobot" }, created_at = "2026-07-20T09:00:00Z" },
            }))
            .OnJson(HttpMethod.Get, "/issues/7/comments", "[]");

        var existing = await Publisher(handler).ListExistingReviewCommentsAsync(Target, CancellationToken.None);

        existing.Should().NotContain(
            e => e.Body.Contains("DRAFT-inline-finding"),
            "the draft is the newest review, so only a tail-first listing can find its id and suppress it");
    }

    [Fact]
    public async Task FindPostedComment_finds_a_marker_at_the_tail_of_the_conversation()
    {
        // The exactly-once backstop: this scan answers "did a crashed attempt already post this?". The comment
        // it is hunting is by construction the NEWEST one on the PR, so on this ascending endpoint it is the
        // last item on the last page. Answer "no" and the daemon posts a duplicate.
        var conversation = FillerToTheTail("unrelated", 6000);
        conversation.Add(Comment(777, $"## Review\nLGTM\n\n<!-- idempotency-key:{Key} -->", conversation.Count));

        var handler = new FakeHttpMessageHandler();
        OnAscendingListing(handler, "/issues/7/comments", conversation);

        var found = await Publisher(handler).FindPostedCommentAsync(Target, Key, CancellationToken.None);

        found.Should().NotBeNull();
        found!.ProviderResponseId.Should().Be("777");
    }

    [Fact]
    public async Task FindPostedComment_walks_back_from_the_last_page_rather_than_asking_for_an_order()
    {
        // Pins the mechanism, not the URL text. Asking this endpoint for direction=desc looks like a fix and
        // changes nothing — GitHub returns the same ascending page. The only way to reach the tail is to read
        // it out of the Link header and walk backwards, and the request sequence is where that is observable.
        var handler = new FakeHttpMessageHandler();
        OnAscendingListing(handler, "/issues/7/comments", Filler(PageSize * PagesBeyondTheCap, "chatter", 100));

        await Publisher(handler).FindPostedCommentAsync(Target, Key, CancellationToken.None);

        PagesRequested(handler, "/issues/7/comments").Should().Equal(
            [1, 7, 6, 5, 4, 3],
            "page 1 locates the tail via rel=\"last\"; the walk then runs backwards and stops at the cap");
    }

    [Fact]
    public async Task ListExisting_yields_the_conversation_newest_first_across_page_boundaries()
    {
        // Reversing each page in isolation would leave the pages themselves in oldest-first order, so the
        // sequence would only look sorted inside a page. The cap only drops the right items if the whole
        // sequence is newest-first.
        var handler = new FakeHttpMessageHandler();
        OnAscendingListing(handler, "/issues/7/comments", Filler(PageSize + 30, "chatter", 100));
        handler
            .OnJson(HttpMethod.Get, "/pulls/7/comments", "[]")
            .OnJson(HttpMethod.Get, "/pulls/7/reviews", "[]");

        var existing = await Publisher(handler).ListExistingReviewCommentsAsync(Target, CancellationToken.None);

        existing.Should().HaveCount(PageSize + 30);
        existing.Select(e => e.PublishedAt).Should().BeInDescendingOrder(
            "a listing walked tail-first must be newest-first end to end, not page by page");
    }

    [Fact]
    public async Task ListExisting_stops_at_the_page_cap_and_drops_the_oldest_conversation()
    {
        // The cap is the counterweight to following pagination at all: a very long thread must not hold the
        // daemon in a listing loop. What it discards has to be the oldest end — which is the half of this the
        // request count alone cannot show.
        var conversation = Filler(PageSize * 50, "chatter", 100);

        var handler = new FakeHttpMessageHandler();
        OnAscendingListing(handler, "/issues/7/comments", conversation);
        handler
            .OnJson(HttpMethod.Get, "/pulls/7/comments", "[]")
            .OnJson(HttpMethod.Get, "/pulls/7/reviews", "[]");

        var existing = await Publisher(handler).ListExistingReviewCommentsAsync(Target, CancellationToken.None);

        handler.CountRequests("/issues/7/comments").Should().Be(
            6, "one request locates the tail, then MaxListPages bounds the walk");
        existing.Should().HaveCount(PageSize * 5);
        existing.Should().Contain(e => e.Body == "chatter-4999", "the newest comment is what dedup needs");
        existing.Should().NotContain(e => e.Body == "chatter-0", "the cap must drop the oldest end");
    }

    [Fact]
    public async Task ListExisting_reads_a_conversation_that_fits_on_a_single_page()
    {
        // GitHub sends no Link header at all when the listing fits on one page. Treating a missing rel="last"
        // as anything but "page 1 is the tail" would either re-fetch nothing or walk off the end.
        var handler = new FakeHttpMessageHandler();
        OnAscendingListing(handler, "/issues/7/comments",
        [
            Comment(1, "first question", 0),
            Comment(2, "second question", 1),
        ]);
        handler
            .OnJson(HttpMethod.Get, "/pulls/7/comments", "[]")
            .OnJson(HttpMethod.Get, "/pulls/7/reviews", "[]");

        var existing = await Publisher(handler).ListExistingReviewCommentsAsync(Target, CancellationToken.None);

        existing.Select(e => e.Body).Should().Equal("second question", "first question");
        handler.CountRequests("/issues/7/comments").Should().Be(1, "a single-page listing needs one request");
    }
}
