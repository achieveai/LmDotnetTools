using System.Net;
using System.Text;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmTestUtils.Logging;
using CodeReviewDaemon.Sample.Orchestration;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace CodeReviewDaemon.Sample.Tests.Orchestration;

/// <summary>
/// Issue #647 — the real <see cref="GitHubIssueContextReader"/> reads a PR's linked issues (and each
/// issue's related PRs) from the GitHub GraphQL API. These tests drive it against a scripted HTTP handler
/// (no network): the four-outcome distinction (<see cref="GitHubIssueLookup"/>), the GraphQL-over-HTTP-200
/// error shape, exhaustive cursor pagination of <c>closingIssuesReferences</c>, and the
/// <c>Truncated</c> cap semantics.
/// </summary>
public sealed class GitHubIssueContextReaderTests : LoggingTestBase
{
    public GitHubIssueContextReaderTests(ITestOutputHelper output)
        : base(output) { }

    private static readonly RepoIdentity Repo = new()
    {
        Provider = "github",
        OrgOrOwner = "acme",
        RepoName = "widgets",
        RepoStableId = "R_node_123",
    };

    private static readonly RepoIdentity AdoRepo = new()
    {
        Provider = "azure-devops",
        OrgOrOwner = "contoso",
        Project = "Platform",
        RepoName = "core",
    };

    private GitHubIssueContextReader Reader(FakeHttpMessageHandler handler) =>
        new(
            new HttpClient(handler),
            new FakeOAuthTokenProvider("github", "gh-token-xyz"),
            LoggerFactory.CreateLogger<GitHubIssueContextReader>()
        );

    /// <summary>Reads the outgoing GraphQL request body synchronously — the handler already buffered it
    /// (it is a fully-materialized <see cref="StringContent"/>), so re-reading here cannot deadlock or
    /// consume a stream twice.</summary>
    private static string RequestBody(HttpRequestMessage request) =>
        request.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? string.Empty;

