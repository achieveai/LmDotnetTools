using AchieveAi.LmDotnetTools.LmWorkflow.Model;
using AchieveAi.LmDotnetTools.LmWorkflow.Runtime;
using FluentAssertions;
using Xunit;

namespace AchieveAi.LmDotnetTools.LmWorkflow.Tests;

/// <summary>
///     The self-correcting spawn-name gate (Option A) — the runtime backstop to the controller prompt.
///     The live "controller keeps creating new agents" loop happened because a mis-named <c>Agent</c>
///     spawn (the bare node id <c>analyze</c> instead of the runtime-surfaced unit name
///     <c>analyze:1:task</c>) ran and was silently discarded: its result never correlated, the unit stayed
///     <c>pending</c>, it re-surfaced in <c>nextExpectedAction</c>, and the controller re-spawned the same
///     wrong name. <see cref="WorkflowRuntime.DescribeSpawnNameRejection"/> lets the exact unit name through
///     (returns null) and rejects anything else with an actionable correction listing the ready unit
///     name(s), so the Agent-tool boundary can turn a mis-named spawn into a recoverable tool error instead
///     of a discarded duplicate.
/// </summary>
public class SpawnNameGateTests
{
    private static WorkflowRuntime RuntimeAtAnalyze()
    {
        var runtime = new WorkflowRuntime();
        runtime.LoadDefinition(WorkflowJson.Deserialize(Phase3Fixtures.LinearBlockingAgent));
        runtime.AdvanceTo("start", "analyze", null);
        return runtime;
    }

    [Fact]
    public void DescribeSpawnNameRejection_AllowsTheExactRuntimeUnitName()
    {
        var runtime = RuntimeAtAnalyze();
        var unit = runtime.ComposeNextExpectedAction().Single().Name;

        runtime
            .DescribeSpawnNameRejection(unit)
            .Should()
            .BeNull(because: "the exact runtime-surfaced unit name correlates, so the spawn must be allowed");
    }

    [Fact]
    public void DescribeSpawnNameRejection_RejectsTheBareNodeId_ListingTheReadyUnitName()
    {
        var runtime = RuntimeAtAnalyze();
        var unit = runtime.ComposeNextExpectedAction().Single().Name; // e.g. "analyze:1:task"

        var rejection = runtime.DescribeSpawnNameRejection("analyze");

        rejection
            .Should()
            .NotBeNull(because: "the bare node id is not a unit name and its result would be silently discarded");
        rejection!.Should().Contain(unit, because: "the correction must hand the controller the exact name to re-issue");
    }

    [Fact]
    public void DescribeSpawnNameRejection_RejectsAMissingName_ListingTheReadyUnitName()
    {
        var runtime = RuntimeAtAnalyze();
        var unit = runtime.ComposeNextExpectedAction().Single().Name;

        var rejection = runtime.DescribeSpawnNameRejection(null);

        rejection.Should().NotBeNull(because: "an omitted name cannot correlate to a unit");
        rejection!.Should().Contain(unit);
    }

    [Fact]
    public void DescribeSpawnNameRejection_JudgesAgainstTheRealUnitSet_EvenBeforeAnyProjectionPoll()
    {
        // Mirrors the live pill order SetCurrentNode -> Agent -> GetWorkflow: the gate must compose the
        // active node's units on demand (exactly as RegisterSpawn does) so a FIRST spawn issued before any
        // GetWorkflow poll is judged against the real unit set rather than spuriously rejected.
        var runtime = RuntimeAtAnalyze();
        var unit = "analyze:1:task";

        runtime
            .DescribeSpawnNameRejection(unit)
            .Should()
            .BeNull(because: "correlation must not depend on the controller polling GetWorkflow first");
    }

    [Fact]
    public void DescribeSpawnNameRejection_AllowsAnAlreadyInFlightUnit_EvenThoughComposeYieldsNoPendingUnit()
    {
        // Regression for the live loop the gate itself introduced: the run observer marks a unit in-flight
        // (RegisterSpawn) BEFORE the tool-boundary gate runs for that same spawn, and Compose() returns only
        // PENDING units — so an in-flight unit yields an empty compose. The gate must judge the name against the
        // full unit set (ActiveUnits), not the pending-only compose, or it wrongly rejects a legitimate spawn as
        // "node has no units" and its result is recorded as a failure (state write dropped).
        var runtime = RuntimeAtAnalyze();
        var unit = runtime.ComposeNextExpectedAction().Single().Name;
        runtime.RegisterSpawn("tc_analyze", unit); // unit is now in-flight -> Compose() yields no pending unit

        runtime.ComposeNextExpectedAction().Should().BeEmpty(because: "the only unit is now in-flight, not pending");
        runtime
            .DescribeSpawnNameRejection(unit)
            .Should()
            .BeNull(because: "an in-flight unit still correlates by name, so re-issuing its exact name must be allowed");
    }

    [Fact]
    public void DescribeSpawnNameRejection_AtANodeWithNoUnits_SteersToRouteInsteadOfSpawn()
    {
        var runtime = new WorkflowRuntime();
        runtime.LoadDefinition(WorkflowJson.Deserialize(Phase3Fixtures.LinearBlockingAgent));
        // Still at the start node: a non-procedural node composes no spawn units, so the controller must
        // ROUTE (SetCurrentNode), not spawn.

        var rejection = runtime.DescribeSpawnNameRejection("anything");

        rejection.Should().NotBeNull(because: "there is no unit to correlate a spawn to at a routing node");
        rejection!.Should().Contain("SetCurrentNode");
    }
}
