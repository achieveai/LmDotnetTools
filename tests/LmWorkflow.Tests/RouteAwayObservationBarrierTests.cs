using System.Text.Json.Nodes;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmWorkflow.Model;
using AchieveAi.LmDotnetTools.LmWorkflow.Runtime;
using AchieveAi.LmDotnetTools.LmWorkflow.Tools;
using FluentAssertions;
using Xunit;

namespace AchieveAi.LmDotnetTools.LmWorkflow.Tests;

/// <summary>
///     Pins the route-away observation barrier. A <c>SetCurrentNode</c> handler runs INLINE on the controller
///     loop's thread, whereas the sub-agent results it depends on reach the runtime only through the
///     out-of-band observer, which lags the loop by however long the subscriber channel takes to drain.
///     Routing into a terminal renders its <c>resultTemplate</c> immediately, so before the barrier a
///     terminal could be finalized from state that still lagged results the controller had already collected
///     — observed as a flaky <c>authored</c> array of 1 where the runtime's own state held 2.
/// </summary>
/// <remarks>
///     The barrier keys on the routing call's OWN tool-call id: every message is published to subscribers
///     before the loop executes it, and the observer consumes in publish order, so seeing that id proves
///     every earlier message — including every prior tool result — is already correlated. These tests drive
///     the real <see cref="WorkflowToolProvider"/> handler and interleave observations by hand, so the
///     behaviour is deterministic rather than load-dependent.
/// </remarks>
public class RouteAwayObservationBarrierTests
{
    private const string RouteToolCallId = "tc_done";

    /// <summary>The routing call must not advance until the observer has caught up to it.</summary>
    [Fact]
    public async Task Route_WaitsForTheObserverToReachTheRoutingCall_BeforeRenderingTheTerminal()
    {
        var runtime = RuntimeAtAuthor(attachObserver: true);

        // Only the FIRST author result has been observed when the controller issues its route.
        ObserveAuthored(runtime, "tc_a0", "author:1:a:0");
        runtime.State["authored"]!.AsArray().Should().HaveCount(1);

        var route = InvokeRoute(runtime, RouteToolCallId);

        // The watermark has not been observed, so the transition is still parked.
        await Task.Delay(50);
        route.IsCompleted.Should().BeFalse();

        // The second result lands while the transition waits — exactly the window that used to be lost.
        ObserveAuthored(runtime, "tc_a1", "author:1:a:1");
        route.IsCompleted.Should().BeFalse();

        // Observing the routing call itself is the watermark that releases the transition.
        runtime.ObserveMessage(RouteCall(RouteToolCallId));

        var result = await route.WaitAsync(TimeSpan.FromSeconds(5));
        result.Should().BeOfType<ToolHandlerResult.Resolved>().Which.Payload.IsError.Should().BeFalse();

        runtime.IsComplete.Should().BeTrue();
        runtime.Result!["authored"]!.AsArray().Should().HaveCount(2);
    }

    /// <summary>
    ///     A REAL provider publishes a tool call to subscribers only as streaming update fragments (the
    ///     finalized <see cref="ToolCallMessage"/> never reaches the subscriber stream), so the first
    ///     fragment carrying the routing id must serve as the watermark too — otherwise every live transition
    ///     would wait out the timeout.
    /// </summary>
    [Fact]
    public async Task Route_IsReleasedByAStreamingUpdateFragment_NotOnlyAFinalizedCall()
    {
        var runtime = RuntimeAtAuthor(attachObserver: true);
        ObserveAuthored(runtime, "tc_a0", "author:1:a:0");

        var route = InvokeRoute(runtime, RouteToolCallId);
        await Task.Delay(50);
        route.IsCompleted.Should().BeFalse();

        ObserveAuthored(runtime, "tc_a1", "author:1:a:1");
        runtime.ObserveMessage(
            new ToolCallUpdateMessage
            {
                FunctionName = WorkflowToolProvider.SetCurrentNodeToolName,
                FunctionArgs = """{ "completed""",
                ToolCallId = RouteToolCallId,
                Role = Role.Assistant,
            }
        );

        _ = await route.WaitAsync(TimeSpan.FromSeconds(5));
        runtime.Result!["authored"]!.AsArray().Should().HaveCount(2);
    }

    /// <summary>
    ///     A watermark already consumed before the handler asks for it must not park the transition: the
    ///     observer normally reaches the routing call while the loop is still dispatching its handler.
    /// </summary>
    [Fact]
    public async Task Route_DoesNotWait_WhenTheWatermarkWasAlreadyObserved()
    {
        var runtime = RuntimeAtAuthor(attachObserver: true);
        ObserveAuthored(runtime, "tc_a0", "author:1:a:0");
        ObserveAuthored(runtime, "tc_a1", "author:1:a:1");
        runtime.ObserveMessage(RouteCall(RouteToolCallId));

        _ = await InvokeRoute(runtime, RouteToolCallId).WaitAsync(TimeSpan.FromSeconds(5));

        runtime.IsComplete.Should().BeTrue();
        runtime.Result!["authored"]!.AsArray().Should().HaveCount(2);
    }

