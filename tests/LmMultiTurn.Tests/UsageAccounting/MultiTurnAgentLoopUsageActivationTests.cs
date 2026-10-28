using System.Runtime.CompilerServices;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmCore.Models;
using AchieveAi.LmDotnetTools.LmMultiTurn.Lifecycle;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using AchieveAi.LmDotnetTools.LmMultiTurn.UsageAccounting;
using AchieveAi.LmDotnetTools.LmTestUtils.Logging;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using Xunit.Abstractions;

namespace AchieveAi.LmDotnetTools.LmMultiTurn.Tests.UsageAccounting;

/// <summary>
/// End-to-end activation of conversation usage accounting in the live loop (#196): a run whose provider
/// reports a UsageMessage results in a persisted usage aggregate on the conversation.
/// </summary>
public class MultiTurnAgentLoopUsageActivationTests(ITestOutputHelper output) : LoggingTestBase(output)
{
    private readonly Mock<IStreamingAgent> _mockAgent = new();

    [Fact]
    public async Task CompleteRun_WaitsForUsageBeforeTerminalLedgerAndLifecycle()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new RecordingUsageStore(null, failuresRemaining: 0)
        {
            BeforeAppend = () =>
            {
                started.TrySetResult();
                return release.Task;
            },
        };
        await using var agent = new UsageCompletionProbe(store, Logger);
        var assignment = await agent.StartAsync();
        agent.ObserveUsage("gen-1", 100, 40);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var completion = agent.CompleteAsync(assignment);
        try
        {
            Logger.LogInformation("Usage append held: CompletionFinished={CompletionFinished}", completion.IsCompleted);
            completion.IsCompleted.Should().BeFalse("known usage is still held in storage");
            (await store.LoadRunLedgerAsync(assignment.RunId))!.Status.Should().Be(RunStatus.InProgress);
            (await store.Inner.LoadRunLifecycleAsync(assignment.RunId))!.Phase.Should().Be(RunLifecyclePhase.Running);
            store.Snapshot.Should().BeNull();
        }
        finally
        {
            release.TrySetResult();
            await completion;
        }

        (await store.LoadRunLedgerAsync(assignment.RunId))!.Status.Should().Be(RunStatus.Completed);
        (await store.Inner.LoadRunLifecycleAsync(assignment.RunId))!.Phase.Should().Be(RunLifecyclePhase.Terminal);
        var aggregate = await ConversationUsageProjection.LoadAsync(store, "usage-thread");
        aggregate!.TotalTokens.Should().Be(140);
        aggregate.Completeness.Should().Be(UsageCompleteness.InProgress, "the reusable conversation remains live");
        Logger.LogInformation(
            "Usage durable before completion: TotalTokens={TotalTokens}, Completeness={Completeness}",
            aggregate.TotalTokens,
            aggregate.Completeness
        );

