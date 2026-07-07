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
/// Locks in locked decision #1 end-to-end: a notify-mode trigger fire NEVER interrupts an
/// in-flight generation, a running tool call, or another wait's park — it always queues behind
/// the current turn and is delivered as a fresh <c>&lt;trigger&gt;</c>-tagged user turn through
/// the loop's ordinary queue gate. Task 5 of the Wait/trigger follow-ups (#140).
/// </summary>
/// <remarks>
/// The behavior under test is already implemented by Tasks 2-4 (the mid-run poll in
/// <c>ExecuteRunTurnsAsync</c>, the precondition guard in <c>ExecuteTurnAsync</c>, and notify
/// mode's immediate "armed" acknowledgment in <see cref="WaitToolProvider"/>). These tests assert
/// on final history ORDERING, never on wall-clock timing: each scenario gates a genuinely
/// in-flight operation (an open LLM stream or a running tool handler) behind a
/// <see cref="TaskCompletionSource{TResult}"/> so a fire issued while it is open can be proven not
/// to preempt it.
/// </remarks>
public class NotifyOrderingTests
{
    private readonly Mock<IStreamingAgent> _mockAgent = new();
    private readonly Mock<ILogger<MultiTurnAgentLoop>> _loggerMock = new();

    private static string WaitArgs(object body) => JsonSerializer.Serialize(body);

    [Fact]
    public async Task Fire_DuringActiveGeneration_QueuesBehindCurrentTurn()
    {
        // Turn 1 arms a notify wait (2 provider calls, no deferral, run 1 completes). Run 2's
        // single-turn generation is gated open with a TaskCompletionSource so it is genuinely
        // in-flight (no message yielded yet) when we fire. Because that turn has no tool calls,
        // ExecuteRunTurnsAsync breaks immediately once it completes — the fire, already queued,
        // is only observed by the outer run loop as a brand-new run (run 3) once run 2 is done.
        var manual = new ManualTriggerSource();
        var options = ManualNotifyOptions(manual);

        var waitCall = new ToolCallMessage
        {
            FunctionName = WaitToolProvider.WaitToolName,
            FunctionArgs = WaitArgs(new { kind = "manual", mode = "notify", timeout = "1h" }),
            ToolCallId = "tc_notify",
            Role = Role.Assistant,
        };

        var generationStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseGeneration = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var callCount = 0;
        _mockAgent
            .Setup(a => a.GenerateReplyStreamingAsync(
                It.IsAny<IEnumerable<IMessage>>(),
                It.IsAny<GenerateReplyOptions>(),
                It.IsAny<CancellationToken>()))
            .Returns<IEnumerable<IMessage>, GenerateReplyOptions, CancellationToken>((_, _, _) =>
            {
                callCount++;
                return callCount switch
                {
                    1 => Task.FromResult(ToAsyncEnumerable([waitCall])),
                    2 => Task.FromResult(ToAsyncEnumerable(
                        [new TextMessage { Text = "armed", Role = Role.Assistant }])),
                    3 => Task.FromResult(GatedAsyncEnumerable(
                        generationStarted,
                        releaseGeneration.Task,
                        [new TextMessage { Text = "gated reply", Role = Role.Assistant }])),
                    _ => Task.FromResult(ToAsyncEnumerable(
                        [new TextMessage { Text = $"handled {callCount}", Role = Role.Assistant }])),
                };
            });

        const string threadId = "ordering-active-generation";
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

        var runsCompleted = SubscribeForRunCompletions(loop, cts.Token, expectedCount: 3);

        await loop.SendAsync([new TextMessage { Text = "arm the notify wait", Role = Role.User }]);
        await runsCompleted[0].Task.WaitAsync(TimeSpan.FromSeconds(5));
        manual.Sinks.Should().ContainKey("tc_notify");

        await loop.SendAsync([new TextMessage { Text = "start generating", Role = Role.User }]);
        await generationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Fire while run 2's generation is still open — nothing has been yielded yet.
        await manual.Sinks["tc_notify"].FireAsync(new TriggerFireEvent("mid-gen"), cts.Token);

        // Let the gated generation finish; run 2 completes with no tool calls.
        releaseGeneration.SetResult(true);
        await runsCompleted[1].Task.WaitAsync(TimeSpan.FromSeconds(5));

        // The queued fire drives its own run once run 2 has fully completed.
        await runsCompleted[2].Task.WaitAsync(TimeSpan.FromSeconds(5));

        await cts.CancelAsync();

        var history = await WaitForHistoryAsync(
            store,
            threadId,
            h => h.OfType<TextMessage>().Any(m => m.Role == Role.User && m.Text.Contains("<trigger>")));

        var gatedReplyIdx = IndexOfTextMessage(history, Role.Assistant, "gated reply");
        var triggerIdx = IndexOfTextMessage(history, Role.User, "<trigger>");

        gatedReplyIdx.Should().BeGreaterThan(-1, "run 2's gated assistant reply must land in history");
        triggerIdx.Should().BeGreaterThan(
            gatedReplyIdx,
            "the fire happened mid-generation but must queue behind the in-flight turn's assistant message, never interrupt it");
    }

