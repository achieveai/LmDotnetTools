using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using FluentAssertions;
using Xunit;

namespace LmMultiTurn.Tests.Persistence;

/// <summary>
/// Integration-level tests for <see cref="MultiTurnAgentBase"/>'s own read/write-through
/// behavior against <see cref="IRunLedgerStore"/> — the durable Queued/InProgress mint, the
/// terminal (Completed/Errored) transition, and injected-input folding. Raw store CRUD is
/// already covered by the per-store test classes; these exercise the base class's contract.
/// </summary>
public class RunLedgerRoundTripTests
{
    #region Constructor gating

    [Fact]
    public async Task Constructor_WithPersistRunLedgerAndLedgerStore_PersistsRunToLedger()
    {
        // No public getter for RunLedgerStore, so prove it became non-null via effect: a run
        // driven through the loop ends up in the ledger for this thread.
        var store = new InMemoryConversationStore();
        const string threadId = "roundtrip-ledger-on";

        var agent = new LedgerTestAgent(threadId, store: store, persistRunLedger: true);
        using var cts = new CancellationTokenSource();
        _ = agent.RunAsync(cts.Token);

        await agent.SendAsync([new TextMessage { Text = "Hi", Role = Role.User }], inputId: "input-1");

        var persisted = await WaitUntilAsync(
            async () => (await store.ListRunLedgerAsync(threadId)).Count > 0,
            TimeSpan.FromSeconds(5));

        persisted.Should().BeTrue("a persistRunLedger:true agent must write a ledger row for its run");
        (await store.ListRunLedgerAsync(threadId)).Should().ContainSingle(e => e.InputIds.Contains("input-1"));

        await cts.CancelAsync();
        await agent.DisposeAsync();
    }

