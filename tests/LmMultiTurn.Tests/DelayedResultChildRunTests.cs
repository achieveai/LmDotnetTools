using System.Runtime.CompilerServices;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmLifecycle;
using AchieveAi.LmDotnetTools.LmLifecycle.Payloads;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.Lifecycle;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using FluentAssertions;
using LmMultiTurn.Tests.Lifecycle;
using Moq;
using Xunit;

namespace LmMultiTurn.Tests;

/// <summary>
/// Covers what a delayed tool result does to the run graph: the outcome a resolver is told, the
/// child run each resolution causes, and the order a lifecycle subscriber sees it all in.
/// </summary>
/// <remarks>
/// <para>
/// The rules under test come from ADR 0004. A resolution never joins the run that requested it —
/// that run is over — so each one gets its own child run whose cause is the real
/// <see cref="ToolCallResultMessage"/>. Only the child that clears the <em>last</em> outstanding
/// call may talk to the provider, because the request carries the whole history and one unfilled
/// placeholder anywhere in it makes the request invalid.
/// </para>
/// <para>
/// Separate from <c>DeferredToolExecutionTests</c>, which covers the deferral itself — the
/// placeholder, the registry, and restoring both from persisted history.
/// </para>
/// </remarks>
public class DelayedResultChildRunTests
{
    private readonly Mock<IStreamingAgent> _mockAgent = new();

    #region Resolution outcomes (T4.7)

    [Fact]
    public async Task RedeliveringTheSameResult_IsADuplicate_NotAConflict()
    {
        await using var harness = await Harness.StartAsync(_mockAgent, "tc_1");

        var first = await harness.Loop.TryResolveToolCallAsync("tc_1", "the-answer");
        var second = await harness.Loop.TryResolveToolCallAsync("tc_1", "the-answer");

        first.Should().Be(ResolveToolCallOutcome.Resolved);
        second
            .Should()
            .Be(
                ResolveToolCallOutcome.Duplicate,
                "a webhook that retries after a lost acknowledgement must not be told it failed"
            );
    }

    [Fact]
    public async Task ASecondResultThatDisagrees_Conflicts_AndTheFirstStands()
    {
        await using var harness = await Harness.StartAsync(_mockAgent, "tc_1");

        _ = await harness.Loop.TryResolveToolCallAsync("tc_1", "the-answer");
        var conflicting = await harness.Loop.TryResolveToolCallAsync("tc_1", "a-different-answer");

        conflicting.Should().Be(ResolveToolCallOutcome.Conflict);
        var stored = await LoadResultsAsync(harness.Store, harness.ThreadId);
        stored
            .Single(m => m.ToolCallId == "tc_1")
            .Result.Should()
            .Be("the-answer", "the committed resolution wins; a later disagreement is rejected, not applied");
    }

    [Fact]
    public async Task ResolvingAToolCallThatWasNeverDeferred_IsNotFound()
    {
        await using var harness = await Harness.StartAsync(_mockAgent, "tc_1");

        var outcome = await harness.Loop.TryResolveToolCallAsync("tc_nonexistent", "whatever");

        outcome.Should().Be(ResolveToolCallOutcome.NotFound, "retrying will never make an unknown tool call exist");
    }

    [Fact]
    public async Task WhenTheStoreRefuses_TheCallStaysDeferredAndTheSameResultLandsOnRetry()
    {
        var store = new FailableLifecycleStore(new InMemoryConversationStore());
        await using var harness = await Harness.StartAsync(_mockAgent, "tc_1", lifecycleStore: store);

        store.FailResolutions = true;
        var refused = await harness.Loop.TryResolveToolCallAsync("tc_1", "the-answer");

        refused.Should().Be(ResolveToolCallOutcome.StoreFailed);
        var pending = await harness.Loop.GetDeferredToolCallsAsync();
        pending
            .Should()
            .ContainSingle(
                p => p.ToolCallId == "tc_1",
                "a resolution the store would not take did not happen, so the call is still outstanding"
            );

        store.FailResolutions = false;
        var retried = await harness.Loop.TryResolveToolCallAsync("tc_1", "the-answer");

        retried
            .Should()
            .Be(
                ResolveToolCallOutcome.Resolved,
                "the retry is the first resolution to commit, so it must not be mistaken for a duplicate"
            );
    }

