using System.Runtime.CompilerServices;
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
/// Loop-level behavior for out-of-band <see cref="NotifyMessage"/> delivery: a notification behaves
/// like any input (wakes an idle agent / injects into a running one), its pill is published live, and
/// — the one edge — a notification arriving while the agent is parked on an unresolved deferral is
/// folded into history and delivered on resume instead of tripping the deferred-guard (RunFailed).
/// </summary>
public class NotifyMessageLoopTests
{
    private readonly Mock<IStreamingAgent> _mockAgent = new();
    private readonly Mock<ILogger<MultiTurnAgentLoop>> _loggerMock = new();

    private static string WaitArgs(object body) => JsonSerializer.Serialize(body);

    [Fact]
    public async Task Notify_WhenIdle_StartsATurn_EnvelopeReachesModel_AndPillPublishedWithPreservedIdentity()
    {
        IEnumerable<IMessage>? sentToModel = null;
        var callCount = 0;
        _mockAgent
            .Setup(a => a.GenerateReplyStreamingAsync(
                It.IsAny<IEnumerable<IMessage>>(),
                It.IsAny<GenerateReplyOptions>(),
                It.IsAny<CancellationToken>()))
            .Returns<IEnumerable<IMessage>, GenerateReplyOptions, CancellationToken>((msgs, _, _) =>
            {
                callCount++;
                sentToModel = [.. msgs];
                return Task.FromResult(ToAsyncEnumerable([new TextMessage { Text = "ack", Role = Role.Assistant }]));
            });

        await using var loop = BuildLoop(new TriggerOptions());
        using var cts = new CancellationTokenSource();
        _ = loop.RunAsync(cts.Token);

        var collector = new MessageCollector(loop, cts.Token);

        var notify = NotifyMessage.Create(
            NotifyKinds.SubAgentCompletion,
            detail: "sub-agent done",
            sourceToolName: "Agent",
            sourceToolCallId: "call-1",
            generationId: "notify:fixed");
        await loop.SendAsync([notify]);
        await collector.WaitForCompletionsAsync(1);

        // Idle notify woke the agent: exactly one LLM turn ran, and the envelope reached the model.
        callCount.Should().Be(1);
        sentToModel.Should().NotBeNull();
        sentToModel!.OfType<NotifyMessage>().Should().ContainSingle()
            .Which.NotifyKind.Should().Be(NotifyKinds.SubAgentCompletion);

        // The pill was published live (not merely persisted) with identity preserved from the producer.
        collector.Snapshot().OfType<NotifyMessage>().Should()
            .ContainSingle(n => n.GenerationId == "notify:fixed" && n.NotifyKind == NotifyKinds.SubAgentCompletion);

        await cts.CancelAsync();
    }

