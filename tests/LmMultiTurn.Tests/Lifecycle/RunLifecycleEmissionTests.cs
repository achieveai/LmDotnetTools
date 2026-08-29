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
/// Covers what a subscriber is allowed to assume about run-scoped lifecycle events: that a run that
/// started completes exactly once, that the pairing survives cancellation and process restart, and
/// that a loop wired without a bundle emits nothing at all.
/// </summary>
public class RunLifecycleEmissionTests
{
    #region Disabled by default

    [Fact]
    public async Task WithoutBundle_EmitsNothingAndWritesNothing()
    {
        var store = new InMemoryConversationStore();
        await using var agent = new LifecycleProbeAgent("thread-1", store: store);

        var assignment = await agent.StartAsync();
        await agent.CompleteAsync(assignment);

        agent.LifecycleEnabled.Should().BeFalse();
        var runs = await store.ListRunLifecycleAsync("thread-1");
        runs.Should().BeEmpty("an unwired loop must not pay for lifecycle it was never asked for");
    }

    [Fact]
    public async Task WithStoreOnly_PersistsRunButPublishesNothing()
    {
        var store = new InMemoryConversationStore();
        var publisher = new RecordingLifecyclePublisher();
        await using var agent = new LifecycleProbeAgent(
            "thread-1",
            new MultiTurnLifecycleServices { LifecycleStore = store },
            store
        );

        var assignment = await agent.StartAsync();
        await agent.CompleteAsync(assignment);

        publisher.Events.Should().BeEmpty();
        var run = await store.LoadRunLifecycleAsync(assignment.RunId);
        run.Should().NotBeNull();
        run!.Phase.Should().Be(RunLifecyclePhase.Terminal);
        run.Outcome.Should().Be(LifecycleRunOutcomes.Completed);
    }

    #endregion

    #region Start/complete pairing

    [Fact]
    public async Task StartThenComplete_EmitsPairedEventsInOrder()
    {
        var (agent, publisher, _) = CreateWiredAgent("thread-1");
        await using var _agent = agent;

        var assignment = await agent.StartAsync();
        await agent.CompleteAsync(assignment);

        publisher.EventTypes.Should().Equal(LifecycleEventTypes.RunStarted, LifecycleEventTypes.RunCompleted);

        var started = publisher.PayloadAt<RunStartedPayload>(0);
        started.RunId.Should().Be(assignment.RunId);
        started.GenerationId.Should().Be(assignment.GenerationId);
        started.AgentKind.Should().Be(LifecycleAgentKinds.Raw);
        started.Cause.Kind.Should().Be(LifecycleRunCauseKinds.UserInput);
        started.WasForked.Should().BeFalse();

        var completed = publisher.PayloadAt<RunCompletedPayload>(1);
        completed.RunId.Should().Be(assignment.RunId);
        completed.Outcome.Should().Be(LifecycleRunOutcomes.Completed);
        completed.Error.Should().BeNull();

        publisher.Events.Select(e => e.SourceStreamId).Should().AllBe(LifecycleSourceStream.ForThread("thread-1"));
        publisher.Events.Select(e => e.SourceSequence).Should().BeInAscendingOrder();
        publisher.Events.Select(e => e.EventId).Should().OnlyHaveUniqueItems();
        publisher.Events.Select(e => e.Correlation?.RunId).Should().AllBe(assignment.RunId);
    }

    [Fact]
    public async Task FailedRun_ReportsErrorOutcomeWithMessage()
    {
        var (agent, publisher, _) = CreateWiredAgent("thread-1");
        await using var _agent = agent;

        var assignment = await agent.StartAsync();
        await agent.CompleteAsync(assignment, isError: true, error: "the provider refused");

        var completed = publisher.PayloadAt<RunCompletedPayload>(1);
        completed.Outcome.Should().Be(LifecycleRunOutcomes.Error);
        completed.Error.Should().NotBeNull();
        completed.Error!.Message.Should().Be("the provider refused");
    }

