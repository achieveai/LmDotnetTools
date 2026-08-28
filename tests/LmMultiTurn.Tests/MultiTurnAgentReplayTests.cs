using System.Runtime.CompilerServices;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Models;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.Delivery;
using AchieveAi.LmDotnetTools.LmMultiTurn.Lifecycle;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;
using AchieveAi.LmDotnetTools.LmTestUtils.Logging;
using FluentAssertions;
using LmMultiTurn.Tests.Lifecycle;
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

    // runId/genId are nullable because that is the wire reality: finalized tool_call messages reach a
    // subscriber WITHOUT a runId (0 of 267 across recordings/*.ws.jsonl), which is exactly the shape
    // that leaves a delivery cursor empty unless the replay already seeded it.
    private static ToolCallMessage ToolCall(string? runId, string? genId, string toolCallId, int orderIdx) =>
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

    /// <summary>
    /// The register-AND-snapshot atomicity itself — the property <c>_replayLock</c> exists to provide, and
    /// the one a single subscriber cannot reach.
    /// <para>
    /// Why one subscriber cannot see it and many can. The window sits between snapshotting
    /// <c>_replayBuffer</c> and registering in <c>_outputSubscribers</c>, and it is a few instructions wide,
    /// so nothing outside <c>SubscribeAsync</c> can schedule a publish into it. What CAN is lock contention:
    /// once the registration moves outside the lock, a subscriber that has registered must still wait to
    /// acquire <c>_replayLock</c>, and every publish that completes during that wait appends to the buffer
    /// AND writes to the already-registered channel. The subscriber then snapshots a buffer that already
    /// contains what it was just sent live. So the window is not widened artificially here — it is entered
    /// by making many subscribers contend with a publisher taking and releasing the lock without pause.
    /// </para>
    /// <para>
    /// The invariant is exact rather than statistical, which is what lets a race be asserted without
    /// tolerances. Replay covers the whole in-flight run from its assignment, so a subscriber joining at ANY
    /// point must end up with every canonical message of the run exactly once: a replay prefix plus a live
    /// suffix that meet without overlapping and without a gap. Registration outside the lock breaks it in
    /// whichever direction the line moves — before the snapshot yields a DUPLICATE, after it yields a LOST
    /// message — and both are caught here.
    /// </para>
    /// <para>
    /// The run is published as CANONICAL text messages, not streaming deltas: <c>ReplayMessagePolicy</c>
    /// keeps fragments out of the bridge entirely, so a run made of deltas would leave every mid-run joiner
    /// legitimately short and the count assertion below would say nothing about the lock.
    /// </para>
    /// <para>
    /// The overlap is CONSTRUCTED, not hoped for, and that is what the <c>registeredDuringBurst</c>
    /// assertion exists to keep honest. <c>PublishToAllAsync</c> completes synchronously, so a publisher
    /// started with <c>Task.Run</c> runs its whole burst without ever yielding — and subscribers queued
    /// behind it on the same thread pool were measured to register only AFTER it had finished, every one of
    /// them against an idle lock, which killed the mutation 0 times. So each subscriber gets a DEDICATED
    /// thread, every one of them is parked on a gate before the first publish, and the publisher opens that
    /// gate itself as its first act. A run in which the subscribers still all arrived late is a test that
    /// proved nothing, and it fails rather than passing quietly.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Subscribers_registering_against_a_contended_publisher_still_get_each_message_once()
    {
        const string runId = "run-1";
        const string genId = "gen-1";
        const int total = 30_000;
        const int subscriberCount = 32;

        // Every bound is sized out of the way so a failure here is attributable to the lock and nothing
        // else: a bounded channel that fills makes PublishToSubscriber DROP the subscriber (ending its
        // enumeration early, which reads exactly like the lost-message defect), and a replay buffer that
        // hits either cap is withheld wholesale in favour of a resync advisory (which reads exactly like
        // the same defect from the other side).
        await using var agent = new ReplayTestAgent(
            "thread-1",
            outputChannelCapacity: 100_000,
            maxReplayBufferSize: 100_000,
            maxReplayBufferBytes: 64L * 1024 * 1024);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        await agent.PublishForTest(Assignment("thread-1", runId, genId));

        var registered = new TaskCompletionSource[subscriberCount];
        for (var s = 0; s < subscriberCount; s++)
        {
            registered[s] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        using var subscribersReady = new CountdownEvent(subscriberCount);
        using var go = new ManualResetEventSlim(false);

        // LongRunning, so each subscriber owns a real thread rather than a thread-pool slot the publisher's
        // non-yielding burst can starve it out of. Registration happens synchronously inside the first
        // MoveNextAsync, i.e. on this very thread, so all 32 registrations race the burst directly.
        var collectors = new Task<List<int>>[subscriberCount];
        for (var s = 0; s < subscriberCount; s++)
        {
            var slot = s;
            collectors[s] = Task.Factory.StartNew(
                    () =>
                    {
                        subscribersReady.Signal();
                        go.Wait(cts.Token);
                        return CollectRunAsync(agent, registered[slot], cts.Token);
                    },
                    cts.Token,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default)
                .Unwrap();
        }

        var registeredDuringBurst = 0;
        var publisher = Task.Factory.StartNew(
                async () =>
                {
                    // Release the subscribers and start publishing in the same breath. Everything the
                    // window needs is already in place: 32 live threads, each one instruction away from
                    // SubscribeAsync.
                    subscribersReady.Wait(cts.Token);
                    go.Set();

                    // Publishes STRAIGHT THROUGH, never parking, and this is the whole mechanism. Parking
                    // releases `_replayLock`, so a publisher that waited mid-stream for the subscribers
                    // would meet every one of them idle. The wait that remains is below, and it gates only
                    // the run's COMPLETION.
                    for (var i = 0; i < total; i++)
                    {
                        await agent.PublishForTest(CompleteText(runId, genId, i.ToString()));
                    }

                    registeredDuringBurst = registered.Count(r => r.Task.IsCompleted);

                    // RunCompletedMessage clears the replay buffer, so it must not be published until every
                    // subscriber has registered — otherwise a late one snapshots nothing and waits forever
                    // on a run that will never publish again, which is a timeout rather than an assertion. A
                    // subscriber that registers after the burst still replays the whole buffered run, so
                    // this costs the test nothing.
                    await Task.WhenAll(registered.Select(r => r.Task));
                    await agent.PublishForTest(
                        new RunCompletedMessage { CompletedRunId = runId, ThreadId = "thread-1" });
                },
                cts.Token,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default)
            .Unwrap();

        await publisher;
        var results = await Task.WhenAll(collectors);

        registeredDuringBurst.Should()
            .BeGreaterThan(
                0,
                "the invariant below is only about the lock if at least one subscriber registered while the "
                    + "publisher was mid-burst; every subscriber arriving after the last publish means the "
                    + "window was never entered and the run proved nothing");

        for (var s = 0; s < results.Length; s++)
        {
            var received = results[s];
            received.Should()
                .OnlyHaveUniqueItems(
                    $"subscriber {s} must never be handed the same message twice — a message buffered AND sent "
                        + "live is what registering outside the lock produces");
            received.Should().BeInAscendingOrder($"subscriber {s} saw a single serial publisher");
            received.Should()
                .HaveCount(
                    total,
                    $"subscriber {s} must end up with the WHOLE run: replay covers it from the assignment "
                        + "onwards, so a short count is a message that fell between the snapshot and the "
                        + "registration rather than a late join");
        }
    }

    /// <summary>Drains one subscription to the end of the run, signalling once it has registered — which the
    /// arrival of any message proves, since registration happens inside the first MoveNextAsync.</summary>
    private static async Task<List<int>> CollectRunAsync(
        ReplayTestAgent agent,
        TaskCompletionSource registered,
        CancellationToken ct)
    {
        var seen = new List<int>();
        await foreach (var m in agent.SubscribeAsync(ct))
        {
            _ = registered.TrySetResult();

            if (m is TextMessage t && int.TryParse(t.Text, out var n))
            {
                seen.Add(n);
            }

            if (m is RunCompletedMessage)
            {
                break;
            }
        }

        return seen;
    }

    [Fact]
    public async Task Replay_buffer_is_capped_so_a_huge_run_does_not_grow_unbounded()
    {
        await using var agent = new ReplayTestAgent("thread-1");
        const string runId = "run-1";
        const string genId = "gen-1";
        const int cap = 10_000; // mirrors MultiTurnAgentBase.MaxReplayBufferSize

        // Assignment fills slot #1; the next `cap - 1` CANONICAL messages fill the buffer to EXACTLY
        // its cap without overflowing. Streaming deltas are excluded from the bridge entirely
        // (ReplayMessagePolicy), so the count cap is exercised here with canonical complete-text
        // messages instead of deltas.
        await agent.PublishForTest(Assignment("thread-1", runId, genId));
        for (var i = 0; i < cap - 1; i++)
        {
            await agent.PublishForTest(CompleteText(runId, genId, i.ToString()));
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var e = agent.SubscribeAsync(cts.Token).GetAsyncEnumerator(cts.Token);

        // A buffer filled to exactly its cap is NOT truncated, so it replays in full.
        for (var i = 0; i < cap; i++)
        {
            (await e.MoveNextAsync()).Should().BeTrue();
            e.Current.Should().NotBeOfType<StreamRecoveryMessage>("an un-truncated buffer replays without a resync control");
        }

        // Prove the buffer held EXACTLY `cap` messages: the next one must be a sentinel published
        // live AFTER subscribing, i.e. nothing was left over in the replay snapshot.
        await agent.PublishForTest(CompleteText(runId, genId, "SENTINEL"));
        (await e.MoveNextAsync()).Should().BeTrue();
        e.Current.Should()
            .BeOfType<TextMessage>()
            .Which.Text.Should()
            .Be("SENTINEL", "the replay snapshot ended exactly at the cap");

        // One more canonical message cannot fit, so the buffer becomes truncated — and a subscriber
        // joining from here is told to resync rather than handed a silently partial replay.
        await agent.PublishForTest(CompleteText(runId, genId, "OVERFLOW"));
        await using var late = agent.SubscribeAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        (await late.MoveNextAsync()).Should().BeTrue();
        late.Current.Should()
            .BeOfType<StreamRecoveryMessage>("the cap+1'th message did not fit, so the buffer is truncated")
            .Which.Reason.Should()
            .Be(StreamRecoveryReason.ReplayTruncated);
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
        for (var i = 0; i < 2; i++)
        {
            await agent.PublishForTest(CompleteText(runId, genId, big));
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var e = agent.SubscribeAsync(cts.Token).GetAsyncEnumerator(cts.Token);

        // The budget has not tripped yet, so all three buffered messages replay in full.
        (await e.MoveNextAsync()).Should().BeTrue();
        e.Current.Should().BeOfType<RunAssignmentMessage>();
        (await e.MoveNextAsync()).Should().BeTrue();
        e.Current.Should().BeOfType<TextMessage>();
        (await e.MoveNextAsync()).Should().BeTrue();
        e.Current.Should().BeOfType<TextMessage>();

        // Prove the buffer held EXACTLY those three: the next message must be a sentinel published live
        // AFTER subscribing (a plain delta — proving live fan-out still delivers deltas even though the
        // bridge never buffers them).
        await agent.PublishForTest(TextDelta(runId, genId, "SENTINEL"));
        (await e.MoveNextAsync()).Should().BeTrue();
        e.Current.Should().BeOfType<TextUpdateMessage>().Which.Text.Should().Be("SENTINEL");

        // A third big canonical message crosses the budget and cannot be buffered, so from here the
        // replay is incomplete — a joining subscriber is told to resync instead of being handed the
        // silently partial prefix.
        await agent.PublishForTest(CompleteText(runId, genId, big));
        await using var late = agent.SubscribeAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        (await late.MoveNextAsync()).Should().BeTrue();
        late.Current.Should()
            .BeOfType<StreamRecoveryMessage>("the byte budget tripped, so the buffered prefix is incomplete")
            .Which.Reason.Should()
            .Be(StreamRecoveryReason.ReplayTruncated);
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

    #region Dropped-subscriber identity (run/generation stamped on the recovery control)

    [Fact]
    public async Task A_dropped_subscriber_is_stamped_with_what_it_received_not_with_the_message_that_failed()
    {
        // The StreamRecoveryMessage tells the client where to resume from, so it must name the last
        // run/generation the subscriber ACTUALLY received. Stamping identity before attempting the
        // write means the message that overflowed (and was therefore never delivered) sets the
        // resume point, telling the client it is caught up on a run it never saw.
        await using var agent = new ReplayTestAgent("thread-1", outputChannelCapacity: 2);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var e = agent.SubscribeAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        var first = e.MoveNextAsync();
        await agent.PublishForTest(Assignment("thread-1", "run-1", "gen-1"));
        (await first).Should().BeTrue();
        e.Current.Should().BeOfType<RunAssignmentMessage>();

        // The subscriber stops draining here; these two fill its bounded channel to capacity.
        await agent.PublishForTest(CompleteText("run-1", "gen-1", "a"));
        await agent.PublishForTest(CompleteText("run-1", "gen-1", "b"));

        // This one cannot be written, so it is never delivered — and it belongs to a DIFFERENT run.
        await agent.PublishForTest(CompleteText("run-2", "gen-2", "never-delivered"));

        (await e.MoveNextAsync()).Should().BeTrue();
        (await e.MoveNextAsync()).Should().BeTrue();
        (await e.MoveNextAsync()).Should().BeTrue();
        var recovery = e.Current.Should().BeOfType<StreamRecoveryMessage>().Subject;
        recovery.Reason.Should().Be(StreamRecoveryReason.SlowConsumer);
        recovery.RunId.Should().Be("run-1", "the resume point is the last run actually delivered");
        recovery.GenerationId.Should().Be("gen-1");

        await e.DisposeAsync();
    }

    [Fact]
    public async Task A_dropped_subscribers_run_and_generation_are_stamped_as_one_coherent_pair()
    {
        // Tracking RunId and GenerationId as independent fields lets a run-2 message inherit run-1's
        // generation, producing a pair that never existed. The resume point must be a pair, advanced
        // atomically: a message that moves the run to a new one carries that run's generation (even
        // when it has none) rather than keeping the previous run's.
        await using var agent = new ReplayTestAgent("thread-1", outputChannelCapacity: 2);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var e = agent.SubscribeAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        var first = e.MoveNextAsync();
        await agent.PublishForTest(Assignment("thread-1", "run-1", "gen-1"));
        (await first).Should().BeTrue();

        // Delivered: moves the subscriber onto run-2, which carries no generation of its own.
        await agent.PublishForTest(new TextMessage { Text = "a", Role = Role.Assistant, RunId = "run-2" });
        await agent.PublishForTest(new TextMessage { Text = "b", Role = Role.Assistant, RunId = "run-2" });

        // Overflows and is dropped, triggering the recovery control.
        await agent.PublishForTest(new TextMessage { Text = "c", Role = Role.Assistant, RunId = "run-2" });

        (await e.MoveNextAsync()).Should().BeTrue();
        (await e.MoveNextAsync()).Should().BeTrue();
        (await e.MoveNextAsync()).Should().BeTrue();
        var recovery = e.Current.Should().BeOfType<StreamRecoveryMessage>().Subject;
        recovery.RunId.Should().Be("run-2");
        recovery.GenerationId.Should()
            .BeNull("run-2 carried no generation, so run-1's generation must not be paired with it");

        await e.DisposeAsync();
    }

    [Fact]
    public async Task ExecuteRunAsync_surfaces_the_resync_control_when_its_subscriber_is_dropped()
    {
        // ExecuteRunAsync's own subscriber is subject to the same slow-consumer eviction as
        // SubscribeAsync's. Ending the iterator silently at that point is indistinguishable from an
        // ordinary run completion, so callers that collect its output report a truncated run as a
        // successful one. It must yield the same terminal recovery control SubscribeAsync does.
        await using var agent = new ReplayTestAgent("thread-1", outputChannelCapacity: 2);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var input = new UserInput(
            [new TextMessage { Text = "hello", Role = Role.User }],
            InputId: "input-1");
        var e = agent.ExecuteRunAsync(input, cts.Token).GetAsyncEnumerator(cts.Token);
        var first = e.MoveNextAsync();
        await agent.PublishForTest(Assignment("thread-1", "run-1", "gen-1"));
        (await first).Should().BeTrue();
        e.Current.Should().BeOfType<RunAssignmentMessage>();

        // The caller stops draining; these fill the run channel and then overflow it.
        await agent.PublishForTest(CompleteText("run-1", "gen-1", "a"));
        await agent.PublishForTest(CompleteText("run-1", "gen-1", "b"));
        await agent.PublishForTest(CompleteText("run-1", "gen-1", "overflow"));

        (await e.MoveNextAsync()).Should().BeTrue();
        (await e.MoveNextAsync()).Should().BeTrue();
        (await e.MoveNextAsync()).Should()
            .BeTrue("a dropped ExecuteRun subscriber must surface an explicit resync control, not end silently");
        var recovery = e.Current.Should().BeOfType<StreamRecoveryMessage>().Subject;
        recovery.Reason.Should().Be(StreamRecoveryReason.SlowConsumer);
        recovery.RunId.Should().Be("run-1");

        (await e.MoveNextAsync()).Should().BeFalse("the recovery control is terminal");
        await e.DisposeAsync();
    }

    [Fact]
    public async Task A_subscriber_dropped_after_replaying_a_run_is_stamped_with_that_run_not_with_nothing()
    {
        // A replayed message is DELIVERED just as surely as a live one — SubscribeAsync yields the whole
        // snapshot before it ever reads the channel — so it must advance the delivery cursor too.
        // Recording only live writes leaves a subscriber that replayed all of run-1 and then received
        // only runId-less content (finalized tool_call/tool_call_result) stamped with NOTHING, handing
        // the client a resume point that names no run at all.
        await using var agent = new ReplayTestAgent("thread-1", outputChannelCapacity: 2);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        // A run is already in flight and buffered before anyone subscribes, so these reach the
        // subscriber below via REPLAY, never via its live channel.
        await agent.PublishForTest(Assignment("thread-1", "run-1", "gen-1"));
        await agent.PublishForTest(CompleteText("run-1", "gen-1", "already-published"));

        var e = agent.SubscribeAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        (await e.MoveNextAsync()).Should().BeTrue();
        e.Current.Should().BeOfType<RunAssignmentMessage>();
        (await e.MoveNextAsync()).Should().BeTrue();
        e.Current.Should().BeOfType<TextMessage>().Which.Text.Should().Be("already-published");

        // Replay drained; now the live queue fills and overflows behind a subscriber that stops reading.
        // Every live message is runId-less, so the cursor can only be non-empty if the replay seeded it.
        await agent.PublishForTest(ToolCall(null, null, "tc-1", 0));
        await agent.PublishForTest(ToolCall(null, null, "tc-2", 1));
        await agent.PublishForTest(ToolCall(null, null, "tc-3", 2));

        (await e.MoveNextAsync()).Should().BeTrue();
        (await e.MoveNextAsync()).Should().BeTrue();
        (await e.MoveNextAsync()).Should().BeTrue();
        var recovery = e.Current.Should().BeOfType<StreamRecoveryMessage>().Subject;
        recovery.Reason.Should().Be(StreamRecoveryReason.SlowConsumer);
        recovery.RunId.Should().Be("run-1", "the replayed prefix was delivered, so it is part of the resume point");
        recovery.GenerationId.Should().Be("gen-1");

        await e.DisposeAsync();
    }

    [Fact]
    public async Task A_subscriber_advised_of_a_truncated_replay_is_stamped_with_the_run_it_was_advised_about()
    {
        // The withheld-replay path delivers no buffered messages at all — only the leading advisory,
        // which itself names the run. That advisory is the only thing this subscriber received from the
        // run, so it is what the cursor must start from; otherwise a subscriber advised about run-1 and
        // then dropped reports a resume point of nothing.
        await using var agent = new ReplayTestAgent("thread-1", outputChannelCapacity: 2, maxReplayBufferSize: 1);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        await agent.PublishForTest(Assignment("thread-1", "run-1", "gen-1"));
        await agent.PublishForTest(CompleteText("run-1", "gen-1", "overflows-the-bridge"));

        var e = agent.SubscribeAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        (await e.MoveNextAsync()).Should().BeTrue();
        e.Current.Should()
            .BeOfType<StreamRecoveryMessage>()
            .Which.Reason.Should()
            .Be(StreamRecoveryReason.ReplayTruncated);

        await agent.PublishForTest(ToolCall(null, null, "tc-1", 0));
        await agent.PublishForTest(ToolCall(null, null, "tc-2", 1));
        await agent.PublishForTest(ToolCall(null, null, "tc-3", 2));

        (await e.MoveNextAsync()).Should().BeTrue();
        (await e.MoveNextAsync()).Should().BeTrue();
        (await e.MoveNextAsync()).Should().BeTrue();
        var recovery = e.Current.Should().BeOfType<StreamRecoveryMessage>().Subject;
        recovery.Reason.Should().Be(StreamRecoveryReason.SlowConsumer);
        recovery.RunId.Should().Be("run-1");
        recovery.GenerationId.Should().Be("gen-1");

        await e.DisposeAsync();
    }

    [Fact]
    public async Task A_replay_seeded_cursor_still_advances_to_a_later_run_delivered_live()
    {
        // Distinguishing case for the seed: it is a STARTING point, not a pin. A fix that stamped the
        // replayed run unconditionally — or that froze the cursor once seeded — would report run-1 here,
        // telling the client to resume from a run it has already moved past.
        await using var agent = new ReplayTestAgent("thread-1", outputChannelCapacity: 2);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        await agent.PublishForTest(Assignment("thread-1", "run-1", "gen-1"));
        await agent.PublishForTest(CompleteText("run-1", "gen-1", "already-published"));

        var e = agent.SubscribeAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        (await e.MoveNextAsync()).Should().BeTrue();
        (await e.MoveNextAsync()).Should().BeTrue();

        // run-2 starts and IS delivered live (it fits), then the run overflows the stalled channel.
        await agent.PublishForTest(Assignment("thread-1", "run-2", "gen-2"));
        await agent.PublishForTest(CompleteText("run-2", "gen-2", "live"));
        await agent.PublishForTest(CompleteText("run-2", "gen-2", "overflow"));

        (await e.MoveNextAsync()).Should().BeTrue();
        (await e.MoveNextAsync()).Should().BeTrue();
        (await e.MoveNextAsync()).Should().BeTrue();
        var recovery = e.Current.Should().BeOfType<StreamRecoveryMessage>().Subject;
        recovery.RunId.Should().Be("run-2", "live delivery moves the cursor forward past the replayed run");
        recovery.GenerationId.Should().Be("gen-2");

        await e.DisposeAsync();
    }

    #endregion

    #region Truncated replay (explicit resync instead of a silently partial prefix)

    [Fact]
    public async Task A_truncated_replay_is_withheld_and_the_joining_subscriber_is_told_to_resync()
    {
        // A capped buffer drops the run's EARLIEST messages' successors — replaying what remains
        // hands the client a prefix that silently omits part of the run, which it cannot detect.
        // Withhold it entirely and say so, so the client reloads authoritative history instead.
        await using var agent = new ReplayTestAgent("thread-1", maxReplayBufferSize: 4);
        await agent.PublishForTest(Assignment("thread-1", "run-1", "gen-1"));
        for (var i = 0; i < 4; i++)
        {
            await agent.PublishForTest(CompleteText("run-1", "gen-1", i.ToString()));
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var e = agent.SubscribeAsync(cts.Token).GetAsyncEnumerator(cts.Token);

        (await e.MoveNextAsync()).Should().BeTrue();
        var recovery = e.Current.Should().BeOfType<StreamRecoveryMessage>().Subject;
        recovery.Reason.Should().Be(StreamRecoveryReason.ReplayTruncated);
        recovery.ThreadId.Should().Be("thread-1");
        recovery.RunId.Should().Be("run-1");
        recovery.GenerationId.Should().Be("gen-1");

        // The advisory LEADS the stream, it does not end it. Only the run's already-published prefix
        // is missing; the live tail is still perfectly good and this subscription goes on carrying it.
        // Ending here would force the consumer to reconnect to keep following the run, and the
        // reconnection lands on the same still-truncated buffer — advised again, for the rest of the run.
        await agent.PublishForTest(TextDelta("run-1", "gen-1", "LIVE"));
        (await e.MoveNextAsync()).Should()
            .BeTrue("the advisory precedes the live tail instead of terminating the stream");
        e.Current.Should().BeOfType<TextUpdateMessage>().Which.Text.Should().Be("LIVE");
    }

    [Fact]
    public async Task Every_subscription_to_a_truncated_run_is_advised_even_after_another_consumed_one()
    {
        // The advisory used to be latched by ONE per-run bool, so whichever subscriber reached the
        // truncated state first consumed the only warning that would ever be issued. This process has
        // several subscribers on the same agent — WorkspaceTranscriptMirror and the sub-agent
        // forwarder subscribe alongside the browser — so the internal one could swallow the browser's
        // warning, leaving the browser with an empty replay and a live tail it cannot tell apart from
        // a complete stream. Every subscription must be advised for itself.
        await using var agent = new ReplayTestAgent("thread-1", maxReplayBufferSize: 2);
        await agent.PublishForTest(Assignment("thread-1", "run-1", "gen-1"));
        await agent.PublishForTest(CompleteText("run-1", "gen-1", "buffered"));
        await agent.PublishForTest(CompleteText("run-1", "gen-1", "truncates"));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        // First subscriber CONSUMES its advisory — the exact act that used to spend the latch.
        await using var first = agent.SubscribeAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        (await first.MoveNextAsync()).Should().BeTrue();
        first.Current.Should()
            .BeOfType<StreamRecoveryMessage>()
            .Which.Reason.Should()
            .Be(StreamRecoveryReason.ReplayTruncated);

        await using var second = agent.SubscribeAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        (await second.MoveNextAsync()).Should().BeTrue();
        second.Current.Should()
            .BeOfType<StreamRecoveryMessage>("a second consumer's replay is just as truncated as the first's")
            .Which.Reason.Should()
            .Be(StreamRecoveryReason.ReplayTruncated);

        // Neither subscription was spent by being advised: both are still registered for fan-out, so
        // one live message reaches BOTH. That is what makes advising everyone loop-free — no consumer
        // has to reconnect to keep following the run.
        await agent.PublishForTest(TextDelta("run-1", "gen-1", "LIVE"));
        (await first.MoveNextAsync()).Should().BeTrue();
        first.Current.Should().BeOfType<TextUpdateMessage>().Which.Text.Should().Be("LIVE");
        (await second.MoveNextAsync()).Should().BeTrue();
        second.Current.Should().BeOfType<TextUpdateMessage>().Which.Text.Should().Be("LIVE");
    }

    [Fact]
    public async Task A_new_run_clears_truncation_so_its_replay_is_served_again()
    {
        // Truncation is a property of one run's buffer. The next run opens a fresh buffer, so its
        // replay must be served normally — otherwise one oversized run poisons every later one.
        await using var agent = new ReplayTestAgent("thread-1", maxReplayBufferSize: 2);
        await agent.PublishForTest(Assignment("thread-1", "run-1", "gen-1"));
        await agent.PublishForTest(CompleteText("run-1", "gen-1", "buffered"));
        await agent.PublishForTest(CompleteText("run-1", "gen-1", "truncates"));

        await agent.PublishForTest(Assignment("thread-1", "run-2", "gen-2"));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var e = agent.SubscribeAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        (await e.MoveNextAsync()).Should().BeTrue();
        e.Current.Should()
            .BeOfType<RunAssignmentMessage>("run-2's buffer is complete, so it replays normally")
            .Which.Assignment.RunId.Should()
            .Be("run-2");
    }

    #endregion

    #region Disposal: admission gating and idempotence

    /// <summary>
    /// Agent whose <see cref="MultiTurnAgentBase.OnDisposeAsync"/> override parks inside disposal
    /// until released, so a test can deterministically observe a SECOND <c>DisposeAsync</c> arriving
    /// while the first teardown is still running — no sleeps, no real thread timing.
    /// </summary>
    private sealed class GatedDisposeAgent : MultiTurnAgentBase
    {
        private int _onDisposeCount;

        public GatedDisposeAgent()
            : base("thread-1", systemPrompt: null, store: null)
        {
        }

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int OnDisposeCount => Volatile.Read(ref _onDisposeCount);

        protected override Task RunLoopAsync(CancellationToken ct) => Task.CompletedTask;

        protected override async Task OnDisposeAsync()
        {
            _ = Interlocked.Increment(ref _onDisposeCount);
            _ = Entered.TrySetResult();
            await Release.Task;
        }
    }

    [Fact]
    public async Task A_second_DisposeAsync_awaits_the_in_flight_teardown_instead_of_returning_early()
    {
        // A plain bool guard makes the second caller return while teardown is still mid-flight, so
        // `await using` hands back a half-disposed agent. Disposal must be idempotent AND awaitable:
        // every caller observes the same single teardown, completed.
        var agent = new GatedDisposeAgent();

        var firstDispose = agent.DisposeAsync().AsTask();
        await agent.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var secondDispose = agent.DisposeAsync().AsTask();
        secondDispose.IsCompleted.Should()
            .BeFalse("a concurrent DisposeAsync must await the running teardown, not report success while it is unfinished");

        _ = agent.Release.TrySetResult();
        await Task.WhenAll(firstDispose, secondDispose).WaitAsync(TimeSpan.FromSeconds(10));
        agent.OnDisposeCount.Should().Be(1, "teardown runs exactly once no matter how many callers dispose");
    }

    /// <summary>
    /// Agent whose teardown always fails, with a per-instance exception the test can identify by
    /// REFERENCE — the only way to tell our fault apart from any other test's on the process-wide
    /// <see cref="TaskScheduler.UnobservedTaskException"/> event.
    /// </summary>
    private sealed class ThrowingDisposeAgent(Exception failure) : MultiTurnAgentBase("thread-1", systemPrompt: null, store: null)
    {
        protected override Task RunLoopAsync(CancellationToken ct) => Task.CompletedTask;

        protected override Task OnDisposeAsync() => Task.FromException(failure);

        public ValueTask PublishForTest(IMessage message) => PublishToAllAsync(message, CancellationToken.None);
    }

    /// <summary>
    /// Disposes a failing agent from a SINGLE caller and drops every reference to it. Separate,
    /// non-inlined method so the agent and the tasks it published become unreachable the moment it
    /// returns — a local in the test's own async state machine would stay rooted, and the finaliser
    /// that raises <see cref="TaskScheduler.UnobservedTaskException"/> would never run for it.
    /// </summary>
    /// <param name="failure">The exception the agent's teardown throws.</param>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task DisposeAndAbandonAsync(Exception failure)
    {
        var agent = new ThrowingDisposeAgent(failure);
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(async () => await agent.DisposeAsync());
        thrown.Should().BeSameAs(failure, "the single caller observes the teardown's own fault");
    }

    [Fact]
    public async Task A_failed_teardown_observed_by_its_only_caller_leaves_no_unobserved_task()
    {
        // Disposal publishes one shared completion so that LATER callers can await the same teardown.
        // The first caller, though, used to await a DIFFERENT task — the teardown's own — and a failing
        // teardown then faulted BOTH: the one it awaited, and the published one that only a second
        // caller would ever look at. With a single caller (the overwhelmingly common case, e.g. one
        // `await using`) nobody observes the published fault, so the task finaliser re-raises it on
        // TaskScheduler.UnobservedTaskException — a fault from an agent that was disposed correctly and
        // whose exception the caller already handled, surfacing later and somewhere else entirely.
        var failure = new InvalidOperationException("teardown failed");
        var leaked = new List<Exception>();
        var gate = new object();

        void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            // Reference identity, because this event is process-wide and every other test running in
            // parallel shares it. Only OUR exception says anything about the code under test.
            if (!e.Exception.Flatten().InnerExceptions.Any(inner => ReferenceEquals(inner, failure)))
            {
                return;
            }

            lock (gate)
            {
                leaked.Add(failure);
            }

            // Claim it so this test cannot fail an unrelated one via an escalation policy.
            e.SetObserved();
        }

        TaskScheduler.UnobservedTaskException += OnUnobserved;
        try
        {
            await DisposeAndAbandonAsync(failure);

            // Force the finaliser that raises the event. Two passes: the first collection queues the
            // abandoned tasks for finalisation, the second reclaims them after their finalisers ran.
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            GC.WaitForPendingFinalizers();

            lock (gate)
            {
                leaked.Should()
                    .BeEmpty("a teardown fault the disposing caller already observed must not be left on a second, abandoned task");
            }
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= OnUnobserved;
        }
    }

    [Fact]
    public async Task Every_caller_of_a_failed_disposal_sees_the_same_exception()
    {
        // The corollary of the test above: routing all callers onto ONE task must not cost later
        // callers the failure. Disposal is idempotent, and "idempotent" includes reporting the same
        // outcome — a second caller that saw success while the teardown had actually failed would be
        // handed an agent it believes is cleanly disposed.
        var failure = new InvalidOperationException("teardown failed");
        var agent = new ThrowingDisposeAgent(failure);

        var first = await Assert.ThrowsAsync<InvalidOperationException>(async () => await agent.DisposeAsync());
        var second = await Assert.ThrowsAsync<InvalidOperationException>(async () => await agent.DisposeAsync());
        var third = await Assert.ThrowsAsync<InvalidOperationException>(async () => await agent.DisposeAsync());

        first.Should().BeSameAs(failure);
        second.Should().BeSameAs(failure, "a repeat caller observes the one teardown's outcome, not a fresh one");
        third.Should().BeSameAs(failure);
    }

    [Fact]
    public async Task A_failed_teardown_still_ends_every_live_subscriber_stream()
    {
        // Teardown used to complete the input and subscriber channels only AFTER OnDisposeAsync
        // returned, on the same straight-line path. A descendant whose cleanup threw therefore
        // skipped the completions entirely, and every connected client was left parked on
        // `await foreach` over a channel nobody would ever complete — a hang with no error, on an
        // agent that is already gone. The failure must be reported, not converted into silence.
        var failure = new InvalidOperationException("teardown failed");
        var agent = new ThrowingDisposeAgent(failure);

        // A live subscriber, registered and reading, exactly like a connected client: publish first
        // so the replay buffer makes the first MoveNextAsync return without racing anything, which
        // is what proves the subscriber is registered before disposal begins.
        await agent.PublishForTest(Assignment("thread-1", "run-1", "gen-1"));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var stream = agent.SubscribeAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        (await stream.MoveNextAsync()).Should().BeTrue();

        // Now parked on the live channel with nothing more to read — only teardown can end it.
        var drain = DrainAsync(stream);
        drain.IsCompleted.Should().BeFalse("the stream stays open until disposal completes its channel");

        var first = await Assert.ThrowsAsync<InvalidOperationException>(async () => await agent.DisposeAsync());
        first.Should().BeSameAs(failure, "the disposing caller must still see the cleanup failure");

        await drain.WaitAsync(TimeSpan.FromSeconds(30));

        // Idempotency must survive the same path: a later caller sees the one teardown's fault, and
        // A_failed_teardown_observed_by_its_only_caller_leaves_no_unobserved_task covers the
        // corollary that no second, abandoned copy of it is left to surface as an unobserved fault.
        var second = await Assert.ThrowsAsync<InvalidOperationException>(async () => await agent.DisposeAsync());
        second.Should().BeSameAs(failure);

        static async Task DrainAsync(IAsyncEnumerator<IMessage> stream)
        {
            while (await stream.MoveNextAsync())
            {
                // Drain to the end of the stream; the assertion is that this loop terminates.
            }
        }
    }

    [Fact]
    public async Task SubscribeAsync_after_disposal_is_rejected_instead_of_hanging()
    {
        // Disposal completes every registered subscriber's channel, but nothing stops a LATER
        // subscriber from registering into the dead fan-out map — its channel is never written to
        // and never completed, so the caller waits forever. Reject the subscription instead.
        var agent = new ReplayTestAgent("thread-1");
        await agent.DisposeAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var subscribe = async () =>
        {
            await foreach (var _ in agent.SubscribeAsync(cts.Token))
            {
                // Drain; the enumerable must not produce anything.
            }
        };

        _ = await subscribe.Should().ThrowAsync<ObjectDisposedException>();
    }

    /// <summary>
    /// Agent that subscribes from inside <see cref="MultiTurnAgentBase.DisposeAsync"/>'s teardown
    /// loop, deterministically reproducing a client connecting in the window between "teardown has
    /// passed the subscriber map" and "disposal has finished" — where a registration would otherwise
    /// be stranded on a channel nobody will ever complete.
    /// </summary>
    private sealed class SubscribeDuringDisposeAgent : MultiTurnAgentBase
    {
        private int _hookRan;

        public SubscribeDuringDisposeAgent()
            : base("thread-1", systemPrompt: null, store: null)
        {
        }

        public Exception? SubscribeFailure { get; private set; }

        public bool SubscribeCompletedNormally { get; private set; }

        protected override Task RunLoopAsync(CancellationToken ct) => Task.CompletedTask;

        public ValueTask PublishForTest(IMessage message) => PublishToAllAsync(message, CancellationToken.None);

        internal override async ValueTask OnSubscriberChannelCompletedDuringDisposeAsync(string subscriberId)
        {
            if (Interlocked.Exchange(ref _hookRan, 1) != 0)
            {
                return;
            }

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try
            {
                await foreach (var _ in SubscribeAsync(cts.Token))
                {
                    // Drain.
                }

                SubscribeCompletedNormally = true;
            }
            catch (Exception ex)
            {
                SubscribeFailure = ex;
            }
        }
    }

    [Fact]
    public async Task SubscribeAsync_racing_disposal_teardown_is_rejected_rather_than_stranded()
    {
        var agent = new SubscribeDuringDisposeAgent();

        // One registered subscriber, so disposal's teardown loop runs the hook exactly once.
        await agent.PublishForTest(Assignment("thread-1", "run-1", "gen-1"));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var e = agent.SubscribeAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        (await e.MoveNextAsync()).Should().BeTrue();

        await agent.DisposeAsync();

        agent.SubscribeCompletedNormally.Should().BeFalse();
        agent.SubscribeFailure.Should()
            .BeOfType<ObjectDisposedException>(
                "a subscription arriving mid-teardown must be refused outright, not left waiting on a channel "
                    + "no one will complete");

        await e.DisposeAsync();
    }

    /// <summary>
    /// Reads a live subscription to its end. The assertion is always that this TERMINATES: a stream
    /// still open after disposal is the hang under test.
    /// </summary>
    private static async Task DrainToEndAsync(IAsyncEnumerator<IMessage> stream)
    {
        while (await stream.MoveNextAsync())
        {
            // Drain only; the messages themselves are not what these tests are about.
        }
    }

    /// <summary>
    /// Registers a live subscriber that has already read one replayed message, so it is provably
    /// registered (and parked on an open channel) before disposal begins — no sleeps, no races.
    /// </summary>
    private static async Task<Task> StartLiveSubscriberAsync(MultiTurnAgentBase agent, CancellationToken ct)
    {
        var stream = agent.SubscribeAsync(ct).GetAsyncEnumerator(ct);
        (await stream.MoveNextAsync()).Should().BeTrue("the replayed message proves the subscriber is registered");

        var drain = DrainToEndAsync(stream);
        drain.IsCompleted.Should().BeFalse("the stream stays open until disposal completes its channel");
        return drain;
    }

    /// <summary>
    /// Agent whose run loop faults, so that <see cref="MultiTurnAgentBase.StopAsync"/> — the FIRST
    /// step of disposal after cancellation — rethrows that fault when disposal awaits the stopped
    /// run. The seam is real production control flow, not an override of disposal itself.
    /// </summary>
    private sealed class FaultingRunLoopAgent(Exception failure)
        : MultiTurnAgentBase("thread-1", systemPrompt: null, store: null)
    {
        protected override Task RunLoopAsync(CancellationToken ct) => Task.FromException(failure);

        public ValueTask PublishForTest(IMessage message) => PublishToAllAsync(message, CancellationToken.None);
    }

    [Fact]
    public async Task A_run_loop_fault_resurfacing_through_StopAsync_still_ends_every_live_stream()
    {
        // StopAsync sat OUTSIDE disposal's only guarded region, so an agent whose loop had faulted —
        // the single most likely reason anyone disposes one — threw here and skipped channel teardown
        // entirely. Every connected client was then parked forever on an `await foreach` over a
        // channel belonging to an agent that no longer exists.
        var failure = new InvalidOperationException("run loop faulted");
        var agent = new FaultingRunLoopAgent(failure);

        // Drive the real path: RunAsync observes and rethrows the loop's fault, leaving _runTask
        // faulted exactly as it would be in production.
        var fromRun = await Assert.ThrowsAsync<InvalidOperationException>(() => agent.RunAsync());
        fromRun.Should().BeSameAs(failure);

        await agent.PublishForTest(Assignment("thread-1", "run-1", "gen-1"));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var drain = await StartLiveSubscriberAsync(agent, cts.Token);

        var first = await Assert.ThrowsAsync<InvalidOperationException>(async () => await agent.DisposeAsync());
        first.Should().BeSameAs(failure, "the primary failure must be preserved, not swallowed by cleanup");

        await drain.WaitAsync(TimeSpan.FromSeconds(30));

        var second = await Assert.ThrowsAsync<InvalidOperationException>(async () => await agent.DisposeAsync());
        second.Should().BeSameAs(failure, "the published disposal outcome stays stable across repeat callers");
    }

    /// <summary>
    /// A clock that is harmless until <see cref="Arm"/> is called, then throws. Lets a test pick
    /// EXACTLY which lifecycle call fails — here, the terminal timestamp inside
    /// <c>TerminalizeOutstandingAsync</c> — while the calls that set the scenario up still succeed.
    /// </summary>
    private sealed class ArmedFailingClock(Exception failure) : TimeProvider
    {
        private volatile bool _armed;

        public void Arm() => _armed = true;

        public override DateTimeOffset GetUtcNow() => _armed ? throw failure : DateTimeOffset.UnixEpoch;
    }

    /// <summary>
    /// Agent with one outstanding lifecycle run and NO started loop, so disposal's StopAsync
    /// early-returns and the armed clock's failure can only have come from the lifecycle
    /// terminalization step.
    /// </summary>
    private sealed class OutstandingLifecycleRunAgent(MultiTurnLifecycleServices services)
        : MultiTurnAgentBase("thread-1", systemPrompt: null, store: null, lifecycleServices: services)
    {
        protected override Task RunLoopAsync(CancellationToken ct) => Task.CompletedTask;

        public Task StartRunForTest() => StartRunAsync([], ct: CancellationToken.None);

        public ValueTask PublishForTest(IMessage message) => PublishToAllAsync(message, CancellationToken.None);
    }

    [Fact]
    public async Task A_lifecycle_terminalization_failure_still_ends_every_live_stream()
    {
        // The second unguarded step. TerminalizeOutstandingAsync exists precisely for the agent
        // disposed with runs still open, so a failure here lands on the path where subscribers are
        // MOST likely to be connected — and used to abandon their channels unfinished.
        var failure = new InvalidOperationException("lifecycle terminalization failed");
        var clock = new ArmedFailingClock(failure);
        var agent = new OutstandingLifecycleRunAgent(new MultiTurnLifecycleServices
        {
            Publisher = new RecordingLifecyclePublisher(),
            TimeProvider = clock,
        });

        // Start the run while the clock still works (RunStartedAsync reads it too), so only the
        // terminal timestamp taken during disposal can fail.
        await agent.StartRunForTest();

        await agent.PublishForTest(Assignment("thread-1", "run-1", "gen-1"));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var drain = await StartLiveSubscriberAsync(agent, cts.Token);

        clock.Arm();

        var first = await Assert.ThrowsAsync<InvalidOperationException>(async () => await agent.DisposeAsync());
        first.Should().BeSameAs(failure);

        await drain.WaitAsync(TimeSpan.FromSeconds(30));

        var second = await Assert.ThrowsAsync<InvalidOperationException>(async () => await agent.DisposeAsync());
        second.Should().BeSameAs(failure);
    }

    /// <summary>
    /// Agent with independently configurable teardown failures: the descendant's own cleanup, and
    /// the per-subscriber completion hook. One type covers both "a hook failure must not strand the
    /// subscribers after it" and "a hook failure must not outrank the real reason disposal failed".
    /// </summary>
    private sealed class SubscriberHookFailureAgent(Exception? hookFailure, Exception? disposeFailure = null)
        : MultiTurnAgentBase("thread-1", systemPrompt: null, store: null)
    {
        private int _hookCalls;
        private readonly List<string> _hookedSubscribers = [];
        private readonly Lock _gate = new();

        /// <summary>Every subscriber whose hook actually ran, including the one that threw.</summary>
        public IReadOnlyList<string> HookedSubscribers
        {
            get
            {
                lock (_gate)
                {
                    return [.. _hookedSubscribers];
                }
            }
        }

        protected override Task RunLoopAsync(CancellationToken ct) => Task.CompletedTask;

        protected override Task OnDisposeAsync() =>
            disposeFailure == null ? Task.CompletedTask : Task.FromException(disposeFailure);

        internal override ValueTask OnSubscriberChannelCompletedDuringDisposeAsync(string subscriberId)
        {
            lock (_gate)
            {
                _hookedSubscribers.Add(subscriberId);
            }

            // Only the FIRST hook fails: the point of the test is what happens to the subscribers
            // that come after it.
            return hookFailure != null && Interlocked.Increment(ref _hookCalls) == 1
                ? ValueTask.FromException(hookFailure)
                : ValueTask.CompletedTask;
        }

        public ValueTask PublishForTest(IMessage message) => PublishToAllAsync(message, CancellationToken.None);
    }

    [Fact]
    public async Task A_failing_subscriber_hook_still_ends_the_streams_of_every_other_subscriber()
    {
        // Teardown completed one subscriber's writer and then ran its hook, per subscriber, in a
        // single pass — making each client's hook a gate on the NEXT client's stream ever ending. One
        // throwing hook and every subscriber after it in the snapshot hung forever. Completing all
        // writers FIRST is the fix; the reporting below is secondary to it.
        var failure = new InvalidOperationException("subscriber teardown hook failed");
        var agent = new SubscriberHookFailureAgent(failure);

        await agent.PublishForTest(Assignment("thread-1", "run-1", "gen-1"));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        // Three, so "every stream after the first" is a real claim rather than a single instance.
        var drains = new List<Task>();
        for (var i = 0; i < 3; i++)
        {
            drains.Add(await StartLiveSubscriberAsync(agent, cts.Token));
        }

        var first = await Assert.ThrowsAsync<InvalidOperationException>(async () => await agent.DisposeAsync());
        first.Should().BeSameAs(failure, "with no earlier failure, the cleanup failure is what disposal reports");

        await Task.WhenAll(drains).WaitAsync(TimeSpan.FromSeconds(30));

        agent.HookedSubscribers.Should()
            .HaveCount(3, "every subscriber's hook runs independently; one failure must not skip the rest");

        var second = await Assert.ThrowsAsync<InvalidOperationException>(async () => await agent.DisposeAsync());
        second.Should().BeSameAs(failure, "a repeat caller sees the same outcome, not a fresh or absent one");
    }

    [Fact]
    public async Task A_failing_subscriber_hook_never_masks_the_real_reason_disposal_failed()
    {
        // Channel teardown runs from disposal's outermost finally, and a finally that throws REPLACES
        // the in-flight exception. If the hook's failure escaped from there, the caller would be told
        // "a subscriber hook failed" while the actual fault — the descendant's own cleanup — vanished.
        var primary = new InvalidOperationException("descendant teardown failed");
        var secondary = new InvalidOperationException("subscriber teardown hook failed");
        var agent = new SubscriberHookFailureAgent(secondary, primary);

        await agent.PublishForTest(Assignment("thread-1", "run-1", "gen-1"));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var drainA = await StartLiveSubscriberAsync(agent, cts.Token);
        var drainB = await StartLiveSubscriberAsync(agent, cts.Token);

        var first = await Assert.ThrowsAsync<InvalidOperationException>(async () => await agent.DisposeAsync());
        first.Should().BeSameAs(primary, "the earlier, primary failure outranks anything cleanup reports");

        await Task.WhenAll(drainA, drainB).WaitAsync(TimeSpan.FromSeconds(30));
        agent.HookedSubscribers.Should().HaveCount(2);

        var second = await Assert.ThrowsAsync<InvalidOperationException>(async () => await agent.DisposeAsync());
        second.Should().BeSameAs(primary);
    }

    #endregion
}
