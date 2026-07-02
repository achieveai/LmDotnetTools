using System.Text.Json.Nodes;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmWorkflow.Model;
using AchieveAi.LmDotnetTools.LmWorkflow.Runtime;
using FluentAssertions;
using Xunit;

namespace AchieveAi.LmDotnetTools.LmWorkflow.Tests;

/// <summary>
///     Reproduces, through the SAME public entry point the host observer uses
///     (<see cref="WorkflowRuntime.ObserveMessage"/>), the live sequence that left a successful blocking
///     sub-agent's task stuck at <c>pending</c>: the controller routed into the procedural node, then
///     issued the <c>Agent</c> tool call and its result flowed back. The result must correlate to the task
///     and validate the join — the task must become <c>validated</c>, never remain <c>pending</c>.
/// </summary>
public class ObserveMessageCorrelationTests
{
    private const string Unit = "analyze:1:task";

    // The analyze task validates against a {summary} schema; a conforming result must validate the join.
    private static WorkflowRuntime RuntimeAtAnalyze()
    {
        var runtime = new WorkflowRuntime();
        runtime.LoadDefinition(WorkflowJson.Deserialize(Phase3Fixtures.LinearBlockingAgent));
        runtime.AdvanceTo("start", "analyze", null);
        return runtime;
    }

    private static ToolCallMessage AgentCall(string toolCallId, string name) =>
        new()
        {
            FunctionName = "Agent",
            FunctionArgs = new JsonObject
            {
                ["subagent_type"] = "general-purpose",
                ["prompt"] = "do it",
                ["name"] = name,
            }.ToJsonString(),
            ToolCallId = toolCallId,
            Role = Role.Assistant,
        };

    private static ToolCallResultMessage AgentResult(string toolCallId, string result, bool isError = false) =>
        new()
        {
            ToolCallId = toolCallId,
            Result = result,
            ToolName = "Agent",
            IsError = isError,
            Role = Role.User,
        };

    // A REAL provider publishes the Agent tool call to subscribers as a sequence of streaming
    // ToolCallUpdateMessages whose FunctionArgs are INCREMENTAL FRAGMENTS (raw byte slices of the args
    // JSON), NOT the full accumulated args — verified live. The finalized ToolCallMessage is added to loop
    // history but never published to the subscriber stream. The observer must reassemble the fragments.
    private static ToolCallUpdateMessage AgentCallFragment(string toolCallId, string argsFragment) =>
        new()
        {
            FunctionName = "Agent",
            FunctionArgs = argsFragment,
            ToolCallId = toolCallId,
            Role = Role.Assistant,
        };

    // Splits a complete args JSON into the kind of ragged fragments a provider streams (mid-token, mid-key),
    // so the test proves reassembly — not a lucky per-fragment parse.
    private static IEnumerable<ToolCallUpdateMessage> AgentCallStream(string toolCallId, string name)
    {
        var full = new JsonObject
        {
            ["subagent_type"] = "general-purpose",
            ["prompt"] = "do it",
            ["name"] = name,
        }.ToJsonString();

        // Chunk into 7-char slices to mimic the observed mid-token fragmentation.
        for (var i = 0; i < full.Length; i += 7)
        {
            yield return AgentCallFragment(toolCallId, full.Substring(i, Math.Min(7, full.Length - i)));
        }
    }

    private static string StatusOf(WorkflowRuntime runtime) =>
        runtime.GetProjection(null)["tasks"]![Unit]!.GetValue<string>();

