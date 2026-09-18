using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using FluentAssertions;
using Moq;
using Xunit;
using static AchieveAi.LmDotnetTools.LmWorkflow.Tests.StartWorkflowTestHarness;

namespace AchieveAi.LmDotnetTools.LmWorkflow.Tests;

/// <summary>
///     Proves the run-observation hardening from the consumer's side: a controller run that ends in an
///     ERROR must fail <see cref="WorkflowRunHandle.Completion"/> — carrying the run's own error text — and
///     must do so promptly rather than leaving the consumer waiting forever.
/// </summary>
/// <remarks>
///     <para>
///         This class used to be written against a pump FAULT: an <see cref="OperationCanceledException"/>
///         raised while nothing was cancelled escaped <c>MultiTurnAgentLoop</c>'s per-run error handler
///         (which excluded that type outright), killed the run loop, and was caught by
///         <c>WorkflowSession</c>'s pump-fault continuation. That exclusion was the defect behind a
///         sub-agent stuck <c>running</c> after an <c>HttpClient.Timeout</c>, so such an exception is now a
///         run ERROR: the run terminalizes with <c>RunCompletedMessage(IsError: true)</c>, the drive
///         enumeration drains normally, and the pump-fault continuation never fires. The workflow then had
///         nothing left that noticed the failure and reported the run as a SUCCESS — which is what these
///         tests now pin against.
///     </para>
///     <para>
///         <b>Precedence.</b> An errored run fails the workflow even when the runtime had already reached its
///         own terminal node (<c>IsComplete == true</c>). Failing is information-preserving — the terminal
///         state stays readable on <c>handle.Runtime</c> and the error text becomes readable on the
///         exception — whereas completing destroys the error text, which has no other surface. The
///         cancellation branch of the drive deliberately goes the other way, because a caller-requested stop
///         of an already-finished workflow carries no error to report.
///     </para>
/// </remarks>
public class WorkflowSessionFaultTests
{
    /// <summary>
    ///     The production shape: <c>HttpClient</c> reports its own <c>Timeout</c> as a
    ///     <see cref="TaskCanceledException"/> while nobody asked the run to stop.
    /// </summary>
    private const string ProviderTimeoutText =
        "The request was canceled due to the configured HttpClient.Timeout of 300 seconds elapsing.";

    private static TaskCanceledException ProviderTimeout() => new(ProviderTimeoutText, new TimeoutException());

    [Fact]
    public async Task ControllerProviderCancellationWithNothingCancelled_FailsCompletionWithTheProviderErrorText_WithoutHanging()
    {
        var controllerMock = new Mock<IStreamingAgent>();
        controllerMock
            .Setup(a =>
                a.GenerateReplyStreamingAsync(
                    It.IsAny<IEnumerable<IMessage>>(),
                    It.IsAny<GenerateReplyOptions>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Throws(ProviderTimeout);

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
        // WaitAsync throws TimeoutException if Completion never resolves at all. That is the never-hang half.
        var awaitCompletion = async () => await handle.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        // The outcome half. "Completion resolved" is NOT "the workflow succeeded": the run ended in an
        // error, so the wait must observe a FAILURE that names why — not merely resolve, and not resolve
        // successfully. Asserting on the text is what stops the reason being swallowed.
        (await awaitCompletion.Should().ThrowAsync<InvalidOperationException>("an errored run is a failed workflow"))
            .WithMessage("*completed with an error*")
            .WithMessage($"*{ProviderTimeoutText}*");

        // The run failed on its first provider call, so the workflow never reached a terminal node.
        handle.Runtime.IsComplete.Should().BeFalse();
    }

    [Fact]
    public async Task ErroredControllerRun_FailsTheWorkflow_EvenWhenItAlreadyReachedItsTerminalNode()
    {
        // Turn 1 routes start → terminal, so the runtime reaches IsComplete BEFORE the run fails. Turn 2 —
        // the wrap-up turn the loop still takes after executing the tool call — hits the provider timeout.
        // This is the precedence case: a resolved terminal and an errored run at the same time.
        var controller = ScriptedControllerMulti(turn =>
            turn == 1
                ? [ToolCall("SetCurrentNode", new() { ["nextNodeId"] = "t" }, "tc_route")]
                : throw ProviderTimeout()
        );

        await using var handle = await WorkflowSession.StartAsync(
            objective: "drive",
            inputs: null,
            definition: MinimalDefinition(),
            subAgentOptions: EmptyControllerOptions(),
            controllerAgent: controller.Object,
            threadId: "wf-terminal-then-error-thread"
        );

        var awaitCompletion = async () => await handle.Completion.WaitAsync(TimeSpan.FromSeconds(30));

        (
            await awaitCompletion.Should().ThrowAsync<InvalidOperationException>("the error outranks the terminal")
        ).WithMessage($"*{ProviderTimeoutText}*");

        // Failing is information-PRESERVING: the terminal the workflow did reach is still readable here.
        // This is also what makes the precedence assertion above non-vacuous — without it the test would
        // pass against an implementation that simply never reached the terminal.
        handle.Runtime.IsComplete.Should().BeTrue("the workflow really did route to its terminal node");
        handle.CurrentNodeId.Should().Be("t");
    }

    [Fact]
    public async Task SuccessfulControllerRun_StillCompletesTheWorkflow_SoOnlyTheErrorFlagFailsIt()
    {
        // The distinguishing case for the fix: every workflow that finishes normally also drains a
        // RunCompletedMessage. A failure signal keyed to the message TYPE rather than to IsError would turn
        // every successful workflow into a failure, and this is the case that catches it.
        var controller = ScriptedController(DriveMinimalToTerminal);

        await using var handle = await WorkflowSession.StartAsync(
            objective: "drive",
            inputs: null,
            definition: MinimalDefinition(),
            subAgentOptions: EmptyControllerOptions(),
            controllerAgent: controller.Object,
            threadId: "wf-clean-completion-thread"
        );

        await handle.Completion.WaitAsync(TimeSpan.FromSeconds(30));

        handle.IsComplete.Should().BeTrue();
        handle.CurrentNodeId.Should().Be("t");
    }
}
