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

    /// <summary>
    /// The PR's own account of itself, ADO-side. Two shapes differ from GitHub and both had to be handled:
    /// the description field is <c>description</c> rather than <c>body</c>, and the target branch arrives as
    /// a full ref (<c>refs/heads/main</c>) that has to be shortened before it reads as a branch name in the
    /// brief. A ref that is not under <c>refs/heads/</c> passes through unchanged rather than being mangled.
    /// </summary>
    [Fact]
    public async Task ListOpenPullRequests_carries_the_stated_intent_and_shortens_the_target_ref()
    {
        var handler = new FakeHttpMessageHandler().OnJson(
            HttpMethod.Get,
            "/pullrequests",
            """
            { "value": [
                { "pullRequestId": 42, "status": "active",
                  "title": "Revert the Contoso revenue report to the Q3 layout",
                  "description": "Rolls back the Q4 rewrite; drill-through was broken on three pages.",
                  "targetRefName": "refs/heads/release/2026.08",
                  "lastMergeSourceCommit": { "commitId": "head-42" },
                  "lastMergeTargetCommit": { "commitId": "base-42" } },
                { "pullRequestId": 50, "status": "active",
                  "targetRefName": "refs/pull/50/merge",
                  "lastMergeSourceCommit": { "commitId": "head-50" },
                  "lastMergeTargetCommit": { "commitId": "base-50" } } ] }
            """
        );

        var page = await Provider(handler).ListOpenPullRequestsAsync(Request(), CancellationToken.None);

        page.PullRequests[0].Title.Should().Be("Revert the Contoso revenue report to the Q3 layout");
        page.PullRequests[0]
            .Description.Should()
            .Be("Rolls back the Q4 rewrite; drill-through was broken on three pages.");
        page.PullRequests[0]
            .TargetBranch.Should()
            .Be("release/2026.08", "the brief names a branch, and refs/heads/ is plumbing the reviewer never types");

        page.PullRequests[1].Title.Should().BeNull("a PR ADO gave no title for stays unknown");
        page.PullRequests[1].Description.Should().BeNull();
        page.PullRequests[1]
            .TargetBranch.Should()
            .Be("refs/pull/50/merge", "a ref outside refs/heads/ is passed through rather than silently mangled");
    }

    /// <summary>
    /// The confidentiality trust signal, ADO-side. Both fields come straight off the PR-list payload per the
    /// REST 7.1 <c>GitPullRequest</c> contract: <c>forkSource</c> is present <b>only</b> for a PR opened from a
    /// fork, and <c>repository.project.visibility</c> is <c>"private"</c> or <c>"public"</c>.
    /// <para>
    /// PR 42 is the case that matters for a corporate ADO org — no fork, private project — and it is the case
    /// that was silently unreachable: the daemon defaulted both signals to the fail-closed value and refused
    /// every configured sibling on all 138 NOVA runs.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ListOpenPullRequests_reads_the_trust_signal_from_fork_source_and_project_visibility()
    {
        var handler = new FakeHttpMessageHandler().OnJson(
            HttpMethod.Get,
            "/pullrequests",
            """
            { "value": [
                { "pullRequestId": 42, "status": "active",
                  "repository": { "name": "Nova", "project": { "name": "Weve_DA", "visibility": "private" } },
                  "lastMergeSourceCommit": { "commitId": "head-42" },
                  "lastMergeTargetCommit": { "commitId": "base-42" } },
                { "pullRequestId": 50, "status": "active",
                  "forkSource": { "repository": { "name": "Nova-fork" }, "name": "refs/heads/feature" },
                  "repository": { "name": "Nova", "project": { "name": "Weve_DA", "visibility": "private" } },
                  "lastMergeSourceCommit": { "commitId": "head-50" },
                  "lastMergeTargetCommit": { "commitId": "base-50" } },
                { "pullRequestId": 51, "status": "active",
                  "lastMergeSourceCommit": { "commitId": "head-51" },
                  "lastMergeTargetCommit": { "commitId": "base-51" } } ] }
            """
        );

        var page = await Provider(handler).ListOpenPullRequestsAsync(Request(), CancellationToken.None);

        page.PullRequests[0].IsForkPr.Should().BeFalse("ADO omits forkSource for a PR opened inside the repo");
        page.PullRequests[0].IsTargetRepoPublic.Should().BeFalse("the project's visibility is private");

        page.PullRequests[1].IsForkPr.Should().BeTrue("forkSource is present, so the head lives in a fork");

        page.PullRequests[2]
            .IsForkPr.Should()
            .BeNull(
                "absence of forkSource only means 'not a fork' in a payload we recognize; with no repository "
                    + "object either, the shape is unknown and so is the answer — the poller, not the provider, "
                    + "turns that into the fail-closed default"
            );
        page.PullRequests[2].IsTargetRepoPublic.Should().BeNull("no project object means visibility is unknown");
    }

    /// <summary>
    /// The shape ADO's PR <b>list</b> actually returns for <c>repository.project</c>: a shallow
    /// <c>TeamProjectReference</c> of <c>id</c> + <c>name</c> + <c>state</c>, and <b>no</b>
    /// <c>visibility</c> — every sample response on the REST 7.1 "Get Pull Requests" page shows exactly
    /// these three keys. Nothing here is private/public; the field simply is not serialized.
    /// </summary>
    private const string PrWithProjectButNoVisibility = """
        { "value": [
            { "pullRequestId": 42, "status": "active",
              "title": "Revert the Contoso revenue report to the Q3 layout",
              "description": "Rolls back the Q4 rewrite; drill-through was broken on three pages.",
              "createdBy": { "uniqueName": "jane.doe@contoso.com", "displayName": "Jane Doe" },
              "repository": { "id": "repo-guid-1", "name": "widgets",
                "project": { "id": "proj-guid-1", "name": "Platform", "state": "wellFormed" } },
              "lastMergeSourceCommit": { "commitId": "head-42" },
              "lastMergeTargetCommit": { "commitId": "base-42" } } ] }
        """;

    private static AdoPrProvider Provider(FakeHttpMessageHandler handler, CapturingLoggerFactory logs) =>
        new(
            new HttpClient(handler),
            new FakeOAuthTokenProvider("ado", "ado-token-abc"),
            logs.CreateLogger<AdoPrProvider>()
        );

    /// <summary>
    /// When the provider cannot establish the target project's visibility it must say WHICH of the
    /// mutually-exclusive causes occurred, because the remedy differs: a payload that OMITS
    /// <c>visibility</c> needs a second call to resolve it, whereas a payload carrying a visibility we do
    /// not recognize needs the parser taught a new value. Left undistinguished, both surface identically
    /// as the fail-closed default and the gate reads "targetPublic=true" on a private corporate repo with
    /// nothing to attribute it to — which is exactly how this went unexplained for 138 runs.
    /// <para>
    /// The line names the property names present on <c>repository.project</c>, not their values, and
    /// carries no PR title, description or author: this is a diagnostic on a payload that is otherwise
    /// full of EUII, and it is read from a log nobody vets before shipping.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ListOpenPullRequests_names_the_pr_list_omitting_visibility_when_it_cannot_establish_it()
    {
        using var logs = new CapturingLoggerFactory();
        var handler = new FakeHttpMessageHandler()
            // No project-metadata route: nothing can rescue the missing visibility, so the provider gives up.
            .OnJson(HttpMethod.Get, "/_apis/projects/", """{"message":"nope"}""", HttpStatusCode.NotFound)
            .OnJson(HttpMethod.Get, "/pullrequests", PrWithProjectButNoVisibility);

        var page = await Provider(handler, logs).ListOpenPullRequestsAsync(Request(), CancellationToken.None);

        page.PullRequests[0].IsTargetRepoPublic.Should().BeNull("nothing established the project's visibility");

        var warning = logs
            .Capturing.MessagesAtLevel(LogLevel.Warning)
            .Should()
            .ContainSingle("one line must tie the cause to the run, not scatter the facts across several")
            .Subject;
        warning
            .Should()
            .Contain("visibility", "the line has to name the property that was missing, not just report a failure");
        warning.Should().Contain("id").And.Contain("name").And.Contain("state");
        warning.Should().NotContain("Revert the Contoso revenue report", "a PR title is EUII");
        warning.Should().NotContain("drill-through", "a PR description is EUII");
        warning.Should().NotContain("jane.doe@contoso.com", "an author identity is EUII");
    }

    /// <summary>
    /// The other cause, which must not read like the first: <c>visibility</c> IS present but carries a
    /// value the parser does not map. That needs the parser taught a value, not a second REST call, so
    /// the line names the value it could not map.
    /// </summary>
    [Fact]
    public async Task ListOpenPullRequests_names_an_unrecognized_project_visibility()
    {
        using var logs = new CapturingLoggerFactory();
        var handler = new FakeHttpMessageHandler()
            .OnJson(HttpMethod.Get, "/_apis/projects/", """{"message":"nope"}""", HttpStatusCode.NotFound)
            .OnJson(
                HttpMethod.Get,
                "/pullrequests",
                """
                { "value": [ { "pullRequestId": 42, "status": "active",
                    "repository": { "name": "widgets",
                      "project": { "id": "proj-guid-1", "name": "Platform", "visibility": "systemprivate" } },
                    "lastMergeSourceCommit": { "commitId": "head-42" },
                    "lastMergeTargetCommit": { "commitId": "base-42" } } ] }
                """
            );

        var page = await Provider(handler, logs).ListOpenPullRequestsAsync(Request(), CancellationToken.None);

        page.PullRequests[0].IsTargetRepoPublic.Should().BeNull("an unrecognized visibility must not read as private");

        var warning = logs.Capturing.MessagesAtLevel(LogLevel.Warning).Should().ContainSingle().Subject;
        warning.Should().Contain("systemprivate", "the value the parser could not map is the whole diagnostic here");
    }

    /// <summary>
    /// The fix. ADO's PR-list payload never carries <c>repository.project.visibility</c> — the sample
    /// responses on the REST 7.1 "Get Pull Requests" page show <c>project</c> as <c>{id, name, state}</c>
    /// — so reading it there can only ever yield "unknown", which collapses to the fail-closed default and
    /// shuts the cross-repo sibling gate on every run of a private corporate org. The visibility is a
    /// property of the PROJECT, and the project API returns it, so the provider resolves it there.
    /// <para>
    /// The project is taken from the poll request (<c>{org}/{project}/_apis/git/repositories/{repo}</c> is
    /// the route being polled), not from the payload: ADO defines <c>repository</c> as the repo containing
    /// the PR's TARGET branch, which is the repo we asked about, so the two can never disagree — and one
    /// of them is a payload shape we have just established we cannot rely on.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ListOpenPullRequests_resolves_project_visibility_from_the_project_api_when_the_pr_list_omits_it()
    {
        using var logs = new CapturingLoggerFactory();
        var handler = new FakeHttpMessageHandler()
            .OnJson(
                HttpMethod.Get,
                "/_apis/projects/",
                """{ "id": "proj-guid-1", "name": "Platform", "state": "wellFormed", "visibility": "private" }"""
            )
            .OnJson(HttpMethod.Get, "/pullrequests", PrWithProjectButNoVisibility);

        var page = await Provider(handler, logs).ListOpenPullRequestsAsync(Request(), CancellationToken.None);

        page.PullRequests[0]
            .IsTargetRepoPublic.Should()
            .BeFalse("the project API answered what the PR list could not, and this project is private");
        logs.Capturing.MessagesAtLevel(LogLevel.Warning)
            .Should()
            .BeEmpty("a resolved visibility is a healthy poll, not a condition to warn about on every cycle");

        var request = handler
            .Requests.Should()
            .ContainSingle(r => r.Uri.ToString().Contains("/_apis/projects/", StringComparison.Ordinal))
            .Subject;
        request
            .Uri.ToString()
            .Should()
            .StartWith(
                "https://dev.azure.com/contoso/_apis/projects/Platform",
                "the project API is ORG-scoped — the project is the resource, not a path prefix"
            );
        request.Uri.Query.Should().Contain("api-version=7.1");
        request.Authorization.Should().StartWith("Basic ", "ADO PATs/bearer tokens are sent via basic auth");
    }

    /// <summary>
    /// A public project must still come back public. The fail-closed default exists so an untrusted fork
    /// or public-project PR never gets private siblings co-located beside it, and a fix that resolved every
    /// project to "private" would defeat exactly the protection it was supposed to leave intact.
    /// </summary>
    [Fact]
    public async Task ListOpenPullRequests_reports_a_public_project_from_the_project_api()
    {
        using var logs = new CapturingLoggerFactory();
        var handler = new FakeHttpMessageHandler()
            .OnJson(HttpMethod.Get, "/_apis/projects/", """{ "name": "Platform", "visibility": "public" }""")
            .OnJson(HttpMethod.Get, "/pullrequests", PrWithProjectButNoVisibility);

        var page = await Provider(handler, logs).ListOpenPullRequestsAsync(Request(), CancellationToken.None);

        page.PullRequests[0].IsTargetRepoPublic.Should().BeTrue();
    }

    /// <summary>
    /// A project's visibility changes about as often as the project is renamed, and the daemon polls every
    /// repo every cycle, so the lookup is resolved once per project for the life of the process. Without
    /// the cache this fix would add one REST round trip per poll per repo, forever, to learn a constant.
    /// </summary>
    [Fact]
    public async Task ListOpenPullRequests_resolves_the_project_visibility_once_per_process()
    {
        using var logs = new CapturingLoggerFactory();
        var handler = new FakeHttpMessageHandler()
            .OnJson(HttpMethod.Get, "/_apis/projects/", """{ "name": "Platform", "visibility": "private" }""")
            .OnJson(HttpMethod.Get, "/pullrequests", PrWithProjectButNoVisibility);
        var provider = Provider(handler, logs);

        var first = await provider.ListOpenPullRequestsAsync(Request(), CancellationToken.None);
        var second = await provider.ListOpenPullRequestsAsync(Request(), CancellationToken.None);

        first.PullRequests[0].IsTargetRepoPublic.Should().BeFalse();
        second.PullRequests[0].IsTargetRepoPublic.Should().BeFalse("the cached answer is used, not re-derived");
        handler.CountRequests("/_apis/projects/").Should().Be(1, "the project's visibility is resolved once");
    }

    /// <summary>
    /// The fail-closed default is load-bearing and this fix must not widen it. When the project API cannot
    /// answer — denied by the operation policy, throttled, 404, or simply carrying a visibility we do not
    /// recognize — the provider reports "unknown", which <c>PrPollingService</c> collapses to the
    /// fail-closed value and the sibling gate stays shut. It must not guess "private" because the org
    /// usually is one.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.NotFound, """{"message":"nope"}""")]
    [InlineData(HttpStatusCode.Unauthorized, """{"message":"denied"}""")]
    [InlineData(HttpStatusCode.OK, """{ "name": "Platform", "visibility": "systemprivate" }""")]
    [InlineData(HttpStatusCode.OK, """{ "name": "Platform" }""")]
    public async Task ListOpenPullRequests_stays_fail_closed_when_the_project_api_cannot_answer(
        HttpStatusCode status,
        string body
    )
    {
        using var logs = new CapturingLoggerFactory();
        var handler = new FakeHttpMessageHandler()
            .OnJson(HttpMethod.Get, "/_apis/projects/", body, status)
            .OnJson(HttpMethod.Get, "/pullrequests", PrWithProjectButNoVisibility);

        var page = await Provider(handler, logs).ListOpenPullRequestsAsync(Request(), CancellationToken.None);

        page.PullRequests.Should()
            .ContainSingle()
            .Which.IsTargetRepoPublic.Should()
            .BeNull("an unanswerable visibility is 'unknown', which fails closed — never a guess at 'private'");
    }

    /// <summary>
    /// A failed visibility lookup must never fault the poll. Losing the trust signal costs this run its
    /// cross-repo siblings; letting the exception escape would cost the run its review entirely, and every
    /// other PR on the page with it.
    /// </summary>
    [Fact]
    public async Task ListOpenPullRequests_survives_a_project_lookup_that_throws()
    {
        using var logs = new CapturingLoggerFactory();
        var handler = new FakeHttpMessageHandler()
            .On(
                req => req.RequestUri!.ToString().Contains("/_apis/projects/", StringComparison.Ordinal),
                _ => throw new HttpRequestException("simulated egress denial")
            )
            .OnJson(HttpMethod.Get, "/pullrequests", PrWithProjectButNoVisibility);

        var page = await Provider(handler, logs).ListOpenPullRequestsAsync(Request(), CancellationToken.None);

        page.PullRequests.Should().ContainSingle().Which.IsTargetRepoPublic.Should().BeNull();
    }

    /// <summary>
    /// A payload that DOES carry the visibility is believed as-is — on-prem Azure DevOps Server and any
    /// future API version that serializes it must not pay for a round trip the cloud API forces on us.
    /// </summary>
    [Fact]
    public async Task ListOpenPullRequests_does_not_call_the_project_api_when_the_payload_carries_visibility()
    {
        using var logs = new CapturingLoggerFactory();
        var handler = new FakeHttpMessageHandler().OnJson(
            HttpMethod.Get,
            "/pullrequests",
            """
            { "value": [ { "pullRequestId": 42, "status": "active",
                "repository": { "name": "widgets", "project": { "name": "Platform", "visibility": "private" } },
                "lastMergeSourceCommit": { "commitId": "head-42" },
                "lastMergeTargetCommit": { "commitId": "base-42" } } ] }
            """
        );

        var page = await Provider(handler, logs).ListOpenPullRequestsAsync(Request(), CancellationToken.None);

        page.PullRequests[0].IsTargetRepoPublic.Should().BeFalse();
        handler.CountRequests("/_apis/projects/").Should().Be(0, "the payload already answered the question");
    }

    /// <summary>
    /// The project API answering 200 with a visibility we cannot map is the one give-up path that used to
    /// log NOTHING — the call succeeded, so neither the non-success nor the exception branch fired, and the
    /// method returned "unknown" in silence. That is the same silent-default failure this whole
    /// investigation was about, and it cost a live run its attribution: run 143 could say the PR-list
    /// carried <c>visibility: "unchanged"</c> but not why the fallback failed to rescue it.
    /// <para>
    /// So the value the project API sent has to appear in the log. It is a visibility enum, not EUII, and
    /// it is the entire remedy — you cannot teach a parser a value nobody has written down.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("""{ "name": "Platform", "visibility": "unchanged" }""", "unchanged")]
    [InlineData("""{ "name": "Platform", "visibility": "systemprivate" }""", "systemprivate")]
    public async Task Project_api_answering_with_an_unmappable_visibility_says_so(string body, string expected)
    {
        using var logs = new CapturingLoggerFactory();
        var handler = new FakeHttpMessageHandler()
            .OnJson(HttpMethod.Get, "/_apis/projects/", body)
            .OnJson(HttpMethod.Get, "/pullrequests", PrWithProjectButNoVisibility);

        var page = await Provider(handler, logs).ListOpenPullRequestsAsync(Request(), CancellationToken.None);

        page.PullRequests[0].IsTargetRepoPublic.Should().BeNull("an unmappable visibility must not be guessed at");
        logs.Capturing.MessagesAtLevel(LogLevel.Debug)
            .Should()
            .ContainSingle(m => m.Contains("project API", StringComparison.Ordinal))
            .Which.Should()
            .Contain(
                expected,
                "the unmapped value IS the remedy — without it the log can say the fallback failed but "
                    + "never why, which is exactly how the PR-list sentinel went unattributed for 143 runs"
            );
    }

    /// <summary>
    /// A body that is not a JSON object at all (an HTML error page served with a 200, say) is the other
    /// silent path off the same branch, and gets the same treatment: unknown, fail-closed, and said out loud.
    /// </summary>
    [Fact]
    public async Task Project_api_answering_with_a_non_object_body_says_so()
    {
        using var logs = new CapturingLoggerFactory();
        var handler = new FakeHttpMessageHandler()
            .OnJson(HttpMethod.Get, "/_apis/projects/", "\"not-an-object\"")
            .OnJson(HttpMethod.Get, "/pullrequests", PrWithProjectButNoVisibility);

        var page = await Provider(handler, logs).ListOpenPullRequestsAsync(Request(), CancellationToken.None);

        page.PullRequests[0].IsTargetRepoPublic.Should().BeNull();
        logs.Capturing.MessagesAtLevel(LogLevel.Debug)
            .Should()
            .ContainSingle(m => m.Contains("project API", StringComparison.Ordinal));
    }

    /// <summary>
    /// ADO project visibility is not two-valued: <c>ProjectVisibility</c> also defines
    /// <c>organization</c> (visible to everyone in the org, not the internet), plus the
    /// <c>systemprivate</c> and <c>unchanged</c> sentinels. A parser that maps only public/private fails on
    /// <c>organization</c> exactly the way it failed on <c>unchanged</c> — and for an enterprise tenant it
    /// is a very plausible real value.
    /// <para>
    /// <c>organization</c> is NOT public. The gate exists to stop an untrusted PR getting sibling
    /// repositories co-located beside it where a prompt-injected agent could read and exfiltrate them; a
    /// project visible only to members of the organization is the same trust domain as the repo under
    /// review. The confidentiality boundary that matters is org-EXTERNAL, not repo-external. <c>public</c>
    /// means internet-visible and stays true.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("organization", false)]
    [InlineData("Organization", false)]
    [InlineData("private", false)]
    [InlineData("public", true)]
    public async Task Organization_visibility_is_not_public(string visibility, bool expected)
    {
        using var logs = new CapturingLoggerFactory();
        var handler = new FakeHttpMessageHandler()
            .OnJson(HttpMethod.Get, "/_apis/projects/", $$"""{ "name": "Platform", "visibility": "{{visibility}}" }""")
            .OnJson(HttpMethod.Get, "/pullrequests", PrWithProjectButNoVisibility);

        var page = await Provider(handler, logs).ListOpenPullRequestsAsync(Request(), CancellationToken.None);

        page.PullRequests[0].IsTargetRepoPublic.Should().Be(expected);
    }

    /// <summary>The same mapping has to hold on the payload path, or the two readers of one enum drift and
    /// a value learned in one place stays unknown in the other.</summary>
    [Fact]
    public async Task Organization_visibility_is_not_public_on_the_payload_path_either()
    {
        using var logs = new CapturingLoggerFactory();
        var handler = new FakeHttpMessageHandler().OnJson(
            HttpMethod.Get,
            "/pullrequests",
            """
            { "value": [ { "pullRequestId": 42, "status": "active",
                "repository": { "name": "widgets", "project": { "name": "Platform", "visibility": "organization" } },
                "lastMergeSourceCommit": { "commitId": "head-42" },
                "lastMergeTargetCommit": { "commitId": "base-42" } } ] }
            """
        );

        var page = await Provider(handler, logs).ListOpenPullRequestsAsync(Request(), CancellationToken.None);

        page.PullRequests[0].IsTargetRepoPublic.Should().BeFalse();
        handler.CountRequests("/_apis/projects/").Should().Be(0, "the payload answered it");
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

        // Select the PR-list call rather than asserting a lone request: a poll of a payload without
        // repository.project.visibility legitimately also asks the project API for it.
        var request = handler
            .Requests.Should()
            .ContainSingle(r => r.Uri.ToString().Contains("/pullrequests", StringComparison.Ordinal))
            .Subject;
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

    [Theory]
    [InlineData("true", PrDraftState.Draft)]
    [InlineData("false", PrDraftState.Ready)]
    [InlineData("null", PrDraftState.Unknown)]
    [InlineData("\"true\"", PrDraftState.Unknown)]
    internal async Task ListOpenPullRequests_maps_isDraft_fail_closed(string draftJson, PrDraftState expected)
    {
        var payload = $$"""
            { "value": [{
              "pullRequestId": 42, "status": "active", "isDraft": {{draftJson}},
              "lastMergeSourceCommit": { "commitId": "head-42" },
              "lastMergeTargetCommit": { "commitId": "base-42" }
            }] }
            """;
        var handler = new FakeHttpMessageHandler().OnJson(HttpMethod.Get, "/pullrequests", payload);

        var page = await Provider(handler).ListOpenPullRequestsAsync(Request(), CancellationToken.None);

        page.PullRequests.Single().DraftState.Should().Be(expected);
    }

    [Theory]
    [InlineData("true", PrDraftState.Draft)]
    [InlineData("false", PrDraftState.Ready)]
    [InlineData("null", PrDraftState.Unknown)]
    [InlineData("{}", PrDraftState.Unknown)]
    internal async Task GetPrState_maps_lifecycle_and_isDraft_independently(string draftJson, PrDraftState expected)
    {
        var payload = $$"""{ "pullRequestId": 42, "status": "active", "isDraft": {{draftJson}} }""";
        var handler = new FakeHttpMessageHandler().OnJson(HttpMethod.Get, "/pullrequests/42", payload);

        var status = await Provider(handler).GetPrStateAsync(Repo, "42", CancellationToken.None);

        status.Lifecycle.Should().Be(PrLifecycle.Open);
        status.DraftState.Should().Be(expected);
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

        state.Lifecycle.Should().Be(PrLifecycle.Open);
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

        state.Lifecycle.Should().Be(PrLifecycle.Merged);
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

        state.Lifecycle.Should().Be(PrLifecycle.Abandoned);
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

            // Answer the project-visibility lookup normally: it runs before the push lookups, and letting it
            // absorb the simulated cancellation would leave these tests passing without the push path — the
            // thing they are named for — ever having been reached.
            if (request.RequestUri!.ToString().Contains("/_apis/projects/", StringComparison.Ordinal))
            {
                return Task.FromResult(
                    new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("{ \"name\": \"Platform\", \"visibility\": \"private\" }"),
                    }
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

            // The project-visibility lookup shares this handler but is not a push, and counting it as one
            // would inflate both the total and the observed peak concurrency this test exists to measure.
            if (uri.Contains("/_apis/projects/", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{ \"name\": \"Platform\", \"visibility\": \"private\" }"),
                };
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

    // ---------------------------------------------------------------------------------------------
    // Paging. ADO's PR-list endpoint caps every response and does NOT hand back a continuation token,
    // so "how many came back" is the only evidence that there are more. Measured in production: with
    // $top=200 a poll of O365 Core/WeveNova returned exactly 200 PRs across one page and no token,
    // against a repo with 552 active PRs. Before that, sending no $top at all took ADO's default of
    // 101 while Weve_DA/Nova had 711. Both times the truncated response was the same SHAPE as a
    // complete one, which is why it went unnoticed for the life of the daemon.
    // ---------------------------------------------------------------------------------------------

    /// <summary>Builds a PR-list page containing <paramref name="count"/> distinct active PRs.</summary>
    private static string Page(int count, int firstId)
    {
        var items = Enumerable
            .Range(firstId, count)
            .Select(id =>
                $$"""
                    { "pullRequestId": {{id}}, "status": "active", "creationDate": "2026-08-01T00:00:00Z",
                      "sourceRefName": "refs/heads/f{{id}}",
                      "lastMergeSourceCommit": { "commitId": "head-{{id}}" },
                      "lastMergeTargetCommit": { "commitId": "base-{{id}}" } }
                    """
            );

        return $$"""{ "count": {{count}}, "value": [ {{string.Join(",", items)}} ] }""";
    }

    [Fact]
    public async Task A_full_page_is_followed_with_skip_until_a_SHORT_page_ends_the_list()
    {
        // Page size 2. First page comes back full (2) so it cannot be the last; the second returns 1,
        // which proves the end. A full page is indistinguishable from a truncated one — only a short
        // page terminates the listing.
        var handler = new FakeHttpMessageHandler().OnSequence(
            HttpMethod.Get,
            "/pullrequests",
            (HttpStatusCode.OK, Page(2, 1)),
            (HttpStatusCode.OK, Page(1, 3))
        );
        var provider = new AdoPrProvider(
            new HttpClient(handler),
            new FakeOAuthTokenProvider("ado", "ado-token-abc"),
            LoggerFactory.CreateLogger<AdoPrProvider>(),
            maxPagesPerPoll: 10,
            maxPrsPerPage: 2
        );

        var page = await provider.ListOpenPullRequestsAsync(
            new PrPollRequest { Repo = Repo, Scope = "s" },
            CancellationToken.None
        );

        page.PullRequests.Should().HaveCount(3, "the short second page is the end of the list, not the start of it");
        var urls = handler
            .Requests.Select(r => r.Uri.ToString())
            .Where(u => u.Contains("/pullrequests", StringComparison.Ordinal))
            .ToList();
        urls.Should().HaveCount(2);
        urls[0].Should().Contain("$top=2").And.NotContain("$skip");
        urls[1].Should().Contain("$skip=2", "the second page has to skip what the first already returned");
    }

    [Fact]
    public async Task A_short_first_page_asks_for_nothing_further()
    {
        var handler = new FakeHttpMessageHandler().OnSequence(
            HttpMethod.Get,
            "/pullrequests",
            (HttpStatusCode.OK, Page(1, 1)),
            (HttpStatusCode.OK, Page(1, 99))
        );
        var provider = new AdoPrProvider(
            new HttpClient(handler),
            new FakeOAuthTokenProvider("ado", "ado-token-abc"),
            LoggerFactory.CreateLogger<AdoPrProvider>(),
            maxPagesPerPoll: 10,
            maxPrsPerPage: 2
        );

        var page = await provider.ListOpenPullRequestsAsync(
            new PrPollRequest { Repo = Repo, Scope = "s" },
            CancellationToken.None
        );

        page.PullRequests.Should().HaveCount(1);
        handler
            .CountRequests("/pullrequests")
            .Should()
            .Be(1, "a page shorter than $top already proves there is nothing after it");
    }

    [Fact]
    public async Task Stopping_at_the_page_ceiling_with_a_full_page_says_so_out_loud()
    {
        // THE REGRESSION THIS PINS. The first version of this fix tested the continuation TOKEN both in
        // the loop condition and in the warning — so the case that actually truncates here (full page, no
        // token) exited the loop quietly and then failed to warn, leaving the cap exactly as invisible as
        // it had been at 101.
        var logs = new CapturingLoggerFactory();
        var handler = new FakeHttpMessageHandler().OnSequence(
            HttpMethod.Get,
            "/pullrequests",
            (HttpStatusCode.OK, Page(2, 1)),
            (HttpStatusCode.OK, Page(2, 3)),
            (HttpStatusCode.OK, Page(2, 5))
        );
        var provider = new AdoPrProvider(
            new HttpClient(handler),
            new FakeOAuthTokenProvider("ado", "ado-token-abc"),
            logs.CreateLogger<AdoPrProvider>(),
            maxPagesPerPoll: 2,
            maxPrsPerPage: 2
        );

        var page = await provider.ListOpenPullRequestsAsync(
            new PrPollRequest { Repo = Repo, Scope = "s" },
            CancellationToken.None
        );

        page.PullRequests.Should().HaveCount(4);
        handler.CountRequests("/pullrequests").Should().Be(2, "the ceiling is 2 pages");
        logs.Capturing.MessagesAtLevel(LogLevel.Warning)
            .Should()
            .ContainSingle(
                m =>
                    m.Contains("stopped after", StringComparison.Ordinal)
                    && m.Contains("were NOT seen", StringComparison.Ordinal),
                "a coverage limit nobody is told about is the defect, not the limit"
            );
    }
}
