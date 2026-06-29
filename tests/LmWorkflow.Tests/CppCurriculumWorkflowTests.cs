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
///     End-to-end proof of the Phase 4b loop through <see cref="WorkflowSession"/>: a C++ curriculum
///     workflow <c>start → research(forEach) → synthesize → gate(conditional, maxVisits) → author(forEach)
///     → done(resultTemplate)</c> loops back through the gate once (the first synthesize reports too few
///     problems, the second enough), accumulates <c>state.research</c>/<c>state.authored</c> across the
///     loop, and completes by composing the final result from the terminal's <c>resultTemplate</c>.
/// </summary>
/// <remarks>
///     This drives the documented <b>blocking</b> Agent path (background fan-out e2e is a known limitation).
///     The controller <see cref="Mock{IStreamingAgent}"/> is scripted turn by turn; the gate loop is driven
///     by the synthesizer sub-agent returning <c>problemCount = 1</c> on its first call and <c>2</c> on its
///     second, which is exactly what a real controller would route on via the runtime's
///     <c>recommendedBranch</c> surface.
/// </remarks>
public class CppCurriculumWorkflowTests
{
    private const int ProblemThreshold = 2;

    [Fact]
    public async Task LoopsThroughGate_AccumulatesState_CompletesViaResultTemplate()
    {
        // Researcher/author sub-agents return fixed schema-valid JSON; the synthesizer returns too few
        // problems on its first call (loop back) and enough on its second (proceed to author).
        var researcher = StaticSubAgent("""{ "topic": "t" }""");
        var author = StaticSubAgent("""{ "solution": "sol" }""");

        var synthCalls = 0;
        var synthesizer = new Mock<IStreamingAgent>();
        synthesizer
            .Setup(a =>
                a.GenerateReplyStreamingAsync(
                    It.IsAny<IEnumerable<IMessage>>(),
                    It.IsAny<GenerateReplyOptions>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Returns(() =>
            {
                synthCalls++;
                var json = synthCalls < ProblemThreshold
                    ? """{ "problemCount": 1, "problems": [ { "id": "p0" } ] }"""
                    : """{ "problemCount": 2, "problems": [ { "id": "p0" }, { "id": "p1" } ] }""";
                return Task.FromResult(
                    ToAsyncEnumerable([new TextMessage { Text = json, Role = Role.Assistant }])
                );
            });

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
                ["researcher"] = Template("researcher", researcher),
                ["synthesizer"] = Template("synthesizer", synthesizer),
                ["author"] = Template("author", author),
            },
        };

        await using var handle = await WorkflowSession.StartAsync(
            objective: "Build a C++ curriculum and finish.",
            inputs: null,
            definition: null,
            subAgentOptions: subAgentOptions,
            controllerAgent: controllerMock.Object,
            threadId: "wf-cpp-thread"
        );

        await handle.Completion.WaitAsync(TimeSpan.FromSeconds(30));

        var runtime = handle.Runtime;
        runtime.IsComplete.Should().BeTrue();

        // The gate was entered at least twice -> the workflow looped back through it at least once.
        runtime.Visits["gate"].Should().BeGreaterThanOrEqualTo(2);

        // state.research accumulated across the loop (two clusters x two research passes), and
        // state.authored accumulated one entry per curriculum problem.
        runtime.State["research"]!.AsArray().Should().HaveCount(4);
        runtime.State["authored"]!.AsArray().Should().HaveCount(2);

        // The final result was composed from the terminal's resultTemplate and validated.
        var result = handle.Result.Should().NotBeNull().And.BeAssignableTo<JsonNode>().Subject;
        result!["curriculum"]!["problemCount"]!.GetValue<int>().Should().Be(2);
        result["authored"]!.AsArray().Should().HaveCount(2);
    }

    /// <summary>
    ///     The scripted controller drive: author the workflow, run the research/synthesize/gate loop once
    ///     more than the threshold demands (loop back, then proceed), author the problems, and finalize via
    ///     the resultTemplate (no explicit result).
    /// </summary>
    private static List<IMessage> NextControllerTurn(ref int turn)
    {
        turn++;
        return turn switch
        {
            1 =>
            [
                ToolCall(
                    "SetWorkflow",
                    new JsonObject { ["definition"] = JsonNode.Parse(CppCurriculum) },
                    "tc_setwf"
                ),
            ],
            2 => [Route("start", "research", "tc_r0")],
            3 => [ToolCall("GetWorkflow", [], "tc_get1")],
            4 =>
            [
                Spawn("researcher", "research:1:r:0", "tc_res1_0"),
                Spawn("researcher", "research:1:r:1", "tc_res1_1"),
            ],
            5 => [Route("research", "synthesize", "tc_syn1")],
            6 => [ToolCall("GetWorkflow", [], "tc_get2")],
            7 => [Spawn("synthesizer", "synthesize:1:s", "tc_syncall1")],
            8 => [Route("synthesize", "gate", "tc_gate1")],
            9 => [Route("gate", "research", "tc_loopback")],
            10 => [ToolCall("GetWorkflow", [], "tc_get3")],
            11 =>
            [
                Spawn("researcher", "research:2:r:0", "tc_res2_0"),
                Spawn("researcher", "research:2:r:1", "tc_res2_1"),
            ],
            12 => [Route("research", "synthesize", "tc_syn2")],
            13 => [ToolCall("GetWorkflow", [], "tc_get4")],
            14 => [Spawn("synthesizer", "synthesize:2:s", "tc_syncall2")],
            15 => [Route("synthesize", "gate", "tc_gate2")],
            16 => [Route("gate", "author", "tc_proceed")],
            17 => [ToolCall("GetWorkflow", [], "tc_get5")],
            18 =>
            [
                Spawn("author", "author:1:a:0", "tc_auth_0"),
                Spawn("author", "author:1:a:1", "tc_auth_1"),
            ],
            19 => [Route("author", "done", "tc_done")],
            _ => [new TextMessage { Text = "Workflow finished.", Role = Role.Assistant }],
        };
    }

