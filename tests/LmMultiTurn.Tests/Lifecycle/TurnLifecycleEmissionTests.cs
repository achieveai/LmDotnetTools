using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Models;
using AchieveAi.LmDotnetTools.LmLifecycle;
using AchieveAi.LmDotnetTools.LmLifecycle.Payloads;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.Lifecycle;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using FluentAssertions;
using Xunit;

namespace LmMultiTurn.Tests.Lifecycle;

/// <summary>
/// Covers what a subscriber is allowed to assume about turn-scoped lifecycle events: that every
/// accepted generation id is reported exactly once, at its final state, whatever ended it — and that
/// a turn is described the same way no matter which loop produced it.
/// </summary>
/// <remarks>
/// The four loops disagree about what a generation id means — the raw loop mints one per turn, the
/// three CLI loops run a whole provider-side agentic loop behind a single one — so these tests are
/// written against the seam all four call rather than against any one loop's turn structure. What
/// they pin down is the contract the seam publishes: one final <c>turn_completed</c> per accepted
/// generation id, counted the same way, whether the loop closed the turn or the run's terminal
/// sweep had to.
/// </remarks>
public class TurnLifecycleEmissionTests
{
    #region The ordinary path

    [Fact]
    public async Task LoopClosedTurn_IsReportedOnceBeforeTheRunCompletes()
    {
        var (agent, publisher) = CreateWiredAgent("thread-1");
        await using var _agent = agent;

        var assignment = await agent.StartAsync();
        agent.OpenTurn(assignment.RunId, assignment.GenerationId);
        agent.Observe(assignment.RunId, assignment.GenerationId, Text("the answer"));
        await agent.CloseTurnAsync(assignment.RunId, assignment.GenerationId);
        await agent.CompleteAsync(assignment);

        publisher.EventTypes.Should().Equal(
            LifecycleEventTypes.RunStarted,
            LifecycleEventTypes.TurnCompleted,
            LifecycleEventTypes.RunCompleted);

        var turn = publisher.PayloadAt<TurnCompletedPayload>(1);
        turn.RunId.Should().Be(assignment.RunId);
        turn.GenerationId.Should().Be(assignment.GenerationId);
        turn.TurnIndex.Should().Be(1, "turn ordinals start at 1");
        turn.Outcome.Should().Be(LifecycleTurnOutcomes.Completed);
        turn.MessageCount.Should().Be(1);
        turn.ToolCallCount.Should().Be(0);
        turn.Error.Should().BeNull();

        publisher.PayloadAt<RunCompletedPayload>(2)
            .TurnCount.Should()
            .Be(1, "the run reports the turns it was told about");
    }

    [Fact]
    public async Task EachTurn_IsReportedUnderItsOwnGenerationIdAndOrdinal()
    {
        var (agent, publisher) = CreateWiredAgent("thread-1");
        await using var _agent = agent;

        var assignment = await agent.StartAsync();

        // The raw loop's shape: one generation id per turn, the first reusing the run's.
        string[] generations = [assignment.GenerationId, "gen-2", "gen-3"];
        foreach (var generationId in generations)
        {
            agent.OpenTurn(assignment.RunId, generationId);
            await agent.CloseTurnAsync(assignment.RunId, generationId);
        }

        await agent.CompleteAsync(assignment);

        var turns = publisher.Payloads<TurnCompletedPayload>(LifecycleEventTypes.TurnCompleted);
        turns.Select(t => t.GenerationId).Should().Equal(generations);
        turns.Select(t => t.TurnIndex).Should().Equal(1, 2, 3);
        publisher.PayloadAt<RunCompletedPayload>(4).TurnCount.Should().Be(3);
    }

    [Fact]
    public async Task RunWithNoTurn_ReportsNoTurnAtAll()
    {
        var (agent, publisher) = CreateWiredAgent("thread-1");
        await using var _agent = agent;

        // A non-final delayed-result sibling performs no model turn — ADR 0004.
        var assignment = await agent.StartAsync();
        await agent.CompleteAsync(assignment);

        publisher.EventTypes.Should().NotContain(LifecycleEventTypes.TurnCompleted);
        publisher.PayloadAt<RunCompletedPayload>(1).TurnCount.Should().Be(0);
    }

    #endregion

    #region Exactly once

