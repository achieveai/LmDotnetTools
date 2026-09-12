using System.Text.Json.Nodes;
using AchieveAi.LmDotnetTools.LmWorkflow.Model;
using AchieveAi.LmDotnetTools.LmWorkflow.Runtime;
using FluentAssertions;
using Xunit;

namespace AchieveAi.LmDotnetTools.LmWorkflow.Tests;

public class StrictWorkflowResultTests
{
    private const string Unit = "analyze:1:task";

    [Theory]
    [InlineData("```json\n{\"summary\":\"ok\"}\n```")]
    [InlineData("Result: {\"summary\":\"ok\"}")]
    [InlineData("{\"summary\":\"first\",\"summary\":\"second\"}")]
    [InlineData("{\"summary\":\"ok\",\"nested\":{\"a\":1,\"a\":2}}")]
    public void StrictResult_RejectsWrappedOrAmbiguousJson_WithoutWritingOutput(string answer)
    {
        var runtime = AtAnalyze(strict: true);
        runtime.RegisterSpawn("turn-1", Unit);

        runtime.CheckSpawnResult("turn-1", answer).IsValid.Should().BeFalse();
        runtime.ObserveResult("turn-1", answer, isError: false);

        runtime.GetProjection(null)["tasks"]![Unit]!.GetValue<string>().Should().Be("pending");
        runtime.Outputs["analyze"]!["task"].Should().BeNull();
    }

    [Fact]
    public void StrictResult_AcceptsWholeJson_AndPreservesValue()
    {
        var runtime = AtAnalyze(strict: true);
        runtime.RegisterSpawn("turn-1", Unit);

        runtime.ObserveResult("turn-1", "{\"summary\":\"ok\"}", isError: false);

        runtime.GetProjection(null)["tasks"]![Unit]!.GetValue<string>().Should().Be("validated");
        runtime.Outputs["analyze"]!["task"]!["summary"]!.GetValue<string>().Should().Be("ok");
    }

    [Fact]
    public void StrictResume_KeepsInFlightOccurrence_ForReconciliationBeforeAnotherInvocation()
    {
        var runtime = AtAnalyze(strict: true);
        runtime.RegisterSpawn("turn-1", Unit);

        var resumed = WorkflowRuntime.FromSnapshot(runtime.Snapshot());

        resumed.Snapshot().Tasks.Single(t => t.Name == Unit).Status.Should().Be(WorkflowTaskStatus.InFlight);
        resumed.Snapshot().Tasks.Single(t => t.Name == Unit).ToolCallId.Should().Be("turn-1");
        resumed.ComposeNextExpectedAction().Should().BeEmpty();
    }

    [Fact]
    public void LegacyResume_StillResurfacesOrphanedControllerTask()
    {
        var runtime = AtAnalyze(strict: false);
        runtime.RegisterSpawn("turn-1", Unit);

        var resumed = WorkflowRuntime.FromSnapshot(runtime.Snapshot());

        resumed.ComposeNextExpectedAction().Should().ContainSingle(u => u.Name == Unit);
    }

    private static WorkflowRuntime AtAnalyze(bool strict)
    {
        var definition = JsonNode.Parse(Phase4Fixtures.SingleTask(maxValidationRetries: 1))!.AsObject();
        definition["strictContracts"] = strict;
        var runtime = WorkflowRuntime.CreateNew();
        runtime.LoadDefinition(WorkflowJson.Deserialize(definition.ToJsonString()));
        runtime.AdvanceTo("start", "analyze", null);
        _ = runtime.ComposeNextExpectedAction();
        return runtime;
    }
}
