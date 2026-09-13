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
            """{"id":555,"comments":[{"id":1}]}""",
            HttpStatusCode.Created
        );

        var posted = await Publisher(handler)
            .PostReviewCommentAsync(Target, Key, "## Review\nLGTM", CancellationToken.None);

        posted.ProviderResponseId.Should().Be("555");
        posted.ProviderCommentId.Should().Be("555/1");
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
    public async Task PostInlineComment_creates_a_thread_with_the_exact_right_side_target()
    {
        var handler = new FakeHttpMessageHandler().OnJson(
            HttpMethod.Post,
            "/pullRequests/7/threads",
            """{"id":600,"comments":[{"id":1}]}""",
            HttpStatusCode.OK
        );
        var target = Target with
        {
            Kind = ReviewCommentKind.Inline,
            Path = "src/Foo.cs",
            Line = 42,
            Side = ReviewCommentSide.Right,
        };

        var posted = await Publisher(handler).PostReviewCommentAsync(target, Key, "Fix this", CancellationToken.None);

        posted.ProviderResponseId.Should().Be("600");
        posted.ProviderCommentId.Should().Be("600/1");
        var request = handler.Requests.Should().ContainSingle().Subject;
        var json = JsonDocument.Parse(request.Body!).RootElement;
        json.GetProperty("comments")[0]
            .GetProperty("content")
            .GetString()
            .Should()
            .Contain($"<!-- idempotency-key:{Key} -->");
        var context = json.GetProperty("threadContext");
        context.GetProperty("filePath").GetString().Should().Be("/src/Foo.cs");
        context.GetProperty("rightFileStart").GetProperty("line").GetInt32().Should().Be(42);
        context.GetProperty("rightFileEnd").GetProperty("line").GetInt32().Should().Be(42);
        context.TryGetProperty("leftFileStart", out _).Should().BeFalse();
    }

    [Fact]
    public async Task PostReply_uses_the_exact_thread_and_parent_comment()
    {
        var handler = new FakeHttpMessageHandler().OnJson(
            HttpMethod.Post,
            "/pullRequests/7/threads/600/comments",
            """{"id":2}""",
            HttpStatusCode.OK
        );
        var target = Target with
        {
            Kind = ReviewCommentKind.Reply,
            ProviderThreadId = "600",
            ReplyToProviderCommentId = "1",
        };

        var posted = await Publisher(handler).PostReviewCommentAsync(target, Key, "Addressed", CancellationToken.None);

        posted.ProviderResponseId.Should().Be("2");
        posted.ProviderCommentId.Should().Be("600/2");
        var request = handler.Requests.Should().ContainSingle().Subject;
        request.Uri.AbsolutePath.Should().EndWith("/pullRequests/7/threads/600/comments");
        var json = JsonDocument.Parse(request.Body!).RootElement;
        json.GetProperty("content").GetString().Should().Contain($"<!-- idempotency-key:{Key} -->");
        json.GetProperty("parentCommentId").GetInt32().Should().Be(1);
        json.GetProperty("commentType").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task FindPostedComment_returns_the_thread_carrying_the_marker()
    {
        var listJson = JsonSerializer.Serialize(
            new
            {
                value = new object[]
                {
                    new { id = 100, comments = new[] { new { content = "unrelated thread" } } },
                    new
                    {
                        id = 200,
                        comments = new[]
                        {
                            new { id = 3, content = $"## Review\nLGTM\n\n<!-- idempotency-key:{Key} -->" },
                        },
                    },
                },
            }
        );
        var handler = new FakeHttpMessageHandler().OnJson(HttpMethod.Get, "/pullRequests/7/threads", listJson);

        var found = await Publisher(handler).FindPostedCommentAsync(Target, Key, CancellationToken.None);

        found.Should().NotBeNull();
        found!.ProviderResponseId.Should().Be("200");
        found.ProviderCommentId.Should().Be("200/3");
    }

    [Fact]
    public async Task FindPostedReply_scans_only_the_exact_thread_comments()
    {
        var listJson = JsonSerializer.Serialize(
            new { value = new[] { new { id = 2, content = $"Addressed\n\n<!-- idempotency-key:{Key} -->" } } }
        );
        var handler = new FakeHttpMessageHandler().OnJson(
            HttpMethod.Get,
            "/pullRequests/7/threads/600/comments",
            listJson
        );
        var target = Target with
        {
            Kind = ReviewCommentKind.Reply,
            ProviderThreadId = "600",
            ReplyToProviderCommentId = "1",
        };

        var found = await Publisher(handler).FindPostedCommentAsync(target, Key, CancellationToken.None);

        found.Should().Be(new PostedComment("2", "600/2"));
        handler.Requests.Should().ContainSingle();
    }

    [Theory]
    [InlineData(null, "1")]
    [InlineData("", "1")]
    [InlineData("0", "1")]
    [InlineData("600/comments/2", "1")]
    [InlineData("600", null)]
    [InlineData("600", "0")]
    public async Task Reply_rejects_unsafe_provider_ids_before_HTTP(string? threadId, string? commentId)
    {
        var handler = new FakeHttpMessageHandler();
        var target = Target with
        {
            Kind = ReviewCommentKind.Reply,
            ProviderThreadId = threadId,
            ReplyToProviderCommentId = commentId,
        };

        var act = () => Publisher(handler).PostReviewCommentAsync(target, Key, "body", CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
        handler.Requests.Should().BeEmpty();
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
    public async Task ListExisting_returns_thread_comments_with_file_line_and_author()
    {
        var threads = JsonSerializer.Serialize(
            new
            {
                value = new object[]
                {
                    new
                    {
                        id = 10,
                        threadContext = new { filePath = "/src/Foo.cs", rightFileStart = new { line = 42 } },
                        comments = new object[]
                        {
                            new
                            {
                                id = 4,
                                content = "Must — null deref here",
                                author = new { displayName = "Revobot" },
                            },
                        },
                    },
                    new
                    {
                        id = 11,
                        // no thread context (PR-level) + one blank comment that must be skipped
                        comments = new object[]
                        {
                            new
                            {
                                id = 1,
                                content = "General note",
                                author = new { displayName = "Alice" },
                            },
                            new
                            {
                                id = 2,
                                content = "   ",
                                author = new { displayName = "Revobot" },
                            },
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
                e.Path == "/src/Foo.cs"
                && e.Line == "42"
                && e.Body.Contains("null deref")
                && e.Author == "Revobot"
                && e.ProviderCommentId == "10/4"
            );
        existing
            .Should()
            .ContainSingle(e =>
                e.Path == null
                && e.Body.Contains("General note")
                && e.Author == "Alice"
                && e.ProviderCommentId == "11/1"
            );
    }

    [Fact]
    public async Task ListExisting_carries_exact_provider_content_version()
    {
        var json =
            """{"value":[{"id":10,"status":"active","comments":[{"id":4,"content":"note","commentType":"text","publishedDate":"2026-07-20T10:00:00Z","lastContentUpdatedDate":"2026-07-21T11:12:13Z"}]}]}""";
        var handler = new FakeHttpMessageHandler().OnJson(HttpMethod.Get, "/pullRequests/7/threads", json);

        var existing = await Publisher(handler).ListExistingReviewCommentsAsync(Target, CancellationToken.None);

        existing.Should().ContainSingle().Which.ProviderVersion.Should().Be("2026-07-21T11:12:13Z");
    }

    [Fact]
    public async Task ListExisting_fails_when_ado_reports_a_continuation_token()
    {
        var handler = new FakeHttpMessageHandler().On(
            request =>
                request.Method == HttpMethod.Get
                && request.RequestUri!.AbsolutePath.Contains("/threads", StringComparison.Ordinal),
            _ =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"value\":[]}", Encoding.UTF8, "application/json"),
                };
                response.Headers.TryAddWithoutValidation("x-ms-continuationtoken", "next-page");
                return response;
            }
        );

        var act = () => Publisher(handler).ListExistingReviewCommentsAsync(Target, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidDataException>();
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
