using AchieveAi.LmDotnetTools.LmWorkflow.Model;
using AchieveAi.LmDotnetTools.LmWorkflow.Runtime;
using FluentAssertions;
using Xunit;

namespace AchieveAi.LmDotnetTools.LmWorkflow.Tests;

/// <summary>
///     Loop-free proof of the background sub-agent correlation chain: an <c>Agent</c> spawn is registered by
///     unit name, its receipt records the <c>agent_id → task</c> correlation <b>without</b> validating, and
///     the later injected result (matched by <c>agent_id</c>) is the thing that validates and records. The
///     blocking path (a non-receipt tool result) still validates from the tool result, and an unknown
///     <c>agent_id</c> is surfaced.
/// </summary>
public class BackgroundCorrelationTests
{
    private const string Unit = "analyze:1:task";

    private static WorkflowRuntime RuntimeAtAnalyze()
    {
        var runtime = new WorkflowRuntime();
        runtime.LoadDefinition(WorkflowJson.Deserialize(Phase4Fixtures.SingleTask(0)));
        runtime.AdvanceTo("start", "analyze", null);
        _ = runtime.ComposeNextExpectedAction();
        return runtime;
    }

    private static string Receipt(string agentId) =>
        $$"""
        { "agent_id": "{{agentId}}", "name": "general-purpose", "template": "general-purpose", "status": "spawned" }
        """;

    private static string StatusOf(WorkflowRuntime runtime, string unit) =>
        runtime.GetProjection(null)["tasks"]![unit]!.GetValue<string>();

    [Fact]
    public void BackgroundReceipt_DoesNotValidate_InjectedResultValidates()
    {
        var runtime = RuntimeAtAnalyze();
        runtime.RegisterSpawn("tc_agent", Unit);

        // The receipt only records the agent_id correlation; it must NOT validate or record an output.
        runtime.ObserveSpawnResult("tc_agent", Receipt("agent777"), isError: false);

        StatusOf(runtime, Unit).Should().Be("in_flight");
        runtime.Outputs["analyze"]!.AsObject().Should().NotContainKey("task");

        // The injected result, matched by agent_id, is what validates and records.
        runtime.ObserveInjectedResult("agent777", """{ "summary": "done" }""", isError: false);

        StatusOf(runtime, Unit).Should().Be("validated");
        runtime.Outputs["analyze"]!["task"]!["summary"]!.GetValue<string>().Should().Be("done");
        runtime.State["analysis"]!["summary"]!.GetValue<string>().Should().Be("done");
    }

    [Fact]
    public void BlockingAnswer_ValidatesFromToolResult()
    {
        var runtime = RuntimeAtAnalyze();
        runtime.RegisterSpawn("tc_agent", Unit);

        // A non-receipt tool result is a blocking answer (P3 behaviour) — validate immediately.
        runtime.ObserveSpawnResult("tc_agent", """{ "summary": "blocking" }""", isError: false);

        StatusOf(runtime, Unit).Should().Be("validated");
        runtime.Outputs["analyze"]!["task"]!["summary"]!.GetValue<string>().Should().Be("blocking");
    }

    [Fact]
    public void InjectedResult_UnknownAgentId_IsSurfacedAsUnmatched()
    {
        var runtime = RuntimeAtAnalyze();

        runtime.ObserveInjectedResult("never_correlated", """{ "summary": "x" }""", isError: false);

        runtime
            .GetProjection(null)["unmatched"]!.AsArray()
            .Select(n => n!.GetValue<string>())
            .Should()
            .Contain("never_correlated");
    }

    [Fact]
    public void InjectedError_MarksTaskFailed()
    {
        var runtime = RuntimeAtAnalyze();
        runtime.RegisterSpawn("tc_agent", Unit);
        runtime.ObserveSpawnResult("tc_agent", Receipt("agentX"), isError: false);

        runtime.ObserveInjectedResult("agentX", "the sub-agent failed", isError: true);

        StatusOf(runtime, Unit).Should().Be("failed");
        runtime.Outputs["analyze"]!["task"]!["_error"].Should().NotBeNull();
    }

    [Fact]
    public void BackgroundInFlight_OrphanResetsOnResume_WithoutPersistedAgentId()
    {
        // Drive a background spawn to the point where the task is in_flight with a LIVE agent_id correlation
        // recorded from the receipt — that agent id lives only in the runtime's in-run correlation map and is
        // (deliberately) NOT carried in the snapshot.
        var runtime = RuntimeAtAnalyze();
        runtime.RegisterSpawn("tc_agent", Unit);
        runtime.ObserveSpawnResult("tc_agent", Receipt("agent777"), isError: false);
        StatusOf(runtime, Unit).Should().Be("in_flight");

        // Snapshot + resume into a fresh runtime. The orphan reset keys off Status == in_flight, so the
        // background task is reset to pending and re-surfaces for re-spawn even though no agent id was
        // persisted — identical to the behaviour when the inert AgentId field was still round-tripped.
        var restored = WorkflowRuntime.FromSnapshot(runtime.Snapshot());

        StatusOf(restored, Unit).Should().Be("pending");
        restored.ComposeNextExpectedAction().Should().ContainSingle(unit => unit.Name == Unit);
    }
}
