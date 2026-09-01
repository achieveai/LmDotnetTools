using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmTestUtils.Logging;
using CodeReviewDaemon.Sample.Configuration;
using CodeReviewDaemon.Sample.Orchestration;
using CodeReviewDaemon.Sample.Persistence;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using CodeReviewDaemon.Sample.Workspace;
using Microsoft.Data.Sqlite;
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

    /// <summary>
    /// Builds a reader through the REAL <see cref="PolicyEnforcedHttpClientFactory"/> /
    /// <see cref="OperationPolicyHandler"/> pipeline production uses, backed by a REAL <see cref="ReviewStore"/>
    /// (a throwaway on-disk SQLite database) — not a bare fake handler directly on an <see cref="HttpClient"/>,
    /// and not a mocked store. <see cref="GitHubIssueContextReader.ReadAsync"/> derives its scope from a
    /// persisted <see cref="ReviewRun"/> and calls <see cref="PolicyEnforcedHttpClientFactory.CreateForGitHubGraphQl"/>
    /// itself, per call, so every test here exercises the same run-identity-bound canonical-scope binding
    /// production wiring does; the constructor's <c>handler</c> only replaces the innermost socket handler.
    /// </summary>
    private sealed class Harness : IDisposable
    {
        private readonly TempSqliteDatabase _db = new();

        public ReviewStore Store { get; }
        public FakeOAuthTokenProvider Tokens { get; } = new("github", "gh-token-xyz");
        public GitHubIssueContextReader Reader { get; }

        public Harness(FakeHttpMessageHandler handler, ILoggerFactory loggerFactory)
        {
            Store = new ReviewStore(_db.ConnectionString);
            var factory = new PolicyEnforcedHttpClientFactory(
                new CodeReviewDaemonOptions
                {
                    EnabledRepos = ["acme/widgets", "acme/gadgets"],
                    EnableCommentPosting = true,
                },
                loggerFactory.CreateLogger<OperationPolicyHandler>(),
                loggerFactory.CreateLogger<RetryHandler>(),
                innerHandlerFactory: () => handler
            );
            Reader = new GitHubIssueContextReader(
                factory,
                Store,
                Tokens,
                loggerFactory.CreateLogger<GitHubIssueContextReader>()
            );
        }

        /// <summary>Persists <paramref name="repo"/> (via <see cref="ReviewStore.EnsureRepo"/>) plus a
        /// <see cref="ReviewRun"/> naming it and <paramref name="prId"/>, and returns the run's id — the only
        /// handle a caller now has to reach either persisted value.</summary>
        public long SeedRun(RepoIdentity repo, string prId, string headSha = "head-sha") =>
            SeedRunForRepoId(Store.EnsureRepo(repo), prId, headSha);

        /// <summary>Seeds a run whose <see cref="ReviewRun.RepoId"/> is then made to point at a repo row
        /// that no longer exists. <see cref="ReviewStore"/>'s own connection enforces
        /// <c>PRAGMA foreign_keys = ON</c> (see <see cref="Persistence.SqliteConnectionFactory"/>), so it
        /// cannot itself insert or delete across a dangling reference — a fresh repo is created and a valid
        /// run seeded against it first. The repo row is then removed through a SEPARATE raw connection to
        /// the same file with <c>foreign_keys</c> explicitly turned off on THAT connection only: SQLite
        /// enforces the pragma per-connection at write time, not globally, so this delete succeeds while
        /// leaving the run's already-committed <c>repo_id</c> dangling — reproducing "the run's repo is
        /// gone" without asking <see cref="ReviewStore"/>'s own connection to violate its own
        /// constraint.</summary>
        public long SeedRunWithMissingRepo(string prId, string headSha = "head-sha")
        {
            var repoId = Store.EnsureRepo(
                new RepoIdentity
                {
                    Provider = "github",
                    OrgOrOwner = "ghost-org",
                    RepoName = "vanished-repo",
                }
            );
            var runId = SeedRunForRepoId(repoId, prId, headSha);
            DeleteRepoRow(repoId);
            return runId;
        }

        private void DeleteRepoRow(long repoId)
        {
            using var connection = new SqliteConnection(_db.ConnectionString);
            connection.Open();
            // This build's default differs from stock SQLite: explicitly turn enforcement off on THIS
            // connection only — Store's own connection (opened earlier, PRAGMA foreign_keys = ON) is
            // unaffected, since the pragma is scoped per-connection.
            using (var pragma = connection.CreateCommand())
            {
                pragma.CommandText = "PRAGMA foreign_keys = OFF;";
                _ = pragma.ExecuteNonQuery();
            }

            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM repo WHERE id = $id;";
            _ = command.Parameters.AddWithValue("$id", repoId);
            _ = command.ExecuteNonQuery();
        }

        private long SeedRunForRepoId(long repoId, string prId, string headSha) =>
            Store
                .CreateOrGetReviewRun(
                    new ReviewRun
                    {
                        RepoId = repoId,
                        PrId = prId,
                        HeadSha = headSha,
                        BaseSha = "base-sha",
                        TriggerWatermark = "2026-06-29T12:34:56Z",
                        ReviewKind = "full",
                        VariantId = "primary",
                        Mode = "collect-only",
                        Stage = ReviewStage.Discovered,
                        WorkflowStatus = WorkflowStatus.Running,
                        PrLifecycleState = PrLifecycleState.Open,
                    }
                )
                .Id;

        public void Dispose()
        {
            Store.Dispose();
            _db.Dispose();
        }
    }

    private Harness NewHarness(FakeHttpMessageHandler handler) => new(handler, LoggerFactory);

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

    /// <summary>
    /// Production-path integration proof: this file's <see cref="Harness"/> already builds the real
    /// <see cref="PolicyEnforcedHttpClientFactory"/> / <see cref="OperationPolicyHandler"/> pipeline for
    /// every test in this file — this test asserts that pipeline actually completes a successful read
    /// with the credential intact, not merely that it compiles into the wiring.
    /// </summary>
    [Fact]
    public async Task ReadAsync_completes_successfully_through_the_real_PolicyEnforcedHttpClientFactory_pipeline()
    {
        var handler = new FakeHttpMessageHandler().On(
            IsGraphQlPost,
            _ => JsonResponse(GraphQlResponse([IssueNode(1)], hasNextPage: false, endCursor: null))
        );

        using var harness = NewHarness(handler);
        var runId = harness.SeedRun(Repo, "7");
        var result = await harness.Reader.ReadAsync(runId, CancellationToken.None);

        result.Outcome.Should().Be(GitHubIssueLookup.Linked);
        result.Issues.Should().ContainSingle();
        handler.Requests.Should().ContainSingle();
        handler.Requests[0].Authorization.Should().Be("Bearer gh-token-xyz");
    }

    [Fact]
    public async Task ReadAsync_returns_Unavailable_for_a_non_github_repo_and_makes_no_request()
    {
        var handler = new FakeHttpMessageHandler();
        using var harness = NewHarness(handler);
        var runId = harness.SeedRun(AdoRepo, "7");

        var result = await harness.Reader.ReadAsync(runId, CancellationToken.None);

        result.Outcome.Should().Be(GitHubIssueLookup.Unavailable);
        handler.Requests.Should().BeEmpty("nobody attempted anything, so no HTTP call should ever fire");
        harness.Tokens.IssuedTokens.Should().BeEmpty("a non-GitHub repo must not even reach the token provider");
    }

    [Fact]
    public async Task ReadAsync_returns_Unavailable_for_a_differently_cased_provider_value()
    {
        var handler = new FakeHttpMessageHandler();
        using var harness = NewHarness(handler);
        var runId = harness.SeedRun(DifferentlyCasedRepo, "7");

        var result = await harness.Reader.ReadAsync(runId, CancellationToken.None);

        result
            .Outcome.Should()
            .Be(
                GitHubIssueLookup.Unavailable,
                "provider comparison is exact/ordinal, matching RepoIdentity.ToPublisherNamespace's own convention"
            );
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ReadAsync_returns_Unavailable_for_a_persisted_nonnumeric_pr_id_instead_of_throwing()
    {
        var handler = new FakeHttpMessageHandler();
        using var harness = NewHarness(handler);
        var runId = harness.SeedRun(Repo, "not-a-number");

        var result = await harness.Reader.ReadAsync(runId, CancellationToken.None);

        result
            .Outcome.Should()
            .Be(
                GitHubIssueLookup.Unavailable,
                "an unparseable persisted PrId is a precondition nothing was asked about, not a failed attempt"
            );
        handler.Requests.Should().BeEmpty("parsing fails before any request is attempted");
        harness.Tokens.IssuedTokens.Should().BeEmpty();
    }

    [Fact]
    public async Task ReadAsync_returns_Unavailable_for_a_nonpositive_persisted_pr_id()
    {
        var handler = new FakeHttpMessageHandler();
        using var harness = NewHarness(handler);
        var runId = harness.SeedRun(Repo, "0");

        var result = await harness.Reader.ReadAsync(runId, CancellationToken.None);

        result.Outcome.Should().Be(GitHubIssueLookup.Unavailable, "a PR number cannot be zero or negative");
        handler.Requests.Should().BeEmpty();
        harness.Tokens.IssuedTokens.Should().BeEmpty();
    }

    [Fact]
    public async Task ReadAsync_returns_Failed_on_a_non_success_http_status()
    {
        var handler = new FakeHttpMessageHandler().On(
            IsGraphQlPost,
            _ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        );

        using var harness = NewHarness(handler);
        var runId = harness.SeedRun(Repo, "7");
        var result = await harness.Reader.ReadAsync(runId, CancellationToken.None);

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

        using var harness = NewHarness(handler);
        var runId = harness.SeedRun(Repo, "7");
        var result = await harness.Reader.ReadAsync(runId, CancellationToken.None);

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

        using var harness = NewHarness(handler);
        var runId = harness.SeedRun(Repo, "7");
        var result = await harness.Reader.ReadAsync(runId, CancellationToken.None);

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

        using var harness = NewHarness(handler);
        var runId = harness.SeedRun(Repo, "7");
        var result = await harness.Reader.ReadAsync(runId, CancellationToken.None);

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

        using var harness = NewHarness(handler);
        var runId = harness.SeedRun(Repo, "7");
        var result = await harness.Reader.ReadAsync(runId, CancellationToken.None);

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

        using var harness = NewHarness(handler);
        var runId = harness.SeedRun(Repo, "7");
        var result = await harness.Reader.ReadAsync(runId, CancellationToken.None);

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

        using var harness = NewHarness(handler);
        var runId = harness.SeedRun(Repo, "7");
        var result = await harness.Reader.ReadAsync(runId, CancellationToken.None);

        result.Issues[0].Title.Should().Be("Line one Line two Line three");
        result.Issues[1].Title.Should().HaveLength(GitHubIssueContextReader.MaxTitleChars);
        result.Issues[1].Title.Should().EndWith("…");
    }

    [Fact]
    public async Task ReadAsync_sends_exact_graphql_variables_and_query_contract()
    {
        var body = GraphQlResponse([], hasNextPage: false, endCursor: null);
        var handler = new FakeHttpMessageHandler().On(IsGraphQlPost, _ => JsonResponse(body));

        using var harness = NewHarness(handler);
        var runId = harness.SeedRun(Repo, "7");
        await harness.Reader.ReadAsync(runId, CancellationToken.None);

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

        using var harness = NewHarness(handler);
        var runId = harness.SeedRun(Repo, "7");
        var act = () => harness.Reader.ReadAsync(runId, cts.Token);

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

        using var harness = NewHarness(handler);
        var runId = harness.SeedRun(Repo, "7");
        var result = await harness.Reader.ReadAsync(runId, CancellationToken.None);

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

        using var harness = NewHarness(handler);
        var runId = harness.SeedRun(Repo, "7");
        var result = await harness.Reader.ReadAsync(runId, CancellationToken.None);

        result.Outcome.Should().Be(GitHubIssueLookup.Failed);
    }

    [Fact]
    public async Task ReadAsync_returns_Failed_when_pageInfo_is_missing_entirely()
    {
        // Issue #647 follow-up (Confirmed Should): a response that omits "pageInfo" altogether used to read
        // as pageInfoPresent=false, which defaulted hasNextPage to false and endCursor to null — indistinguishable
        // from a genuine last-page-with-no-more-results. That is silently trusting an assumption GitHub's schema
        // never actually promised for this response. Fail closed instead: a connection missing its required
        // pageInfo container could not be read, not that it reported "no more pages".
        var body = JsonSerializer.Serialize(
            new
            {
                data = new
                {
                    repository = new
                    {
                        pullRequest = new { closingIssuesReferences = new { nodes = new[] { IssueNode(1) } } },
                    },
                },
            }
        );
        var handler = new FakeHttpMessageHandler().On(IsGraphQlPost, _ => JsonResponse(body));

        using var harness = NewHarness(handler);
        var runId = harness.SeedRun(Repo, "7");
        var result = await harness.Reader.ReadAsync(runId, CancellationToken.None);

        result.Outcome.Should().Be(GitHubIssueLookup.Failed, "a missing pageInfo container could not be read");
    }

    [Fact]
    public async Task ReadAsync_returns_Failed_when_hasNextPage_is_the_wrong_kind()
    {
        // "hasNextPage" must be a JSON boolean per GitHub's schema; a string/number/null here means the
        // response could not be trusted, not that the field silently defaulted to false.
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
                                pageInfo = new { hasNextPage = "false", endCursor = (string?)null },
                                nodes = new[] { IssueNode(1) },
                            },
                        },
                    },
                },
            }
        );
        var handler = new FakeHttpMessageHandler().On(IsGraphQlPost, _ => JsonResponse(body));

        using var harness = NewHarness(handler);
        var runId = harness.SeedRun(Repo, "7");
        var result = await harness.Reader.ReadAsync(runId, CancellationToken.None);

        result
            .Outcome.Should()
            .Be(GitHubIssueLookup.Failed, "hasNextPage as a string is not the boolean the schema promises");
    }

    [Fact]
    public async Task ReadAsync_returns_Failed_when_endCursor_is_the_wrong_kind()
    {
        // "endCursor" must be a JSON string or null; a number/boolean/object here is a shape GitHub's schema
        // never produces, so the page cannot be trusted rather than treated as cursor-less.
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
                                pageInfo = new { hasNextPage = false, endCursor = 12345 },
                                nodes = new[] { IssueNode(1) },
                            },
                        },
                    },
                },
            }
        );
        var handler = new FakeHttpMessageHandler().On(IsGraphQlPost, _ => JsonResponse(body));

        using var harness = NewHarness(handler);
        var runId = harness.SeedRun(Repo, "7");
        var result = await harness.Reader.ReadAsync(runId, CancellationToken.None);

        result
            .Outcome.Should()
            .Be(
                GitHubIssueLookup.Failed,
                "endCursor as a number is neither the string nor the null the schema promises"
            );
    }

    [Fact]
    public async Task ReadAsync_returns_Failed_when_nodes_is_missing_entirely()
    {
        // Issue #647 follow-up (Confirmed Should): a response that omits "nodes" altogether used to read as
        // an empty list with RawNodeCount 0 — indistinguishable from a genuine "this PR closes no issues".
        // Fail closed instead: a connection missing its required nodes array could not be read, not that it
        // reported NoneLinked.
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
                            },
                        },
                    },
                },
            }
        );
        var handler = new FakeHttpMessageHandler().On(IsGraphQlPost, _ => JsonResponse(body));

        using var harness = NewHarness(handler);
        var runId = harness.SeedRun(Repo, "7");
        var result = await harness.Reader.ReadAsync(runId, CancellationToken.None);

        result.Outcome.Should().Be(GitHubIssueLookup.Failed, "a missing nodes container could not be read");
    }

    [Fact]
    public async Task ReadAsync_returns_Failed_when_nodes_is_not_an_array()
    {
        // "nodes" must be a JSON array per GitHub's schema; an object/string/number here means the response
        // could not be trusted, not that it silently contained zero entries.
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
                                nodes = "not-an-array",
                            },
                        },
                    },
                },
            }
        );
        var handler = new FakeHttpMessageHandler().On(IsGraphQlPost, _ => JsonResponse(body));

        using var harness = NewHarness(handler);
        var runId = harness.SeedRun(Repo, "7");
        var result = await harness.Reader.ReadAsync(runId, CancellationToken.None);

        result.Outcome.Should().Be(GitHubIssueLookup.Failed, "nodes as a string is not the array the schema promises");
    }

    [Fact]
    public async Task ReadAsync_returns_NoneLinked_when_pageInfo_and_nodes_are_well_formed_but_empty()
    {
        // Pinning the case Part C must NOT break: a genuinely well-formed page with zero linked issues
        // (both containers present with the right shapes, nodes simply empty) is still a real NoneLinked,
        // not a failure.
        var body = GraphQlResponse([], hasNextPage: false, endCursor: null);
        var handler = new FakeHttpMessageHandler().On(IsGraphQlPost, _ => JsonResponse(body));

        using var harness = NewHarness(handler);
        var runId = harness.SeedRun(Repo, "7");
        var result = await harness.Reader.ReadAsync(runId, CancellationToken.None);

        result.Outcome.Should().Be(GitHubIssueLookup.NoneLinked);
        result.Issues.Should().BeEmpty();
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

        using var harness = NewHarness(handler);
        var runId = harness.SeedRun(Repo, "7");
        var result = await harness.Reader.ReadAsync(runId, CancellationToken.None);

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

        using var harness = NewHarness(handler);
        var runId = harness.SeedRun(Repo, "7");
        var result = await harness.Reader.ReadAsync(runId, CancellationToken.None);

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

        using var harness = NewHarness(handler);
        var runId = harness.SeedRun(Repo, "7");
        var result = await harness.Reader.ReadAsync(runId, CancellationToken.None);

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

        using var harness = NewHarness(handler);
        var runId = harness.SeedRun(Repo, "7");
        var result = await harness.Reader.ReadAsync(runId, CancellationToken.None);

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

        using var harness = NewHarness(handler);
        var runId = harness.SeedRun(Repo, "7");
        var result = await harness.Reader.ReadAsync(runId, CancellationToken.None);

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

        using var harness = NewHarness(handler);
        var runId = harness.SeedRun(Repo, "7");
        var result = await harness.Reader.ReadAsync(runId, CancellationToken.None);

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

        using var harness = NewHarness(handler);
        var runId = harness.SeedRun(Repo, "7");
        var result = await harness.Reader.ReadAsync(runId, CancellationToken.None);

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

        using var harness = NewHarness(handler);
        var runId = harness.SeedRun(Repo, "7");
        var result = await harness.Reader.ReadAsync(runId, CancellationToken.None);

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

        using var harness = NewHarness(handler);
        var runId = harness.SeedRun(Repo, "7");
        var result = await harness.Reader.ReadAsync(runId, CancellationToken.None);

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

        using var harness = NewHarness(handler);
        var runId = harness.SeedRun(Repo, "7");
        var result = await harness.Reader.ReadAsync(runId, CancellationToken.None);

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

        using var harness = NewHarness(handler);
        var runId = harness.SeedRun(Repo, "7");
        var result = await harness.Reader.ReadAsync(runId, CancellationToken.None);

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

        using var harness = NewHarness(handler);
        var runId = harness.SeedRun(Repo, "7");
        var result = await harness.Reader.ReadAsync(runId, CancellationToken.None);

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

    [Fact]
    public void ReadAsync_has_exactly_one_overload_and_it_accepts_only_a_review_run_id()
    {
        // A caller must not be able to pass an alternate repo or PR beside the run id. The strongest proof
        // of that is not a runtime check on some hypothetical caller — it is that the type itself exposes no
        // method shape a caller could use to try.
        var overloads = typeof(GitHubIssueContextReader)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.Name == nameof(GitHubIssueContextReader.ReadAsync))
            .ToArray();

        var readAsync = overloads.Should().ContainSingle("no bypass of the run-id-only contract may remain").Subject;
        var parameters = readAsync.GetParameters();
        parameters.Should().HaveCount(2);
        parameters[0].ParameterType.Should().Be(typeof(long), "the only input identifying WHICH run is the run id");
        parameters[1].ParameterType.Should().Be(typeof(CancellationToken));
    }

    [Fact]
    public async Task ReadAsync_returns_Unavailable_and_touches_neither_network_nor_token_for_a_missing_run()
    {
        var handler = new FakeHttpMessageHandler();
        using var harness = NewHarness(handler);

        var result = await harness.Reader.ReadAsync(reviewRunId: 999_999, CancellationToken.None);

        result.Outcome.Should().Be(GitHubIssueLookup.Unavailable, "there is no run to derive a scope from");
        handler.Requests.Should().BeEmpty("a missing run must never reach the HTTP client");
        harness.Tokens.IssuedTokens.Should().BeEmpty("a missing run must never reach the token provider");
    }

    [Fact]
    public async Task ReadAsync_returns_Unavailable_and_touches_neither_network_nor_token_when_the_runs_repo_is_gone()
    {
        // A run whose repo row has since been removed must fail exactly like a missing run — Unavailable,
        // before the client or the token provider is ever touched.
        var handler = new FakeHttpMessageHandler();
        using var harness = NewHarness(handler);
        var runId = harness.SeedRunWithMissingRepo("7");

        var result = await harness.Reader.ReadAsync(runId, CancellationToken.None);

        result.Outcome.Should().Be(GitHubIssueLookup.Unavailable, "the run's repo row does not exist");
        handler.Requests.Should().BeEmpty();
        harness.Tokens.IssuedTokens.Should().BeEmpty();
    }

    [Fact]
    public async Task ReadAsync_derives_both_the_canonical_scope_and_the_request_variables_from_the_same_persisted_run()
    {
        // The binding under test: the client's canonical GraphQL scope AND the request body's (owner, repo,
        // number) variables must come from the SAME persisted row, not from two independently supplied
        // values that happen to usually agree. Proven by seeding two DIFFERENT runs and showing the request
        // each one actually sends tracks its OWN persisted identity — and that both succeed, which the
        // policy handler (bound per-call to that call's own canonical scope) would refuse to do if the scope
        // and the body variables had drifted apart from one another.
        var otherRepo = new RepoIdentity
        {
            Provider = "github",
            OrgOrOwner = "acme",
            RepoName = "gadgets",
            RepoStableId = "R_node_456",
        };
        var body = GraphQlResponse([], hasNextPage: false, endCursor: null);
        var handler = new FakeHttpMessageHandler().On(IsGraphQlPost, _ => JsonResponse(body));
        using var harness = NewHarness(handler);

        var firstRunId = harness.SeedRun(Repo, "7", headSha: "head-a");
        var firstResult = await harness.Reader.ReadAsync(firstRunId, CancellationToken.None);

        var secondRunId = harness.SeedRun(otherRepo, "9", headSha: "head-b");
        var secondResult = await harness.Reader.ReadAsync(secondRunId, CancellationToken.None);

        firstResult.Outcome.Should().Be(GitHubIssueLookup.NoneLinked);
        secondResult.Outcome.Should().Be(GitHubIssueLookup.NoneLinked);
        handler.Requests.Should().HaveCount(2, "each run's own request must actually reach the (fake) network");

        using var firstVariables = JsonDocument.Parse(handler.Requests[0].Body!);
        firstVariables.RootElement.GetProperty("variables").GetProperty("owner").GetString().Should().Be("acme");
        firstVariables.RootElement.GetProperty("variables").GetProperty("repo").GetString().Should().Be("widgets");
        firstVariables.RootElement.GetProperty("variables").GetProperty("number").GetInt32().Should().Be(7);

        using var secondVariables = JsonDocument.Parse(handler.Requests[1].Body!);
        secondVariables.RootElement.GetProperty("variables").GetProperty("owner").GetString().Should().Be("acme");
        secondVariables.RootElement.GetProperty("variables").GetProperty("repo").GetString().Should().Be("gadgets");
        secondVariables.RootElement.GetProperty("variables").GetProperty("number").GetInt32().Should().Be(9);
    }
}
