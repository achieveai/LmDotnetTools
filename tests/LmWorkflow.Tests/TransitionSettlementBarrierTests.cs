using System.Diagnostics;
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
///     Pins the ordering between the two threads that race over a terminal node's result.
///
///     <para>
///         A controller loop runs a <c>SetCurrentNode</c> tool handler on its TOOL-EXECUTION thread, and
///         <see cref="WorkflowRuntime.AdvanceTo"/> deep-clones the terminal node's <c>resultTemplate</c> from
///         live state right there. Sub-agent answers, meanwhile, are applied on the out-of-band OBSERVER
///         thread. Nothing in the loop orders the two, so a fan-out unit whose answer was already delivered
///         could still be missing from the captured result — state converged, the RESULT was short. That is
///         the intermittent failure this barrier removes, and every test here fails deterministically (not
///         statistically) if <c>WaitForNodeSettlementAsync</c> stops doing its job.
///     </para>
/// </summary>
public class TransitionSettlementBarrierTests
{
    // The bounded backstop inside WorkflowRuntime. Mirrored here so the liveness test can be read without
    // cross-referencing the implementation; if the two ever diverge the liveness test's bounds catch it.
    private static readonly TimeSpan BarrierBudget = TimeSpan.FromMilliseconds(2000);

    [Fact]
    public async Task Transition_WaitsForAnInFlightUnitsWrite_SoTheTerminalResultIsComplete()
    {
        // Two fan-out units are spawned; unit 0 answers immediately, unit 1's answer lands 250ms into the
        // transition — i.e. AFTER the controller has already issued SetCurrentNode. Without the barrier the
        // handler composes {"results": ["alpha-done"]} and moves on; unit 1's write then lands in state that
        // no longer feeds anything. The barrier makes the transition wait for the answer that is owed.
        var runtime = RuntimeAtFan();
        SpawnUnit(runtime, 0);
        SpawnUnit(runtime, 1);
        AnswerUnit(runtime, 0, "alpha-done");

        var lateAnswer = Task.Run(async () =>
        {
            await Task.Delay(250);
            AnswerUnit(runtime, 1, "beta-done");
        });

        var elapsed = Stopwatch.StartNew();
        var response = await RouteToDone(runtime, "tc_route_done");
        elapsed.Stop();
        await lateAnswer;

        runtime.IsComplete.Should().BeTrue();
        response.Should().NotBeNull();

        var results = runtime.Result!["results"]!.AsArray();
        results.Should().HaveCount(2, "the transition must not capture a result that is missing an answer it was already owed");
        results.Select(r => r!.GetValue<string>()).Should().BeEquivalentTo(["alpha-done", "beta-done"]);

        elapsed
            .Elapsed.Should()
            .BeGreaterThan(
                TimeSpan.FromMilliseconds(150),
                "the transition is expected to have BLOCKED on the outstanding answer, not to have won a race"
            );
    }

    [Fact]
    public async Task Transition_DoesNotWaitForAUnitTheControllerNeverSpawned()
    {
        // A `pending` unit is ambiguous from the tool thread: it is either "spawned, answer still queued" or
        // "never spawned at all". The drain marker resolves it — the loop PUBLISHES a tool call before it
        // EXECUTES it, and a subscriber channel is drained FIFO by a single reader, so once the observer has
        // reached this very SetCurrentNode call, every earlier Agent RESULT has already been applied. A unit
        // still pending at that point was never spawned, and holding the transition for it would stall the
        // workflow for the whole budget on a perfectly normal partial fan-out.
        var runtime = RuntimeAtFan();
        SpawnUnit(runtime, 0);
        AnswerUnit(runtime, 0, "alpha-done");

        // Unit 1 is composed but never spawned. The observer reaches the transition call first (this is the
        // normal ordering: published before executed).
        runtime.ObserveMessage(RouteCall("tc_route_done"));

        var elapsed = Stopwatch.StartNew();
        _ = await RouteToDone(runtime, "tc_route_done");
        elapsed.Stop();

        runtime.IsComplete.Should().BeTrue();
        runtime.Result!["results"]!.AsArray().Should().HaveCount(1);
        elapsed
            .Elapsed.Should()
            .BeLessThan(
                BarrierBudget / 2,
                "a never-spawned unit past the drain marker must release the transition immediately"
            );
    }

    [Fact]
    public async Task Transition_ProceedsAfterTheBudget_WhenAnAnswerNeverArrives()
    {
        // Liveness: a spawn whose result is dropped (crashed sub-agent, severed observer) must not wedge the
        // workflow. The barrier is a bounded best-effort — it degrades to exactly today's behaviour, late,
        // with a warning, rather than hanging.
        var runtime = RuntimeAtFan();
        SpawnUnit(runtime, 0);
        AnswerUnit(runtime, 0, "alpha-done");
        SpawnUnit(runtime, 1); // in-flight forever

        var elapsed = Stopwatch.StartNew();
        _ = await RouteToDone(runtime, "tc_route_done");
        elapsed.Stop();

        runtime.IsComplete.Should().BeTrue("the transition must complete even when an answer is never delivered");
        elapsed.Elapsed.Should().BeGreaterThan(BarrierBudget - TimeSpan.FromMilliseconds(500));
        elapsed.Elapsed.Should().BeLessThan(BarrierBudget + TimeSpan.FromSeconds(8));
    }

