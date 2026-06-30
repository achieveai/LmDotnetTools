using System.Net;
using System.Text.Json;
using CodeReviewDaemon.Sample.Orchestration;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeReviewDaemon.Sample.Tests.Scenarios;

/// <summary>
/// P4.4 — the real <see cref="GitHubReviewCommentPublisher"/> posts and scans PR comments via the GitHub
/// issues-comments API. These tests pin: the post embeds the hidden idempotency marker (so the backstop
/// scan can find it), the scan recognizes a previously-posted comment by that marker, and the request
/// shape (bearer auth, the comments endpoint).
/// </summary>
public sealed class GitHubReviewCommentPublisherTests
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

    private static GitHubReviewCommentPublisher Publisher(FakeHttpMessageHandler handler) =>
        new(
            new HttpClient(handler),
            new FakeOAuthTokenProvider("github", "gh-token-xyz"),
            NullLogger<GitHubReviewCommentPublisher>.Instance);

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
}
