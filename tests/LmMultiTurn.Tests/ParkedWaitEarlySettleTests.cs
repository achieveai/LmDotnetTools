using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.Triggers;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace LmMultiTurn.Tests;

/// <summary>
/// Bug #6: a run parked on <c>Wait</c> used to refuse anything that was not a pure
/// <see cref="NotifyMessage"/> batch, exactly as a parked <c>AskUserQuestion</c> did before bug #5
/// was fixed. A parked wait now degrades the same way - it settles early with its own placeholder so
/// the interrupting turn can run, and the trigger's real result is redirected into the conversation
/// when it finally fires.
///
/// <para>
/// The difference from a question is the <b>curated</b> wake policy. Nobody is standing by for a
/// wait, so only input somebody is waiting on ends the park; background chatter is folded into
/// history under the parked run exactly as before and the wait stays armed.
/// </para>
/// </summary>
public class ParkedWaitEarlySettleTests
{
    private readonly Mock<IStreamingAgent> _mockAgent = new();
    private readonly Mock<ILogger<MultiTurnAgentLoop>> _loggerMock = new();

    private const string WaitCallId = "tc_wait";

    private static string WaitArgs() =>
        JsonSerializer.Serialize(
            new
            {
                kind = "host_event",
                args = new { },
                timeout = "10m",
                label = "the build",
            }
        );

    private static ToolCallMessage WaitCall() =>
        new()
        {
            FunctionName = WaitToolProvider.WaitToolName,
            FunctionArgs = WaitArgs(),
            ToolCallId = WaitCallId,
            Role = Role.Assistant,
        };

    private static NotifyMessage Notify(string kind, string detail) =>
        NotifyMessage.Create(kind, detail: detail, sourceToolName: "Agent", sourceToolCallId: "call-x");

    // ---------------------------------------------------------------- criterion (a)

    [Fact]
    public async Task UserTextMessage_WhileParkedOnWait_SettlesEarly_AndOneTurnCarriesThePairAndTheMessage()
    {
        var fixture = await ParkOnWaitAsync();

        await fixture.Loop.SendAsync([new TextMessage { Text = "stop waiting, do X", Role = Role.User }]);
        await fixture.Collector.WaitForCompletionsAsync(2);

        (await fixture.Loop.GetDeferredToolCallsAsync()).Should().BeEmpty();
        fixture.Requests.Should().HaveCount(2, "the interrupting input is carried in ONE turn, not two");

        var request = fixture.Requests[^1];
        var settled = AskUserQuestionEarlySettleTests
            .ExtractResults(request)
            .Should()
            .ContainSingle(r => r.ToolCallId == WaitCallId)
            .Subject;
        settled.IsDeferred.Should().BeFalse();
        settled.IsError.Should().BeFalse();
        settled.Result.Should().Contain(EarlySettlePlaceholders.EarlySettleStatus);
        settled.Result.Should().Contain(WaitToolProvider.EarlySettlePlaceholder);
        request.OfType<TextMessage>().Should().Contain(t => t.Text == "stop waiting, do X");

        fixture.Collector.Snapshot().OfType<RunCompletedMessage>().Should().NotContain(m => m.IsError);
        await fixture.DisposeAsync();
    }

    [Fact]
    public async Task PeerAgentMessage_WhileParkedOnWait_SettlesEarly_AndTheRequestCarriesACompletePair()
    {
        var fixture = await ParkOnWaitAsync();

        var peer = AgentMessage.Create(
            messageId: "am-1",
            agentMessageType: AgentMessageType.Question,
            fromAgentId: "agent-b",
            fromName: "Bee",
            body: "are you still waiting?"
        );
        await fixture.Loop.SendAsync([peer]);
        await fixture.Collector.WaitForCompletionsAsync(2);

        (await fixture.Loop.GetDeferredToolCallsAsync()).Should().BeEmpty();

        var request = fixture.Requests[^1];
        AskUserQuestionEarlySettleTests
            .ExtractResults(request)
            .Should()
            .ContainSingle(r => r.ToolCallId == WaitCallId)
            .Which.Result.Should()
            .Contain(EarlySettlePlaceholders.EarlySettleStatus);
        request.OfType<AgentMessage>().Should().ContainSingle(m => m.MessageId == "am-1");

        fixture.Collector.Snapshot().OfType<RunCompletedMessage>().Should().NotContain(m => m.IsError);
        await fixture.DisposeAsync();
    }

