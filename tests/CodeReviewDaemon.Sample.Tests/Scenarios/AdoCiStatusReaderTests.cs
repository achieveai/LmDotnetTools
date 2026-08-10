using System.Net;
using AchieveAi.LmDotnetTools.LmTestUtils.Logging;
using CodeReviewDaemon.Sample.Orchestration;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using CodeReviewDaemon.Sample.Workspace;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace CodeReviewDaemon.Sample.Tests.Scenarios;

/// <summary>
/// The <see cref="AdoCiStatusReader"/> reads a PR's CI verdict out of Azure DevOps so the reviewer's brief
/// can state it. Before it existed the reviewer had no pipeline signal at all and fell back to repeating
/// the author's own claim — run 22 wrote "The PR commit states that representative restore and build
/// validation succeeded" — while PR 5505458's pipeline was sitting in ADO with 45,051 tests, 1 failure, and
/// the failing project named in the build timeline.
/// <para>
/// Driven against a scripted handler (no network), these tests pin all five policy shapes the daemon can
/// meet, the bounding that keeps a 68-record timeline out of the brief, and — through the real
/// <see cref="OperationPolicyHandler"/> — that every call the reader makes is one the daemon's own
/// operation policy permits.
/// </para>
/// </summary>
public sealed class AdoCiStatusReaderTests : LoggingTestBase
{
    public AdoCiStatusReaderTests(ITestOutputHelper output)
        : base(output)
    {
    }

    private static readonly RepoIdentity Repo = new()
    {
        Provider = "azure-devops",
        OrgOrOwner = "contoso",
        Project = "Platform",
        RepoName = "core",
    };

    private const string ProjectId = "5f4c1e00-0000-4000-8000-000000000001";
    private const string PrId = "5505458";
    private const string BuildId = "39168345";

    /// <summary>The evaluation ADO returns for a PR whose Build policy has produced a failed build — the
    /// 5505458 shape, where a second Build policy is still merely queued.</summary>
    private const string RejectedEvaluations = """
        {
          "count": 2,
          "value": [
            {
              "configuration": { "type": { "displayName": "Build" }, "isEnabled": true },
              "status": "queued",
              "context": {}
            },
            {
              "configuration": { "type": { "displayName": "Build" }, "isEnabled": true },
              "status": "rejected",
              "context": { "buildId": 39168345 }
            }
          ]
        }
        """;

    private const string FailedBuild = """
        { "id": 39168345, "buildNumber": "20260807.3", "status": "completed", "result": "failed" }
        """;

    private const string OneFailureOf45051 = """
        {
          "aggregatedResultsAnalysis": {
            "totalTests": 45051,
            "resultsByOutcome": {
              "Passed": { "outcome": "passed", "count": 45050 },
              "Failed": { "outcome": "failed", "count": 1 }
            }
          }
        }
        """;

    /// <summary>The timeline shape that actually named the failure on 5505458: the message lives on a
    /// record's <c>issues</c>, and most of the 68 records carry none.</summary>
    private const string TimelineNamingTagService = """
        {
          "records": [
            { "name": "Checkout", "type": "Task", "result": "succeeded", "issues": [] },
            { "name": "Test", "type": "Task", "result": "failed", "issues": [
              { "type": "error", "message": "clr\\src\\Plane0\\MetricLibrary\\TagService\\TagService.UnitTests_Retail_Amd64__TEST Attempt: [2], 1 of 1 tests failed." }
            ] },
            { "name": "Report", "type": "Job", "result": "failed", "issues": [
              { "type": "warning", "message": "a warning nobody needs in a review brief" }
            ] }
          ]
        }
        """;

    private AdoCiStatusReader CreateReader(FakeHttpMessageHandler handler) =>
        new(
            new HttpClient(handler),
            new FakeOAuthTokenProvider("ado", "ado-token-abc"),
            LoggerFactory.CreateLogger<AdoCiStatusReader>());

