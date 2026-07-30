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
        "7");

    public AdoReviewCommentPublisherTests(ITestOutputHelper output)
        : base(output)
    {
    }

    private AdoReviewCommentPublisher Publisher(FakeHttpMessageHandler handler) =>
        new(
            new HttpClient(handler),
            new FakeOAuthTokenProvider("ado", "ado-token-abc"),
            LoggerFactory.CreateLogger<AdoReviewCommentPublisher>());

    [Fact]
    public void Provider_id_is_ado()
    {
        Publisher(new FakeHttpMessageHandler()).Provider.Should().Be("ado");
    }

    [Fact]
    public async Task PostReviewComment_creates_a_thread_embedding_the_marker()
    {
        var handler = new FakeHttpMessageHandler().OnJson(
            HttpMethod.Post, "/pullRequests/7/threads", """{"id":555}""", HttpStatusCode.Created);

        var posted = await Publisher(handler).PostReviewCommentAsync(Target, Key, "## Review\nLGTM", CancellationToken.None);

        posted.ProviderResponseId.Should().Be("555");
        var request = handler.Requests.Should().ContainSingle().Subject;
        request.Method.Should().Be(HttpMethod.Post);
        request.Uri.ToString().Should()
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
        var listJson = JsonSerializer.Serialize(new
        {
            value = new[]
            {
                new { id = 100, comments = new[] { new { content = "unrelated thread" } } },
                new { id = 200, comments = new[] { new { content = $"## Review\nLGTM\n\n<!-- idempotency-key:{Key} -->" } } },
            },
        });
        var handler = new FakeHttpMessageHandler().OnJson(HttpMethod.Get, "/pullRequests/7/threads", listJson);

        var found = await Publisher(handler).FindPostedCommentAsync(Target, Key, CancellationToken.None);

        found.Should().NotBeNull();
        found!.ProviderResponseId.Should().Be("200");
    }

    [Fact]
    public async Task FindPostedComment_returns_null_when_no_thread_carries_the_marker()
    {
        var listJson = JsonSerializer.Serialize(new
        {
            value = new[] { new { id = 100, comments = new[] { new { content = "nothing here" } } } },
        });
        var handler = new FakeHttpMessageHandler().OnJson(HttpMethod.Get, "/pullRequests/7/threads", listJson);

        var found = await Publisher(handler).FindPostedCommentAsync(Target, Key, CancellationToken.None);

        found.Should().BeNull();
    }

    [Fact]
    public async Task PostReviewComment_throws_on_a_non_success_status()
    {
        var handler = new FakeHttpMessageHandler().OnJson(
            HttpMethod.Post, "/pullRequests/7/threads", """{"message":"forbidden"}""", HttpStatusCode.Forbidden);

        var act = () => Publisher(handler).PostReviewCommentAsync(Target, Key, "body", CancellationToken.None);

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task ListExisting_returns_thread_comments_with_file_line_and_author()
    {
        var threads = JsonSerializer.Serialize(new
        {
            value = new object[]
            {
                new
                {
                    threadContext = new { filePath = "/src/Foo.cs", rightFileStart = new { line = 42 } },
                    comments = new object[] { new { content = "Must — null deref here", author = new { displayName = "Revobot" } } },
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
        });
        var handler = new FakeHttpMessageHandler().OnJson(HttpMethod.Get, "/pullRequests/7/threads", threads);

        var existing = await Publisher(handler).ListExistingReviewCommentsAsync(Target, CancellationToken.None);

        existing.Should().HaveCount(2, "one inline finding + one PR-level note; the blank comment is skipped");
        existing.Should().ContainSingle(e =>
            e.Path == "/src/Foo.cs" && e.Line == "42" && e.Body.Contains("null deref") && e.Author == "Revobot");
        existing.Should().ContainSingle(e => e.Path == null && e.Body.Contains("General note") && e.Author == "Alice");
    }

    [Fact]
    public async Task ListExisting_maps_thread_status_to_active_or_resolved()
    {
        // The daemon must not re-post a finding that is already an ACTIVE (open) comment, but MAY re-raise a
        // RESOLVED one if the issue persists — so the publisher reports each thread's status. ADO returns
        // 'status' as a string; 'active'/'pending' are open, 'fixed'/'closed'/'wontFix'/'byDesign' are resolved,
        // and a thread with NO status is treated as active (conservative — never re-post a possibly-open one).
        var threads = JsonSerializer.Serialize(new
        {
            value = new object[]
            {
                new { status = "active", comments = new object[] { new { content = "still open", author = new { displayName = "Revobot" } } } },
                new { status = "fixed", comments = new object[] { new { content = "already fixed", author = new { displayName = "Revobot" } } } },
                new { comments = new object[] { new { content = "no status field", author = new { displayName = "Revobot" } } } },
            },
        });
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
        var threads = JsonSerializer.Serialize(new
        {
            value = new object[]
            {
                new { status = "active", comments = new object[]
                {
                    new { content = "REAL-FINDING here", commentType = "text", author = new { displayName = "Revobot" } },
                    new { content = "Gautam voted -5", commentType = "system", author = new { displayName = "Azure DevOps" } },
                    new { content = "DELETED-BODY", commentType = "text", isDeleted = true, author = new { displayName = "alice" } },
                } },
            },
        });
        var handler = new FakeHttpMessageHandler().OnJson(HttpMethod.Get, "/pullRequests/7/threads", threads);

        var existing = await Publisher(handler).ListExistingReviewCommentsAsync(Target, CancellationToken.None);

        existing.Should().ContainSingle(e => e.Body.Contains("REAL-FINDING"));
        existing.Should().NotContain(e => e.Body.Contains("voted"), "system activity is not review discussion");
        existing.Should().NotContain(e => e.Body.Contains("DELETED-BODY"), "deleted comments are excluded");
    }
}
