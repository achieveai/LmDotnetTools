using System.Runtime.CompilerServices;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using FluentAssertions;
using Moq;
using Xunit;

namespace LmMultiTurn.Tests.Recovery;

/// <summary>
/// Covers the collision between the two reasons a turn can end without finishing: a deferred tool
/// call the loop must wait for, and a provider stream cut short by a transport failure.
/// </summary>
/// <remarks>
/// <para>
/// These are separate mechanisms that meet on the same turn. Stream recovery wants to issue a
/// replacement request immediately; a deferral says the conversation cannot advance at all until an
/// out-of-process answer arrives. The deferral wins, because a continuation request would have to
/// carry a tool result that does not exist yet.
/// </para>
/// <para>
/// The second property here is subtler and is the reason these tests drive real child runs rather
/// than a single run: the recovery budget belongs to the LOGICAL INPUT, but a parked input resumes
/// in a different run. A budget that lives only in the run would silently refill on every park.
/// </para>
/// </remarks>
public class InterruptedDeferralRecoveryTests
{
    private const string DeferringToolCallId = "call_ask_user";
    private const string DeferringToolName = "ask_user_question";

    [Fact]
    public async Task InterruptionWithAnUnresolvedDeferral_ParksInsteadOfContinuing_AndAsksOnlyOnce()
    {
        // The stream dies immediately after requesting a client-side effect. That effect is now in
        // flight with a human on the other end of it; nothing the provider could be asked next is
        // answerable until it comes back.
        await using var harness = await Harness.StartAsync(
            (attempt, ct) => attempt == 1
                ? Emit([DeferringToolCall()], ResponseEnded(), ct)
                : Emit([new TextMessage { Text = "Your favourite colour is blue.", Role = Role.Assistant }], null, ct));

        // THE point of the fix, in two parts. Without the park, recovery fires here: it reissues the
        // request while the tool result is still a placeholder, which the turn precondition rejects —
        // so the count alone stays at 1 and only the run's OUTCOME tells the two apart. A parked run
        // ends cleanly, waiting; a recovered one ends in an error the user never should have seen.
        harness.ProviderCallCount.Should().Be(
            1,
            "an interrupted turn holding an unresolved deferral parks; it does not launch a continuation");
        harness.LastRunFailed.Should().BeFalse(
            "parking is a normal, successful end to a run — the conversation is waiting, not broken");
        harness.LastRunError.Should().BeNull();
        harness.ToolInvocations.Should().Be(1, "the deferring handler ran once and is awaiting its answer");
        harness.Messages.OfType<GenerationAbandonedMessage>().Should().ContainSingle(
            "the client is still told to drop the abandoned generation's partial output");

        (await harness.Loop.GetDeferredToolCallsAsync()).Should().ContainSingle(
            p => p.ToolCallId == DeferringToolCallId,
            "the call the stream died on is still outstanding, not lost with the attempt");

        // The answer arrives. It resumes in a child run, which is where the conversation continues.
        await harness.ResolveAndWaitAsync("blue");

        harness.ProviderCallCount.Should().Be(2, "the resolution is what continues the conversation");
        harness.ToolInvocations.Should().Be(1, "a completed effect is never re-executed by recovery");
        harness.SecondRequest.Should().NotBeNull();
        ToolResultIds(harness.SecondRequest!).Should().Contain(
            DeferringToolCallId,
            "the continuation carries the answer rather than re-asking for it");
        harness.LastRunFailed.Should().BeFalse();
    }

    [Fact]
    public async Task RecoveryBudgetIsSpentOncePerLogicalInput_EvenAcrossAParkAndResume()
    {
        // Attempt 1 is interrupted while a deferral is outstanding, so it parks — and that park is
        // what spends this input's one recovery. Attempt 2 is the child run that resumes it, and it
        // is interrupted too. There is no budget left, so the run must fail rather than buy a third
        // attempt: a logical input that can refill its budget by parking can loop forever against a
        // broken transport, one client round-trip at a time.
        await using var harness = await Harness.StartAsync(
            (attempt, ct) => attempt == 1
                ? Emit([DeferringToolCall()], ResponseEnded(), ct)
                : Emit(
                    [new TextUpdateMessage { Text = "still ", Role = Role.Assistant }],
                    ResponseEnded(),
                    ct));

        harness.ProviderCallCount.Should().Be(1);

        await harness.ResolveAndWaitAsync("blue");

        harness.ProviderCallCount.Should().Be(
            2,
            "the child run inherits the spent budget, so its own interruption buys no further attempt");
        harness.LastRunFailed.Should().BeTrue();
        harness.LastRunError.Should().Contain("stream_interrupted_after_recovery");
    }

    private static ToolCallMessage DeferringToolCall() =>
        new()
        {
            FunctionName = DeferringToolName,
            FunctionArgs = "{}",
            ToolCallId = DeferringToolCallId,
            Role = Role.Assistant,
        };

    private static HttpIOException ResponseEnded() =>
        new(HttpRequestError.ResponseEnded, "The response ended prematurely.");

    /// <summary>Every tool call id answered by a result in <paramref name="request"/>, either shape.</summary>
    private static IEnumerable<string?> ToolResultIds(IEnumerable<IMessage> request) =>
        request.SelectMany(m => m switch
        {
            ToolCallResultMessage single => [single.ToolCallId],
            ToolsCallResultMessage aggregate => aggregate.ToolCallResults.Select(r => r.ToolCallId),
            _ => [],
        });

    private static async IAsyncEnumerable<IMessage> Emit(
        IEnumerable<IMessage> messages,
        Exception? endWith,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var message in messages)
        {
            ct.ThrowIfCancellationRequested();
            yield return message;
            await Task.Yield();
        }

