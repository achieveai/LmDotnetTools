using System.Net;
using System.Text;
using AchieveAi.LmDotnetTools.LmTestUtils.Logging;
using CodeReviewDaemon.Sample.Configuration;
using CodeReviewDaemon.Sample.Orchestration;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace CodeReviewDaemon.Sample.Tests.Scenarios;

/// <summary>
/// P4.4 — the real <see cref="AdoPrProvider"/> reads active PRs from the Azure DevOps REST API. Driven
/// against a scripted HTTP handler, these tests pin the request shape ADO requires (basic auth from the
/// OAuth provider, the <c>searchCriteria.status=active</c> + <c>api-version</c> query), the
/// <c>{ "value": [...] }</c> envelope mapping (pullRequestId/merge-source/merge-target/status), and the
/// versioned opaque cursor.
/// </summary>
public sealed class AdoPrProviderTests : LoggingTestBase
{
    public AdoPrProviderTests(ITestOutputHelper output)
        : base(output) { }

    private static readonly RepoIdentity Repo = new()
    {
        Provider = "azure-devops",
        OrgOrOwner = "contoso",
        Project = "Platform",
        RepoName = "widgets",
        RepoStableId = "repo-guid-1",
    };

    private const string TwoActivePrs = """
        {
          "count": 2,
          "value": [
            {
              "pullRequestId": 42,
              "status": "active",
              "lastMergeSourceCommit": { "commitId": "head-42" },
              "lastMergeTargetCommit": { "commitId": "base-42" }
            },
            {
              "pullRequestId": 50,
              "status": "active",
              "lastMergeSourceCommit": { "commitId": "head-50" },
              "lastMergeTargetCommit": { "commitId": "base-50" }
            }
          ]
        }
        """;

    private static PrPollRequest Request(OpaqueCursor? cursor = null, DateTimeOffset? recencyCutoff = null) =>
        new()
        {
            Repo = Repo,
            Scope = "contoso/Platform/widgets:active-prs",
            Cursor = cursor,
            RecencyCutoff = recencyCutoff,
        };

    // One PR opened before a recency window (needs a last-push lookup) and one opened inside it (does not).
    private const string DatedPrs = """
        {
          "value": [
            { "pullRequestId": 42, "status": "active", "creationDate": "2026-06-01T00:00:00Z",
              "sourceRefName": "refs/heads/feature-42",
              "lastMergeSourceCommit": { "commitId": "head-42" }, "lastMergeTargetCommit": { "commitId": "base-42" } },
            { "pullRequestId": 50, "status": "active", "creationDate": "2026-07-09T00:00:00Z",
              "sourceRefName": "refs/heads/feature-50",
              "lastMergeSourceCommit": { "commitId": "head-50" }, "lastMergeTargetCommit": { "commitId": "base-50" } }
          ]
        }
        """;

    private const string OneOldPr = """
        { "value": [ { "pullRequestId": 42, "status": "active", "creationDate": "2026-06-01T00:00:00Z",
            "sourceRefName": "refs/heads/feature-42",
            "lastMergeSourceCommit": { "commitId": "head-42" }, "lastMergeTargetCommit": { "commitId": "base-42" } } ] }
        """;

    private AdoPrProvider Provider(FakeHttpMessageHandler handler) =>
        new(
            new HttpClient(handler),
            new FakeOAuthTokenProvider("ado", "ado-token-abc"),
            LoggerFactory.CreateLogger<AdoPrProvider>()
        );

    [Fact]
    public void Provider_id_is_ado()
    {
        Provider(new FakeHttpMessageHandler()).Provider.Should().Be("ado");
    }

    /// <summary>
    /// The author is only ever read from <c>uniqueName</c>. ADO also offers <c>displayName</c>, and falling
    /// back to it looks harmless until you follow where the value goes: it keys
    /// <c>KnowledgeBase/developers/&lt;slug&gt;.reviewfeedbacks.md</c>, a file committed to a PUBLIC repo and
    /// handed to the next reviewer as "this author's recurring mistakes". Two people may share a display
    /// name — ADO does not constrain it — so the fallback would file one developer's mistakes under the
    /// other's, and no slugging scheme downstream can undo an identity that was ambiguous on arrival.
    /// A null author is already an ordinary outcome here: it writes no record at all.
    /// </summary>
    [Theory]
    [InlineData("""{ "uniqueName": "jane.doe@contoso.com", "displayName": "Jane Doe" }""", "jane.doe@contoso.com")]
    [InlineData("""{ "displayName": "Jane Doe" }""", null)]
    [InlineData("""{ "uniqueName": "   ", "displayName": "Jane Doe" }""", null)]
    [InlineData("""{ "displayName": 7 }""", null)]
    [InlineData("{ }", null)]
    public async Task ListOpenPullRequests_takes_the_author_only_from_a_unique_identity(
        string createdBy,
        string? expected
    )
    {
        var handler = new FakeHttpMessageHandler().OnJson(
            HttpMethod.Get,
            "/pullrequests",
            $$"""
            { "value": [ { "pullRequestId": 42, "status": "active", "createdBy": {{createdBy}},
                "lastMergeSourceCommit": { "commitId": "head-42" },
                "lastMergeTargetCommit": { "commitId": "base-42" } } ] }
            """
        );

        var page = await Provider(handler).ListOpenPullRequestsAsync(Request(), CancellationToken.None);

        page.PullRequests.Should().ContainSingle().Which.Author.Should().Be(expected);
    }

    [Fact]
    public async Task ListOpenPullRequests_maps_each_pr_to_a_descriptor()
    {
        var handler = new FakeHttpMessageHandler().OnJson(HttpMethod.Get, "/pullrequests", TwoActivePrs);

        var page = await Provider(handler).ListOpenPullRequestsAsync(Request(), CancellationToken.None);

        page.PullRequests.Should().HaveCount(2);
        var first = page.PullRequests[0];
        first.PrId.Should().Be("42");
        first.HeadSha.Should().Be("head-42");
        first.BaseSha.Should().Be("base-42");
        first.LifecycleState.Should().Be(PrLifecycleState.Open);
    }

    /// <summary>
    /// The prose half of the knowledge-retrieval key, captured at poll time because the Reviewed stage runs
    /// long after this page is gone. A blank description collapses to null so it ranks identically to a PR
    /// that carries none at all.
    /// </summary>
    [Theory]
    [InlineData(
        """  "title": "Remove the stale featureflag entry", "description": "Still in the task def." """,
        "Remove the stale featureflag entry",
        "Still in the task def."
    )]
    [InlineData(
        """  "title": "Remove the stale featureflag entry", "description": "  " """,
        "Remove the stale featureflag entry",
        null
    )]
    [InlineData("""  "title": "Remove the stale featureflag entry" """, "Remove the stale featureflag entry", null)]
    [InlineData("""  "description": "Still in the task def." """, null, "Still in the task def.")]
    public async Task ListOpenPullRequests_captures_what_the_pr_says_it_does(
        string prose,
        string? expectedTitle,
        string? expectedDescription
    )
    {
        var handler = new FakeHttpMessageHandler().OnJson(
            HttpMethod.Get,
            "/pullrequests",
            $$"""
            { "value": [ { "pullRequestId": 42, "status": "active", {{prose}},
                "lastMergeSourceCommit": { "commitId": "head-42" },
                "lastMergeTargetCommit": { "commitId": "base-42" } } ] }
            """
        );

        var page = await Provider(handler).ListOpenPullRequestsAsync(Request(), CancellationToken.None);

        var pr = page.PullRequests.Should().ContainSingle().Subject;
        pr.Title.Should().Be(expectedTitle);
        pr.Description.Should().Be(expectedDescription);
    }