    [Fact]
    public async Task WhenTheCallerWithdrawsMidWrite_TheResolutionIsCancelled_NotBlamedOnTheStore()
    {
        var store = new FailableLifecycleStore(new InMemoryConversationStore());
        await using var harness = await Harness.StartAsync(_mockAgent, "tc_1", lifecycleStore: store);
        using var caller = new CancellationTokenSource();

        // The caller goes away while its own write is in flight: the request it arrived on completes,
        // its token is cancelled, and the store observes that token and stops.
        store.BeforeResolve = ct =>
        {
            caller.Cancel();
            throw new OperationCanceledException(ct);
        };

        var outcome = await harness.Loop.TryResolveToolCallAsync("tc_1", "the-answer", ct: caller.Token);

        outcome
            .Should()
            .Be(
                ResolveToolCallOutcome.Cancelled,
                "the delivery was withdrawn by the caller, and saying anything else would suggest the "
                    + "store is unhealthy when it is not"
            );
        var pending = await harness.Loop.GetDeferredToolCallsAsync();
        pending.Should().ContainSingle(p => p.ToolCallId == "tc_1");
    }

    [Fact]
    public async Task WhenTheStoreCancelsItself_TheResolutionIsAStoreFailure_AndTheRetryLands()
    {
        var store = new FailableLifecycleStore(new InMemoryConversationStore());
        await using var harness = await Harness.StartAsync(_mockAgent, "tc_1", lifecycleStore: store);
        var failed = false;

        // The caller is not cancelling anything. The store times out internally — a connection
        // deadline, a linked token of its own — and that surfaces as the same exception type.
        store.BeforeResolve = _ => failed ? Task.CompletedTask : throw new OperationCanceledException();
        var refused = await harness.Loop.TryResolveToolCallAsync("tc_1", "the-answer");

        refused
            .Should()
            .Be(
                ResolveToolCallOutcome.StoreFailed,
                "a cancellation the caller did not ask for is the store failing, and calling it "
                    + "Cancelled would invite the caller to drop a result the store would take"
            );
        var pending = await harness.Loop.GetDeferredToolCallsAsync();
        pending.Should().ContainSingle(p => p.ToolCallId == "tc_1", "the state left behind is retry-safe");

        failed = true;
        var retried = await harness.Loop.TryResolveToolCallAsync("tc_1", "the-answer");

        retried.Should().Be(ResolveToolCallOutcome.Resolved);
    }

    [Fact]
    public async Task ConcurrentDeliveriesOfOneResult_CommitExactlyOnce()
    {
        await using var harness = await Harness.StartAsync(_mockAgent, "tc_1");

        var racers = await Task.WhenAll(
            Enumerable
                .Range(0, 8)
                .Select(_ => Task.Run(() => harness.Loop.TryResolveToolCallAsync("tc_1", "the-answer")))
        );

        racers
            .Count(o => o == ResolveToolCallOutcome.Resolved)
            .Should()
            .Be(1, "exactly one delivery may commit; the rest are the same result arriving again");
        racers.Should().OnlyContain(o => o == ResolveToolCallOutcome.Resolved || o == ResolveToolCallOutcome.Duplicate);

        await harness.WaitForProviderCallsAsync(2);
        harness
            .ProviderCallCount.Should()
            .Be(2, "one commit means one continuation, however many callers raced to deliver it");
    }

    [Fact]
    public async Task ADeferralRestoredFromAnotherProcess_ResolvesNormally()
    {
        var store = new InMemoryConversationStore();
        string threadId;

        await using (var first = await Harness.StartAsync(_mockAgent, "tc_1", store: store))
        {
            threadId = first.ThreadId;
            var pending = await first.Loop.GetDeferredToolCallsAsync();
            pending.Should().ContainSingle();
        }

        // A new loop over the same store stands in for the process that replaces the one that died.
        var mockAgent = new Mock<IStreamingAgent>();
        Harness.SetupProvider(mockAgent, "tc_1");
        await using var restored = new MultiTurnAgentLoop(
            mockAgent.Object,
            Harness.BuildRegistry("tc_1"),
            threadId,
            store: store
        );
        (await restored.RecoverAsync()).Should().BeTrue();

        var outcome = await restored.TryResolveToolCallAsync("tc_1", "the-answer");

        outcome
            .Should()
            .Be(
                ResolveToolCallOutcome.Resolved,
                "the deferral outlived the process that created it, so its resolution must still land"
            );
        var stored = await LoadResultsAsync(store, threadId);
        stored.Single(m => m.ToolCallId == "tc_1").IsDeferred.Should().BeFalse();
    }

