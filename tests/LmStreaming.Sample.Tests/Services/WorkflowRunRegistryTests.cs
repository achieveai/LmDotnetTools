using AchieveAi.LmDotnetTools.LmMultiTurn.Collaboration;
using LmStreaming.Sample.Services;

namespace LmStreaming.Sample.Tests.Services;

/// <summary>
/// Durability of the workflow-tab index (<see cref="WorkflowRunRegistry"/>): workflow + delegate tabs are
/// written through to disk so they survive a server restart that evicts the in-memory
/// <c>WorkflowManager</c>. Upserts are merge-only (never delete a run that has left memory), the live
/// snapshot wins on conflict, and persistence is a no-op when no index directory is configured.
/// </summary>
public sealed class WorkflowRunRegistryTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "wf-index-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort temp cleanup.
        }
    }

    private static SubAgentSummary Tab(string kind, string id, string status) =>
        new()
        {
            AgentId = id,
            Kind = kind,
            Name = id,
            Template = "t",
            Task = "task",
            Status = status,
            ThreadId = $"{kind}-{id}",
        };

    [Fact]
    public void PersistThenGet_RoundTripsTheTabs()
    {
        var registry = new WorkflowRunRegistry(_dir);

        registry.PersistTabs("t1", [Tab("workflow", "wf1", "completed"), Tab("subagent", "d1", "completed")]);

        registry.GetPersistedTabs("t1").Select(t => t.AgentId).Should().BeEquivalentTo(["wf1", "d1"]);
    }

    [Fact]
    public void PersistTabs_Upserts_AndNeverDeletesARunThatLeftTheSnapshot()
    {
        var registry = new WorkflowRunRegistry(_dir);

        registry.PersistTabs("t1", [Tab("subagent", "d1", "running")]);
        // A later snapshot no longer reports d1 (its run left memory) but adds d2 — d1 must be retained.
        registry.PersistTabs("t1", [Tab("subagent", "d2", "running")]);

        registry.GetPersistedTabs("t1").Select(t => t.AgentId).Should().BeEquivalentTo(["d1", "d2"]);
    }

    [Fact]
    public void PersistTabs_LiveSnapshotWins_OnConflict()
    {
        var registry = new WorkflowRunRegistry(_dir);

        registry.PersistTabs("t1", [Tab("subagent", "d1", "running")]);
        registry.PersistTabs("t1", [Tab("subagent", "d1", "completed")]);

        registry.GetPersistedTabs("t1").Should().ContainSingle().Which.Status.Should().Be("completed");
    }

    [Fact]
    public void FreshRegistry_ReconcilesPersistedRunningTabsToInterrupted()
    {
        var writer = new WorkflowRunRegistry(_dir);
        writer.PersistTabs("t1", [Tab("workflow", "wf1", "running")]);

        var restarted = new WorkflowRunRegistry(_dir);
        var restored = restarted.GetPersistedTabs("t1");

        restored.Should().ContainSingle().Which.Status.Should().Be("interrupted");
    }

    [Fact]
    public void FreshRegistry_ReconcilesPersistedQueuedTabsToInterrupted()
    {
        var writer = new WorkflowRunRegistry(_dir);
        writer.PersistTabs("t1", [Tab("subagent", "d1", "queued")]);

        var restarted = new WorkflowRunRegistry(_dir);

        restarted.GetPersistedTabs("t1").Should().ContainSingle().Which.Status.Should().Be("interrupted");
    }

    [Fact]
    public void CorruptIndex_IsSurfacedInsteadOfTreatedAsEmpty()
    {
        var registry = new WorkflowRunRegistry(_dir);
        registry.PersistTabs("t1", [Tab("workflow", "wf1", "completed")]);
        var path = Directory.GetFiles(_dir, "*.json").Single();
        File.WriteAllText(path, "{truncated");

        var act = () => registry.GetPersistedTabs("t1");

        act.Should().Throw<JsonException>();
    }

    [Fact]
    public void Persistence_IsNoOp_WhenNoIndexDirectoryConfigured()
    {
        var registry = new WorkflowRunRegistry();

        registry.PersistTabs("t1", [Tab("subagent", "d1", "running")]);

        registry.GetPersistedTabs("t1").Should().BeEmpty();
    }

    [Fact]
    public void GetPersistedTabs_ReturnsEmpty_ForUnknownConversation()
    {
        var registry = new WorkflowRunRegistry(_dir);

        registry.GetPersistedTabs("never-persisted").Should().BeEmpty();
    }

    [Fact]
    public void PersistTabs_RoundTripsCollaborationMetadata()
    {
        var registry = new WorkflowRunRegistry(_dir);
        var tab = Tab("subagent", "d1", AgentCollaborationStatuses.Completed)
            .WithCollaboration(
                new AgentDirectoryEntry
                {
                    AgentId = "d1",
                    CollaborationId = "thread-root",
                    Name = "d1",
                    ParentAgentId = "wfctl-w1",
                    AncestorAgentIds = ["thread-root", "wfctl-w1"],
                    Kind = AgentKind.WorkflowDelegate,
                    Role = "review the diff",
                    Description = "ask about the diff",
                    AgentType = "code-reviewer",
                    StructuralDepth = 2,
                    DelegationDepth = 1,
                    Status = AgentCollaborationStatuses.Completed,
                });

        registry.PersistTabs("t1", [tab]);
        var restored = new WorkflowRunRegistry(_dir).GetPersistedTabs("t1").Should().ContainSingle().Subject;

        restored.SchemaVersion.Should().Be(CollaborationNodeRecord.CurrentSchemaVersion);
        restored.CollaborationId.Should().Be("thread-root");
        restored.ParentAgentId.Should().Be("wfctl-w1");
        restored.AncestorAgentIds.Should().Equal("thread-root", "wfctl-w1");
        restored.AgentKind.Should().Be(nameof(AgentKind.WorkflowDelegate));
        restored.Role.Should().Be("review the diff");
        restored.StructuralDepth.Should().Be(2);
        restored.DelegationDepth.Should().Be(1);
        restored.ToNodeRecord().Should().NotBeNull("a persisted row must read back as the shared node shape");
    }

    [Fact]
    public void RestoredCollaborationTab_IsNeverReportedAsLive()
    {
        var registry = new WorkflowRunRegistry(_dir);
        registry.PersistTabs(
            "t1",
            [
                Tab("subagent", "d1", AgentCollaborationStatuses.Completed) with
                {
                    CollaborationId = "thread-root",
                    AgentKind = nameof(AgentKind.SubAgent),
                    IsLive = true,
                },
            ]);

        var restored = new WorkflowRunRegistry(_dir).GetPersistedTabs("t1").Single();

        restored.IsLive.Should().BeFalse("nothing in this file survived the process that wrote it");
    }

    [Fact]
    public void PersistTabs_NeverWritesViewerScopedFlags()
    {
        var registry = new WorkflowRunRegistry(_dir);
        registry.PersistTabs(
            "t1",
            [Tab("subagent", "d1", "completed") with { IsCurrent = true, IsReadable = true }]);

        var raw = File.ReadAllText(Directory.GetFiles(_dir, "*.json").Single());

        raw.Should().NotContain("isCurrent").And.NotContain("isReadable",
            "those answer 'for this reader', and every later reader is a different one");
    }

    [Fact]
    public void Pre244IndexFile_LoadsAsTheTabsItAlwaysDescribed()
    {
        _ = Directory.CreateDirectory(_dir);
        // A file exactly as a pre-#244 build left it: none of the collaboration members exist.
        File.WriteAllText(
            Path.Combine(_dir, "t1.json"),
            """
            [{"agentId":"wf1","kind":"workflow","name":"review","template":"code-review",
              "task":"review pr","status":"running","threadId":"workflow-w1-t1",
              "lastActivityUtc":"2026-07-01T10:00:00+00:00"}]
            """);

        var restored = new WorkflowRunRegistry(_dir).GetPersistedTabs("t1").Should().ContainSingle().Subject;

        restored.AgentId.Should().Be("wf1");
        restored.Template.Should().Be("code-review");
        restored.Status.Should().Be("interrupted", "it was still running when its host stopped");
        restored.SchemaVersion.Should().Be(0);
        restored.CollaborationId.Should().BeNull();
        restored.IsLive.Should().BeNull("a legacy row never claimed to know, and we must not invent it");
    }
}