        var nextAssignment = await agent.StartAsync();
        agent.ObserveUsage("gen-2", 20, 5);
        await agent.CompleteAsync(nextAssignment);
        var nextAggregate = await ConversationUsageProjection.LoadAsync(store, "usage-thread");
        nextAggregate!.TotalTokens.Should().Be(165);
        nextAggregate.Completeness.Should().Be(UsageCompleteness.InProgress);
    }

    [Fact]
    public async Task CompleteRun_DoesNotWaitForUsageObservedAfterCompletionStarted()
    {
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSecond = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        var store = new RecordingUsageStore(null, failuresRemaining: 0)
        {
            BeforeAppend = () =>
            {
                if (Interlocked.Increment(ref attempts) == 1)
                {
                    firstStarted.SetResult();
                    return releaseFirst.Task;
                }

                secondStarted.TrySetResult();
                return releaseSecond.Task;
            },
        };
        await using var agent = new UsageCompletionProbe(store, Logger);
        var assignment = await agent.StartAsync();
        agent.ObserveUsage("gen-1", 100, 40);
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // A descendant keeps reporting after completion began: it must not extend the captured boundary.
        var completion = agent.CompleteAsync(assignment);
        agent.ObserveUsage("later-gen", 20, 5);
        try
        {
            releaseFirst.SetResult();
            await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await completion.WaitAsync(TimeSpan.FromSeconds(5));
            (await store.LoadRunLedgerAsync(assignment.RunId))!
                .Status.Should()
                .Be(RunStatus.Completed, "the later write is still held but was not part of the boundary");
            Logger.LogInformation("Completion finished with a later usage write held: Attempts={Attempts}", attempts);
        }
        finally
        {
            releaseFirst.TrySetResult();
            releaseSecond.TrySetResult();
        }
    }

    [Fact]
    public async Task Dispose_WaitsForAllOwnedUsageAndTerminalCompleteness()
    {
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSecond = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        var store = new RecordingUsageStore(null, failuresRemaining: 0)
        {
            BeforeAppend = () =>
            {
                if (Interlocked.Increment(ref attempts) == 1)
                {
                    firstStarted.SetResult();
                    return releaseFirst.Task;
                }
                secondStarted.TrySetResult();
                return releaseSecond.Task;
            },
        };
        var agent = new UsageCompletionProbe(store, Logger);
        agent.ObserveUsage("gen-1", 100, 40);
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        agent.ObserveUsage("gen-2", 20, 5);
        var disposal = agent.DisposeAsync().AsTask();
        try
        {
            disposal.IsCompleted.Should().BeFalse();
            releaseFirst.SetResult();
            await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            disposal.IsCompleted.Should().BeFalse("the trailing usage and terminal completeness remain held");
        }
        finally
        {
            releaseFirst.TrySetResult();
            releaseSecond.TrySetResult();
            await disposal;
        }
        var aggregate = await ConversationUsageProjection.LoadAsync(store, "usage-thread");
        aggregate!.TotalTokens.Should().Be(165);
        aggregate.Completeness.Should().Be(UsageCompleteness.Complete);
        Logger.LogInformation(
            "Disposal usage durable: TotalTokens={TotalTokens}, Completeness={Completeness}",
            aggregate.TotalTokens,
            aggregate.Completeness
        );
    }

    [Fact]
    public async Task CompleteRun_StorageFailureStillCompletesRun_WithoutInventingUsage_ThenRetries()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        var store = new RecordingUsageStore(null, failuresRemaining: 1)
        {
            BeforeAppend = () =>
            {
                if (Interlocked.Increment(ref attempts) > 1)
                {
                    return Task.CompletedTask;
                }

                started.SetResult();
                return release.Task;
            },
        };
        await using var agent = new UsageCompletionProbe(store, Logger);
        var assignment = await agent.StartAsync();
        agent.ObserveUsage("gen-1", 100, 40);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var completion = agent.CompleteAsync(assignment);
        completion.IsCompleted.Should().BeFalse("the failing write is still held");
        release.SetResult();
        await completion.WaitAsync(TimeSpan.FromSeconds(10));

        // Usage persistence is best-effort (#196): the run still completes, but nothing is saved for it.
        (await store.LoadRunLedgerAsync(assignment.RunId))!
            .Status.Should()
            .Be(RunStatus.Completed);
        store.Snapshot.Should().BeNull("a failed write must not advance or invent durable usage");
        store.Calls.Should().ContainSingle().Which.Succeeded.Should().BeFalse();

        var nextAssignment = await agent.StartAsync();
        agent.ObserveUsage("gen-2", 20, 5);
        await agent.CompleteAsync(nextAssignment);
        var aggregate = await ConversationUsageProjection.LoadAsync(store, "usage-thread");
        aggregate!.TotalTokens.Should().Be(165, "the retained first observation is retried with the next write");
        Logger.LogInformation(
            "Usage recovered after failed completion write: TotalTokens={TotalTokens}, Attempts={Attempts}",
            aggregate.TotalTokens,
            attempts
        );
    }

    [Fact]
    public async Task CompleteRun_CancellationLeavesUsagePending_ForARecoveredBoundary()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new RecordingUsageStore(null, failuresRemaining: 0)
        {
            BeforeAppend = () =>
            {
                started.TrySetResult();
                return release.Task;
            },
        };
        await using var agent = new UsageCompletionProbe(store, Logger);
        var assignment = await agent.StartAsync();
        agent.ObserveUsage("gen-1", 100, 40);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using var cts = new CancellationTokenSource();
        var completion = agent.CompleteAsync(assignment, cts.Token);
        try
        {
            await cts.CancelAsync();
            var waitForCompletion = async () => await completion;
            await waitForCompletion.Should().ThrowAsync<OperationCanceledException>();
            (await store.LoadRunLedgerAsync(assignment.RunId))!.Status.Should().Be(RunStatus.InProgress);
            store.Snapshot.Should().BeNull();
        }
        finally
        {
            release.TrySetResult();
        }

        // No production loop retries completion after a cancelled wait: the stopped run is reported
        // Cancelled and reconciled as Interrupted on restart. This second call only proves the retained
        // observation is not lost and a later boundary can still persist it.
        await agent.CompleteAsync(assignment);
        (await ConversationUsageProjection.LoadAsync(store, "usage-thread"))!.TotalTokens.Should().Be(140);
        (await store.LoadRunLedgerAsync(assignment.RunId))!.Status.Should().Be(RunStatus.Completed);
    }

    [Fact]
    public async Task Run_PersistsUsageAggregate_FromPrimaryUsageMessage()
    {
        SetupMockAgentResponse([
            new UsageMessage
            {
                Usage = new Usage { PromptTokens = 100, CompletionTokens = 40 },
                GenerationId = "gen-1",
            },
            new TextMessage { Text = "done", Role = Role.Assistant },
        ]);

        var store = new InMemoryConversationStore();
        var registry = new FunctionRegistry();
        await using var loop = new MultiTurnAgentLoop(_mockAgent.Object, registry, "usage-thread", store: store);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        _ = loop.RunAsync(cts.Token);

        var userInput = new UserInput([new TextMessage { Text = "Hi", Role = Role.User }], InputId: "input-1");

        await foreach (var _ in loop.ExecuteRunAsync(userInput, cts.Token))
        {
            // drain the run to completion
        }

        // Persistence of the aggregate is fire-and-forget, so poll until it lands (bounded by cts).
        ConversationUsageAggregate? aggregate = null;
        while (aggregate is null)
        {
            aggregate = await ConversationUsageProjection.LoadAsync(store, "usage-thread");
            if (aggregate is null)
            {
                await Task.Delay(20, cts.Token);
            }
        }

        aggregate.Should().NotBeNull();
        aggregate!.TotalTokens.Should().Be(140);
        aggregate.RootConversationId.Should().Be("usage-thread");
        aggregate.PerModel.Should().ContainSingle();

        await cts.CancelAsync();
    }

    [Fact]
    public async Task Dispose_FlushesUsage_MakingItDurableWithoutPolling()
    {
        // The awaited flush at disposal is the durability boundary: a run's final usage must be persisted
        // synchronously by DisposeAsync, not left to a fire-and-forget write that may be lost at shutdown.
        SetupMockAgentResponse([
            new UsageMessage
            {
                Usage = new Usage { PromptTokens = 100, CompletionTokens = 40 },
                GenerationId = "gen-1",
            },
            new TextMessage { Text = "done", Role = Role.Assistant },
        ]);

        var store = new InMemoryConversationStore();
        var registry = new FunctionRegistry();
        var loop = new MultiTurnAgentLoop(_mockAgent.Object, registry, "usage-thread", store: store);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        _ = loop.RunAsync(cts.Token);

        var userInput = new UserInput([new TextMessage { Text = "Hi", Role = Role.User }], InputId: "input-1");

        await foreach (var _ in loop.ExecuteRunAsync(userInput, cts.Token))
        {
            // drain the run to completion
        }

        await loop.DisposeAsync();

        // No polling: if the disposal flush works, usage is durable the instant DisposeAsync returns.
        var aggregate = await ConversationUsageProjection.LoadAsync(store, "usage-thread");
        aggregate.Should().NotBeNull();
        aggregate!.TotalTokens.Should().Be(140);
    }

    [Fact]
    public async Task Dispose_MarksUsageComplete_OnTerminalFlush()
    {
        // A terminal loop (disposed after its run finished) must persist Completeness=Complete, not the
        // live InProgress default — otherwise the conversation-wide completeness state machine is dead and
        // no consumer can distinguish a finished conversation from a still-running one (#196, BUG 2).
        SetupMockAgentResponse([
            new UsageMessage
            {
                Usage = new Usage { PromptTokens = 100, CompletionTokens = 40 },
                GenerationId = "gen-1",
            },
            new TextMessage { Text = "done", Role = Role.Assistant },
        ]);

        var store = new InMemoryConversationStore();
        var registry = new FunctionRegistry();
        var loop = new MultiTurnAgentLoop(_mockAgent.Object, registry, "usage-thread", store: store);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        _ = loop.RunAsync(cts.Token);

        var userInput = new UserInput([new TextMessage { Text = "Hi", Role = Role.User }], InputId: "input-1");

        await foreach (var _ in loop.ExecuteRunAsync(userInput, cts.Token))
        {
            // drain the run to completion
        }

        await loop.DisposeAsync();

        var aggregate = await ConversationUsageProjection.LoadAsync(store, "usage-thread");
        aggregate.Should().NotBeNull();
        aggregate!.Completeness.Should().Be(UsageCompleteness.Complete);
    }

    [Fact]
    public async Task DuringRun_UsageAggregate_StaysInProgress()
    {
        // While the loop is still alive (not yet terminal), coalesced fire-and-forget writes must keep the
        // aggregate InProgress — a live conversation must not falsely advertise Complete (#196, BUG 2).
        SetupMockAgentResponse([
            new UsageMessage
            {
                Usage = new Usage { PromptTokens = 100, CompletionTokens = 40 },
                GenerationId = "gen-1",
            },
            new TextMessage { Text = "done", Role = Role.Assistant },
        ]);

        var store = new InMemoryConversationStore();
        var registry = new FunctionRegistry();
        await using var loop = new MultiTurnAgentLoop(_mockAgent.Object, registry, "usage-thread", store: store);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        _ = loop.RunAsync(cts.Token);

        var userInput = new UserInput([new TextMessage { Text = "Hi", Role = Role.User }], InputId: "input-1");

        await foreach (var _ in loop.ExecuteRunAsync(userInput, cts.Token))
        {
            // drain the run to completion; the loop stays alive waiting for more input
        }

        ConversationUsageAggregate? aggregate = null;
        while (aggregate is null)
        {
            aggregate = await ConversationUsageProjection.LoadAsync(store, "usage-thread");
            if (aggregate is null)
            {
                await Task.Delay(20, cts.Token);
            }
        }

        aggregate.Should().NotBeNull();
        aggregate!.Completeness.Should().Be(UsageCompleteness.InProgress);

        await cts.CancelAsync();
    }

    [Fact]
    public async Task RestartedCapabilityStore_RetriesFailedNewDelta_WithoutResendingHydratedRecords()
    {
        var oldRecord = new UsageRecord
        {
            LogicalCallId = "old",
            ProviderAttemptId = "usage-thread:old",
            RootConversationId = "usage-thread",
            RequestedModel = "model",
            InputTokens = 25,
            Revision = 7,
        };
        var store = new RecordingUsageStore(
            new ConversationUsageSnapshot
            {
                SchemaVersion = 1,
                FoldedRevision = 7,
                Completeness = UsageCompleteness.InProgress,
                Records = [oldRecord],
            },
            failuresRemaining: 1
        );
        SetupMockAgentResponse([
            new UsageMessage
            {
                Usage = new Usage { PromptTokens = 100, CompletionTokens = 40 },
                GenerationId = "new",
            },
            new TextMessage { Text = "done", Role = Role.Assistant },
        ]);
        var loop = new MultiTurnAgentLoop(_mockAgent.Object, new FunctionRegistry(), "usage-thread", store: store);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        _ = loop.RunAsync(cts.Token);

        await foreach (
            var _ in loop.ExecuteRunAsync(
                new UserInput([new TextMessage { Text = "Hi", Role = Role.User }], InputId: "input-1"),
                cts.Token
            )
        )
        {
            // drain the run to completion
        }

        await loop.DisposeAsync();

        var nonEmptyCalls = store.Calls.Where(call => call.RecordIds.Count > 0).ToList();
        nonEmptyCalls.Should().HaveCount(2, "the failed delta is retried once at the next durability boundary");
        nonEmptyCalls
            .Should()
            .OnlyContain(call =>
                call.RecordIds.SequenceEqual(new[] { "usage-thread:new" })
                && !call.RecordIds.Contains(oldRecord.ProviderAttemptId)
            );
        nonEmptyCalls.Count(call => call.Succeeded).Should().Be(1);
        store
            .Snapshot!.Records.Select(record => record.ProviderAttemptId)
            .Should()
            .BeEquivalentTo(["usage-thread:old", "usage-thread:new"]);
    }

    [Fact]
    public async Task RestartedCapabilityStore_PreservesAggregateOnlyUsage_UntilARecordArrives()
    {
        var legacyRecord = new UsageRecord
        {
            LogicalCallId = "legacy",
            ProviderAttemptId = "usage-thread:legacy",
            RootConversationId = "usage-thread",
            RequestedModel = "model",
            InputTokens = 75,
            OutputTokens = 25,
            Revision = 7,
        };
        var aggregate = ConversationUsageAggregate.Fold("usage-thread", [legacyRecord], 7, UsageCompleteness.Partial);
        var store = new RecordingUsageStore(
            new ConversationUsageSnapshot
            {
                SchemaVersion = 1,
                FoldedRevision = 7,
                Completeness = UsageCompleteness.Partial,
                Aggregate = aggregate,
                Records = [],
            },
            failuresRemaining: 0
        );
        SetupMockAgentResponse([new TextMessage { Text = "done", Role = Role.Assistant }]);
        var loop = new MultiTurnAgentLoop(_mockAgent.Object, new FunctionRegistry(), "usage-thread", store: store);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        _ = loop.RunAsync(cts.Token);

        await foreach (
            var _ in loop.ExecuteRunAsync(
                new UserInput([new TextMessage { Text = "Hi", Role = Role.User }], InputId: "input-1"),
                cts.Token
            )
        )
        {
            // drain the run to completion
        }

        await loop.DisposeAsync();

        store.Snapshot!.Aggregate.Should().BeEquivalentTo(aggregate);
        store.Snapshot.Records.Should().BeEmpty();
    }

    [Fact]
    public async Task Run_PublishesLiveUsageFrame_ReflectingFoldedTotal()
    {
        // The loop broadcasts a ConversationUsageMessage to subscribers as usage folds in, so the client
        // banner reflects it live rather than only after a reload of the persisted aggregate (#196, BUG 1b).
        SetupMockAgentResponse([
            new UsageMessage
            {
                Usage = new Usage { PromptTokens = 100, CompletionTokens = 40 },
                GenerationId = "gen-1",
            },
            new TextMessage { Text = "done", Role = Role.Assistant },
        ]);

        var store = new InMemoryConversationStore();
        var registry = new FunctionRegistry();
        await using var loop = new MultiTurnAgentLoop(_mockAgent.Object, registry, "usage-thread", store: store);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        _ = loop.RunAsync(cts.Token);

        var frames = new List<ConversationUsageMessage>();
        var subscribeTask = Task.Run(
            async () =>
            {
                await foreach (var msg in loop.SubscribeAsync(cts.Token))
                {
                    if (msg is ConversationUsageMessage frame)
                    {
                        frames.Add(frame);
                    }
                }
            },
            cts.Token
        );

        // Let the subscription register before the run produces usage.
        await Task.Delay(100, cts.Token);

        var userInput = new UserInput([new TextMessage { Text = "Hi", Role = Role.User }], InputId: "input-1");

        await foreach (var _ in loop.ExecuteRunAsync(userInput, cts.Token))
        {
            // drain the run to completion
        }

        // Poll for the frame to arrive on the subscription (delivery is asynchronous; bounded by cts).
        while (frames.Count == 0)
        {
            await Task.Delay(20, cts.Token);
        }

        frames.Should().NotBeEmpty();
        frames[^1].TotalTokens.Should().Be(140);
        frames[^1].ThreadId.Should().Be("usage-thread");

        await cts.CancelAsync();
    }

    private void SetupMockAgentResponse(List<IMessage> messages)
    {
        _mockAgent
            .Setup(a =>
                a.GenerateReplyStreamingAsync(
                    It.IsAny<IEnumerable<IMessage>>(),
                    It.IsAny<GenerateReplyOptions>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Returns(Task.FromResult(ToAsyncEnumerable(messages)));
    }

    private static async IAsyncEnumerable<IMessage> ToAsyncEnumerable(
        List<IMessage> messages,
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

    private sealed record AppendCall(IReadOnlyList<string> RecordIds, bool Succeeded);

    private sealed class UsageCompletionProbe : MultiTurnAgentBase
    {
        public UsageCompletionProbe(RecordingUsageStore store, ILogger logger)
            : base(
                "usage-thread",
                store: store,
                logger: logger,
                persistRunLedger: true,
                lifecycleServices: new MultiTurnLifecycleServices { LifecycleStore = store.Inner }
            )
        {
            UsageLedger = new UsageLedger(ThreadId);
        }

        public Task<RunAssignment> StartAsync() => StartRunAsync([]);

        public Task CompleteAsync(RunAssignment assignment, CancellationToken ct = default) =>
            CompleteRunAsync(assignment.RunId, assignment.GenerationId, ct: ct);

        public void ObserveUsage(string generationId, int inputTokens, int outputTokens) =>
            AddToHistory(
                new UsageMessage
                {
                    GenerationId = generationId,
                    Usage = new Usage { PromptTokens = inputTokens, CompletionTokens = outputTokens },
                }
            );

        protected override Task RunLoopAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class RecordingUsageStore : IConversationStore, IConversationUsageStore, IRunLedgerStore
    {
        private readonly InMemoryConversationStore _inner = new();
        private readonly object _gate = new();
        private readonly List<AppendCall> _calls = [];
        private int _failuresRemaining;
        private ConversationUsageSnapshot? _snapshot;

        public RecordingUsageStore(ConversationUsageSnapshot? snapshot, int failuresRemaining)
        {
            _snapshot = snapshot;
            _failuresRemaining = failuresRemaining;
        }

        public InMemoryConversationStore Inner => _inner;

        public Func<Task>? BeforeAppend { get; init; }

        public IReadOnlyList<AppendCall> Calls
        {
            get
            {
                lock (_gate)
                {
                    return [.. _calls];
                }
            }
        }

        public ConversationUsageSnapshot? Snapshot
        {
            get
            {
                lock (_gate)
                {
                    return _snapshot;
                }
            }
        }

        public async Task AppendUsageAsync(
            ConversationUsageAggregate aggregate,
            IReadOnlyList<UsageRecord> changedRecords,
            CancellationToken ct = default
        )
        {
            if (BeforeAppend is { } beforeAppend)
            {
                await beforeAppend();
            }

            lock (_gate)
            {
                var ids = changedRecords.Select(record => record.ProviderAttemptId).ToArray();
                if (_failuresRemaining > 0)
                {
                    _failuresRemaining--;
                    _calls.Add(new AppendCall(ids, Succeeded: false));
                    throw new InvalidOperationException("injected usage append failure");
                }

                var records = ConversationUsageProjection.MergeRecords(_snapshot?.Records ?? [], changedRecords);
                var persistedAggregate =
                    records.Count == 0
                        ? _snapshot?.Aggregate is { } current && current.FoldedRevision > aggregate.FoldedRevision
                            ? current
                            : aggregate
                        : null;
                _snapshot = new ConversationUsageSnapshot
                {
                    SchemaVersion = Math.Max(_snapshot?.SchemaVersion ?? 0, aggregate.SchemaVersion),
                    FoldedRevision = Math.Max(_snapshot?.FoldedRevision ?? 0, aggregate.FoldedRevision),
                    Completeness = (UsageCompleteness)
                        Math.Max(
                            (int)(_snapshot?.Completeness ?? UsageCompleteness.InProgress),
                            (int)aggregate.Completeness
                        ),
                    Aggregate = persistedAggregate,
                    Records = records,
                };
                _calls.Add(new AppendCall(ids, Succeeded: true));
            }
        }

        public Task UpsertRunLedgerAsync(RunLedgerEntry entry, CancellationToken ct = default) =>
            _inner.UpsertRunLedgerAsync(entry, ct);

        public Task<RunLedgerEntry?> LoadRunLedgerAsync(string runId, CancellationToken ct = default) =>
            _inner.LoadRunLedgerAsync(runId, ct);

        public Task<IReadOnlyList<RunLedgerEntry>> ListRunLedgerAsync(
            string threadId,
            CancellationToken ct = default
        ) => _inner.ListRunLedgerAsync(threadId, ct);

        public Task RecordAcceptedInputAsync(
            string threadId,
            string inputId,
            DateTimeOffset acceptedAt,
            CancellationToken ct = default
        ) => _inner.RecordAcceptedInputAsync(threadId, inputId, acceptedAt, ct);

        public Task RemoveAcceptedInputAsync(string threadId, string inputId, CancellationToken ct = default) =>
            _inner.RemoveAcceptedInputAsync(threadId, inputId, ct);

        public Task<IReadOnlySet<string>> ListAcceptedInputIdsAsync(string threadId, CancellationToken ct = default) =>
            _inner.ListAcceptedInputIdsAsync(threadId, ct);

        public Task<ConversationUsageSnapshot?> LoadUsageAsync(string threadId, CancellationToken ct = default) =>
            Task.FromResult(Snapshot);

        public Task AppendMessagesAsync(
            string threadId,
            IReadOnlyList<PersistedMessage> messages,
            CancellationToken ct = default
        ) => _inner.AppendMessagesAsync(threadId, messages, ct);

        public Task<IReadOnlyList<PersistedMessage>> LoadMessagesAsync(
            string threadId,
            CancellationToken ct = default
        ) => _inner.LoadMessagesAsync(threadId, ct);

        public Task ReplaceMessageAsync(
            string threadId,
            PersistedMessage replacement,
            CancellationToken ct = default
        ) => _inner.ReplaceMessageAsync(threadId, replacement, ct);

        public Task SaveMetadataAsync(string threadId, ThreadMetadata metadata, CancellationToken ct = default) =>
            _inner.SaveMetadataAsync(threadId, metadata, ct);

        public Task<ThreadMetadata?> LoadMetadataAsync(string threadId, CancellationToken ct = default) =>
            _inner.LoadMetadataAsync(threadId, ct);

        public Task UpdateMetadataAsync(
            string threadId,
            Func<ThreadMetadata?, ThreadMetadata> update,
            CancellationToken ct = default
        ) => _inner.UpdateMetadataAsync(threadId, update, ct);

        public Task DeleteThreadAsync(string threadId, CancellationToken ct = default) =>
            _inner.DeleteThreadAsync(threadId, ct);

        public Task<IReadOnlyList<ThreadMetadata>> ListThreadsAsync(
            int limit = 50,
            int offset = 0,
            ConversationListOptions? options = null,
            CancellationToken ct = default
        ) => _inner.ListThreadsAsync(limit, offset, options, ct);
    }
}
