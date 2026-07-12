using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using FluentAssertions;
using Moq;
using Xunit;

namespace LmMultiTurn.Tests.SubAgents;

/// <summary>
/// Synchronized race tests for <see cref="SubAgentState"/>'s continuation-admission /
/// terminal-disposal / restart coordination — the deadlock-free lease + single-flight-restart
/// primitives that <c>SubAgentManager.SendMessageAsync</c> and <c>HandleRunCompletionAsync</c>
/// build on. They prove, deterministically:
/// <list type="bullet">
/// <item><description>A terminal owned-provider disposal cannot overlap an admitted inject send:
/// <see cref="SubAgentState.BeginTerminalDisposalAsync"/> parks until the in-flight send lease is
/// released.</description></item>
/// <item><description>A continuation that arrives while the owned provider is being disposed is
/// routed to a fresh-provider restart, never injected through the provider being torn
/// down.</description></item>
/// <item><description>Exactly one caller performs the restart of a finished run; concurrent
/// continuations await it instead of both entering <c>RestartRunAsync</c>.</description></item>
/// </list>
/// </summary>
public class SubAgentStateLifecycleTests
{
    [Fact]
    public async Task BeginTerminalDisposal_ParksUntilInFlightInjectSendLeaseIsReleased()
    {
        var state = NewOwnedProviderState();

        // Admit an inject continuation: a send lease is now in flight (the manager would be inside
        // state.Agent.SendAsync at this point).
        var decision = state.BeginContinuation(notifyParentOnCompletion: false);
        decision.Mode.Should().Be(ContinuationMode.Inject);

        // A terminal completion must NOT flip the sub-agent terminal — which is the point just
        // before the manager disposes the owned provider — while the send lease is still held.
        var disposal = state.BeginTerminalDisposalAsync(isError: false);
        await Task.Delay(150);
        disposal.IsCompleted.Should().BeFalse(
            "terminal disposal must await the in-flight inject send lease so disposal cannot overlap a send");
        state.Status.Should().Be(SubAgentStatus.Running, "status must stay Running until the send lease drains");

        // Releasing the lease lets the disposal proceed and flip terminal.
        state.EndInjectLease();
        await disposal.WaitAsync(TimeSpan.FromSeconds(5));
        state.Status.Should().Be(SubAgentStatus.Completed);
    }

    [Fact]
    public async Task BeginContinuation_WhileOwnedProviderDisposalPending_RoutesToRestartNotInject()
    {
        var state = NewOwnedProviderState();

        // Hold a send lease so the terminal disposal parks in the "terminating" state (status still
        // Running, owned provider about to be disposed).
        var held = state.BeginContinuation(notifyParentOnCompletion: false);
        held.Mode.Should().Be(ContinuationMode.Inject);

        var disposal = state.BeginTerminalDisposalAsync(isError: false);
        await Task.Delay(50);

        // A continuation arriving now must NOT inject through the disposing owned provider — it is
        // routed to a fresh-provider restart instead.
        var racing = state.BeginContinuation(notifyParentOnCompletion: false);
        racing.Mode.Should().Be(ContinuationMode.Restart);

        // Drain the lease so the parked disposal completes, then release the restart claim.
        state.EndInjectLease();
        await disposal.WaitAsync(TimeSpan.FromSeconds(5));
        state.EndRestart();
    }

    [Fact]
    public async Task BeginContinuation_FromFinishedRun_OnlyOneCallerClaimsRestart()
    {
        var state = NewOwnedProviderState();
        state.Status = SubAgentStatus.Completed; // the run has finished

        var first = state.BeginContinuation(notifyParentOnCompletion: false);
        var second = state.BeginContinuation(notifyParentOnCompletion: false);
        var third = state.BeginContinuation(notifyParentOnCompletion: false);

        first.Mode.Should().Be(ContinuationMode.Restart, "exactly one caller performs the restart");
        second.Mode.Should().Be(ContinuationMode.AwaitRestart);
        third.Mode.Should().Be(ContinuationMode.AwaitRestart);
        second.RestartCompleted.Should().NotBeNull();
        second.RestartCompleted!.IsCompleted.Should().BeFalse("the restart is still in flight");

        // The restart owner finishing wakes every waiter so they re-evaluate against the re-armed loop.
        state.EndRestart();
        await second.RestartCompleted!.WaitAsync(TimeSpan.FromSeconds(5));
        third.RestartCompleted!.IsCompleted.Should().BeTrue();
    }

    private static SubAgentState NewOwnedProviderState()
    {
        var state = new SubAgentState
        {
            AgentId = "agent-1",
            TemplateName = "tmpl",
            Task = "do work",
            Agent = new Mock<IMultiTurnAgent>().Object,
            Template = new SubAgentTemplate
            {
                Name = "tmpl",
                SystemPrompt = "You are a test agent.",
                AgentFactory = () => new Mock<IStreamingAgent>().Object,
            },
        };

        // Give it an owned provider so the terminal-disposal path engages the send-lease drain.
        state.SetOwnedProviderAgent(new Mock<IStreamingAgent>().Object);
        return state;
    }
}
