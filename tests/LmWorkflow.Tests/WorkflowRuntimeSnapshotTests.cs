using System.Text.Json.Nodes;
using AchieveAi.LmDotnetTools.LmWorkflow.Model;
using AchieveAi.LmDotnetTools.LmWorkflow.Persistence;
using AchieveAi.LmDotnetTools.LmWorkflow.Runtime;
using FluentAssertions;
using Xunit;

namespace AchieveAi.LmDotnetTools.LmWorkflow.Tests;

/// <summary>
///     Loop-free tests of <see cref="WorkflowRuntime.Snapshot"/> / <see cref="WorkflowRuntime.FromSnapshot"/>:
///     a driven runtime is snapshotted and rebuilt, and the full mutable state must survive — except that an
///     orphaned in-flight task is reset so the resumed controller re-spawns it.
/// </summary>
public class WorkflowRuntimeSnapshotTests
{
    private const string AnalyzeUnit = "analyze:1:task";

    /// <summary>A runtime positioned at <c>analyze</c> with its single task validated (output + write applied).</summary>
    private static WorkflowRuntime RuntimeWithValidatedAnalyze()
    {
        var runtime = new WorkflowRuntime();
        runtime.LoadDefinition(WorkflowJson.Deserialize(Phase3Fixtures.LinearBlockingAgent));
        runtime.AdvanceTo("start", "analyze", null);
        _ = runtime.ComposeNextExpectedAction();
        runtime.RegisterSpawn("tc_agent", AnalyzeUnit);
        runtime.ObserveResult("tc_agent", """{ "summary": "all good" }""", isError: false);
        return runtime;
    }

    [Fact]
    public void SnapshotThenFromSnapshot_RestoresAllRuntimeState()
    {
        var runtime = RuntimeWithValidatedAnalyze();
        runtime.AdvanceTo("analyze", "done", JsonNode.Parse("""{ "summary": "final" }"""));

        var restored = WorkflowRuntime.FromSnapshot(runtime.Snapshot());

        restored.CurrentNodeId.Should().Be("done");
        restored.Step.Should().Be(2);
        restored.IsComplete.Should().BeTrue();
        restored.Result!["summary"]!.GetValue<string>().Should().Be("final");
        restored.Visits.Should().Contain(new KeyValuePair<string, int>("start", 1));
        restored.Visits.Should().Contain(new KeyValuePair<string, int>("analyze", 1));
        restored.Visits.Should().Contain(new KeyValuePair<string, int>("done", 1));
        restored.Outputs["analyze"]!["task"]!["summary"]!.GetValue<string>().Should().Be("all good");
        restored.State["analysis"]!["summary"]!.GetValue<string>().Should().Be("all good");
        StatusOf(restored, AnalyzeUnit).Should().Be("validated");
    }

    [Fact]
    public void FromSnapshot_ResetsInFlightOrphan_ButKeepsValidatedTaskAndItsOutput()
    {
        var runtime = RuntimeWithValidatedAnalyze();

        // Inject a second occurrence that was mid-spawn (in_flight with a live tool-call correlation).
        var baseSnapshot = runtime.Snapshot();
        var orphan = new WorkflowTaskSnapshot
        {
            Name = "analyze:1:orphan",
            NodeId = "analyze",
            Visit = 1,
            TaskId = "orphan",
            Status = WorkflowTaskStatus.InFlight,
            ToolCallId = "tc_orphan",
            MaxValidationRetries = 0,
        };
        var snapshot = baseSnapshot with { Tasks = [.. baseSnapshot.Tasks, orphan] };

        var restored = WorkflowRuntime.FromSnapshot(snapshot);

        // Orphan: in_flight -> pending, correlation dropped so the controller re-spawns it.
        StatusOf(restored, "analyze:1:orphan").Should().Be("pending");
        restored.IsRegisteredSpawn("tc_orphan").Should().BeFalse();

        // The validated task is untouched: still validated, output intact.
        StatusOf(restored, AnalyzeUnit).Should().Be("validated");
        restored.Outputs["analyze"]!["task"]!["summary"]!.GetValue<string>().Should().Be("all good");
    }

    private static string StatusOf(WorkflowRuntime runtime, string unitName) =>
        runtime.GetProjection(null)["tasks"]![unitName]!.GetValue<string>();
}
