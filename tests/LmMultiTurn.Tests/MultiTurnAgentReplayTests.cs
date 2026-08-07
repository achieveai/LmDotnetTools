using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Models;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.Delivery;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;
using AchieveAi.LmDotnetTools.LmTestUtils.Logging;
using FluentAssertions;
using Microsoft.Extensions.Logging;
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
    private sealed class ReplayTestAgent : MultiTurnAgentBase
    {
        public ReplayTestAgent(
            string threadId,
            int outputChannelCapacity = 1000,
            int maxReplayBufferSize = 10_000,
            long maxReplayBufferBytes = 8L * 1024 * 1024,
            ILogger? logger = null)
            : base(
                threadId,
                systemPrompt: null,
                store: null,
                logger: logger,
                outputChannelCapacity: outputChannelCapacity,
                maxReplayBufferSize: maxReplayBufferSize,
                maxReplayBufferBytes: maxReplayBufferBytes)
        {
        }

        // The loop is driven manually in these tests via PublishForTest; never started.
        protected override Task RunLoopAsync(CancellationToken ct) => Task.CompletedTask;

        public ValueTask PublishForTest(IMessage message) => PublishToAllAsync(message, CancellationToken.None);
    }

    private static RunAssignmentMessage Assignment(string threadId, string runId, string genId) =>
        new() { Assignment = new RunAssignment(runId, genId), ThreadId = threadId };

    private static RunAssignmentMessage InjectedAssignment(string threadId, string runId, string genId) =>
        new() { Assignment = new RunAssignment(runId, genId, WasInjected: true), ThreadId = threadId };

    private static TextUpdateMessage TextDelta(string runId, string genId, string text) =>
        new() { Text = text, Role = Role.Assistant, RunId = runId, GenerationId = genId, MessageOrderIdx = 0 };

    // Canonical (complete) text message — the counterpart CompleteText emits to replace TextDelta as a
    // bridge filler in tests below, since streaming deltas no longer enter the replay bridge at all.
    private static TextMessage CompleteText(string runId, string genId, string text) =>
        new() { Text = text, Role = Role.Assistant, RunId = runId, GenerationId = genId };

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

        // The run is already in flight and has published the assignment, far more streaming deltas
        // than the bridge's default count cap (10,000), and one canonical complete text — all BEFORE
        // the client (re)connects (the switch-away/refresh window). The deltas must never enter the
        // canonical/control bridge at all, so publishing 10,001 of them must not truncate or evict the
        // canonical complete message that follows.
        await agent.PublishForTest(Assignment("thread-1", runId, genId));
        for (var i = 0; i < 10_001; i++)
        {
            await agent.PublishForTest(TextDelta(runId, genId, i.ToString()));
        }

        await agent.PublishForTest(CompleteText(runId, genId, "Hello!"));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var e = agent.SubscribeAsync(cts.Token).GetAsyncEnumerator(cts.Token);

        // The reconnecting subscriber replays ONLY the canonical/control messages — the assignment and
        // the complete text — never the 10,001 streaming deltas that preceded it.
        (await e.MoveNextAsync()).Should().BeTrue();
        e.Current.Should().BeOfType<RunAssignmentMessage>();
        (await e.MoveNextAsync()).Should().BeTrue();
        e.Current.Should().BeOfType<TextMessage>().Which.Text.Should().Be("Hello!");

        // Now the run continues live — the same subscriber must keep receiving NEW deltas too: live
        // fan-out is never filtered, only bridge insertion is.
        await agent.PublishForTest(TextDelta(runId, genId, "new-delta"));
        (await e.MoveNextAsync()).Should().BeTrue();
        e.Current.Should().BeOfType<TextUpdateMessage>().Which.Text.Should().Be("new-delta");
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
        await agent.PublishForTest(CompleteText(runId, genId, "Hel"));

        (await aFirst).Should().BeTrue();
        a.Current.Should().BeOfType<RunAssignmentMessage>();
        (await a.MoveNextAsync()).Should().BeTrue();
        a.Current.Should().BeOfType<TextMessage>().Which.Text.Should().Be("Hel");

        // Subscriber B reconnects mid-run and REPLAYS the canonical message A already saw live.
        await using var b = agent.SubscribeAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        (await b.MoveNextAsync()).Should().BeTrue();
        b.Current.Should().BeOfType<RunAssignmentMessage>();
        (await b.MoveNextAsync()).Should().BeTrue();
        b.Current.Should().BeOfType<TextMessage>().Which.Text.Should().Be("Hel");

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

        // Assignment fills slot #1; the next `cap` CANONICAL messages overflow by one, which must be
        // dropped. Streaming deltas are excluded from the bridge entirely (ReplayMessagePolicy), so the
        // count cap is exercised here with canonical complete-text messages instead of deltas.
        await agent.PublishForTest(Assignment("thread-1", runId, genId));
        for (var i = 0; i < cap; i++)
        {
            await agent.PublishForTest(CompleteText(runId, genId, i.ToString()));
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var e = agent.SubscribeAsync(cts.Token).GetAsyncEnumerator(cts.Token);

        // Drain exactly `cap` replayed messages — the buffer must hold no more than this.
        for (var i = 0; i < cap; i++)
        {
            (await e.MoveNextAsync()).Should().BeTrue();
        }

        // Prove the buffer held EXACTLY `cap` (not cap+1): the next message must be a sentinel
        // published live AFTER subscribing, not the overflowed canonical message that was dropped.
        await agent.PublishForTest(CompleteText(runId, genId, "SENTINEL"));
        (await e.MoveNextAsync()).Should().BeTrue();
        e.Current.Should()
            .BeOfType<TextMessage>()
            .Which.Text.Should()
            .Be("SENTINEL", "the in-flight replay buffer is bounded at the cap, so the overflow message was dropped");
    }

    [Fact]
    public async Task A_stalled_subscriber_neither_blocks_the_publisher_nor_starves_other_subscribers()
    {
        // Regression for the publisher hot-path BLOCKER: a slow/stalled subscriber whose bounded
        // output channel fills must NOT backpressure the live run (or other subscribers). Before the
        // fix, the publisher awaited each subscriber's WriteAsync via Task.WhenAll, so one full channel
        // hung PublishToAllAsync indefinitely; now a full subscriber is dropped instead.
        const int slowSubscriberCapacity = 4;
        await using var agent = new ReplayTestAgent("thread-1", outputChannelCapacity: slowSubscriberCapacity);
        const string runId = "run-1";
        const string genId = "gen-1";
        const int burst = 50; // >> the slow subscriber's capacity, so its channel overflows
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        // Fast subscriber B and slow subscriber A are both registered before the run starts.
        var bEnum = agent.SubscribeAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        var bFirst = bEnum.MoveNextAsync();
        var aEnum = agent.SubscribeAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        var aFirst = aEnum.MoveNextAsync(); // A consumes only the assignment below, then never drains.

        await agent.PublishForTest(Assignment("thread-1", runId, genId));
        (await aFirst).Should().BeTrue("A receives the assignment, then stops draining (its channel fills)");
        (await bFirst).Should().BeTrue();
        bEnum.Current.Should().BeOfType<RunAssignmentMessage>();

        // Publish a burst far exceeding A's capacity, draining B one message per publish so B never
        // backs up. Each publish must return PROMPTLY: before the fix, the (capacity+1)th write to the
        // stalled A would await forever inside Task.WhenAll, so the per-publish timeout would fire.
        for (var i = 0; i < burst; i++)
        {
            await agent.PublishForTest(TextDelta(runId, genId, i.ToString()))
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(3), cts.Token);

            (await bEnum.MoveNextAsync()).Should().BeTrue("the fast subscriber keeps receiving while A is stalled");
            bEnum.Current.Should().BeOfType<TextUpdateMessage>().Which.Text.Should().Be(i.ToString());
        }

        await agent.PublishForTest(new RunCompletedMessage { CompletedRunId = runId, ThreadId = "thread-1" })
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(3), cts.Token);

        // B (which kept pace) received every delta in order plus RunCompleted, even though A stalled
        // on the same publisher and was dropped.
        (await bEnum.MoveNextAsync()).Should().BeTrue();
        bEnum.Current.Should().BeOfType<RunCompletedMessage>();

        // Before A was dropped, its bounded channel had already buffered `slowSubscriberCapacity`
        // deltas (the ones written while A was still registered but not draining) — those are
        // ordinary queued content and must be drained first, in order, exactly like any subscriber's
        // backlog.
        for (var i = 0; i < slowSubscriberCapacity; i++)
        {
            (await aEnum.MoveNextAsync()).Should().BeTrue("A's buffered backlog must be delivered before the terminal control");
            aEnum.Current.Should().BeOfType<TextUpdateMessage>().Which.Text.Should().Be(i.ToString());
        }

        // A's stream must NOT silently end when its backlog is exhausted — it must yield exactly one
        // StreamRecoveryMessage stamped with the run/generation it was last caught up on (the
        // assignment), so the client can tell "you were disconnected, resync" apart from a normal
        // run completion or unsubscribe.
        (await aEnum.MoveNextAsync()).Should()
            .BeTrue("a dropped subscriber must surface an explicit resync control, not a silent end");
        var recovery = aEnum.Current.Should().BeOfType<StreamRecoveryMessage>().Subject;
        recovery.Reason.Should().Be(StreamRecoveryReason.SlowConsumer);
        recovery.ThreadId.Should().Be("thread-1");
        recovery.RunId.Should().Be(runId);
        recovery.GenerationId.Should().Be(genId);

        // The recovery control is yielded exactly once; the enumerable then ends cleanly.
        (await aEnum.MoveNextAsync()).Should()
            .BeFalse("the recovery control is terminal and must not repeat or be followed by more messages");

        await aEnum.DisposeAsync();
        await bEnum.DisposeAsync();
    }

    [Fact]
    public async Task Replay_buffer_is_capped_by_estimated_bytes_not_just_count()
    {
        // Generous count cap, tiny byte budget: prove the BYTE cap stops buffering before the count
        // cap would. EstimateMessageBytes ≈ 128 + text.Length*2 for both TextMessage and
        // TextUpdateMessage, so a 200-char canonical message ≈ 528 bytes: assignment (≈128) + two
        // canonical messages (≈528 each = 1184) crosses the 1000-byte budget, so only the assignment +
        // first two canonical messages are retained; the third and all later ones are dropped. Uses
        // canonical complete-text fillers (not deltas — deltas never enter the bridge at all now).
        await using var agent = new ReplayTestAgent(
            "thread-1",
            maxReplayBufferSize: 1_000_000,
            maxReplayBufferBytes: 1_000);
        const string runId = "run-1";
        const string genId = "gen-1";
        var big = new string('x', 200);

        await agent.PublishForTest(Assignment("thread-1", runId, genId));
        for (var i = 0; i < 20; i++)
        {
            await agent.PublishForTest(CompleteText(runId, genId, big));
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var e = agent.SubscribeAsync(cts.Token).GetAsyncEnumerator(cts.Token);

        // Exactly three messages were buffered before the byte budget tripped.
        (await e.MoveNextAsync()).Should().BeTrue();
        e.Current.Should().BeOfType<RunAssignmentMessage>();
        (await e.MoveNextAsync()).Should().BeTrue();
        e.Current.Should().BeOfType<TextMessage>();
        (await e.MoveNextAsync()).Should().BeTrue();
        e.Current.Should().BeOfType<TextMessage>();

        // Prove the buffer held EXACTLY those three: the next message must be a sentinel published live
        // AFTER subscribing (a plain delta — proving live fan-out still delivers deltas even though the
        // bridge never buffers them), not the byte-capped (dropped) third canonical message.
        await agent.PublishForTest(TextDelta(runId, genId, "SENTINEL"));
        (await e.MoveNextAsync()).Should().BeTrue();
        e.Current.Should().BeOfType<TextUpdateMessage>().Which.Text.Should().Be("SENTINEL");
    }

    [Fact]
    public async Task Large_streaming_deltas_never_consume_bridge_bytes_but_a_canonical_message_still_enters()
    {
        // Deltas are excluded from the bridge before byte accounting even runs, so a large burst of
        // large deltas must never threaten a tiny byte budget; only a canonical/control message counts
        // against it and is still faithfully replayed.
        await using var agent = new ReplayTestAgent(
            "thread-1",
            maxReplayBufferSize: 1_000_000,
            maxReplayBufferBytes: 1_000);
        const string runId = "run-1";
        const string genId = "gen-1";
        // Each delta would be ≈128 + 5,000*2 = 10,128 bytes if buffered — 10x the entire byte budget on
        // its own, and there are 50 of them.
        var hugeDelta = new string('x', 5_000);

        await agent.PublishForTest(Assignment("thread-1", runId, genId));
        for (var i = 0; i < 50; i++)
        {
            await agent.PublishForTest(TextDelta(runId, genId, hugeDelta));
        }

        await agent.PublishForTest(CompleteText(runId, genId, "final"));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var e = agent.SubscribeAsync(cts.Token).GetAsyncEnumerator(cts.Token);

        // Replay contains ONLY the assignment and the canonical complete text — none of the 50 huge
        // deltas ever occupied a byte of the bridge's budget.
        (await e.MoveNextAsync()).Should().BeTrue();
        e.Current.Should().BeOfType<RunAssignmentMessage>();
        (await e.MoveNextAsync()).Should().BeTrue();
        e.Current.Should().BeOfType<TextMessage>().Which.Text.Should().Be("final");

        var next = e.MoveNextAsync();
        next.IsCompleted.Should().BeFalse("nothing beyond the assignment and the canonical text was buffered");

        // Complete the pending read with a genuinely live delta — proves live fan-out of deltas is
        // untouched by the byte-starved bridge, and avoids leaving MoveNextAsync outstanding when the
        // enumerator disposes (IAsyncEnumerator forbids overlapping MoveNextAsync/DisposeAsync calls).
        await agent.PublishForTest(TextDelta(runId, genId, "live-after"));
        (await next).Should().BeTrue();
        e.Current.Should().BeOfType<TextUpdateMessage>().Which.Text.Should().Be("live-after");
    }

    [Fact]
    public async Task Bridge_truncation_warning_fires_only_for_canonical_overflow_never_for_raw_deltas()
    {
        // The truncation warning must reflect the CANONICAL/control bridge's cap, not raw deltas: a run
        // that emits far more deltas than a deliberately tiny cap must never warn (deltas never enter
        // the bridge), while a canonical/control entry that overflows that same tiny cap must.
        var capturingLogger = new CapturingLogger<ReplayTestAgent>();
        await using var agent = new ReplayTestAgent("thread-1", maxReplayBufferSize: 1, logger: capturingLogger);
        const string runId = "run-1";
        const string genId = "gen-1";

        // The assignment alone fills the single-slot cap.
        await agent.PublishForTest(Assignment("thread-1", runId, genId));

        for (var i = 0; i < 500; i++)
        {
            await agent.PublishForTest(TextDelta(runId, genId, i.ToString()));
        }

        capturingLogger.WarningCount("replay buffer hit its cap").Should().Be(
            0, "500 raw deltas alone must never trip the canonical/control bridge's truncation warning");

        // A canonical message now overflows the single-slot cap.
        await agent.PublishForTest(CompleteText(runId, genId, "overflow"));

        capturingLogger.WarningCount("replay buffer hit its cap").Should().BeGreaterThan(
            0, "a canonical/control entry exceeding the cap must trip the truncation warning");
    }

    [Fact]
    public async Task Same_run_injection_assignment_does_not_clear_the_replay_buffer()
    {
        // #171: an out-of-band notification folded into the ACTIVE run publishes a WasInjected
        // RunAssignmentMessage with the SAME runId. That must NOT reset the replay buffer, or a client
        // reconnecting after the notification loses the run's earlier streamed deltas. The reset keys on
        // a NEW runId, not on every RunAssignmentMessage (see MultiTurnAgentBase.PublishToAllAsync).
        await using var agent = new ReplayTestAgent("thread-1");
        const string runId = "run-1";
        const string genId = "gen-1";

        await agent.PublishForTest(Assignment("thread-1", runId, genId));
        await agent.PublishForTest(CompleteText(runId, genId, "Hel"));
        await agent.PublishForTest(CompleteText(runId, genId, "lo"));
        // The notification's injection assignment — same run.
        await agent.PublishForTest(InjectedAssignment("thread-1", runId, genId));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var e = agent.SubscribeAsync(cts.Token).GetAsyncEnumerator(cts.Token);

        // A reconnecting subscriber still replays the run's earlier assignment + canonical messages
        // (not cleared).
        (await e.MoveNextAsync()).Should().BeTrue();
        e.Current.Should().BeOfType<RunAssignmentMessage>()
            .Which.Assignment.WasInjected.Should().BeFalse("the original run assignment is replayed first");
        (await e.MoveNextAsync()).Should().BeTrue();
        e.Current.Should().BeOfType<TextMessage>().Which.Text.Should().Be("Hel");
        (await e.MoveNextAsync()).Should().BeTrue();
        e.Current.Should().BeOfType<TextMessage>().Which.Text.Should().Be("lo");
        // Followed by the injection assignment itself (also buffered within the same run).
        (await e.MoveNextAsync()).Should().BeTrue();
        e.Current.Should().BeOfType<RunAssignmentMessage>()
            .Which.Assignment.WasInjected.Should().BeTrue();
    }

    [Fact]
    public async Task New_run_assignment_clears_the_previous_runs_replay_buffer()
    {
        // The other side of the runId-keyed reset: a genuinely new run (different runId) MUST clear the
        // prior run's buffer, so a subscriber joining during run-2 never replays run-1's stale deltas.
        await using var agent = new ReplayTestAgent("thread-1");

        await agent.PublishForTest(Assignment("thread-1", "run-1", "gen-1"));
        await agent.PublishForTest(TextDelta("run-1", "gen-1", "old"));
        await agent.PublishForTest(Assignment("thread-1", "run-2", "gen-2"));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var e = agent.SubscribeAsync(cts.Token).GetAsyncEnumerator(cts.Token);

        // Only run-2's assignment is replayed — run-1's assignment + "old" delta were cleared.
        (await e.MoveNextAsync()).Should().BeTrue();
        e.Current.Should().BeOfType<RunAssignmentMessage>()
            .Which.Assignment.RunId.Should().Be("run-2");

        // Nothing else is buffered: the next read blocks until a genuinely live message arrives.
        var next = e.MoveNextAsync();
        next.IsCompleted.Should().BeFalse("run-1's buffered messages were cleared by the new-run assignment");
        await agent.PublishForTest(TextDelta("run-2", "gen-2", "new"));
        (await next).Should().BeTrue();
        e.Current.Should().BeOfType<TextUpdateMessage>().Which.Text.Should().Be("new");
    }

    /// <summary>
    ///     Classification cases for <see cref="ReplayMessagePolicy.IsCanonicalOrControl" />: every known
    ///     streaming update/fragment type is NOT canonical (a resynchronizing consumer rebuilds the text it
    ///     carries from the canonical complete message), while complete content, control and accounting
    ///     messages ARE.
    /// </summary>
    public static TheoryData<IMessage, bool> ReplayClassificationCases() => new()
    {
        // Fragments — never canonical.
        { TextDelta("run-1", "gen-1", "Hel"), false },
        { new ReasoningUpdateMessage { Reasoning = "thin", Role = Role.Assistant }, false },
        { new ToolCallUpdateMessage { Role = Role.Assistant, FunctionName = "get_weather" }, false },
        { new ToolsCallUpdateMessage { Role = Role.Assistant, ToolCallUpdates = [] }, false },
        // A JSON fragment arrives as an argument-fragment-bearing tool-call update, not a distinct
        // message type — pinned explicitly so the fragment path is covered by the policy.
        {
            new ToolCallUpdateMessage
            {
                Role = Role.Assistant,
                FunctionName = "get_weather",
                FunctionArgs = "{\"loc",
                JsonFragmentUpdates = [],
            },
            false
        },

        // Canonical content and control — always replayed.
        { Assignment("thread-1", "run-1", "gen-1"), true },
        { new TextMessage { Text = "Hello", Role = Role.Assistant }, true },
        { new ReasoningMessage { Reasoning = "thought", Role = Role.Assistant }, true },
        { ToolCall("run-1", "gen-1", "call_1", 1), true },
        { ToolResult("run-1", "gen-1", "call_1", 2), true },
        { new NotifyMessage { NotifyKind = NotifyKinds.ClientNotification, Label = "done" }, true },
        { new UsageMessage { Usage = new Usage() }, true },
        { new RunCompletedMessage { CompletedRunId = "run-1", ThreadId = "thread-1" }, true },
    };

    [Theory]
    [MemberData(nameof(ReplayClassificationCases))]
    public void Replay_policy_classifies_only_canonical_and_control_messages(
        IMessage message,
        bool expected)
    {
        ReplayMessagePolicy.IsCanonicalOrControl(message).Should().Be(expected);
    }

    /// <summary>
    /// Agent whose <see cref="MultiTurnAgentBase.OnSubscriberChannelCompletedDuringDisposeAsync"/>
    /// override simulates a publish racing <see cref="MultiTurnAgentBase.DisposeAsync"/>'s teardown
    /// at the exact instant a subscriber's output channel is completed - no sleeps, no real thread
    /// timing. If disposal completes-then-removes (the bug), the simulated publish still finds the
    /// subscriber present, its fast-path write fails against the now-completed channel, and it is
    /// "dropped" as if slow, wrongly setting <see cref="StreamRecoveryMessage"/> on ordinary
    /// shutdown. If disposal removes-then-completes (the fix), the simulated publish's snapshot
    /// never includes the already-removed subscriber and nothing is set.
    /// </summary>
    private sealed class DisposeRaceTestAgent : MultiTurnAgentBase
    {
        public DisposeRaceTestAgent()
            : base("thread-1", systemPrompt: null, store: null)
        {
        }

        protected override Task RunLoopAsync(CancellationToken ct) => Task.CompletedTask;

        public ValueTask PublishForTest(IMessage message) => PublishToAllAsync(message, CancellationToken.None);

        internal override ValueTask OnSubscriberChannelCompletedDuringDisposeAsync(string subscriberId) =>
            PublishToAllAsync(
                new RunCompletedMessage { CompletedRunId = "race-run", ThreadId = "thread-1" },
                CancellationToken.None);
    }

    [Fact]
    public async Task DisposeAsync_NeverProducesStreamRecoveryMessage_EvenWhenPublishRacesTeardown()
    {
        var agent = new DisposeRaceTestAgent();

        // Publish a canonical message BEFORE subscribing so SubscribeAsync's registration snapshot
        // has one buffered item. The first MoveNextAsync() below then yields it synchronously via
        // the replay loop's `yield return` - the iterator never reaches `await foreach` over the
        // live channel, so no reader continuation is ever registered on it. That keeps the rest of
        // this scenario single-threaded and deterministic: nothing but our own code below reacts to
        // the channel completing, so the hook's simulated publish is the only thing racing teardown.
        await agent.PublishForTest(Assignment("thread-1", "run-1", "gen-1"));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var e = agent.SubscribeAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        (await e.MoveNextAsync()).Should().BeTrue();
        e.Current.Should().BeOfType<RunAssignmentMessage>();

        // Disposal tears down the subscriber. Its hook (DisposeRaceTestAgent) simulates a publish
        // racing the exact instant the subscriber's channel is completed.
        await agent.DisposeAsync();

        // Ordinary disposal - even with a publish simulated to race the exact teardown instant - must
        // end the subscriber's stream cleanly. It must never surface a StreamRecoveryMessage; that is
        // reserved for PublishToSubscriber's slow-consumer eviction path.
        (await e.MoveNextAsync()).Should().BeFalse();
    }
}
