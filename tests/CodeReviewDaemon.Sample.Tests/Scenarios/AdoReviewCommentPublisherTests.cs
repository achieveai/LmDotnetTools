using System.Net;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmTestUtils.Logging;
using CodeReviewDaemon.Sample.Orchestration;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace CodeReviewDaemon.Sample.Tests.Scenarios;

/// <summary>
/// P4.4 — the real <see cref="AdoReviewCommentPublisher"/> posts and scans PR review comments via the
/// Azure DevOps pull-request <c>threads</c> API. ADO has no flat issue-comment list, so a review comment
/// is a single-comment thread. These tests pin: the post creates a thread carrying the hidden idempotency
/// marker, the scan recognizes a previously-posted thread by that marker, the request shape (basic auth,
/// the threads endpoint), and the failure mode.
/// </summary>
public sealed class AdoReviewCommentPublisherTests : LoggingTestBase
{
    private const string Key = "v1:ado:contoso:Platform:repo-guid-1:7:post-review-comment:review:summary:wm-1:primary";

    private static readonly ReviewCommentTarget Target = new(
        new RepoIdentity
        {
            Provider = "ado",
            OrgOrOwner = "contoso",
            Project = "Platform",
            RepoName = "widgets",
            RepoStableId = "repo-guid-1",
        },
        "7"
    );

    public AdoReviewCommentPublisherTests(ITestOutputHelper output)
        : base(output) { }

    private AdoReviewCommentPublisher Publisher(FakeHttpMessageHandler handler) =>
        new(
            new HttpClient(handler),
            new FakeOAuthTokenProvider("ado", "ado-token-abc"),
            LoggerFactory.CreateLogger<AdoReviewCommentPublisher>()
        );

    [Fact]
    public void Provider_id_is_ado()
    {
        Publisher(new FakeHttpMessageHandler()).Provider.Should().Be("ado");
    }

    [Fact]
    public async Task PostReviewComment_creates_a_thread_embedding_the_marker()
    {
        var handler = new FakeHttpMessageHandler().OnJson(
            HttpMethod.Post,
            "/pullRequests/7/threads",
            """{"id":555,"comments":[{"id":42}]}""",
            HttpStatusCode.Created
        );

        var posted = await Publisher(handler)
            .PostReviewCommentAsync(Target, Key, "## Review\nLGTM", CancellationToken.None);

        posted.ProviderResponseId.Should().Be("thread:555:comment:42");
        var request = handler.Requests.Should().ContainSingle().Subject;
        request.Method.Should().Be(HttpMethod.Post);
        request
            .Uri.ToString()
            .Should()
            .StartWith("https://dev.azure.com/contoso/Platform/_apis/git/repositories/widgets/pullRequests/7/threads");
        request.Uri.Query.Should().Contain("api-version=7.1");
        request.Authorization.Should().StartWith("Basic ", "ADO PATs/bearer tokens are sent via basic auth");

        var root = JsonDocument.Parse(request.Body!).RootElement;
        var content = root.GetProperty("comments")[0].GetProperty("content").GetString();
        content.Should().Contain("## Review\nLGTM");
        content.Should().Contain($"<!-- idempotency-key:{Key} -->", "the marker makes the post discoverable on replay");
    }

    [Fact]
    public async Task FindPostedComment_returns_the_thread_carrying_the_marker()
    {
        var listJson = JsonSerializer.Serialize(
            new
            {
                value = new[]
                {
                    new { id = 100, comments = new[] { new { id = 1, content = "unrelated thread" } } },
                    new
                    {
                        id = 200,
                        comments = new[]
                        {
                            new { id = 7, content = $"## Review\nLGTM\n\n<!-- idempotency-key:{Key} -->" },
                        },
                    },
                },
            }
        );
        var handler = new FakeHttpMessageHandler().OnJson(HttpMethod.Get, "/pullRequests/7/threads", listJson);

        var found = await Publisher(handler).FindPostedCommentAsync(Target, Key, CancellationToken.None);

        found.Should().NotBeNull();
        found!.ProviderResponseId.Should().Be("thread:200:comment:7");
    }

