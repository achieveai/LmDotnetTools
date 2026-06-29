using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using AchieveAi.LmDotnetTools.LmWorkflow.Persistence;
using AchieveAi.LmDotnetTools.LmWorkflow.Runtime;
using FluentAssertions;
using Moq;
using Xunit;

namespace AchieveAi.LmDotnetTools.LmWorkflow.Tests;

/// <summary>
///     End-to-end proof of single-root persistence + resume: a scripted controller drives a linear workflow
///     PART WAY (records the first node's output, then pauses without reaching the terminal). The
///     <see cref="InMemoryWorkflowStore"/> snapshot is asserted to reflect the recorded output + active node,
///     then <see cref="WorkflowSession.ResumeAsync"/> rebuilds a fresh runtime, recovers the controller's
///     conversation from the <see cref="InMemoryConversationStore"/>, and drives the workflow to completion.
/// </summary>
public class ResumeBehavioralTests
{
    private const string AnalyzeUnit = "analyze:1:task";
    private const string ThreadId = "wf-resume-thread";
    private const string InstanceId = "wf-resume-1";

    [Fact]
    public async Task StartPersistsMidRun_ThenResumeRestoresStateAndCompletes()
    {
        var workflowStore = new InMemoryWorkflowStore();
        var conversationStore = new InMemoryConversationStore();
        var subAgentOptions = BuildSubAgentOptions();

        // --- Phase 1: start, record the analyze output, then PAUSE before the terminal. ---
        var pausingController = ScriptedController(PauseAfterAnalyze);
        await using (
            var handle = await WorkflowSession.StartAsync(
                objective: "Analyze the topic and finish.",
                inputs: null,
                definition: null,
                subAgentOptions: subAgentOptions,
                controllerAgent: pausingController.Object,
                threadId: ThreadId,
                store: workflowStore,
                instanceId: InstanceId,
                conversationStore: conversationStore
            )
        )
        {
            await handle.Completion.WaitAsync(TimeSpan.FromSeconds(30));

            // The run paused mid-flow: the output is recorded but the workflow is NOT complete.
            handle.Runtime.IsComplete.Should().BeFalse();
            handle.Runtime.CurrentNodeId.Should().Be("analyze");

            // The persisted snapshot reflects the recorded output + advanced node.
            var persisted = await workflowStore.LoadAsync(InstanceId);
            persisted.Should().NotBeNull();
            persisted!.CurrentNodeId.Should().Be("analyze");
            persisted.IsComplete.Should().BeFalse();
            persisted.Outputs["analyze"]!["task"]!["summary"]!.GetValue<string>()
                .Should()
                .Be("analyzed-by-subagent");
            persisted.State["analysis"]!["summary"]!.GetValue<string>().Should().Be("analyzed-by-subagent");
            persisted.Visits["analyze"].Should().Be(1);
            persisted.Tasks.Single(t => t.Name == AnalyzeUnit).Status
                .Should()
                .Be(WorkflowTaskStatus.Validated);

            // The controller conversation was persisted under the thread id.
            conversationStore.GetMessageCount(ThreadId).Should().BeGreaterThan(0);
        }

        var messagesAfterPause = conversationStore.GetMessageCount(ThreadId);

        // --- Phase 2: resume into a FRESH runtime + loop and drive to completion. ---
        var resumingController = ScriptedController(ResumeToTerminal);
        await using var resumed = await WorkflowSession.ResumeAsync(
            instanceId: InstanceId,
            store: workflowStore,
            subAgentOptions: subAgentOptions,
            controllerAgent: resumingController.Object,
            threadId: ThreadId,
            conversationStore: conversationStore
        );

        await resumed.Completion.WaitAsync(TimeSpan.FromSeconds(30));

        // The restored runtime carried over the active node + recorded channels, then completed.
        resumed.Runtime.IsComplete.Should().BeTrue();
        resumed.Runtime.CurrentNodeId.Should().Be("done");
        resumed.Result!["summary"]!.GetValue<string>().Should().Be("final-result");
        resumed.Runtime.Outputs["analyze"]!["task"]!["summary"]!.GetValue<string>()
            .Should()
            .Be("analyzed-by-subagent");
        resumed.Runtime.State["analysis"]!["summary"]!.GetValue<string>().Should().Be("analyzed-by-subagent");
        resumed.Runtime.Visits["analyze"].Should().Be(1);

        // RecoverAsync restored the prior controller history, and the resumed loop continued the SAME
        // thread — proven by the message count growing beyond the paused total.
        conversationStore.GetMessageCount(ThreadId).Should().BeGreaterThan(messagesAfterPause);
    }

    /// <summary>A sub-agent that always returns a valid <c>{summary}</c> blocking answer.</summary>
    private static SubAgentOptions BuildSubAgentOptions()
    {
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

        return new SubAgentOptions
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
    }

    private static Mock<IStreamingAgent> ScriptedController(Func<int, IMessage> script)
    {
        var controller = new Mock<IStreamingAgent>();
        var turn = 0;
        controller
            .Setup(a =>
                a.GenerateReplyStreamingAsync(
                    It.IsAny<IEnumerable<IMessage>>(),
                    It.IsAny<GenerateReplyOptions>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Returns(() => Task.FromResult(ToAsyncEnumerable([script(++turn)])));
        return controller;
    }

    /// <summary>Author → route to analyze → read → spawn analyze → PAUSE (plain text ends the run mid-flow).</summary>
    private static IMessage PauseAfterAnalyze(int turn) =>
        turn switch
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
                    ["name"] = AnalyzeUnit,
                },
                "tc_agent"
            ),
            _ => new TextMessage { Text = "Pausing the workflow here.", Role = Role.Assistant },
        };

    /// <summary>Read restored state → route analyze → done with the final result → end.</summary>
    private static IMessage ResumeToTerminal(int turn) =>
        turn switch
        {
            1 => ToolCall("GetWorkflow", [], "tc_get_resume"),
            2 => ToolCall(
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
