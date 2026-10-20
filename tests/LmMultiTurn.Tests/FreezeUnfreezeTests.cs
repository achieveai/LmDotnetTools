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
/// Regression tests for <see cref="IFreezableAgent"/> on <see cref="MultiTurnAgentLoop"/>. A frozen loop must
/// finish its in-flight turn (so the tool result is captured in history) and then stop advancing — no next turn,
/// no delayed continuation — until unfrozen, WITHOUT being torn down. This is how an owner makes a conversation
/// quiescent so the review slot it holds can be reclaimed.
/// </summary>
public class FreezeUnfreezeTests
{
    [Fact]
    public async Task Freeze_CapturesInFlightTurnToolResult_ButDoesNotStartNextTurn_UntilUnfrozen()
    {
        // Turn 1 is a tool call; turn 2 is the final text. Turn 1's generation is gated so we can Freeze
        // BETWEEN the two turns deterministically: Freeze is set before turn 1 is released, so it is already
        // in effect when the loop finishes turn 1 (running the tool) and reaches the pre-turn-2 freeze gate.
        var turn1Started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseTurn1 = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var toolRan = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = new System.Collections.Concurrent.ConcurrentQueue<int>();

        var agent = new GatedSequenceAgent(
            turn1Started,
            releaseTurn1,
            calls,
            call =>
                call == 1
                    ? new ToolCallMessage
                    {
                        FunctionName = "probe",
                        FunctionArgs = "{}",
                        ToolCallId = "call_1",
                        Role = Role.Assistant,
                        GenerationId = "gen1",
                    }
                    : new TextMessage
                    {
                        Text = "final answer",
                        Role = Role.Assistant,
                        GenerationId = "gen2",
                    }
        );

        var registry = new FunctionRegistry();
        registry.AddFunction(
            new FunctionContract
            {
                Name = "probe",
                Description = "A parameterless probe tool.",
                Parameters = [],
            },
            (_, _, _) =>
            {
                _ = toolRan.TrySetResult(true);
                return Task.FromResult<ToolHandlerResult>(ToolHandlerResult.FromText("ok"));
            }
        );

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var loop = new MultiTurnAgentLoop(agent, registry, "freeze-thread");
        _ = loop.RunAsync(cts.Token);

        var outputs = new System.Collections.Concurrent.ConcurrentBag<IMessage>();
        var runTask = Task.Run(
            async () =>
            {
                await foreach (
                    var msg in loop.ExecuteRunAsync(
                        new UserInput([new TextMessage { Text = "hi", Role = Role.User }], InputId: "input-1"),
                        cts.Token
                    )
                )
                {
                    outputs.Add(msg);
                }
            },
            cts.Token
        );

        // Turn 1 is generating. Freeze now, THEN release turn 1.
        await turn1Started.Task.WaitAsync(cts.Token);
        loop.Freeze();
        loop.IsFrozen.Should().BeTrue();
        _ = releaseTurn1.TrySetResult(true);

        // The tool ran and its result was captured — turn 1 has fully settled.
        await toolRan.Task.WaitAsync(cts.Token);

        // But a frozen loop does NOT advance to turn 2: the run stays open and no second model call happens.
        await Task.Delay(300, cts.Token);
        runTask.IsCompleted.Should().BeFalse("a frozen loop must not complete the run");
        calls.Should().ContainSingle().Which.Should().Be(1, "turn 2 must not start while frozen");

        // Unfreeze — the loop resumes, turn 2 runs, and the run completes.
        loop.Unfreeze();
        loop.IsFrozen.Should().BeFalse();
        await runTask.WaitAsync(cts.Token);

        int[] expectedCalls = [1, 2];
        calls.Should().BeEquivalentTo(expectedCalls, "turn 2 runs only after unfreeze");
        outputs.OfType<ToolCallResultMessage>().Should().NotBeEmpty("the tool result was captured before the freeze");
        outputs.OfType<TextMessage>().Should().Contain(m => m.Text == "final answer");
        outputs.OfType<RunCompletedMessage>().Should().NotBeEmpty();
    }

    /// <summary>
    /// A streaming agent that gates its FIRST generation on <paramref name="release"/> (signalling
    /// <paramref name="started"/> when it begins) and records each call's 1-based index into
    /// <paramref name="calls"/>, so a test can observe exactly which turns have started.
    /// </summary>
    private sealed class GatedSequenceAgent(
        TaskCompletionSource<bool> started,
        TaskCompletionSource<bool> release,
        System.Collections.Concurrent.ConcurrentQueue<int> calls,
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
            calls.Enqueue(callIndex);
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
