using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using AchieveAi.LmDotnetTools.LmWorkflow.Model;
using Moq;

namespace AchieveAi.LmDotnetTools.LmWorkflow.Tests;

/// <summary>
///     Shared scaffolding for the StartWorkflow tool-family tests: scripted controllers, minimal/invalid
///     definitions, and empty controller options.
/// </summary>
internal static class StartWorkflowTestHarness
{
    /// <summary>A structurally-parseable but INVALID definition: a start with no terminal node.</summary>
    public const string InvalidNoTerminal = """
        {
          "schemaVersion": 1,
          "objective": "invalid",
          "nodes": [
            { "id": "s", "type": "start", "title": "Start", "next": ["t"] }
          ]
        }
        """;

    public static WorkflowDefinition MinimalDefinition() =>
        WorkflowJson.Deserialize(WorkflowFixtures.MinimalValid);

    public static WorkflowDefinition InvalidDefinition() => WorkflowJson.Deserialize(InvalidNoTerminal);

    public static SubAgentOptions EmptyControllerOptions() =>
        new() { Templates = new Dictionary<string, SubAgentTemplate>() };

    /// <summary>Routes <c>start → terminal</c> for <see cref="WorkflowFixtures.MinimalValid"/>, then ends the run.</summary>
    public static IMessage DriveMinimalToTerminal(int turn) =>
        turn switch
        {
            1 => ToolCall("SetCurrentNode", new JsonObject { ["nextNodeId"] = "t" }, "tc_route"),
            _ => new TextMessage { Text = "Workflow finished.", Role = Role.Assistant },
        };

    /// <summary>Never advances the workflow — keeps calling GetWorkflow so the loop runs until its turn cap.</summary>
    public static IMessage NeverComplete(int turn) =>
        ToolCall("GetWorkflow", [], $"tc_get_{turn}");

    /// <summary>A controller that returns one scripted message per turn.</summary>
    public static Mock<IStreamingAgent> ScriptedController(Func<int, IMessage> script)
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

    /// <summary>
    ///     A controller whose FIRST turn blocks until <paramref name="gate"/> is released, then drives the
    ///     minimal workflow to its terminal. Used to hold a workflow "running" for Check/Wait/capacity tests.
    /// </summary>
    public static Mock<IStreamingAgent> GatedController(TaskCompletionSource gate)
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
            .Returns(
                async (IEnumerable<IMessage> _, GenerateReplyOptions _, CancellationToken ct) =>
                {
                    var thisTurn = Interlocked.Increment(ref turn);
                    if (thisTurn == 1)
                    {
                        await gate.Task.WaitAsync(ct).ConfigureAwait(false);
                    }

                    return ToAsyncEnumerable([DriveMinimalToTerminal(thisTurn)]);
                }
            );
        return controller;
    }

    /// <summary>
    ///     A controller whose pump faults: it throws an <see cref="OperationCanceledException"/> while nothing
    ///     is cancelled, which faults the run's Completion (see WorkflowSession's pump-fault handling) — the
    ///     "controller run threw" path, distinct from turn-budget exhaustion.
    /// </summary>
    public static Mock<IStreamingAgent> FaultingController()
    {
        var controller = new Mock<IStreamingAgent>();
        controller
            .Setup(a =>
                a.GenerateReplyStreamingAsync(
                    It.IsAny<IEnumerable<IMessage>>(),
                    It.IsAny<GenerateReplyOptions>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Throws(new OperationCanceledException("controller pump fault"));
        return controller;
    }

    public static ToolCallMessage ToolCall(string functionName, JsonObject args, string toolCallId) =>
        new()
        {
            FunctionName = functionName,
            FunctionArgs = args.ToJsonString(),
            ToolCallId = toolCallId,
            Role = Role.Assistant,
        };

    public static async IAsyncEnumerable<IMessage> ToAsyncEnumerable(
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
