using System.Runtime.CompilerServices;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;
using FluentAssertions;
using Xunit;

namespace LmMultiTurn.Tests;

/// <summary>
/// Regression tests for what <see cref="MultiTurnAgentLoop"/> reports in
/// <see cref="RunCompletedMessage.PendingMessageCount"/>.
///
/// The loop drains input that arrived DURING a run into a follow-on run through the same agent and
/// provider, so such a completion is not terminal — and consumers act on that: <c>SubAgentManager</c>
/// disposes a sub-agent's owned provider agent only on a completion with no pending input. The loop
/// used to hardcode 0 here (unlike <c>ClaudeAgentLoop</c>, which has always reported its real queue
/// depth), so a message that landed while a run was finishing produced a "terminal" completion, the
/// provider — and its <c>HttpClient</c> — was disposed, and the follow-on run's very first request
/// threw <c>ObjectDisposedException</c>. Observed live as: <c>No tool calls in turn 11, run
/// complete</c> → <c>Message queued</c> → <c>Run … completed</c> → <c>Starting run … (inputs: 1)</c>
/// → <c>Run … failing (ObjectDisposedException)</c>.
///
/// Both tests gate the provider mid-generation so the second message lands after the turn loop's
/// pre-turn input poll — i.e. in the exact window that is still queued when the run completes —
/// making the race deterministic rather than timing-dependent.
/// </summary>
public class MultiTurnAgentLoopPendingInputTests
{
    [Fact]
    public async Task CompleteRun_InputQueuedWhileRunInFlight_ReportsItAsPending()
    {
        // Arrange: the first generation blocks until the test has queued a second message, so that
        // message is provably still in the input channel when the run completes.
        var generationStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseGeneration = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var agent = new GatedStreamingAgent(
            generationStarted,
            releaseGeneration,
            _ => new TextMessage { Text = "answer", Role = Role.Assistant }
        );

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var loop = new MultiTurnAgentLoop(agent, new FunctionRegistry(), "test-thread");
        _ = loop.RunAsync(cts.Token);

        // ExecuteRunAsync subscribes BEFORE it sends, so the completion cannot be missed.
        var messages = new List<IMessage>();
        var runTask = Task.Run(
            async () =>
            {
                await foreach (
                    var msg in loop.ExecuteRunAsync(
                        new UserInput([new TextMessage { Text = "first", Role = Role.User }], InputId: "input-1"),
                        cts.Token
                    )
                )
                {
                    messages.Add(msg);
                }
            },
            cts.Token
        );

        // Act: queue a second message while the first run is mid-generation, then let it finish.
        (await generationStarted.Task.WaitAsync(TimeSpan.FromSeconds(15)))
            .Should()
            .BeTrue();
        _ = await loop.SendAsync([new TextMessage { Text = "second", Role = Role.User }], inputId: "input-2");
        releaseGeneration.SetResult(true);

        await runTask.WaitAsync(TimeSpan.FromSeconds(15));

        // Assert: the completion advertises the queued input, so a consumer knows a follow-on run is
        // coming and must NOT tear the agent's provider down.
        var completed = messages.OfType<RunCompletedMessage>().Should().ContainSingle().Subject;
        completed.IsError.Should().BeFalse();
        completed
            .PendingMessageCount.Should()
            .Be(1, "the message queued during the run is still unassigned when the run completes");
        completed
            .HasPendingMessages.Should()
            .BeTrue("SubAgentManager disposes a sub-agent's owned provider only on a completion with no pending input");
    }

    [Fact]
    public async Task CompleteRun_FailedRunWithInputQueuedWhileInFlight_ReportsItAsPending()
    {
        // Same window, error path: a run that FAILS is still followed by a run for whatever queued
        // while it ran, so the provider must survive this completion too. The error path used to omit
        // the argument entirely, silently defaulting to 0.
        var generationStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseGeneration = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var agent = new GatedStreamingAgent(
            generationStarted,
            releaseGeneration,
            callIndex =>
                callIndex == 1
                    ? throw new InvalidOperationException("generation failed")
                    : new TextMessage { Text = "answer", Role = Role.Assistant }
        );

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var loop = new MultiTurnAgentLoop(agent, new FunctionRegistry(), "test-thread");
        _ = loop.RunAsync(cts.Token);

        var messages = new List<IMessage>();
        var runTask = Task.Run(
            async () =>
            {
                await foreach (
                    var msg in loop.ExecuteRunAsync(
                        new UserInput([new TextMessage { Text = "first", Role = Role.User }], InputId: "input-1"),
                        cts.Token
                    )
                )
                {
                    messages.Add(msg);
                }
            },
            cts.Token
        );

        (await generationStarted.Task.WaitAsync(TimeSpan.FromSeconds(15))).Should().BeTrue();
        _ = await loop.SendAsync([new TextMessage { Text = "second", Role = Role.User }], inputId: "input-2");
        releaseGeneration.SetResult(true);

        await runTask.WaitAsync(TimeSpan.FromSeconds(15));

        var completed = messages.OfType<RunCompletedMessage>().Should().ContainSingle().Subject;
        completed.IsError.Should().BeTrue("the generation threw");
        completed.PendingMessageCount.Should().Be(1);
        completed
            .HasPendingMessages.Should()
            .BeTrue("a failed run is followed by a run for the queued input, through the same provider");
    }

    /// <summary>
    /// Streaming agent whose FIRST generation signals <paramref name="started"/> and then blocks on
    /// <paramref name="release"/> — the seam a test needs to land input inside a run, after the turn
    /// loop's pre-turn input poll. Later generations run straight through so the follow-on run the
    /// loop starts for that input does not wedge the test.
    /// </summary>
    private sealed class GatedStreamingAgent(
        TaskCompletionSource<bool> started,
        TaskCompletionSource<bool> release,
        Func<int, IMessage> reply
    ) : IStreamingAgent
    {
        private int _callCount;

        public async Task<IAsyncEnumerable<IMessage>> GenerateReplyStreamingAsync(
            IEnumerable<IMessage> messages,
            GenerateReplyOptions? options = null,
            CancellationToken cancellationToken = default
        )
        {
            var callIndex = Interlocked.Increment(ref _callCount);
            if (callIndex == 1)
            {
                _ = started.TrySetResult(true);
                _ = await release.Task.WaitAsync(cancellationToken);
            }

            return Single(reply(callIndex), cancellationToken);
        }

        public async Task<IEnumerable<IMessage>> GenerateReplyAsync(
            IEnumerable<IMessage> messages,
            GenerateReplyOptions? options = null,
            CancellationToken cancellationToken = default
        )
        {
            var stream = await GenerateReplyStreamingAsync(messages, options, cancellationToken);
            var collected = new List<IMessage>();
            await foreach (var msg in stream.WithCancellation(cancellationToken))
            {
                collected.Add(msg);
            }

            return collected;
        }

        private static async IAsyncEnumerable<IMessage> Single(
            IMessage message,
            [EnumeratorCancellation] CancellationToken ct
        )
        {
            ct.ThrowIfCancellationRequested();
            yield return message;
            await Task.Yield();
        }
    }
}
