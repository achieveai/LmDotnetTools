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
/// P4.4 — the real <see cref="GitHubPrProvider"/> reads open PRs from the GitHub REST API. These tests
/// drive it against a scripted HTTP handler (no network): they pin the request shape GitHub requires
/// (bearer auth from the OAuth provider, a <c>User-Agent</c>, the <c>vnd.github+json</c> accept header,
/// the <c>state=open</c> query), the descriptor mapping (number/head/base/updated_at/lifecycle), and the
/// versioned opaque cursor it advances.
/// </summary>
public sealed class GitHubPrProviderTests : LoggingTestBase
{
    public GitHubPrProviderTests(ITestOutputHelper output)
        : base(output) { }

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

    private static PrPollRequest Request(OpaqueCursor? cursor = null) =>
        new()
        {
            Repo = Repo,
            Scope = "acme/widgets:open-prs",
            Cursor = cursor,
        };

    private GitHubPrProvider Provider(
        FakeHttpMessageHandler handler,
        int maxPagesPerPoll = CodeReviewDaemonOptions.DefaultMaxPagesPerPoll
    ) =>
        new(
            new HttpClient(handler),
            new FakeOAuthTokenProvider("github", "gh-token-xyz"),
            LoggerFactory.CreateLogger<GitHubPrProvider>(),
            maxPagesPerPoll
        );

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

    /// <summary>
    /// The prose half of the knowledge-retrieval key. Sibling PRs applying one architectural pattern often
    /// touch entirely different files, so ranked purely on changed paths the relevant lesson scores zero on
    /// one of them; the pattern is named in the title, and the title has to survive the poll to be usable.
    /// </summary>
    [Fact]
    public async Task ListOpenPullRequests_captures_what_the_pr_says_it_does()
    {
        const string describedPr = """
            [
              {
                "number": 7,
                "state": "open",
                "merged_at": null,
                "updated_at": "2026-06-01T10:00:00Z",
                "head": { "sha": "head-7" },
                "base": { "sha": "base-7" },
                "title": "Remove the stale featureflag entry",
                "body": "The ECS task definition still carries the flag."
              }
            ]
            """;
        var handler = new FakeHttpMessageHandler().OnJson(HttpMethod.Get, "/repos/acme/widgets/pulls", describedPr);

        var page = await Provider(handler).ListOpenPullRequestsAsync(Request(), CancellationToken.None);

        page.PullRequests[0].Title.Should().Be("Remove the stale featureflag entry");
        page.PullRequests[0].Description.Should().Be("The ECS task definition still carries the flag.");
    }

    /// <summary>
    /// GitHub sends <c>"body": ""</c> — not a missing property — for a PR opened with no description, and
    /// <c>null</c> for some shapes. Both must arrive as null, because "the author wrote nothing" and "we
    /// captured nothing" must rank identically rather than one of them looking like prose.
    /// </summary>
    [Theory]
    [InlineData("\"\"")]
    [InlineData("\"   \"")]
    [InlineData("null")]
    public async Task ListOpenPullRequests_maps_an_empty_body_to_no_prose_at_all(string body)
    {
        var emptyBodyPr = $$"""
            [
              {
                "number": 7,
                "state": "open",
                "merged_at": null,
                "updated_at": "2026-06-01T10:00:00Z",
                "head": { "sha": "head-7" },
                "base": { "sha": "base-7" },
                "title": "Remove the stale featureflag entry",
                "body": {{body}}
              }
            ]
            """;
        var handler = new FakeHttpMessageHandler().OnJson(HttpMethod.Get, "/repos/acme/widgets/pulls", emptyBodyPr);

        var page = await Provider(handler).ListOpenPullRequestsAsync(Request(), CancellationToken.None);

        page.PullRequests[0].Description.Should().BeNull();
        page.PullRequests[0]
            .Title.Should()
            .Be("Remove the stale featureflag entry", "a blank body must not take the title down with it");
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
            HttpMethod.Get,
            "/repos/acme/widgets/pulls",
            """{"message":"Bad credentials"}""",
            HttpStatusCode.Unauthorized
        );

