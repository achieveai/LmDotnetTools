using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using FluentAssertions;
using Moq;
using Xunit;

namespace AchieveAi.LmDotnetTools.LmWorkflow.Tests;

/// <summary>
///     Proves the run-observation hardening from the consumer's side: a controller whose very first provider
///     call throws must resolve <see cref="WorkflowRunHandle.Completion"/> promptly rather than leaving it
///     unresolved forever, and disposal must still complete.
/// </summary>
/// <remarks>
///     This used to be written against a pump FAULT: an <see cref="OperationCanceledException"/> raised while
///     nothing was cancelled escaped <c>MultiTurnAgentLoop</c>'s per-run error handler (which excluded that
///     type outright), killed the run loop, and was caught by <c>WorkflowSession</c>'s pump-fault continuation.
///     That exclusion was the defect behind a sub-agent stuck <c>running</c> after an <c>HttpClient.Timeout</c>,
///     so such an exception is now a run ERROR: the run terminalizes, the drive enumeration drains, and the
///     wait resolves without an exception. The anti-hang guarantee this class exists for is unchanged and is
///     what is asserted below; only the mechanism that delivers it moved.
/// </remarks>
public class WorkflowSessionFaultTests
{
    [Fact]
    public async Task ControllerProviderCancellationWithNothingCancelled_ResolvesCompletionWithinTimeout_NotHang()
    {
        // The production shape: HttpClient reports its own Timeout as a TaskCanceledException while nobody
        // asked the run to stop. It must not be mistaken for the caller cancelling.
        var controllerMock = new Mock<IStreamingAgent>();
        controllerMock
            .Setup(a =>
                a.GenerateReplyStreamingAsync(
                    It.IsAny<IEnumerable<IMessage>>(),
                    It.IsAny<GenerateReplyOptions>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Throws(
                new TaskCanceledException(
                    "The request was canceled due to the configured HttpClient.Timeout of 300 seconds elapsing.",
                    new TimeoutException()
                )
            );

        var subAgentOptions = new SubAgentOptions { Templates = new Dictionary<string, SubAgentTemplate>() };

        await using var handle = await WorkflowSession.StartAsync(
            objective: "Drive the workflow.",
            inputs: null,
            definition: null,
            subAgentOptions: subAgentOptions,
            controllerAgent: controllerMock.Object,
            threadId: "wf-fault-thread"
        );

        // A short timeout guarantees a regression (the old hang) fails the test FAST rather than stalling CI:
        // WaitAsync throws TimeoutException if Completion never resolves at all.
        var awaitCompletion = async () => await handle.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        await awaitCompletion.Should().NotThrowAsync("the consumer must never be left waiting on a failed run");

        // The run failed, so the workflow never reached a terminal node. This is the assertion that keeps the
        // test honest: Completion resolving is NOT the workflow having done its work.
        handle.Runtime.IsComplete.Should().BeFalse();
    }
}
