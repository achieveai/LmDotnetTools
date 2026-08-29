using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using FluentAssertions;
using Xunit;

namespace LmMultiTurn.Tests;

/// <summary>
/// Pins <see cref="MultiTurnAgentBase.StopAsync"/> as an honest boundary (#506): when it returns
/// normally, the run it was asked to stop is over — and when it cannot make that true, it does not
/// pretend otherwise.
/// </summary>
/// <remarks>
/// Two separate ways the old <c>StopAsync</c> could return while the run was still live, each with
/// its own test because they fail through different state:
/// <list type="bullet">
///   <item>
///     the PRE-LOOP window — history recovery, ledger reconciliation, lifecycle reconciliation, usage
///     hydration and <c>OnBeforeRunAsync</c> all ran before <c>RunAsync</c> assigned the loop task, so
///     a <c>StopAsync</c> arriving in that window saw two null fields and returned as a no-op while
///     store work was in flight; and
///   </item>
///   <item>
///     the TIMEOUT path — a loop that did not stop inside the budget was logged and then had its
///     tracking state cleared anyway, which left <c>IsRunning</c> reporting <see langword="false"/>
///     for a loop that was still running.
///   </item>
/// </list>
/// </remarks>
public class MultiTurnAgentStopBoundaryTests
{
    /// <summary>How long a blocked-forever wait is given before the test gives up and FAILS.</summary>
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long a stop is watched for while the work it must wait out is held open. Only an upper
    /// bound on the bug: the old no-op returned in microseconds.
    /// </summary>
    private static readonly TimeSpan BlockedObservation = TimeSpan.FromMilliseconds(500);

    [Fact]
    public async Task StopAsync_DuringThePreLoopWindow_WaitsForTheStartupStoreWriteToFinish()
    {
        var store = new InMemoryConversationStore();
        await using var agent = new PreLoopBlockingAgent("thread-startup", store);

        var run = Task.Run(() => agent.RunAsync(CancellationToken.None));
        await agent.StartupEntered.Task.WaitAsync(Generous);

        var stop = agent.StopAsync(Generous);

        // Startup is provably in flight (StartupEntered fired) and provably unfinished (the gate is
        // still closed), so a stop that completes here has returned over live startup work.
        var raced = await Task.WhenAny(stop, Task.Delay(BlockedObservation));
        _ = raced
            .Should()
            .NotBeSameAs(
                stop,
                "StopAsync must not report a stopped agent while the run is still inside its pre-loop startup window"
            );
        _ = agent.StartupWriteCompleted.Should().BeFalse();

        agent.ReleaseStartup();

        await stop.WaitAsync(Generous);
        _ = agent
            .StartupWriteCompleted.Should()
            .BeTrue("the startup store write must have landed before StopAsync returned");

        await run.WaitAsync(Generous);
    }

    [Fact]
    public async Task StopAsync_WhenTheLoopOutlivesTheTimeout_DoesNotReportTheAgentStopped()
    {
        await using var agent = new UnstoppableLoopAgent("thread-stuck");

        var run = Task.Run(() => agent.RunAsync(CancellationToken.None));
        await agent.LoopEntered.Task.WaitAsync(Generous);

        // The loop ignores cancellation, so this stop CANNOT succeed. What it must not do is clear the
        // state that says a run is live and hand back a normal return that reads as "stopped".
        await agent.StopAsync(TimeSpan.FromMilliseconds(250)).WaitAsync(Generous);

        _ = agent
            .IsRunning.Should()
            .BeTrue(
                "a stop whose timeout expired has NOT stopped the loop, and must not leave the agent reporting otherwise"
            );

        agent.ReleaseLoop();
        await run.WaitAsync(Generous);
    }

    /// <summary>
    /// Parks inside <c>OnBeforeRunAsync</c> — the last step of the pre-loop startup window — then does
    /// a conversation-store write, so a stop that returns early is observable as a write it did not
    /// wait for.
    /// </summary>
    private sealed class PreLoopBlockingAgent(string threadId, IConversationStore store)
        : MultiTurnAgentBase(threadId, store: store)
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _startupWriteCompleted;

        /// <summary>Fires once startup has been entered and is parked on the gate.</summary>
        public TaskCompletionSource StartupEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Whether the startup store write has run to completion.</summary>
        public bool StartupWriteCompleted => Volatile.Read(ref _startupWriteCompleted) != 0;

        public void ReleaseStartup() => _gate.TrySetResult();

        protected override async Task OnBeforeRunAsync()
        {
            _ = StartupEntered.TrySetResult();

            // Deliberately NOT cancellable: a writer already past its own cancellation check is exactly
            // what a stop has to wait out. A gate that cancelled would let the fix be "cancel it"
            // rather than "wait for it", and lose the write.
            await _gate.Task;

            await Store!.UpdateMetadataAsync(
                ThreadId,
                existing =>
                    existing
                    ?? new ThreadMetadata
                    {
                        ThreadId = ThreadId,
                        LastUpdated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    },
                CancellationToken.None
            );

            _ = Interlocked.Exchange(ref _startupWriteCompleted, 1);
        }

        protected override Task RunLoopAsync(CancellationToken ct) => Task.CompletedTask;
    }

    /// <summary>
    /// A loop that does not observe cancellation, so every stop against it times out. Released
    /// explicitly by the test so the run still ends and disposal is not left hanging on it.
    /// </summary>
    private sealed class UnstoppableLoopAgent(string threadId) : MultiTurnAgentBase(threadId)
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Fires once the run loop has been entered and is parked on the gate.</summary>
        public TaskCompletionSource LoopEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void ReleaseLoop() => _gate.TrySetResult();

        protected override async Task RunLoopAsync(CancellationToken ct)
        {
            _ = LoopEntered.TrySetResult();
            await _gate.Task;
        }
    }
}
