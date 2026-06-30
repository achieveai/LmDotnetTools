using System.Net;
using AchieveAi.LmDotnetTools.LmTestUtils.Logging;
using CodeReviewDaemon.Sample.Orchestration;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace CodeReviewDaemon.Sample.Tests.Scenarios;

/// <summary>
/// P4.4 — the real <see cref="GitHubPrProvider"/> reads open PRs from the GitHub REST API. These tests
/// drive it against a scripted HTTP handler (no network): they pin the request shape GitHub requires
/// (bearer auth from the OAuth provider, a <c>User-Agent</c>, the <c>vnd.github+json</c> accept header,
/// the <c>state=open</c> query), the descriptor mapping (number/head/base/updated_at/lifecycle), and the
/// versioned opaque cursor it advances.
/// </summary>
public sealed class GitHubPrProviderTests : LoggingTestBase
{
    public GitHubPrProviderTests(ITestOutputHelper output)
        : base(output)
    {
    }

    private static readonly RepoIdentity Repo = new()
    {
        Provider = "github",
        OrgOrOwner = "acme",
        RepoName = "widgets",
        RepoStableId = "R_node_123",
    };

    private const string TwoOpenPrs = """
        [
          {
            "number": 7,
            "state": "open",
            "merged_at": null,
            "updated_at": "2026-06-01T10:00:00Z",
            "head": { "sha": "head-7" },
            "base": { "sha": "base-7" }
          },
          {
            "number": 9,
            "state": "open",
            "merged_at": null,
            "updated_at": "2026-06-02T12:30:00Z",
            "head": { "sha": "head-9" },
            "base": { "sha": "base-9" }
          }
        ]
        """;

    private static PrPollRequest Request(OpaqueCursor? cursor = null) => new()
    {
        Repo = Repo,
        Scope = "acme/widgets:open-prs",
        Cursor = cursor,
    };

    private GitHubPrProvider Provider(FakeHttpMessageHandler handler) =>
        new(
            new HttpClient(handler),
            new FakeOAuthTokenProvider("github", "gh-token-xyz"),
            LoggerFactory.CreateLogger<GitHubPrProvider>());

    [Fact]
    public void Provider_id_is_github()
    {
        Provider(new FakeHttpMessageHandler()).Provider.Should().Be("github");
    }

    [Fact]
    public async Task ListOpenPullRequests_maps_each_pr_to_a_descriptor()
    {
        var handler = new FakeHttpMessageHandler().OnJson(HttpMethod.Get, "/repos/acme/widgets/pulls", TwoOpenPrs);

        var page = await Provider(handler).ListOpenPullRequestsAsync(Request(), CancellationToken.None);

        page.PullRequests.Should().HaveCount(2);
        var first = page.PullRequests[0];
        first.PrId.Should().Be("7");
        first.HeadSha.Should().Be("head-7");
        first.BaseSha.Should().Be("base-7");
        first.TriggerWatermark.Should().Be("2026-06-01T10:00:00Z");
        first.LifecycleState.Should().Be(PrLifecycleState.Open);
    }

    [Fact]
    public async Task ListOpenPullRequests_sends_the_request_github_requires()
    {
        var handler = new FakeHttpMessageHandler().OnJson(HttpMethod.Get, "/repos/acme/widgets/pulls", TwoOpenPrs);

        _ = await Provider(handler).ListOpenPullRequestsAsync(Request(), CancellationToken.None);

        var request = handler.Requests.Should().ContainSingle().Subject;
        request.Method.Should().Be(HttpMethod.Get);
        request.Uri.ToString().Should().StartWith("https://api.github.com/repos/acme/widgets/pulls");
        request.Uri.Query.Should().Contain("state=open");
        request.Authorization.Should().Be("Bearer gh-token-xyz");
        request.UserAgent.Should().NotBeNullOrEmpty("GitHub rejects requests without a User-Agent");
    }

    [Fact]
    public async Task ListOpenPullRequests_advances_a_versioned_opaque_cursor()
    {
        var handler = new FakeHttpMessageHandler().OnJson(HttpMethod.Get, "/repos/acme/widgets/pulls", TwoOpenPrs);

        var page = await Provider(handler).ListOpenPullRequestsAsync(Request(), CancellationToken.None);

        page.NextCursor.Provider.Should().Be("github");
        page.NextCursor.Scope.Should().Be("acme/widgets:open-prs");
        page.NextCursor.CursorVersion.Should().Be(PrPollingService.CursorVersion);
        page.NextCursor.CursorPayload.Should().NotBeNullOrWhiteSpace();
        // The high-water mark is the newest updated_at seen, so the next poll can prune unchanged PRs.
        page.NextCursor.HighWaterMark.Should().Be("2026-06-02T12:30:00Z");
    }

    [Fact]
    public async Task ListOpenPullRequests_maps_a_merged_pr_to_the_merged_lifecycle()
    {
        const string mergedPr = """
            [
              {
                "number": 4,
                "state": "closed",
                "merged_at": "2026-06-03T09:00:00Z",
                "updated_at": "2026-06-03T09:00:00Z",
                "head": { "sha": "head-4" },
                "base": { "sha": "base-4" }
              }
            ]
            """;
        var handler = new FakeHttpMessageHandler().OnJson(HttpMethod.Get, "/repos/acme/widgets/pulls", mergedPr);

        var page = await Provider(handler).ListOpenPullRequestsAsync(Request(), CancellationToken.None);

        page.PullRequests.Should().ContainSingle().Which.LifecycleState.Should().Be(PrLifecycleState.Merged);
    }

    [Fact]
    public async Task ListOpenPullRequests_throws_on_a_non_success_status()
    {
        var handler = new FakeHttpMessageHandler().OnJson(
            HttpMethod.Get, "/repos/acme/widgets/pulls", """{"message":"Bad credentials"}""", HttpStatusCode.Unauthorized);

        var act = () => Provider(handler).ListOpenPullRequestsAsync(Request(), CancellationToken.None);

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task ListOpenPullRequests_handles_an_empty_page()
    {
        var handler = new FakeHttpMessageHandler().OnJson(HttpMethod.Get, "/repos/acme/widgets/pulls", "[]");

        var page = await Provider(handler).ListOpenPullRequestsAsync(Request(), CancellationToken.None);

        page.PullRequests.Should().BeEmpty();
        page.NextCursor.CursorVersion.Should().Be(PrPollingService.CursorVersion);
    }
}
