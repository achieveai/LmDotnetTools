using System.Net;
using System.Text;
using AchieveAi.LmDotnetTools.LmTestUtils.Logging;
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
        : base(output)
    {
    }

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

    private static PrPollRequest Request(OpaqueCursor? cursor = null, DateTimeOffset? recencyCutoff = null) => new()
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
              "lastMergeSourceCommit": { "commitId": "head-42" }, "lastMergeTargetCommit": { "commitId": "base-42" } },
            { "pullRequestId": 50, "status": "active", "creationDate": "2026-07-09T00:00:00Z",
              "lastMergeSourceCommit": { "commitId": "head-50" }, "lastMergeTargetCommit": { "commitId": "base-50" } }
          ]
        }
        """;

    private const string OneOldPr = """
        { "value": [ { "pullRequestId": 42, "status": "active", "creationDate": "2026-06-01T00:00:00Z",
            "lastMergeSourceCommit": { "commitId": "head-42" }, "lastMergeTargetCommit": { "commitId": "base-42" } } ] }
        """;

    private AdoPrProvider Provider(FakeHttpMessageHandler handler) =>
        new(
            new HttpClient(handler),
            new FakeOAuthTokenProvider("ado", "ado-token-abc"),
            LoggerFactory.CreateLogger<AdoPrProvider>());

    [Fact]
    public void Provider_id_is_ado()
    {
        Provider(new FakeHttpMessageHandler()).Provider.Should().Be("ado");
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

    [Fact]
    public async Task ListOpenPullRequests_sends_the_request_ado_requires()
    {
        var handler = new FakeHttpMessageHandler().OnJson(HttpMethod.Get, "/pullrequests", TwoActivePrs);

        _ = await Provider(handler).ListOpenPullRequestsAsync(Request(), CancellationToken.None);

        var request = handler.Requests.Should().ContainSingle().Subject;
        request.Method.Should().Be(HttpMethod.Get);
        request.Uri.ToString().Should()
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
            HttpMethod.Get, "/pullrequests", """{"message":"unauthorized"}""", HttpStatusCode.Unauthorized);

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
                _ => JsonResponse(page2))
            .On(
                req => req.RequestUri!.ToString().Contains("/pullrequests", StringComparison.Ordinal),
                _ => JsonResponse(page1, ("x-ms-continuationtoken", "TOKEN2")));

        var page = await Provider(handler).ListOpenPullRequestsAsync(Request(), CancellationToken.None);

        page.PullRequests.Select(p => p.PrId).Should().BeEquivalentTo(["42", "50"], "both pages are accumulated");
        handler.CountRequests("/pullrequests").Should().Be(2, "the provider followed exactly one continuation token");
        page.NextCursor.HighWaterMark.Should().Be("50", "the highest pullRequestId across all pages");
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
        var handler = new FakeHttpMessageHandler().OnJson(HttpMethod.Get, "/pullrequests", """{"count":0,"value":[]}""");

        var page = await Provider(handler).ListOpenPullRequestsAsync(Request(), CancellationToken.None);

        page.PullRequests.Should().BeEmpty();
        page.NextCursor.CursorVersion.Should().Be(PrPollingService.CursorVersion);
    }

    [Fact]
    public async Task GetPrState_maps_an_active_pr_to_open()
    {
        var handler = new FakeHttpMessageHandler().OnJson(
            HttpMethod.Get, "/pullrequests/42", """{ "pullRequestId": 42, "status": "active" }""");

        var state = await Provider(handler).GetPrStateAsync(Repo, "42", CancellationToken.None);

        state.Should().Be(PrLifecycle.Open);
    }

    [Fact]
    public async Task GetPrState_maps_a_completed_pr_to_merged()
    {
        var handler = new FakeHttpMessageHandler().OnJson(
            HttpMethod.Get, "/pullrequests/42", """{ "pullRequestId": 42, "status": "completed" }""");

        var state = await Provider(handler).GetPrStateAsync(Repo, "42", CancellationToken.None);

        state.Should().Be(PrLifecycle.Merged);
    }

    [Fact]
    public async Task GetPrState_maps_an_abandoned_pr_to_abandoned()
    {
        var handler = new FakeHttpMessageHandler().OnJson(
            HttpMethod.Get, "/pullrequests/42", """{ "pullRequestId": 42, "status": "abandoned" }""");

        var state = await Provider(handler).GetPrStateAsync(Repo, "42", CancellationToken.None);

        state.Should().Be(PrLifecycle.Abandoned);
    }

    [Fact]
    public async Task GetPrState_sends_the_single_pr_request_ado_requires()
    {
        var handler = new FakeHttpMessageHandler().OnJson(
            HttpMethod.Get, "/pullrequests/42", """{ "pullRequestId": 42, "status": "active" }""");

        _ = await Provider(handler).GetPrStateAsync(Repo, "42", CancellationToken.None);

        var request = handler.Requests.Should().ContainSingle().Subject;
        request.Method.Should().Be(HttpMethod.Get);
        request.Uri.ToString().Should()
            .StartWith("https://dev.azure.com/contoso/Platform/_apis/git/repositories/widgets/pullrequests/42");
        request.Uri.Query.Should().Contain("api-version=7.1");
        request.Authorization.Should().StartWith("Basic ", "ADO PATs/bearer tokens are sent via basic auth");
    }

    [Fact]
    public async Task RecencyCutoff_resolves_ado_updated_from_the_last_push_for_old_prs_only()
    {
        var cutoff = new DateTimeOffset(2026, 7, 4, 0, 0, 0, TimeSpan.Zero);
        var handler = new FakeHttpMessageHandler()
            .OnJson(HttpMethod.Get, "/commits/head-42", """{ "committer": { "date": "2026-07-10T12:00:00Z" } }""")
            .OnJson(HttpMethod.Get, "/pullrequests", DatedPrs);

        var page = await Provider(handler)
            .ListOpenPullRequestsAsync(Request(recencyCutoff: cutoff), CancellationToken.None);

        // PR 42 (opened 2026-06-01, before the window) → its last push (2026-07-10) becomes UpdatedAt.
        page.PullRequests[0].PrId.Should().Be("42");
        page.PullRequests[0].UpdatedAt.Should().Be(new DateTimeOffset(2026, 7, 10, 12, 0, 0, TimeSpan.Zero));
        // PR 50 (opened 2026-07-09, inside the window) → no extra call, UpdatedAt stays null.
        page.PullRequests[1].PrId.Should().Be("50");
        page.PullRequests[1].UpdatedAt.Should().BeNull();

        handler.CountRequests("/commits/head-42").Should().Be(1, "the old PR's last-push date is fetched");
        handler.CountRequests("/commits/head-50").Should().Be(0, "the recent PR skips the extra call");
    }

    [Fact]
    public async Task RecencyCutoff_keeps_an_old_pr_whose_push_date_cannot_be_fetched()
    {
        var cutoff = new DateTimeOffset(2026, 7, 4, 0, 0, 0, TimeSpan.Zero);
        var handler = new FakeHttpMessageHandler()
            .OnJson(HttpMethod.Get, "/commits/head-42", """{"message":"nope"}""", HttpStatusCode.NotFound)
            .OnJson(HttpMethod.Get, "/pullrequests", OneOldPr);

        var page = await Provider(handler)
            .ListOpenPullRequestsAsync(Request(recencyCutoff: cutoff), CancellationToken.None);

        // Keep-on-uncertainty: the recency filter uses UpdatedAt ?? CreatedAt, and CreatedAt is < cutoff here,
        // so an unfetchable push date must resolve to the cutoff to KEEP the PR — leaving it null would fall
        // back to the (older) CreatedAt and drop an active PR.
        page.PullRequests[0].UpdatedAt.Should().Be(cutoff);
    }

    [Fact]
    public async Task No_recency_cutoff_means_no_extra_commit_calls()
    {
        var handler = new FakeHttpMessageHandler().OnJson(HttpMethod.Get, "/pullrequests", DatedPrs);

        var page = await Provider(handler).ListOpenPullRequestsAsync(Request(), CancellationToken.None);

        page.PullRequests.Should().OnlyContain(p => p.UpdatedAt == null, "with no window ADO never fetches push dates");
        handler.CountRequests("/commits/").Should().Be(0);
    }
}