    [Fact]
    public async Task Notify_WhileParkedOnWait_DoesNotRunFailed_AndIsDeliveredOnResume()
    {
        var manual = new ManualLoopTriggerSource();
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

        var waitCall = new ToolCallMessage
        {
            FunctionName = WaitToolProvider.WaitToolName,
            FunctionArgs = WaitArgs(new { kind = "host_event", args = new { }, timeout = "10m" }),
            ToolCallId = "tc_host",
            Role = Role.Assistant,
        };
        var finalText = new TextMessage { Text = "resumed", Role = Role.Assistant };

        var callCount = 0;
        IEnumerable<IMessage>? resumeMessages = null;
        _mockAgent
            .Setup(a => a.GenerateReplyStreamingAsync(
                It.IsAny<IEnumerable<IMessage>>(),
                It.IsAny<GenerateReplyOptions>(),
                It.IsAny<CancellationToken>()))
            .Returns<IEnumerable<IMessage>, GenerateReplyOptions, CancellationToken>((msgs, _, _) =>
            {
                callCount++;
                if (callCount == 1)
                {
                    return Task.FromResult(ToAsyncEnumerable([waitCall]));
                }
                resumeMessages = [.. msgs];
                return Task.FromResult(ToAsyncEnumerable([finalText]));
            });

        await using var loop = BuildLoop(options);
        using var cts = new CancellationTokenSource();
        _ = loop.RunAsync(cts.Token);
        var collector = new MessageCollector(loop, cts.Token);

        // Turn 1: the LLM parks on a host_event Wait.
        await loop.SendAsync([new TextMessage { Text = "wait for host", Role = Role.User }]);
        await collector.WaitForCompletionsAsync(1);
        (await loop.GetDeferredToolCallsAsync()).Should().ContainSingle(p => p.ToolCallId == "tc_host");

        // A background sub-agent completes while the parent is parked → notify arrives.
        var notify = NotifyMessage.Create(
            NotifyKinds.SubAgentCompletion, detail: "bg done", sourceToolName: "Agent", sourceToolCallId: "call-x");
        await loop.SendAsync([notify]);

        // Give the loop time to drain-and-park the notify. It must NOT start a turn (no extra LLM call),
        // must NOT fail the run (no error completion), and the Wait stays deferred.
        await Task.Delay(300);
        callCount.Should().Be(1);
        collector.Snapshot().OfType<RunCompletedMessage>().Should().NotContain(m => m.IsError);
        (await loop.GetDeferredToolCallsAsync()).Should().ContainSingle(p => p.ToolCallId == "tc_host");

        // The host fires → the parked run resumes and turn 2 sees BOTH the resolved Wait and the notify.
        await manual.FireAsync("host-payload");
        await collector.WaitForCompletionsAsync(2);
        callCount.Should().Be(2);

        resumeMessages.Should().NotBeNull();
        resumeMessages!.OfType<NotifyMessage>().Should().ContainSingle(n => n.Detail == "bg done");
        ExtractResults(resumeMessages!).Should().ContainSingle(r => r.ToolCallId == "tc_host");
        collector.Snapshot().OfType<RunCompletedMessage>().Should().NotContain(m => m.IsError);

        await cts.CancelAsync();
    }

    private MultiTurnAgentLoop BuildLoop(TriggerOptions options) => new(
        _mockAgent.Object,
        new FunctionRegistry(),
        "notify-thread",
        logger: _loggerMock.Object,
        triggerOptions: options);

    /// <summary>Collects the loop's published output on a background task for assertions.</summary>
    private sealed class MessageCollector
    {
        private readonly List<IMessage> _messages = [];
        private readonly object _gate = new();
        private volatile int _completions;

        public MessageCollector(MultiTurnAgentLoop loop, CancellationToken ct)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await foreach (var msg in loop.SubscribeAsync(ct))
                    {
                        lock (_gate)
                        {
                            _messages.Add(msg);
                        }

                        if (msg is RunCompletedMessage)
                        {
                            _completions++;
                        }
                    }
                }
                catch (OperationCanceledException) { }
            }, ct);
        }

        public List<IMessage> Snapshot()
        {
            lock (_gate)
            {
                return [.. _messages];
            }
        }

        public async Task WaitForCompletionsAsync(int count)
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
            while (DateTimeOffset.UtcNow < deadline)
            {
                if (_completions >= count)
                {
                    return;
                }

                await Task.Delay(25);
            }

            throw new TimeoutException($"Expected {count} run completion(s); saw {_completions}.");
        }
    }

    private static List<ToolCallResultMessage> ExtractResults(IEnumerable<IMessage> messages) =>
    [
        .. messages.OfType<ToolCallResultMessage>()
            .Concat(messages.OfType<ToolsCallResultMessage>()
                .SelectMany(m => m.ToolCallResults.Select(r => new ToolCallResultMessage
                {
                    ToolCallId = r.ToolCallId,
                    Result = r.Result,
                    IsError = r.IsError,
                }))),
    ];

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

    private sealed class ManualLoopTriggerSource : ITriggerSource
    {
        private volatile ITriggerEventSink? _sink;

        public ValueTask<IArmedTrigger> ArmAsync(
            TriggerArmRequest request, ITriggerEventSink eventSink, CancellationToken cancellationToken)
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
            public string WaitId { get; } = waitId;

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