    [Fact]
    public void Constructor_WithPersistRunLedgerAndNullStore_Throws()
    {
        var act = () => new LedgerTestAgent("thread", store: null, persistRunLedger: true);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Constructor_WithPersistRunLedgerAndNonLedgerStore_Throws()
    {
        // A store that implements IConversationStore but NOT IRunLedgerStore must be rejected.
        var act = () => new LedgerTestAgent("thread", store: new ConversationOnlyStore(), persistRunLedger: true);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Constructor_WithPersistRunLedgerFalse_AndNonLedgerStore_DoesNotThrow()
    {
        // Guard: the gating only fires when persistRunLedger is on; a plain store is fine otherwise.
        var act = () => new LedgerTestAgent("thread", store: new ConversationOnlyStore(), persistRunLedger: false);

        act.Should().NotThrow();
    }

    #endregion

    #region StartRunAsync / CompleteRunAsync write-through

    [Fact]
    public async Task StartRunAsync_WritesInProgressLedgerEntry_WithBatchInputIds()
    {
        // Pause the loop right after StartRunAsync returns (before CompleteRunAsync) so we can
        // observe the InProgress row that StartRunAsync leaves behind.
        var store = new InMemoryConversationStore();
        const string threadId = "roundtrip-inprogress";

        var agent = new LedgerTestAgent(
            threadId,
            store: store,
            persistRunLedger: true,
            pauseBeforeComplete: true);
        using var cts = new CancellationTokenSource();
        _ = agent.RunAsync(cts.Token);

        await agent.SendAsync([new TextMessage { Text = "Hi", Role = Role.User }], inputId: "input-A");
        await agent.SendAsync([new TextMessage { Text = "There", Role = Role.User }], inputId: "input-B");

        await agent.RunStarted.WaitAsync(TimeSpan.FromSeconds(5));

        var entry = await store.LoadRunLedgerAsync(agent.LastRunId!);
        entry.Should().NotBeNull();
        entry!.Status.Should().Be(RunStatus.InProgress);
        entry.InputIds.Should().BeEquivalentTo(["input-A", "input-B"]);

        // Cleanup: cancellation unblocks the paused loop.
        await cts.CancelAsync();
        await agent.DisposeAsync();
    }

    [Fact]
    public async Task CompleteRunAsync_MarksLedgerCompleted_PreservingInputIds()
    {
        var store = new InMemoryConversationStore();
        const string threadId = "roundtrip-completed";

        var agent = new LedgerTestAgent(
            threadId,
            store: store,
            persistRunLedger: true,
            messagesToReturn: [new TextMessage { Text = "Reply", Role = Role.Assistant }]);
        using var cts = new CancellationTokenSource();
        _ = agent.RunAsync(cts.Token);

        await agent.SendAsync([new TextMessage { Text = "Hi", Role = Role.User }], inputId: "input-C");

        var completed = await WaitUntilAsync(
            async () => (await store.ListRunLedgerAsync(threadId))
                .Any(e => e.InputIds.Contains("input-C") && e.Status == RunStatus.Completed),
            TimeSpan.FromSeconds(5));

        completed.Should().BeTrue();
        var entry = (await store.ListRunLedgerAsync(threadId)).Single(e => e.InputIds.Contains("input-C"));
        entry.Status.Should().Be(RunStatus.Completed);
        entry.InputIds.Should().Contain("input-C");

        await cts.CancelAsync();
        await agent.DisposeAsync();
    }

    [Fact]
    public async Task CompleteRunAsync_WithError_MarksLedgerErrored_PreservingInputIds()
    {
        var store = new InMemoryConversationStore();
        const string threadId = "roundtrip-errored";

        var agent = new LedgerTestAgent(
            threadId,
            store: store,
            persistRunLedger: true,
            completeWithError: true);
        using var cts = new CancellationTokenSource();
        _ = agent.RunAsync(cts.Token);

        await agent.SendAsync([new TextMessage { Text = "Hi", Role = Role.User }], inputId: "input-D");

        var errored = await WaitUntilAsync(
            async () => (await store.ListRunLedgerAsync(threadId))
                .Any(e => e.InputIds.Contains("input-D") && e.Status == RunStatus.Errored),
            TimeSpan.FromSeconds(5));

        errored.Should().BeTrue();
        var entry = (await store.ListRunLedgerAsync(threadId)).Single(e => e.InputIds.Contains("input-D"));
        entry.Status.Should().Be(RunStatus.Errored);
        entry.InputIds.Should().Contain("input-D");

        await cts.CancelAsync();
        await agent.DisposeAsync();
    }

    #endregion

    #region RecordInjectedInputsAsync

    [Fact]
    public async Task RecordInjectedInputsAsync_UnionsNewIds_WithoutDroppingOriginals()
    {
        var store = new InMemoryConversationStore();
        const string threadId = "roundtrip-inject";
        const string runId = "run-inject";
        var createdAt = DateTimeOffset.UtcNow;
        await store.UpsertRunLedgerAsync(new RunLedgerEntry(
            threadId, runId, RunStatus.InProgress, ["orig-1", "orig-2"], createdAt, createdAt));

        await using var agent = new LedgerTestAgent(threadId, store: store, persistRunLedger: true);

        await agent.TestRecordInjectedInputsAsync(runId, ["inj-1", "inj-2"]);

        var entry = await store.LoadRunLedgerAsync(runId);
        entry.Should().NotBeNull();
        entry!.InputIds.Should().Contain(["orig-1", "orig-2", "inj-1", "inj-2"]);
    }

    [Fact]
    public async Task RecordInjectedInputsAsync_DoesNotDuplicate_WhenInjectedIdAlreadyPresent()
    {
        var store = new InMemoryConversationStore();
        const string threadId = "roundtrip-inject-dup";
        const string runId = "run-inject-dup";
        var createdAt = DateTimeOffset.UtcNow;
        await store.UpsertRunLedgerAsync(new RunLedgerEntry(
            threadId, runId, RunStatus.InProgress, ["orig-1"], createdAt, createdAt));

        await using var agent = new LedgerTestAgent(threadId, store: store, persistRunLedger: true);

        await agent.TestRecordInjectedInputsAsync(runId, ["orig-1", "inj-1"]);

        var entry = await store.LoadRunLedgerAsync(runId);
        entry!.InputIds.Should().BeEquivalentTo(["orig-1", "inj-1"]);
        entry.InputIds.Count(id => id == "orig-1").Should().Be(1);
    }

    [Fact]
    public async Task RecordInjectedInputsAsync_IsNoOp_WhenLedgerPersistenceDisabled()
    {
        var store = new InMemoryConversationStore();
        const string threadId = "roundtrip-inject-off";

        await using var agent = new LedgerTestAgent(threadId, store: store, persistRunLedger: false);

        // RunLedgerStore is null (persistRunLedger:false) -> no throw, no store mutation.
        var act = () => agent.TestRecordInjectedInputsAsync("run-x", ["inj-1"]);
        await act.Should().NotThrowAsync();
        (await store.LoadRunLedgerAsync("run-x")).Should().BeNull();
        (await store.ListRunLedgerAsync(threadId)).Should().BeEmpty();
    }

    [Fact]
    public async Task RecordInjectedInputsAsync_IsNoOp_WhenInjectedIdsEmpty()
    {
        var store = new InMemoryConversationStore();
        const string threadId = "roundtrip-inject-empty";
        const string runId = "run-inject-empty";
        var createdAt = DateTimeOffset.UtcNow;
        await store.UpsertRunLedgerAsync(new RunLedgerEntry(
            threadId, runId, RunStatus.InProgress, ["orig-1"], createdAt, createdAt));

        await using var agent = new LedgerTestAgent(threadId, store: store, persistRunLedger: true);

        await agent.TestRecordInjectedInputsAsync(runId, []);

        // Empty injected list returns before any store write: InputIds and UpdatedAt untouched.
        var entry = await store.LoadRunLedgerAsync(runId);
        entry!.InputIds.Should().BeEquivalentTo(["orig-1"]);
        entry.UpdatedAt.Should().Be(createdAt);
    }

    #endregion

    #region Helpers

    private static async Task<bool> WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
            {
                return true;
            }

            await Task.Delay(20);
        }

        return await condition();
    }