    [Fact]
    public async Task ListOpenPullRequests_sends_the_request_ado_requires()
    {
        var handler = new FakeHttpMessageHandler().OnJson(HttpMethod.Get, "/pullrequests", TwoActivePrs);

        _ = await Provider(handler).ListOpenPullRequestsAsync(Request(), CancellationToken.None);

        var request = handler.Requests.Should().ContainSingle().Subject;
        request.Method.Should().Be(HttpMethod.Get);
        request
            .Uri.ToString()
            .Should()
            .StartWith("https://dev.azure.com/contoso/Platform/_apis/git/repositories/widgets/pullrequests");
        request.Uri.Query.Should().Contain("searchCriteria.status=active");
        request.Uri.Query.Should().Contain("api-version=7.1");
        request.Authorization.Should().StartWith("Basic ", "ADO PATs/bearer tokens are sent via basic auth");
    }

    [Fact]
    public async Task ListOpenPullRequests_advances_a_versioned_opaque_cursor()
    {
        var handler = new FakeHttpMessageHandler().OnJson(HttpMethod.Get, "/pullrequests", TwoActivePrs);

        var page = await Provider(handler).ListOpenPullRequestsAsync(Request(), CancellationToken.None);

        page.NextCursor.Provider.Should().Be("ado");
        page.NextCursor.Scope.Should().Be("contoso/Platform/widgets:active-prs");
        page.NextCursor.CursorVersion.Should().Be(PrPollingService.CursorVersion);
        page.NextCursor.HighWaterMark.Should().Be("50", "the highest active pullRequestId seen");
    }

    [Fact]
    public async Task ListOpenPullRequests_maps_abandoned_and_completed_lifecycles()
    {
        const string mixed = """
            {
              "value": [
                { "pullRequestId": 1, "status": "completed",
                  "lastMergeSourceCommit": { "commitId": "h1" }, "lastMergeTargetCommit": { "commitId": "b1" } },
                { "pullRequestId": 2, "status": "abandoned",
                  "lastMergeSourceCommit": { "commitId": "h2" }, "lastMergeTargetCommit": { "commitId": "b2" } }
              ]
            }
            """;
        var handler = new FakeHttpMessageHandler().OnJson(HttpMethod.Get, "/pullrequests", mixed);

        var page = await Provider(handler).ListOpenPullRequestsAsync(Request(), CancellationToken.None);

        page.PullRequests[0].LifecycleState.Should().Be(PrLifecycleState.Merged);
        page.PullRequests[1].LifecycleState.Should().Be(PrLifecycleState.Abandoned);
    }

    [Fact]
    public async Task ListOpenPullRequests_throws_on_a_non_success_status()
    {
        var handler = new FakeHttpMessageHandler().OnJson(
            HttpMethod.Get,
            "/pullrequests",
            """{"message":"unauthorized"}""",
            HttpStatusCode.Unauthorized
        );

        var act = () => Provider(handler).ListOpenPullRequestsAsync(Request(), CancellationToken.None);

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task ListOpenPullRequests_follows_the_continuation_token_across_pages()
    {
        // PR #121 M5 — page 1 returns an x-ms-continuationtoken header; the provider must re-request with
        // &continuationToken= and accumulate. Page 2 has no continuation header, so pagination stops.
        const string page1 = """
            { "value": [ { "pullRequestId": 42, "status": "active",
                "lastMergeSourceCommit": { "commitId": "head-42" }, "lastMergeTargetCommit": { "commitId": "base-42" } } ] }
            """;
        const string page2 = """
            { "value": [ { "pullRequestId": 50, "status": "active",
                "lastMergeSourceCommit": { "commitId": "head-50" }, "lastMergeTargetCommit": { "commitId": "base-50" } } ] }
            """;
        var handler = new FakeHttpMessageHandler()
            .On(
                req => req.RequestUri!.ToString().Contains("continuationToken=TOKEN2", StringComparison.Ordinal),
                _ => JsonResponse(page2)
            )
            .On(
                req => req.RequestUri!.ToString().Contains("/pullrequests", StringComparison.Ordinal),
                _ => JsonResponse(page1, ("x-ms-continuationtoken", "TOKEN2"))
            );

        var page = await Provider(handler).ListOpenPullRequestsAsync(Request(), CancellationToken.None);

        page.PullRequests.Select(p => p.PrId).Should().BeEquivalentTo(["42", "50"], "both pages are accumulated");
        handler.CountRequests("/pullrequests").Should().Be(2, "the provider followed exactly one continuation token");
        page.NextCursor.HighWaterMark.Should().Be("50", "the highest pullRequestId across all pages");
    }

    // ---- Issue #537: $top + $skip paging, bounded by the operator's MaxPagesPerPoll ----

    private AdoPrProvider Provider(
        FakeHttpMessageHandler handler,
        int maxPagesPerPoll,
        int maxPrsPerPage = 200,
        ILogger<AdoPrProvider>? logger = null
    ) =>
        new(
            new HttpClient(handler),
            new FakeOAuthTokenProvider("ado", "ado-token-abc"),
            logger ?? LoggerFactory.CreateLogger<AdoPrProvider>(),
            maxPagesPerPoll,
            maxPrsPerPage
        );

    /// <summary>
    /// A repo of <paramref name="totalPrs"/> active PRs served the way ADO serves them: honouring
    /// <c>$top</c>/<c>$skip</c> and — the detail that made this a live defect — sending <b>no</b>
    /// <c>x-ms-continuationtoken</c> at all. A provider that pages on the token alone reads the very first
    /// full page as the whole repo, which is how 711 active PRs presented as 101.
    /// </summary>
    private static FakeHttpMessageHandler PagedRepo(int totalPrs) =>
        new FakeHttpMessageHandler().On(
            req => req.RequestUri!.AbsolutePath.EndsWith("/pullrequests", StringComparison.Ordinal),
            req =>
            {
                var query = System.Web.HttpUtility.ParseQueryString(req.RequestUri!.Query);
                var top = int.Parse(query["$top"]!, System.Globalization.CultureInfo.InvariantCulture);
                var skip = query["$skip"] is { } s
                    ? int.Parse(s, System.Globalization.CultureInfo.InvariantCulture)
                    : 0;

                var ids = Enumerable.Range(skip + 1, Math.Clamp(totalPrs - skip, 0, top));
                var values = string.Join(
                    ",",
                    ids.Select(id =>
                        $$"""
                            { "pullRequestId": {{id}}, "status": "active",
                              "lastMergeSourceCommit": { "commitId": "head-{{id}}" },
                              "lastMergeTargetCommit": { "commitId": "base-{{id}}" } }
                            """
                    )
                );
                return JsonResponse($$"""{ "value": [ {{values}} ] }""");
            }
        );

    /// <summary>
    /// AC#3 — 30 active PRs at 2 per page is 15 pages, more than the old hardcoded ceiling of 10. With
    /// <c>MaxPagesPerPoll</c> configured above it, PRs 21–30 (which live only on pages 11–15) must be
    /// enumerated. Restoring <c>pages &lt; 10</c> stops at PR 20 and fails here.
    /// </summary>
    [Fact]
    public async Task ListOpenPullRequests_enumerates_past_page_ten_when_configured_to()
    {
        var handler = PagedRepo(totalPrs: 30);

        var page = await Provider(handler, maxPagesPerPoll: 20, maxPrsPerPage: 2)
            .ListOpenPullRequestsAsync(Request(), CancellationToken.None);

        page.PullRequests.Select(p => p.PrId)
            .Should()
            .Contain(["21", "25", "30"], "these PRs exist only on pages past the old hardcoded ceiling of 10");
        page.PullRequests.Should().HaveCount(30);
        // 15 full pages, then one short (empty) page proving the end of the list rather than assuming it.
        handler.CountRequests("/pullrequests").Should().Be(16);
    }

    /// <summary>
    /// The same repo under the old bound: the poll is truncated at 10 pages. This is the measured M7
    /// symptom, and it is what an operator raises <c>MaxPagesPerPoll</c> to escape.
    /// </summary>
    [Fact]
    public async Task ListOpenPullRequests_stops_at_the_configured_bound()
    {
        var handler = PagedRepo(totalPrs: 30);

        var page = await Provider(handler, maxPagesPerPoll: 10, maxPrsPerPage: 2)
            .ListOpenPullRequestsAsync(Request(), CancellationToken.None);

        handler.CountRequests("/pullrequests").Should().Be(10);
        page.PullRequests.Should().HaveCount(20, "10 pages of 2 — the rest were not seen this poll");
    }

    /// <summary>A page bound that cannot be a page count is treated as unset, never as zero pages (which
    /// would make the repo read as empty) and never as unbounded.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task A_nonsensical_page_bound_polls_the_documented_default_number_of_pages(int configured)
    {
        var handler = PagedRepo(totalPrs: 100);

        _ = await Provider(handler, maxPagesPerPoll: configured, maxPrsPerPage: 2)
            .ListOpenPullRequestsAsync(Request(), CancellationToken.None);

        handler
            .CountRequests("/pullrequests")
            .Should()
            .Be(
                CodeReviewDaemonOptions.DefaultMaxPagesPerPoll,
                "a nonsensical bound degrades to the default, not to 0"
            );
    }

    /// <summary>
    /// <c>$top</c> is sent explicitly. Omitting it is not "no limit": ADO substitutes its own default of
    /// 101, which is indistinguishable in the response from a repo that has 101 open PRs.
    /// </summary>
    [Theory]
    [InlineData(50, "$top=50")]
    [InlineData(5000, "$top=1000")]
    [InlineData(0, "$top=200")]
    public async Task The_page_size_is_sent_and_clamped_to_ados_maximum(int configured, string expected)
    {
        var handler = new FakeHttpMessageHandler().OnJson(HttpMethod.Get, "/pullrequests", """{ "value": [] }""");

        _ = await Provider(handler, maxPagesPerPoll: 10, maxPrsPerPage: configured)
            .ListOpenPullRequestsAsync(Request(), CancellationToken.None);

        handler.Requests.Should().ContainSingle().Which.Uri.Query.Should().Contain(expected);
    }

    /// <summary>
    /// Page 2 onwards is addressed by <c>$skip</c>, advanced by the number of PRs actually read — not by a
    /// continuation token, which this endpoint does not send.
    /// </summary>
    [Fact]
    public async Task Subsequent_pages_are_addressed_by_skip()
    {
        var handler = PagedRepo(totalPrs: 7);

        _ = await Provider(handler, maxPagesPerPoll: 10, maxPrsPerPage: 3)
            .ListOpenPullRequestsAsync(Request(), CancellationToken.None);

        handler
            .Requests.Select(r => System.Web.HttpUtility.ParseQueryString(r.Uri.Query)["$skip"])
            .Should()
            .Equal([null, "3", "6"], "the first page carries no $skip; each later one skips what was read");
    }

    /// <summary>
    /// A truncated poll must SAY it was truncated. This matters more on ADO than anywhere else: the endpoint
    /// sends no continuation token, so a full page and the end of the repo are the same response, and the
    /// only thing distinguishing "you have seen everything" from "you have seen 101 of 711" is this line.
    /// Asserted on the RENDERED text including the counts, because a warning naming the wrong numbers sends
    /// the operator to raise a bound that was not the one that bound.
    /// </summary>
    [Fact]
    public async Task A_truncated_poll_warns_with_the_counts_that_bound_it()
    {
        var logger = new CapturingLogger<AdoPrProvider>();
        var handler = PagedRepo(totalPrs: 30);

        var page = await Provider(handler, maxPagesPerPoll: 10, maxPrsPerPage: 2, logger: logger)
            .ListOpenPullRequestsAsync(Request(), CancellationToken.None);

        page.PullRequests.Should().HaveCount(20, "10 pages of 2, of the repo's 30 PRs");
        logger
            .CountAtLevel(LogLevel.Warning, "stopped after 10 page(s) of 2 with more results still available; 20 PR(s)")
            .Should()
            .Be(1, "the operator is told the poll was incomplete, and with the real counts");
    }

    /// <summary>
    /// The other half, without which the assertion above is satisfied by a guard that always fires. It also
    /// pins the termination rule itself: the poll is complete because the last page came back SHORTER than
    /// <c>$top</c> — the only end-of-list evidence this endpoint offers.
    /// </summary>
    [Fact]
    public async Task A_complete_poll_does_not_warn()
    {
        var logger = new CapturingLogger<AdoPrProvider>();
        var handler = PagedRepo(totalPrs: 7);

        var page = await Provider(handler, maxPagesPerPoll: 10, maxPrsPerPage: 3, logger: logger)
            .ListOpenPullRequestsAsync(Request(), CancellationToken.None);

        page.PullRequests.Should().HaveCount(7, "the whole repo was enumerated");
        logger
            .CountAtLevel(LogLevel.Warning, "stopped after")
            .Should()
            .Be(0, "a page shorter than $top proved the end of the list; nothing was left unseen");
    }

    private static HttpResponseMessage JsonResponse(string json, params (string Name, string Value)[] headers)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        foreach (var (name, value) in headers)
        {
            response.Headers.Add(name, value);
        }

        return response;
    }