    /// <summary>A handler answering every route the reader can call, so a test overrides only what it cares
    /// about. Routes match in registration order, so a test's own <c>On*</c> call must come first.</summary>
    private static FakeHttpMessageHandler AllRoutes(
        string evaluations = RejectedEvaluations,
        string build = FailedBuild,
        string testSummary = OneFailureOf45051,
        string timeline = TimelineNamingTagService) =>
        new FakeHttpMessageHandler()
            .OnJson(HttpMethod.Get, "_apis/projects/Platform", $$"""{ "id": "{{ProjectId}}", "name": "Platform" }""")
            .OnJson(HttpMethod.Get, "_apis/policy/evaluations", evaluations)
            .OnJson(HttpMethod.Get, $"_apis/build/builds/{BuildId}/timeline", timeline)
            .OnJson(HttpMethod.Get, $"_apis/build/builds/{BuildId}", build)
            .OnJson(HttpMethod.Get, "_apis/test/ResultSummaryByBuild", testSummary);

    [Fact]
    public async Task A_rejected_build_reports_failed_with_counts_and_names_the_failing_test()
    {
        var handler = AllRoutes();

        var status = await CreateReader(handler).ReadAsync(Repo, PrId, ProjectId, CancellationToken.None);

        status.State.Should().Be(AdoCiState.Failed);
        status.BuildId.Should().Be(BuildId);
        status.BuildStatus.Should().Be("completed");
        status.BuildResult.Should().Be("failed");
        status.TotalTests.Should().Be(45051);
        status.PassedTests.Should().Be(45050);
        status.FailedTests.Should().Be(1);
        status.FailureMessages.Should().ContainSingle()
            .Which.Should().Contain("TagService.UnitTests", "the failing project is the whole point of the read");
        status.OmittedFailureMessages.Should().Be(0);
        status.FailureMessages.Should().NotContain(m => m.Contains("a warning nobody needs"),
            "only error issues are failures; warnings are noise in a brief");
    }

    /// <summary>
    /// The 5505458 shape has TWO Build evaluations, <c>queued</c> and <c>rejected</c>. The rejected one is
    /// the one a reviewer must be told about — and it is the only one carrying a buildId — so a reader that
    /// took the first entry would have reported "CI has not run" on a PR whose pipeline had already failed.
    /// </summary>
    [Fact]
    public async Task The_failing_evaluation_wins_over_a_queued_sibling()
    {
        var handler = AllRoutes();

        var status = await CreateReader(handler).ReadAsync(Repo, PrId, ProjectId, CancellationToken.None);

        status.State.Should().Be(AdoCiState.Failed);
        status.BuildId.Should().Be(BuildId);
    }

    [Fact]
    public async Task An_approved_build_reports_succeeded_and_does_not_fetch_the_timeline()
    {
        const string approved = """
            {
              "value": [
                { "configuration": { "type": { "displayName": "Build" } },
                  "status": "approved", "context": { "buildId": 39168345 } }
              ]
            }
            """;
        const string succeededBuild = """
            { "id": 39168345, "status": "completed", "result": "succeeded" }
            """;
        const string allPassed = """
            { "aggregatedResultsAnalysis": { "totalTests": 45051,
                "resultsByOutcome": { "Passed": { "count": 45051 } } } }
            """;
        var handler = AllRoutes(evaluations: approved, build: succeededBuild, testSummary: allPassed);

        var status = await CreateReader(handler).ReadAsync(Repo, PrId, ProjectId, CancellationToken.None);

        status.State.Should().Be(AdoCiState.Succeeded);
        status.TotalTests.Should().Be(45051);
        status.PassedTests.Should().Be(45051);
        status.FailedTests.Should().Be(0);
        status.FailureMessages.Should().BeEmpty();
        handler.CountRequests("/timeline").Should().Be(
            0, "a green build's 68 timeline records are a round trip and a payload with nothing in them");
    }