    /// <summary>
    ///     A host that drives the runtime WITHOUT an ordered observer (no <c>WorkflowSession</c>) has no
    ///     watermark to wait for; the barrier must stay a no-op there instead of stalling every transition
    ///     for the full timeout.
    /// </summary>
    [Fact]
    public async Task Route_IsANoOp_WhenNoOrderedObserverIsAttached()
    {
        var runtime = RuntimeAtAuthor(attachObserver: false);
        ObserveAuthored(runtime, "tc_a0", "author:1:a:0");

        _ = await InvokeRoute(runtime, RouteToolCallId).WaitAsync(TimeSpan.FromSeconds(5));

        runtime.IsComplete.Should().BeTrue();
    }

    /// <summary>A transition with no tool-call id of its own has nothing to wait on and must proceed.</summary>
    [Fact]
    public async Task Route_IsANoOp_WhenTheCallCarriesNoToolCallId()
    {
        var runtime = RuntimeAtAuthor(attachObserver: true);
        ObserveAuthored(runtime, "tc_a0", "author:1:a:0");

        _ = await InvokeRoute(runtime, toolCallId: null).WaitAsync(TimeSpan.FromSeconds(5));

        runtime.IsComplete.Should().BeTrue();
    }

    /// <summary>Positions a runtime on the author node with its two forEach units composed.</summary>
    private static WorkflowRuntime RuntimeAtAuthor(bool attachObserver)
    {
        var runtime = new WorkflowRuntime();
        if (attachObserver)
        {
            runtime.AttachOrderedObserver();
        }

        runtime.LoadDefinition(WorkflowJson.Deserialize(AuthorThenTerminal));
        runtime.AdvanceTo("start", "author", null);

        // Composing the node surfaces the two ready-to-spawn units the observations below correlate to.
        _ = runtime.GetProjection(null);
        return runtime;
    }

    /// <summary>Feeds one blocking author spawn and its schema-valid answer through the observer entry point.</summary>
    private static void ObserveAuthored(WorkflowRuntime runtime, string toolCallId, string unitName)
    {
        runtime.ObserveMessage(
            new ToolCallMessage
            {
                FunctionName = "Agent",
                FunctionArgs = new JsonObject
                {
                    ["subagent_type"] = "author",
                    ["prompt"] = "Author it.",
                    ["name"] = unitName,
                }.ToJsonString(),
                ToolCallId = toolCallId,
                Role = Role.Assistant,
            }
        );

        runtime.ObserveMessage(
            new ToolCallResultMessage
            {
                ToolCallId = toolCallId,
                Result = $$"""{ "solution": "{{unitName}}" }""",
                ToolName = "Agent",
                Role = Role.User,
            }
        );
    }

    /// <summary>Invokes the REAL <c>SetCurrentNode</c> handler the controller loop would call.</summary>
    private static Task<ToolHandlerResult> InvokeRoute(WorkflowRuntime runtime, string? toolCallId)
    {
        var handler = new WorkflowToolProvider(runtime)
            .GetFunctions()
            .Single(f => f.Contract.Name == WorkflowToolProvider.SetCurrentNodeToolName)
            .Handler;

        var args = new JsonObject { ["completedNodeId"] = "author", ["nextNodeId"] = "done" }.ToJsonString();

        return handler(args, new ToolCallContext { ToolCallId = toolCallId }, CancellationToken.None);
    }

    private static ToolCallMessage RouteCall(string toolCallId) =>
        new()
        {
            FunctionName = WorkflowToolProvider.SetCurrentNodeToolName,
            FunctionArgs = new JsonObject { ["completedNodeId"] = "author", ["nextNodeId"] = "done" }.ToJsonString(),
            ToolCallId = toolCallId,
            Role = Role.Assistant,
        };

    private const string AuthorThenTerminal = """
        {
          "schemaVersion": 1,
          "objective": "Author two problems and finish.",
          "inputs": { "problems": ["p0", "p1"] },
          "state": { "authored": [] },
          "maxStepBudget": 100,
          "nodes": [
            { "id": "start", "type": "start", "title": "Start", "next": ["author"] },
            {
              "id": "author",
              "type": "procedural",
              "title": "Author problems",
              "tasksMode": "authored",
              "joinPolicy": { "mode": "all" },
              "taskList": [
                {
                  "id": "a",
                  "delegate": "agent",
                  "subagent_type": "author",
                  "forEach": "inputs.problems",
                  "promptTemplate": "Author problem {{item}} idx={{index}}.",
                  "outputSchema": {
                    "type": "object",
                    "required": ["solution"],
                    "properties": { "solution": { "type": "string" } }
                  },
                  "writes": { "to": "state.authored", "mode": "append" }
                }
              ],
              "next": ["done"]
            },
            {
              "id": "done",
              "type": "terminal",
              "title": "Done",
              "resultTemplate": { "authored": "{{state.authored}}" },
              "finalOutputSchema": {
                "type": "object",
                "required": ["authored"],
                "properties": { "authored": { "type": "array" } }
              }
            }
          ]
        }
        """;
}
