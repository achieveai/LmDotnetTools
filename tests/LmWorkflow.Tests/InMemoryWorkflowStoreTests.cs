using System.Text.Json.Nodes;
using AchieveAi.LmDotnetTools.LmWorkflow.Model;
using AchieveAi.LmDotnetTools.LmWorkflow.Persistence;
using AchieveAi.LmDotnetTools.LmWorkflow.Runtime;
using FluentAssertions;
using Xunit;

namespace AchieveAi.LmDotnetTools.LmWorkflow.Tests;

/// <summary>
///     Behavioral tests for <see cref="InMemoryWorkflowStore"/>: round-trip fidelity, absent/delete/list
///     semantics, and the isolation guarantee that a stored snapshot never aliases the live runtime.
/// </summary>
public class InMemoryWorkflowStoreTests
{
    private const string AnalyzeUnit = "analyze:1:task";

    /// <summary>Drives a runtime to a validated mid-flow state and captures a snapshot under <paramref name="instanceId"/>.</summary>
    private static WorkflowInstanceSnapshot PopulatedSnapshot(string instanceId)
    {
        var runtime = new WorkflowRuntime();
        runtime.LoadDefinition(WorkflowJson.Deserialize(Phase3Fixtures.LinearBlockingAgent));
        runtime.AdvanceTo("start", "analyze", null);
        _ = runtime.ComposeNextExpectedAction();
        runtime.RegisterSpawn("tc_agent", AnalyzeUnit);
        runtime.ObserveResult("tc_agent", """{ "summary": "all good" }""", isError: false);
        return runtime.Snapshot() with { InstanceId = instanceId };
    }

    [Fact]
    public async Task SaveThenLoad_RoundTripsAnEqualSnapshot()
    {
        var store = new InMemoryWorkflowStore();
        var original = PopulatedSnapshot("wf-1");

        await store.SaveAsync("wf-1", original);
        var loaded = await store.LoadAsync("wf-1");

        loaded.Should().NotBeNull();
        JsonNode
            .DeepEquals(JsonNode.Parse(loaded!.ToJson()), JsonNode.Parse(original.ToJson()))
            .Should()
            .BeTrue("a loaded snapshot must serialize identically to the saved one");
    }

    [Fact]
    public async Task LoadAsync_AbsentInstance_ReturnsNull()
    {
        var store = new InMemoryWorkflowStore();

        (await store.LoadAsync("never-saved")).Should().BeNull();
    }

    [Fact]
    public async Task DeleteAsync_RemovesSnapshot_AndIsNoOpWhenAbsent()
    {
        var store = new InMemoryWorkflowStore();
        await store.SaveAsync("wf-1", PopulatedSnapshot("wf-1"));

        await store.DeleteAsync("wf-1");
        (await store.LoadAsync("wf-1")).Should().BeNull();

        var deleteAbsent = async () => await store.DeleteAsync("missing");
        await deleteAbsent.Should().NotThrowAsync();
    }

    [Fact]
    public async Task ListAsync_ReturnsAllStoredInstanceIds()
    {
        var store = new InMemoryWorkflowStore();
        await store.SaveAsync("wf-1", PopulatedSnapshot("wf-1"));
        await store.SaveAsync("wf-2", PopulatedSnapshot("wf-2"));

        (await store.ListAsync()).Should().BeEquivalentTo(["wf-1", "wf-2"]);
    }

    [Fact]
    public async Task SaveAsync_StoresIsolatedCopy_MutatingThePassedSnapshotDoesNotLeak()
    {
        var store = new InMemoryWorkflowStore();
        var snapshot = PopulatedSnapshot("wf-1");
        await store.SaveAsync("wf-1", snapshot);

        // Mutate the in-memory snapshot AFTER saving; the stored copy must not observe it.
        snapshot.State["injected"] = JsonValue.Create("leak");

        var loaded = await store.LoadAsync("wf-1");
        loaded!.State.Should().NotContainKey("injected");
    }

