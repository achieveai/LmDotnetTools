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

    [Fact]
    public async Task BeginTerminalDisposal_UnblocksWedgedInjectSend_ViaLifecycleTokenCancellation()
    {
        // Blocker B: an admitted inject send that wedges on a stalled provider/channel must be
        // cancellable by terminal disposal, even when the CALLER'S token never fires. The send links
        // LinkLifecycleToken(caller); terminal disposal cancels the lifecycle token so the send unblocks,
        // its lease drains, and disposal proceeds instead of parking forever.
        var state = NewOwnedProviderState();

        var decision = state.BeginContinuation(notifyParentOnCompletion: false);
        decision.Mode.Should().Be(ContinuationMode.Inject);

        // The wedged inject send: a non-cancelable caller token (CancellationToken.None) linked with the
        // run's lifecycle token, awaiting forever. Its lease is released in finally, mirroring the manager.
        var sendObservedCancellation = false;
        var wedgedSend = Task.Run(async () =>
        {
            using var linked = state.LinkLifecycleToken(CancellationToken.None);
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, linked.Token);
            }
            catch (OperationCanceledException)
            {
                sendObservedCancellation = true;
            }
            finally
            {
                state.EndInjectLease();
            }
        });

        // Terminal disposal parks on the lease drain AND cancels the lifecycle token, which unblocks the
        // wedged send; without the lifecycle link this would hang forever on the non-cancelable caller.
        var disposal = state.BeginTerminalDisposalAsync(isError: false);

        await disposal.WaitAsync(TimeSpan.FromSeconds(5));
        await wedgedSend.WaitAsync(TimeSpan.FromSeconds(5));

        sendObservedCancellation.Should().BeTrue("terminal disposal must cancel the wedged inject send");
        state.Status.Should().Be(SubAgentStatus.Completed);
    }

    [Fact]
    public async Task TryArmRunning_AfterRunReachedTerminal_DoesNotResurrectToRunning()
    {
        // Blocker C: a fast restarted run can complete and dispose its owned provider before the restart's
        // "arm Running" publish executes. That publish (TryArmRunning) must be generation-guarded so it
        // cannot overwrite the terminal state — otherwise the next continuation injects into a dead run
        // whose provider is already disposed.
        var terminated = NewOwnedProviderState();
        var generation = terminated.BeginRunGeneration();

        // The restarted run completes terminally BEFORE TryArmRunning runs.
        await terminated.BeginTerminalDisposalAsync(isError: false);
        terminated.Status.Should().Be(SubAgentStatus.Completed);

        terminated.TryArmRunning(generation).Should().BeFalse(
            "a run that already reached terminal must not be resurrected to Running");
        terminated.Status.Should().Be(SubAgentStatus.Completed, "the terminal state must survive the guarded publish");

        // Control: a generation whose run has NOT gone terminal publishes Running normally.
        var live = NewOwnedProviderState();
        var liveGeneration = live.BeginRunGeneration();
        live.TryArmRunning(liveGeneration).Should().BeTrue();
        live.Status.Should().Be(SubAgentStatus.Running);
    }

    [Fact]
    public void OwnedProviderPoison_IsSetOnFailedDisposal_AndClearedByFreshProvider()
    {
        // Blocker A: a FAILED terminal disposal must poison the run's provider so a continuation rebuilds a
        // fresh one instead of reusing a partially-disposed instance. A failed disposal leaves the dispose
        // guard reset (HasDisposedOwnedProviderAgent == false), so the poison flag is the signal the
        // restart path keys on; assigning a fresh provider clears it.
        var state = NewOwnedProviderState();
        state.OwnedProviderTerminalDisposeFailed.Should().BeFalse();

        state.MarkOwnedProviderTerminalDisposeFailed();
        state.OwnedProviderTerminalDisposeFailed.Should().BeTrue("a failed terminal disposal poisons the provider");
        state.HasDisposedOwnedProviderAgent.Should().BeFalse(
            "a failed disposal did not latch Disposed, so poison — not HasDisposed — must drive the rebuild");

        state.SetOwnedProviderAgent(new Mock<IStreamingAgent>().Object);
        state.OwnedProviderTerminalDisposeFailed.Should().BeFalse("assigning a fresh provider clears the poison");
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
