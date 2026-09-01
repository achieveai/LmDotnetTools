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
/// error shape, exhaustive cursor pagination of <c>closingIssuesReferences</c>, the <c>Truncated</c> cap
/// semantics, the cursor-progress guard, the exact request/query contract, and the deterministic GraphQL
/// node identity.
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

    /// <summary>Same as <see cref="Repo"/> but with a differently-cased provider — pins that the provider
    /// check is exact/ordinal (matching <c>RepoIdentity.ToPublisherNamespace</c>'s own convention), not a
    /// case-insensitive match.</summary>
    private static readonly RepoIdentity DifferentlyCasedRepo = new()
    {
        Provider = "GitHub",
        OrgOrOwner = "acme",
        RepoName = "widgets",
        RepoStableId = "R_node_123",
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
        object[]? relatedPrs = null,
        string? id = null
    ) =>
        new
        {
            id = id ?? $"I_{number}",
            number,
            url = $"https://github.com/{repo}/issues/{number}",
            title,
            state,
            repository = new { nameWithOwner = repo },
            closedByPullRequestsReferences = new { nodes = relatedPrs ?? [] },
        };

    /// <summary>An issue node with no <c>id</c> field at all — GitHub's GraphQL identity is the one field
    /// this reader treats as mandatory (see <c>ParseLinkedIssue</c>), so this is what a genuinely
    /// unparseable node looks like.</summary>
    private static object IssueNodeMissingId(int number, string repo = "acme/widgets") =>
        new
        {
            number,
            url = $"https://github.com/{repo}/issues/{number}",
            title = "No id",
            state = "OPEN",
            repository = new { nameWithOwner = repo },
            closedByPullRequestsReferences = new { nodes = Array.Empty<object>() },
        };

    private static object RelatedPrNode(int number, string repo = "acme/widgets", string? id = null) =>
        new
        {
            id = id ?? $"PR_{number}",
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
    public async Task ReadAsync_returns_Unavailable_for_a_differently_cased_provider_value()
    {
        var handler = new FakeHttpMessageHandler();

        var result = await Reader(handler).ReadAsync(DifferentlyCasedRepo, "7", CancellationToken.None);

        result
            .Outcome.Should()
            .Be(
                GitHubIssueLookup.Unavailable,
                "provider comparison is exact/ordinal, matching RepoIdentity.ToPublisherNamespace's own convention"
            );
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ReadAsync_returns_Failed_for_a_nonnumeric_pr_id_instead_of_throwing()
    {
        var handler = new FakeHttpMessageHandler();

        var result = await Reader(handler).ReadAsync(Repo, "not-a-number", CancellationToken.None);

        result.Outcome.Should().Be(GitHubIssueLookup.Failed);
        handler.Requests.Should().BeEmpty("parsing fails before any request is attempted");
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
    public async Task ReadAsync_returns_Failed_when_the_response_carries_partial_errors_alongside_data()
    {
        // GitHub's GraphQL errors are not all-or-nothing — a response can carry a populated "data" tree
        // AND a non-empty "errors" array in the same HTTP 200 (a partial/field-level failure). Fail-closed
        // to Failed here too: there is no new "Partial" outcome, and the brief must not render a linked-
        // issues list that GitHub itself flagged as incomplete.
        var body = JsonSerializer.Serialize(
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
                                pageInfo = new { hasNextPage = false, endCursor = (string?)null },
                                nodes = new[] { IssueNode(1) },
                            },
                        },
                    },
                },
                errors = new[] { new { type = "SOME_FIELD_ERROR", message = "partial failure" } },
            }
        );
        var handler = new FakeHttpMessageHandler().On(IsGraphQlPost, _ => JsonResponse(body));

        var result = await Reader(handler).ReadAsync(Repo, "7", CancellationToken.None);

        result
            .Outcome.Should()
            .Be(
                GitHubIssueLookup.Failed,
                "fail-closed on any GraphQL error, even alongside partial data — there is no new Partial state"
            );
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
                    relatedPrs: [RelatedPrNode(7), RelatedPrNode(9)],
                    id: "I_kwDO_42"
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
        issue.NodeId.Should().Be("I_kwDO_42");
        issue.Number.Should().Be(42);
        issue.Repository.Should().Be("acme/widgets");
        issue.Title.Should().Be("Add widget support");
        issue.State.Should().Be("OPEN");
        issue.Url.Should().Be("https://github.com/acme/widgets/issues/42");
        issue.RelatedPullRequests.Should().HaveCount(2);
        issue.RelatedPullRequests[0].NodeId.Should().Be("PR_7");
        issue.RelatedPullRequests[0].Number.Should().Be(7);
        issue.RelatedPullRequests[1].NodeId.Should().Be("PR_9");
        issue.RelatedPullRequests[1].Number.Should().Be(9);
    }

    [Fact]
    public async Task ReadAsync_maps_a_related_pull_request_from_a_different_repository()
    {
        // GitHub's closingIssuesReferences can name a related PR living in a different repository than the
        // issue itself (e.g. a fork or a split monorepo) — the cross-repo identity must survive untouched.
        var body = GraphQlResponse(
            [IssueNode(42, relatedPrs: [RelatedPrNode(99, repo: "acme/other-repo", id: "PR_cross_99")])],
            hasNextPage: false,
            endCursor: null
        );
        var handler = new FakeHttpMessageHandler().On(IsGraphQlPost, _ => JsonResponse(body));

        var result = await Reader(handler).ReadAsync(Repo, "7", CancellationToken.None);

        result.Outcome.Should().Be(GitHubIssueLookup.Linked);
        var relatedPr = result.Issues[0].RelatedPullRequests.Should().ContainSingle().Subject;
        relatedPr.NodeId.Should().Be("PR_cross_99");
        relatedPr.Repository.Should().Be("acme/other-repo");
        relatedPr.Url.Should().Be("https://github.com/acme/other-repo/pull/99");
    }

    [Fact]
    public async Task ReadAsync_condenses_newlines_and_truncates_an_overlong_title()
    {
        var longTitle = new string('a', GitHubIssueContextReader.MaxTitleChars + 50);
        var titleWithNewlines = "Line one\nLine two\r\nLine three";
        var body = GraphQlResponse(
            [IssueNode(1, title: titleWithNewlines), IssueNode(2, title: longTitle)],
            hasNextPage: false,
            endCursor: null
        );
        var handler = new FakeHttpMessageHandler().On(IsGraphQlPost, _ => JsonResponse(body));

        var result = await Reader(handler).ReadAsync(Repo, "7", CancellationToken.None);

        result.Issues[0].Title.Should().Be("Line one Line two Line three");
        result.Issues[1].Title.Should().HaveLength(GitHubIssueContextReader.MaxTitleChars);
        result.Issues[1].Title.Should().EndWith("…");
    }

    [Fact]
    public async Task ReadAsync_sends_exact_graphql_variables_and_query_contract()
    {
        var body = GraphQlResponse([], hasNextPage: false, endCursor: null);
        var handler = new FakeHttpMessageHandler().On(IsGraphQlPost, _ => JsonResponse(body));

        await Reader(handler).ReadAsync(Repo, "7", CancellationToken.None);

        var recorded = handler.Requests.Should().ContainSingle().Subject;
        using var requestJson = JsonDocument.Parse(recorded.Body!);
        var variables = requestJson.RootElement.GetProperty("variables");
        variables.GetProperty("owner").GetString().Should().Be("acme");
        variables.GetProperty("repo").GetString().Should().Be("widgets");
        variables.GetProperty("number").GetInt32().Should().Be(7);
        variables.GetProperty("pageSize").GetInt32().Should().Be(GitHubIssueContextReader.PageSize);
        variables.GetProperty("after").ValueKind.Should().Be(JsonValueKind.Null);

        // Whitespace-normalized so CSharpier's own reformatting of the raw query string literal cannot
        // break this pin — the field ORDER and the orderBy clause are the contract under test, not the
        // literal indentation.
        var query = requestJson.RootElement.GetProperty("query").GetString()!;
        var normalizedQuery = string.Join(' ', query.Split(['\n', '\r', ' '], StringSplitOptions.RemoveEmptyEntries));
        normalizedQuery
            .Should()
            .Contain(
                "orderBy: { field: CREATED_AT, direction: ASC }",
                "walking pages in order must not rest on GitHub's unspecified connection default"
            );
        normalizedQuery
            .Should()
            .Contain(
                "nodes { id number url title state",
                "each linked-issue node must select GitHub's own GraphQL id, not just (repository, number)"
            );
        normalizedQuery
            .Should()
            .Contain(
                "nodes { id number url repository { nameWithOwner } } }",
                "each related-PR node must also select GitHub's own GraphQL id"
            );
    }

    [Fact]
    public async Task ReadAsync_rethrows_a_real_caller_cancellation_instead_of_reporting_Failed()
    {
        using var cts = new CancellationTokenSource();
        var handler = new FakeHttpMessageHandler().On(
            IsGraphQlPost,
            _ =>
            {
                cts.Cancel();
                throw new OperationCanceledException(cts.Token);
            }
        );

        var act = () => Reader(handler).ReadAsync(Repo, "7", cts.Token);

        await act.Should()
            .ThrowAsync<OperationCanceledException>(
                "an abandoned review must propagate the cancellation, not silently report Failed"
            );
    }

    [Fact]
    public async Task ReadAsync_returns_Failed_when_a_page_mixes_a_parseable_and_an_unparseable_node()
    {
        var body = GraphQlResponse([IssueNode(1), IssueNodeMissingId(2)], hasNextPage: false, endCursor: null);
        var handler = new FakeHttpMessageHandler().On(IsGraphQlPost, _ => JsonResponse(body));

        var result = await Reader(handler).ReadAsync(Repo, "7", CancellationToken.None);

        result
            .Outcome.Should()
            .Be(
                GitHubIssueLookup.Failed,
                "one unparseable node in the page means the lookup could not be completed, not that it partially succeeded"
            );
        result.Issues.Should().BeEmpty();
    }

    [Fact]
    public async Task ReadAsync_returns_Failed_when_every_node_on_a_non_empty_page_is_unparseable()
    {
        var body = GraphQlResponse([IssueNodeMissingId(1), IssueNodeMissingId(2)], hasNextPage: false, endCursor: null);
        var handler = new FakeHttpMessageHandler().On(IsGraphQlPost, _ => JsonResponse(body));

        var result = await Reader(handler).ReadAsync(Repo, "7", CancellationToken.None);

        result.Outcome.Should().Be(GitHubIssueLookup.Failed);
    }

    [Fact]
    public async Task ReadAsync_truncates_mid_page_when_a_single_page_exceeds_the_cap_on_its_own()
    {
        // A page reporting more nodes than the cap in one shot is nonconformant (a well-behaved server
        // honors "first: pageSize"), but the walk must still cap in place rather than trust the count.
        var oversizedNodes = Enumerable
            .Range(1, GitHubIssueContextReader.MaxIssues + 5)
            .Select(n => IssueNode(n))
            .ToArray();
        var body = GraphQlResponse(oversizedNodes, hasNextPage: false, endCursor: null);
        var handler = new FakeHttpMessageHandler().On(IsGraphQlPost, _ => JsonResponse(body));

        var result = await Reader(handler).ReadAsync(Repo, "7", CancellationToken.None);

        result.Outcome.Should().Be(GitHubIssueLookup.Linked);
        result.Issues.Should().HaveCount(GitHubIssueContextReader.MaxIssues);
        result.Truncated.Should().BeTrue();
        handler.Requests.Should().HaveCount(1, "a single oversized page must cap in place, not fetch again");
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
    public async Task ReadAsync_folds_a_cross_page_repeat_of_the_same_issue_into_one_entry_without_consuming_the_cap()
    {
        // Issue #647 item 7: a duplicate-heavy first page (here, MaxIssues copies of the SAME issue) must
        // fold to one entry AND must not be allowed to look "full" — the walk still has to reach the second
        // page's genuinely new issue instead of truncating on nothing but repeats.
        var duplicateNodes = Enumerable
            .Range(0, GitHubIssueContextReader.MaxIssues)
            .Select(_ => IssueNode(1))
            .ToArray();
        var page1 = GraphQlResponse(duplicateNodes, hasNextPage: true, endCursor: "cursor-1");
        var page2 = GraphQlResponse([IssueNode(2)], hasNextPage: false, endCursor: null);

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
        result.Truncated.Should().BeFalse("MaxIssues copies of the same issue must not consume the cap");
        result.Issues.Select(i => i.Number).Should().BeEquivalentTo([1, 2], o => o.WithStrictOrdering());
        handler
            .CountRequests("/graphql")
            .Should()
            .Be(2, "a duplicate-filled first page must not look full, so the walk must still fetch the next page");
    }

    [Fact]
    public async Task ReadAsync_folds_an_in_page_repeat_of_the_same_issue_without_dropping_the_issue_after_it()
    {
        // Issue #647 item 7: distinguishes the duplicate branch's "continue" (skip only the repeated node,
        // keep walking the rest of THIS page) from a "break" (abort the whole page's remaining nodes). A
        // single page of [A, exact duplicate of A, B] with hasNextPage:false is the minimal case where only
        // "continue" reaches B — "break" would silently drop it, even though nothing here is a next-page
        // fetch or a cap truncation.
        var body = GraphQlResponse(
            [IssueNode(1, id: "I_1"), IssueNode(1, id: "I_1"), IssueNode(2, id: "I_2")],
            hasNextPage: false,
            endCursor: null
        );
        var handler = new FakeHttpMessageHandler().On(IsGraphQlPost, _ => JsonResponse(body));

        var result = await Reader(handler).ReadAsync(Repo, "7", CancellationToken.None);

        result.Outcome.Should().Be(GitHubIssueLookup.Linked);
        result.Truncated.Should().BeFalse("two distinct issues on one page never approaches the cap");
        result
            .Issues.Select(i => i.Number)
            .Should()
            .BeEquivalentTo(
                [1, 2],
                o => o.WithStrictOrdering(),
                "the duplicate of A must fold away without consuming B, which sits right after it in the same page"
            );
    }

    [Fact]
    public async Task ReadAsync_returns_Failed_when_the_same_repository_and_number_disagree_on_node_id()
    {
        // Same (repository, number) pair reported with two different GraphQL node ids is an identity
        // disagreement nothing here can resolve — Failed is the only honest outcome, never a silent pick.
        var body = GraphQlResponse(
            [IssueNode(1, id: "I_1"), IssueNode(1, id: "I_1_different")],
            hasNextPage: false,
            endCursor: null
        );
        var handler = new FakeHttpMessageHandler().On(IsGraphQlPost, _ => JsonResponse(body));

        var result = await Reader(handler).ReadAsync(Repo, "7", CancellationToken.None);

        result.Outcome.Should().Be(GitHubIssueLookup.Failed);
    }

    [Fact]
    public async Task ReadAsync_does_not_collapse_the_same_issue_number_across_different_repositories()
    {
        // The dedup key is (repository, number), not number alone — two repositories that happen to share an
        // issue number are two distinct issues, not a duplicate.
        var body = GraphQlResponse(
            [IssueNode(1, repo: "acme/widgets", id: "I_1"), IssueNode(1, repo: "acme/gadgets", id: "I_1_other")],
            hasNextPage: false,
            endCursor: null
        );
        var handler = new FakeHttpMessageHandler().On(IsGraphQlPost, _ => JsonResponse(body));

        var result = await Reader(handler).ReadAsync(Repo, "7", CancellationToken.None);

        result.Outcome.Should().Be(GitHubIssueLookup.Linked);
        result.Issues.Should().HaveCount(2);
    }

    [Fact]
    public async Task ReadAsync_reports_Failed_for_an_operationcanceledexception_not_tied_to_the_callers_own_token()
    {
        // An internal timeout (e.g. an HttpClient-owned linked CTS) can surface as OperationCanceledException
        // even though the caller's own token was never cancelled. Only the caller's OWN token being cancelled
        // means "the review was abandoned" (see the sibling test above); anything else is a failed attempt.
        var handler = new FakeHttpMessageHandler().On(
            IsGraphQlPost,
            _ => throw new OperationCanceledException("unrelated internal timeout")
        );

        var result = await Reader(handler).ReadAsync(Repo, "7", CancellationToken.None);

        result.Outcome.Should().Be(GitHubIssueLookup.Failed, "the caller's own token was never cancelled");
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

    [Fact]
    public async Task ReadAsync_returns_Failed_when_the_first_page_stalls_with_no_end_cursor_and_nothing_was_confirmed()
    {
        // The very first page claims more pages exist but hands back no cursor to ask for them with. Its
        // own data is untrusted along with the broken pagination signal, so with nothing confirmed from an
        // earlier page, this is Failed — never a silent NoneLinked and never an infinite retry.
        var body = GraphQlResponse([IssueNode(1)], hasNextPage: true, endCursor: null);
        var handler = new FakeHttpMessageHandler().On(IsGraphQlPost, _ => JsonResponse(body));

        var result = await Reader(handler).ReadAsync(Repo, "7", CancellationToken.None);

        result.Outcome.Should().Be(GitHubIssueLookup.Failed);
        handler.Requests.Should().HaveCount(1, "the walk must stop, not retry the same broken page forever");
    }

    [Fact]
    public async Task ReadAsync_truncates_instead_of_looping_when_a_later_page_has_no_end_cursor_but_claims_more()
    {
        var page1 = GraphQlResponse([IssueNode(1)], hasNextPage: true, endCursor: "cursor-1");
        var page2 = GraphQlResponse([IssueNode(2)], hasNextPage: true, endCursor: null);

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
        result.Truncated.Should().BeTrue();
        result.Issues.Select(i => i.Number).Should().BeEquivalentTo([1]);
        handler.Requests.Should().HaveCount(2, "the walk must stop at the broken page, not retry it forever");
    }

    [Fact]
    public async Task ReadAsync_truncates_instead_of_looping_when_the_cursor_repeats()
    {
        var page1 = GraphQlResponse([IssueNode(1)], hasNextPage: true, endCursor: "cursor-1");
        // Same cursor handed back again — a server that never actually advances.
        var page2 = GraphQlResponse([IssueNode(2)], hasNextPage: true, endCursor: "cursor-1");

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
        result.Truncated.Should().BeTrue();
        result.Issues.Select(i => i.Number).Should().BeEquivalentTo([1]);
        handler.Requests.Should().HaveCount(2, "a repeated cursor must stop the walk, not spin forever");
    }

    [Fact]
    public async Task ReadAsync_stops_at_the_absolute_page_request_bound_even_though_each_page_makes_nominal_progress()
    {
        // Every page hands back a genuinely NEW cursor (so the no-progress guard above never fires) and
        // parses cleanly, yet the server would need far more than MaxPageRequests pages to ever reach
        // MaxIssues at one issue per page. Only the absolute, item-count-independent bound can stop this.
        var page1 = GraphQlResponse([IssueNode(1)], hasNextPage: true, endCursor: "c1");
        var page2 = GraphQlResponse([IssueNode(2)], hasNextPage: true, endCursor: "c2");
        var page3 = GraphQlResponse([IssueNode(3)], hasNextPage: true, endCursor: "c3");

        var handler = new FakeHttpMessageHandler()
            .On(
                req => IsGraphQlPost(req) && RequestBody(req).Contains("\"after\":null", StringComparison.Ordinal),
                _ => JsonResponse(page1)
            )
            .On(
                req => IsGraphQlPost(req) && RequestBody(req).Contains("\"after\":\"c1\"", StringComparison.Ordinal),
                _ => JsonResponse(page2)
            )
            .On(
                req => IsGraphQlPost(req) && RequestBody(req).Contains("\"after\":\"c2\"", StringComparison.Ordinal),
                _ => JsonResponse(page3)
            );

        var result = await Reader(handler).ReadAsync(Repo, "7", CancellationToken.None);

        result.Outcome.Should().Be(GitHubIssueLookup.Linked);
        result.Truncated.Should().BeTrue();
        result.Issues.Select(i => i.Number).Should().BeEquivalentTo([1, 2, 3]);
        handler
            .Requests.Should()
            .HaveCount(
                (GitHubIssueContextReader.MaxIssues / GitHubIssueContextReader.PageSize) + 1,
                "the absolute page-request bound must stop the walk even though the server keeps nominally advancing"
            );
    }
}