    // ---------------------------------------------------------------- criterion (c), wake half

    [Theory]
    [InlineData(NotifyKinds.SubAgentCompletion)]
    [InlineData(NotifyKinds.DescendantQuestion)]
    [InlineData(NotifyKinds.WorkflowCompletion)]
    public async Task AWakingNotification_WhileParkedOnWait_SettlesEarly_AndIsDeliveredInTheSameTurn(string kind)
    {
        var fixture = await ParkOnWaitAsync();

        await fixture.Loop.SendAsync([Notify(kind, "something finished")]);
        await fixture.Collector.WaitForCompletionsAsync(2);

        (await fixture.Loop.GetDeferredToolCallsAsync()).Should().BeEmpty();

        var request = fixture.Requests[^1];
        AskUserQuestionEarlySettleTests
            .ExtractResults(request)
            .Should()
            .ContainSingle(r => r.ToolCallId == WaitCallId)
            .Which.Result.Should()
            .Contain(EarlySettlePlaceholders.EarlySettleStatus);
        request.OfType<NotifyMessage>().Should().ContainSingle(n => n.NotifyKind == kind);

        fixture.Collector.Snapshot().OfType<RunCompletedMessage>().Should().NotContain(m => m.IsError);
        await fixture.DisposeAsync();
    }

    // ---------------------------------------------------------------- criterion (b)

    [Theory]
    [InlineData(NotifyKinds.TodoNudge)]
    [InlineData(NotifyKinds.TodoDigest)]
    [InlineData(NotifyKinds.ContextDiscovery)]
    [InlineData(NotifyKinds.ClientNotification)]
    public async Task ANonWakingNotification_WhileParkedOnWait_IsFoldedIn_AndTheWaitStaysDeferred(string kind)
    {
        var fixture = await ParkOnWaitAsync();

        await fixture.Loop.SendAsync([Notify(kind, "background chatter")]);

        await AchieveAi.LmDotnetTools.LmTestUtils.Wait.UntilAsync(
            () =>
                fixture.Collector.Snapshot().OfType<NotifyMessage>().Any(n => n.Detail == "background chatter")
                || fixture.Requests.Count > 1
                || fixture.Collector.Snapshot().OfType<RunCompletedMessage>().Any(m => m.IsError),
            because: "the parked loop folded the notification into history and published its pill live"
        );

        // The cheapest ending still wins: no second turn, no placeholder, the wait stays parked and armed.
        fixture.Requests.Should().HaveCount(1);
        (await fixture.Loop.GetDeferredToolCallsAsync()).Should().ContainSingle(p => p.ToolCallId == WaitCallId);
        fixture.Collector.Snapshot().OfType<RunCompletedMessage>().Should().NotContain(m => m.IsError);
        fixture
            .Loop.GetHistorySnapshot()
            .OfType<ToolCallResultMessage>()
            .Should()
            .ContainSingle(r => r.ToolCallId == WaitCallId)
            .Which.IsDeferred.Should()
            .BeTrue();

        await fixture.DisposeAsync();
    }

    // ---------------------------------------------------------------- criterion (c), target half