    [Fact]
    public async Task Transition_FromANodeWithNoUnits_CostsNothing()
    {
        // The fast path that keeps every observer-less caller (WorkflowHardeningTests drives the handler
        // directly, with nothing publishing drain markers) free of the barrier entirely.
        var runtime = new WorkflowRuntime();
        runtime.LoadDefinition(WorkflowJson.Deserialize(Definition));

        var elapsed = Stopwatch.StartNew();
        _ = await Invoke(
            Tool(runtime, WorkflowToolProvider.SetCurrentNodeToolName),
            new JsonObject { ["completedNodeId"] = "start", ["nextNodeId"] = "fan" }.ToJsonString(),
            "tc_route_fan"
        );
        elapsed.Stop();

        runtime.CurrentNodeId.Should().Be("fan");
        elapsed.Elapsed.Should().BeLessThan(TimeSpan.FromMilliseconds(500));
    }

    // --- harness ---

    private static WorkflowRuntime RuntimeAtFan()
    {
        var runtime = new WorkflowRuntime();
        runtime.LoadDefinition(WorkflowJson.Deserialize(Definition));
        runtime.AdvanceTo("start", "fan", null);

        // What the controller's projection poll does: compose the node's units so they can be spawned by name.
        runtime.ComposeNextExpectedAction().Should().HaveCount(2);
        return runtime;
    }

    private static void SpawnUnit(WorkflowRuntime runtime, int index) =>
        runtime.ObserveMessage(
            new ToolCallMessage
            {
                FunctionName = "Agent",
                FunctionArgs = new JsonObject
                {
                    ["subagent_type"] = "general-purpose",
                    ["prompt"] = $"idx={index}",
                    ["name"] = $"fan:1:task:{index}",
                }.ToJsonString(),
                ToolCallId = $"tc_agent_{index}",
                Role = Role.Assistant,
            }
        );

    private static void AnswerUnit(WorkflowRuntime runtime, int index, string text) =>
        runtime.ObserveMessage(
            new ToolCallResultMessage
            {
                ToolCallId = $"tc_agent_{index}",
                Result = new JsonObject { ["text"] = text }.ToJsonString(),
                ToolName = "Agent",
                IsError = false,
                Role = Role.User,
            }
        );

    private static ToolCallMessage RouteCall(string toolCallId) =>
        new()
        {
            FunctionName = WorkflowToolProvider.SetCurrentNodeToolName,
            FunctionArgs = new JsonObject
            {
                ["completedNodeId"] = "fan",
                ["nextNodeId"] = "done",
            }.ToJsonString(),
            ToolCallId = toolCallId,
            Role = Role.Assistant,
        };

    private static Task<ToolHandlerResult.Resolved> RouteToDone(WorkflowRuntime runtime, string toolCallId) =>
        Invoke(
            Tool(runtime, WorkflowToolProvider.SetCurrentNodeToolName),
            new JsonObject { ["completedNodeId"] = "fan", ["nextNodeId"] = "done" }.ToJsonString(),
            toolCallId
        );

    private static FunctionDescriptor Tool(WorkflowRuntime runtime, string name) =>
        new WorkflowToolProvider(runtime).GetFunctions().Single(f => f.Contract.Name == name);

    private static async Task<ToolHandlerResult.Resolved> Invoke(
        FunctionDescriptor tool,
        string argsJson,
        string toolCallId
    ) =>
        (ToolHandlerResult.Resolved)
            await tool.Handler(
                argsJson,
                new ToolCallContext { ToolCallId = toolCallId },
                CancellationToken.None
            );

    // start → fan (forEach over 2 items, each appending to state.results) → done, whose resultTemplate reads
    // that appended array. Deliberately no finalOutputSchema: a short result must surface as a SHORT ARRAY,
    // not as a validation exception, so a regression reads as "found 1, expected 2" at the assertion.
    private const string Definition = """
        {
          "schemaVersion": 1,
          "objective": "Fan out and finish.",
          "inputs": { "items": ["alpha", "beta"] },
          "state": {},
          "maxStepBudget": 50,
          "nodes": [
            { "id": "start", "type": "start", "title": "Start", "next": ["fan"] },
            {
              "id": "fan",
              "type": "procedural",
              "title": "Fan out",
              "tasksMode": "authored",
              "joinPolicy": { "mode": "all" },
              "taskList": [
                {
                  "id": "task",
                  "delegate": "agent",
                  "subagent_type": "general-purpose",
                  "forEach": "inputs.items",
                  "promptTemplate": "Process {{item}} at {{index}}.",
                  "outputSchema": {
                    "type": "object",
                    "required": ["text"],
                    "properties": { "text": { "type": "string" } }
                  },
                  "writes": { "from": "text", "to": "state.results", "mode": "append" }
                }
              ],
              "next": ["done"]
            },
            {
              "id": "done",
              "type": "terminal",
              "title": "Done",
              "resultTemplate": { "results": "{{state.results}}" }
            }
          ]
        }
        """;
}