    #endregion

    #region One child per result (T4.8)

    [Fact]
    public async Task EachResolvedResultGetsItsOwnChildRun_AndOnlyTheLastTalksToTheProvider()
    {
        var publisher = new RecordingLifecyclePublisher();
        await using var harness = await Harness.StartAsync(_mockAgent, ["tc_a", "tc_b", "tc_c"], publisher: publisher);

        await harness.Loop.ResolveToolCallAsync("tc_a", "result-a");
        await harness.Loop.ResolveToolCallAsync("tc_b", "result-b");
        await harness.Loop.ResolveToolCallAsync("tc_c", "result-c");
        await harness.WaitForProviderCallsAsync(2);

        var runsStarted = publisher.Payloads<RunStartedPayload>(LifecycleEventTypes.RunStarted);
        var children = runsStarted.Where(r => r.Cause.Kind == LifecycleRunCauseKinds.ToolResult).ToList();
        children.Should().HaveCount(3, "one child run per resolved result — no batching, no skipping");
        children.Select(c => c.RunId).Should().OnlyHaveUniqueItems();
        children.Select(c => c.Cause.ToolCallId).Should().BeEquivalentTo(["tc_a", "tc_b", "tc_c"]);
        children.Should().OnlyContain(c => !c.WasForked, "a delayed result continues a line, it does not branch one");

        harness
            .ProviderCallCount.Should()
            .Be(2, "only the child that cleared the last outstanding call may build a provider request");

        var completions = publisher
            .Payloads<RunCompletedPayload>(LifecycleEventTypes.RunCompleted)
            .Where(c => children.Any(child => child.RunId == c.RunId))
            .ToList();
        completions
            .Count(c => c.Outcome == LifecycleRunOutcomes.AwaitingSiblingResults)
            .Should()
            .Be(2, "the two non-final siblings finish honestly: waiting, not done");
        completions.Should().ContainSingle(c => c.Outcome == LifecycleRunOutcomes.Completed);

        // The provider call count above says the siblings never reached a model. TurnCount says the
        // same thing in the vocabulary a subscriber actually reads: a run that took no turn reports
        // none, so "waiting" and "did nothing" are the same story told twice rather than two stories.
        completions
            .Where(c => c.Outcome == LifecycleRunOutcomes.AwaitingSiblingResults)
            .Should()
            .OnlyContain(c => c.TurnCount == 0, "a sibling that waits performs no model turn");
        completions
            .Single(c => c.Outcome == LifecycleRunOutcomes.Completed)
            .TurnCount.Should()
            .BeGreaterThan(0, "the child that cleared the last outstanding call is the one that runs a turn");
    }

    [Fact]
    public async Task ResolutionOrderDoesNotDecideTheOwner_TheLastOneOutstandingDoes()
    {
        var publisher = new RecordingLifecyclePublisher();
        await using var harness = await Harness.StartAsync(_mockAgent, ["tc_a", "tc_b"], publisher: publisher);

        // Deliberately backwards: the call requested second resolves first.
        await harness.Loop.ResolveToolCallAsync("tc_b", "result-b");
        harness.ProviderCallCount.Should().Be(1, "tc_a is still outstanding");

        await harness.Loop.ResolveToolCallAsync("tc_a", "result-a");
        await harness.WaitForProviderCallsAsync(2);

        var owner = publisher
            .Payloads<RunStartedPayload>(LifecycleEventTypes.RunStarted)
            .Single(r => r.Cause.ToolCallId == "tc_a");
        publisher
            .Payloads<RunCompletedPayload>(LifecycleEventTypes.RunCompleted)
            .Single(c => c.RunId == owner.RunId)
            .Outcome.Should()
            .Be(LifecycleRunOutcomes.Completed, "ownership follows what is left outstanding, not arrival order");
    }