    [Fact]
    public async Task Fire_DuringToolExecution_QueuesBehindToolResult()
    {
        // Run 2's turn 1 calls a slow tool whose handler blocks on a TaskCompletionSource. We fire
        // while the handler is still running (mid-execution), then release it. Because the turn
        // HAD a tool call, ExecuteRunTurnsAsync does not break — it polls for new input before the
        // next turn, drains the already-queued fire, and folds it into THIS run as turn 2, ahead
        // of a fresh LLM call. Final history must still show the tool's result before the fire.
        var manual = new ManualTriggerSource();
        var registry = new FunctionRegistry();

        var toolStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseTool = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        registry.AddFunction(
            new FunctionContract { Name = "SlowTool", Description = "test slow tool", Parameters = [] },
            async (_, _, _) =>
            {
                toolStarted.TrySetResult(true);
                await releaseTool.Task;
                return ToolHandlerResult.FromText("tool done");
            });

        var options = ManualNotifyOptions(manual);

        var waitCall = new ToolCallMessage
        {
            FunctionName = WaitToolProvider.WaitToolName,
            FunctionArgs = WaitArgs(new { kind = "manual", mode = "notify", timeout = "1h" }),
            ToolCallId = "tc_notify",
            Role = Role.Assistant,
        };
        var slowToolCall = new ToolCallMessage
        {
            FunctionName = "SlowTool",
            FunctionArgs = "{}",
            ToolCallId = "tc_slow",
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
                return callCount switch
                {
                    1 => Task.FromResult(ToAsyncEnumerable([waitCall])),
                    2 => Task.FromResult(ToAsyncEnumerable(
                        [new TextMessage { Text = "armed", Role = Role.Assistant }])),
                    3 => Task.FromResult(ToAsyncEnumerable([slowToolCall])),
                    _ => Task.FromResult(ToAsyncEnumerable(
                        [new TextMessage { Text = $"handled {callCount}", Role = Role.Assistant }])),
                };
            });

        const string threadId = "ordering-tool-execution";
        var store = new InMemoryConversationStore();

        await using var loop = new MultiTurnAgentLoop(
            _mockAgent.Object,
            registry,
            threadId,
            store: store,
            logger: _loggerMock.Object,
            triggerOptions: options);

        using var cts = new CancellationTokenSource();
        _ = loop.RunAsync(cts.Token);

        var runsCompleted = SubscribeForRunCompletions(loop, cts.Token, expectedCount: 2);

        await loop.SendAsync([new TextMessage { Text = "arm the notify wait", Role = Role.User }]);
        await runsCompleted[0].Task.WaitAsync(TimeSpan.FromSeconds(5));
        manual.Sinks.Should().ContainKey("tc_notify");

        await loop.SendAsync([new TextMessage { Text = "run the slow tool", Role = Role.User }]);
        await toolStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Fire while the tool handler is still executing (blocked on releaseTool).
        await manual.Sinks["tc_notify"].FireAsync(new TriggerFireEvent("mid-tool"), cts.Token);

        // Let the tool finish. Run 2 folds the already-queued fire into a second turn of the
        // SAME run rather than ending — it must still land after the tool's own result.
        releaseTool.SetResult(true);
        await runsCompleted[1].Task.WaitAsync(TimeSpan.FromSeconds(5));

