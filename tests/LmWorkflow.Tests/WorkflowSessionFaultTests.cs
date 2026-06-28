using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using FluentAssertions;
using Moq;
using Xunit;

namespace AchieveAi.LmDotnetTools.LmWorkflow.Tests;

/// <summary>
///     Proves the run-observation hardening (Fix M4): when the controller pump faults early — before the
///     drive enumeration observes a run completion — the run handle's <see cref="WorkflowRunHandle.Completion"/>
///     FAULTS instead of hanging forever, and disposal still completes promptly.
/// </summary>
public class WorkflowSessionFaultTests
{
    [Fact]
    public async Task ControllerFaultingPump_FaultsCompletion_WithinTimeout_NotHang()
    {
        // An OperationCanceledException raised while NOTHING is cancelled escapes the loop's per-run error
        // handling (which only swallows non-cancellation errors) and faults the run pump itself — the exact
        // "pump faults early" case that previously left Completion unresolved forever.
        var controllerMock = new Mock<IStreamingAgent>();
        controllerMock
            .Setup(a =>
                a.GenerateReplyStreamingAsync(
                    It.IsAny<IEnumerable<IMessage>>(),
                    It.IsAny<GenerateReplyOptions>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Throws(new OperationCanceledException("controller pump fault"));

        var subAgentOptions = new SubAgentOptions
        {
            Templates = new Dictionary<string, SubAgentTemplate>(),
        };

        await using var handle = await WorkflowSession.StartAsync(
            objective: "Drive the workflow.",
            inputs: null,
            definition: null,
            subAgentOptions: subAgentOptions,
            controllerAgent: controllerMock.Object,
            threadId: "wf-fault-thread"
        );

        // A short timeout guarantees a regression (the old hang) fails the test FAST rather than stalling CI:
        // WaitAsync surfaces the faulted Completion as the original exception, or a TimeoutException on a hang.
        var awaitCompletion = async () =>
            await handle.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        await awaitCompletion.Should().ThrowAsync<OperationCanceledException>();

        // The workflow never reached a terminal node.
        handle.Runtime.IsComplete.Should().BeFalse();
    }
}
