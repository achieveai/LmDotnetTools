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
/// The original notify-mode <c>Wait</c> tool call still parks as <c>Deferred()</c> today (dispatch
/// not returning a normal result for notify mode is a documented follow-up, see the #140 design
/// spec and the #140 Task-2 commit) and is never resolved through <c>ResolveToolCallAsync</c> — so
/// its deferred placeholder stays in history. That means the run started by an injected trigger
/// envelope can itself fail its "no unresolved deferred tool calls" precondition before ever
/// calling the provider again. This test therefore reads the injected envelope back from a
/// conversation store (populated by the loop's own <c>AddToHistory</c> persistence, which runs
/// before that precondition is even evaluated) rather than from the mock provider's next captured
/// call — the assertion is about queue-gate delivery, not about a subsequent successful LLM turn.
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

        history.OfType<TextMessage>().Should().Contain(
            m => m.Role == Role.User && m.Text.Contains("<trigger>") && m.Text.Contains("fire-1"));
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
            FunctionArgs = WaitArgs(new { kind = "manual", mode = "notify", timeout = "1h" }),
            ToolCallId = "tc_notify",
            Role = Role.Assistant,
        };

        var callCount = 0;
        _mockAgent
            .Setup(a => a.GenerateReplyStreamingAsync(
                It.IsAny<IEnumerable<IMessage>>(),
                It.IsAny<GenerateReplyOptions>(),
                It.IsAny<CancellationToken>()))
            .Returns<IEnumerable<IMessage>, GenerateReplyOptions, CancellationToken>((_, _, _) =>
            {
                callCount++;
                if (callCount == 1)
                {
                    return Task.FromResult(ToAsyncEnumerable([waitCall]));
                }

                var finalText = new TextMessage { Text = $"handled {callCount}", Role = Role.Assistant };
                return Task.FromResult(ToAsyncEnumerable([finalText]));
            });

        const string threadId = "notify-thread";
        var store = new InMemoryConversationStore();

        await using var loop = new MultiTurnAgentLoop(
            _mockAgent.Object,
            new FunctionRegistry(),
            threadId,
            store: store,
            logger: _loggerMock.Object,
            triggerOptions: options);

        using var cts = new CancellationTokenSource();
        _ = loop.RunAsync(cts.Token);

        var runsCompleted = SubscribeForRunCompletions(loop, cts.Token, expectedCount: fireCount + 1);

        await loop.SendAsync([new TextMessage { Text = "arm the notify wait", Role = Role.User }]);
        await runsCompleted[0].Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Parked: the notify-mode Wait call armed but was never resolved through _resolve — it
        // stays a live deferral (armed) while the loop is otherwise idle between fires.
        callCount.Should().Be(1);
        (await loop.GetDeferredToolCallsAsync()).Should().ContainSingle(p => p.ToolCallId == "tc_notify");

        for (var i = 0; i < fireCount; i++)
        {
            var sink = manual.Sinks["tc_notify"];
            await sink.FireAsync(new TriggerFireEvent($"fire-{i + 1}"), cts.Token);
            await runsCompleted[i + 1].Task.WaitAsync(TimeSpan.FromSeconds(5));
        }

        await cts.CancelAsync();

        // AddToHistory persists fire-and-forget, so give the last fire's write a brief window to
        // land after its RunCompletedMessage (which fires whether or not the injected run's own
        // provider call succeeds — see the remarks on this test's precondition caveat).
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        IReadOnlyList<IMessage> history = [];
        while (DateTimeOffset.UtcNow < deadline)
        {
            history = MessagePersistenceConverter.FromPersistedMessages(
                await store.LoadMessagesAsync(threadId));
            if (history.OfType<TextMessage>().Any(
                m => m.Role == Role.User && m.Text.Contains("<trigger>")))
            {
                break;
            }
            await Task.Delay(50);
        }

        return history;
    }

    private static List<TaskCompletionSource<bool>> SubscribeForRunCompletions(
        MultiTurnAgentLoop loop, CancellationToken ct, int expectedCount)
    {
        var sources = Enumerable.Range(0, expectedCount)
            .Select(_ => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously))
            .ToList();
        var completed = 0;
        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (var msg in loop.SubscribeAsync(ct))
                {
                    if (msg is RunCompletedMessage)
                    {
                        var idx = completed;
                        completed++;
                        if (idx < sources.Count)
                        {
                            sources[idx].TrySetResult(true);
                        }
                    }
                }
            }
            catch (OperationCanceledException) { }
        }, ct);
        return sources;
    }

    private static async IAsyncEnumerable<IMessage> ToAsyncEnumerable(
        IEnumerable<IMessage> messages,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var msg in messages)
        {
            ct.ThrowIfCancellationRequested();
            yield return msg;
            await Task.Yield();
        }
    }
}
