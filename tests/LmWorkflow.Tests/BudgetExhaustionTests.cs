using AchieveAi.LmDotnetTools.LmWorkflow.Model;
using AchieveAi.LmDotnetTools.LmWorkflow.Runtime;
using FluentAssertions;
using Xunit;

namespace AchieveAi.LmDotnetTools.LmWorkflow.Tests;

/// <summary>
///     Proves the maxStepBudget/onBudgetExhausted safety rail: once <see cref="WorkflowRuntime.Step"/>
///     reaches the definition's budget, a normal advance is REFUSED and only the <c>onBudgetExhausted</c>
///     escape is allowed (bypassing the declared-edge check); the projection surfaces
///     <c>budgetExhausted</c>/<c>onBudgetExhausted</c>. When no escape is defined the advance is allowed so
///     the controller is never deadlocked.
/// </summary>
public class BudgetExhaustionTests
{
    private static WorkflowRuntime Loaded(string json)
    {
        var runtime = new WorkflowRuntime();
        runtime.LoadDefinition(WorkflowJson.Deserialize(json));
        return runtime;
    }

    [Fact]
    public void BudgetExhausted_RefusesNormalAdvance_AllowsOnlyOnBudgetExhausted()
    {
        var runtime = Loaded(Phase4bFixtures.BudgetWithEscape);

        runtime.AdvanceTo("start", "a", null); // step 1
        runtime.AdvanceTo("a", "b", null); // step 2
        runtime.AdvanceTo("b", "a", null); // step 3 == maxStepBudget

        runtime.Step.Should().Be(3);

        // A normal advance is refused, naming the onBudgetExhausted escape.
        var act = () => runtime.AdvanceTo("a", "b", null);
        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*Step budget 3 exhausted*onBudgetExhausted 'done'*");
        runtime.CurrentNodeId.Should().Be("a");

        // Routing to onBudgetExhausted is allowed even though 'a' declares no edge to 'done'.
        runtime.AdvanceTo("a", "done", null);
        runtime.IsComplete.Should().BeTrue();
        runtime.CurrentNodeId.Should().Be("done");
    }

    [Fact]
    public void Projection_AtBudget_SurfacesBudgetExhaustedAndEscape()
    {
        var runtime = Loaded(Phase4bFixtures.BudgetWithEscape);

        runtime.AdvanceTo("start", "a", null);
        runtime.AdvanceTo("a", "b", null);
        runtime.AdvanceTo("b", "a", null); // step 3 == budget

        var projection = runtime.GetProjection(null);
        projection["budgetExhausted"]!.GetValue<bool>().Should().BeTrue();
        projection["onBudgetExhausted"]!.GetValue<string>().Should().Be("done");
    }

    [Fact]
    public void BudgetExhausted_WithNoEscape_AllowsAdvance_WithoutDeadlock()
    {
        var runtime = Loaded(Phase4bFixtures.BudgetNoEscape);

        runtime.AdvanceTo("start", "a", null); // step 1
        runtime.AdvanceTo("a", "b", null); // step 2 == maxStepBudget

        runtime.Step.Should().Be(2);

        // No onBudgetExhausted is defined, so a normal advance still proceeds (no deadlock).
        var act = () => runtime.AdvanceTo("b", "a", null);
        act.Should().NotThrow();
        runtime.Step.Should().Be(3);

        // The projection still flags exhaustion but carries no escape target.
        var projection = runtime.GetProjection(null);
        projection["budgetExhausted"]!.GetValue<bool>().Should().BeTrue();
        projection.Should().NotContainKey("onBudgetExhausted");
    }
}