    [Fact]
    public async Task ListOpenPullRequests_handles_an_empty_envelope()
    {
        var handler = new FakeHttpMessageHandler().OnJson(
            HttpMethod.Get,
            "/pullrequests",
            """{"count":0,"value":[]}"""
        );

        var page = await Provider(handler).ListOpenPullRequestsAsync(Request(), CancellationToken.None);

        page.PullRequests.Should().BeEmpty();
        page.NextCursor.CursorVersion.Should().Be(PrPollingService.CursorVersion);
    }

    [Fact]
    public async Task Engagement_snapshot_preserves_thread_ancestry_status_anchor_and_iteration_context()
    {
        const string pr = """
            { "pullRequestId": 42, "status": "active", "lastUpdatedDate": "2026-09-02T12:05:00Z",
              "lastMergeSourceCommit": { "commitId": "head-42" },
              "lastMergeTargetCommit": { "commitId": "base-42" } }
            """;
        const string threads = """
            {
              "value": [
                {
                  "id": 900,
                  "status": "fixed",
                  "threadContext": {
                    "filePath": "/src/a.cs",
                    "rightFileStart": { "line": 10, "offset": 1 },
                    "rightFileEnd": { "line": 12, "offset": 4 }
                  },
                  "pullRequestThreadContext": {
                    "iterationContext": { "firstComparingIteration": 2, "secondComparingIteration": 3 },
                    "changeTrackingId": 44
                  },
                  "comments": [
                    { "id": 1, "parentCommentId": 0, "content": "daemon root",
                      "publishedDate": "2026-09-02T12:00:00Z", "commentType": "text",
                      "author": { "displayName": "Review Bot" } },
                    { "id": 2, "parentCommentId": 1, "content": "human reply",
                      "publishedDate": "2026-09-02T12:01:00Z", "commentType": "text",
                      "author": { "displayName": "Developer" } },
                    { "id": 3, "parentCommentId": 1, "content": "deleted body",
                      "publishedDate": "2026-09-02T12:02:00Z", "commentType": "text", "isDeleted": true,
                      "author": { "displayName": "Developer" } },
                    { "id": 4, "parentCommentId": 0, "content": "vote update",
                      "publishedDate": "2026-09-02T12:03:00Z", "commentType": "system",
                      "author": { "displayName": "Project Service" } }
                  ]
                }
              ]
            }
            """;
        var handler = new FakeHttpMessageHandler()
            .OnJson(HttpMethod.Get, "/threads", threads)
            .OnJson(HttpMethod.Get, "/pullrequests/42", pr);

        var snapshot = await Provider(handler)
            .GetEngagementSnapshotAsync(
                Repo,
                "42",
                new ProviderActivityWatermark(
                    "ado",
                    new DateTimeOffset(2026, 9, 2, 11, 59, 0, TimeSpan.Zero),
                    "seed:0"
                ),
                new HashSet<string>(["thread:900:comment:1"], StringComparer.Ordinal),
                CancellationToken.None
            );

        snapshot.Lifecycle.Should().Be(PrLifecycleState.Open);
        snapshot.HeadSha.Should().Be("head-42");
        snapshot.BaseSha.Should().Be("base-42");
        snapshot.ExternalActivity.Select(activity => activity.CommentId).Should().Equal("2", "3", "4");
        var reply = snapshot.ExternalActivity[0];
        reply.ProviderObjectId.Should().Be("thread:900:comment:2");
        reply.ThreadId.Should().Be("900");
        reply.ParentCommentId.Should().Be("1");
        reply.Path.Should().Be("/src/a.cs");
        reply.Side.Should().Be("RIGHT");
        reply.StartLine.Should().Be(10);
        reply.EndLine.Should().Be(12);
        reply.Status.Should().Be("fixed");
        reply.IterationContext.Should().Contain("secondComparingIteration");
        reply.CreatesDiscussionDemand.Should().BeTrue();
        snapshot.ExternalActivity[1].Kind.Should().Be(ProviderDiscussionKind.Deleted);
        snapshot.ExternalActivity[1].CreatesDiscussionDemand.Should().BeFalse();
        snapshot.ExternalActivity[2].Kind.Should().Be(ProviderDiscussionKind.System);
        snapshot.ExternalActivity[2].CreatesDiscussionDemand.Should().BeFalse();
    }

