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
