using System.Runtime.CompilerServices;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using AchieveAi.LmDotnetTools.LmMultiTurn.Triggers;
using AchieveAi.LmDotnetTools.LmTestUtils;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace LmMultiTurn.Tests.Triggers;

/// <summary>
/// End-to-end coverage that a notify-mode trigger fire is delivered as a fresh,
/// <c>&lt;trigger&gt;</c>-tagged user turn through the loop's ordinary queue gate — never
/// interrupting an in-flight generation, per locked decision #1. Task 3 of the Wait/trigger
/// follow-ups (#140). Mirrors <see cref="WaitTriggerLoopIntegrationTests"/>'s loop + mock-provider
/// scaffold and reuses the shared notify-capable <see cref="ManualTriggerSource"/> fake.
/// </summary>
/// <remarks>
/// A notify-mode <c>Wait</c> returns an immediate "armed" acknowledgment (it does not park a
/// deferred tool call), so the arming run completes normally and the notify wait stays armed for
/// fires. Each fire is injected as a fresh <c>&lt;trigger&gt;</c>-tagged user turn through the
/// loop's queue gate and drives a new run. This test reads the injected envelope back from the
/// conversation store (populated by the loop's own <c>AddToHistory</c> persistence) — the
/// assertion is about queue-gate delivery.
/// </remarks>
public class NotifyEnvelopeDeliveryTests
{
    private readonly Mock<IStreamingAgent> _mockAgent = new();
    private readonly Mock<ILogger<MultiTurnAgentLoop>> _loggerMock = new();

    private static string WaitArgs(object body) => JsonSerializer.Serialize(body);

    [Fact]
    public async Task NotifyFire_InjectsTriggerTaggedUserTurn()
    {
        // Arrange: loop with a notify-capable manual source registered via AdditionalRegistrations,
        // mock provider that, on the first user turn, calls Wait(kind:"manual", mode:"notify",
        // timeout:"1h"). Act: fire the source's sink once. Assert: history gains a user
        // TextMessage whose Text contains "<trigger>" and the fire payload.
        var history = await RunNotifyScenarioAsync(fireCount: 1);

        history
            .OfType<TextMessage>()
            .Should()
            .Contain(m => m.Role == Role.User && m.Text.Contains("<trigger>") && m.Text.Contains("fire-1"));
    }