    [Fact]
    public async Task Engagement_snapshot_retains_the_parent_of_an_in_window_thread_reply_as_ancestor_only()
    {
        const string pr = """
            { "pullRequestId": 42, "status": "active", "lastUpdatedDate": "2026-09-02T12:03:00Z",
              "lastMergeSourceCommit": { "commitId": "head-42" },
              "lastMergeTargetCommit": { "commitId": "base-42" } }
            """;
        const string threads = """
            { "value": [
              { "id": 900, "status": "active", "comments": [
                { "id": 1, "parentCommentId": 0, "content": "original question",
                  "publishedDate": "2026-09-02T12:00:00Z", "commentType": "text",
                  "author": { "displayName": "Developer" } },
                { "id": 2, "parentCommentId": 1, "content": "the retry is per attempt",
                  "publishedDate": "2026-09-02T12:02:00Z", "commentType": "text",
                  "author": { "displayName": "Reviewer" } }
              ] }
            ] }
            """;
        var handler = new FakeHttpMessageHandler()
            .OnJson(HttpMethod.Get, "/threads", threads)
            .OnJson(HttpMethod.Get, "/pullrequests/42", pr);
        var after = new ProviderActivityWatermark(
            "ado",
            new DateTimeOffset(2026, 9, 2, 12, 0, 0, TimeSpan.Zero),
            "thread:900:comment:1"
        );

        var snapshot = await Provider(handler)
            .GetEngagementSnapshotAsync(Repo, "42", after, new HashSet<string>(), CancellationToken.None);

        snapshot.ExternalActivity.Should().ContainSingle().Which.CommentId.Should().Be("2");
        snapshot.AncestorActivity.Should().ContainSingle().Which.CommentId.Should().Be("1");
    }

    [Fact]
    public async Task Engagement_snapshot_keeps_same_author_text_and_excludes_only_the_receipted_root_object()
    {
        const string pr = """
            { "pullRequestId": 42, "status": "active", "lastUpdatedDate": "2026-09-02T12:03:00Z",
              "lastMergeSourceCommit": { "commitId": "head-42" },
              "lastMergeTargetCommit": { "commitId": "base-42" } }
            """;
        const string threads = """
            { "value": [
              { "id": 900, "status": "active", "comments": [
                { "id": 1, "parentCommentId": 0, "content": "same body", "publishedDate": "2026-09-02T12:00:00Z",
                  "commentType": "text", "author": { "displayName": "Review Bot" } },
                { "id": 2, "parentCommentId": 1, "content": "same body", "publishedDate": "2026-09-02T12:01:00Z",
                  "commentType": "text", "author": { "displayName": "Review Bot" } }
              ] }
            ] }
            """;
        var handler = new FakeHttpMessageHandler()
            .OnJson(HttpMethod.Get, "/threads", threads)
            .OnJson(HttpMethod.Get, "/pullrequests/42", pr);

        var snapshot = await Provider(handler)
            .GetEngagementSnapshotAsync(
                Repo,
                "42",
                after: null,
                new HashSet<string>(["thread:900:comment:1"], StringComparer.Ordinal),
                CancellationToken.None
            );

        snapshot.ExternalActivity.Should().ContainSingle().Which.CommentId.Should().Be("2");
    }

    [Fact]
    public async Task GetPrState_maps_an_active_pr_to_open()
    {
        var handler = new FakeHttpMessageHandler().OnJson(
            HttpMethod.Get,
            "/pullrequests/42",
            """{ "pullRequestId": 42, "status": "active" }"""
        );

        var state = await Provider(handler).GetPrStateAsync(Repo, "42", CancellationToken.None);

        state.Should().Be(PrLifecycle.Open);
    }

    [Fact]
    public async Task Inline_anchor_snapshot_returns_current_iteration_change_identity_without_inventing_line_ranges()
    {
        const string iterations = """
            {
              "value": [
                { "id": 1, "sourceRefCommit": { "commitId": "head-1" } },
                { "id": 2, "sourceRefCommit": { "commitId": "head-42" } }
              ]
            }
            """;
        const string changes = """
            {
              "changeEntries": [
                {
                  "changeTrackingId": 17,
                  "changeType": "edit",
                  "item": { "path": "/src/Foo.cs" }
                }
              ],
              "nextSkip": 0,
              "nextTop": 0
            }
            """;
        var handler = new FakeHttpMessageHandler()
            .OnSequence(
                HttpMethod.Get,
                "/pullrequests/42?",
                (HttpStatusCode.OK, """{ "lastMergeSourceCommit": { "commitId": "head-42" } }"""),
                (HttpStatusCode.OK, """{ "lastMergeSourceCommit": { "commitId": "head-42" } }""")
            )
            .OnJson(HttpMethod.Get, "/iterations/2/changes", changes)
            .OnJson(HttpMethod.Get, "/iterations", iterations);

        var snapshot = await Provider(handler)
            .GetInlineAnchorSnapshotAsync(Repo, "42", "head-42", CancellationToken.None);

        snapshot.HeadSha.Should().Be("head-42");
        var file = snapshot.Files.Should().ContainSingle().Subject;
        file.Path.Should().Be("/src/Foo.cs");
        file.RightRanges.Should().BeEmpty("ADO's provider response contains no line hunks");
        file.LeftRanges.Should().BeEmpty("ADO's provider response contains no line hunks");
        file.AdoContext.Should().Be(new AdoPullRequestThreadContext(17, new AdoIterationContext(1, 2)));

        handler.CountRequests("/pullrequests/42?").Should().Be(2, "the head is checked before and after inventory");
        handler.CountRequests("/iterations?").Should().Be(1);
        var changeRequest = handler
            .Requests.Should()
            .ContainSingle(r => r.Uri.AbsolutePath.EndsWith("/iterations/2/changes", StringComparison.Ordinal))
            .Which;
        var query = System.Web.HttpUtility.ParseQueryString(changeRequest.Uri.Query);
        query["$compareTo"].Should().Be("0", "native change identity must match the common-base PR diff");
        query["$top"].Should().Be("2000");
    }

