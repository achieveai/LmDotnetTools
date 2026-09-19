using System.Runtime.CompilerServices;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmCore.Models;
using AchieveAi.LmDotnetTools.LmLifecycle;
using AchieveAi.LmDotnetTools.LmLifecycle.Payloads;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.Lifecycle;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;
using FluentAssertions;
using LmMultiTurn.Tests.Lifecycle;
using Moq;
using Xunit;

namespace LmMultiTurn.Tests.Recovery;

/// <summary>
/// Covers the failure shape that leaves a run with no terminal message at all: an
/// <see cref="OperationCanceledException"/> raised by something OTHER than the run's own cancellation.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="HttpClient"/> reports its own <c>Timeout</c> as a <see cref="TaskCanceledException"/>, which is
/// an <see cref="OperationCanceledException"/>. The per-run error handler used to exclude that type outright,
/// so a provider timeout skipped the "complete the run with an error" path entirely, escaped the run loop and
/// killed it — leaving the caller (a parent agent waiting on a sub-agent) parked on a run that is never
/// completed. These tests pin both halves: a timeout fails the run and the loop survives it, and a genuine
/// cancellation still takes the cancellation path.
/// </para>
/// </remarks>
public class ProviderTimeoutRunErrorTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    private readonly Mock<IStreamingAgent> _mockAgent = new();

    /// <summary>The number of provider calls made, in order.</summary>
    private int _attempts;

    [Fact]
    public async Task ProviderTimeoutWhileRunIsNotCancelled_FailsTheRunAndLeavesTheLoopUsable()
    {
        // Attempt 1 is the production shape: HttpClient.Timeout elapsed while nobody asked the run to stop.
        ScriptProvider(
            (attempt, ct) =>
                attempt == 1
                    ? Throw(HttpClientTimeout(), ct)
                    : Emit([new TextMessage { Text = "Recovered.", Role = Role.Assistant }], ct)
        );

        using var cts = new CancellationTokenSource();
        await using var loop = new MultiTurnAgentLoop(_mockAgent.Object, new FunctionRegistry(), "provider-timeout");
        var loopTask = loop.RunAsync(cts.Token);

        var first = await WithinBudgetAsync(DrainAsync(loop, "Go", cts.Token), "the timed-out run must terminalize");

        var completed = first
            .Messages.OfType<RunCompletedMessage>()
            .Should()
            .ContainSingle("a run that failed must still tell the caller it ended")
            .Subject;
        completed.IsError.Should().BeTrue();
        completed.ErrorMessage.Should().Contain("HttpClient.Timeout", "the caller is told WHY the run failed");

        // The whole point of the per-run handler is that the loop outlives one bad run.
        loopTask.IsCompleted.Should().BeFalse("a per-run failure must not kill the run loop");

        var second = await WithinBudgetAsync(DrainAsync(loop, "Again", cts.Token), "the loop must accept more work");
        second
            .Messages.OfType<TextMessage>()
            .Where(m => m.Role == Role.Assistant)
            .Should()
            .ContainSingle()
            .Which.Text.Should()
            .Be("Recovered.");
        second.Messages.OfType<RunCompletedMessage>().Should().ContainSingle().Which.IsError.Should().BeFalse();

        await cts.CancelAsync();
    }

    [Fact]
    public async Task ProviderCancellationWhileRunIsCancelled_AbandonsTheRunWithoutCompletingIt()
    {
        using var cts = new CancellationTokenSource();

        // Byte for byte the same exception type as the timeout case — the only difference is that the run
        // was actually asked to stop first, which must keep it on the cancellation path.
        ScriptProvider((_, ct) => CancelThenThrow(cts, ct));

        // Observed through the lifecycle publisher, NOT through a subscriber: a subscriber draining the run
        // on the run's own token races that token's cancellation and can legitimately see nothing, which
        // made the old assertions both flaky and blind to an error completion. The publisher records every
        // event synchronously whatever the token says, so the error-vs-cancel decision is always captured.
        var publisher = new RecordingLifecyclePublisher();
        await using var loop = new MultiTurnAgentLoop(
            _mockAgent.Object,
            new FunctionRegistry(),
            "run-cancelled",
            lifecycleServices: new MultiTurnLifecycleServices { Publisher = publisher }
        );
        var loopTask = loop.RunAsync(cts.Token);

        _ = await loop.SendAsync([new TextMessage { Text = "Go", Role = Role.User }], ct: CancellationToken.None);

        // The per-run handler runs inline in the loop, so once the loop has ended the run's error-vs-cancel
        // decision has already been made and published. Ordering, not timing, carries the assertions below.
        var loopEnded = await Task.WhenAny(loopTask, Task.Delay(Budget));
        loopEnded.Should().BeSameAs(loopTask, "the run cancelled its own token, which must end the loop");
        loopTask.IsFaulted.Should().BeFalse("a user-requested stop ends the loop as a cancellation, not a fault");

        _attempts
            .Should()
            .Be(1, "nothing below says anything about cancellation unless the run actually reached the provider");
        publisher
            .Payloads<RunCompletedPayload>(LifecycleEventTypes.RunCompleted)
            .Should()
            .BeEmpty("a user-requested stop abandons the run: the per-run handler must neither fail nor complete it");

        // The stop path is what ends an abandoned run, and it says so: the one terminal event is Cancelled.
        await loop.StopAsync();

        var started = publisher
            .Payloads<RunStartedPayload>(LifecycleEventTypes.RunStarted)
            .Should()
            .ContainSingle()
            .Subject;
        var completed = publisher
            .Payloads<RunCompletedPayload>(LifecycleEventTypes.RunCompleted)
            .Should()
            .ContainSingle("the run ends exactly once")
            .Subject;
        completed.RunId.Should().Be(started.RunId);
        completed.Outcome.Should().Be(LifecycleRunOutcomes.Cancelled, "the run was stopped, not failed");
        completed.Error.Should().BeNull();
    }

    [Fact]
    public async Task ContextObservationCancellationWhileNothingIsCancelled_IsSwallowedAndTheRunSucceeds()
    {
        // A SECOND site guarded by IsRunCancellation: the per-generation context observation. Its handler
        // exists so observing a turn never breaks the turn - but "never" has to include an
        // OperationCanceledException nobody asked for (a capacity lookup on its own internal deadline),
        // which the old `ex is not OperationCanceledException` shape let escape. Escaping here is not
        // silent: it reaches the per-run handler and completes the run as an ERROR, so an observation
        // failure would be reported to the caller as the model turn having failed.
        var capacity = new CancellingCapacityResolver();
        ScriptProvider((_, ct) => Emit([new TextMessage { Text = "Observed anyway.", Role = Role.Assistant }], ct));

        using var cts = new CancellationTokenSource();
        await using var loop = new MultiTurnAgentLoop(
            _mockAgent.Object,
            new FunctionRegistry(),
            "context-observation-cancel",
            defaultOptions: new GenerateReplyOptions { ModelId = "model-x" },
            lifecycleServices: new MultiTurnLifecycleServices { CapacityResolver = capacity }
        );
        _ = loop.RunAsync(cts.Token);

        var drained = await WithinBudgetAsync(
            DrainAsync(loop, "Go", cts.Token),
            "an observation failure must not leave the run without a terminal message"
        );

        capacity
            .Calls.Should()
            .BeGreaterThan(0, "the test proves nothing unless the observation actually reached the resolver");
        cts.IsCancellationRequested.Should().BeFalse("nobody asked this run to stop");

        var completed = drained.Messages.OfType<RunCompletedMessage>().Should().ContainSingle().Subject;
        completed.IsError.Should().BeFalse("observing a turn must never fail the turn it observes");
        drained
            .Messages.OfType<TextMessage>()
            .Where(m => m.Role == Role.Assistant)
            .Should()
            .ContainSingle()
            .Which.Text.Should()
            .Be("Observed anyway.", "the turn proceeds unobserved rather than not at all");

        await cts.CancelAsync();
    }

    /// <summary>
    /// A capacity lookup that gives up on its own internal deadline: it raises the same
    /// <see cref="TaskCanceledException"/> shape as an HTTP timeout, while the run's token is untouched.
    /// </summary>
    private sealed class CancellingCapacityResolver : IModelCapacityResolver
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public ModelCapacity? Resolve(string modelId)
        {
            _ = Interlocked.Increment(ref _calls);
            throw HttpClientTimeout();
        }
    }

    /// <summary>The exception <see cref="HttpClient"/> raises when its own <c>Timeout</c> elapses.</summary>
    private static TaskCanceledException HttpClientTimeout() =>
        new(
            "The request was canceled due to the configured HttpClient.Timeout of 300 seconds elapsing.",
            new TimeoutException("A task was canceled.")
        );

    /// <summary>What draining a run produced: every message it published, in order.</summary>
    private sealed record Drained(List<IMessage> Messages);

    private static async Task<Drained> DrainAsync(MultiTurnAgentLoop loop, string text, CancellationToken ct)
    {
        var messages = new List<IMessage>();
        await foreach (
            var msg in loop.ExecuteRunAsync(new UserInput([new TextMessage { Text = text, Role = Role.User }]), ct)
        )
        {
            messages.Add(msg);
        }

        return new Drained(messages);
    }

    /// <summary>
    /// Fails the test rather than hanging it when the run never terminalizes — the defect under test is
    /// precisely "no terminal message ever arrives", which an unbounded await would report as a timeout of
    /// the whole test run instead of as this assertion.
    /// </summary>
    private static async Task<Drained> WithinBudgetAsync(Task<Drained> drain, string because)
    {
        var finished = await Task.WhenAny(drain, Task.Delay(Budget));
        finished.Should().BeSameAs(drain, because);
        return await drain;
    }

    private void ScriptProvider(Func<int, CancellationToken, IAsyncEnumerable<IMessage>> attemptScript)
    {
        _mockAgent
            .Setup(a =>
                a.GenerateReplyStreamingAsync(
                    It.IsAny<IEnumerable<IMessage>>(),
                    It.IsAny<GenerateReplyOptions>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Returns<IEnumerable<IMessage>, GenerateReplyOptions, CancellationToken>(
                (_, _, ct) => Task.FromResult(attemptScript(Interlocked.Increment(ref _attempts), ct))
            );
    }

    private static async IAsyncEnumerable<IMessage> Emit(
        IEnumerable<IMessage> messages,
        [EnumeratorCancellation] CancellationToken ct = default
    )
    {
        foreach (var message in messages)
        {
            ct.ThrowIfCancellationRequested();
            yield return message;
            await Task.Yield();
        }
    }

    private static async IAsyncEnumerable<IMessage> Throw(
        Exception failure,
        [EnumeratorCancellation] CancellationToken ct = default
    )
    {
        await Task.Yield();
        _ = ct;
        throw failure;
#pragma warning disable CS0162 // Unreachable — required to make this an iterator.
        yield break;
#pragma warning restore CS0162
    }

    private static async IAsyncEnumerable<IMessage> CancelThenThrow(
        CancellationTokenSource cts,
        [EnumeratorCancellation] CancellationToken ct = default
    )
    {
        await cts.CancelAsync();
        _ = ct;
        throw HttpClientTimeout();
#pragma warning disable CS0162 // Unreachable — required to make this an iterator.
        yield break;
#pragma warning restore CS0162
    }
}