    [Fact]
    public async Task CompletingTwice_EmitsRunCompletedOnce()
    {
        var (agent, publisher, _) = CreateWiredAgent("thread-1");
        await using var _agent = agent;

        var assignment = await agent.StartAsync();
        await agent.CompleteAsync(assignment);
        await agent.CompleteAsync(assignment);

        publisher
            .EventTypes.Count(t => t == LifecycleEventTypes.RunCompleted)
            .Should()
            .Be(1, "a subscriber pairs starts with completions and a second completion breaks the pairing");
    }

    [Fact]
    public async Task ConcurrentCompletions_TerminalizeExactlyOnce()
    {
        var (agent, publisher, _) = CreateWiredAgent("thread-1");
        await using var _agent = agent;

        var assignment = await agent.StartAsync();

        var racers = Enumerable
            .Range(0, 8)
            .Select(_ =>
                Task.Run(() =>
                    agent.Lifecycle.TryCompleteRunAsync(
                        assignment.RunId,
                        assignment.GenerationId,
                        LifecycleRunOutcomes.Completed
                    )
                )
            )
            .ToArray();

        var winners = await Task.WhenAll(racers);

        winners.Count(won => won).Should().Be(1);
        publisher.EventTypes.Count(t => t == LifecycleEventTypes.RunCompleted).Should().Be(1);
    }

    [Fact]
    public async Task SecondFinalizerOverSameStore_LosesTheDurableRace()
    {
        var store = new InMemoryConversationStore();
        var publisher = new RecordingLifecyclePublisher();
        var services = new MultiTurnLifecycleServices { Publisher = publisher, LifecycleStore = store };

        var first = new RunTurnLifecycleFinalizer("thread-1", services);
        var second = new RunTurnLifecycleFinalizer("thread-1", services);

        // Both incarnations believe they own the run — only the store can break the tie.
        await first.RunStartedAsync("run-1", "gen-1");
        await second.RunStartedAsync("run-1", "gen-1");

        var firstWon = await first.TryCompleteRunAsync("run-1", "gen-1", LifecycleRunOutcomes.Completed);
        var secondWon = await second.TryCompleteRunAsync("run-1", "gen-1", LifecycleRunOutcomes.Error);

        firstWon.Should().BeTrue();
        secondWon.Should().BeFalse();
        publisher.EventTypes.Count(t => t == LifecycleEventTypes.RunCompleted).Should().Be(1);
    }

    #endregion

    #region Cancellation and interruption

    [Fact]
    public async Task StopAsync_TerminalizesTheRunItInterrupted()
    {
        var store = new InMemoryConversationStore();
        var publisher = new RecordingLifecyclePublisher();
        await using var agent = new LifecycleProbeAgent(
            "thread-1",
            new MultiTurnLifecycleServices { Publisher = publisher, LifecycleStore = store },
            store
        );

        var run = agent.RunAsync();
        var runId = await agent.WaitForLoopRunAsync();

        await agent.StopAsync();
        await run;

        publisher.EventTypes.Should().Contain(LifecycleEventTypes.RunCompleted);
        var completed = publisher.Payloads<RunCompletedPayload>(LifecycleEventTypes.RunCompleted).Single();
        completed.RunId.Should().Be(runId);
        completed.Outcome.Should().Be(LifecycleRunOutcomes.Cancelled);

        var persisted = await store.LoadRunLifecycleAsync(runId);
        persisted!.Phase.Should().Be(RunLifecyclePhase.Terminal);
        persisted.Outcome.Should().Be(LifecycleRunOutcomes.Cancelled);
    }

