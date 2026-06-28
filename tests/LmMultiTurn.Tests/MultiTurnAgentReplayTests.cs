using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;
using FluentAssertions;
using Xunit;

namespace LmMultiTurn.Tests;

/// <summary>
/// Streaming resume: a client that reconnects mid-run (after switching conversations or
/// refreshing) must be able to resume the in-flight stream. The backend run keeps running after
/// the client disconnects (pooled agent), but <see cref="MultiTurnAgentBase.SubscribeAsync"/>
/// historically created a fresh subscriber with NO replay — so a reconnecting client received
/// only messages published after it re-subscribed and the visible stream "froze". These tests
/// pin the replay contract: a subscriber joining mid-run gets the in-flight run's already-published
/// messages first, then live ones; a subscriber joining after completion gets no replay.
/// </summary>
public sealed class MultiTurnAgentReplayTests
{
    private sealed class ReplayTestAgent(string threadId) : MultiTurnAgentBase(threadId, systemPrompt: null, store: null, logger: null)
    {
        // The loop is driven manually in these tests via PublishForTest; never started.
        protected override Task RunLoopAsync(CancellationToken ct) => Task.CompletedTask;

        public ValueTask PublishForTest(IMessage message) => PublishToAllAsync(message, CancellationToken.None);
    }

    private static RunAssignmentMessage Assignment(string threadId, string runId, string genId) =>
        new() { Assignment = new RunAssignment(runId, genId), ThreadId = threadId };

    private static TextUpdateMessage TextDelta(string runId, string genId, string text) =>
        new() { Text = text, Role = Role.Assistant, RunId = runId, GenerationId = genId, MessageOrderIdx = 0 };

    private static ToolCallMessage ToolCall(string runId, string genId, string toolCallId, int orderIdx) =>
        new()
        {
            Role = Role.Assistant,
            RunId = runId,
            GenerationId = genId,
            MessageOrderIdx = orderIdx,
            ToolCallId = toolCallId,
            FunctionName = "get_weather",
            FunctionArgs = "{\"location\":\"Seattle\"}",
        };

    private static ToolCallResultMessage ToolResult(string runId, string genId, string toolCallId, int orderIdx) =>
        new()
        {
            Role = Role.Tool,
            RunId = runId,
            GenerationId = genId,
            MessageOrderIdx = orderIdx,
            ToolCallId = toolCallId,
            Result = "{\"location\":\"Seattle\",\"temperature\":72}",
        };

    [Fact]
    public async Task Subscriber_joining_mid_run_replays_buffered_messages_then_streams_live()
    {
        await using var agent = new ReplayTestAgent("thread-1");
        const string runId = "run-1";
        const string genId = "gen-1";

        // The run is already in flight and has published several messages BEFORE the client
        // (re)connects — exactly the switch-away/refresh window.
        await agent.PublishForTest(Assignment("thread-1", runId, genId));
        await agent.PublishForTest(TextDelta(runId, genId, "Hel"));
        await agent.PublishForTest(TextDelta(runId, genId, "lo"));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var e = agent.SubscribeAsync(cts.Token).GetAsyncEnumerator(cts.Token);

        // The reconnecting subscriber must REPLAY the in-flight run's already-published messages.
        (await e.MoveNextAsync()).Should().BeTrue();
        e.Current.Should().BeOfType<RunAssignmentMessage>();
        (await e.MoveNextAsync()).Should().BeTrue();
        e.Current.Should().BeOfType<TextUpdateMessage>().Which.Text.Should().Be("Hel");
        (await e.MoveNextAsync()).Should().BeTrue();
        e.Current.Should().BeOfType<TextUpdateMessage>().Which.Text.Should().Be("lo");

        // Now the run continues live — the same subscriber must keep receiving.
        await agent.PublishForTest(TextDelta(runId, genId, "!"));
        await agent.PublishForTest(new RunCompletedMessage { CompletedRunId = runId, ThreadId = "thread-1" });

        (await e.MoveNextAsync()).Should().BeTrue();
        e.Current.Should().BeOfType<TextUpdateMessage>().Which.Text.Should().Be("!");
        (await e.MoveNextAsync()).Should().BeTrue();
        e.Current.Should().BeOfType<RunCompletedMessage>();
    }

