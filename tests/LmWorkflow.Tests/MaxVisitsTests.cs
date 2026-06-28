using AchieveAi.LmDotnetTools.LmWorkflow.Model;
using AchieveAi.LmDotnetTools.LmWorkflow.Runtime;
using FluentAssertions;
using Xunit;

namespace AchieveAi.LmDotnetTools.LmWorkflow.Tests;

/// <summary>
///     Proves the maxVisits/onMaxVisits safety rail: a node with <c>maxVisits = N</c> may be ENTERED N
///     times, the (N+1)-th entry is REFUSED (with a message naming the <c>onMaxVisits</c> target), the
///     projection surfaces <c>atVisitCeiling</c>/<c>onMaxVisits</c> at the ceiling, and routing to the
///     <c>onMaxVisits</c> target is allowed. The runtime mutates nothing on the rejected path.
/// </summary>
public class MaxVisitsTests
{
    private static WorkflowRuntime LoadedLoop(int maxVisits, bool withOnMaxVisits = true)
    {
        var runtime = new WorkflowRuntime();
        runtime.LoadDefinition(
            WorkflowJson.Deserialize(Phase4bFixtures.MaxVisitsLoop(maxVisits, withOnMaxVisits))
        );
        return runtime;
    }

    [Fact]
    public void MaxVisitsTwo_AllowsEntriesOneAndTwo_RefusesThird()
    {
        var runtime = LoadedLoop(maxVisits: 2);

        runtime.AdvanceTo("start", "a", null); // a #1
        runtime.AdvanceTo("a", "gate", null); // gate #1 (allowed)
        runtime.AdvanceTo("gate", "a", null); // back-edge -> a #2
        runtime.AdvanceTo("a", "gate", null); // gate #2 (allowed; now at ceiling)

        runtime.Visits["gate"].Should().Be(2);

        runtime.AdvanceTo("gate", "a", null); // back-edge -> a #3

        // The 3rd entry into gate is refused; nothing mutates on the rejected path.
        var act = () => runtime.AdvanceTo("a", "gate", null);
        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*maxVisits 2*onMaxVisits 'author'*");
        runtime.Visits["gate"].Should().Be(2);
        runtime.CurrentNodeId.Should().Be("a");
    }

    [Fact]
    public void Projection_AtCeiling_SurfacesAtVisitCeilingAndOnMaxVisits()
    {
        var runtime = LoadedLoop(maxVisits: 2);

        runtime.AdvanceTo("start", "a", null);
        runtime.AdvanceTo("a", "gate", null); // gate #1
        runtime.AdvanceTo("gate", "a", null);
        runtime.AdvanceTo("a", "gate", null); // gate #2 (ceiling); current node is gate

        var projection = runtime.GetProjection(null);
        projection["atVisitCeiling"]!.GetValue<bool>().Should().BeTrue();
        projection["onMaxVisits"]!.GetValue<string>().Should().Be("author");
    }

    [Fact]
    public void RoutingToOnMaxVisits_IsAllowed_FromTheCeiling()
    {
        var runtime = LoadedLoop(maxVisits: 2);

        runtime.AdvanceTo("start", "a", null);
        runtime.AdvanceTo("a", "gate", null); // gate #1
        runtime.AdvanceTo("gate", "a", null);
        runtime.AdvanceTo("a", "gate", null); // gate #2 (ceiling); current node is gate

        // The onMaxVisits target ('author') is reachable from the ceiling.
        runtime.AdvanceTo("gate", "author", null);
        runtime.CurrentNodeId.Should().Be("author");
    }

    [Fact]
    public void BackEdgeLoop_MaxVisitsThree_AllowsThreeEntries_ThenRefuses()
    {
        var runtime = LoadedLoop(maxVisits: 3);

        // A -> gate -> A -> gate -> A -> gate (three gate entries), then the fourth is refused.
        runtime.AdvanceTo("start", "a", null); // a #1
        runtime.AdvanceTo("a", "gate", null); // gate #1
        runtime.AdvanceTo("gate", "a", null); // a #2
        runtime.AdvanceTo("a", "gate", null); // gate #2
        runtime.AdvanceTo("gate", "a", null); // a #3
        runtime.AdvanceTo("a", "gate", null); // gate #3 (ceiling)
        runtime.AdvanceTo("gate", "a", null); // a #4

        runtime.Visits["gate"].Should().Be(3);

        var act = () => runtime.AdvanceTo("a", "gate", null); // gate #4 -> refused
        act.Should().Throw<InvalidOperationException>().WithMessage("*maxVisits 3*");
    }

    [Fact]
    public void MaxVisits_WithNoOnMaxVisits_RefusesWithAClearMessage()
    {
        var runtime = LoadedLoop(maxVisits: 1, withOnMaxVisits: false);

        runtime.AdvanceTo("start", "a", null); // a #1
        runtime.AdvanceTo("a", "gate", null); // gate #1 (ceiling at maxVisits=1)
        runtime.AdvanceTo("gate", "a", null); // back-edge -> a #2

        var act = () => runtime.AdvanceTo("a", "gate", null); // gate #2 -> refused
        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*maxVisits 1*no onMaxVisits is defined*");
    }
}