    [Fact]
    public void FromJson_NewerSchemaVersion_Throws()
    {
        // A snapshot written by a newer, unknown schema must be refused rather than silently mis-read.
        const string json = """{ "schemaVersion": 999, "instanceId": "wf-future" }""";

        var act = () => WorkflowInstanceSnapshot.FromJson(json);

        act.Should().Throw<NotSupportedException>().WithMessage("*outside supported versions*");
    }

    [Fact]
    public void FromJson_CurrentSchemaVersion_Loads()
    {
        const string json = """{ "schemaVersion": 2, "instanceId": "wf-current" }""";

        var snapshot = WorkflowInstanceSnapshot.FromJson(json);

        snapshot.SchemaVersion.Should().Be(WorkflowInstanceSnapshot.CurrentSchemaVersion);
        snapshot.InstanceId.Should().Be("wf-current");
    }

    [Fact]
    public void Schema1_snapshot_resumes_and_next_runtime_save_upgrades_to_schema2()
    {
        var legacy = JsonNode.Parse(PopulatedSnapshot("legacy").ToJson())!.AsObject();
        legacy["schemaVersion"] = 1;
        legacy.Remove("sessions");
        legacy.Remove("deadlines");
        var loaded = WorkflowInstanceSnapshot.FromJson(legacy.ToJsonString());
        loaded.SchemaVersion.Should().Be(1);
        loaded.Sessions.Should().BeEmpty();
        loaded.Deadlines.Should().BeEmpty();
        var saved = WorkflowRuntime.FromSnapshot(loaded).Snapshot();
        saved.SchemaVersion.Should().Be(2);
        saved.State.ToJsonString().Should().Be(loaded.State.ToJsonString());
        saved.Tasks.Should().Contain(value => value.Status == WorkflowTaskStatus.Validated);
    }

    [Fact]
    public void Schema1_reader_guard_refuses_schema2_inflight_execution_state()
    {
        var snapshot = PopulatedSnapshot("automatic") with
        {
            Sessions = new Dictionary<string, string> { ["parent"] = "host-thread" },
            Deadlines = new Dictionary<string, DateTimeOffset> { [AnalyzeUnit] = DateTimeOffset.UtcNow },
        };
        // Frozen schema-1 envelope guard, from the reader before automatic execution was introduced.
        // The legacy DTO ignores additive JSON fields, so its schema gate is the downgrade barrier.
        var legacyRead = () =>
        {
            var envelope = JsonNode.Parse(snapshot.ToJson())!;
            if (envelope["schemaVersion"]!.GetValue<int>() > 1)
                throw new NotSupportedException("Workflow snapshot schema version is newer than supported 1.");
        };
        legacyRead.Should().Throw<NotSupportedException>();
        var current = WorkflowInstanceSnapshot.FromJson(snapshot.ToJson());
        current.Sessions.Should().Contain("parent", "host-thread");
        current.Deadlines.Should().ContainKey(AnalyzeUnit);
    }

    [Fact]
    public async Task SaveAsync_StoresIsolatedCopy_LaterRuntimeMutationDoesNotLeak()
    {
        var store = new InMemoryWorkflowStore();
        var runtime = new WorkflowRuntime();
        runtime.LoadDefinition(WorkflowJson.Deserialize(Phase3Fixtures.LinearBlockingAgent));
        runtime.AttachStore(store, "wf-1");
        runtime.AdvanceTo("start", "analyze", null);

        var afterAdvance = await store.LoadAsync("wf-1");
        afterAdvance!.CurrentNodeId.Should().Be("analyze");

        // A subsequent runtime mutation persists a NEW snapshot, but the already-loaded copy is frozen.
        runtime.SetState("state.injected", JsonValue.Create("later"), "set");
        afterAdvance.State.Should().NotContainKey("injected");
    }
}