    [Fact]
    public async Task Subscriber_joining_mid_run_replays_tool_call_and_result()
    {
        // The frozen-tool-pill resume bug needs the replay to carry BOTH the tool call AND its
        // result: a client that switches away mid-tool-call and returns rebuilds the unresolved
        // pill from REST history, then resolves it ONLY if the resumed stream replays the tool
        // call and its result. This pins that the in-flight replay includes tool messages, not
        // just text — the contract the client switch-back/resume render path depends on.
        await using var agent = new ReplayTestAgent("thread-1");
        const string runId = "run-1";
        const string genId = "gen-1";
        const string toolCallId = "call_1";

        // The run issued a tool call and produced its result BEFORE the client (re)connects.
        await agent.PublishForTest(Assignment("thread-1", runId, genId));
        await agent.PublishForTest(ToolCall(runId, genId, toolCallId, orderIdx: 1));
        await agent.PublishForTest(ToolResult(runId, genId, toolCallId, orderIdx: 2));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var e = agent.SubscribeAsync(cts.Token).GetAsyncEnumerator(cts.Token);

        // The reconnecting subscriber must replay the run assignment, the tool call, and its result.
        (await e.MoveNextAsync()).Should().BeTrue();
        e.Current.Should().BeOfType<RunAssignmentMessage>();

        (await e.MoveNextAsync()).Should().BeTrue();
        e.Current.Should().BeOfType<ToolCallMessage>()
            .Which.ToolCallId.Should().Be(toolCallId);

        (await e.MoveNextAsync()).Should().BeTrue();
        e.Current.Should().BeOfType<ToolCallResultMessage>()
            .Which.ToolCallId.Should().Be(toolCallId);

        // The run then completes live and the same subscriber receives it.
        await agent.PublishForTest(new RunCompletedMessage { CompletedRunId = runId, ThreadId = "thread-1" });
        (await e.MoveNextAsync()).Should().BeTrue();
        e.Current.Should().BeOfType<RunCompletedMessage>();
    }

    [Fact]
    public async Task Subscriber_joining_after_run_completed_does_not_replay_the_finished_run()
    {
        await using var agent = new ReplayTestAgent("thread-1");
        const string runId = "run-1";
        const string genId = "gen-1";

        await agent.PublishForTest(Assignment("thread-1", runId, genId));
        await agent.PublishForTest(TextDelta(runId, genId, "Hello"));
        await agent.PublishForTest(new RunCompletedMessage { CompletedRunId = runId, ThreadId = "thread-1" });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var e = agent.SubscribeAsync(cts.Token).GetAsyncEnumerator(cts.Token);

        // No active run ⇒ the first read must NOT synchronously yield the finished run's messages
        // (the client already has those via persisted REST history; replaying them would duplicate).
        var first = e.MoveNextAsync();
        first.IsCompleted.Should().BeFalse("a subscriber joining after completion must not replay the finished run");

        // It only receives genuinely new live messages.
        await agent.PublishForTest(TextDelta("run-2", "gen-2", "new"));
        (await first).Should().BeTrue();
        e.Current.Should().BeOfType<TextUpdateMessage>().Which.Text.Should().Be("new");
    }