    #endregion

    #region The cause is the real result (T4.9)

    [Fact]
    public async Task TheChildCarriesTheRealToolResult_AndTheProviderSeesItExactlyOnce()
    {
        var publisher = new RecordingLifecyclePublisher();
        await using var harness = await Harness.StartAsync(_mockAgent, "tc_1", publisher: publisher);

        await harness.Loop.ResolveToolCallAsync("tc_1", "the-answer");
        await harness.WaitForProviderCallsAsync(2);

        var child = publisher
            .Payloads<RunStartedPayload>(LifecycleEventTypes.RunStarted)
            .Should()
            .ContainSingle(r => r.Cause.Kind == LifecycleRunCauseKinds.ToolResult)
            .Subject;
        child
            .Cause.ToolCallId.Should()
            .Be("tc_1", "the cause is the tool call that resolved — nothing synthetic is invented to stand in for it");

        harness.SecondRequest.Should().NotBeNull();
        harness
            .SecondRequest!.OfType<TextMessage>()
            .Should()
            .NotContain(
                m => m.Role == Role.User && m.Text != "Go",
                "no fabricated user message is appended to explain the result"
            );

        ResultsFor(harness.SecondRequest!, "tc_1")
            .Should()
            .ContainSingle(
                "the resolution fills the placeholder in place; carrying it as a cause must not append it again"
            )
            .Which.Should()
            .Be("the-answer");
    }

    [Fact]
    public async Task NoSiblingResultIsAppendedTwice_WhenSeveralResolve()
    {
        await using var harness = await Harness.StartAsync(_mockAgent, ["tc_a", "tc_b"]);

        await harness.Loop.ResolveToolCallAsync("tc_a", "result-a");
        await harness.Loop.ResolveToolCallAsync("tc_b", "result-b");
        await harness.WaitForProviderCallsAsync(2);

        harness.SecondRequest.Should().NotBeNull();
        ResultsFor(harness.SecondRequest!, "tc_a").Should().ContainSingle().Which.Should().Be("result-a");
        ResultsFor(harness.SecondRequest!, "tc_b").Should().ContainSingle().Which.Should().Be("result-b");
    }

    #endregion

    #region Event ordering (T4.10)

    [Fact]
    public async Task ToolCompletedForTheOriginatingRun_PrecedesTheChildRunsEvents()
    {
        var publisher = new RecordingLifecyclePublisher();
        await using var harness = await Harness.StartAsync(_mockAgent, "tc_1", publisher: publisher);

        await harness.Loop.ResolveToolCallAsync("tc_1", "the-answer");
        await harness.WaitForProviderCallsAsync(2);

        var toolCompleted = publisher
            .Payloads<ToolCompletedPayload>(LifecycleEventTypes.ToolCompleted)
            .Should()
            .ContainSingle(p => p.WasDeferred)
            .Subject;
        toolCompleted
            .RunId.Should()
            .Be(
                harness.OriginatingRunId,
                "the tool belongs to the run that asked for it, however long the answer took to arrive"
            );
        toolCompleted.ToolCallId.Should().Be("tc_1");
        toolCompleted.Outcome.Should().Be(LifecycleToolOutcomes.Succeeded);

        var child = publisher
            .Payloads<RunStartedPayload>(LifecycleEventTypes.RunStarted)
            .Single(r => r.Cause.Kind == LifecycleRunCauseKinds.ToolResult);

        // Indices in publication order: the completion of the tool has to be visible before anything
        // it caused, or a subscriber sees a run whose reason for existing has not happened yet. The
        // child's turn sits between its own two run events, so the whole causal chain — tool
        // finished, run began because of it, turn ran, run ended — reads in order.
        var events = publisher.Events;
        var toolCompletedAt = events
            .Select((e, i) => (e, i))
            .First(x => x.e.EventType == LifecycleEventTypes.ToolCompleted)
            .i;
        var childStartedAt = IndexOf(events, LifecycleEventTypes.RunStarted, child.RunId);
        var childTurnAt = IndexOf(events, LifecycleEventTypes.TurnCompleted, child.RunId);
        var childCompletedAt = IndexOf(events, LifecycleEventTypes.RunCompleted, child.RunId);

        toolCompletedAt.Should().BeLessThan(childStartedAt);
        childStartedAt.Should().BeLessThan(childTurnAt);
        childTurnAt.Should().BeLessThan(childCompletedAt);
    }