    [Fact]
    public async Task FindPostedComment_returns_null_when_no_thread_carries_the_marker()
    {
        var listJson = JsonSerializer.Serialize(
            new { value = new[] { new { id = 100, comments = new[] { new { content = "nothing here" } } } } }
        );
        var handler = new FakeHttpMessageHandler().OnJson(HttpMethod.Get, "/pullRequests/7/threads", listJson);

        var found = await Publisher(handler).FindPostedCommentAsync(Target, Key, CancellationToken.None);

        found.Should().BeNull();
    }

    [Fact]
    public async Task PostReviewComment_throws_on_a_non_success_status()
    {
        var handler = new FakeHttpMessageHandler().OnJson(
            HttpMethod.Post,
            "/pullRequests/7/threads",
            """{"message":"forbidden"}""",
            HttpStatusCode.Forbidden
        );

        var act = () => Publisher(handler).PostReviewCommentAsync(Target, Key, "body", CancellationToken.None);

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task PostLocatedThread_preserves_file_spans_and_pull_request_iteration_context()
    {
        var handler = new FakeHttpMessageHandler().OnJson(
            HttpMethod.Post,
            "/pullRequests/7/threads",
            """{"id":555,"status":"active","threadContext":{"filePath":"/src/Foo.cs","rightFileStart":{"line":40,"offset":2},"rightFileEnd":{"line":42,"offset":9}},"pullRequestThreadContext":{"changeTrackingId":17,"iterationContext":{"firstComparingIteration":2,"secondComparingIteration":3}},"comments":[{"id":42,"parentCommentId":0,"_links":{"self":{"href":"https://dev.azure.com/contoso/Platform/_apis/git/repositories/widgets/pullRequests/7/threads/555/comments/42"}}}]}""",
            HttpStatusCode.Created
        );
        var request = new AdoThreadRequest(
            new ProviderCommentSpan("/src/Foo.cs", "RIGHT", 40, 42, 2, 9),
            new AdoPullRequestThreadContext(17, new AdoIterationContext(2, 3)),
            "finding"
        );

        var posted = await Publisher(handler).PostLocatedThreadAsync(Target, Key, request, CancellationToken.None);

        posted.ProviderResponseId.Should().Be("thread:555:comment:42");
        posted.ThreadId.Should().Be("555");
        posted.CommentId.Should().Be("42");
        posted.ParentCommentId.Should().Be("0");
        posted.Status.Should().Be("active");
        posted.Span.Should().Be(request.Span);
        posted.IterationContext.Should().Be(request.PullRequestContext);
        var payload = JsonDocument.Parse(handler.Requests.Should().ContainSingle().Subject.Body!).RootElement;
        payload
            .GetProperty("threadContext")
            .GetProperty("rightFileStart")
            .GetProperty("line")
            .GetInt32()
            .Should()
            .Be(40);
        payload
            .GetProperty("threadContext")
            .GetProperty("rightFileEnd")
            .GetProperty("offset")
            .GetInt32()
            .Should()
            .Be(9);
        payload
            .GetProperty("pullRequestThreadContext")
            .GetProperty("iterationContext")
            .GetProperty("secondComparingIteration")
            .GetInt32()
            .Should()
            .Be(3);
    }

    [Fact]
    public async Task ReplyToThread_posts_to_existing_thread_comments_with_parent_comment_id()
    {
        var handler = new FakeHttpMessageHandler().OnJson(
            HttpMethod.Post,
            "/pullRequests/7/threads/555/comments",
            """{"id":43,"parentCommentId":42,"content":"reply","_links":{"self":{"href":"https://dev.azure.com/comment/43"}}}""",
            HttpStatusCode.Created
        );
        var request = new AdoThreadReplyRequest(
            "555",
            "42",
            "active",
            new ProviderCommentSpan("/src/Foo.cs", "LEFT", 8, 9, 1, 3),
            new AdoPullRequestThreadContext(17, new AdoIterationContext(2, 3)),
            "reply"
        );

        var posted = await Publisher(handler).ReplyToThreadAsync(Target, Key, request, CancellationToken.None);

        posted.ProviderResponseId.Should().Be("thread:555:comment:43");
        posted.ThreadId.Should().Be("555");
        posted.CommentId.Should().Be("43");
        posted.ParentCommentId.Should().Be("42");
        posted.Status.Should().Be("active");
        posted.Span.Should().Be(request.Span);
        posted.IterationContext.Should().Be(request.PullRequestContext);
        var sent = handler.Requests.Should().ContainSingle().Subject;
        sent.Uri.AbsolutePath.Should().EndWith("/pullRequests/7/threads/555/comments");
        var payload = JsonDocument.Parse(sent.Body!).RootElement;
        payload.GetProperty("parentCommentId").GetInt32().Should().Be(42);
        payload.GetProperty("content").GetString().Should().Contain($"idempotency-key:{Key}");
    }

    [Fact]
    public async Task FindPostedComment_returns_rich_thread_receipt_for_a_reply_marker()
    {
        var listJson =
            """{"value":[{"id":555,"status":"fixed","threadContext":{"filePath":"/src/Foo.cs","leftFileStart":{"line":8,"offset":1},"leftFileEnd":{"line":9,"offset":3}},"pullRequestThreadContext":{"changeTrackingId":17,"iterationContext":{"firstComparingIteration":2,"secondComparingIteration":3}},"comments":[{"id":42,"parentCommentId":0,"content":"root"},{"id":43,"parentCommentId":42,"content":"reply\n<!-- idempotency-key:v1:ado:contoso:Platform:repo-guid-1:7:post-review-comment:review:summary:wm-1:primary -->","_links":{"self":{"href":"https://dev.azure.com/comment/43"}}}]}]}""";
        var handler = new FakeHttpMessageHandler().OnJson(HttpMethod.Get, "/pullRequests/7/threads", listJson);

        var found = await Publisher(handler).FindPostedCommentAsync(Target, Key, CancellationToken.None);

        found.Should().NotBeNull();
        found!.ThreadId.Should().Be("555");
        found.CommentId.Should().Be("43");
        found.ParentCommentId.Should().Be("42");
        found.Permalink.Should().Be("https://dev.azure.com/comment/43");
        found.Status.Should().Be("fixed");
        found.Span.Should().Be(new ProviderCommentSpan("/src/Foo.cs", "LEFT", 8, 9, 1, 3));
        found.IterationContext.Should().Be(new AdoPullRequestThreadContext(17, new AdoIterationContext(2, 3)));
    }

    [Fact]
    public async Task ListExisting_returns_thread_comments_with_file_line_and_author()
    {
        var threads = JsonSerializer.Serialize(
            new
            {
                value = new object[]
                {
                    new
                    {
                        threadContext = new { filePath = "/src/Foo.cs", rightFileStart = new { line = 42 } },
                        comments = new object[]
                        {
                            new { content = "Must — null deref here", author = new { displayName = "Revobot" } },
                        },
                    },
                    new
                    {
                        // no thread context (PR-level) + one blank comment that must be skipped
                        comments = new object[]
                        {
                            new { content = "General note", author = new { displayName = "Alice" } },
                            new { content = "   ", author = new { displayName = "Revobot" } },
                        },
                    },
                },
            }
        );
        var handler = new FakeHttpMessageHandler().OnJson(HttpMethod.Get, "/pullRequests/7/threads", threads);

        var existing = await Publisher(handler).ListExistingReviewCommentsAsync(Target, CancellationToken.None);

        existing.Should().HaveCount(2, "one inline finding + one PR-level note; the blank comment is skipped");
        existing
            .Should()
            .ContainSingle(e =>
                e.Path == "/src/Foo.cs" && e.Line == "42" && e.Body.Contains("null deref") && e.Author == "Revobot"
            );
        existing.Should().ContainSingle(e => e.Path == null && e.Body.Contains("General note") && e.Author == "Alice");
    }

    [Fact]
    public async Task ListExisting_caps_a_body_at_the_cap_shared_with_the_other_provider()
    {
        // The sibling of the GitHub suite's test of the same name, asserting the same numbers. The point of the
        // pair is the drift the two private copies of this cap invited (#225 item 4): a change made in one
        // publisher alone cannot leave both files green.
        var threads = JsonSerializer.Serialize(
            new
            {
                value = new object[]
                {
                    new
                    {
                        status = "active",
                        comments = new object[]
                        {
                            new { content = "HEAD-" + new string('a', 1_400), author = new { displayName = "alice" } },
                            new { content = "LONG-" + new string('b', 4_000), author = new { displayName = "alice" } },
                        },
                    },
                },
            }
        );
        var handler = new FakeHttpMessageHandler().OnJson(HttpMethod.Get, "/pullRequests/7/threads", threads);

        var existing = await Publisher(handler).ListExistingReviewCommentsAsync(Target, CancellationToken.None);

        // A body well past the OLD 280-char cap survives whole — the resolution signal that settles a thread sits
        // at the end of a conversation, and cutting it off is how a settled thread gets re-raised.
        existing
            .Should()
            .ContainSingle(e => e.Body.StartsWith("HEAD-", StringComparison.Ordinal))
            .Which.Body.Length.Should()
            .Be(1_405, "a body under the shared cap is carried verbatim");

        // …and the cap is still a real cap.
        var capped = existing
            .Should()
            .ContainSingle(e => e.Body.StartsWith("LONG-", StringComparison.Ordinal))
            .Which.Body;
        capped.Length.Should().Be(2_001, "2,000 characters plus the ellipsis that marks the cut");
        capped.Should().EndWith("…", "a truncated comment must be distinguishable from a terse one");
    }

    [Fact]
    public async Task ListExisting_maps_thread_status_to_active_or_resolved()
    {
        // The daemon must not re-post a finding that is already an ACTIVE (open) comment, but MAY re-raise a
        // RESOLVED one if the issue persists — so the publisher reports each thread's status. ADO returns
        // 'status' as a string; 'active'/'pending' are open, 'fixed'/'closed'/'wontFix'/'byDesign' are resolved,
        // and a thread with NO status is treated as active (conservative — never re-post a possibly-open one).
        var threads = JsonSerializer.Serialize(
            new
            {
                value = new object[]
                {
                    new
                    {
                        status = "active",
                        comments = new object[]
                        {
                            new { content = "still open", author = new { displayName = "Revobot" } },
                        },
                    },
                    new
                    {
                        status = "fixed",
                        comments = new object[]
                        {
                            new { content = "already fixed", author = new { displayName = "Revobot" } },
                        },
                    },
                    new
                    {
                        comments = new object[]
                        {
                            new { content = "no status field", author = new { displayName = "Revobot" } },
                        },
                    },
                },
            }
        );
        var handler = new FakeHttpMessageHandler().OnJson(HttpMethod.Get, "/pullRequests/7/threads", threads);

        var existing = await Publisher(handler).ListExistingReviewCommentsAsync(Target, CancellationToken.None);

        existing.Should().ContainSingle(e => e.Body.Contains("still open") && e.IsActive);
        existing.Should().ContainSingle(e => e.Body.Contains("already fixed") && !e.IsActive);
        existing.Should().ContainSingle(e => e.Body.Contains("no status field") && e.IsActive);
    }

    [Fact]
    public async Task ListExisting_skips_system_activity_and_deleted_comments()
    {
        // ADO threads also carry non-discussion entries: system activity (merges/votes/reviewer updates all use
        // commentType "system") and deleted comments. Those are non-blank but not review discussion, so they must
        // not consume the bounded existing-comment budget and displace real findings/questions.
        var threads = JsonSerializer.Serialize(
            new
            {
                value = new object[]
                {
                    new
                    {
                        status = "active",
                        comments = new object[]
                        {
                            new
                            {
                                content = "REAL-FINDING here",
                                commentType = "text",
                                author = new { displayName = "Revobot" },
                            },
                            new
                            {
                                content = "Gautam voted -5",
                                commentType = "system",
                                author = new { displayName = "Azure DevOps" },
                            },
                            new
                            {
                                content = "DELETED-BODY",
                                commentType = "text",
                                isDeleted = true,
                                author = new { displayName = "alice" },
                            },
                        },
                    },
                },
            }
        );
        var handler = new FakeHttpMessageHandler().OnJson(HttpMethod.Get, "/pullRequests/7/threads", threads);

        var existing = await Publisher(handler).ListExistingReviewCommentsAsync(Target, CancellationToken.None);

        existing.Should().ContainSingle(e => e.Body.Contains("REAL-FINDING"));
        existing.Should().NotContain(e => e.Body.Contains("voted"), "system activity is not review discussion");
        existing.Should().NotContain(e => e.Body.Contains("DELETED-BODY"), "deleted comments are excluded");
    }
}
