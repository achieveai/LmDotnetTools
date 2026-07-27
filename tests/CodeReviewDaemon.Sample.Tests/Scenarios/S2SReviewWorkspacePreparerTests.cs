using CodeReviewDaemon.Sample.Agents;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using CodeReviewDaemon.Sample.Workspace.Git;
using CodeReviewDaemon.Sample.Workspace.Sandbox;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeReviewDaemon.Sample.Tests.Scenarios;

/// <summary>
/// Unit tests for <see cref="S2SReviewWorkspacePreparer"/> — the "set up the PR checkout on the shared host"
/// step that runs before an S2S review provisions. They pin three contracts the shared-host topology depends
/// on: the derived leaf is <c>SanitizeDirectory</c>-stable (so the daemon's host clone dir and LmStreaming's
/// stored <c>DirectoryRelPath</c> name the SAME directory), the host clone drives the exact
/// probe→clone→fetch→checkout git sequence against the PR's base/head shas and the provider-correct remote,
/// and an existing workspace for the same leaf is reused rather than duplicated.
/// </summary>
public sealed class S2SReviewWorkspacePreparerTests
{
    private const string BasePath = "/sandbox/workspaces";

    [Theory]
    [InlineData("118", "review-pr-118")]
    [InlineData("feature/x", "review-pr-featurex")] // path separators stripped
    [InlineData("1 2", "review-pr-1-2")] // whitespace runs collapse to '-'
    [InlineData("..", "review-pr")] // '..' removed then trailing '-' trimmed → stable fallback
    public void DeriveLeaf_produces_a_sanitize_stable_single_segment_leaf(string prId, string expected)
    {
        S2SReviewWorkspacePreparer.DeriveLeaf(prId).Should().Be(expected);
    }

    [Fact]
    public async Task PrepareAsync_host_clones_the_pr_base_and_head_into_the_leaf_dir_for_github()
    {
        // A fresh host dir: the rev-parse probe fails, so the preparer must clone → fetch the exact base+head
        // → force-checkout the head. Fetch/checkout succeed via the runner's default result.
        var git = new FakeSandboxCommandRunner()
            .OnArgvContains("rev-parse --is-inside-work-tree", new SandboxCommandResult(1, string.Empty, "not a repo"));
        var handler = new FakeHttpMessageHandler()
            .OnJson(HttpMethod.Get, "api/workspaces", "[]")
            .OnJson(
                HttpMethod.Post,
                "api/workspaces",
                "{\"id\":\"ws-new\",\"name\":\"Review PR #118\",\"directoryRelPath\":\"review-pr-118\","
                    + "\"marketplaces\":[\"code-reviewer\"]}");
        using var http = NewHttp(handler);
        var preparer = NewPreparer(http, git);

        var prepared = await preparer.PrepareAsync(
            MakeRun("118"), MakeRepo("github", project: null), "github", CancellationToken.None);

        prepared.Leaf.Should().Be("review-pr-118");
        prepared.WorkspaceId.Should().Be("ws-new");

        var commands = git.Commands.Select(c => string.Join(" ", c.Argv)).ToList();
        commands.Should().Contain(
            c => c.Contains("clone")
                && c.Contains("https://github.com/achieveai/LmDotnetTools.git")
                && c.Contains("/sandbox/workspaces/review-pr-118"),
            "the checkout is host-cloned into {base}/{leaf}");
        commands.Should().Contain(c => c.Contains("fetch origin base-sha head-sha"), "the exact PR commits are fetched");
        commands.Should().Contain(c => c.Contains("checkout --force head-sha"), "the PR head is force-checked-out");

        var createBody = handler.Requests
            .Single(r => r.Method == HttpMethod.Post && r.Uri.ToString().Contains("api/workspaces", StringComparison.Ordinal))
            .Body;
        createBody.Should().Contain("\"directoryRelPath\":\"review-pr-118\"")
            .And.Contain("\"marketplaces\":[\"code-reviewer\"]", "the code-reviewer marketplace surfaces the sub-agent tree");
    }