        await cts.CancelAsync();

        var history = await WaitForHistoryAsync(
            store,
            threadId,
            h => h.OfType<TextMessage>().Any(m => m.Role == Role.User && m.Text.Contains("<trigger>")));

        var toolResultIdx = IndexOfToolResult(history, "tc_slow");
        var triggerIdx = IndexOfTextMessage(history, Role.User, "<trigger>");

        toolResultIdx.Should().BeGreaterThan(-1, "the slow tool's result must land in history");
        triggerIdx.Should().BeGreaterThan(
            toolResultIdx,
            "the fire happened mid-tool-execution but must queue behind the tool result, never interrupt it");
    }

    [Fact]
    public async Task Fire_WhileAnotherBlockWaitDeferred_DoesNotResolveTheBlockWait()
    {
        // A single turn arms two waits at once: a block-mode timer (long timeout so it parks as
        // Deferred and never naturally fires during the test) and a notify-mode manual wait. Once
        // parked, we fire the notify wait. Delivery still goes through the ordinary queue gate and
        // drives a new run, but that run's precondition guard refuses to call the LLM at all while
        // the block wait's placeholder is unresolved, so it errors out without touching _deferred.
        // Assert the block wait's tool_call_id is still reported as deferred afterward.
        var manual = new ManualTriggerSource();
        var options = ManualNotifyOptions(manual);

        var blockWaitCall = new ToolCallMessage
        {
            FunctionName = WaitToolProvider.WaitToolName,
            FunctionArgs = WaitArgs(new { kind = "timer", args = new { }, timeout = "10m" }),
            ToolCallId = "tc_block",
            Role = Role.Assistant,
        };
        var notifyWaitCall = new ToolCallMessage
        {
            FunctionName = WaitToolProvider.WaitToolName,
            FunctionArgs = WaitArgs(new { kind = "manual", mode = "notify", timeout = "10m" }),
            ToolCallId = "tc_notify",
            Role = Role.Assistant,
        };

        _mockAgent
            .Setup(a => a.GenerateReplyStreamingAsync(
                It.IsAny<IEnumerable<IMessage>>(),
                It.IsAny<GenerateReplyOptions>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.FromResult(ToAsyncEnumerable([blockWaitCall, notifyWaitCall])));

        const string threadId = "ordering-block-untouched";
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

        var runsCompleted = SubscribeForRunCompletions(loop, cts.Token, expectedCount: 2);

        await loop.SendAsync([new TextMessage { Text = "arm both waits", Role = Role.User }]);
        await runsCompleted[0].Task.WaitAsync(TimeSpan.FromSeconds(5));

        var before = (await loop.GetDeferredToolCallsAsync()).Should()
            .ContainSingle(p => p.ToolCallId == "tc_block").Subject;
        manual.Sinks.Should().ContainKey("tc_notify");

        await manual.Sinks["tc_notify"].FireAsync(new TriggerFireEvent("fire-1"), cts.Token);
        await runsCompleted[1].Task.WaitAsync(TimeSpan.FromSeconds(5));

        await cts.CancelAsync();

        var after = (await loop.GetDeferredToolCallsAsync()).Should()
            .ContainSingle(
                p => p.ToolCallId == "tc_block",
                "a notify fire must never resolve or otherwise touch a parked block wait's deferred tool call")
            .Subject;

        // Presence-by-key is not enough: a regression that mutated the entry in place (e.g.
        // overwrote FunctionArgs/DeferredAtUnixMs/GenerationId while keeping the same key) would
        // slip past a key check. DeferredToolCallInfo is a record, so value-equality proves the
        // entry is byte-for-byte unchanged — every field, not just the id.
        after.Should().Be(
            before,
            "a notify fire must leave the parked block wait's deferred entry entirely unchanged, not merely still-keyed");
    }

    [Fact]
    public async Task MultipleFires_DeliverInFireOrder()
    {
        // Three fires issued back-to-back (each awaited only until its own enqueue completes, not
        // until any resulting run finishes) must still surface as three <trigger> turns in history,
        // in the order they were fired — however many runs the outer loop happens to batch them
        // into. Assert on relative content ordering, not on the number of runs.
        var manual = new ManualTriggerSource();
        var options = ManualNotifyOptions(manual);

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
                return callCount switch
                {
                    1 => Task.FromResult(ToAsyncEnumerable([waitCall])),
                    _ => Task.FromResult(ToAsyncEnumerable(
                        [new TextMessage { Text = $"handled {callCount}", Role = Role.Assistant }])),
                };
            });

        const string threadId = "ordering-multiple-fires";
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

        var runsCompleted = SubscribeForRunCompletions(loop, cts.Token, expectedCount: 1);

        await loop.SendAsync([new TextMessage { Text = "arm the notify wait", Role = Role.User }]);
        await runsCompleted[0].Task.WaitAsync(TimeSpan.FromSeconds(5));
        manual.Sinks.Should().ContainKey("tc_notify");

        var sink = manual.Sinks["tc_notify"];
        await sink.FireAsync(new TriggerFireEvent("fire-1"), cts.Token);
        await sink.FireAsync(new TriggerFireEvent("fire-2"), cts.Token);
        await sink.FireAsync(new TriggerFireEvent("fire-3"), cts.Token);

        var history = await WaitForHistoryAsync(
            store,
            threadId,
            h => h.OfType<TextMessage>().Count(m => m.Role == Role.User && m.Text.Contains("<trigger>")) >= 3,
            timeoutSeconds: 10);

        await cts.CancelAsync();

        var triggerTexts = history
            .OfType<TextMessage>()
            .Where(m => m.Role == Role.User && m.Text.Contains("<trigger>"))
            .Select(m => m.Text)
            .ToList();

        triggerTexts.Should().HaveCount(3);
        var fireOrder = triggerTexts
            .Select(t => t.Contains("fire-1") ? 1 : t.Contains("fire-2") ? 2 : t.Contains("fire-3") ? 3 : 0)
            .ToList();
        fireOrder.Should().Equal([1, 2, 3], "the three fires must be delivered in the order they occurred");
    }

    /// <summary>
    /// Registers <paramref name="manual"/> under the <c>manual</c> kind (block+notify capable) so a
    /// wait with <c>kind: "manual"</c> arms against it. Every scenario wires the same source the
    /// same way — this keeps that boilerplate in one place.
    /// </summary>
    private static TriggerOptions ManualNotifyOptions(ManualTriggerSource manual) => new()
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

    private static async Task<IReadOnlyList<IMessage>> WaitForHistoryAsync(
        InMemoryConversationStore store,
        string threadId,
        Func<IReadOnlyList<IMessage>, bool> condition,
        int timeoutSeconds = 5)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(timeoutSeconds);
        IReadOnlyList<IMessage> history = [];
        while (DateTimeOffset.UtcNow < deadline)
        {
            history = MessagePersistenceConverter.FromPersistedMessages(await store.LoadMessagesAsync(threadId));
            if (condition(history))
            {
                break;
            }
            await Task.Delay(50);
        }
        return history;
    }

    private static int IndexOfTextMessage(IReadOnlyList<IMessage> history, Role role, string substring)
    {
        for (var i = 0; i < history.Count; i++)
        {
            if (history[i] is TextMessage tm && tm.Role == role && tm.Text.Contains(substring, StringComparison.Ordinal))
            {
                return i;
            }
        }
        return -1;
    }

    private static int IndexOfToolResult(IReadOnlyList<IMessage> history, string toolCallId)
    {
        for (var i = 0; i < history.Count; i++)
        {
            if (history[i] is ToolCallResultMessage tcr && tcr.ToolCallId == toolCallId)
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>Yields each message with no gating — completes as soon as it is enumerated.</summary>
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

    /// <summary>
    /// Signals <paramref name="started"/> the instant enumeration begins, then blocks until
    /// <paramref name="gate"/> completes before yielding any message — simulates a genuinely
    /// in-flight (open) LLM stream that a test can prove was not preempted by a mid-flight fire.
    /// </summary>
    private static async IAsyncEnumerable<IMessage> GatedAsyncEnumerable(
        TaskCompletionSource<bool> started,
        Task gate,
        IEnumerable<IMessage> messages,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        started.TrySetResult(true);
        await gate;
        foreach (var msg in messages)
        {
            ct.ThrowIfCancellationRequested();
            yield return msg;
            await Task.Yield();
        }
    }
}
