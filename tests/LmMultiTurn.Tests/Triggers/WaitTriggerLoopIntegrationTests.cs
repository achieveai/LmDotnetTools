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
/// End-to-end tests wiring the Wait/trigger primitive through <see cref="MultiTurnAgentLoop"/>:
/// the LLM calls <c>Wait</c>, the run parks on the deferral, the trigger fires, the run
/// auto-resumes, and the LLM's next turn sees the resolved payload. Covers the built-in timer,
/// a host-registered external source (via the seam), and restart reconciliation.
/// </summary>
public class WaitTriggerLoopIntegrationTests
{
    private readonly Mock<IStreamingAgent> _mockAgent = new();
    private readonly Mock<ILogger<MultiTurnAgentLoop>> _loggerMock = new();

    private static string WaitArgs(object body) => JsonSerializer.Serialize(body);

    [Fact]
    public async Task Wait_Timer_ParksThenAutoResumes_WithFiredPayload()
    {
        // Turn 1: the LLM asks to wait on a one-shot timer. Turn 2 (after the timer fires and the run
        // auto-resumes) returns final text and must see the resolved "fired" payload.
        //
        // The delay must comfortably exceed the window between firstDone completing and the parked-state
        // assertions (callCount==1 / deferred registered) executing. A 150ms delay was comparable to a
        // single thread-pool scheduling hiccup: under full-suite load the timer could fire and auto-resume
        // (callCount->2, deferred cleared) before those assertions ran, failing intermittently. 1s is
        // robust — the parked assertions take microseconds, and both they and the timer-fire callback share
        // the same thread-pool/timer queue, so any load that delays the assertions delays the fire too.
        var waitCall = new ToolCallMessage
        {
            FunctionName = WaitToolProvider.WaitToolName,
            FunctionArgs = WaitArgs(new { kind = "timer", args = new { delay = "1s" }, timeout = "10m" }),
            ToolCallId = "tc_wait",
            Role = Role.Assistant,
        };
        var finalText = new TextMessage { Text = "resumed", Role = Role.Assistant };

        var callCount = 0;
        IEnumerable<IMessage>? secondCallMessages = null;
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
                secondCallMessages = [.. msgs];
                return Task.FromResult(ToAsyncEnumerable([finalText]));
            });

        await using var loop = BuildLoop(new TriggerOptions());
        using var cts = new CancellationTokenSource();
        _ = loop.RunAsync(cts.Token);

        var (firstDone, secondDone) = SubscribeForTwoRuns(loop, cts.Token);

        await loop.SendAsync([new TextMessage { Text = "sleep then continue", Role = Role.User }]);
        await firstDone.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Parked: exactly one LLM call so far, and the Wait is registered as deferred.
        callCount.Should().Be(1);
        (await loop.GetDeferredToolCallsAsync()).Should().ContainSingle(p => p.ToolCallId == "tc_wait");

        // The timer fires (~150ms) and auto-resumes the run.
        await secondDone.Task.WaitAsync(TimeSpan.FromSeconds(5));
        callCount.Should().Be(2);

        var resolved = ExtractResults(secondCallMessages!).Single(m => m.ToolCallId == "tc_wait");
        ReadStatus(resolved.Result).Should().Be("fired");
        (await loop.GetDeferredToolCallsAsync()).Should().BeEmpty();

        await cts.CancelAsync();
    }

    [Fact]
    public async Task Wait_ExternalHostSource_ArmsAndFires_ThroughTheSeam()
    {
        // Proves a host can register a brand-new trigger kind with no loop change: the LLM waits on
        // it, the host fires it out-of-band, and the run resumes with the fired payload.
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
        var finalText = new TextMessage { Text = "done", Role = Role.Assistant };

        var callCount = 0;
        IEnumerable<IMessage>? secondCallMessages = null;
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
                secondCallMessages = [.. msgs];
                return Task.FromResult(ToAsyncEnumerable([finalText]));
            });

        await using var loop = BuildLoop(options);
        using var cts = new CancellationTokenSource();
        _ = loop.RunAsync(cts.Token);

        var (firstDone, secondDone) = SubscribeForTwoRuns(loop, cts.Token);

        await loop.SendAsync([new TextMessage { Text = "wait for host", Role = Role.User }]);
        await firstDone.Task.WaitAsync(TimeSpan.FromSeconds(5));

        (await loop.GetDeferredToolCallsAsync()).Should().ContainSingle(p => p.ToolCallId == "tc_host");

        // Host fires the external event; the parked run resumes.
        await manual.FireAsync("host-payload");

        await secondDone.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var resolved = ExtractResults(secondCallMessages!).Single(m => m.ToolCallId == "tc_host");
        ReadStatus(resolved.Result).Should().Be("fired");
        resolved.Result.Should().Contain("host-payload");

        await cts.CancelAsync();
    }

    [Fact]
    public async Task Wait_Timer_RejectedKind_DoesNotPark()
    {
        // An unknown kind is a fail-fast Resolved error (not a park): the run continues in the same
        // turn and the LLM sees the rejection so it can self-correct.
        var waitCall = new ToolCallMessage
        {
            FunctionName = WaitToolProvider.WaitToolName,
            FunctionArgs = WaitArgs(new { kind = "does_not_exist", args = new { }, timeout = "10m" }),
            ToolCallId = "tc_bad",
            Role = Role.Assistant,
        };
        var finalText = new TextMessage { Text = "recovered", Role = Role.Assistant };

        var callCount = 0;
        _mockAgent
            .Setup(a => a.GenerateReplyStreamingAsync(
                It.IsAny<IEnumerable<IMessage>>(),
                It.IsAny<GenerateReplyOptions>(),
                It.IsAny<CancellationToken>()))
            .Returns<IEnumerable<IMessage>, GenerateReplyOptions, CancellationToken>((_, _, _) =>
            {
                callCount++;
                return callCount == 1
                    ? Task.FromResult(ToAsyncEnumerable([waitCall]))
                    : Task.FromResult(ToAsyncEnumerable([finalText]));
            });

        await using var loop = BuildLoop(new TriggerOptions());
        using var cts = new CancellationTokenSource();
        _ = loop.RunAsync(cts.Token);

        var messages = new List<IMessage>();
        await foreach (var msg in loop.ExecuteRunAsync(
            new UserInput([new TextMessage { Text = "go", Role = Role.User }]), cts.Token))
        {
            messages.Add(msg);
        }

        // No deferral: the rejection resolved inline and the run ran to a normal completion.
        (await loop.GetDeferredToolCallsAsync()).Should().BeEmpty();
        var result = messages.OfType<ToolCallResultMessage>().Single(m => m.ToolCallId == "tc_bad");
        result.IsError.Should().BeTrue();
        result.Result.Should().Contain("unknown_kind");

        await cts.CancelAsync();
    }

    [Fact]
    public async Task RestoredTimerWait_ExpiredWhileOffline_ResolvesOnRecover()
    {
        // A previous process parked on a timer Wait then exited. On restart the reconcile pass must
        // resolve it (never leave the deferred tool call hanging).
        var threadId = "trigger-restore";
        var runId = "run_prev";
        var generationId = "gen_prev";
        var store = new InMemoryConversationStore();

        var toolCall = new ToolCallMessage
        {
            ToolCallId = "tc_restore",
            FunctionName = WaitToolProvider.WaitToolName,
            FunctionArgs = WaitArgs(new { kind = "timer", args = new { }, timeout = "1m" }),
            Role = Role.Assistant,
            FromAgent = "test",
            GenerationId = generationId,
            RunId = runId,
        };
        var deferred = new ToolCallResultMessage
        {
            ToolCallId = "tc_restore",
            ToolName = WaitToolProvider.WaitToolName,
            Result = string.Empty,
            IsDeferred = true,
            // Armed 30 minutes ago with a 1-minute timeout → long elapsed.
            DeferredAt = DateTimeOffset.UtcNow.AddMinutes(-30).ToUnixTimeMilliseconds(),
            Role = Role.User,
            GenerationId = generationId,
            RunId = runId,
        };

        await store.AppendMessagesAsync(threadId,
        [
            MessagePersistenceConverter.ToPersistedMessage(toolCall, threadId, runId),
            MessagePersistenceConverter.ToPersistedMessage(deferred, threadId, runId),
        ]);
        await store.SaveMetadataAsync(threadId, new ThreadMetadata
        {
            ThreadId = threadId,
            LatestRunId = runId,
            LastUpdated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        });

        // Any auto-resumed run just returns final text.
        _mockAgent
            .Setup(a => a.GenerateReplyStreamingAsync(
                It.IsAny<IEnumerable<IMessage>>(),
                It.IsAny<GenerateReplyOptions>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.FromResult(ToAsyncEnumerable([new TextMessage { Text = "back", Role = Role.Assistant }])));

        await using var loop = new MultiTurnAgentLoop(
            _mockAgent.Object,
            new FunctionRegistry(),
            threadId,
            store: store,
            logger: _loggerMock.Object,
            triggerOptions: new TriggerOptions());

        (await loop.RecoverAsync()).Should().BeTrue();

        // The reconcile re-armed an already-elapsed timer; it resolves the parked wait shortly after.
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if ((await loop.GetDeferredToolCallsAsync()).Count == 0)
            {
                break;
            }
            await Task.Delay(50);
        }

        (await loop.GetDeferredToolCallsAsync()).Should().BeEmpty("a restored timer wait must not be left hanging");
    }

    private MultiTurnAgentLoop BuildLoop(TriggerOptions options) => new(
        _mockAgent.Object,
        new FunctionRegistry(),
        "trigger-thread",
        logger: _loggerMock.Object,
        triggerOptions: options);

    private static (TaskCompletionSource<bool> First, TaskCompletionSource<bool> Second) SubscribeForTwoRuns(
        MultiTurnAgentLoop loop, CancellationToken ct)
    {
        var first = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = 0;
        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (var msg in loop.SubscribeAsync(ct))
                {
                    if (msg is RunCompletedMessage)
                    {
                        completed++;
                        if (completed == 1)
                        {
                            first.TrySetResult(true);
                        }
                        else if (completed == 2)
                        {
                            second.TrySetResult(true);
                        }
                    }
                }
            }
            catch (OperationCanceledException) { }
        }, ct);
        return (first, second);
    }

    private static string ReadStatus(string payloadJson)
    {
        using var doc = JsonDocument.Parse(payloadJson);
        return doc.RootElement.GetProperty("status").GetString()!;
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

    /// <summary>A host-registered source whose fire the test triggers out-of-band.</summary>
    private sealed class ManualLoopTriggerSource : ITriggerSource
    {
        private volatile ITriggerEventSink? _sink;

        public ValueTask<IArmedTrigger> ArmAsync(TriggerArmRequest request, ITriggerEventSink eventSink, CancellationToken cancellationToken)
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