    [Fact]
    public async Task AFailedDelayedResult_ReportsTheToolAsFailed()
    {
        var publisher = new RecordingLifecyclePublisher();
        await using var harness = await Harness.StartAsync(_mockAgent, "tc_1", publisher: publisher);

        await harness.Loop.ResolveToolCallAsync("tc_1", "the upstream service rejected it", isError: true);
        await harness.WaitForProviderCallsAsync(2);

        var toolCompleted = publisher
            .Payloads<ToolCompletedPayload>(LifecycleEventTypes.ToolCompleted)
            .Should()
            .ContainSingle(p => p.WasDeferred)
            .Subject;
        toolCompleted.Outcome.Should().Be(LifecycleToolOutcomes.Failed);
        toolCompleted.Error.Should().NotBeNull();
        toolCompleted.Error!.Message.Should().Be("the upstream service rejected it");
    }

    #endregion

    #region Helpers

    /// <summary>The tool results a thread actually has on disk, rehydrated from persistence.</summary>
    private static async Task<IReadOnlyList<ToolCallResultMessage>> LoadResultsAsync(
        IConversationStore store,
        string threadId
    )
    {
        var persisted = await store.LoadMessagesAsync(threadId);
        return [.. MessagePersistenceConverter.FromPersistedMessages(persisted).OfType<ToolCallResultMessage>()];
    }

    private static int IndexOf(
        IReadOnlyList<AchieveAi.LmDotnetTools.LmLifecycle.LifecycleEventEnvelope> events,
        string eventType,
        string runId
    )
    {
        var index = events
            .Select((e, i) => (e, i))
            .Where(x => x.e.EventType == eventType && x.e.Correlation?.RunId == runId)
            .Select(x => (int?)x.i)
            .FirstOrDefault();
        index.Should().NotBeNull($"expected a {eventType} event for run {runId}");
        return index!.Value;
    }

    /// <summary>Every result carried for <paramref name="toolCallId"/>, however it was framed.</summary>
    /// <remarks>
    /// A provider request may carry tool results singly or aggregated, and "appended twice" has to be
    /// caught across both shapes — counting only one of them would miss the duplicate.
    /// </remarks>
    private static IReadOnlyList<string> ResultsFor(IReadOnlyList<IMessage> request, string toolCallId) =>
        [
            .. request.OfType<ToolCallResultMessage>().Where(m => m.ToolCallId == toolCallId).Select(m => m.Result),
            .. request
                .OfType<ToolsCallResultMessage>()
                .SelectMany(m => m.ToolCallResults)
                .Where(r => r.ToolCallId == toolCallId)
                .Select(r => r.Result),
        ];

    /// <summary>
    /// A loop whose first turn deferred every tool call it made, parked and waiting for results.
    /// </summary>
    /// <remarks>
    /// Every test here starts from that same state, and getting there takes a mock provider, a
    /// registry of deferring handlers, a subscription, and a wait for the first run to end. Repeating
    /// that per test buries the behaviour under arrangement and gives the run-completion race four
    /// more places to be got subtly wrong.
    /// </remarks>
    private sealed class Harness : IAsyncDisposable
    {
        /// <summary>
        /// How long a wait here may block before it reports a hang instead of continuing to hope.
        /// </summary>
        /// <remarks>
        /// Every wait below returns on a signal, so this bound is never reached on a run that works
        /// and its size does not trade against flakiness — it exists only so a genuine deadlock
        /// fails readably rather than hanging the suite. It is deliberately far larger than any
        /// observed duration (the whole class runs in about a second) because the one thing a
        /// backstop must not do is fire on a loaded machine and be read as a behavioural failure.
        /// </remarks>
        private static readonly TimeSpan WaitBudget = TimeSpan.FromSeconds(30);