    private static HttpResponseMessage JsonResponse(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static bool IsGraphQlPost(HttpRequestMessage request) =>
        request.Method == HttpMethod.Post
        && request.RequestUri is not null
        && request.RequestUri.ToString().Contains("/graphql", StringComparison.Ordinal);

    private static object IssueNode(
        int number,
        string title = "Fix the thing",
        string state = "OPEN",
        string repo = "acme/widgets",
        object[]? relatedPrs = null
    ) =>
        new
        {
            number,
            url = $"https://github.com/{repo}/issues/{number}",
            title,
            state,
            repository = new { nameWithOwner = repo },
            closedByPullRequestsReferences = new { nodes = relatedPrs ?? [] },
        };

    private static object RelatedPrNode(int number, string repo = "acme/widgets") =>
        new
        {
            number,
            url = $"https://github.com/{repo}/pull/{number}",
            repository = new { nameWithOwner = repo },
        };

    private static string GraphQlResponse(IEnumerable<object> nodes, bool hasNextPage, string? endCursor) =>
        JsonSerializer.Serialize(
            new
            {
                data = new
                {
                    repository = new
                    {
                        pullRequest = new
                        {
                            closingIssuesReferences = new
                            {
                                pageInfo = new { hasNextPage, endCursor },
                                nodes = nodes.ToArray(),
                            },
                        },
                    },
                },
            }
        );

    [Fact]
    public async Task ReadAsync_returns_Unavailable_for_a_non_github_repo_and_makes_no_request()
    {
        var handler = new FakeHttpMessageHandler();

        var result = await Reader(handler).ReadAsync(AdoRepo, "7", CancellationToken.None);

        result.Outcome.Should().Be(GitHubIssueLookup.Unavailable);
        handler.Requests.Should().BeEmpty("nobody attempted anything, so no HTTP call should ever fire");
    }

    [Fact]
    public async Task ReadAsync_returns_Failed_on_a_non_success_http_status()
    {
        var handler = new FakeHttpMessageHandler().On(
            IsGraphQlPost,
            _ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        );

        var result = await Reader(handler).ReadAsync(Repo, "7", CancellationToken.None);

        result.Outcome.Should().Be(GitHubIssueLookup.Failed);
        result.Issues.Should().BeEmpty();
    }

    [Fact]
    public async Task ReadAsync_returns_Failed_when_the_graphql_response_carries_a_top_level_error()
    {
        var body = JsonSerializer.Serialize(
            new
            {
                data = (object?)null,
                errors = new[] { new { type = "NOT_FOUND", message = "Could not resolve to a PullRequest" } },
            }
        );
        var handler = new FakeHttpMessageHandler().On(IsGraphQlPost, _ => JsonResponse(body));

        var result = await Reader(handler).ReadAsync(Repo, "7", CancellationToken.None);

        result.Outcome.Should().Be(GitHubIssueLookup.Failed);
    }

    [Fact]
    public async Task ReadAsync_returns_NoneLinked_when_the_pr_closes_no_issues()
    {
        var body = GraphQlResponse([], hasNextPage: false, endCursor: null);
        var handler = new FakeHttpMessageHandler().On(IsGraphQlPost, _ => JsonResponse(body));

        var result = await Reader(handler).ReadAsync(Repo, "7", CancellationToken.None);

        result.Outcome.Should().Be(GitHubIssueLookup.NoneLinked);
        result.Issues.Should().BeEmpty();
        result.Truncated.Should().BeFalse();
    }

    [Fact]
    public async Task ReadAsync_maps_linked_issues_and_their_related_pull_requests()
    {
        var body = GraphQlResponse(
            [
                IssueNode(
                    42,
                    title: "Add widget support",
                    state: "OPEN",
                    relatedPrs: [RelatedPrNode(7), RelatedPrNode(9)]
                ),
            ],
            hasNextPage: false,
            endCursor: null
        );
        var handler = new FakeHttpMessageHandler().On(IsGraphQlPost, _ => JsonResponse(body));

        var result = await Reader(handler).ReadAsync(Repo, "7", CancellationToken.None);

        result.Outcome.Should().Be(GitHubIssueLookup.Linked);
        result.Issues.Should().HaveCount(1);
        var issue = result.Issues[0];
        issue.Number.Should().Be(42);
        issue.Repository.Should().Be("acme/widgets");
        issue.Title.Should().Be("Add widget support");
        issue.State.Should().Be("OPEN");
        issue.Url.Should().Be("https://github.com/acme/widgets/issues/42");
        issue.RelatedPullRequests.Should().HaveCount(2);
        issue.RelatedPullRequests[0].Number.Should().Be(7);
        issue.RelatedPullRequests[1].Number.Should().Be(9);
    }

    [Fact]
    public async Task ReadAsync_walks_every_cursor_page_and_does_not_truncate_when_everything_fits()
    {
        var page1 = GraphQlResponse([IssueNode(1), IssueNode(2)], hasNextPage: true, endCursor: "cursor-1");
        var page2 = GraphQlResponse([IssueNode(3)], hasNextPage: false, endCursor: null);

        var handler = new FakeHttpMessageHandler()
            .On(
                req => IsGraphQlPost(req) && RequestBody(req).Contains("\"after\":null", StringComparison.Ordinal),
                _ => JsonResponse(page1)
            )
            .On(
                req => IsGraphQlPost(req) && RequestBody(req).Contains("cursor-1", StringComparison.Ordinal),
                _ => JsonResponse(page2)
            );

        var result = await Reader(handler).ReadAsync(Repo, "7", CancellationToken.None);

        result.Outcome.Should().Be(GitHubIssueLookup.Linked);
        result.Truncated.Should().BeFalse();
        result.Issues.Select(i => i.Number).Should().BeEquivalentTo([1, 2, 3], o => o.WithStrictOrdering());
    }

    [Fact]
    public async Task ReadAsync_sets_Truncated_when_the_cap_is_reached_with_more_pages_still_pending()
    {
        // MaxIssues == 2 * PageSize: two full pages exactly fill the cap while the server still reports
        // hasNextPage — the boundary case that distinguishes "the cap happened to land on a page edge" from
        // "there was really nothing left".
        var page1Nodes = Enumerable.Range(1, GitHubIssueContextReader.PageSize).Select(n => IssueNode(n)).ToArray();
        var page2Nodes = Enumerable
            .Range(GitHubIssueContextReader.PageSize + 1, GitHubIssueContextReader.PageSize)
            .Select(n => IssueNode(n))
            .ToArray();
        var page1 = GraphQlResponse(page1Nodes, hasNextPage: true, endCursor: "cursor-1");
        var page2 = GraphQlResponse(page2Nodes, hasNextPage: true, endCursor: "cursor-2");

        var handler = new FakeHttpMessageHandler()
            .On(
                req => IsGraphQlPost(req) && RequestBody(req).Contains("\"after\":null", StringComparison.Ordinal),
                _ => JsonResponse(page1)
            )
            .On(
                req => IsGraphQlPost(req) && RequestBody(req).Contains("cursor-1", StringComparison.Ordinal),
                _ => JsonResponse(page2)
            );

        var result = await Reader(handler).ReadAsync(Repo, "7", CancellationToken.None);

        result.Outcome.Should().Be(GitHubIssueLookup.Linked);
        result.Issues.Should().HaveCount(GitHubIssueContextReader.MaxIssues);
        result.Truncated.Should().BeTrue();
        handler.CountRequests("/graphql").Should().Be(2, "the walk must stop at the cap, not keep paging forever");
    }
}
