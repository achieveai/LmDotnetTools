using CodeReviewDaemon.Sample.Agents;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeReviewDaemon.Sample.Tests.Scenarios;

public sealed class S2SReviewWorkspacePreparerTests
{
    [Fact]
    public async Task All_slots_and_repositories_reuse_one_catalog_entry_with_distinct_cwd()
    {
        var handler = new FakeHttpMessageHandler()
            .OnJson(HttpMethod.Get, "api/workspaces", "[]")
            .OnJson(
                HttpMethod.Post,
                "api/workspaces",
                """{"id":"shared","name":"Nova reviews","directoryRelPath":"nova-reviews","marketplaces":[]}"""
            );
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5051/") };
        var preparer = NewPreparer(http);
        var first = await preparer.AdoptSlotAsync(new("Nova", 0), Run(), default);
        var second = await preparer.AdoptSlotAsync(new("Widgets", 1), Run(), default);
        first.Leaf.Should().Be("nova-reviews");
        first.WorkspaceId.Should().Be(second.WorkspaceId);
        first.WorkingDirectoryRelPath.Should().Be(".worktrees/Nova-0");
        second.WorkingDirectoryRelPath.Should().Be(".worktrees/Widgets-1");
        second.SourceRelativePath.Should().Be(".worktrees/Widgets-1/repos/Widgets");
        handler.Requests.Count(r => r.Method == HttpMethod.Post).Should().Be(1);
    }

    [Fact]
    public async Task Existing_shared_workspace_is_reused_without_creation()
    {
        var handler = new FakeHttpMessageHandler().OnJson(
            HttpMethod.Get,
            "api/workspaces",
            """[{"id":"existing","name":"Nova reviews","directoryRelPath":"nova-reviews","marketplaces":[]}]"""
        );
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5051/") };
        var prepared = await NewPreparer(http).AdoptSlotAsync(new("Nova", 5), Run(), default);
        prepared.WorkspaceId.Should().Be("existing");
        handler.Requests.Should().OnlyContain(r => r.Method == HttpMethod.Get);
    }

    [Fact]
    public async Task Invalid_slot_is_rejected_before_network_calls()
    {
        var handler = new FakeHttpMessageHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5051/") };
        await Assert.ThrowsAsync<ArgumentException>(() =>
            NewPreparer(http).AdoptSlotAsync(new("../Nova", 0), Run(), default)
        );
        handler.Requests.Should().BeEmpty();
    }

    private static S2SReviewWorkspacePreparer NewPreparer(HttpClient http) =>
        new(
            new LmStreamingS2SClient(http, "s", "id", "key"),
            "code-reviewer",
            NullLogger<S2SReviewWorkspacePreparer>.Instance
        );

    private static ReviewRun Run() =>
        new()
        {
            RepoId = 1,
            PrId = "118",
            HeadSha = "head",
            BaseSha = "base",
            TriggerWatermark = "wm",
            ReviewKind = "full",
            VariantId = "primary",
            Mode = "collect-only",
            Stage = ReviewStage.Discovered,
            WorkflowStatus = WorkflowStatus.Running,
            PrLifecycleState = PrLifecycleState.Open,
        };
}
