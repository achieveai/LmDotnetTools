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
}