    #endregion

    #region Test doubles

    /// <summary>
    /// Minimal concrete <see cref="MultiTurnAgentBase"/> whose loop drives one run per drained
    /// batch and exposes ledger-relevant state/hooks to tests. Threads <c>persistRunLedger</c>
    /// through to the base so ledger write-through can be exercised end to end.
    /// </summary>
    private sealed class LedgerTestAgent : MultiTurnAgentBase
    {
        private readonly List<IMessage> _messagesToReturn;
        private readonly bool _completeWithError;
        private readonly bool _pauseBeforeComplete;
        private readonly TaskCompletionSource _runStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseGate =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public LedgerTestAgent(
            string threadId,
            IConversationStore? store = null,
            bool persistRunLedger = false,
            List<IMessage>? messagesToReturn = null,
            bool completeWithError = false,
            bool pauseBeforeComplete = false)
            : base(threadId, store: store, persistRunLedger: persistRunLedger)
        {
            _messagesToReturn = messagesToReturn ?? [];
            _completeWithError = completeWithError;
            _pauseBeforeComplete = pauseBeforeComplete;
        }

        /// <summary>Run id of the first run once <see cref="MultiTurnAgentBase.StartRunAsync"/> returned.</summary>
        public string? LastRunId { get; private set; }

        /// <summary>Completes when the first run's <c>StartRunAsync</c> has returned.</summary>
        public Task RunStarted => _runStarted.Task;

        /// <summary>Test-only bridge to the protected <c>RecordInjectedInputsAsync</c>.</summary>
        public Task TestRecordInjectedInputsAsync(
            string runId,
            IReadOnlyList<string> injectedInputIds,
            CancellationToken ct = default)
            => RecordInjectedInputsAsync(runId, injectedInputIds, ct);

        protected override TimeSpan FallbackGracePeriod => TimeSpan.FromMilliseconds(100);

        protected override async Task RunLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                if (!await InputReader.WaitToReadAsync(ct))
                {
                    break;
                }

                if (!TryDrainInputs(out var batch) || batch.Count == 0)
                {
                    continue;
                }

                var assignment = await StartRunAsync(batch, ct: ct);
                LastRunId = assignment.RunId;
                _runStarted.TrySetResult();

                await PublishToAllAsync(
                    new RunAssignmentMessage { Assignment = assignment, ThreadId = ThreadId },
                    ct);

                if (_pauseBeforeComplete)
                {
                    // Hold before the terminal write so a test can observe the InProgress row.
                    await _releaseGate.Task.WaitAsync(ct);
                }

                try
                {
                    foreach (var msg in _messagesToReturn)
                    {
                        await PublishToAllAsync(msg, ct);
                    }
                }
                finally
                {
                    await CompleteRunAsync(
                        assignment.RunId,
                        assignment.GenerationId,
                        false,
                        null,
                        0,
                        isError: _completeWithError,
                        errorMessage: _completeWithError ? "boom" : null,
                        ct: ct);
                }
            }
        }
    }

    /// <summary>
    /// A store implementing only <see cref="IConversationStore"/> (NOT <see cref="IRunLedgerStore"/>),
    /// used to assert the constructor rejects <c>persistRunLedger:true</c> against it. Members are
    /// benign no-ops; the constructor throws before any is invoked.
    /// </summary>
    private sealed class ConversationOnlyStore : IConversationStore
    {
        public Task AppendMessagesAsync(string threadId, IReadOnlyList<PersistedMessage> messages, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task ReplaceMessageAsync(string threadId, PersistedMessage replacement, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<PersistedMessage>> LoadMessagesAsync(string threadId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<PersistedMessage>>([]);

        public Task SaveMetadataAsync(string threadId, ThreadMetadata metadata, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<ThreadMetadata?> LoadMetadataAsync(string threadId, CancellationToken ct = default)
            => Task.FromResult<ThreadMetadata?>(null);

        public Task UpdateMetadataAsync(string threadId, Func<ThreadMetadata?, ThreadMetadata> update, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task DeleteThreadAsync(string threadId, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<ThreadMetadata>> ListThreadsAsync(int limit = 50, int offset = 0, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ThreadMetadata>>([]);
    }

    #endregion
}
