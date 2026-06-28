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
        runtime.SetState("state.injected", JsonValue.Create("later"), "set", null);
        afterAdvance.State.Should().NotContainKey("injected");
    }
}