    [Fact]
    public async Task DisposeWithoutStop_TerminalizesAsInterrupted()
    {
        var store = new InMemoryConversationStore();
        var publisher = new RecordingLifecyclePublisher();
        var agent = new LifecycleProbeAgent(
            "thread-1",
            new MultiTurnLifecycleServices { Publisher = publisher, LifecycleStore = store },
            store
        );

        var assignment = await agent.StartAsync();
        await agent.DisposeAsync();

        var completed = publisher.Payloads<RunCompletedPayload>(LifecycleEventTypes.RunCompleted).Single();
        completed.RunId.Should().Be(assignment.RunId);
        completed.Outcome.Should().Be(LifecycleRunOutcomes.Interrupted);
    }

    [Fact]
    public async Task RestartWithDanglingRun_ReconcilesItAsInterrupted()
    {
        var store = new InMemoryConversationStore();

        // A previous incarnation started a run and never came back.
        var abandoned = new RunTurnLifecycleFinalizer(
            "thread-1",
            new MultiTurnLifecycleServices { LifecycleStore = store }
        );
        await abandoned.RunStartedAsync("run-orphan", "gen-orphan");

        var publisher = new RecordingLifecyclePublisher();
        await using var agent = new LifecycleProbeAgent(
            "thread-1",
            new MultiTurnLifecycleServices { Publisher = publisher, LifecycleStore = store },
            store,
            startRunOnLoop: false
        );

        var run = agent.RunAsync();
        await agent.WaitForLoopEntryAsync();
        await agent.StopAsync();
        await run;

        var completed = publisher.Payloads<RunCompletedPayload>(LifecycleEventTypes.RunCompleted).Single();
        completed.RunId.Should().Be("run-orphan");
        completed.Outcome.Should().Be(LifecycleRunOutcomes.Interrupted);

        var persisted = await store.LoadRunLifecycleAsync("run-orphan");
        persisted!.Phase.Should().Be(RunLifecyclePhase.Terminal);
        persisted.Outcome.Should().Be(LifecycleRunOutcomes.Interrupted);

        (await store.ListNonTerminalRunsAsync("thread-1")).Should().BeEmpty();
    }

    [Fact]
    public async Task Reconciliation_RunsOnceEvenAcrossRestarts()
    {
        var store = new InMemoryConversationStore();
        var publisher = new RecordingLifecyclePublisher();
        await using var agent = new LifecycleProbeAgent(
            "thread-1",
            new MultiTurnLifecycleServices { Publisher = publisher, LifecycleStore = store },
            store,
            startRunOnLoop: false
        );

        for (var i = 0; i < 2; i++)
        {
            var run = agent.RunAsync();
            await agent.WaitForLoopEntryAsync();
            await agent.StopAsync();
            await run;
        }

        publisher.Events.Should().BeEmpty("there was nothing dangling to reconcile");
    }

    #endregion

    #region Lineage and identity

    [Fact]
    public async Task SpawnedAgent_StampsItsLineageOnEveryEvent()
    {
        var publisher = new RecordingLifecyclePublisher();
        await using var agent = new LifecycleProbeAgent(
            "thread-child",
            new MultiTurnLifecycleServices
            {
                Publisher = publisher,
                Lineage = new AgentLineage
                {
                    ParentThreadId = "thread-parent",
                    ParentRunId = "run-parent",
                    SpawningToolCallId = "call-spawn",
                    SubAgentId = "researcher",
                },
            }
        );

        var assignment = await agent.StartAsync();
        await agent.CompleteAsync(assignment);

        publisher.Events.Should().HaveCount(2);
        foreach (var correlation in publisher.Events.Select(e => e.Correlation))
        {
            correlation.Should().NotBeNull();
            correlation!.ThreadId.Should().Be("thread-child");
            correlation.ParentThreadId.Should().Be("thread-parent");
            correlation.ParentRunId.Should().Be("run-parent");
            correlation.SpawningToolCallId.Should().Be("call-spawn");
            correlation.SubAgentId.Should().Be("researcher");
        }
    }

