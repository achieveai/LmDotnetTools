using System.Net;
using System.Text.Json;
using CodeReviewDaemon.Sample.Orchestration;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeReviewDaemon.Sample.Tests.Scenarios;

/// <summary>
/// P4.4 — the real <see cref="AdoReviewCommentPublisher"/> posts and scans PR review comments via the
/// Azure DevOps pull-request <c>threads</c> API. ADO has no flat issue-comment list, so a review comment
/// is a single-comment thread. These tests pin: the post creates a thread carrying the hidden idempotency
/// marker, the scan recognizes a previously-posted thread by that marker, the request shape (basic auth,
/// the threads endpoint), and the failure mode.
/// </summary>
public sealed class AdoReviewCommentPublisherTests
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

    private static AdoReviewCommentPublisher Publisher(FakeHttpMessageHandler handler) =>
        new(
            new HttpClient(handler),
            new FakeOAuthTokenProvider("ado", "ado-token-abc"),
            NullLogger<AdoReviewCommentPublisher>.Instance);

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
}