    [Fact]
    public async Task Inline_anchor_snapshot_follows_every_bounded_iteration_change_page()
    {
        const string iterations = """
            { "value": [ { "id": 3, "sourceRefCommit": { "commitId": "head-42" } } ] }
            """;
        var handler = new FakeHttpMessageHandler()
            .OnSequence(
                HttpMethod.Get,
                "/iterations/3/changes",
                (
                    HttpStatusCode.OK,
                    """
                    { "changeEntries": [
                        { "changeTrackingId": 17, "item": { "path": "/src/First.cs" } }
                      ], "nextSkip": 1, "nextTop": 2000 }
                    """
                ),
                (
                    HttpStatusCode.OK,
                    """
                    { "changeEntries": [
                        { "changeTrackingId": 18, "item": { "path": "/src/Second.cs" } }
                      ], "nextSkip": 0, "nextTop": 0 }
                    """
                )
            )
            .OnJson(HttpMethod.Get, "/iterations", iterations)
            .OnJson(HttpMethod.Get, "/pullrequests/42?", """{ "lastMergeSourceCommit": { "commitId": "head-42" } }""");

        var snapshot = await Provider(handler)
            .GetInlineAnchorSnapshotAsync(Repo, "42", "head-42", CancellationToken.None);

        snapshot.Files.Select(file => file.Path).Should().Equal("/src/First.cs", "/src/Second.cs");
        handler.CountRequests("/iterations/3/changes").Should().Be(2);
        var requests = handler
            .Requests.Where(request =>
                request.Uri.AbsolutePath.EndsWith("/iterations/3/changes", StringComparison.Ordinal)
            )
            .ToArray();
        System.Web.HttpUtility.ParseQueryString(requests[0].Uri.Query)["$skip"].Should().BeNull();
        System.Web.HttpUtility.ParseQueryString(requests[1].Uri.Query)["$skip"].Should().Be("1");
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{ \"value\": null }")]
    [InlineData("{ \"value\": {} }")]
    [InlineData("[]")]
    [InlineData("null")]
    public async Task Inline_anchor_snapshot_fails_closed_on_malformed_iterations_envelope(string iterations)
    {
        var handler = new FakeHttpMessageHandler()
            .OnJson(HttpMethod.Get, "/iterations", iterations)
            .OnJson(HttpMethod.Get, "/pullrequests/42?", """{ "lastMergeSourceCommit": { "commitId": "head-42" } }""");

        var act = () => Provider(handler).GetInlineAnchorSnapshotAsync(Repo, "42", "head-42", CancellationToken.None);

        _ = await act.Should().ThrowAsync<InvalidDataException>();
        handler.CountRequests("/iterations?").Should().Be(1);
        handler.CountRequests("/changes").Should().Be(0);
        handler.CountRequests("/pullrequests/42?").Should().Be(1);
    }

    [Fact]
    public async Task Inline_anchor_snapshot_returns_empty_when_no_iteration_matches_the_exact_head()
    {
        var handler = new FakeHttpMessageHandler()
            .OnJson(
                HttpMethod.Get,
                "/iterations",
                """{ "value": [ { "id": 3, "sourceRefCommit": { "commitId": "different-head" } } ] }"""
            )
            .OnJson(HttpMethod.Get, "/pullrequests/42?", """{ "lastMergeSourceCommit": { "commitId": "head-42" } }""");

        var snapshot = await Provider(handler)
            .GetInlineAnchorSnapshotAsync(Repo, "42", "head-42", CancellationToken.None);

        snapshot.HeadSha.Should().Be("head-42");
        snapshot.Files.Should().BeEmpty();
        handler.CountRequests("/iterations?").Should().Be(1);
        handler.CountRequests("/changes").Should().Be(0);
        handler.CountRequests("/pullrequests/42?").Should().Be(1);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("3.5")]
    public async Task Inline_anchor_snapshot_fails_closed_on_non_positive_or_non_integral_iteration_id(
        string iterationId
    )
    {
        var handler = new FakeHttpMessageHandler()
            .OnJson(
                HttpMethod.Get,
                "/iterations",
                $$"""{ "value": [ { "id": {{iterationId}}, "sourceRefCommit": { "commitId": "head-42" } } ] }"""
            )
            .OnJson(HttpMethod.Get, "/pullrequests/42?", """{ "lastMergeSourceCommit": { "commitId": "head-42" } }""");

        var act = () => Provider(handler).GetInlineAnchorSnapshotAsync(Repo, "42", "head-42", CancellationToken.None);

        _ = await act.Should().ThrowAsync<InvalidDataException>();
        handler.CountRequests("/iterations?").Should().Be(1);
        handler.CountRequests("/changes").Should().Be(0);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{ \"changeEntries\": null }")]
    [InlineData("{ \"changeEntries\": {} }")]
    [InlineData("[]")]
    [InlineData("null")]
    public async Task Inline_anchor_snapshot_fails_closed_on_malformed_change_page_envelope(string changes)
    {
        var handler = new FakeHttpMessageHandler()
            .OnJson(HttpMethod.Get, "/iterations/3/changes", changes)
            .OnJson(
                HttpMethod.Get,
                "/iterations",
                """{ "value": [ { "id": 3, "sourceRefCommit": { "commitId": "head-42" } } ] }"""
            )
            .OnJson(HttpMethod.Get, "/pullrequests/42?", """{ "lastMergeSourceCommit": { "commitId": "head-42" } }""");

        var act = () => Provider(handler).GetInlineAnchorSnapshotAsync(Repo, "42", "head-42", CancellationToken.None);

        _ = await act.Should().ThrowAsync<InvalidDataException>();
        handler.CountRequests("/iterations/3/changes").Should().Be(1);
        handler.CountRequests("/pullrequests/42?").Should().Be(1);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{ \"changeTrackingId\": 17 }")]
    [InlineData("{ \"changeTrackingId\": 17, \"item\": null }")]
    [InlineData("{ \"changeTrackingId\": 17, \"item\": {} }")]
    [InlineData("{ \"changeTrackingId\": 17, \"item\": { \"path\": null } }")]
    [InlineData("{ \"changeTrackingId\": 17, \"item\": { \"path\": 7 } }")]
    [InlineData("{ \"changeTrackingId\": 17, \"item\": { \"path\": \"  \" } }")]
    [InlineData("{ \"item\": { \"path\": \"/src/Foo.cs\" } }")]
    [InlineData("{ \"changeTrackingId\": \"17\", \"item\": { \"path\": \"/src/Foo.cs\" } }")]
    [InlineData("{ \"changeTrackingId\": 0, \"item\": { \"path\": \"/src/Foo.cs\" } }")]
    [InlineData("{ \"changeTrackingId\": -1, \"item\": { \"path\": \"/src/Foo.cs\" } }")]
    [InlineData("{ \"changeTrackingId\": 17.5, \"item\": { \"path\": \"/src/Foo.cs\" } }")]
    public async Task Inline_anchor_snapshot_fails_closed_on_malformed_change_entry(string entry)
    {
        var handler = new FakeHttpMessageHandler()
            .OnJson(
                HttpMethod.Get,
                "/iterations/3/changes",
                $$"""{ "changeEntries": [ {{entry}} ], "nextSkip": 0, "nextTop": 0 }"""
            )
            .OnJson(
                HttpMethod.Get,
                "/iterations",
                """{ "value": [ { "id": 3, "sourceRefCommit": { "commitId": "head-42" } } ] }"""
            )
            .OnJson(HttpMethod.Get, "/pullrequests/42?", """{ "lastMergeSourceCommit": { "commitId": "head-42" } }""");

        var act = () => Provider(handler).GetInlineAnchorSnapshotAsync(Repo, "42", "head-42", CancellationToken.None);

        _ = await act.Should().ThrowAsync<InvalidDataException>();
        handler.CountRequests("/iterations/3/changes").Should().Be(1);
        handler.CountRequests("/pullrequests/42?").Should().Be(1);
    }

