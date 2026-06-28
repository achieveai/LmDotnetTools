using System.Text.Json.Nodes;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmWorkflow.Model;
using AchieveAi.LmDotnetTools.LmWorkflow.Runtime;
using AchieveAi.LmDotnetTools.LmWorkflow.Tools;
using FluentAssertions;
using Xunit;

namespace AchieveAi.LmDotnetTools.LmWorkflow.Tests;

/// <summary>
///     Proves the human-readable rendering of the controller projection: a procedural node lists its
///     ready-to-spawn units and join, a conditional node names the recommended branch, and an active
///     safety rail surfaces its escape target. Also proves the <c>GetWorkflow</c> tool returns prose
///     (not JSON) when the controller asks for a <c>prose</c> projection.
/// </summary>
public class WorkflowProseRendererTests
{
    private static string Prose(WorkflowRuntime runtime) =>
        WorkflowProseRenderer.Render(runtime.GetProjection("prose"));

    [Fact]
    public void Render_ProceduralWithUnits_DescribesNodeUnitsAndJoin()
    {
        var runtime = new WorkflowRuntime();
        runtime.LoadDefinition(WorkflowJson.Deserialize(Phase4Fixtures.ForEachWorkflow("all")));
        runtime.AdvanceTo("start", "fan", null);
        _ = runtime.ComposeNextExpectedAction();

        var prose = Prose(runtime);

        prose.Should().Contain("At node 'fan'");
        prose.Should().Contain("Ready to spawn 3 unit(s)");
        prose.Should().Contain("fan:1:task:0");
        prose.Should().Contain("general-purpose");
        prose.Should().Contain("Join (all)");
    }

    [Fact]
    public void Render_ConditionalWithRecommendedBranch_NamesTheBranch()
    {
        var runtime = new WorkflowRuntime();
        runtime.LoadDefinition(WorkflowJson.Deserialize(Phase4bFixtures.ConditionalRouting));
        runtime.SetState("state.count", JsonValue.Create(10), "set", null);
        runtime.AdvanceTo("start", "gate", null);

        Prose(runtime).Should().Contain("Recommended branch: 'high'");
    }

    [Fact]
    public void Render_RailActive_SurfacesTheVisitCeilingEscape()
    {
        var runtime = new WorkflowRuntime();
        runtime.LoadDefinition(WorkflowJson.Deserialize(Phase4bFixtures.MaxVisitsLoop(2)));
        runtime.AdvanceTo("start", "a", null);
        runtime.AdvanceTo("a", "gate", null); // gate #1
        runtime.AdvanceTo("gate", "a", null);
        runtime.AdvanceTo("a", "gate", null); // gate #2 -> at the visit ceiling

        var prose = Prose(runtime);

        prose.Should().Contain("Visit ceiling reached");
        prose.Should().Contain("onMaxVisits 'author'");
    }

    [Fact]
    public async Task GetWorkflowTool_WithProseProjection_ReturnsProseNotJson()
    {
        var runtime = new WorkflowRuntime();
        runtime.LoadDefinition(WorkflowJson.Deserialize(Phase4bFixtures.ConditionalRouting));
        runtime.SetState("state.count", JsonValue.Create(10), "set", null);
        runtime.AdvanceTo("start", "gate", null);

        var getWorkflow = new WorkflowToolProvider(runtime)
            .GetFunctions()
            .Single(f => f.Contract.Name == "GetWorkflow");

        var result = await getWorkflow.Handler(
            new JsonObject { ["projection"] = "prose" }.ToJsonString(),
            new ToolCallContext(),
            CancellationToken.None
        );

        result.ResultText.Should().StartWith("At node 'gate'");
        result.ResultText.Should().NotContain("\"currentNodeId\""); // not the JSON projection
    }
}
