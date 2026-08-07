using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;
using FluentAssertions;
using Xunit;
using static AchieveAi.LmDotnetTools.LmWorkflow.Tests.StartWorkflowTestHarness;

namespace AchieveAi.LmDotnetTools.LmWorkflow.Tests;

/// <summary>
///     Whether a <see cref="StreamRecoveryMessage"/> ends the controller's stream is a property of its
///     <see cref="StreamRecoveryReason"/>, not of the message type — the enum's own contract, which
///     <c>ChatWebSocketManager</c> already honours. These pin both halves for the workflow's drive loop:
///     a <see cref="StreamRecoveryReason.ReplayTruncated"/> advisory only says the run's already-published
///     PREFIX is missing and the live tail follows on the same subscription (so the workflow must keep
///     consuming and still complete), while <see cref="StreamRecoveryReason.SlowConsumer"/> means this
///     consumer was dropped and receives nothing further (so the workflow must fail rather than report a
///     truncated run as a successful one).
/// </summary>
public class WorkflowSessionStreamRecoveryTests
{
    [Fact]
    public async Task ReplayTruncatedAdvisory_IsNotTerminal_SoTheWorkflowStillReachesItsTerminalNode()
    {
        // Turn 1 leads with the advisory and then drives start → terminal in the SAME turn, which is
        // exactly the shape SubscribeAsync produces: the advisory is a LEADING frame and the live tail
        // follows it on the same subscription. Treating it as terminal abandons that tail.
        var controller = ScriptedControllerMulti(turn =>
            turn == 1
                ?
                [
                    new StreamRecoveryMessage(
                        "wf-truncated-thread",
                        "run-1",
                        "gen-1",
                        StreamRecoveryReason.ReplayTruncated
                    ),
                    ToolCall("SetCurrentNode", new() { ["nextNodeId"] = "t" }, "tc_route"),
                ]
                : [new TextMessage { Text = "Workflow finished.", Role = Role.Assistant }]
        );

        await using var handle = await WorkflowSession.StartAsync(
            objective: "drive",
            inputs: null,
            definition: MinimalDefinition(),
            subAgentOptions: EmptyControllerOptions(),
            controllerAgent: controller.Object,
            threadId: "wf-truncated-thread"
        );

        // A regression fails FAST here: the old reason-blind handler faults Completion with an
        // InvalidOperationException instead of letting the run drain.
        await handle.Completion.WaitAsync(TimeSpan.FromSeconds(30));

        handle.IsComplete.Should().BeTrue();
        handle.CurrentNodeId.Should().Be("t", "the tail that followed the advisory still routed the workflow");
    }

    [Fact]
    public async Task SlowConsumerRecovery_IsTerminal_SoTheWorkflowFailsInsteadOfCompleting()
    {
        // The distinguishing case: same message TYPE, terminal REASON. A fix that skipped every
        // StreamRecoveryMessage would let this run drain and report a stream this consumer was dropped
        // from as a successfully completed workflow.
        var controller = ScriptedControllerMulti(turn =>
            turn == 1
                ?
                [
                    new StreamRecoveryMessage(
                        "wf-dropped-thread",
                        "run-1",
                        "gen-1",
                        StreamRecoveryReason.SlowConsumer
                    ),
                ]
                : [new TextMessage { Text = "Workflow finished.", Role = Role.Assistant }]
        );

        await using var handle = await WorkflowSession.StartAsync(
            objective: "drive",
            inputs: null,
            definition: MinimalDefinition(),
            subAgentOptions: EmptyControllerOptions(),
            controllerAgent: controller.Object,
            threadId: "wf-dropped-thread"
        );

        var awaitCompletion = async () => await handle.Completion.WaitAsync(TimeSpan.FromSeconds(30));

        (await awaitCompletion.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*severed*")
            .WithMessage($"*{StreamRecoveryReason.SlowConsumer}*");

        handle.IsComplete.Should().BeFalse();
    }
}