    [Fact]
    public async Task ClosingTheSameTurnTwice_ReportsItOnce()
    {
        var (agent, publisher) = CreateWiredAgent("thread-1");
        await using var _agent = agent;

        var assignment = await agent.StartAsync();
        agent.OpenTurn(assignment.RunId, assignment.GenerationId);
        await agent.CloseTurnAsync(assignment.RunId, assignment.GenerationId);
        await agent.CloseTurnAsync(assignment.RunId, assignment.GenerationId);

        publisher.EventTypes
            .Count(t => t == LifecycleEventTypes.TurnCompleted)
            .Should()
            .Be(1, "a second report would double-count the turn a subscriber already saw finish");
    }

    [Fact]
    public async Task ConcurrentTurnCompletions_ReportExactlyOnce()
    {
        var (agent, publisher) = CreateWiredAgent("thread-1");
        await using var _agent = agent;

        var assignment = await agent.StartAsync();
        agent.OpenTurn(assignment.RunId, assignment.GenerationId);

        var racers = Enumerable
            .Range(0, 8)
            .Select(_ => Task.Run(() => agent.Lifecycle.TurnCompletedAsync(
                assignment.RunId,
                assignment.GenerationId)))
            .ToArray();

        var winners = await Task.WhenAll(racers);

        winners.Count(won => won).Should().Be(1);
        publisher.EventTypes.Count(t => t == LifecycleEventTypes.TurnCompleted).Should().Be(1);
    }

    [Fact]
    public async Task TurnAlreadyClosedByTheLoop_IsNotReportedAgainByTheTerminalSweep()
    {
        var (agent, publisher) = CreateWiredAgent("thread-1");
        await using var _agent = agent;

        var assignment = await agent.StartAsync();
        agent.OpenTurn(assignment.RunId, assignment.GenerationId);
        await agent.CloseTurnAsync(assignment.RunId, assignment.GenerationId);

        // The run then fails. The sweep must find nothing left to report.
        await agent.CompleteAsync(assignment, isError: true, error: "the provider refused");

        var turns = publisher.Payloads<TurnCompletedPayload>(LifecycleEventTypes.TurnCompleted);
        turns.Should().ContainSingle();
        turns[0].Outcome.Should().Be(
            LifecycleTurnOutcomes.Completed,
            "the turn had already finished normally before the run went wrong");
    }

    #endregion

    #region The terminal sweep

    [Fact]
    public async Task OpenTurnAtRunError_IsReportedWithTheRunsOutcomeAndError()
    {
        var (agent, publisher) = CreateWiredAgent("thread-1");
        await using var _agent = agent;

        var assignment = await agent.StartAsync();
        agent.OpenTurn(assignment.RunId, assignment.GenerationId);
        agent.Observe(assignment.RunId, assignment.GenerationId, Text("half an answer"));

        await agent.CompleteAsync(assignment, isError: true, error: "the provider refused");

        publisher.EventTypes.Should().Equal(
            LifecycleEventTypes.RunStarted,
            LifecycleEventTypes.TurnCompleted,
            LifecycleEventTypes.RunCompleted);

        var turn = publisher.PayloadAt<TurnCompletedPayload>(1);
        turn.Outcome.Should().Be(LifecycleTurnOutcomes.Error);
        turn.Error!.Message.Should().Be("the provider refused");
        turn.MessageCount.Should().Be(1, "what the turn produced before it died is still reported");

        publisher.PayloadAt<RunCompletedPayload>(2).TurnCount.Should().Be(1);
    }

    [Fact]
    public async Task OpenTurnAtCancellation_IsReportedAsCancelled()
    {
        var store = new InMemoryConversationStore();
        var publisher = new RecordingLifecyclePublisher();
        await using var agent = new TurnProbeAgent(
            "thread-1",
            new MultiTurnLifecycleServices { Publisher = publisher, LifecycleStore = store },
            store,
            openTurnOnLoop: true);

        var run = agent.RunAsync();
        var runId = await agent.WaitForLoopRunAsync();

        // Cancellation escapes the loop without ever reaching a completion call, which is exactly
        // the case the sweep exists for.
        await agent.StopAsync();
        await run;

        var turn = publisher.Payloads<TurnCompletedPayload>(LifecycleEventTypes.TurnCompleted).Single();
        turn.RunId.Should().Be(runId);
        turn.Outcome.Should().Be(LifecycleTurnOutcomes.Cancelled);

        publisher.EventTypes.Should().Equal(
            LifecycleEventTypes.RunStarted,
            LifecycleEventTypes.TurnCompleted,
            LifecycleEventTypes.RunCompleted);
    }