    [Fact]
    public async Task Live_and_reconnecting_subscribers_both_receive_every_message_once()
    {
        await using var agent = new ReplayTestAgent("thread-1");
        const string runId = "run-1";
        const string genId = "gen-1";
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        // Subscriber A is live from before the run starts. The first MoveNextAsync registers it
        // synchronously (the lock/register runs before the first await), so publishes below reach it.
        await using var a = agent.SubscribeAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        var aFirst = a.MoveNextAsync();

        await agent.PublishForTest(Assignment("thread-1", runId, genId));
        await agent.PublishForTest(TextDelta(runId, genId, "Hel"));

        (await aFirst).Should().BeTrue();
        a.Current.Should().BeOfType<RunAssignmentMessage>();
        (await a.MoveNextAsync()).Should().BeTrue();
        a.Current.Should().BeOfType<TextUpdateMessage>().Which.Text.Should().Be("Hel");

        // Subscriber B reconnects mid-run and REPLAYS what A already saw live.
        await using var b = agent.SubscribeAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        (await b.MoveNextAsync()).Should().BeTrue();
        b.Current.Should().BeOfType<RunAssignmentMessage>();
        (await b.MoveNextAsync()).Should().BeTrue();
        b.Current.Should().BeOfType<TextUpdateMessage>().Which.Text.Should().Be("Hel");

        // A subsequent live message reaches BOTH exactly once.
        await agent.PublishForTest(TextDelta(runId, genId, "lo"));
        (await a.MoveNextAsync()).Should().BeTrue();
        a.Current.Should().BeOfType<TextUpdateMessage>().Which.Text.Should().Be("lo");
        (await b.MoveNextAsync()).Should().BeTrue();
        b.Current.Should().BeOfType<TextUpdateMessage>().Which.Text.Should().Be("lo");
    }

    [Fact]
    public async Task Concurrent_subscribe_during_active_publishing_delivers_each_message_exactly_once()
    {
        // Exercises the real race the `_replayLock` guards: a subscriber registering WHILE a
        // publisher is actively publishing. With a single serial publisher the messages are totally
        // ordered, so the subscriber must observe a contiguous, gap-free, duplicate-free run made of
        // a replay prefix + a live suffix.
        await using var agent = new ReplayTestAgent("thread-1");
        const string runId = "run-1";
        const string genId = "gen-1";
        const int total = 500;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        await agent.PublishForTest(Assignment("thread-1", runId, genId));

        var publisher = Task.Run(async () =>
        {
            for (var i = 0; i < total; i++)
            {
                await agent.PublishForTest(TextDelta(runId, genId, i.ToString()));
            }

            await agent.PublishForTest(new RunCompletedMessage { CompletedRunId = runId, ThreadId = "thread-1" });
        }, cts.Token);

        var received = new List<int>();
        await foreach (var m in agent.SubscribeAsync(cts.Token))
        {
            if (m is TextUpdateMessage t && int.TryParse(t.Text, out var n))
            {
                received.Add(n);
            }

            if (m is RunCompletedMessage)
            {
                break;
            }
        }

        await publisher;

        received.Should().OnlyHaveUniqueItems("no message may be delivered twice (replay XOR live)");
        received.Should().BeInAscendingOrder("a single serial publisher produces a total order");
        received.Should().Contain(total - 1, "the subscriber must receive through the end of the run");
    }

    [Fact]
    public async Task Replay_buffer_is_capped_so_a_huge_run_does_not_grow_unbounded()
    {
        await using var agent = new ReplayTestAgent("thread-1");
        const string runId = "run-1";
        const string genId = "gen-1";
        const int cap = 10_000; // mirrors MultiTurnAgentBase.MaxReplayBufferSize

        // Assignment fills slot #1; the next `cap` deltas overflow by one, which must be dropped.
        await agent.PublishForTest(Assignment("thread-1", runId, genId));
        for (var i = 0; i < cap; i++)
        {
            await agent.PublishForTest(TextDelta(runId, genId, i.ToString()));
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var e = agent.SubscribeAsync(cts.Token).GetAsyncEnumerator(cts.Token);

        // Drain exactly `cap` replayed messages — the buffer must hold no more than this.
        for (var i = 0; i < cap; i++)
        {
            (await e.MoveNextAsync()).Should().BeTrue();
        }

        // Prove the buffer held EXACTLY `cap` (not cap+1): the next message must be a sentinel
        // published live AFTER subscribing, not the overflowed delta that was dropped.
        await agent.PublishForTest(TextDelta(runId, genId, "SENTINEL"));
        (await e.MoveNextAsync()).Should().BeTrue();
        e.Current.Should()
            .BeOfType<TextUpdateMessage>()
            .Which.Text.Should()
            .Be("SENTINEL", "the in-flight replay buffer is bounded at the cap, so the overflow delta was dropped");
    }
}
