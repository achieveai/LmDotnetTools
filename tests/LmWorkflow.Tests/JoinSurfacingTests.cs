using System.Text.Json.Nodes;
using AchieveAi.LmDotnetTools.LmWorkflow.Model;
using AchieveAi.LmDotnetTools.LmWorkflow.Runtime;
using FluentAssertions;
using Xunit;

namespace AchieveAi.LmDotnetTools.LmWorkflow.Tests;

/// <summary>
///     Proves the controller-facing <c>join</c> surface in the projection: counts by status and the
///     <c>satisfied</c> flag, which flips when all units validate (mode <c>all</c>) or when the first one
///     validates (mode <c>any</c>). The runtime never auto-advances — this is purely a surface.
/// </summary>
public class JoinSurfacingTests
{
    private static WorkflowRuntime RuntimeAtFan(string joinMode)
    {
        var runtime = new WorkflowRuntime();
        runtime.LoadDefinition(WorkflowJson.Deserialize(Phase4Fixtures.ForEachWorkflow(joinMode)));
        runtime.AdvanceTo("start", "fan", null);
        _ = runtime.ComposeNextExpectedAction();
        return runtime;
    }

    private static void ValidateUnit(WorkflowRuntime runtime, int index)
    {
        runtime.RegisterSpawn($"tc_{index}", $"fan:1:task:{index}");
        runtime.ObserveResult($"tc_{index}", $$"""{ "text": "v{{index}}" }""", isError: false);
    }

    private static JsonNode Join(WorkflowRuntime runtime) => runtime.GetProjection(null)["join"]!;

    [Fact]
    public void AllJoin_Satisfied_OnlyWhenEveryUnitValidated()
    {
        var runtime = RuntimeAtFan("all");

        var join = Join(runtime);
        join["mode"]!.GetValue<string>().Should().Be("all");
        join["total"]!.GetValue<int>().Should().Be(3);
        join["satisfied"]!.GetValue<bool>().Should().BeFalse();

        ValidateUnit(runtime, 0);
        Join(runtime)["satisfied"]!.GetValue<bool>().Should().BeFalse();

        ValidateUnit(runtime, 1);
        Join(runtime)["satisfied"]!.GetValue<bool>().Should().BeFalse();

        ValidateUnit(runtime, 2);
        var done = Join(runtime);
        done["validated"]!.GetValue<int>().Should().Be(3);
        done["satisfied"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public void AnyJoin_Satisfied_AfterFirstValidated()
    {
        var runtime = RuntimeAtFan("any");

        var join = Join(runtime);
        join["mode"]!.GetValue<string>().Should().Be("any");
        join["satisfied"]!.GetValue<bool>().Should().BeFalse();

        ValidateUnit(runtime, 0);

        var after = Join(runtime);
        after["validated"]!.GetValue<int>().Should().Be(1);
        after["satisfied"]!.GetValue<bool>().Should().BeTrue();
    }
}
