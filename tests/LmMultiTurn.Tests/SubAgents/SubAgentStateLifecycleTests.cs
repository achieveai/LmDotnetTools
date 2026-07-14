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

    [Fact]
    public async Task BeginTerminalDisposal_ThrowingLifecycleCancellationCallback_StillCompletesTerminalTransition()
    {
        // Blocker (round 4): CancellationTokenSource.Cancel() runs callbacks synchronously and aggregates
        // any that throw into an AggregateException. That must NOT abort BeginTerminalDisposalAsync's lease
        // drain / terminal-status transition — otherwise the provider is left neither disposed nor poisoned
        // and a later restart could reuse an interrupted provider.
        var state = NewOwnedProviderState();

        var decision = state.BeginContinuation(notifyParentOnCompletion: false);
        decision.Mode.Should().Be(ContinuationMode.Inject);

        // A linked token (as the inject send would hold) with a callback that throws when the lifecycle
        // token is cancelled during terminal disposal.
        using var linked = state.LinkLifecycleToken(CancellationToken.None);
        var callbackFired = false;
        _ = linked.Token.Register(() =>
        {
            callbackFired = true;
            throw new InvalidOperationException("cancellation callback boom");
        });

        // The cancel (with its throwing callback) happens inside here; it must be swallowed and the
        // disposal must park on the still-held lease rather than fault or skip the transition.
        var disposal = state.BeginTerminalDisposalAsync(isError: false);

        await Task.Delay(100);
        callbackFired.Should().BeTrue("terminal disposal must have cancelled the lifecycle token");
        disposal.IsCompleted.Should().BeFalse("the throwing callback must not skip the lease drain");
        state.Status.Should().Be(SubAgentStatus.Running, "status stays Running until the lease drains");

        // Releasing the lease lets the (un-aborted) transition finish and flip terminal.
        state.EndInjectLease();
        await disposal.WaitAsync(TimeSpan.FromSeconds(5));
        state.Status.Should().Be(SubAgentStatus.Completed,
            "a throwing cancellation callback must not abort the terminal transition");
    }

    [Fact]
    public void TryBeginInjectLease_WhileRunning_GrantsLease_WithoutMutatingNotifyOrClaimingRestart()
    {
        // The out-of-band context-delivery lease (WI #198) admits into a genuinely-running sub-agent, but —
        // unlike BeginContinuation(bool) — must NOT overwrite the parent-relay preference (which would
        // silently drop or double the sub-agent's own completion relay).
        var state = NewOwnedProviderState();
        state.NotifyParentOnCompletion = true;

        state.TryBeginInjectLease().Should().BeTrue("a running, non-terminating sub-agent accepts context");
        state.NotifyParentOnCompletion.Should().BeTrue(
            "TryBeginInjectLease must not mutate NotifyParentOnCompletion");

        state.EndInjectLease();
    }

    [Fact]
    public async Task TryBeginInjectLease_TakesTheSameLeaseTerminalDisposalDrains()
    {
        // The inject lease shares the counter BeginTerminalDisposalAsync awaits, so a disposal cannot flip
        // terminal (and dispose the owned provider) while an admitted context send is still in flight.
        var state = NewOwnedProviderState();

        state.TryBeginInjectLease().Should().BeTrue();

        var disposal = state.BeginTerminalDisposalAsync(isError: false);
        await Task.Delay(100);
        disposal.IsCompleted.Should().BeFalse("terminal disposal must await the in-flight inject lease");
        state.Status.Should().Be(SubAgentStatus.Running);

        state.EndInjectLease();
        await disposal.WaitAsync(TimeSpan.FromSeconds(5));
        state.Status.Should().Be(SubAgentStatus.Completed);
    }

    [Fact]
    public void TryBeginInjectLease_WhenCompleted_Refused_AndDoesNotClaimRestart()
    {
        // A finished sub-agent refuses context (dropped, never restarted). The refusal must also leave the
        // single-flight restart claim untouched, so a real continuation still owns the restart.
        var state = NewOwnedProviderState();
        state.Status = SubAgentStatus.Completed;

        state.TryBeginInjectLease().Should().BeFalse("a finished sub-agent must not accept context");

        state.BeginContinuation(notifyParentOnCompletion: false).Mode
            .Should().Be(ContinuationMode.Restart, "TryBeginInjectLease must not consume the restart claim");
        state.EndRestart();
    }

    [Fact]
    public async Task TryBeginInjectLease_WhileTerminating_Refused_TeardownRace()
    {
        // Teardown race: a context delivery arriving after a terminal owned-provider disposal has begun
        // (status still Running, but _terminating set) must be refused, never resurrecting the finishing
        // sub-agent into an extra run against a provider that is being torn down.
        var state = NewOwnedProviderState();

        // Hold a lease so the terminal disposal parks in the "terminating" state (status still Running).
        state.BeginContinuation(notifyParentOnCompletion: false).Mode.Should().Be(ContinuationMode.Inject);
        var disposal = state.BeginTerminalDisposalAsync(isError: false);
        await Task.Delay(50);

        state.TryBeginInjectLease().Should().BeFalse(
            "context must not be injected into a sub-agent whose terminal disposal has begun");

        state.EndInjectLease();
        await disposal.WaitAsync(TimeSpan.FromSeconds(5));
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
