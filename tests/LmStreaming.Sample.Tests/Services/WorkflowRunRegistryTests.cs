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
}
