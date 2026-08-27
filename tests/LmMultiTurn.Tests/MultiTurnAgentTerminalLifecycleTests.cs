using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;
using FluentAssertions;
using Xunit;

namespace LmMultiTurn.Tests;

/// <summary>
/// The terminal boundary of a <see cref="MultiTurnAgentBase"/>: what "disposed" means to callers who
/// race each other, and what happens to a subscription that races the disposal that drains subscribers.
/// <para>
/// Every barrier here is a <see cref="TaskCompletionSource"/> released by the test, never a sleep — a
/// test that waited a while and then looked would be a slow test that proved nothing about the ordering.
/// The one time budget below is a guardrail so a mis-wired fixture fails loudly instead of hanging the
/// run; on a passing run nothing ever waits on it.
/// </para>
/// </summary>
public sealed class MultiTurnAgentTerminalLifecycleTests
{
    /// <summary>Failure budget for a wait that a correct implementation satisfies immediately.</summary>
    private static readonly TimeSpan Guardrail = TimeSpan.FromSeconds(15);

    /// <summary>
    /// An agent whose <see cref="OnDisposeAsync"/> can be parked on demand. Parking there puts the
    /// disposing caller INSIDE the disposal body — after the point where the old code flipped
    /// <c>_isDisposed</c> and before the point where subscribers are drained — which is exactly the
    /// window both races live in, and the only one a test can schedule from outside.
    /// </summary>
    private sealed class GatedDisposeAgent : MultiTurnAgentBase
    {
        private readonly Exception? _disposeFailure;
        private int _onDisposeCallCount;

        public GatedDisposeAgent(Exception? disposeFailure = null)
            : base("terminal-lifecycle-thread", systemPrompt: null, store: null, logger: null)
        {
            _disposeFailure = disposeFailure;
        }

        /// <summary>When true, <see cref="OnDisposeAsync"/> parks until <see cref="OnDisposeGate"/> opens.</summary>
        public bool ParkInOnDispose { get; init; }

        /// <summary>Completes once a disposing caller has reached <see cref="OnDisposeAsync"/>.</summary>
        public TaskCompletionSource OnDisposeEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Released by the test to let the parked disposal finish.</summary>
        public TaskCompletionSource OnDisposeGate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int OnDisposeCallCount => Volatile.Read(ref _onDisposeCallCount);

        /// <summary>Test-only door onto the protected fan-out so a publish can be driven without a run.</summary>
        public ValueTask PublishForTest(IMessage message) => PublishToAllAsync(message, CancellationToken.None);

        // The loop is never started in these tests: StopAsync short-circuits when there is no run task,
        // so disposal reaches OnDisposeAsync without needing a live loop to schedule around.
        protected override Task RunLoopAsync(CancellationToken ct) => Task.CompletedTask;

        protected override async Task OnDisposeAsync()
        {
            _ = Interlocked.Increment(ref _onDisposeCallCount);

            if (ParkInOnDispose)
            {
                _ = OnDisposeEntered.TrySetResult();
                await OnDisposeGate.Task.WaitAsync(Guardrail);
            }

            if (_disposeFailure != null)
            {
                throw _disposeFailure;
            }
        }
    }

    private static TextUpdateMessage TextDelta(string text) =>
        new()
        {
            Text = text,
            Role = Role.Assistant,
            RunId = "run-1",
            GenerationId = "gen-1",
            MessageOrderIdx = 0,
        };

