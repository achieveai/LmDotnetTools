using System.Text.Json.Nodes;
using AchieveAi.LmDotnetTools.LmWorkflow.Model;
using AchieveAi.LmDotnetTools.LmWorkflow.Runtime;
using FluentAssertions;
using Xunit;

namespace AchieveAi.LmDotnetTools.LmWorkflow.Tests;

/// <summary>
///     Proves the read-only <c>recommendedBranch</c>/<c>branchEvaluations</c> surface for an active
///     conditional node: the recommendation is the first structured branch whose condition holds (prose
///     <c>when</c> branches are skipped), falling back to <c>else</c>; the surface never constrains
///     <see cref="WorkflowRuntime.AdvanceTo"/>, which still accepts any declared edge the controller picks.
/// </summary>
public class ConditionalRoutingTests
{
    private static WorkflowRuntime RuntimeAtGate(int count)
    {
        var runtime = new WorkflowRuntime();
        runtime.LoadDefinition(WorkflowJson.Deserialize(Phase4bFixtures.ConditionalRouting));
        runtime.SetState("state.count", JsonValue.Create(count), "set", null);
        runtime.AdvanceTo("start", "gate", null);
        return runtime;
    }

    private static string Recommended(WorkflowRuntime runtime) =>
        runtime.GetProjection(null)["recommendedBranch"]!.GetValue<string>();

    [Fact]
    public void RecommendedBranch_PicksFirstMatchingStructuredBranch()
    {
        // count=10 matches the first structured branch (gte 5) even though gte 1 also matches.
        Recommended(RuntimeAtGate(10)).Should().Be("high");
    }

    [Fact]
    public void RecommendedBranch_FallsThroughToSecondStructuredBranch()
    {
        // count=3 fails gte 5 but matches gte 1.
        Recommended(RuntimeAtGate(3)).Should().Be("low");
    }

    [Fact]
    public void RecommendedBranch_FallsBackToElse_WhenNoneMatch()
    {
        // count=0 matches no structured branch -> the else target.
        Recommended(RuntimeAtGate(0)).Should().Be("zero");
    }

    [Fact]
    public void ProseWhenBranch_IsSkippedForTheRecommendation_ButSurfacedAsUnmatched()
    {
        var projection = RuntimeAtGate(10).GetProjection(null);

        // The prose branch (first in order) never wins the recommendation.
        projection["recommendedBranch"]!.GetValue<string>().Should().NotBe("prose_target");

        var evaluations = projection["branchEvaluations"]!.AsArray();
        evaluations.Should().HaveCount(3);
        evaluations[0]!["to"]!.GetValue<string>().Should().Be("prose_target");
        evaluations[0]!["matched"]!.GetValue<bool>().Should().BeFalse();
        evaluations[1]!["to"]!.GetValue<string>().Should().Be("high");
        evaluations[1]!["matched"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public void AdvanceTo_StillAllowsTheControllerToPickElseOrAnyBranchTarget()
    {
        // Recommendation is "high", but the controller may route to the else target...
        var toElse = RuntimeAtGate(10);
        toElse.AdvanceTo("gate", "zero", null);
        toElse.CurrentNodeId.Should().Be("zero");

        // ...or to a different branch target than the one recommended.
        var toOtherBranch = RuntimeAtGate(10);
        toOtherBranch.AdvanceTo("gate", "low", null);
        toOtherBranch.CurrentNodeId.Should().Be("low");
    }
}