        var act = () => Provider(handler).ListOpenPullRequestsAsync(Request(), CancellationToken.None);

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task ListOpenPullRequests_follows_link_header_pagination_across_pages()
    {
        // PR #121 M5 — the first page carries a Link rel="next" to page 2; the provider must follow it and
        // accumulate PRs from every page. Page 2 has no Link header, so pagination stops there.
        const string page1 = """
            [ { "number": 7, "state": "open", "merged_at": null, "updated_at": "2026-06-01T10:00:00Z",
                "head": { "sha": "head-7" }, "base": { "sha": "base-7" } } ]
            """;
        const string page2 = """
            [ { "number": 9, "state": "open", "merged_at": null, "updated_at": "2026-06-02T12:30:00Z",
                "head": { "sha": "head-9" }, "base": { "sha": "base-9" } } ]
            """;
        var handler = new FakeHttpMessageHandler()
            .On(
                req => req.RequestUri!.ToString().Contains("page=2", StringComparison.Ordinal),
                _ => JsonResponse(page2)
            )
            .On(
                req => req.RequestUri!.ToString().Contains("per_page=100", StringComparison.Ordinal),
                _ =>
                    JsonResponse(
                        page1,
                        ("Link", "<https://api.github.com/repos/acme/widgets/pulls?state=open&page=2>; rel=\"next\"")
                    )
            );

        var page = await Provider(handler).ListOpenPullRequestsAsync(Request(), CancellationToken.None);

        page.PullRequests.Select(p => p.PrId).Should().BeEquivalentTo(["7", "9"], "both pages are accumulated");
        handler.CountRequests("/pulls").Should().Be(2, "the provider followed exactly one 'next' link");
        page.NextCursor.HighWaterMark.Should().Be("2026-06-02T12:30:00Z", "the newest updated_at across all pages");
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
    public async Task ListOpenPullRequests_handles_an_empty_page()
    {
        var handler = new FakeHttpMessageHandler().OnJson(HttpMethod.Get, "/repos/acme/widgets/pulls", "[]");

        var page = await Provider(handler).ListOpenPullRequestsAsync(Request(), CancellationToken.None);

        page.PullRequests.Should().BeEmpty();
        page.NextCursor.CursorVersion.Should().Be(PrPollingService.CursorVersion);
    }

    [Fact]
    public async Task Engagement_snapshot_unifies_provider_native_discussion_and_excludes_only_exact_receipts()
    {
        const string pr = """
            {
              "number": 7,
              "state": "open",
              "merged_at": null,
              "updated_at": "2026-09-02T13:00:00Z",
              "head": { "sha": "head-7" },
              "base": { "sha": "base-7" }
            }
            """;
        const string reviews = """
            [
              { "id": 101, "state": "COMMENTED", "body": "daemon summary",
                "submitted_at": "2026-09-02T12:00:00Z", "user": { "login": "review-bot" } },
              { "id": 102, "state": "COMMENTED", "body": "same author, human-authored review",
                "submitted_at": "2026-09-02T12:03:00Z", "user": { "login": "review-bot" } }
            ]
            """;
        const string reviewComments = """
            [
              { "id": 201, "body": "line finding", "path": "src/a.cs", "line": 11, "side": "RIGHT",
                "start_line": 10, "start_side": "RIGHT", "created_at": "2026-09-02T12:01:00Z",
                "html_url": "https://github.test/review/201", "user": { "login": "alice" } },
              { "id": 202, "in_reply_to_id": 201, "body": "reply", "path": "src/a.cs", "line": 11,
                "side": "RIGHT", "created_at": "2026-09-02T12:02:00Z",
                "html_url": "https://github.test/review/202", "user": { "login": "bob" } }
            ]
            """;
        const string issueComments = """
            [
              { "id": 301, "body": "PR-level question", "created_at": "2026-09-02T12:04:00Z",
                "html_url": "https://github.test/issue/301", "user": { "login": "carol" } }
            ]
            """;
        var handler = new FakeHttpMessageHandler()
            .OnJson(HttpMethod.Get, "/pulls/7/reviews", reviews)
            .OnJson(HttpMethod.Get, "/pulls/7/comments", reviewComments)
            .OnJson(HttpMethod.Get, "/issues/7/comments", issueComments)
            .OnJson(HttpMethod.Get, "/pulls/7", pr);

        var snapshot = await Provider(handler)
            .GetEngagementSnapshotAsync(
                Repo,
                "7",
                new ProviderActivityWatermark(
                    "github",
                    new DateTimeOffset(2026, 9, 2, 11, 59, 0, TimeSpan.Zero),
                    "seed:0"
                ),
                new HashSet<string>(["review:101"], StringComparer.Ordinal),
                CancellationToken.None
            );

        snapshot.Lifecycle.Should().Be(PrLifecycleState.Open);
        snapshot.HeadSha.Should().Be("head-7");
        snapshot.BaseSha.Should().Be("base-7");
        snapshot.ExternalActivity.Select(activity => activity.CommentId).Should().Equal("201", "202", "102", "301");
        snapshot.ExternalActivity.Should().NotContain(activity => activity.Body == "daemon summary");
        snapshot
            .ExternalActivity.Should()
            .ContainSingle(activity => activity.Body == "same author, human-authored review");
        snapshot
            .ExternalActivity.Single(activity => activity.CommentId == "102")
            .ProviderObjectId.Should()
            .Be("review:102");
        snapshot
            .ExternalActivity.Single(activity => activity.CommentId == "301")
            .ProviderObjectId.Should()
            .Be("issue-comment:301");
        var reply = snapshot.ExternalActivity.Single(activity => activity.CommentId == "202");
        reply.ProviderObjectId.Should().Be("review-comment:202");
        reply.ThreadId.Should().Be("201");
        reply.ParentCommentId.Should().Be("201");
        reply.Path.Should().Be("src/a.cs");
        reply.Side.Should().Be("RIGHT");
        reply.EndLine.Should().Be(11);
        snapshot.LatestObserved.Should().Be(snapshot.ExternalActivity[^1].Watermark);
    }

    [Fact]
    public async Task Engagement_snapshot_maps_a_closed_unmerged_pr_to_abandoned()
    {
        const string pr = """
            { "number": 7, "state": "closed", "merged_at": null, "updated_at": "2026-09-02T12:03:00Z",
              "head": { "sha": "head-7" }, "base": { "sha": "base-7" } }
            """;
        var handler = new FakeHttpMessageHandler()
            .OnJson(HttpMethod.Get, "/pulls/7/reviews", "[]")
            .OnJson(HttpMethod.Get, "/pulls/7/comments", "[]")
            .OnJson(HttpMethod.Get, "/issues/7/comments", "[]")
            .OnJson(HttpMethod.Get, "/pulls/7", pr);

        var snapshot = await Provider(handler)
            .GetEngagementSnapshotAsync(Repo, "7", null, new HashSet<string>(), CancellationToken.None);

        snapshot.Lifecycle.Should().Be(PrLifecycleState.Abandoned);
    }

    [Fact]
    public async Task Engagement_snapshot_retains_the_parent_of_an_in_window_review_reply_as_ancestor_only()
    {
        const string pr = """
            { "number": 7, "state": "open", "merged_at": null, "updated_at": "2026-09-02T12:03:00Z",
              "head": { "sha": "head-7" }, "base": { "sha": "base-7" } }
            """;
        const string reviewComments = """
            [
              { "id": 200, "body": "original question", "created_at": "2026-09-02T12:00:00Z",
                "path": "src/a.cs", "line": 10, "side": "RIGHT", "user": { "login": "alice" } },
              { "id": 201, "in_reply_to_id": 200, "body": "the retry is per attempt",
                "created_at": "2026-09-02T12:02:00Z", "path": "src/a.cs", "line": 10,
                "side": "RIGHT", "user": { "login": "bob" } }
            ]
            """;
        var handler = new FakeHttpMessageHandler()
            .OnJson(HttpMethod.Get, "/pulls/7/reviews", "[]")
            .OnJson(HttpMethod.Get, "/pulls/7/comments", reviewComments)
            .OnJson(HttpMethod.Get, "/issues/7/comments", "[]")
            .OnJson(HttpMethod.Get, "/pulls/7", pr);
        var after = new ProviderActivityWatermark(
            "github",
            new DateTimeOffset(2026, 9, 2, 12, 0, 0, TimeSpan.Zero),
            "review-comment:200"
        );

        var snapshot = await Provider(handler)
            .GetEngagementSnapshotAsync(Repo, "7", after, new HashSet<string>(), CancellationToken.None);

        snapshot.ExternalActivity.Should().ContainSingle().Which.CommentId.Should().Be("201");
        snapshot.AncestorActivity.Should().ContainSingle().Which.CommentId.Should().Be("200");
    }

    [Fact]
    public async Task Engagement_snapshot_applies_the_frozen_pr_upper_bound_and_prior_watermark()
    {
        const string pr = """
            { "number": 7, "state": "open", "merged_at": null, "updated_at": "2026-09-02T12:03:00Z",
              "head": { "sha": "head-7" }, "base": { "sha": "base-7" } }
            """;
        const string issueComments = """
            [
              { "id": 300, "body": "already consumed", "created_at": "2026-09-02T12:00:00Z",
                "user": { "login": "alice" } },
              { "id": 301, "body": "inside frozen window", "created_at": "2026-09-02T12:02:00Z",
                "user": { "login": "alice" } },
              { "id": 302, "body": "arrived after snapshot", "created_at": "2026-09-02T12:04:00Z",
                "user": { "login": "alice" } }
            ]
            """;
        var handler = new FakeHttpMessageHandler()
            .OnJson(HttpMethod.Get, "/pulls/7/reviews", "[]")
            .OnJson(HttpMethod.Get, "/pulls/7/comments", "[]")
            .OnJson(HttpMethod.Get, "/issues/7/comments", issueComments)
            .OnJson(HttpMethod.Get, "/pulls/7", pr);
        var after = new ProviderActivityWatermark(
            "github",
            new DateTimeOffset(2026, 9, 2, 12, 0, 0, TimeSpan.Zero),
            "issue-comment:300"
        );

        var snapshot = await Provider(handler)
            .GetEngagementSnapshotAsync(Repo, "7", after, new HashSet<string>(), CancellationToken.None);

        snapshot.ExternalActivity.Should().ContainSingle().Which.CommentId.Should().Be("301");
        snapshot.LatestObserved.Should().Be(snapshot.ExternalActivity[0].Watermark);
    }

    [Fact]
    public async Task Engagement_snapshot_reads_every_activity_page_within_the_frozen_boundary()
    {
        const string pr = """
            { "number": 7, "state": "open", "merged_at": null, "updated_at": "2026-09-02T12:03:00Z",
              "head": { "sha": "head-7" }, "base": { "sha": "base-7" } }
            """;
        var firstPage =
            "["
            + string.Join(
                ',',
                Enumerable
                    .Range(1, 100)
                    .Select(id =>
                        $$"""{ "id": {{id}}, "body": "page-one", "created_at": "2026-09-02T12:01:00Z", "user": { "login": "alice" } }"""
                    )
            )
            + "]";
        const string secondPage = """
            [
              { "id": 101, "body": "page-two", "created_at": "2026-09-02T12:02:00Z",
                "user": { "login": "bob" } },
              { "id": 102, "body": "after-frozen-boundary", "created_at": "2026-09-02T12:04:00Z",
                "user": { "login": "carol" } }
            ]
            """;
        var handler = new FakeHttpMessageHandler()
            .OnJson(HttpMethod.Get, "/pulls/7/reviews", "[]")
            .OnJson(HttpMethod.Get, "/pulls/7/comments", "[]")
            .On(
                request =>
                    request.Method == HttpMethod.Get
                    && request.RequestUri!.AbsolutePath.EndsWith("/issues/7/comments", StringComparison.Ordinal)
                    && request.RequestUri.Query.Contains("page=2", StringComparison.Ordinal),
                _ => JsonResponse(secondPage)
            )
            .OnJson(HttpMethod.Get, "/issues/7/comments", firstPage)
            .OnJson(HttpMethod.Get, "/pulls/7", pr);

        var snapshot = await Provider(handler)
            .GetEngagementSnapshotAsync(
                Repo,
                "7",
                after: null,
                new HashSet<string>(StringComparer.Ordinal),
                CancellationToken.None
            );

        handler
            .Requests.Count(request =>
                request.Uri.AbsolutePath.EndsWith("/issues/7/comments", StringComparison.Ordinal)
            )
            .Should()
            .Be(2);
        snapshot.ExternalActivity.Should().HaveCount(101);
        snapshot.ExternalActivity.Should().ContainSingle(activity => activity.ProviderObjectId == "issue-comment:101");
        snapshot.ExternalActivity.Should().NotContain(activity => activity.ProviderObjectId == "issue-comment:102");
    }

    [Fact]
    public async Task Inline_anchor_snapshot_reads_every_current_pr_file_for_the_expected_head()
    {
        const string files = """
            [
              {
                "filename": "src/Retry.cs",
                "status": "modified",
                "patch": "@@ -10,1 +10,3 @@\n keep\n+added one\n+added two"
              },
              {
                "filename": "src/Old.cs",
                "status": "removed",
                "patch": "@@ -4,2 +0,0 @@\n-old one\n-old two"
              }
            ]
            """;
        var handler = new FakeHttpMessageHandler()
            .OnJson(HttpMethod.Get, "/repos/acme/widgets/pulls/7/files", files)
            .OnJson(HttpMethod.Get, "/repos/acme/widgets/pulls/7?", """{ "number": 7, "head": { "sha": "head-7" } }""")
            .OnJson(HttpMethod.Get, "/repos/acme/widgets/pulls/7", """{ "number": 7, "head": { "sha": "head-7" } }""");

        var snapshot = await Provider(handler)
            .GetInlineAnchorSnapshotAsync(Repo, "7", "head-7", CancellationToken.None);

        snapshot.HeadSha.Should().Be("head-7");
        snapshot.Files.Should().HaveCount(2);
        snapshot.Files[0].Path.Should().Be("src/Retry.cs");
        snapshot.Files[0].RightRanges.Should().Equal(new LineRange(11, 2));
        snapshot.Files[0].LeftRanges.Should().BeEmpty();
        snapshot.Files[1].Path.Should().Be("src/Old.cs");
        snapshot.Files[1].RightRanges.Should().BeEmpty();
        snapshot.Files[1].LeftRanges.Should().Equal(new LineRange(4, 2));
        handler
            .Requests.Should()
            .ContainSingle(request => request.Uri.AbsolutePath.EndsWith("/pulls/7/files", StringComparison.Ordinal))
            .Which.Uri.Query.Should()
            .Contain("per_page=100");
    }

    [Fact]
    public async Task Inline_anchor_snapshot_follows_every_files_page()
    {
        const string firstPage = """
            [
              {
                "filename": "src/First.cs",
                "patch": "@@ -1,1 +1,2 @@\n keep\n+first"
              }
            ]
            """;
        const string secondPage = """
            [
              {
                "filename": "src/Second.cs",
                "patch": "@@ -8,1 +8,2 @@\n keep\n+second"
              }
            ]
            """;
        var headReads = 0;
        var handler = new FakeHttpMessageHandler()
            .On(
                request =>
                    request.Method == HttpMethod.Get
                    && request.RequestUri!.AbsolutePath.EndsWith("/pulls/7/files", StringComparison.Ordinal)
                    && request.RequestUri.Query.Contains("page=2", StringComparison.Ordinal),
                _ => JsonResponse(secondPage)
            )
            .On(
                request =>
                    request.Method == HttpMethod.Get
                    && request.RequestUri!.AbsolutePath.EndsWith("/pulls/7/files", StringComparison.Ordinal),
                _ =>
                    JsonResponse(
                        firstPage,
                        (
                            "Link",
                            "<https://api.github.com/repos/acme/widgets/pulls/7/files?per_page=100&page=2>; rel=\"next\""
                        )
                    )
            )
            .On(
                request =>
                    request.Method == HttpMethod.Get
                    && request.RequestUri!.AbsolutePath.EndsWith("/pulls/7", StringComparison.Ordinal),
                _ =>
                {
                    headReads++;
                    return JsonResponse("""{ "head": { "sha": "head-7" } }""");
                }
            );

        var snapshot = await Provider(handler)
            .GetInlineAnchorSnapshotAsync(Repo, "7", "head-7", CancellationToken.None);

        snapshot.Files.Select(file => file.Path).Should().Equal("src/First.cs", "src/Second.cs");
        handler.CountRequests("/pulls/7/files").Should().Be(2);
        headReads.Should().Be(2);
    }

    [Fact]
    public async Task Inline_anchor_snapshot_is_empty_when_the_head_changes_during_inventory()
    {
        var headReads = 0;
        var handler = new FakeHttpMessageHandler()
            .OnJson(
                HttpMethod.Get,
                "/pulls/7/files",
                """[{ "filename": "src/Retry.cs", "patch": "@@ -1,1 +1,2 @@\n keep\n+added" }]"""
            )
            .On(
                request =>
                    request.Method == HttpMethod.Get
                    && request.RequestUri!.AbsolutePath.EndsWith("/pulls/7", StringComparison.Ordinal),
                _ =>
                    JsonResponse(
                        headReads++ == 0 ? """{ "head": { "sha": "head-7" } }""" : """{ "head": { "sha": "head-8" } }"""
                    )
            );

        var snapshot = await Provider(handler)
            .GetInlineAnchorSnapshotAsync(Repo, "7", "head-7", CancellationToken.None);

        snapshot.HeadSha.Should().Be("head-8");
        snapshot.Files.Should().BeEmpty();
        headReads.Should().Be(2);
    }

    [Fact]
    public async Task Inline_anchor_snapshot_fails_closed_when_files_exceed_the_page_bound()
    {
        const string page = """[{ "filename": "src/Retry.cs", "patch": "@@ -1,1 +1,2 @@\n keep\n+added" }]""";
        var handler = new FakeHttpMessageHandler()
            .On(
                request =>
                    request.Method == HttpMethod.Get
                    && request.RequestUri!.AbsolutePath.EndsWith("/pulls/7/files", StringComparison.Ordinal),
                _ =>
                    JsonResponse(
                        page,
                        (
                            "Link",
                            "<https://api.github.com/repos/acme/widgets/pulls/7/files?per_page=100&page=2>; rel=\"next\""
                        )
                    )
            )
            .OnJson(HttpMethod.Get, "/pulls/7", """{ "head": { "sha": "head-7" } }""");

        var act = () =>
            Provider(handler, maxPagesPerPoll: 1)
                .GetInlineAnchorSnapshotAsync(Repo, "7", "head-7", CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*page bound*");
        handler.CountRequests("/pulls/7/files").Should().Be(1);
    }

    [Fact]
    public async Task Inline_anchor_snapshot_does_not_authorize_a_file_without_a_patch()
    {
        var handler = new FakeHttpMessageHandler()
            .OnJson(HttpMethod.Get, "/pulls/7/files", """[{ "filename": "src/TooLarge.cs", "status": "modified" }]""")
            .OnJson(HttpMethod.Get, "/pulls/7", """{ "head": { "sha": "head-7" } }""");

        var snapshot = await Provider(handler)
            .GetInlineAnchorSnapshotAsync(Repo, "7", "head-7", CancellationToken.None);

        snapshot.HeadSha.Should().Be("head-7");
        snapshot.Files.Should().BeEmpty();
    }

    [Fact]
    public async Task GetPrState_maps_an_open_pr_to_open()
    {
        const string openPr = """
            { "number": 7, "state": "open", "merged_at": null }
            """;
        var handler = new FakeHttpMessageHandler().OnJson(HttpMethod.Get, "/repos/acme/widgets/pulls/7", openPr);

        var state = await Provider(handler).GetPrStateAsync(Repo, "7", CancellationToken.None);

        state.Should().Be(PrLifecycle.Open);
    }

    [Fact]
    public async Task GetCurrentHeadSha_reads_the_head_sha_the_pr_actually_has()
    {
        // The #331 guard is only as good as this parse. MockPrProvider proves the guard's LOGIC and cannot
        // touch the payload field at all — if `head.sha` ever stopped being read, this parser would return
        // null, the guard would read that as INDETERMINATE, every review would sail through unchecked, and
        // every test that goes through the mock would stay green. That is exactly the defect #331 was.
        var handler = new FakeHttpMessageHandler().OnJson(
            HttpMethod.Get,
            "/repos/acme/widgets/pulls/7",
            """{ "number": 7, "head": { "ref": "feature", "sha": "d34dbeef" } }"""
        );

        var head = await Provider(handler).GetCurrentHeadShaAsync(Repo, "7", CancellationToken.None);

        head.Should().Be("d34dbeef");
        handler
            .Requests.Should()
            .ContainSingle()
            .Which.Uri.ToString()
            .Should()
            .EndWith("/repos/acme/widgets/pulls/7", "the currency check must cost one single-PR read");
    }

    [Theory]
    [InlineData("""{ "number": 7 }""")]
    [InlineData("""{ "number": 7, "head": {} }""")]
    [InlineData("""{ "number": 7, "head": { "sha": "" } }""")]
    [InlineData("""{ "number": 7, "head": { "sha": "   " } }""")]
    [InlineData("""{ "number": 7, "head": { "sha": null } }""")]
    public async Task GetCurrentHeadSha_is_null_when_the_payload_carries_no_head_rather_than_throwing(string payload)
    {
        var handler = new FakeHttpMessageHandler().OnJson(HttpMethod.Get, "/repos/acme/widgets/pulls/7", payload);

        var head = await Provider(handler).GetCurrentHeadShaAsync(Repo, "7", CancellationToken.None);

        // "The host reports no head" is INDETERMINATE — the caller may only refuse a review on a head the
        // host positively reported. Throwing here would turn a thin payload into an abandoned review.
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
            "/repos/acme/widgets/pulls/7",
            """{ "message": "nope" }""",
            status
        );

        var act = () => Provider(handler).GetCurrentHeadShaAsync(Repo, "7", CancellationToken.None);

        // The load-bearing case. Null means "nothing contradicts the recorded head" and lets the review
        // through; a provider that flattened an outage into null would make the guard vacuous exactly when
        // the host cannot be trusted. The executor is what decides an unreachable host is indeterminate —
        // that decision must not be pre-made here, where it cannot be told apart from a real answer.
        _ = await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task GetPrState_maps_a_closed_and_merged_pr_to_merged()
    {
        const string mergedPr = """
            { "number": 7, "state": "closed", "merged_at": "2026-07-01T00:00:00Z" }
            """;
        var handler = new FakeHttpMessageHandler().OnJson(HttpMethod.Get, "/repos/acme/widgets/pulls/7", mergedPr);

        var state = await Provider(handler).GetPrStateAsync(Repo, "7", CancellationToken.None);

        state.Should().Be(PrLifecycle.Merged);
    }

    [Fact]
    public async Task GetPrState_maps_a_closed_and_unmerged_pr_to_abandoned()
    {
        const string abandonedPr = """
            { "number": 7, "state": "closed", "merged_at": null }
            """;
        var handler = new FakeHttpMessageHandler().OnJson(HttpMethod.Get, "/repos/acme/widgets/pulls/7", abandonedPr);

        var state = await Provider(handler).GetPrStateAsync(Repo, "7", CancellationToken.None);

        state.Should().Be(PrLifecycle.Abandoned);
    }

    // ---- Issue #537: MaxPagesPerPoll is the bound, and it must be able to exceed the old hardcoded 10 ----

    private GitHubPrProvider Provider(
        FakeHttpMessageHandler handler,
        int maxPagesPerPoll,
        int maxPrsPerPage = 100,
        ILogger<GitHubPrProvider>? logger = null
    ) =>
        new(
            new HttpClient(handler),
            new FakeOAuthTokenProvider("github", "gh-token-xyz"),
            logger ?? LoggerFactory.CreateLogger<GitHubPrProvider>(),
            maxPagesPerPoll,
            maxPrsPerPage
        );

    /// <summary>
    /// Serves <paramref name="totalPages"/> pages of one PR each, chained by <c>Link rel="next"</c>. Pages
    /// are handed out in call order (the provider walks them strictly sequentially), so the PR number on a
    /// response is also the 1-based index of the page it came from: PR <c>13</c> can only have been read on
    /// page 13.
    /// </summary>
    private static FakeHttpMessageHandler PagedRepo(int totalPages)
    {
        var served = 0;
        return new FakeHttpMessageHandler().On(
            req => req.RequestUri!.AbsolutePath.EndsWith("/pulls", StringComparison.Ordinal),
            _ =>
            {
                var page = ++served;
                var json = $$"""
                    [ { "number": {{page}}, "state": "open", "merged_at": null,
                        "updated_at": "2026-06-01T10:00:00Z",
                        "head": { "sha": "head-{{page}}" }, "base": { "sha": "base-{{page}}" } } ]
                    """;
                return page < totalPages
                    ? JsonResponse(
                        json,
                        (
                            "Link",
                            $"<https://api.github.com/repos/acme/widgets/pulls?state=open&page={page + 1}>; rel=\"next\""
                        )
                    )
                    : JsonResponse(json);
            }
        );
    }

    /// <summary>
    /// AC#3 — the repo has more pages than the old hardcoded ceiling of 10, and a configured
    /// <c>MaxPagesPerPoll</c> above it must actually enumerate past page 10. PR 11 through 14 exist only on
    /// pages 11 through 14, so their presence is the proof; restoring the old <c>pages &lt; 10</c> bound
    /// stops the walk at PR 10 and fails here.
    /// </summary>
    [Fact]
    public async Task ListOpenPullRequests_enumerates_past_page_ten_when_configured_to()
    {
        var handler = PagedRepo(totalPages: 14);

        var page = await Provider(handler, maxPagesPerPoll: 14)
            .ListOpenPullRequestsAsync(Request(), CancellationToken.None);

        handler.CountRequests("/pulls").Should().Be(14, "the configured bound allows all 14 pages");
        page.PullRequests.Select(p => p.PrId)
            .Should()
            .Contain(["11", "12", "13", "14"], "these PRs exist only on pages past the old hardcoded ceiling of 10");
        page.PullRequests.Should().HaveCount(14);
    }

    /// <summary>
    /// The bound is the CONFIGURED value in both directions: below the old constant it must also bind, so a
    /// test cannot pass merely because 10 happens to be the number in play.
    /// </summary>
    [Theory]
    [InlineData(3)]
    [InlineData(12)]
    public async Task ListOpenPullRequests_fetches_exactly_the_configured_number_of_pages(int maxPages)
    {
        var handler = PagedRepo(totalPages: 30);

        var page = await Provider(handler, maxPagesPerPoll: maxPages)
            .ListOpenPullRequestsAsync(Request(), CancellationToken.None);

        handler.CountRequests("/pulls").Should().Be(maxPages);
        page.PullRequests.Should().HaveCount(maxPages);
    }

    /// <summary>A page bound that cannot be a page count is treated as unset, never as zero pages (which
    /// would make the repo read as empty) and never as unbounded.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task A_nonsensical_page_bound_polls_the_documented_default_number_of_pages(int configured)
    {
        var handler = PagedRepo(totalPages: 30);

        _ = await Provider(handler, maxPagesPerPoll: configured)
            .ListOpenPullRequestsAsync(Request(), CancellationToken.None);

        handler
            .CountRequests("/pulls")
            .Should()
            .Be(
                CodeReviewDaemonOptions.DefaultMaxPagesPerPoll,
                "a nonsensical bound degrades to the default, not to 0"
            );
    }

    /// <summary>
    /// <c>per_page</c> carries the configured page size, clamped to GitHub's documented maximum of 100 —
    /// asking for more is silently ignored by GitHub, so a configured 500 would read as honoured.
    /// </summary>
    [Theory]
    [InlineData(25, "per_page=25")]
    [InlineData(500, "per_page=100")]
    [InlineData(0, "per_page=100")]
    public async Task The_page_size_is_sent_and_clamped_to_githubs_maximum(int configured, string expected)
    {
        var handler = new FakeHttpMessageHandler().OnJson(HttpMethod.Get, "/repos/acme/widgets/pulls", "[]");

        _ = await Provider(handler, maxPagesPerPoll: 10, maxPrsPerPage: configured)
            .ListOpenPullRequestsAsync(Request(), CancellationToken.None);

        handler.Requests.Should().ContainSingle().Which.Uri.Query.Should().Contain(expected);
    }

    /// <summary>
    /// A truncated poll must SAY it was truncated. This is the operator's only signal that the PR list they
    /// are looking at is partial — a truncated page and a complete one are the same shape on the wire, which
    /// is how a 101-of-711 cap survived unnoticed in production — so the warning is load-bearing, not
    /// decoration. It is asserted on the RENDERED text including the numbers, because a warning that fires
    /// with the wrong counts tells the operator to raise a bound that was not the one that bound.
    /// </summary>
    [Fact]
    public async Task A_truncated_poll_warns_with_the_counts_that_bound_it()
    {
        var logger = new CapturingLogger<GitHubPrProvider>();
        var handler = PagedRepo(totalPages: 30);

        var page = await Provider(handler, maxPagesPerPoll: 10, logger: logger)
            .ListOpenPullRequestsAsync(Request(), CancellationToken.None);

        page.PullRequests.Should().HaveCount(10, "10 of the repo's 30 pages were read");
        logger
            .CountAtLevel(
                LogLevel.Warning,
                "stopped after 10 page(s) of 100 with more results still available; 10 PR(s)"
            )
            .Should()
            .Be(1, "the operator is told the poll was incomplete, and with the real counts");
    }

    /// <summary>
    /// The other half, without which the assertion above is satisfied by a guard that always fires: a poll
    /// that reached the end of the repo must warn about NOTHING. A warning on every poll is a warning on no
    /// poll.
    /// </summary>
    [Fact]
    public async Task A_complete_poll_does_not_warn()
    {
        var logger = new CapturingLogger<GitHubPrProvider>();
        var handler = PagedRepo(totalPages: 3);

        _ = await Provider(handler, maxPagesPerPoll: 10, logger: logger)
            .ListOpenPullRequestsAsync(Request(), CancellationToken.None);

        logger
            .CountAtLevel(LogLevel.Warning, "stopped after")
            .Should()
            .Be(0, "the repo's last page carried no rel=\"next\", so nothing was left unseen");
    }
}