        private readonly CancellationTokenSource _cts = new();
        private readonly TaskCompletionSource<bool> _firstRunCompleted = new();
        private readonly List<string> _completedRunIds = [];
        private readonly Lock _gate = new();

        /// <summary>One permit per provider call, released once that call's bookkeeping is visible.</summary>
        private readonly SemaphoreSlim _providerCalled = new(0);

        /// <summary>One permit per run-completed message the subscription has recorded.</summary>
        private readonly SemaphoreSlim _runCompleted = new(0);

        private readonly int _deferredAtStart;
        private int _providerCallCount;

        private Harness(MultiTurnAgentLoop loop, IConversationStore store, string threadId, int deferredAtStart)
        {
            Loop = loop;
            Store = store;
            ThreadId = threadId;
            _deferredAtStart = deferredAtStart;
        }

        public MultiTurnAgentLoop Loop { get; }

        public IConversationStore Store { get; }

        public string ThreadId { get; }

        /// <summary>The run that made the deferring tool calls.</summary>
        public string OriginatingRunId { get; private set; } = string.Empty;

        /// <summary>What the provider was handed on its second call, once one happens.</summary>
        public IReadOnlyList<IMessage>? SecondRequest { get; private set; }

        public int ProviderCallCount => Volatile.Read(ref _providerCallCount);

        public static Task<Harness> StartAsync(
            Mock<IStreamingAgent> mockAgent,
            string toolCallId,
            IConversationStore? store = null,
            IRunLifecycleStore? lifecycleStore = null,
            RecordingLifecyclePublisher? publisher = null
        ) => StartAsync(mockAgent, [toolCallId], store, lifecycleStore, publisher);

        public static async Task<Harness> StartAsync(
            Mock<IStreamingAgent> mockAgent,
            IReadOnlyList<string> toolCallIds,
            IConversationStore? store = null,
            IRunLifecycleStore? lifecycleStore = null,
            RecordingLifecyclePublisher? publisher = null
        )
        {
            store ??= new InMemoryConversationStore();
            var threadId = $"thread-{Guid.NewGuid():N}";

            var services =
                publisher == null && lifecycleStore == null
                    ? null
                    : new MultiTurnLifecycleServices
                    {
                        Publisher = publisher ?? (ILifecyclePublisher)NullLifecyclePublisher.Instance,
                        LifecycleStore = lifecycleStore,
                    };

            var loop = new MultiTurnAgentLoop(
                mockAgent.Object,
                BuildRegistry(toolCallIds),
                threadId,
                store: store,
                lifecycleServices: services
            );

            var harness = new Harness(loop, store, threadId, toolCallIds.Count);
            SetupProvider(mockAgent, toolCallIds, harness);

            _ = loop.RunAsync(harness._cts.Token);
            harness.Observe();

            await loop.SendAsync([new TextMessage { Text = "Go", Role = Role.User }]);
            await harness._firstRunCompleted.Task.WaitAsync(WaitBudget);

            var pending = await loop.GetDeferredToolCallsAsync();
            pending.Should().HaveCount(toolCallIds.Count, "the harness starts from a fully parked run");
            return harness;
        }

