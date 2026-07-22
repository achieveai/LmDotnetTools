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
            .OnJson(HttpMethod.Get, "/pulls/7/reviews", reviews);

        var existing = await Publisher(handler).ListExistingReviewCommentsAsync(Target, CancellationToken.None);

        existing.Should().HaveCount(3, "two non-blank inline comments + one non-empty review summary");
        existing.Should().ContainSingle(e =>
            e.Path == "src/Foo.cs" && e.Line == "42" && e.Body.Contains("null deref") && e.Author == "revobot");
        existing.Should().ContainSingle(e =>
            e.Path == "src/Bar.cs" && e.Line == "7" && e.Author == "alice"); // original_line fallback when line is absent
        existing.Should().ContainSingle(e => e.Path == null && e.Body.Contains("Reviewed PR 7"));
    }

    [Fact]
    public async Task ListExisting_returns_empty_for_a_pr_with_no_comments()
    {
        var handler = new FakeHttpMessageHandler()
            .OnJson(HttpMethod.Get, "/pulls/7/comments", "[]")
            .OnJson(HttpMethod.Get, "/pulls/7/reviews", "[]");

        var existing = await Publisher(handler).ListExistingReviewCommentsAsync(Target, CancellationToken.None);

        existing.Should().BeEmpty();
    }
}
