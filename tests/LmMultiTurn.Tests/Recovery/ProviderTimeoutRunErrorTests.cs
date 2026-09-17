using System.Runtime.CompilerServices;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;
using FluentAssertions;
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
            .OfType<RunCompletedMessage>()
            .Should()
            .ContainSingle("a run that failed must still tell the caller it ended")
            .Subject;
        completed.IsError.Should().BeTrue();
        completed.ErrorMessage.Should().Contain("HttpClient.Timeout", "the caller is told WHY the run failed");

        // The whole point of the per-run handler is that the loop outlives one bad run.
        loopTask.IsCompleted.Should().BeFalse("a per-run failure must not kill the run loop");

        var second = await WithinBudgetAsync(DrainAsync(loop, "Again", cts.Token), "the loop must accept more work");
        second
            .OfType<TextMessage>()
            .Where(m => m.Role == Role.Assistant)
            .Should()
            .ContainSingle()
            .Which.Text.Should()
            .Be("Recovered.");
        second.OfType<RunCompletedMessage>().Should().ContainSingle().Which.IsError.Should().BeFalse();

        await cts.CancelAsync();
    }

    [Fact]
    public async Task ProviderCancellationWhileRunIsCancelled_IsNotReportedAsARunError()
    {
        using var cts = new CancellationTokenSource();

        // Byte for byte the same exception type as the timeout case — the only difference is that the run
        // was actually asked to stop first, which must keep it on the cancellation path.
        ScriptProvider((_, ct) => CancelThenThrow(cts, ct));

        await using var loop = new MultiTurnAgentLoop(_mockAgent.Object, new FunctionRegistry(), "run-cancelled");
        _ = loop.RunAsync(cts.Token);

        var messages = await WithinBudgetAsync(
            DrainAsync(loop, "Go", cts.Token),
            "a cancelled run must not hang the caller"
        );

        messages
            .OfType<RunCompletedMessage>()
            .Should()
            .NotContain(m => m.IsError, "a user-requested stop is not a run failure");
    }

    /// <summary>The exception <see cref="HttpClient"/> raises when its own <c>Timeout</c> elapses.</summary>
    private static TaskCanceledException HttpClientTimeout() =>
        new(
            "The request was canceled due to the configured HttpClient.Timeout of 300 seconds elapsing.",
            new TimeoutException("A task was canceled.")
        );

    private static async Task<List<IMessage>> DrainAsync(MultiTurnAgentLoop loop, string text, CancellationToken ct)
    {
        var messages = new List<IMessage>();
        try
        {
            await foreach (
                var msg in loop.ExecuteRunAsync(new UserInput([new TextMessage { Text = text, Role = Role.User }]), ct)
            )
            {
                messages.Add(msg);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected only in the cancellation case; its assertion is on what was published, not on the throw.
        }

        return messages;
    }

    /// <summary>
    /// Fails the test rather than hanging it when the run never terminalizes — the defect under test is
    /// precisely "no terminal message ever arrives", which an unbounded await would report as a timeout of
    /// the whole test run instead of as this assertion.
    /// </summary>
    private static async Task<List<IMessage>> WithinBudgetAsync(Task<List<IMessage>> drain, string because)
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