    [Fact]
    public async Task PrepareAsync_builds_the_ado_dev_azure_remote_from_org_project_and_repo()
    {
        var git = new FakeSandboxCommandRunner()
            .OnArgvContains("rev-parse --is-inside-work-tree", new SandboxCommandResult(1, string.Empty, "not a repo"));
        var handler = new FakeHttpMessageHandler()
            .OnJson(HttpMethod.Get, "api/workspaces", "[]")
            .OnJson(
                HttpMethod.Post,
                "api/workspaces",
                "{\"id\":\"ws-ado\",\"name\":\"Review PR #200\",\"directoryRelPath\":\"review-pr-200\",\"marketplaces\":[]}");
        using var http = NewHttp(handler);
        var preparer = NewPreparer(http, git);

        _ = await preparer.PrepareAsync(
            MakeRun("200"), MakeRepo("ado", project: "Platform"), "ado", CancellationToken.None);

        var commands = git.Commands.Select(c => string.Join(" ", c.Argv)).ToList();
        commands.Should().Contain(
            c => c.Contains("clone") && c.Contains("https://dev.azure.com/achieveai/Platform/_git/LmDotnetTools"),
            "ADO clones from dev.azure.com/{org}/{project}/_git/{repo}");
    }

    [Fact]
    public async Task PrepareAsync_reuses_an_existing_workspace_for_the_same_leaf_without_creating_a_duplicate()
    {
        // The probe succeeds (default result) so no clone happens; the existing workspace whose DirectoryRelPath
        // matches the derived leaf is reused — so only ONE api/workspaces call (the GET), never a POST.
        var git = new FakeSandboxCommandRunner();
        var handler = new FakeHttpMessageHandler()
            .OnJson(
                HttpMethod.Get,
                "api/workspaces",
                "[{\"id\":\"ws-existing\",\"name\":\"Review PR #118\",\"directoryRelPath\":\"review-pr-118\","
                    + "\"marketplaces\":[\"code-reviewer\"]}]");
        using var http = NewHttp(handler);
        var preparer = NewPreparer(http, git);

        var prepared = await preparer.PrepareAsync(
            MakeRun("118"), MakeRepo("github", project: null), "github", CancellationToken.None);

        prepared.WorkspaceId.Should().Be("ws-existing", "the workspace pointing at the leaf is reused, not recreated");
        handler.Requests
            .Where(r => r.Uri.ToString().Contains("api/workspaces", StringComparison.Ordinal))
            .Should().OnlyContain(r => r.Method == HttpMethod.Get, "reuse must not POST a duplicate workspace");
    }

    private static HttpClient NewHttp(FakeHttpMessageHandler handler) =>
        new(handler) { BaseAddress = new Uri("http://localhost:5051/") };

    private static S2SReviewWorkspacePreparer NewPreparer(HttpClient http, FakeSandboxCommandRunner git) =>
        new(
            new LmStreamingS2SClient(http, "s", "id", "key"),
            new GitRunner(git),
            BasePath,
            reviewMarketplace: "code-reviewer",
            NullLogger<S2SReviewWorkspacePreparer>.Instance);

    private static ReviewRun MakeRun(string prId) =>
        new()
        {
            RepoId = 1,
            PrId = prId,
            HeadSha = "head-sha",
            BaseSha = "base-sha",
            TriggerWatermark = "wm-1",
            ReviewKind = "full",
            VariantId = "primary",
            Mode = "collect-only",
            Stage = ReviewStage.Discovered,
            WorkflowStatus = WorkflowStatus.Running,
            PrLifecycleState = PrLifecycleState.Open,
        };

    private static RepoIdentity MakeRepo(string provider, string? project) =>
        new()
        {
            Provider = provider,
            OrgOrOwner = "achieveai",
            Project = project,
            RepoName = "LmDotnetTools",
            RepoStableId = "repo-stable-1",
        };
}