    [Fact]
    public async Task A_running_build_reports_in_progress_with_no_result()
    {
        const string running = """
            {
              "value": [
                { "configuration": { "type": { "displayName": "Build" } },
                  "status": "running", "context": { "buildId": 39168345 } }
              ]
            }
            """;
        const string inProgressBuild = """
            { "id": 39168345, "status": "inProgress" }
            """;
        const string noResultsYet = """
            { "aggregatedResultsAnalysis": { "totalTests": 0, "resultsByOutcome": {} } }
            """;
        var handler = AllRoutes(evaluations: running, build: inProgressBuild, testSummary: noResultsYet);

        var status = await CreateReader(handler).ReadAsync(Repo, PrId, ProjectId, CancellationToken.None);

        status.State.Should().Be(AdoCiState.Running);
        status.BuildStatus.Should().Be("inProgress");
        status.BuildResult.Should().BeNull("a build that has not finished has no result");
        status.TotalTests.Should().Be(0);
        handler.CountRequests("/timeline").Should().Be(0);
    }

    /// <summary>
    /// A Build policy that has not produced a build carries no <c>buildId</c>. That is distinguishable
    /// straight off the evaluation, so the reader must not spend a round trip discovering it — and must not
    /// let the brief read as though a build had run and been fine.
    /// </summary>
    [Fact]
    public async Task A_queued_policy_with_no_build_reports_that_ci_never_ran()
    {
        const string queued = """
            {
              "value": [
                { "configuration": { "type": { "displayName": "Build" } }, "status": "queued", "context": {} }
              ]
            }
            """;
        var handler = AllRoutes(evaluations: queued);

        var status = await CreateReader(handler).ReadAsync(Repo, PrId, ProjectId, CancellationToken.None);

        status.State.Should().Be(AdoCiState.NotStarted);
        status.BuildId.Should().BeNull();
        status.TotalTests.Should().BeNull("nothing ran, so there is no count — not a count of zero");
        handler.CountRequests("_apis/build/builds").Should().Be(0);
        handler.CountRequests("ResultSummaryByBuild").Should().Be(0);
    }

    /// <summary>
    /// UNVERIFIED SHAPE. No PR in the sample had a repository without a Build policy, so this pins what the
    /// reader does with an evaluation list carrying no <c>Build</c> entry rather than what ADO was observed
    /// to send. The distinction the brief needs is between "CI passed" and "there is no CI", and those must
    /// never collapse.
    /// </summary>
    [Fact]
    public async Task A_pr_with_no_build_policy_is_distinguished_from_a_passing_build()
    {
        const string noBuildPolicy = """
            {
              "value": [
                { "configuration": { "type": { "displayName": "Minimum number of reviewers" } },
                  "status": "approved", "context": {} },
                { "configuration": { "type": { "displayName": "Work item linking" } },
                  "status": "approved", "context": {} }
              ]
            }
            """;
        var handler = AllRoutes(evaluations: noBuildPolicy);

        var status = await CreateReader(handler).ReadAsync(Repo, PrId, ProjectId, CancellationToken.None);

        status.State.Should().Be(AdoCiState.NoBuildPolicy);
        status.BuildId.Should().BeNull();
        handler.CountRequests("_apis/build/builds").Should().Be(0);
    }