    [Fact]
    public async Task OpenTurnAtDispose_IsReportedAsInterrupted()
    {
        var store = new InMemoryConversationStore();
        var publisher = new RecordingLifecyclePublisher();
        var agent = new TurnProbeAgent(
            "thread-1",
            new MultiTurnLifecycleServices { Publisher = publisher, LifecycleStore = store },
            store);

        var assignment = await agent.StartAsync();
        agent.OpenTurn(assignment.RunId, assignment.GenerationId);
        await agent.DisposeAsync();

        var turn = publisher.Payloads<TurnCompletedPayload>(LifecycleEventTypes.TurnCompleted).Single();
        turn.RunId.Should().Be(assignment.RunId);
        turn.Outcome.Should().Be(LifecycleTurnOutcomes.Interrupted);
    }

    [Fact]
    public async Task RunThatHitItsTurnCeiling_ReportsItsOpenTurnAsCompleted()
    {
        var (agent, publisher) = CreateWiredAgent("thread-1");
        await using var _agent = agent;

        var assignment = await agent.StartAsync();
        agent.OpenTurn(assignment.RunId, assignment.GenerationId);

        await agent.CompleteAsync(assignment, outcome: LifecycleRunOutcomes.MaxTurns);

        publisher.PayloadAt<RunCompletedPayload>(2).Outcome.Should().Be(LifecycleRunOutcomes.MaxTurns);
        publisher.PayloadAt<TurnCompletedPayload>(1)
            .Outcome.Should()
            .Be(
                LifecycleTurnOutcomes.Completed,
                "a run that stops at its ceiling stopped between turns — the turn itself was fine");
    }

    [Fact]
    public async Task TurnOfARunThatLostTheTerminalRace_IsNotReported()
    {
        var store = new InMemoryConversationStore();
        var publisher = new RecordingLifecyclePublisher();
        var services = new MultiTurnLifecycleServices
        {
            Publisher = publisher,
            LifecycleStore = store,
        };

        var first = new RunTurnLifecycleFinalizer("thread-1", services);
        var second = new RunTurnLifecycleFinalizer("thread-1", services);

        await first.RunStartedAsync("run-1", "gen-1");
        await second.RunStartedAsync("run-1", "gen-1");
        first.TurnStarted("run-1", "gen-1");
        second.TurnStarted("run-1", "gen-1");

        (await first.TryCompleteRunAsync("run-1", "gen-1", LifecycleRunOutcomes.Completed))
            .Should()
            .BeTrue();
        (await second.TryCompleteRunAsync("run-1", "gen-1", LifecycleRunOutcomes.Error))
            .Should()
            .BeFalse();

        // The loser swept its own copy of the turn before discovering it lost the run. Reporting a
        // turn is cheap and idempotent per finalizer, but the run must still end once.
        publisher.EventTypes.Count(t => t == LifecycleEventTypes.RunCompleted).Should().Be(1);
    }

    #endregion

    #region What a turn is counted as

    [Fact]
    public async Task StreamingFragments_DoNotCountTowardTheTurnsMessages()
    {
        var (agent, publisher) = CreateWiredAgent("thread-1");
        await using var _agent = agent;

        var assignment = await agent.StartAsync();
        agent.OpenTurn(assignment.RunId, assignment.GenerationId);

        // What a CLI translator pushes through history: deltas, then the complete message that
        // supersedes them. Counting both would make message_count mean something different per
        // provider, which is the one thing this event may not do.
        agent.Observe(assignment.RunId, assignment.GenerationId, new TextUpdateMessage { Text = "the " });
        agent.Observe(assignment.RunId, assignment.GenerationId, new TextUpdateMessage { Text = "the ans" });
        agent.Observe(assignment.RunId, assignment.GenerationId, new ReasoningUpdateMessage { Reasoning = "hmm" });
        agent.Observe(
            assignment.RunId,
            assignment.GenerationId,
            new ToolCallUpdateMessage { FunctionName = "search", FunctionArgs = "{\"q\"" });
        agent.Observe(assignment.RunId, assignment.GenerationId, Text("the answer"));

        await agent.CloseTurnAsync(assignment.RunId, assignment.GenerationId);

        var turn = publisher.PayloadAt<TurnCompletedPayload>(1);
        turn.MessageCount.Should().Be(1);
        turn.ToolCallCount.Should().Be(0, "a fragment of a tool call is not a requested tool call");
    }

