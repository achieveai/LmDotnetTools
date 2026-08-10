using CodeReviewDaemon.Sample.Persistence.Models;

namespace CodeReviewDaemon.Sample.Tests.Infrastructure;

/// <summary>
/// The Azure DevOps payloads PR 5505458 actually returned, and a handler that answers every route
/// <c>AdoCiStatusReader</c> can call.
/// </summary>
/// <remarks>
/// Shared rather than re-invented because two very different tests need the SAME bytes to be worth anything.
/// <c>AdoCiStatusReaderTests</c> asks whether the reader parses them correctly; the brief-delivery tests ask
/// whether what it parsed reaches the reviewer. Had the second set invented its own tidier JSON, it could
/// have gone green against a shape ADO never sends — proving delivery of something that does not exist,
/// which is a more convincing kind of nothing than no test at all.
/// <para>
/// 5505458 is the run-22 incident: 45,051 tests, exactly one failure, the failing project named only inside
/// a 68-record build timeline, and a reviewer that — having no pipeline signal — repeated the PR author's own
/// claim that validation had succeeded.
/// </para>
/// </remarks>
internal static class AdoCiStatusPayloads
{
    /// <summary>The ADO-shaped repository these payloads describe. Three segments, because
    /// <c>AdoCiStatusReader.ReadAsync</c> returns <c>Unavailable</c> without issuing a single request when
    /// <see cref="RepoIdentity.Project"/> is empty — a GitHub-shaped repo silently renders no block at all.</summary>
    public static RepoIdentity Repo { get; } = new()
    {
        Provider = "azure-devops",
        OrgOrOwner = "contoso",
        Project = "Platform",
        RepoName = "LmDotnetTools",
        RepoStableId = "repo-stable-ado-1",
    };

    /// <summary>The submodule remote the store's <c>.gitmodules</c> must name for
    /// <c>ResolveStoreSubmodulePathAsync</c> to match this repo — it matches on the REMOTE URL, not the
    /// name, so an ADO identity paired with a github.com URL resolves to no submodule and the review never
    /// assembles a brief to carry the block.</summary>
    public static string SubmoduleRemoteUrl =>
        $"https://dev.azure.com/{Repo.OrgOrOwner}/{Repo.Project}/_git/{Repo.RepoName}";

    public const string ProjectId = "5f4c1e00-0000-4000-8000-000000000001";
    public const string BuildId = "39168345";

    /// <summary>The evaluation ADO returns for a PR whose Build policy has produced a failed build — the
    /// 5505458 shape, where a second Build policy is still merely queued.</summary>
    public const string RejectedEvaluations = """
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

    public const string FailedBuild = """
        { "id": 39168345, "buildNumber": "20260807.3", "status": "completed", "result": "failed" }
        """;

    public const string OneFailureOf45051 = """
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
    public const string TimelineNamingTagService = """
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

    /// <summary>A handler answering every route the reader can call, so a caller overrides only what it cares
    /// about. Routes match in registration order, so a caller's own <c>On*</c> call must come first.</summary>
    public static FakeHttpMessageHandler AllRoutes(
        string evaluations = RejectedEvaluations,
        string build = FailedBuild,
        string testSummary = OneFailureOf45051,
        string timeline = TimelineNamingTagService) =>
        new FakeHttpMessageHandler()
            .OnJson(
                HttpMethod.Get,
                $"_apis/projects/{Repo.Project}",
                $$"""{ "id": "{{ProjectId}}", "name": "{{Repo.Project}}" }""")
            .OnJson(HttpMethod.Get, "_apis/policy/evaluations", evaluations)
            .OnJson(HttpMethod.Get, $"_apis/build/builds/{BuildId}/timeline", timeline)
            .OnJson(HttpMethod.Get, $"_apis/build/builds/{BuildId}", build)
            .OnJson(HttpMethod.Get, "_apis/test/ResultSummaryByBuild", testSummary);
}