    [Theory]
    [InlineData(-1, 2000)]
    [InlineData(-1, -1)]
    [InlineData(1, -1)]
    [InlineData(1, 2001)]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    public async Task Inline_anchor_snapshot_fails_closed_on_invalid_first_page_continuation(int nextSkip, int nextTop)
    {
        var handler = new FakeHttpMessageHandler()
            .OnJson(
                HttpMethod.Get,
                "/iterations/3/changes",
                $$"""{ "changeEntries": [], "nextSkip": {{nextSkip}}, "nextTop": {{nextTop}} }"""
            )
            .OnJson(
                HttpMethod.Get,
                "/iterations",
                """{ "value": [ { "id": 3, "sourceRefCommit": { "commitId": "head-42" } } ] }"""
            )
            .OnJson(HttpMethod.Get, "/pullrequests/42?", """{ "lastMergeSourceCommit": { "commitId": "head-42" } }""");

        var act = () => Provider(handler).GetInlineAnchorSnapshotAsync(Repo, "42", "head-42", CancellationToken.None);

        _ = await act.Should().ThrowAsync<InvalidDataException>();
        handler.CountRequests("/iterations/3/changes").Should().Be(1);
        handler.CountRequests("/pullrequests/42?").Should().Be(1);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(1)]
    public async Task Inline_anchor_snapshot_fails_closed_when_continuation_skip_does_not_advance(int secondNextSkip)
    {
        var handler = new FakeHttpMessageHandler()
            .OnSequence(
                HttpMethod.Get,
                "/iterations/3/changes",
                (HttpStatusCode.OK, """{ "changeEntries": [], "nextSkip": 2, "nextTop": 2000 }"""),
                (HttpStatusCode.OK, $$"""{ "changeEntries": [], "nextSkip": {{secondNextSkip}}, "nextTop": 2000 }""")
            )
            .OnJson(
                HttpMethod.Get,
                "/iterations",
                """{ "value": [ { "id": 3, "sourceRefCommit": { "commitId": "head-42" } } ] }"""
            )
            .OnJson(HttpMethod.Get, "/pullrequests/42?", """{ "lastMergeSourceCommit": { "commitId": "head-42" } }""");

        var act = () => Provider(handler).GetInlineAnchorSnapshotAsync(Repo, "42", "head-42", CancellationToken.None);

        _ = await act.Should().ThrowAsync<InvalidDataException>();
        handler.CountRequests("/iterations/3/changes").Should().Be(2);
        handler.CountRequests("/pullrequests/42?").Should().Be(1);
    }

    [Theory]
    [InlineData("\"3\"", "0", "0")]
    [InlineData("3", "\"0\"", "0")]
    [InlineData("3", "0", "\"0\"")]
    public async Task Inline_anchor_snapshot_fails_closed_when_numeric_identity_or_paging_fields_are_not_numbers(
        string iterationId,
        string nextSkip,
        string nextTop
    )
    {
        var handler = new FakeHttpMessageHandler()
            .OnJson(
                HttpMethod.Get,
                "/iterations/3/changes",
                $$"""
                { "changeEntries": [
                    { "changeTrackingId": 17, "item": { "path": "/src/Foo.cs" } }
                  ], "nextSkip": {{nextSkip}}, "nextTop": {{nextTop}} }
                """
            )
            .OnJson(
                HttpMethod.Get,
                "/iterations",
                $$"""{ "value": [ { "id": {{iterationId}}, "sourceRefCommit": { "commitId": "head-42" } } ] }"""
            )
            .OnJson(HttpMethod.Get, "/pullrequests/42?", """{ "lastMergeSourceCommit": { "commitId": "head-42" } }""");

        var act = () => Provider(handler).GetInlineAnchorSnapshotAsync(Repo, "42", "head-42", CancellationToken.None);

        _ = await act.Should().ThrowAsync<InvalidDataException>();
    }

    [Theory]
    [InlineData(
        """
            { "changeEntries": [
                { "changeTrackingId": 17, "item": { "path": "/src/Foo.cs" } },
                { "changeTrackingId": 17, "item": { "path": "/src/Other.cs" } }
              ], "nextSkip": 0, "nextTop": 0 }
            """
    )]
    [InlineData(
        """
            { "changeEntries": [
                { "changeTrackingId": 17, "item": { "path": "/src/Foo.cs" } },
                { "changeTrackingId": 18, "item": { "path": "/src/Foo.cs" } }
              ], "nextSkip": 0, "nextTop": 0 }
            """
    )]
    public async Task Inline_anchor_snapshot_fails_closed_on_duplicate_change_or_file_identity(string changes)
    {
        var handler = new FakeHttpMessageHandler()
            .OnJson(HttpMethod.Get, "/iterations/3/changes", changes)
            .OnJson(
                HttpMethod.Get,
                "/iterations",
                """{ "value": [ { "id": 3, "sourceRefCommit": { "commitId": "head-42" } } ] }"""
            )
            .OnJson(HttpMethod.Get, "/pullrequests/42?", """{ "lastMergeSourceCommit": { "commitId": "head-42" } }""");

        var act = () => Provider(handler).GetInlineAnchorSnapshotAsync(Repo, "42", "head-42", CancellationToken.None);

        _ = await act.Should().ThrowAsync<InvalidDataException>();
    }

    [Theory]
    [InlineData(17, "/src/Second.cs")]
    [InlineData(18, "/src/First.cs")]
    public async Task Inline_anchor_snapshot_fails_closed_on_duplicate_identity_across_pages(
        int secondTrackingId,
        string secondPath
    )
    {
        var handler = new FakeHttpMessageHandler()
            .OnSequence(
                HttpMethod.Get,
                "/iterations/3/changes",
                (
                    HttpStatusCode.OK,
                    """
                    { "changeEntries": [
                        { "changeTrackingId": 17, "item": { "path": "/src/First.cs" } }
                      ], "nextSkip": 1, "nextTop": 2000 }
                    """
                ),
                (
                    HttpStatusCode.OK,
                    $$"""
                    { "changeEntries": [
                        { "changeTrackingId": {{secondTrackingId}}, "item": { "path": "{{secondPath}}" } }
                      ], "nextSkip": 0, "nextTop": 0 }
                    """
                )
            )
            .OnJson(
                HttpMethod.Get,
                "/iterations",
                """{ "value": [ { "id": 3, "sourceRefCommit": { "commitId": "head-42" } } ] }"""
            )
            .OnJson(HttpMethod.Get, "/pullrequests/42?", """{ "lastMergeSourceCommit": { "commitId": "head-42" } }""");

        var act = () => Provider(handler).GetInlineAnchorSnapshotAsync(Repo, "42", "head-42", CancellationToken.None);

        _ = await act.Should().ThrowAsync<InvalidDataException>();
        handler.CountRequests("/iterations/3/changes").Should().Be(2);
    }

    [Fact]
    public async Task Inline_anchor_snapshot_throws_instead_of_returning_partial_inventory_when_page_bound_is_exhausted()
    {
        const string page = """
            { "changeEntries": [
                { "changeTrackingId": 17, "item": { "path": "/src/Foo.cs" } }
              ], "nextSkip": 1, "nextTop": 2000 }
            """;
        var handler = new FakeHttpMessageHandler()
            .OnJson(HttpMethod.Get, "/iterations/3/changes", page)
            .OnJson(
                HttpMethod.Get,
                "/iterations",
                """{ "value": [ { "id": 3, "sourceRefCommit": { "commitId": "head-42" } } ] }"""
            )
            .OnJson(HttpMethod.Get, "/pullrequests/42?", """{ "lastMergeSourceCommit": { "commitId": "head-42" } }""");

        var act = () =>
            Provider(handler, maxPagesPerPoll: 1)
                .GetInlineAnchorSnapshotAsync(Repo, "42", "head-42", CancellationToken.None);

        _ = await act.Should().ThrowAsync<InvalidOperationException>();
        handler.CountRequests("/iterations/3/changes").Should().Be(1);
    }

    [Fact]
    public async Task Inline_anchor_snapshot_fails_closed_when_exact_head_matches_multiple_iterations()
    {
        var handler = new FakeHttpMessageHandler()
            .OnJson(
                HttpMethod.Get,
                "/iterations",
                """
                { "value": [
                    { "id": 2, "sourceRefCommit": { "commitId": "head-42" } },
                    { "id": 3, "sourceRefCommit": { "commitId": "head-42" } }
                  ] }
                """
            )
            .OnJson(HttpMethod.Get, "/pullrequests/42?", """{ "lastMergeSourceCommit": { "commitId": "head-42" } }""");

        var snapshot = await Provider(handler)
            .GetInlineAnchorSnapshotAsync(Repo, "42", "head-42", CancellationToken.None);

        snapshot.Files.Should().BeEmpty();
        handler.CountRequests("/changes").Should().Be(0);
    }