    private static Mock<IStreamingAgent> StaticSubAgent(string answerJson)
    {
        var mock = new Mock<IStreamingAgent>();
        mock.Setup(a =>
                a.GenerateReplyStreamingAsync(
                    It.IsAny<IEnumerable<IMessage>>(),
                    It.IsAny<GenerateReplyOptions>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Returns(() =>
                Task.FromResult(
                    ToAsyncEnumerable([new TextMessage { Text = answerJson, Role = Role.Assistant }])
                )
            );
        return mock;
    }

    private static SubAgentTemplate Template(string name, Mock<IStreamingAgent> mock) =>
        new()
        {
            Name = name,
            SystemPrompt = $"You are the {name} sub-agent.",
            AgentFactory = () => mock.Object,
        };

    private static ToolCallMessage Route(string completed, string next, string toolCallId) =>
        ToolCall(
            "SetCurrentNode",
            new JsonObject { ["completedNodeId"] = completed, ["nextNodeId"] = next },
            toolCallId
        );

    private static ToolCallMessage Spawn(string subagentType, string name, string toolCallId) =>
        ToolCall(
            "Agent",
            new JsonObject
            {
                ["subagent_type"] = subagentType,
                ["prompt"] = "Do the unit of work.",
                ["name"] = name,
            },
            toolCallId
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

    private const string CppCurriculum = """
        {
          "schemaVersion": 1,
          "objective": "Build a C++ curriculum.",
          "sharedContext": "You build a C++ curriculum.",
          "inputs": { "clusters": ["pointers", "templates"] },
          "state": { "research": [], "curriculum": {}, "authored": [] },
          "maxStepBudget": 100,
          "nodes": [
            { "id": "start", "type": "start", "title": "Start", "next": ["research"] },
            {
              "id": "research",
              "type": "procedural",
              "title": "Research clusters",
              "tasksMode": "authored",
              "joinPolicy": { "mode": "all" },
              "taskList": [
                {
                  "id": "r",
                  "delegate": "agent",
                  "subagent_type": "researcher",
                  "forEach": "inputs.clusters",
                  "promptTemplate": "Research {{item}} idx={{index}}.",
                  "outputSchema": {
                    "type": "object",
                    "required": ["topic"],
                    "properties": { "topic": { "type": "string" } }
                  },
                  "writes": { "to": "state.research", "mode": "append" }
                }
              ],
              "next": ["synthesize"]
            },
            {
              "id": "synthesize",
              "type": "procedural",
              "title": "Synthesize curriculum",
              "tasksMode": "authored",
              "joinPolicy": { "mode": "all" },
              "taskList": [
                {
                  "id": "s",
                  "delegate": "agent",
                  "subagent_type": "synthesizer",
                  "promptTemplate": "Synthesize from {{state.research}}.",
                  "outputSchema": {
                    "type": "object",
                    "required": ["problemCount", "problems"],
                    "properties": {
                      "problemCount": { "type": "integer" },
                      "problems": { "type": "array" }
                    }
                  },
                  "writes": { "to": "state.curriculum", "mode": "set" }
                }
              ],
              "next": ["gate"]
            },
            {
              "id": "gate",
              "type": "conditional",
              "title": "Gate on problem count",
              "maxVisits": 3,
              "onMaxVisits": "author",
              "branches": [
                {
                  "when": { "op": "gte", "path": "state.curriculum.problemCount", "value": 2 },
                  "to": "author"
                }
              ],
              "else": "research"
            },
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
                  "forEach": "state.curriculum.problems",
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
              "resultTemplate": {
                "curriculum": "{{state.curriculum}}",
                "authored": "{{state.authored}}"
              },
              "finalOutputSchema": {
                "type": "object",
                "required": ["curriculum", "authored"],
                "properties": {
                  "curriculum": { "type": "object" },
                  "authored": { "type": "array" }
                }
              }
            }
          ]
        }
        """;
}