    [Fact]
    public async Task TheTriggerFiring_ResolvesTheWaitNormally_AndWritesNoPlaceholder()
    {
        var fixture = await ParkOnWaitAsync();

        await fixture.Manual.FireAsync("host-payload");
        await fixture.Collector.WaitForCompletionsAsync(2);

        // The event the wait was armed on is its own ending: the real payload resolves the call, and
        // nothing was ever settled early.
        var resolved = fixture
            .Loop.GetHistorySnapshot()
            .OfType<ToolCallResultMessage>()
            .Should()
            .ContainSingle(r => r.ToolCallId == WaitCallId)
            .Subject;
        resolved.IsDeferred.Should().BeFalse();
        resolved.Result.Should().Contain("host-payload");
        resolved.Result.Should().NotContain(EarlySettlePlaceholders.EarlySettleStatus);

        fixture
            .Loop.GetHistorySnapshot()
            .OfType<TextMessage>()
            .Should()
            .NotContain(t => t.Text.Contains(WaitToolProvider.InjectionTag, StringComparison.Ordinal));

        await fixture.DisposeAsync();
    }

    // ---------------------------------------------------------------- criterion (d)

    [Fact]
    public async Task TheRealWaitResultAfterAnEarlySettle_IsInjectedAsItsOwnTurn_NotRefused()
    {
        var fixture = await ParkOnWaitAsync();

        await fixture.Loop.SendAsync([new TextMessage { Text = "never mind the build", Role = Role.User }]);
        await fixture.Collector.WaitForCompletionsAsync(2);

        // The trigger is still armed, so its payload arrives after the call has already been closed.
        await fixture.Manual.FireAsync("host-payload");
        await fixture.Collector.WaitForCompletionsAsync(3);

        // Exactly one tool_result for the call: the redirect must not append a second one.
        AskUserQuestionEarlySettleTests
            .ExtractResults(fixture.Loop.GetHistorySnapshot())
            .Where(r => r.ToolCallId == WaitCallId)
            .Should()
            .ContainSingle();

        var injected = fixture
            .Loop.GetHistorySnapshot()
            .OfType<TextMessage>()
            .Should()
            .ContainSingle(t => t.Text.Contains(WaitCallId, StringComparison.Ordinal))
            .Subject;
        injected.Text.Should().Contain(WaitToolProvider.InjectionTag, "a timer firing is not an answer from a human");
        injected.Text.Should().Contain("host-payload");
        injected.Text.Should().Contain("host_event", "the injected turn restates the wait it answers");
        injected.Role.Should().Be(Role.User);

        await fixture.DisposeAsync();
    }

    // ---------------------------------------------------------------- the policy's vocabulary

    /// <summary>
    /// The wake policy read directly, including the one case the loop-level tests cannot reach: a
    /// notify-mode wait firing is a <see cref="TextMessage"/> like a human turn is, and only the queue
    /// entry it arrived on tells them apart.
    /// </summary>
    [Fact]
    public void ATriggerFireLooksLikeAHumanTurnButDoesNotWake()
    {
        var human = new TextMessage { Text = "<trigger>\nfired\n</trigger>", Role = Role.User };

        EarlySettlePlaceholders
            .WakesAParkedWait(
                new ParkedInterruptionBatch(
                    [new ParkedInterruption(human, IsTriggerFire: false)],
                    AllNotifications: false
                )
            )
            .Should()
            .BeTrue();

        EarlySettlePlaceholders
            .WakesAParkedWait(
                new ParkedInterruptionBatch(
                    [new ParkedInterruption(human, IsTriggerFire: true)],
                    AllNotifications: false
                )
            )
            .Should()
            .BeFalse();
    }

    /// <summary>A batch wakes when ANY message in it does: a nudge cannot hide a human message.</summary>
    [Fact]
    public void AMixedBatchWakesOnTheMessageThatMatters()
    {
        var batch = new ParkedInterruptionBatch(
            [
                new ParkedInterruption(Notify(NotifyKinds.TodoNudge, "nudge"), IsTriggerFire: false),
                new ParkedInterruption(new TextMessage { Text = "hello", Role = Role.User }, IsTriggerFire: false),
            ],
            AllNotifications: false
        );

        EarlySettlePlaceholders.WakesAParkedWait(batch).Should().BeTrue();
    }