        if (endWith is not null)
        {
            throw endWith;
        }
    }

    /// <summary>
    /// Drives a real loop through park-and-resume, observing every run it starts.
    /// </summary>
    /// <remarks>
    /// A subscription rather than a single <c>ExecuteRunAsync</c> drain, because the run that parks
    /// is not the run that finishes: the resolution causes a child run, and the properties under test
    /// are about what that child inherits.
    /// </remarks>
    private sealed class Harness : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        private readonly List<IMessage> _messages = [];
        private readonly object _gate = new();
        private TaskCompletionSource<RunCompletedMessage> _runCompleted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _providerCallCount;
        private readonly StrongBox<int> _toolInvocations;

        private Harness(MultiTurnAgentLoop loop, StrongBox<int> toolInvocations)
        {
            Loop = loop;
            _toolInvocations = toolInvocations;
        }

        public MultiTurnAgentLoop Loop { get; }

        public int ProviderCallCount => Volatile.Read(ref _providerCallCount);

        /// <summary>How many times the deferring handler actually ran — the visible-effect count.</summary>
        public int ToolInvocations => Volatile.Read(ref _toolInvocations.Value);

        /// <summary>What the provider was handed on its second call, once one happens.</summary>
        public IReadOnlyList<IMessage>? SecondRequest { get; private set; }

        public bool LastRunFailed { get; private set; }

        public string? LastRunError { get; private set; }

        public IReadOnlyList<IMessage> Messages
        {
            get
            {
                lock (_gate)
                {
                    return [.. _messages];
                }
            }
        }

        public static async Task<Harness> StartAsync(
            Func<int, CancellationToken, IAsyncEnumerable<IMessage>> attemptScript)
        {
            var mockAgent = new Mock<IStreamingAgent>();

            // The registry has to be complete before the loop is constructed: the loop builds its tool
            // pipeline from it once, so a function registered afterwards is invisible and its call comes
            // back as an unknown-tool error instead of a deferral.
            var toolInvocations = new StrongBox<int>();
            var registry = new FunctionRegistry();
            registry.AddFunction(
                new FunctionContract
                {
                    Name = DeferringToolName,
                    Description = "Asks the user something and waits for the answer",
                    Parameters = [],
                },
                (_, _, _) =>
                {
                    Interlocked.Increment(ref toolInvocations.Value);
                    return Task.FromResult<ToolHandlerResult>(new ToolHandlerResult.Deferred());
                });

            var loop = new MultiTurnAgentLoop(
                mockAgent.Object,
                registry,
                $"thread-{Guid.NewGuid():N}",
                store: new InMemoryConversationStore());

            var harness = new Harness(loop, toolInvocations);

            mockAgent
                .Setup(a => a.GenerateReplyStreamingAsync(
                    It.IsAny<IEnumerable<IMessage>>(),
                    It.IsAny<GenerateReplyOptions>(),
                    It.IsAny<CancellationToken>()))
                .Returns<IEnumerable<IMessage>, GenerateReplyOptions, CancellationToken>((sent, _, ct) =>
                {
                    var attempt = Interlocked.Increment(ref harness._providerCallCount);
                    if (attempt == 2)
                    {
                        harness.SecondRequest = [.. sent];
                    }

                    return Task.FromResult(attemptScript(attempt, ct));
                });

            harness.Observe();
            _ = loop.RunAsync(harness._cts.Token);

            var parked = harness.NextRunCompletion();
            await loop.SendAsync([new TextMessage { Text = "What is my favourite colour?", Role = Role.User }]);
            await parked.WaitAsync(TimeSpan.FromSeconds(10));

            (await loop.GetDeferredToolCallsAsync()).Should().NotBeEmpty(
                "every test here starts from a run parked on a deferral");
            return harness;
        }

        /// <summary>Answers the outstanding deferral and waits for the child run it causes to finish.</summary>
        public async Task ResolveAndWaitAsync(string result)
        {
            var continued = NextRunCompletion();
            await Loop.ResolveToolCallAsync(DeferringToolCallId, result);
            await continued.WaitAsync(TimeSpan.FromSeconds(10));
        }

        /// <summary>Arms a wait for the next run completion BEFORE the action that causes it.</summary>
        private Task<RunCompletedMessage> NextRunCompletion()
        {
            var pending = new TaskCompletionSource<RunCompletedMessage>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            Volatile.Write(ref _runCompleted, pending);
            return pending.Task;
        }

        private void Observe()
        {
            var messages = Loop.SubscribeAsync(_cts.Token).GetAsyncEnumerator(_cts.Token);
            var first = messages.MoveNextAsync();

            // Not the harness token: a cancelled one would skip the body and leave the pending move
            // unobserved. Enumerating starts on this thread so the subscription is attached before
            // anything is sent — a late subscriber gets no replay and would wait forever.
            _ = Task.Run(async () =>
            {
                try
                {
                    for (var has = await first; has; has = await messages.MoveNextAsync())
                    {
                        var current = messages.Current;
                        lock (_gate)
                        {
                            _messages.Add(current);
                        }

                        if (current is not RunCompletedMessage completed)
                        {
                            continue;
                        }

                        LastRunFailed = completed.IsError;
                        LastRunError = completed.ErrorMessage;
                        Volatile.Read(ref _runCompleted).TrySetResult(completed);
                    }
                }
                catch (OperationCanceledException)
                {
                    // Cancelling the token is how the harness ends the subscription.
                }
                finally
                {
                    await messages.DisposeAsync();
                }
            }, CancellationToken.None);
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            await Loop.DisposeAsync();
            _cts.Dispose();
        }
    }
}