        /// <summary>
        /// Waits until the provider has been called <paramref name="count"/> times and the runs
        /// those calls belong to have finished publishing.
        /// </summary>
        /// <remarks>
        /// Both halves wait on the event they need rather than on a clock, which is the whole point
        /// of the rewrite. The first half consumes a permit released by the provider mock itself,
        /// so it returns the instant the call lands; the version before it sampled a counter every
        /// 20ms against a 5s wall clock and failed once in CI having seen a single call, which on a
        /// two-core runner says the poll was starved rather than that the loop misbehaved. The
        /// second half waits for exactly the number of run-completed messages the resolves imply,
        /// replacing a fixed 150ms sleep: a sleep long enough to be safe is dead time on every run
        /// and still not safe on the run that matters.
        /// </remarks>
        public async Task WaitForProviderCallsAsync(int count)
        {
            // Permits accumulate, so a call that landed before this method was entered is still
            // counted — the wait cannot miss a signal by arriving after it.
            for (var seen = 0; seen < count; seen++)
            {
                (await _providerCalled.WaitAsync(WaitBudget))
                    .Should()
                    .BeTrue(
                        $"the provider should have been called {count} times, but call {seen + 1} "
                            + $"never arrived within {WaitBudget}"
                    );
            }

            ProviderCallCount.Should().BeGreaterThanOrEqualTo(count);

            // One completion for the originating run, plus one per resolve that actually committed.
            // Derived rather than guessed: a resolve the store refused stays deferred and starts no
            // child run, so it must not be waited for.
            var expectedCompletions = 1 + (_deferredAtStart - (await Loop.GetDeferredToolCallsAsync()).Count);
            for (var seen = 0; seen < expectedCompletions; seen++)
            {
                (await _runCompleted.WaitAsync(WaitBudget))
                    .Should()
                    .BeTrue(
                        $"{expectedCompletions} run(s) should have completed, but completion "
                            + $"{seen + 1} never arrived within {WaitBudget}"
                    );
            }
        }

        public static FunctionRegistry BuildRegistry(params string[] toolCallIds) =>
            BuildRegistry((IReadOnlyList<string>)toolCallIds);

        public static FunctionRegistry BuildRegistry(IReadOnlyList<string> toolCallIds)
        {
            var registry = new FunctionRegistry();
            foreach (var name in toolCallIds.Select(ToolNameFor))
            {
                registry.AddFunction(
                    new FunctionContract
                    {
                        Name = name,
                        Description = $"Defers, for {name}",
                        Parameters = [],
                    },
                    (_, _, _) => Task.FromResult<ToolHandlerResult>(new ToolHandlerResult.Deferred())
                );
            }

            return registry;
        }

        public static void SetupProvider(Mock<IStreamingAgent> mockAgent, params string[] toolCallIds) =>
            SetupProvider(mockAgent, toolCallIds, harness: null);

