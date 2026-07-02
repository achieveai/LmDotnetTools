using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using FluentAssertions;
using Moq;
using Xunit;

namespace AchieveAi.LmDotnetTools.LmWorkflow.Tests;

/// <summary>
///     End-to-end proof of the Phase 3 slice: a scripted controller authors a linear workflow and drives it
///     over <see cref="WorkflowSession"/>, the runtime composes a blocking sub-agent spawn, and the session
///     observes/validates/records the sub-agent result before the workflow completes into its terminal.
/// </summary>
public class WorkflowEndToEndTests
{
    [Fact]
    public async Task LinearBlockingAgent_RecordsValidatedOutput_AndCompletes()
    {
        // The sub-agent always returns a valid {summary} JSON object as its final answer; the blocking
        // Agent spawn surfaces that text verbatim as the tool result the session observes.
        var subAgentMock = new Mock<IStreamingAgent>();
        subAgentMock
            .Setup(a =>
                a.GenerateReplyStreamingAsync(
                    It.IsAny<IEnumerable<IMessage>>(),
                    It.IsAny<GenerateReplyOptions>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Returns(
                Task.FromResult(
                    ToAsyncEnumerable(
                        [
                            new TextMessage
                            {
                                Text = """{ "summary": "analyzed-by-subagent" }""",
                                Role = Role.Assistant,
                            },
                        ]
                    )
                )
            );

        // The controller scripts the full drive: author → route to analyze → read → spawn → finalize → done.
        var controllerMock = new Mock<IStreamingAgent>();
        var turn = 0;
        controllerMock
            .Setup(a =>
                a.GenerateReplyStreamingAsync(
                    It.IsAny<IEnumerable<IMessage>>(),
                    It.IsAny<GenerateReplyOptions>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Returns(() => Task.FromResult(ToAsyncEnumerable([NextControllerMessage(ref turn)])));

        var subAgentOptions = new SubAgentOptions
        {
            Templates = new Dictionary<string, SubAgentTemplate>
            {
                ["general-purpose"] = new SubAgentTemplate
                {
                    Name = "general-purpose",
                    SystemPrompt = "You are a general-purpose analysis agent.",
                    AgentFactory = () => subAgentMock.Object,
                },
            },
        };

        await using var handle = await WorkflowSession.StartAsync(
            objective: "Analyze the topic and finish.",
            inputs: null,
            definition: null,
            subAgentOptions: subAgentOptions,
            controllerAgent: controllerMock.Object,
            threadId: "wf-e2e-thread"
        );

        // Deterministic wait: Completion is signalled only after the observer drains the full run stream,
        // so by the time it resolves every sub-agent result has already been recorded.
        await handle.Completion.WaitAsync(TimeSpan.FromSeconds(30));

        var runtime = handle.Runtime;
        runtime.IsComplete.Should().BeTrue();

        // The validated sub-agent output is recorded under outputs[analyze][task].
        runtime.Outputs["analyze"]!["task"]!["summary"]!.GetValue<string>()
            .Should()
            .Be("analyzed-by-subagent");

        // The task's write landed in state.analysis.
        runtime.State["analysis"]!["summary"]!.GetValue<string>().Should().Be("analyzed-by-subagent");

        // The terminal result was captured and validated (distinct from the sub-agent output).
        handle.Result.Should().NotBeNull();
        handle.Result!["summary"]!.GetValue<string>().Should().Be("final-result");

        // The prompt the runtime composed for the spawn included the shared context and the schema
        // directive, substituted the {{inputs.topic}} binding, and excluded controllerInstructions.
        var composedPrompt = runtime.ComposedUnits["analyze:1:task"].Prompt;
        composedPrompt.Should().Contain(Phase3Fixtures.SharedContextMarker);
        composedPrompt.Should().Contain("Analyze the topic widgets.");
        composedPrompt.Should().Contain(Phase3Fixtures.SchemaDirective);
        composedPrompt.Should().NotContain(Phase3Fixtures.ControllerInstructionsMarker);
    }

    /// <summary>
    ///     The "runs to completion even when a sub-agent fails" property that the live hang exposed as
    ///     untested at the session level: a blocking sub-agent returns schema-INVALID output, so the runtime
    ///     terminally fails the task (maxValidationRetries=0) and surfaces the node's onFailure route; the
    ///     controller follows it into the failure terminal and the workflow still reaches completion. A
    ///     workflow must not deadlock just because a delegated agent failed.
    /// </summary>
    [Fact]
    public async Task BlockingAgentFailsSchemaValidation_RoutesOnFailure_AndStillCompletes()
    {
        // The sub-agent returns a well-formed JSON object that does NOT match the {summary} schema, so
        // validation fails and — with maxValidationRetries=0 — the task is terminally failed.
        var subAgentMock = new Mock<IStreamingAgent>();
        subAgentMock
            .Setup(a =>
                a.GenerateReplyStreamingAsync(
                    It.IsAny<IEnumerable<IMessage>>(),
                    It.IsAny<GenerateReplyOptions>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Returns(
                Task.FromResult(
                    ToAsyncEnumerable(
                        [
                            new TextMessage
                            {
                                Text = """{ "not_summary": "schema-miss" }""",
                                Role = Role.Assistant,
                            },
                        ]
                    )
                )
            );

        var controllerMock = new Mock<IStreamingAgent>();
        var turn = 0;
        controllerMock
            .Setup(a =>
                a.GenerateReplyStreamingAsync(
                    It.IsAny<IEnumerable<IMessage>>(),
                    It.IsAny<GenerateReplyOptions>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Returns(() => Task.FromResult(ToAsyncEnumerable([NextFailureDriveMessage(ref turn)])));

        var subAgentOptions = new SubAgentOptions
        {
            Templates = new Dictionary<string, SubAgentTemplate>
            {
                ["general-purpose"] = new SubAgentTemplate
                {
                    Name = "general-purpose",
                    SystemPrompt = "You are a general-purpose analysis agent.",
                    AgentFactory = () => subAgentMock.Object,
                },
            },
        };

        await using var handle = await WorkflowSession.StartAsync(
            objective: "Analyze the topic and finish.",
            inputs: null,
            definition: null,
            subAgentOptions: subAgentOptions,
            controllerAgent: controllerMock.Object,
            threadId: "wf-e2e-fail-thread"
        );

        await handle.Completion.WaitAsync(TimeSpan.FromSeconds(30));

        var runtime = handle.Runtime;

        // The workflow reached its FAILURE terminal — it did not deadlock on the failed task.
        runtime.IsComplete.Should().BeTrue();
        runtime.CurrentNodeId.Should().Be("fail");

        // The task is recorded as failed (not validated, not stuck pending).
        runtime.GetProjection(null)["tasks"]!["analyze:1:task"]!.GetValue<string>()
            .Should()
            .Be("failed");
    }

    /// <summary>
    ///     Produces the controller's turn-by-turn drive for the failure path: author → route to analyze →
    ///     read → spawn (which fails validation) → route the surfaced onFailure into the failure terminal.
    /// </summary>
    private static IMessage NextFailureDriveMessage(ref int turn)
    {
        turn++;
        return turn switch
        {
            1 => ToolCall(
                "SetWorkflow",
                new JsonObject { ["definition"] = JsonNode.Parse(OnFailureWorkflow) },
                "tc_setwf"
            ),
            2 => ToolCall(
                "SetCurrentNode",
                new JsonObject { ["completedNodeId"] = "start", ["nextNodeId"] = "analyze" },
                "tc_route_analyze"
            ),
            3 => ToolCall("GetWorkflow", [], "tc_get"),
            4 => ToolCall(
                "Agent",
                new JsonObject
                {
                    ["subagent_type"] = "general-purpose",
                    ["prompt"] = "Spawn the analysis task.",
                    ["name"] = "analyze:1:task",
                },
                "tc_agent"
            ),
            5 => ToolCall(
                "SetCurrentNode",
                new JsonObject { ["completedNodeId"] = "analyze", ["nextNodeId"] = "fail" },
                "tc_route_fail"
            ),
            _ => new TextMessage { Text = "Workflow failed and finished.", Role = Role.Assistant },
        };
    }

    /// <summary>
    ///     A single-task workflow whose procedural node declares an onFailure terminal: the task validates
    ///     against a {summary} schema with maxValidationRetries=0, so a schema-miss fails it immediately.
    /// </summary>
    private const string OnFailureWorkflow = """
        {
          "schemaVersion": 1,
          "objective": "Analyze the topic and finish.",
          "state": {},
          "maxStepBudget": 50,
          "nodes": [
            { "id": "start", "type": "start", "title": "Start", "next": ["analyze"] },
            {
              "id": "analyze",
              "type": "procedural",
              "title": "Analyze",
              "tasksMode": "authored",
              "joinPolicy": { "mode": "all" },
              "onFailure": "fail",
              "taskList": [
                {
                  "id": "task",
                  "delegate": "agent",
                  "subagent_type": "general-purpose",
                  "promptTemplate": "Analyze the topic.",
                  "outputSchema": {
                    "type": "object",
                    "required": ["summary"],
                    "properties": { "summary": { "type": "string" } }
                  },
                  "onFailure": "fail",
                  "maxValidationRetries": 0
                }
              ],
              "next": ["done"]
            },
            { "id": "done", "type": "terminal", "title": "Done" },
            { "id": "fail", "type": "terminal", "title": "Failed" }
          ]
        }
        """;

    /// <summary>
    ///     Produces the controller's tool call for the current turn (incremented per provider call). Each
    ///     turn issues exactly one tool call until the final plain-text turn ends the run.
    /// </summary>
    private static IMessage NextControllerMessage(ref int turn)
    {
        turn++;
        return turn switch
        {
            1 => ToolCall(
                "SetWorkflow",
                new JsonObject { ["definition"] = JsonNode.Parse(Phase3Fixtures.LinearBlockingAgent) },
                "tc_setwf"
            ),
            2 => ToolCall(
                "SetCurrentNode",
                new JsonObject { ["completedNodeId"] = "start", ["nextNodeId"] = "analyze" },
                "tc_route_analyze"
            ),
            3 => ToolCall("GetWorkflow", [], "tc_get"),
            4 => ToolCall(
                "Agent",
                new JsonObject
                {
                    ["subagent_type"] = "general-purpose",
                    ["prompt"] = "Spawn the analysis task.",
                    ["name"] = "analyze:1:task",
                },
                "tc_agent"
            ),
            5 => ToolCall(
                "SetCurrentNode",
                new JsonObject
                {
                    ["completedNodeId"] = "analyze",
                    ["nextNodeId"] = "done",
                    ["result"] = new JsonObject { ["summary"] = "final-result" },
                },
                "tc_route_done"
            ),
            _ => new TextMessage { Text = "Workflow finished.", Role = Role.Assistant },
        };
    }

    private static ToolCallMessage ToolCall(string functionName, JsonObject args, string toolCallId) =>
        new()
        {
            FunctionName = functionName,
            FunctionArgs = args.ToJsonString(),
            ToolCallId = toolCallId,
            Role = Role.Assistant,
        };

    private static async IAsyncEnumerable<IMessage> ToAsyncEnumerable(
        List<IMessage> messages,
        [EnumeratorCancellation] CancellationToken ct = default
    )
    {
        foreach (var message in messages)
        {
            ct.ThrowIfCancellationRequested();
            yield return message;
            await Task.Yield();
        }
    }
}
