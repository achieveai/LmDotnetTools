using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using FluentAssertions;
using Moq;
using Xunit;

namespace AchieveAi.LmDotnetTools.LmWorkflow.Tests;

/// <summary>
///     End-to-end proof of forEach fan-out through <see cref="WorkflowSession"/>: a procedural node fans a
///     single task over a 3-element array, the controller spawns all three units by their composed names, and
///     the session records each validated sub-agent result at its index in an output array, then advances to
///     the terminal once the join is satisfied.
/// </summary>
/// <remarks>
///     This drives the <b>blocking</b> Agent path (the documented fallback for the background variant). The
///     real background path relays results as injected <c>&lt;sub-agent&gt;</c> user messages, but the
///     controller loop adds those to history <i>without</i> publishing them to subscribers — so the session's
///     stream observer cannot see them deterministically. The full background correlation chain
///     (receipt → injected result) is proven loop-free in <see cref="BackgroundCorrelationTests"/>.
/// </remarks>
public class ForEachFanOutIntegrationTests
{
    [Fact]
    public async Task ForEachFanOut_RecordsAllOutputsAsIndexedArray()
    {
        // The sub-agent echoes the index carried in its task prompt, so each of the three fan-out units
        // produces a distinct, index-correlated result.
        var subAgentMock = new Mock<IStreamingAgent>();
        subAgentMock
            .Setup(a =>
                a.GenerateReplyStreamingAsync(
                    It.IsAny<IEnumerable<IMessage>>(),
                    It.IsAny<GenerateReplyOptions>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Returns<IEnumerable<IMessage>, GenerateReplyOptions, CancellationToken>(
                (messages, _, _) =>
                {
                    var task = messages.OfType<TextMessage>().LastOrDefault(m => m.Role == Role.User);
                    var match = Regex.Match(task?.Text ?? string.Empty, @"idx=(\d+)");
                    var idx = match.Success ? match.Groups[1].Value : "x";
                    return Task.FromResult(
                        ToAsyncEnumerable(
                            [
                                new TextMessage
                                {
                                    Text = $$"""{ "text": "done-{{idx}}" }""",
                                    Role = Role.Assistant,
                                },
                            ]
                        )
                    );
                }
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
            .Returns(() => Task.FromResult(ToAsyncEnumerable(NextControllerTurn(ref turn))));

        var subAgentOptions = new SubAgentOptions
        {
            Templates = new Dictionary<string, SubAgentTemplate>
            {
                ["general-purpose"] = new SubAgentTemplate
                {
                    Name = "general-purpose",
                    SystemPrompt = "You are a general-purpose fan-out agent.",
                    AgentFactory = () => subAgentMock.Object,
                },
            },
        };

        await using var handle = await WorkflowSession.StartAsync(
            objective: "Fan out over the items and finish.",
            inputs: null,
            definition: null,
            subAgentOptions: subAgentOptions,
            controllerAgent: controllerMock.Object,
            threadId: "wf-foreach-thread"
        );

        await handle.Completion.WaitAsync(TimeSpan.FromSeconds(30));

        var runtime = handle.Runtime;
        runtime.IsComplete.Should().BeTrue();

        // All three validated outputs landed in outputs[fan][task] as an indexed array.
        var array = runtime.Outputs["fan"]!["task"]!.AsArray();
        array.Should().HaveCount(3);
        array[0]!["text"]!.GetValue<string>().Should().Be("done-0");
        array[1]!["text"]!.GetValue<string>().Should().Be("done-1");
        array[2]!["text"]!.GetValue<string>().Should().Be("done-2");
    }

    private static List<IMessage> NextControllerTurn(ref int turn)
    {
        turn++;
        return turn switch
        {
            1 =>
            [
                ToolCall(
                    "SetWorkflow",
                    new JsonObject
                    {
                        ["definition"] = JsonNode.Parse(Phase4Fixtures.ForEachWorkflow("all")),
                    },
                    "tc_setwf"
                ),
            ],
            2 =>
            [
                ToolCall(
                    "SetCurrentNode",
                    new JsonObject { ["completedNodeId"] = "start", ["nextNodeId"] = "fan" },
                    "tc_route_fan"
                ),
            ],
            3 => [ToolCall("GetWorkflow", [], "tc_get")],
            4 =>
            [
                SpawnCall(0),
                SpawnCall(1),
                SpawnCall(2),
            ],
            5 =>
            [
                ToolCall(
                    "SetCurrentNode",
                    new JsonObject
                    {
                        ["completedNodeId"] = "fan",
                        ["nextNodeId"] = "done",
                        ["result"] = new JsonObject { ["ok"] = true },
                    },
                    "tc_route_done"
                ),
            ],
            _ => [new TextMessage { Text = "Workflow finished.", Role = Role.Assistant }],
        };
    }

    private static ToolCallMessage SpawnCall(int index) =>
        ToolCall(
            "Agent",
            new JsonObject
            {
                ["subagent_type"] = "general-purpose",
                ["prompt"] = $"idx={index}",
                ["name"] = $"fan:1:task:{index}",
            },
            $"tc_agent_{index}"
        );

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