    [Fact]
    public async Task ForkedRun_ReportsWasForkedAndTheParentRun()
    {
        var (agent, publisher, _) = CreateWiredAgent("thread-1");
        await using var _agent = agent;

        var assignment = await agent.StartAsync(parentRunId: "run-parent", wasForked: true);
        await agent.CompleteAsync(assignment);

        var started = publisher.PayloadAt<RunStartedPayload>(0);
        started.WasForked.Should().BeTrue();
        publisher.Events[0].Correlation!.ParentRunId.Should().Be("run-parent");
    }

    [Fact]
    public void ForAgent_LetsTheLoopNameItselfAndTheHostNameTheModel()
    {
        MultiTurnLifecycleServices
            .ForAgent(null, LifecycleAgentKinds.Claude)
            .Should()
            .BeSameAs(MultiTurnLifecycleServices.Disabled);

        MultiTurnLifecycleServices
            .ForAgent(MultiTurnLifecycleServices.Disabled, LifecycleAgentKinds.Claude)
            .Should()
            .BeSameAs(MultiTurnLifecycleServices.Disabled);

        var hostSupplied = new MultiTurnLifecycleServices
        {
            Publisher = new RecordingLifecyclePublisher(),
            AgentKind = LifecycleAgentKinds.Raw,
            ModelId = "resolved/model",
        };

        var stamped = MultiTurnLifecycleServices.ForAgent(hostSupplied, LifecycleAgentKinds.Codex, "loop-guess");

        stamped.AgentKind.Should().Be(LifecycleAgentKinds.Codex);
        stamped.ModelId.Should().Be("resolved/model");

        MultiTurnLifecycleServices
            .ForAgent(hostSupplied with { ModelId = null }, LifecycleAgentKinds.Codex, "loop-guess")
            .ModelId.Should()
            .Be("loop-guess");
    }

    #endregion

    #region Degradation

    [Fact]
    public async Task StoreFaultOnStart_StillPublishesBothEvents()
    {
        var publisher = new RecordingLifecyclePublisher();
        await using var agent = new LifecycleProbeAgent(
            "thread-1",
            new MultiTurnLifecycleServices { Publisher = publisher, LifecycleStore = new ThrowingRunLifecycleStore() }
        );

        var assignment = await agent.StartAsync();
        await agent.CompleteAsync(assignment);

        publisher.EventTypes.Should().Equal(LifecycleEventTypes.RunStarted, LifecycleEventTypes.RunCompleted);
        publisher.PayloadAt<RunCompletedPayload>(1).RunId.Should().Be(assignment.RunId);
    }

    [Fact]
    public async Task PublisherFault_DoesNotBreakTheRun()
    {
        var store = new InMemoryConversationStore();
        await using var agent = new LifecycleProbeAgent(
            "thread-1",
            new MultiTurnLifecycleServices { Publisher = new ThrowingLifecyclePublisher(), LifecycleStore = store },
            store
        );

        var assignment = await agent.StartAsync();
        await agent.CompleteAsync(assignment);

        var persisted = await store.LoadRunLifecycleAsync(assignment.RunId);
        persisted!.Outcome.Should().Be(LifecycleRunOutcomes.Completed);
    }

    #endregion

    #region Helpers

    private static (
        LifecycleProbeAgent Agent,
        RecordingLifecyclePublisher Publisher,
        InMemoryConversationStore Store
    ) CreateWiredAgent(string threadId)
    {
        var store = new InMemoryConversationStore();
        var publisher = new RecordingLifecyclePublisher();
        var agent = new LifecycleProbeAgent(
            threadId,
            new MultiTurnLifecycleServices { Publisher = publisher, LifecycleStore = store },
            store
        );
        return (agent, publisher, store);
    }

    /// <summary>
    /// A loop that does nothing but drive the base class's run bookkeeping, so the tests observe the
    /// lifecycle wiring rather than any provider's turn behavior.
    /// </summary>
    private sealed class LifecycleProbeAgent : MultiTurnAgentBase
    {
        private readonly bool _startRunOnLoop;
        private readonly TaskCompletionSource<string> _loopRunStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        private readonly TaskCompletionSource _loopEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public LifecycleProbeAgent(
            string threadId,
            MultiTurnLifecycleServices? services = null,
            IConversationStore? store = null,
            bool startRunOnLoop = true
        )
            : base(threadId, store: store, lifecycleServices: services)
        {
            _startRunOnLoop = startRunOnLoop;
        }