    [Fact]
    public async Task Inline_anchor_snapshot_discards_inventory_when_head_changes_during_read()
    {
        var handler = new FakeHttpMessageHandler()
            .OnSequence(
                HttpMethod.Get,
                "/pullrequests/42?",
                (HttpStatusCode.OK, """{ "lastMergeSourceCommit": { "commitId": "head-42" } }"""),
                (HttpStatusCode.OK, """{ "lastMergeSourceCommit": { "commitId": "head-43" } }""")
            )
            .OnJson(
                HttpMethod.Get,
                "/iterations/3/changes",
                """
                { "changeEntries": [
                    { "changeTrackingId": 17, "item": { "path": "/src/Foo.cs" } }
                  ], "nextSkip": 0, "nextTop": 0 }
                """
            )
            .OnJson(
                HttpMethod.Get,
                "/iterations",
                """{ "value": [ { "id": 3, "sourceRefCommit": { "commitId": "head-42" } } ] }"""
            );

        var snapshot = await Provider(handler)
            .GetInlineAnchorSnapshotAsync(Repo, "42", "head-42", CancellationToken.None);

        snapshot.HeadSha.Should().Be("head-43");
        snapshot.Files.Should().BeEmpty();
    }

    [Fact]
    public async Task GetCurrentHeadSha_reads_the_source_branch_commit_the_poll_also_records()
    {
        // MockPrProvider proves the guard's LOGIC and never touches the payload. If
        // `lastMergeSourceCommit.commitId` stopped being read, this parser would return null, the guard would
        // read that as INDETERMINATE, and every ADO review would sail through unchecked with every existing
        // test still green — the #331 defect, silently restored.
        var handler = new FakeHttpMessageHandler().OnJson(
            HttpMethod.Get,
            "/pullrequests/42",
            """{ "pullRequestId": 42, "lastMergeSourceCommit": { "commitId": "ad0c0ffee" } }"""
        );

        var head = await Provider(handler).GetCurrentHeadShaAsync(Repo, "42", CancellationToken.None);

        // Deliberately the SAME field ListOpenPullRequestsAsync records as HeadSha: comparing two different
        // notions of "head" would manufacture disagreements out of field semantics rather than out of a push.
        head.Should().Be("ad0c0ffee");
    }