        private static void SetupProvider(
            Mock<IStreamingAgent> mockAgent,
            IReadOnlyList<string> toolCallIds,
            Harness? harness
        )
        {
            var calls = 0;
            mockAgent
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
                        var call = Interlocked.Increment(ref calls);
                        if (harness != null)
                        {
                            Interlocked.Increment(ref harness._providerCallCount);
                        }

                        IAsyncEnumerable<IMessage> reply;
                        if (call == 1)
                        {
                            reply = ToAsyncEnumerable([
                                .. toolCallIds.Select(id => new ToolCallMessage
                                {
                                    FunctionName = ToolNameFor(id),
                                    FunctionArgs = "{}",
                                    ToolCallId = id,
                                    Role = Role.Assistant,
                                }),
                            ]);
                        }
                        else
                        {
                            if (harness != null && call == 2)
                            {
                                harness.SecondRequest = [.. msgs];
                            }

                            reply = ToAsyncEnumerable([new TextMessage { Text = "all done", Role = Role.Assistant }]);
                        }

                        // Released last, after everything this call records. A waiter that woke on the
                        // counter alone could observe the call and read a SecondRequest not yet
                        // assigned; releasing here both orders the two and publishes the write.
                        harness?._providerCalled.Release();
                        return Task.FromResult(reply);
                    }
                );
        }

        private static string ToolNameFor(string toolCallId) => $"defer_{toolCallId}";

        private void Observe()
        {
            var messages = Loop.SubscribeAsync(_cts.Token).GetAsyncEnumerator(_cts.Token);
            var first = messages.MoveNextAsync();

            // Not the harness token: a cancelled one would skip the body and leave the pending move
            // unobserved. Enumerating starts on this thread so the subscription is attached before
            // the caller sends anything — a late subscriber gets no replay and would wait forever.
            _ = Task.Run(
                async () =>
                {
                    try
                    {
                        for (var has = await first; has; has = await messages.MoveNextAsync())
                        {
                            if (messages.Current is not RunCompletedMessage completed)
                            {
                                continue;
                            }

                            lock (_gate)
                            {
                                _completedRunIds.Add(completed.CompletedRunId);
                            }

                            if (_firstRunCompleted.TrySetResult(true))
                            {
                                OriginatingRunId = completed.CompletedRunId;
                            }

                            // Released after the recording, so a waiter that wakes on it sees the run
                            // it woke for already in the list.
                            _runCompleted.Release();
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        // Cancelling the token is how the harness ends the subscription.
                    }
                    finally
                    {
                        await messages.DisposeAsync();
                    }
                },
                CancellationToken.None
            );
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            await Loop.DisposeAsync();
            _cts.Dispose();

            // The two semaphores are deliberately not disposed. Both are released from work that
            // outlives this call — the subscription pump and the provider mock — so disposing them
            // would trade a clean teardown for an ObjectDisposedException on a background task.
            // Neither ever allocates a wait handle, so there is nothing to leak.
        }

        private static async IAsyncEnumerable<IMessage> ToAsyncEnumerable(
            IEnumerable<IMessage> messages,
            [EnumeratorCancellation] CancellationToken ct = default
        )
        {
            foreach (var msg in messages)
            {
                ct.ThrowIfCancellationRequested();
                yield return msg;
                await Task.Yield();
            }
        }
    }

    /// <summary>
    /// A lifecycle store that can be told to refuse resolutions, or to do something of the test's
    /// choosing part-way through one, so the loop's behaviour on a store failure is exercised rather
    /// than assumed.
    /// </summary>
    private sealed class FailableLifecycleStore(InMemoryConversationStore inner) : IRunLifecycleStore
    {
        /// <summary>When set, <see cref="TryResolveDeferredToolCallAsync"/> throws instead of committing.</summary>
        public bool FailResolutions { get; set; }

        /// <summary>
        /// Runs inside <see cref="TryResolveDeferredToolCallAsync"/> before it commits anything, so a
        /// test can decide what the store appears to do — including throwing.
        /// </summary>
        public Func<CancellationToken, Task>? BeforeResolve { get; set; }

        public Task RecordRunStartedAsync(RunLifecycleState state, CancellationToken ct = default) =>
            inner.RecordRunStartedAsync(state, ct);

        public Task<RunLifecycleState?> LoadRunLifecycleAsync(string runId, CancellationToken ct = default) =>
            inner.LoadRunLifecycleAsync(runId, ct);

        public Task<IReadOnlyList<RunLifecycleState>> ListRunLifecycleAsync(
            string threadId,
            CancellationToken ct = default
        ) => inner.ListRunLifecycleAsync(threadId, ct);

        public Task<IReadOnlyList<RunLifecycleState>> ListNonTerminalRunsAsync(
            string threadId,
            CancellationToken ct = default
        ) => inner.ListNonTerminalRunsAsync(threadId, ct);

        public Task<bool> TryMarkRunTerminalAsync(
            string runId,
            string outcome,
            int turnCount,
            DateTimeOffset terminalAt,
            CancellationToken ct = default
        ) => inner.TryMarkRunTerminalAsync(runId, outcome, turnCount, terminalAt, ct);

        public Task<DeferredToolCallRecord> RecordDeferredToolCallAsync(
            string runId,
            DeferredToolCallRecord record,
            CancellationToken ct = default
        ) => inner.RecordDeferredToolCallAsync(runId, record, ct);

        public async Task<DeferredResolutionOutcome> TryResolveDeferredToolCallAsync(
            string threadId,
            string toolCallId,
            string resolutionFingerprint,
            string? childRunId,
            DateTimeOffset resolvedAt,
            CancellationToken ct = default
        )
        {
            if (BeforeResolve != null)
            {
                await BeforeResolve(ct);
            }

            return FailResolutions
                ? throw new InvalidOperationException("the store is unavailable")
                : await inner.TryResolveDeferredToolCallAsync(
                    threadId,
                    toolCallId,
                    resolutionFingerprint,
                    childRunId,
                    resolvedAt,
                    ct
                );
        }

        public Task<string?> AttachDeferredChildRunAsync(
            string threadId,
            string toolCallId,
            string childRunId,
            DateTimeOffset attachedAt,
            CancellationToken ct = default
        ) => inner.AttachDeferredChildRunAsync(threadId, toolCallId, childRunId, attachedAt, ct);
    }

    #endregion
}
