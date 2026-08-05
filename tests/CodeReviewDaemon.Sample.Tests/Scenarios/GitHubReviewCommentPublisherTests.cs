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
    // ABSENCE: an item that was not seen is treated as never posted. A listing truncated at GitHub's first
    // page therefore does not degrade, it inverts — so each of these tests puts the item that matters on
    // page 2 behind a full first page, which is exactly the shape a single-request enumerator cannot see.

    /// <summary>A full page of JSON objects — enough to make the enumerator ask for the page after it.</summary>
    private static string FullPage(int startId, string bodyPrefix) => Page(PageSize, startId, bodyPrefix);

    private static string Page(int count, int startId, string bodyPrefix) =>
        JsonSerializer.Serialize(Enumerable.Range(0, count).Select(i => new
        {
            id = startId + i,
            body = $"{bodyPrefix}-{i}",
            state = "COMMENTED",
            submitted_at = "2026-07-20T10:00:00Z",
            created_at = "2026-07-20T10:00:00Z",
            user = new { login = "alice" },
        }).ToArray());

    private const int PageSize = 100;

    /// <summary>Routes one URL by page number, so page 2 can carry what page 1 does not.</summary>
    private static FakeHttpMessageHandler OnPage(
        FakeHttpMessageHandler handler, string pathContains, int page, string json) =>
        handler.On(
            req => req.Method == HttpMethod.Get
                && req.RequestUri is not null
                && req.RequestUri.ToString().Contains(pathContains, StringComparison.Ordinal)
                && req.RequestUri.ToString().Contains($"page={page}", StringComparison.Ordinal),
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });

    [Fact]
    public async Task ListExisting_reads_a_question_that_falls_beyond_the_first_page_of_conversation()
    {
        // The bot's own prior summaries AND questions addressed to it live on /issues/{pr}/comments. Stopping at
        // page 1 of a busy PR is what makes the daemon repost a resolved finding or leave a question unanswered.
        var handler = new FakeHttpMessageHandler();
        OnPage(handler, "/issues/7/comments", 2, JsonSerializer.Serialize(new[]
        {
            new { id = 9001, body = "@revobot is this still needed?", user = new { login = "alice" }, created_at = "2026-07-21T09:00:00Z" },
        }));
        OnPage(handler, "/issues/7/comments", 1, FullPage(8000, "older-chatter"));
        handler.OnJson(HttpMethod.Get, "/pulls/7/comments", "[]").OnJson(HttpMethod.Get, "/pulls/7/reviews", "[]");

        var existing = await Publisher(handler).ListExistingReviewCommentsAsync(Target, CancellationToken.None);

        existing.Should().ContainSingle(
            e => e.Body.Contains("still needed"),
            "a question on the second page is still discussion the reviewer must answer");
    }

    [Fact]
    public async Task ListExisting_reads_a_pending_draft_that_falls_beyond_the_first_page_of_reviews()
    {
        // Pagination on /pulls/{pr}/reviews is not merely about returning more rows: the ids collected there are
        // what suppress a stale draft's inline comments. Miss the draft on page 2 and its finding seeds dedup,
        // suppressing the valid submitted replacement — the very failure the PENDING filter exists to prevent.
        var handler = new FakeHttpMessageHandler();
        OnPage(handler, "/pulls/7/reviews", 2, JsonSerializer.Serialize(new object[]
        {
            new { id = 5001, body = "", user = new { login = "revobot" }, state = "PENDING" },
        }));
        OnPage(handler, "/pulls/7/reviews", 1, FullPage(1000, "older-review"));
        handler
            .OnJson(HttpMethod.Get, "/pulls/7/comments", JsonSerializer.Serialize(new object[]
            {
                new { body = "DRAFT-inline-finding", path = "src/Foo.cs", line = 3, pull_request_review_id = 5001, user = new { login = "revobot" }, created_at = "2026-07-20T09:00:00Z" },
            }))
            .OnJson(HttpMethod.Get, "/issues/7/comments", "[]");

        var existing = await Publisher(handler).ListExistingReviewCommentsAsync(Target, CancellationToken.None);

        existing.Should().NotContain(
            e => e.Body.Contains("DRAFT-inline-finding"),
            "the draft's id is on page 2, so only a paginated review listing can suppress its inline comment");
    }

    [Fact]
    public async Task FindPostedComment_finds_a_marker_that_falls_beyond_the_first_page()
    {
        // The exactly-once backstop: this scan answers "did a crashed attempt already post this?". On a PR whose
        // conversation exceeds one page, a single-request scan answers "no" for a comment that exists, and the
        // daemon posts a duplicate. Absence is the answer that matters, so it is the one that must be earned.
        var handler = new FakeHttpMessageHandler();
        OnPage(handler, "/issues/7/comments", 2, JsonSerializer.Serialize(new[]
        {
            new { id = 777, body = $"## Review\nLGTM\n\n<!-- idempotency-key:{Key} -->" },
        }));
        OnPage(handler, "/issues/7/comments", 1, FullPage(6000, "unrelated"));

        var found = await Publisher(handler).FindPostedCommentAsync(Target, Key, CancellationToken.None);

        found.Should().NotBeNull();
        found!.ProviderResponseId.Should().Be("777");
    }

    [Fact]
    public async Task FindPostedComment_scans_newest_first()
    {
        var handler = new FakeHttpMessageHandler().OnJson(HttpMethod.Get, "/issues/7/comments", "[]");

        await Publisher(handler).FindPostedCommentAsync(Target, Key, CancellationToken.None);

        handler.Requests.Should().ContainSingle().Which.Uri.ToString().Should().Contain(
            "direction=desc",
            "a comment this daemon just posted is the newest one, so newest-first finds it on page 1");
    }

    [Fact]
    public async Task ListExisting_requests_conversation_comments_newest_first()
    {
        var handler = new FakeHttpMessageHandler()
            .OnJson(HttpMethod.Get, "/pulls/7/comments", "[]")
            .OnJson(HttpMethod.Get, "/pulls/7/reviews", "[]")
            .OnJson(HttpMethod.Get, "/issues/7/comments", "[]");

        await Publisher(handler).ListExistingReviewCommentsAsync(Target, CancellationToken.None);

        handler.Requests.Should().Contain(
            r => r.Uri.ToString().Contains("/issues/7/comments") && r.Uri.ToString().Contains("direction=desc"),
            "the page cap must drop the oldest conversation, not the discussion still under argument");
    }

    [Fact]
    public async Task ListExisting_stops_at_the_page_cap_when_every_page_is_full()
    {
        // The cap is the counterweight to following pagination at all: an endpoint that never returns a short
        // page must not be able to hold the daemon in a listing loop.
        var handler = new FakeHttpMessageHandler()
            .OnJson(HttpMethod.Get, "/pulls/7/comments", "[]")
            .OnJson(HttpMethod.Get, "/pulls/7/reviews", FullPage(2000, "endless-review"))
            .OnJson(HttpMethod.Get, "/issues/7/comments", "[]");

        await Publisher(handler).ListExistingReviewCommentsAsync(Target, CancellationToken.None);

        handler.CountRequests("/pulls/7/reviews").Should().Be(
            5, "MaxListPages bounds a listing whose pages are always full");
    }
}