    /// <summary>
    /// A timeline holds 68 records on a real build and an error message can be an entire compiler dump. The
    /// brief cannot absorb either, so the list is capped and each message truncated — and the drop is
    /// COUNTED, because a silently-cut list reads to the reviewer as the complete set of failures.
    /// </summary>
    [Fact]
    public async Task The_failure_list_is_capped_and_reports_how_many_were_dropped()
    {
        var issues = string.Join(
            ",\n",
            Enumerable.Range(1, AdoCiStatusReader.MaxFailureMessages + 4)
                .Select(i => $$"""
                    { "name": "Task{{i}}", "type": "Task", "result": "failed", "issues": [
                      { "type": "error", "message": "failure {{i}} {{new string('x', 500)}}" } ] }
                    """));
        var handler = AllRoutes(timeline: $$"""{ "records": [ {{issues}} ] }""");

        var status = await CreateReader(handler).ReadAsync(Repo, PrId, ProjectId, CancellationToken.None);

        status.FailureMessages.Should().HaveCount(AdoCiStatusReader.MaxFailureMessages);
        status.OmittedFailureMessages.Should().Be(4);
        status.FailureMessages.Should().OnlyContain(
            m => m.Length <= AdoCiStatusReader.MaxFailureMessageChars,
            "one runaway build error must not crowd the rest of the brief out");
    }

    /// <summary>ADO repeats the same error on a task record and again on its parent job. The brief should
    /// carry each distinct failure once, or the cap is spent on duplicates.</summary>
    [Fact]
    public async Task Repeated_issue_text_is_reported_once()
    {
        const string duplicated = """
            {
              "records": [
                { "name": "Test", "type": "Task", "result": "failed",
                  "issues": [ { "type": "error", "message": "TagService.UnitTests: 1 of 1 tests failed." } ] },
                { "name": "Test", "type": "Job", "result": "failed",
                  "issues": [ { "type": "error", "message": "TagService.UnitTests: 1 of 1 tests failed." } ] }
              ]
            }
            """;
        var handler = AllRoutes(timeline: duplicated);

        var status = await CreateReader(handler).ReadAsync(Repo, PrId, ProjectId, CancellationToken.None);

        status.FailureMessages.Should().ContainSingle();
        status.OmittedFailureMessages.Should().Be(0, "a duplicate that was folded away was not dropped");
    }

    /// <summary>
    /// A read that fails must report that it could not establish CI — never a state the brief would render as
    /// a healthy pipeline. Getting this backwards is worse than having no reader: the reviewer would cite a
    /// green build that nobody ever read.
    /// </summary>
    [Fact]
    public async Task A_failed_read_reports_unavailable_rather_than_a_green_build()
    {
        var handler = new FakeHttpMessageHandler()
            .OnJson(HttpMethod.Get, "_apis/policy/evaluations", "{}", HttpStatusCode.Forbidden);

        var status = await CreateReader(handler).ReadAsync(Repo, PrId, ProjectId, CancellationToken.None);

        status.State.Should().Be(AdoCiState.Unavailable);
        status.BuildId.Should().BeNull();
        status.TotalTests.Should().BeNull();
    }

    [Fact]
    public async Task A_denied_egress_reports_unavailable_rather_than_faulting_the_review()
    {
        // The policy handler throws OperationDeniedException. A CI read is an enrichment: losing it must cost
        // the brief a line, never cost the PR its review.
        var denyEverything = new OperationPolicyHandler(
            [],
            "ado",
            LoggerFactory.CreateLogger<OperationPolicyHandler>())
        {
            InnerHandler = AllRoutes(),
        };
        var reader = new AdoCiStatusReader(
            new HttpClient(denyEverything),
            new FakeOAuthTokenProvider("ado", "ado-token-abc"),
            LoggerFactory.CreateLogger<AdoCiStatusReader>());

        var status = await reader.ReadAsync(Repo, PrId, ProjectId, CancellationToken.None);

        status.State.Should().Be(AdoCiState.Unavailable);
    }