    /// <summary>
    /// Arms a notify-capable manual trigger, waits for the first (parking) run to complete, fires
    /// the source <paramref name="fireCount"/> times, and returns the persisted conversation
    /// history read back from the loop's own store once every fire's run has completed.
    /// </summary>
    private async Task<IReadOnlyList<IMessage>> RunNotifyScenarioAsync(int fireCount)
    {
        var manual = new ManualTriggerSource();
        var options = new TriggerOptions
        {
            AdditionalRegistrations =
            [
                new TriggerSourceRegistration
                {
                    Kind = "manual",
                    Description = "test notify source",
                    ArgsSchema = "{}",
                    Capabilities = ManualTriggerSource.Caps,
                    Source = manual,
                },
            ],
        };

        var waitCall = new ToolCallMessage
        {
            FunctionName = WaitToolProvider.WaitToolName,
            FunctionArgs = WaitArgs(
                new
                {
                    kind = "manual",
                    mode = "notify",
                    timeout = "1h",
                }
            ),
            ToolCallId = "tc_notify",
            Role = Role.Assistant,
        };

        var callCount = 0;
        _mockAgent
            .Setup(a =>
                a.GenerateReplyStreamingAsync(
                    It.IsAny<IEnumerable<IMessage>>(),
                    It.IsAny<GenerateReplyOptions>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Returns<IEnumerable<IMessage>, GenerateReplyOptions, CancellationToken>(
                (_, _, _) =>
                {
                    callCount++;
                    if (callCount == 1)
                    {
                        return Task.FromResult(ToAsyncEnumerable([waitCall]));
                    }

                    var finalText = new TextMessage { Text = $"handled {callCount}", Role = Role.Assistant };
                    return Task.FromResult(ToAsyncEnumerable([finalText]));
                }
            );

        const string threadId = "notify-thread";
        var store = new InMemoryConversationStore();

        await using var loop = new MultiTurnAgentLoop(
            _mockAgent.Object,
            new FunctionRegistry(),
            threadId,
            store: store,
            logger: _loggerMock.Object,
            triggerOptions: options
        );

        using var cts = new CancellationTokenSource();
        _ = loop.RunAsync(cts.Token);

        var runsCompleted = SubscribeForRunCompletions(loop, cts.Token, expectedCount: fireCount + 1);

        await loop.SendAsync([new TextMessage { Text = "arm the notify wait", Role = Role.User }]);
        await runsCompleted[0].Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Notify mode returns an immediate "armed" ack (no deferral); the arming run completes
        // normally and the wait stays armed, so its sink is registered under the tool-call id.
        manual.Sinks.Should().ContainKey("tc_notify");

        for (var i = 0; i < fireCount; i++)
        {
            var sink = manual.Sinks["tc_notify"];
            await sink.FireAsync(new TriggerFireEvent($"fire-{i + 1}"), cts.Token);
            await runsCompleted[i + 1].Task.WaitAsync(TimeSpan.FromSeconds(5));
        }

        await cts.CancelAsync();

        // AddToHistory persists fire-and-forget, so the write lands after its RunCompletedMessage
        // (which fires whether or not the injected run's own provider call succeeds — see the remarks
        // on this test's precondition caveat). Loud: a deadline that returned the last snapshot
        // silently left every caller to re-derive, from its own assertion failure, that the wait was
        // what actually gave up.
        IReadOnlyList<IMessage> history = [];
        await Wait.UntilAsync(
            async () =>
            {
                history = MessagePersistenceConverter.FromPersistedMessages(await store.LoadMessagesAsync(threadId));
                return history.OfType<TextMessage>().Any(m => m.Role == Role.User && m.Text.Contains("<trigger>"));
            },
            "the fire-and-forget AddToHistory write put a <trigger> envelope into persisted history",
            TimeSpan.FromSeconds(5),
            TimeSpan.FromMilliseconds(50)
        );

        return history;
    }

    /// <summary>
    /// Establishes a live subscription and returns one completion source per expected run.
    /// Registration happens synchronously here, before this method returns: SubscribeAsync registers
    /// the subscriber ahead of the iterator's first suspension point, so issuing (not awaiting) the
    /// first move is what makes the subscription live. Leaving registration to the pump task instead
    /// would race the caller's trigger — a run that completes before the subscriber registers closes
    /// the replay buffer, and the late subscriber then sees nothing at all.
    /// </summary>
    private static List<TaskCompletionSource<bool>> SubscribeForRunCompletions(
        MultiTurnAgentLoop loop,
        CancellationToken ct,
        int expectedCount
    )
    {
        var sources = Enumerable
            .Range(0, expectedCount)
            .Select(_ => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously))
            .ToList();

        var subscription = loop.SubscribeAsync(ct).GetAsyncEnumerator(ct);
        var firstMove = subscription.MoveNextAsync();

        // Not scheduled with `ct`: the pump is already cancellation-bound through the enumerator, and
        // handing the token to Task.Run would let a pre-cancelled token skip the body outright,
        // stranding the move issued above with nothing to observe or dispose it.
        _ = Task.Run(async () =>
        {
            var completed = 0;
            try
            {
                for (var move = firstMove; await move; move = subscription.MoveNextAsync())
                {
                    if (subscription.Current is not RunCompletedMessage)
                    {
                        continue;
                    }

                    if (completed < sources.Count)
                    {
                        sources[completed].TrySetResult(true);
                    }

                    completed++;
                }
            }
            catch (OperationCanceledException) { }
            finally
            {
                await subscription.DisposeAsync();
            }
        });

        return sources;
    }

    private static async IAsyncEnumerable<IMessage> ToAsyncEnumerable(
        IEnumerable<IMessage> messages,
        [EnumeratorCancellation] CancellationToken ct = default
    )
    {
        foreach (var msg in messages)
        {
            ct.ThrowIfCancellationRequested();
            yield return msg;
            await Task.Yield();
        }
    }
}