    [Theory]
    [InlineData("""{ "pullRequestId": 42 }""")]
    [InlineData("""{ "pullRequestId": 42, "lastMergeSourceCommit": {} }""")]
    [InlineData("""{ "pullRequestId": 42, "lastMergeSourceCommit": { "commitId": "" } }""")]
    [InlineData("""{ "pullRequestId": 42, "lastMergeSourceCommit": { "commitId": "  " } }""")]
    [InlineData("""{ "pullRequestId": 42, "lastMergeSourceCommit": { "commitId": null } }""")]
    public async Task GetCurrentHeadSha_is_null_when_the_payload_carries_no_commit_rather_than_throwing(string payload)
    {
        var handler = new FakeHttpMessageHandler().OnJson(HttpMethod.Get, "/pullrequests/42", payload);

        var head = await Provider(handler).GetCurrentHeadShaAsync(Repo, "42", CancellationToken.None);

        // Reachable in practice: ADO refreshes lastMergeSourceCommit on merge evaluation, so a freshly
        // created PR can genuinely carry none yet. That is indeterminate, not an error.
        head.Should().BeNull();
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task GetCurrentHeadSha_propagates_a_non_success_response_rather_than_flattening_it_to_null(
        HttpStatusCode status
    )
    {
        var handler = new FakeHttpMessageHandler().OnJson(
            HttpMethod.Get,
            "/pullrequests/42",
            """{ "message": "nope" }""",
            status
        );

        var act = () => Provider(handler).GetCurrentHeadShaAsync(Repo, "42", CancellationToken.None);

        // "Unreachable" must never be reported as "nothing contradicts the recorded head". The executor owns
        // the decision to treat an outage as indeterminate; pre-making it here would erase the distinction.
        _ = await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task GetPrState_maps_a_completed_pr_to_merged()
    {
        var handler = new FakeHttpMessageHandler().OnJson(
            HttpMethod.Get,
            "/pullrequests/42",
            """{ "pullRequestId": 42, "status": "completed" }"""
        );

        var state = await Provider(handler).GetPrStateAsync(Repo, "42", CancellationToken.None);

        state.Should().Be(PrLifecycle.Merged);
    }

    [Fact]
    public async Task GetPrState_maps_an_abandoned_pr_to_abandoned()
    {
        var handler = new FakeHttpMessageHandler().OnJson(
            HttpMethod.Get,
            "/pullrequests/42",
            """{ "pullRequestId": 42, "status": "abandoned" }"""
        );

        var state = await Provider(handler).GetPrStateAsync(Repo, "42", CancellationToken.None);

        state.Should().Be(PrLifecycle.Abandoned);
    }

    [Fact]
    public async Task GetPrState_sends_the_single_pr_request_ado_requires()
    {
        var handler = new FakeHttpMessageHandler().OnJson(
            HttpMethod.Get,
            "/pullrequests/42",
            """{ "pullRequestId": 42, "status": "active" }"""
        );

        _ = await Provider(handler).GetPrStateAsync(Repo, "42", CancellationToken.None);

        var request = handler.Requests.Should().ContainSingle().Subject;
        request.Method.Should().Be(HttpMethod.Get);
        request
            .Uri.ToString()
            .Should()
            .StartWith("https://dev.azure.com/contoso/Platform/_apis/git/repositories/widgets/pullrequests/42");
        request.Uri.Query.Should().Contain("api-version=7.1");
        request.Authorization.Should().StartWith("Basic ", "ADO PATs/bearer tokens are sent via basic auth");
    }

    [Fact]
    public async Task RecencyCutoff_resolves_ado_updated_from_the_last_push_for_old_prs_only()
    {
        var cutoff = new DateTimeOffset(2026, 7, 4, 0, 0, 0, TimeSpan.Zero);
        var handler = new FakeHttpMessageHandler()
            .OnJson(HttpMethod.Get, "/pushes", """{ "value": [ { "date": "2026-07-10T12:00:00Z" } ] }""")
            .OnJson(HttpMethod.Get, "/pullrequests", DatedPrs);

        var page = await Provider(handler)
            .ListOpenPullRequestsAsync(Request(recencyCutoff: cutoff), CancellationToken.None);

        // PR 42 (opened 2026-06-01, before the window) → its source branch's last push (2026-07-10) becomes UpdatedAt.
        page.PullRequests[0].PrId.Should().Be("42");
        page.PullRequests[0].UpdatedAt.Should().Be(new DateTimeOffset(2026, 7, 10, 12, 0, 0, TimeSpan.Zero));
        // PR 50 (opened 2026-07-09, inside the window) → no extra call, UpdatedAt stays null.
        page.PullRequests[1].PrId.Should().Be("50");
        page.PullRequests[1].UpdatedAt.Should().BeNull();

        handler.CountRequests("feature-42").Should().Be(1, "the old PR's source-branch last push is fetched");
        handler.CountRequests("feature-50").Should().Be(0, "the recent PR skips the extra call");
    }

    [Fact]
    public async Task RecencyCutoff_keeps_an_old_pr_whose_push_date_cannot_be_fetched()
    {
        var cutoff = new DateTimeOffset(2026, 7, 4, 0, 0, 0, TimeSpan.Zero);
        var handler = new FakeHttpMessageHandler()
            .OnJson(HttpMethod.Get, "/pushes", """{"message":"nope"}""", HttpStatusCode.NotFound)
            .OnJson(HttpMethod.Get, "/pullrequests", OneOldPr);

        var page = await Provider(handler)
            .ListOpenPullRequestsAsync(Request(recencyCutoff: cutoff), CancellationToken.None);

        // Keep-on-uncertainty WITHOUT fabrication: when the last-push lookup fails for an old PR, the provider
        // leaves the recency signal indeterminate — BOTH UpdatedAt and CreatedAt null — so
        // PrPollingService.ApplyRecencyFilter's "activity (UpdatedAt ?? CreatedAt) is null ⇒ keep" path applies.
        // It must NOT fabricate a boundary timestamp (an earlier `?? cutoff` did, conflating "unknown" with
        // "active exactly at the cutoff"), nor fall back to the stale opened-date (which would drop a
        // possibly-active PR).
        page.PullRequests[0].UpdatedAt.Should().BeNull("an unknown push date must not be fabricated");
        page.PullRequests[0].CreatedAt.Should().BeNull("the recency signal is indeterminate, so the filter keeps it");
    }

    [Fact]
    public async Task RecencyCutoff_keeps_an_old_pr_with_no_source_ref()
    {
        // An old PR whose sourceRefName is missing/blank can't be push-dated, so its recency is indeterminate:
        // both signals null ⇒ ApplyRecencyFilter keeps it, rather than dropping on the stale opened-date. No
        // push lookup is attempted (there is no ref to query).
        var cutoff = new DateTimeOffset(2026, 7, 4, 0, 0, 0, TimeSpan.Zero);
        var handler = new FakeHttpMessageHandler().OnJson(
            HttpMethod.Get,
            "/pullrequests",
            """
            { "value": [ { "pullRequestId": 42, "status": "active", "creationDate": "2026-06-01T00:00:00Z",
                "lastMergeSourceCommit": { "commitId": "head-42" }, "lastMergeTargetCommit": { "commitId": "base-42" } } ] }
            """
        );

        var page = await Provider(handler)
            .ListOpenPullRequestsAsync(Request(recencyCutoff: cutoff), CancellationToken.None);

        page.PullRequests[0].UpdatedAt.Should().BeNull();
        page.PullRequests[0].CreatedAt.Should().BeNull("no source ref ⇒ recency indeterminate ⇒ kept");
        handler.CountRequests("/pushes").Should().Be(0, "no source ref means no push lookup is attempted");
    }

    [Fact]
    public async Task No_recency_cutoff_means_no_extra_commit_calls()
    {
        var handler = new FakeHttpMessageHandler().OnJson(HttpMethod.Get, "/pullrequests", DatedPrs);

        var page = await Provider(handler).ListOpenPullRequestsAsync(Request(), CancellationToken.None);

        page.PullRequests.Should().OnlyContain(p => p.UpdatedAt == null, "with no window ADO never fetches push dates");
        handler.CountRequests("/pushes").Should().Be(0);
    }

    [Fact]
    public async Task RecencyLookups_run_with_bounded_concurrency()
    {
        // Many old PRs each need a /pushes lookup. The lookups must overlap (run concurrently) — a sequential
        // implementation would show a max concurrency of 1 — but never exceed the provider's concurrency cap.
        var cutoff = new DateTimeOffset(2026, 7, 4, 0, 0, 0, TimeSpan.Zero);
        const int oldPrCount = 20;
        var items = string.Join(
            ",",
            Enumerable
                .Range(1, oldPrCount)
                .Select(i =>
                    "{ \"pullRequestId\": "
                    + i
                    + ", \"status\": \"active\", \"creationDate\": \"2026-06-01T00:00:00Z\", "
                    + "\"sourceRefName\": \"refs/heads/f"
                    + i
                    + "\", "
                    + "\"lastMergeSourceCommit\": { \"commitId\": \"h"
                    + i
                    + "\" }, "
                    + "\"lastMergeTargetCommit\": { \"commitId\": \"b"
                    + i
                    + "\" } }"
                )
        );
        var prsJson = "{ \"value\": [ " + items + " ] }";

        using var handler = new ConcurrencyTrackingHandler(prsJson);
        var provider = new AdoPrProvider(
            new HttpClient(handler),
            new FakeOAuthTokenProvider("ado", "ado-token-abc"),
            LoggerFactory.CreateLogger<AdoPrProvider>()
        );

        var page = await provider.ListOpenPullRequestsAsync(Request(recencyCutoff: cutoff), CancellationToken.None);

        page.PullRequests.Should().HaveCount(oldPrCount);
        handler.TotalPushes.Should().Be(oldPrCount, "every eligible old PR receives exactly one /pushes lookup");
        handler
            .MaxConcurrentPushes.Should()
            .BeGreaterThan(1, "the per-PR push lookups run concurrently, not sequentially");
        handler.MaxConcurrentPushes.Should().BeLessThanOrEqualTo(6, "concurrency is bounded by the provider's cap");
    }

    [Fact]
    public async Task Timed_out_push_lookup_keeps_the_pr_and_does_not_fault_the_poll()
    {
        // An HttpClient timeout surfaces as a TaskCanceledException with the caller's token NOT cancelled. It
        // must be treated as a failed lookup (recency indeterminate ⇒ keep the PR), NOT propagated to fault the
        // whole poll via Task.WhenAll.
        var cutoff = new DateTimeOffset(2026, 7, 4, 0, 0, 0, TimeSpan.Zero);
        using var handler = new CancelOnPushHandler(OneOldPr);
        var provider = new AdoPrProvider(
            new HttpClient(handler),
            new FakeOAuthTokenProvider("ado", "ado-token-abc"),
            LoggerFactory.CreateLogger<AdoPrProvider>()
        );

        var page = await provider.ListOpenPullRequestsAsync(Request(recencyCutoff: cutoff), CancellationToken.None);

        page.PullRequests.Should().ContainSingle();
        page.PullRequests[0].UpdatedAt.Should().BeNull();
        page.PullRequests[0]
            .CreatedAt.Should()
            .BeNull("a timed-out push lookup leaves recency indeterminate ⇒ the PR is kept");
    }

    [Fact]
    public async Task Caller_cancellation_during_push_lookup_propagates()
    {
        // A REAL caller cancellation (the poll was aborted) must propagate, not be swallowed as a failed lookup.
        var cutoff = new DateTimeOffset(2026, 7, 4, 0, 0, 0, TimeSpan.Zero);
        using var cts = new CancellationTokenSource();
        using var handler = new CancelOnPushHandler(OneOldPr, cts);
        var provider = new AdoPrProvider(
            new HttpClient(handler),
            new FakeOAuthTokenProvider("ado", "ado-token-abc"),
            LoggerFactory.CreateLogger<AdoPrProvider>()
        );

        var act = async () => await provider.ListOpenPullRequestsAsync(Request(recencyCutoff: cutoff), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    /// <summary>
    /// Returns the PR list for <c>/pullrequests</c>, then simulates a cancellation on the first <c>/pushes</c>
    /// call: with no <paramref name="cancelOnPush"/> it throws a <see cref="TaskCanceledException"/> WITHOUT
    /// cancelling the caller's token (an HttpClient timeout); with one, it cancels that token first (a real
    /// caller cancellation).
    /// </summary>
    private sealed class CancelOnPushHandler(string prsJson, CancellationTokenSource? cancelOnPush = null)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            if (request.RequestUri!.ToString().Contains("/pullrequests", StringComparison.Ordinal))
            {
                return Task.FromResult(
                    new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(prsJson) }
                );
            }

            cancelOnPush?.Cancel();
            throw new TaskCanceledException("simulated push-lookup cancellation");
        }
    }

    /// <summary>
    /// Returns the PR list for <c>/pullrequests</c> and, for each <c>/pushes</c> call, records the total number
    /// of push lookups and the peak number simultaneously in-flight (holding each briefly so genuine overlap is
    /// observable).
    /// </summary>
    private sealed class ConcurrencyTrackingHandler(string prsJson) : HttpMessageHandler
    {
        private readonly object _lock = new();
        private int _current;

        public int MaxConcurrentPushes { get; private set; }

        public int TotalPushes { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            var uri = request.RequestUri!.ToString();
            if (uri.Contains("/pullrequests", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(prsJson) };
            }

            lock (_lock)
            {
                _current++;
                TotalPushes++;
                MaxConcurrentPushes = Math.Max(MaxConcurrentPushes, _current);
            }

            try
            {
                await Task.Delay(30, cancellationToken);
            }
            finally
            {
                lock (_lock)
                {
                    _current--;
                }
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{ \"value\": [ { \"date\": \"2026-07-10T12:00:00Z\" } ] }"),
            };
        }
    }
}