    /// <summary>
    /// The reader's calls flow through the daemon's real <see cref="OperationPolicyHandler"/>, built from the
    /// same <see cref="DaemonOperationPolicy.BuildForRun"/> the daemon uses. This is the test that proves the
    /// route exception and the reader agree: every URL the reader constructs is one the policy permits, and
    /// each is classified <see cref="SandboxOperation.ReadProviderMetadata"/> so it can never reach the write
    /// arm. A mismatch between the two shows up here as <see cref="AdoCiState.Unavailable"/>.
    /// </summary>
    [Fact]
    public async Task Every_call_passes_the_daemons_own_operation_policy()
    {
        var fake = AllRoutes();
        var policyHandler = new OperationPolicyHandler(
            [DaemonOperationPolicy.BuildForRun(Repo, reviewBotRepoUrl: null)],
            "ado",
            LoggerFactory.CreateLogger<OperationPolicyHandler>())
        {
            InnerHandler = fake,
        };
        var reader = new AdoCiStatusReader(
            new HttpClient(policyHandler),
            new FakeOAuthTokenProvider("ado", "ado-token-abc"),
            LoggerFactory.CreateLogger<AdoCiStatusReader>());

        var status = await reader.ReadAsync(Repo, PrId, projectId: null, CancellationToken.None);

        status.State.Should().Be(AdoCiState.Failed);
        fake.Requests.Should().HaveCount(
            5, "project lookup, evaluations, build, test summary, timeline — all through the policy");
        fake.Requests.Should().OnlyContain(
            r => r.Method == HttpMethod.Get, "the CI read is read-only in every branch");
        fake.Requests.Should().OnlyContain(
            r => r.Authorization != null && r.Authorization.StartsWith("Basic ", StringComparison.Ordinal),
            "ADO REST takes the bearer in the password field of HTTP basic");
    }

    /// <summary>
    /// The policy-evaluation artifactId is keyed by the project GUID, and <c>-preview.1</c> is not optional:
    /// plain <c>api-version=7.1</c> returns 400 on this route.
    /// </summary>
    [Fact]
    public async Task The_evaluation_request_carries_the_artifact_id_and_the_preview_api_version()
    {
        var handler = AllRoutes();

        _ = await CreateReader(handler).ReadAsync(Repo, PrId, ProjectId, CancellationToken.None);

        var evaluation = handler.Requests.Single(r => r.Uri.AbsolutePath.EndsWith("/policy/evaluations", StringComparison.Ordinal));
        evaluation.Uri.AbsolutePath.Should().Be("/contoso/Platform/_apis/policy/evaluations");
        Uri.UnescapeDataString(evaluation.Uri.Query).Should()
            .Contain($"artifactId=vstfs:///CodeReview/CodeReviewId/{ProjectId}/{PrId}")
            .And.Contain("api-version=7.1-preview.1");
    }

    /// <summary>
    /// Nothing on a <see cref="RepoIdentity"/> or a persisted run carries the project GUID, so a caller that
    /// has only the project NAME must still be able to read CI. The GUID is resolved from the org-scoped
    /// project API — a route the policy already permits for the visibility lookup, so this adds no new reach.
    /// </summary>
    [Fact]
    public async Task The_project_guid_is_resolved_when_the_caller_has_none()
    {
        var handler = AllRoutes();

        var status = await CreateReader(handler).ReadAsync(Repo, PrId, projectId: null, CancellationToken.None);

        status.State.Should().Be(AdoCiState.Failed);
        var evaluation = handler.Requests.Single(r => r.Uri.AbsolutePath.EndsWith("/policy/evaluations", StringComparison.Ordinal));
        Uri.UnescapeDataString(evaluation.Uri.Query).Should().Contain($"/{ProjectId}/{PrId}");
    }

    [Fact]
    public async Task A_repo_with_no_project_reports_unavailable_without_calling_anything()
    {
        // GitHub-shaped identity, or an ADO one the poller could not fully resolve. There is no project route
        // to ask, and the policy would deny the call anyway.
        var handler = AllRoutes();
        var noProject = new RepoIdentity { Provider = "azure-devops", OrgOrOwner = "contoso", RepoName = "core" };

        var status = await CreateReader(handler).ReadAsync(noProject, PrId, projectId: null, CancellationToken.None);

        status.State.Should().Be(AdoCiState.Unavailable);
        handler.Requests.Should().BeEmpty();
    }
}