    /// <summary>
    ///     The REAL live path: the Agent call reaches the subscriber stream ONLY as streaming
    ///     <see cref="ToolCallUpdateMessage"/> fragments (each a raw slice of the args JSON), followed by the
    ///     finalized <see cref="ToolCallResultMessage"/>. The observer must reassemble the fragments, read the
    ///     spawn name, correlate the result, and validate the task. This is the exact case the live workspace
    ///     run failed — the task stayed <c>pending</c> because no single fragment was parseable.
    /// </summary>
    [Fact]
    public void ObserveMessage_StreamingFragmentedAgentCall_ThenResult_ValidatesTheTask()
    {
        var runtime = RuntimeAtAnalyze();

        foreach (var fragment in AgentCallStream("tc_agent", Unit))
        {
            runtime.ObserveMessage(fragment);
        }

        runtime.ObserveMessage(AgentResult("tc_agent", """{ "summary": "done" }"""));

        StatusOf(runtime)
            .Should()
            .Be("validated", because: "the observer must reassemble the streamed args fragments to read the spawn name");
    }

    /// <summary>
    ///     The exact live ordering: the controller surfaced the unit (GetWorkflow/projection composes it),
    ///     then the Agent call and its blocking result are OBSERVED. The task must end validated.
    /// </summary>
    [Fact]
    public void ObserveMessage_AgentCallThenResult_AfterProjection_ValidatesTheTask()
    {
        var runtime = RuntimeAtAnalyze();

        // The controller reads the node (this composes + registers the unit into the runtime's task map).
        _ = runtime.GetProjection(null);

        runtime.ObserveMessage(AgentCall("tc_agent", Unit));
        runtime.ObserveMessage(AgentResult("tc_agent", """{ "summary": "done" }"""));

        StatusOf(runtime).Should().Be("validated", because: "an observed blocking result must correlate to the task");
    }

    /// <summary>
    ///     The failure the live run actually hit: the controller issued the Agent call WITHOUT first calling
    ///     GetWorkflow, so no projection had composed the unit when the call was observed. The correlation
    ///     must still succeed — the runtime already knows the active node's authored tasks, so it must be
    ///     able to register the spawn regardless of whether the controller polled the projection first.
    /// </summary>
    [Fact]
    public void ObserveMessage_AgentCallThenResult_WithoutPriorProjection_StillValidatesTheTask()
    {
        var runtime = RuntimeAtAnalyze();

        // NOTE: no GetProjection / ComposeNextExpectedAction call here — mirror the live pill order
        // SetCurrentNode -> Agent -> GetWorkflow, where the Agent call is observed before any GetWorkflow.
        runtime.ObserveMessage(AgentCall("tc_agent", Unit));
        runtime.ObserveMessage(AgentResult("tc_agent", """{ "summary": "done" }"""));

        StatusOf(runtime).Should().Be("validated", because: "correlation must not depend on the controller polling GetWorkflow first");
    }

    /// <summary>
    ///     A non-workflow Agent call (a name that matches no authored unit — the common case in a
    ///     Workspace Agent conversation) must be handled gracefully: its streamed fragments and its result
    ///     do not correlate, do not throw, and — critically — do not contaminate a subsequent REAL workflow
    ///     spawn. This exercises the hardened buffer lifecycle (drop-on-parse / clear-on-any-result).
    /// </summary>
    [Fact]
    public void ObserveMessage_NonWorkflowAgentCall_IsHarmless_AndDoesNotBreakLaterCorrelation()
    {
        var runtime = RuntimeAtAnalyze();

        // An Agent spawn whose name is NOT an authored unit of the active node.
        foreach (var fragment in AgentCallStream("tc_other", "some:other:spawn"))
        {
            runtime.ObserveMessage(fragment);
        }

        runtime.ObserveMessage(AgentResult("tc_other", "arbitrary non-workflow output"));

        // The real workflow unit is untouched by the unrelated spawn.
        StatusOf(runtime).Should().Be("pending");

        // A genuine spawn for the authored unit still correlates and validates afterward.
        foreach (var fragment in AgentCallStream("tc_agent", Unit))
        {
            runtime.ObserveMessage(fragment);
        }

        runtime.ObserveMessage(AgentResult("tc_agent", """{ "summary": "done" }"""));

        StatusOf(runtime).Should().Be("validated");
    }
}