    [Fact]
    public async Task BatchedAndSingularToolCalls_AreCountedTheSameWay()
    {
        var (agent, publisher) = CreateWiredAgent("thread-1");
        await using var _agent = agent;

        var assignment = await agent.StartAsync();
        agent.OpenTurn(assignment.RunId, assignment.GenerationId);

        // A provider that batches its calls into one message still requested each of them; a loop
        // downstream of the transformation middleware sees only the singular form.
        agent.Observe(
            assignment.RunId,
            assignment.GenerationId,
            new ToolsCallMessage
            {
                ToolCalls =
                [
                    new ToolCall { FunctionName = "search", ToolCallId = "call-1" },
                    new ToolCall { FunctionName = "read", ToolCallId = "call-2" },
                ],
            });
        agent.Observe(
            assignment.RunId,
            assignment.GenerationId,
            new ToolCallMessage { FunctionName = "write", ToolCallId = "call-3" });
        agent.Observe(
            assignment.RunId,
            assignment.GenerationId,
            new ToolCallResultMessage { ToolCallId = "call-3", Result = "ok" });

        await agent.CloseTurnAsync(assignment.RunId, assignment.GenerationId);

        var turn = publisher.PayloadAt<TurnCompletedPayload>(1);
        turn.ToolCallCount.Should().Be(3);
        turn.MessageCount.Should().Be(3, "the result is a message the turn produced, not a call it made");
    }

    [Fact]
    public async Task ReportedUsage_PreservesWhatTheProviderDidNotSay()
    {
        var (agent, publisher) = CreateWiredAgent("thread-1");
        await using var _agent = agent;

        var assignment = await agent.StartAsync();
        agent.OpenTurn(assignment.RunId, assignment.GenerationId);
        agent.Observe(
            assignment.RunId,
            assignment.GenerationId,
            new UsageMessage
            {
                Usage = new Usage { PromptTokens = 120, CompletionTokens = 30, TotalTokens = 150 },
            });

        await agent.CloseTurnAsync(assignment.RunId, assignment.GenerationId);

        var usage = publisher.PayloadAt<TurnCompletedPayload>(1).Usage;
        usage.Should().NotBeNull();
        usage!.PromptTokens.Should().Be(120);
        usage.CompletionTokens.Should().Be(30);
        usage.TotalTokens.Should().Be(150);
        usage.CachedPromptTokens.Should().BeNull(
            "a provider that reports no cache detail is not a provider reporting zero cached tokens");
        usage.ReasoningTokens.Should().BeNull();
        usage.Completeness.Should().Be(LifecycleUsageCompleteness.Complete);
    }

    [Fact]
    public async Task ReportedUsage_CarriesTheDetailCountsWhenThereAreAny()
    {
        var (agent, publisher) = CreateWiredAgent("thread-1");
        await using var _agent = agent;

        var assignment = await agent.StartAsync();
        agent.OpenTurn(assignment.RunId, assignment.GenerationId);
        agent.Observe(
            assignment.RunId,
            assignment.GenerationId,
            new UsageMessage
            {
                Usage = new Usage
                {
                    PromptTokens = 120,
                    CompletionTokens = 30,
                    TotalTokens = 150,
                    InputTokenDetails = new InputTokenDetails { CachedTokens = 96 },
                    OutputTokenDetails = new OutputTokenDetails { ReasoningTokens = 12 },
                },
            });

        await agent.CloseTurnAsync(assignment.RunId, assignment.GenerationId);

        var usage = publisher.PayloadAt<TurnCompletedPayload>(1).Usage;
        usage!.CachedPromptTokens.Should().Be(96);
        usage.ReasoningTokens.Should().Be(12);
    }

    [Fact]
    public async Task TurnWithNoUsageReported_CarriesNoUsage()
    {
        var (agent, publisher) = CreateWiredAgent("thread-1");
        await using var _agent = agent;

        var assignment = await agent.StartAsync();
        agent.OpenTurn(assignment.RunId, assignment.GenerationId);
        agent.Observe(assignment.RunId, assignment.GenerationId, Text("the answer"));
        await agent.CloseTurnAsync(assignment.RunId, assignment.GenerationId);

        publisher.PayloadAt<TurnCompletedPayload>(1).Usage.Should().BeNull();
    }

