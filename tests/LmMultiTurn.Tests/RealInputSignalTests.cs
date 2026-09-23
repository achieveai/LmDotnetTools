using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;
using FluentAssertions;
using Xunit;

namespace LmMultiTurn.Tests;

/// <summary>
/// The interruption signal a blocking tool waits on
/// (<see cref="MultiTurnAgentBase.WaitForRealInputAsync"/>). Its whole job is to distinguish input a
/// run would act on from the loop's own wake sentinel — an entry with
/// <see cref="QueuedInput.Resume"/> set and no messages, written to the same channel purely to break
/// the input wait. Implemented as <c>PendingInputCount &gt; 0</c> it would count the sentinel and end
/// a wait for a reason that never happened.
/// </summary>
public class RealInputSignalTests
{
    [Fact]
    public async Task WakeSentinel_DoesNotSignalRealInput_ThoughItDoesSitOnTheChannel()
    {
        await using var agent = new SignalProbeAgent();

        await agent.EnqueueSentinelAsync();

        agent.QueuedCount.Should().Be(1, "the sentinel really is on the channel");
        agent.HasRealInputQueued.Should().BeFalse("a wake sentinel is not an interruption");

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        var wait = agent.WaitForRealInputAsync(cts.Token);
        await FluentActions.Awaiting(() => wait).Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task RealInput_SignalsAndReleasesAWaiter()
    {
        await using var agent = new SignalProbeAgent();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var wait = agent.WaitForRealInputAsync(cts.Token);
        wait.IsCompleted.Should().BeFalse();

        _ = await agent.SendAsync([new TextMessage { Text = "hello", Role = Role.User }]);

        await wait;
        agent.HasRealInputQueued.Should().BeTrue();
    }

    [Fact]
    public async Task DrainingReArmsTheSignal_SoTheNextWaitBlocksAgain()
    {
        await using var agent = new SignalProbeAgent();

        _ = await agent.SendAsync([new TextMessage { Text = "hello", Role = Role.User }]);
        agent.HasRealInputQueued.Should().BeTrue();

        _ = agent.Drain(out var drained);
        drained.Should().ContainSingle();
        agent.HasRealInputQueued.Should().BeFalse("the drain consumed the input the signal announced");

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        await FluentActions
            .Awaiting(() => agent.WaitForRealInputAsync(cts.Token))
            .Should()
            .ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task ASentinelDrainedAlongsideRealInput_LeavesTheSignalSetForTheRealInput()
    {
        await using var agent = new SignalProbeAgent();

        _ = await agent.SendAsync([new TextMessage { Text = "first", Role = Role.User }]);
        _ = agent.Drain(out _);
        agent.HasRealInputQueued.Should().BeFalse();

        // Re-arming must not break later signalling: a sentinel still says nothing, and the real
        // input that follows it still releases a waiter.
        await agent.EnqueueSentinelAsync();
        agent.HasRealInputQueued.Should().BeFalse("a sentinel after a re-arm is still not an interruption");

        _ = await agent.SendAsync([new TextMessage { Text = "second", Role = Role.User }]);
        agent.HasRealInputQueued.Should().BeTrue();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await agent.WaitForRealInputAsync(cts.Token);
    }

    [Fact]
    public async Task ACancelledWait_ReportsCancellation_EvenWhenTheSignalIsAlreadySet()
    {
        // Task.WaitAsync on an already-completed task ignores its token. A blocking tool has to be able
        // to tell "my turn was cancelled" from "real input interrupted me", so cancellation wins here.
        await using var agent = new SignalProbeAgent();

        _ = await agent.SendAsync([new TextMessage { Text = "hello", Role = Role.User }]);
        agent.HasRealInputQueued.Should().BeTrue("the signal is set before the wait starts");

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await FluentActions
            .Awaiting(() => agent.WaitForRealInputAsync(cancelled.Token))
            .Should()
            .ThrowAsync<OperationCanceledException>();
    }

    /// <summary>Exposes the base's protected input plumbing; runs no loop of its own.</summary>
    private sealed class SignalProbeAgent() : MultiTurnAgentBase("signal-probe-thread")
    {
        public int QueuedCount => PendingInputCount;

        public ValueTask EnqueueSentinelAsync() =>
            EnqueueRawAsync(
                new QueuedInput(
                    new UserInput([], InputId: null, ParentRunId: null),
                    ReceiptId: "wake:probe",
                    QueuedAt: DateTimeOffset.UtcNow,
                    Resume: new ResumeSentinel(string.Empty, string.Empty)
                )
            );

        public bool Drain(out List<QueuedInput> inputs) => TryDrainInputs(out inputs);

        protected override Task RunLoopAsync(CancellationToken ct) => Task.CompletedTask;
    }
}