        public bool LifecycleEnabled => Lifecycle.IsEnabled;

        public new RunTurnLifecycleFinalizer Lifecycle => base.Lifecycle;

        public Task<RunAssignment> StartAsync(
            string? parentRunId = null,
            bool wasForked = false,
            CancellationToken ct = default
        ) => StartRunAsync([], parentRunId, ct, wasForked);

        public Task CompleteAsync(
            RunAssignment assignment,
            bool isError = false,
            string? error = null,
            CancellationToken ct = default
        ) => CompleteRunAsync(assignment.RunId, assignment.GenerationId, isError: isError, errorMessage: error, ct: ct);

        public Task<string> WaitForLoopRunAsync() => _loopRunStarted.Task;

        public Task WaitForLoopEntryAsync() => _loopEntered.Task;

        protected override async Task RunLoopAsync(CancellationToken ct)
        {
            _loopEntered.TrySetResult();

            if (_startRunOnLoop)
            {
                var assignment = await StartRunAsync([], ct: ct);
                _loopRunStarted.TrySetResult(assignment.RunId);
            }

            // Park until stopped: the run above is deliberately never completed, so the only thing
            // that can pair it with a terminal event is the cancellation path. Real loops absorb
            // their own stop signal rather than letting it escape RunAsync; this one does the same.
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

    private sealed class ThrowingLifecyclePublisher : ILifecyclePublisher
    {
        public ValueTask PublishAsync(LifecycleEventEnvelope envelope, CancellationToken ct = default) =>
            throw new InvalidOperationException("the subscriber is gone");
    }

    private sealed class ThrowingRunLifecycleStore : IRunLifecycleStore
    {
        public Task RecordRunStartedAsync(RunLifecycleState state, CancellationToken ct = default) =>
            throw new InvalidOperationException("the lifecycle store is unavailable");

        public Task<RunLifecycleState?> LoadRunLifecycleAsync(string runId, CancellationToken ct = default) =>
            throw new InvalidOperationException("the lifecycle store is unavailable");

        public Task<IReadOnlyList<RunLifecycleState>> ListRunLifecycleAsync(
            string threadId,
            CancellationToken ct = default
        ) => throw new InvalidOperationException("the lifecycle store is unavailable");

        public Task<IReadOnlyList<RunLifecycleState>> ListNonTerminalRunsAsync(
            string threadId,
            CancellationToken ct = default
        ) => throw new InvalidOperationException("the lifecycle store is unavailable");

        public Task<bool> TryMarkRunTerminalAsync(
            string runId,
            string outcome,
            int turnCount,
            DateTimeOffset terminalAt,
            CancellationToken ct = default
        ) => throw new InvalidOperationException("the lifecycle store is unavailable");

        public Task<DeferredToolCallRecord> RecordDeferredToolCallAsync(
            string runId,
            DeferredToolCallRecord record,
            CancellationToken ct = default
        ) => throw new InvalidOperationException("the lifecycle store is unavailable");

        public Task<DeferredResolutionOutcome> TryResolveDeferredToolCallAsync(
            string threadId,
            string toolCallId,
            string resolutionFingerprint,
            string? childRunId,
            DateTimeOffset resolvedAt,
            CancellationToken ct = default
        ) => throw new InvalidOperationException("the lifecycle store is unavailable");

        public Task<string?> AttachDeferredChildRunAsync(
            string threadId,
            string toolCallId,
            string childRunId,
            DateTimeOffset attachedAt,
            CancellationToken ct = default
        ) => throw new InvalidOperationException("the lifecycle store is unavailable");
    }

    #endregion
}