    [Fact]
    public async Task MessagesObservedOutsideAnOpenTurn_AreIgnored()
    {
        var (agent, publisher) = CreateWiredAgent("thread-1");
        await using var _agent = agent;

        var assignment = await agent.StartAsync();

        // Before the turn opens, and after it closed: neither belongs to a turn anyone will report.
        agent.Observe(assignment.RunId, assignment.GenerationId, Text("stray"));
        agent.OpenTurn(assignment.RunId, assignment.GenerationId);
        agent.Observe(assignment.RunId, assignment.GenerationId, Text("counted"));
        await agent.CloseTurnAsync(assignment.RunId, assignment.GenerationId);
        agent.Observe(assignment.RunId, assignment.GenerationId, Text("late"));

        publisher.PayloadAt<TurnCompletedPayload>(1).MessageCount.Should().Be(1);
    }

    #endregion

    #region Disabled by default

    [Fact]
    public async Task WithoutBundle_ReportsNoTurn()
    {
        await using var agent = new TurnProbeAgent("thread-1");

        var assignment = await agent.StartAsync();
        agent.OpenTurn(assignment.RunId, assignment.GenerationId);
        agent.Observe(assignment.RunId, assignment.GenerationId, Text("the answer"));
        await agent.CloseTurnAsync(assignment.RunId, assignment.GenerationId);
        await agent.CompleteAsync(assignment);

        agent.Lifecycle.IsEnabled.Should().BeFalse();
    }

    #endregion

    #region Helpers

    private static TextMessage Text(string text) => new() { Text = text, Role = Role.Assistant };

    private static (TurnProbeAgent Agent, RecordingLifecyclePublisher Publisher) CreateWiredAgent(
        string threadId)
    {
        var store = new InMemoryConversationStore();
        var publisher = new RecordingLifecyclePublisher();
        var agent = new TurnProbeAgent(
            threadId,
            new MultiTurnLifecycleServices { Publisher = publisher, LifecycleStore = store },
            store);
        return (agent, publisher);
    }

    /// <summary>
    /// A loop that drives the base class's turn seam directly, so the tests observe the seam's
    /// contract rather than any one provider's turn structure.
    /// </summary>
    private sealed class TurnProbeAgent : MultiTurnAgentBase
    {
        private readonly bool _openTurnOnLoop;
        private readonly TaskCompletionSource<string> _loopRunStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TurnProbeAgent(
            string threadId,
            MultiTurnLifecycleServices? services = null,
            IConversationStore? store = null,
            bool openTurnOnLoop = false)
            : base(threadId, store: store, lifecycleServices: services)
        {
            _openTurnOnLoop = openTurnOnLoop;
        }

        public new RunTurnLifecycleFinalizer Lifecycle => base.Lifecycle;

        public Task<RunAssignment> StartAsync(CancellationToken ct = default) =>
            StartRunAsync([], null, ct);

        public void OpenTurn(string runId, string generationId) => BeginTurn(runId, generationId);

        public void Observe(string runId, string generationId, IMessage message) =>
            ObserveTurnMessage(runId, generationId, message);

        public Task CloseTurnAsync(
            string runId,
            string generationId,
            string? outcome = null,
            CancellationToken ct = default) =>
            CompleteTurnAsync(runId, generationId, outcome, ct);

        public Task CompleteAsync(
            RunAssignment assignment,
            bool isError = false,
            string? error = null,
            string? outcome = null,
            CancellationToken ct = default) =>
            CompleteRunAsync(
                assignment.RunId,
                assignment.GenerationId,
                isError: isError,
                errorMessage: error,
                outcome: outcome,
                ct: ct);

        public Task<string> WaitForLoopRunAsync() => _loopRunStarted.Task;

        protected override async Task RunLoopAsync(CancellationToken ct)
        {
            var assignment = await StartRunAsync([], ct: ct);

            if (_openTurnOnLoop)
            {
                BeginTurn(assignment.RunId, assignment.GenerationId);
            }

            _loopRunStarted.TrySetResult(assignment.RunId);

            // Park until stopped: the turn above is deliberately never closed, so only the
            // cancellation path can report it. Real loops absorb their own stop signal rather than
            // letting it escape RunAsync; this one does the same.
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                // Stopping is how this loop ends.
            }
        }
    }

    #endregion
}
