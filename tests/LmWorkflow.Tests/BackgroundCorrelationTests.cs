using AchieveAi.LmDotnetTools.LmWorkflow.Model;
using AchieveAi.LmDotnetTools.LmWorkflow.Runtime;
using FluentAssertions;
using Xunit;

namespace AchieveAi.LmDotnetTools.LmWorkflow.Tests;

/// <summary>
///     V1 <b>fails fast</b> on background (<c>run_in_background</c>) spawns: an observed background spawn
///     RECEIPT (<c>{ "status": "spawned", "agent_id": ... }</c>) cannot be completed end-to-end in V1
///     (<c>MultiTurnAgentLoop</c> does not publish injected sub-agent completions to its subscribers), so the
///     runtime terminally and non-retryably fails the task with a clear, non-sensitive reason instead of
///     deferring it until the step budget trips. The blocking <c>Agent</c> path (a non-receipt tool result)
///     still validates from the tool result.
/// </summary>
/// <remarks>
///     The injected-result correlation (<c>ObserveInjectedResult</c> + <c>_tasksByAgentId</c>) is DORMANT /
///     forward-built in V1: its unknown-id "unmatched" path is exercised here, the parser feeding it is
///     covered by <see cref="SubAgentResultParserTests"/>, and its validates/records-by-<c>agent_id</c>
///     behaviour is re-enabled in the follow-up that makes injected completions observable. That behaviour is
///     intentionally NOT exercised here: with the receipt path failing fast, <c>_tasksByAgentId</c> is never
///     populated, and no hacky internal seam is added just to keep an otherwise-unreachable test alive.
/// </remarks>
public class BackgroundCorrelationTests
{
    private const string Unit = "analyze:1:task";

    private const string UnsupportedReason =
        "background spawns (run_in_background) are not supported in V1; use a blocking spawn";

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
    public void BackgroundReceipt_FailsFast_TerminallyWithUnsupportedModeReason()
    {
        var runtime = RuntimeAtAnalyze();
        runtime.RegisterSpawn("tc_agent", Unit);

        // A background spawn receipt is unsupported in V1: it must hard-fail the task immediately rather than
        // defer (which would hang in_flight until the step budget tripped).
        runtime.ObserveSpawnResult("tc_agent", Receipt("agent777"), isError: false);

        var projection = runtime.GetProjection(null);

        // Terminally failed (NOT retried back to pending) and the node's onFailure route is surfaced.
        projection["tasks"]![Unit]!.GetValue<string>().Should().Be("failed");
        projection["onFailure"]!.GetValue<string>().Should().Be("fail");

        // The stable, non-sensitive reason lands in all three sinks: the {_error} marker, the taskErrors
        // projection, and the persisted snapshot.
        runtime.Outputs["analyze"]!["task"]!["_error"]!.GetValue<string>().Should().Be(UnsupportedReason);
        projection["taskErrors"]![Unit]!.GetValue<string>().Should().Be(UnsupportedReason);
        runtime.Snapshot().Tasks.Single(t => t.Name == Unit).LastError.Should().Be(UnsupportedReason);
    }

    [Fact]
    public void BackgroundReceipt_FailsFast_DoesNotReSurfaceForRespawn()
    {
        var runtime = RuntimeAtAnalyze();
        runtime.RegisterSpawn("tc_agent", Unit);

        runtime.ObserveSpawnResult("tc_agent", Receipt("agent777"), isError: false);

        // Non-retryable: the unit must not re-appear in nextExpectedAction (re-spawning the same unsupported
        // mode could never succeed), and its spawn correlation is cleared.
        runtime
            .GetProjection(null)["nextExpectedAction"]!.AsArray()
            .Should()
            .NotContain(n => n!["name"]!.GetValue<string>() == Unit);
        runtime.IsRegisteredSpawn("tc_agent").Should().BeFalse();
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
        // ObserveInjectedResult is dormant in V1 (the receipt path no longer seeds _tasksByAgentId), so every
        // injected result finds no mapping and is surfaced as a harmless "unmatched" diagnostic.
        var runtime = RuntimeAtAnalyze();

        runtime.ObserveInjectedResult("never_correlated", """{ "summary": "x" }""", isError: false);

        runtime
            .GetProjection(null)["unmatched"]!.AsArray()
            .Select(n => n!.GetValue<string>())
            .Should()
            .Contain("never_correlated");
    }
}