    // ---------------------------------------------------------------- harness

    /// <summary>
    /// Turn 1 parks on a <c>Wait</c> armed on a manually fired host event; every later turn answers
    /// with plain text. Returns once the wait is confirmed outstanding.
    /// </summary>
    private async Task<Fixture> ParkOnWaitAsync()
    {
        var manual = new ManualTriggerSource();
        var options = new TriggerOptions
        {
            AdditionalRegistrations =
            [
                new TriggerSourceRegistration
                {
                    Kind = "host_event",
                    Description = "wait for a host-fired event",
                    ArgsSchema = "{}",
                    Capabilities = new TriggerCapabilities(true, false, false),
                    Source = manual,
                },
            ],
        };

        var requests = new List<List<IMessage>>();
        var gate = new object();
        var turn = 0;
        _mockAgent
            .Setup(a =>
                a.GenerateReplyStreamingAsync(
                    It.IsAny<IEnumerable<IMessage>>(),
                    It.IsAny<GenerateReplyOptions>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Returns<IEnumerable<IMessage>, GenerateReplyOptions, CancellationToken>(
                (msgs, _, _) =>
                {
                    int index;
                    lock (gate)
                    {
                        requests.Add([.. msgs]);
                        index = ++turn;
                    }

                    return Task.FromResult(
                        index == 1
                            ? AskUserQuestionEarlySettleTests.ToAsyncEnumerable([WaitCall()])
                            : AskUserQuestionEarlySettleTests.ToAsyncEnumerable([
                                new TextMessage { Text = $"ack {index}", Role = Role.Assistant },
                            ])
                    );
                }
            );

        var loop = new MultiTurnAgentLoop(
            _mockAgent.Object,
            new FunctionRegistry(),
            "parked-wait-thread",
            logger: _loggerMock.Object,
            triggerOptions: options
        );

        var cts = new CancellationTokenSource();
        _ = loop.RunAsync(cts.Token);
        var collector = new LoopMessageCollector(loop, cts.Token);

        await loop.SendAsync([new TextMessage { Text = "watch the build", Role = Role.User }]);
        await collector.WaitForCompletionsAsync(1);
        (await loop.GetDeferredToolCallsAsync()).Should().ContainSingle(p => p.ToolCallId == WaitCallId);

        return new Fixture(loop, collector, cts, requests, gate, manual);
    }

    private sealed record Fixture(
        MultiTurnAgentLoop Loop,
        LoopMessageCollector Collector,
        CancellationTokenSource Cts,
        List<List<IMessage>> RawRequests,
        object Gate,
        ManualTriggerSource Manual
    )
    {
        public IReadOnlyList<List<IMessage>> Requests
        {
            get
            {
                lock (Gate)
                {
                    return [.. RawRequests];
                }
            }
        }

        public async Task DisposeAsync()
        {
            await Cts.CancelAsync();
            await Loop.DisposeAsync();
            Cts.Dispose();
        }
    }

    /// <summary>A trigger source the test fires by hand, standing in for any host-owned event.</summary>
    private sealed class ManualTriggerSource : ITriggerSource
    {
        private volatile ITriggerEventSink? _sink;

        public ValueTask<IArmedTrigger> ArmAsync(
            TriggerArmRequest request,
            ITriggerEventSink eventSink,
            CancellationToken cancellationToken
        )
        {
            _sink = eventSink;
            return ValueTask.FromResult<IArmedTrigger>(new Handle(request.WaitId));
        }

        public async Task FireAsync(string payload)
        {
            var sink = _sink;
            if (sink != null)
            {
                await sink.FireAsync(new TriggerFireEvent(payload), CancellationToken.None);
            }
        }

        private sealed class Handle(string waitId) : IArmedTrigger
        {
            public string WaitId => waitId;

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