    private static async Task<Exception?> CaptureAsync(Task task)
    {
        try
        {
            await task;
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    // ---------------------------------------------------------------------------------------------
    // PR451-006 — concurrent DisposeAsync callers share ONE completion boundary and ONE exception.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// A second caller must not be told "disposed" while the first caller is still disposing. The old
    /// body was <c>if (_isDisposed) return; _isDisposed = true;</c> — a check-then-act whose loser
    /// returned a COMPLETED ValueTask the instant the winner set the flag, so
    /// <c>await agent.DisposeAsync()</c> could return before the agent's channels, cancellation sources
    /// and owned resources had been torn down at all.
    /// </summary>
    [Fact]
    public async Task DisposeAsync_SecondCaller_DoesNotComplete_UntilTheFirstCallerHasFinishedDisposing()
    {
        var agent = new GatedDisposeAgent { ParkInOnDispose = true };

        var first = agent.DisposeAsync().AsTask();
        await agent.OnDisposeEntered.Task.WaitAsync(Guardrail);

        // Barrier established: caller #1 is parked INSIDE the disposal body. An async method runs
        // synchronously until its first INCOMPLETE await, so the completion state of the ValueTask this
        // call hands back is decided by the time the call returns — no sleep, no polling.
        var second = agent.DisposeAsync().AsTask();

        second
            .IsCompleted.Should()
            .BeFalse(
                "a caller must observe the SAME completion instant as the caller that owns disposal, "
                    + "not the instant the disposed flag was set"
            );
        first.IsCompleted.Should().BeFalse("the owning caller is still parked in OnDisposeAsync");

        agent.OnDisposeGate.SetResult();
        await Task.WhenAll(first, second).WaitAsync(Guardrail);

        agent.OnDisposeCallCount.Should().Be(1, "exactly one caller may run the disposal body");
    }

    /// <summary>
    /// A disposal that faults must fault EVERY caller with the same exception. Otherwise the winner
    /// sees the teardown failure and every loser sees a clean success — the shape that turns a failed
    /// shutdown into a silent one.
    /// </summary>
    [Fact]
    public async Task DisposeAsync_WhenTeardownFaults_EveryCallerObservesTheSameExceptionInstance()
    {
        var failure = new InvalidOperationException("simulated teardown failure");
        var agent = new GatedDisposeAgent(failure) { ParkInOnDispose = true };

        var first = agent.DisposeAsync().AsTask();
        await agent.OnDisposeEntered.Task.WaitAsync(Guardrail);
        var second = agent.DisposeAsync().AsTask();

        agent.OnDisposeGate.SetResult();

        var firstError = await CaptureAsync(first.WaitAsync(Guardrail));
        var secondError = await CaptureAsync(second.WaitAsync(Guardrail));

        firstError.Should().BeSameAs(failure);
        secondError
            .Should()
            .BeSameAs(
                failure,
                "a caller that did not win the disposal race still needs the teardown failure, "
                    + "and it must be the same failure the winner saw"
            );
    }

    /// <summary>
    /// A faulted teardown must still complete subscriber channels. A subscriber whose channel is never
    /// completed does not leak quietly — its <c>SubscribeAsync</c> enumerator parks forever.
    /// </summary>
    [Fact]
    public async Task DisposeAsync_WhenTeardownFaults_StillEndsExistingSubscriptions()
    {
        var agent = new GatedDisposeAgent(new InvalidOperationException("simulated teardown failure"));

        // Driving the enumerator by hand is what makes "the subscriber is registered" a fact rather than
        // a hope: the first MoveNextAsync runs the iterator body synchronously up to its first incomplete
        // await, and registration happens before that point. A `Task.Run(await foreach ...)` would race
        // the publish below.
        await using var subscriber = agent.SubscribeAsync().GetAsyncEnumerator();
        var pending = subscriber.MoveNextAsync();

        await agent.PublishForTest(TextDelta("live"));
        (await pending.AsTask().WaitAsync(Guardrail)).Should().BeTrue();

        var error = await CaptureAsync(agent.DisposeAsync().AsTask().WaitAsync(Guardrail));
        error.Should().BeOfType<InvalidOperationException>();

        (await subscriber.MoveNextAsync().AsTask().WaitAsync(Guardrail))
            .Should()
            .BeFalse("the terminal drain must not be conditional on a clean teardown");
    }

    // ---------------------------------------------------------------------------------------------
    // PR451-007 — subscription registration is atomic against the terminal subscriber drain.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// A subscription that starts while disposal is in flight must end on its own. Registration used to
    /// be an unsynchronised dictionary write while the drain was an unsynchronised iterate-and-clear:
    /// a subscriber that registered after the drain had listed its targets was left holding a bounded
    /// channel whose only completer had already run, so its enumerator parked forever — a hung request,
    /// not a leak that eventually clears.
    /// </summary>
    [Fact]
    public async Task SubscribeAsync_StartedWhileDisposalIsInFlight_EndsWithoutWaitingForDisposalToFinish()
    {
        var agent = new GatedDisposeAgent { ParkInOnDispose = true };

        var dispose = agent.DisposeAsync().AsTask();
        await agent.OnDisposeEntered.Task.WaitAsync(Guardrail);

        await using var subscriber = agent.SubscribeAsync().GetAsyncEnumerator();

        // The subscription must terminate while disposal is STILL PARKED. That is the discriminating
        // half: a subscriber admitted into a disposing agent has nobody left to complete its channel.
        (await subscriber.MoveNextAsync().AsTask().WaitAsync(Guardrail))
            .Should()
            .BeFalse("a subscription that loses the race to the terminal drain must end, not park");

        agent.OnDisposeGate.SetResult();
        await dispose.WaitAsync(Guardrail);
    }

    /// <summary>The same boundary once disposal has fully returned.</summary>
    [Fact]
    public async Task SubscribeAsync_AfterDisposalCompleted_EndsImmediately()
    {
        var agent = new GatedDisposeAgent();
        await agent.DisposeAsync();

        await using var subscriber = agent.SubscribeAsync().GetAsyncEnumerator();

        (await subscriber.MoveNextAsync().AsTask().WaitAsync(Guardrail)).Should().BeFalse();
    }

    /// <summary>
    /// The gate closes for NEW joins only. A subscriber that attached before disposal began must still
    /// receive what the shutdown path publishes (run terminalisation is published during disposal), and
    /// must then be ended by the drain. This is the control that keeps the fix from buying atomicity by
    /// draining subscribers too early.
    /// </summary>
    [Fact]
    public async Task ExistingSubscriber_StillReceivesMessagesPublishedDuringDisposal_ThenIsEnded()
    {
        var agent = new GatedDisposeAgent { ParkInOnDispose = true };

        await using var subscriber = agent.SubscribeAsync().GetAsyncEnumerator();
        var first = subscriber.MoveNextAsync();

        await agent.PublishForTest(TextDelta("before-disposal"));
        (await first.AsTask().WaitAsync(Guardrail)).Should().BeTrue();
        subscriber.Current.Should().BeOfType<TextUpdateMessage>().Which.Text.Should().Be("before-disposal");

        var dispose = agent.DisposeAsync().AsTask();
        await agent.OnDisposeEntered.Task.WaitAsync(Guardrail);

        await agent.PublishForTest(TextDelta("during-disposal"));
        (await subscriber.MoveNextAsync().AsTask().WaitAsync(Guardrail)).Should().BeTrue();
        subscriber.Current.Should().BeOfType<TextUpdateMessage>().Which.Text.Should().Be("during-disposal");

        agent.OnDisposeGate.SetResult();
        await dispose.WaitAsync(Guardrail);

        (await subscriber.MoveNextAsync().AsTask().WaitAsync(Guardrail)).Should().BeFalse();
    }

    /// <summary>
    /// <see cref="MultiTurnAgentBase.ExecuteRunAsync"/> refuses a disposed agent rather than admitting a
    /// subscriber nobody will drain. Passes before the fix too — it pins the contract the now-atomic
    /// check has to preserve, so tightening the check cannot silently change the caller-visible failure.
    /// </summary>
    [Fact]
    public async Task ExecuteRunAsync_AfterDisposal_ThrowsObjectDisposed()
    {
        var agent = new GatedDisposeAgent();
        await agent.DisposeAsync();

        var run = async () =>
        {
            await foreach (var _ in agent.ExecuteRunAsync(new UserInput([TextDelta("hi")])))
            {
                // no-op
            }
        };

        _ = await run.Should().ThrowAsync<ObjectDisposedException>();
    }
}
