using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using FluentAssertions;
using Xunit;

namespace LmMultiTurn.Tests;

/// <summary>
/// Durability tests for <see cref="MultiTurnAgentBase.TrySendAsync"/> — the non-blocking send
/// variant that must (a) durably record an accepted input BEFORE touching the channel, (b) return
/// null and roll back that record when the channel is full, and (c) propagate a store failure
/// WITHOUT ever writing to the channel.
/// </summary>
public class TrySendAsyncDurabilityTests
{
    private static List<IMessage> UserMessages(string text) =>
        [new TextMessage { Text = text, Role = Role.User }];

    [Fact]
    public async Task TrySendAsync_WhenStoreRecordFails_PropagatesException_AndNeverWritesToChannel()
    {
        // Arrange: a store whose RecordAcceptedInputAsync throws for the accepted-input write.
        var store = new FaultInjectingLedgerStore
        {
            OnRecordAcceptedInput = _ => throw new InvalidOperationException("simulated durable-store failure"),
        };
        await using var agent = new LedgerTestAgent("thread-store-fail", store, persistRunLedger: true);

        // Act: the durable record throws before any channel write is attempted.
        var act = () => agent.TrySendAsync(UserMessages("hello"), inputId: "input-1").AsTask();

        // Assert: the exception propagates (maps to HTTP 500) and no SendReceipt is produced.
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*simulated durable-store failure*");

        // Assert (behavioral): the channel was never written to. Start the loop and give it a
        // moment — if anything had been enqueued the loop would drain it and bump the counter.
        using var cts = new CancellationTokenSource();
        _ = agent.RunAsync(cts.Token);
        await Task.Delay(150);

        agent.DrainedInputCount.Should().Be(0,
            "a store failure must block the enqueue entirely — nothing may reach the input channel");

        await cts.CancelAsync();
    }

    [Fact]
    public async Task TrySendAsync_WhenChannelFull_ReturnsNull_AndRollsBackOnlyTheRejectedAcceptedInput()
    {
        // Arrange: a real (non-throwing) ledger store and a tiny channel so it fills after 2 sends.
        // The drain loop is intentionally NOT started, so the channel stays full.
        var store = new FaultInjectingLedgerStore();
        var threadId = "thread-queue-full";
        await using var agent = new LedgerTestAgent(
            threadId,
            store,
            persistRunLedger: true,
            inputChannelCapacity: 2);

        // Fill the channel to capacity — both of these succeed.
        var receipt1 = await agent.TrySendAsync(UserMessages("first"), inputId: "queued-1");
        var receipt2 = await agent.TrySendAsync(UserMessages("second"), inputId: "queued-2");

        receipt1.Should().NotBeNull();
        receipt2.Should().NotBeNull();

        // Act: the channel is now full, so this one is rejected.
        var rejected = await agent.TrySendAsync(UserMessages("third"), inputId: "rejected-3");

        // Assert: rejected send returns null (maps to HTTP 503).
        rejected.Should().BeNull();

        // Assert: the rejected input's accepted-input record was compensated (rolled back), while
        // the two successfully-queued inputs' records remain — proving only the rejected one was
        // removed, not the whole batch.
        var accepted = await store.ListAcceptedInputIdsAsync(threadId);
        accepted.Should().Contain("queued-1")
            .And.Contain("queued-2");
        accepted.Should().NotContain("rejected-3",
            "the compensating RemoveAcceptedInputAsync must undo the accepted-input record for the rejected send");
    }

    [Fact]
    public async Task TrySendAsync_HappyPath_DurablyRecordsAcceptedInput_BeforeChannelWrite()
    {
        // Arrange: a call-order-recording ledger store and room in the channel.
        var store = new FaultInjectingLedgerStore();
        var threadId = "thread-happy";
        await using var agent = new LedgerTestAgent(threadId, store, persistRunLedger: true);

        var beforeSend = DateTimeOffset.UtcNow;

        // Act
        var receipt = await agent.TrySendAsync(UserMessages("hello"), inputId: "happy-1");
        var afterSend = DateTimeOffset.UtcNow;

        // Assert: a populated SendReceipt is returned.
        receipt.Should().NotBeNull();
        receipt!.ReceiptId.Should().NotBeNullOrEmpty();
        receipt.InputId.Should().Be("happy-1");
        receipt.QueuedAt.Should().BeOnOrAfter(beforeSend).And.BeOnOrBefore(afterSend);

        // Assert: the accepted-input record exists immediately — the durable write happened
        // synchronously as part of the call, independent of the loop ever draining the channel.
        var accepted = await store.ListAcceptedInputIdsAsync(threadId);
        accepted.Should().Contain(receipt.ReceiptId);

        // Assert (ordering invariant): the code always records the accepted input BEFORE attempting
        // the channel write, and never rolls it back on the happy path.
        store.CallLog.Should().Contain("RecordAccepted:happy-1");
        store.CallLog.Should().NotContain("RemoveAccepted:happy-1");
    }

    [Fact]
    public async Task TrySendAsync_AfterRunStarts_RemovesTheAcceptedInputRecord_NowFoldedIntoTheLedger()
    {
        // Arrange: accept an input but don't start the loop yet, so it's durably accepted and
        // nothing else.
        var store = new FaultInjectingLedgerStore();
        var threadId = "thread-fold-cleanup";
        await using var agent = new LedgerTestAgent(threadId, store, persistRunLedger: true);

        var receipt = await agent.TrySendAsync(UserMessages("hello"), inputId: "fold-1");
        receipt.Should().NotBeNull();

        var acceptedBeforeRun = await store.ListAcceptedInputIdsAsync(threadId);
        acceptedBeforeRun.Should().Contain("fold-1", "accepted before any run has drained it");

        // Act: start the loop so StartRunAsync folds "fold-1" into the new run's InputIds.
        using var cts = new CancellationTokenSource();
        _ = agent.RunAsync(cts.Token);
        await Task.Delay(150);

        // Assert: the pre-run acceptance record is gone now that the run ledger itself covers the
        // input id — it must not be retained forever once folded (see StartRunAsync).
        var acceptedAfterRun = await store.ListAcceptedInputIdsAsync(threadId);
        acceptedAfterRun.Should().NotContain("fold-1",
            "once StartRunAsync folds the input id into the run's ledger InputIds, the pre-run acceptance record must be cleaned up rather than retained forever");

        await cts.CancelAsync();
    }

    [Fact]
    public async Task CompleteRunAsync_WhenTerminalLedgerUpsertFails_NeverPublishesRunCompleted()
    {
        // Arrange: only the terminal (Completed/Errored) ledger write fails — the earlier
        // Queued/InProgress writes in StartRunAsync succeed normally.
        var store = new FaultInjectingLedgerStore
        {
            OnUpsertRunLedger = entry =>
            {
                if (entry.Status is RunStatus.Completed or RunStatus.Errored)
                {
                    throw new InvalidOperationException("simulated terminal-ledger-write failure");
                }

                return Task.CompletedTask;
            },
        };
        var threadId = "thread-terminal-fail";
        var agent = new LedgerTestAgent(threadId, store, persistRunLedger: true);

        var received = new List<IMessage>();
        using var subscribeCts = new CancellationTokenSource();
        var subscribeTask = Task.Run(async () =>
        {
            await foreach (var message in agent.SubscribeAsync(subscribeCts.Token))
            {
                received.Add(message);
            }
        });

        try
        {
            var receipt = await agent.TrySendAsync(UserMessages("hello"), inputId: "terminal-1");
            receipt.Should().NotBeNull();

            // Act: the loop drains the input, starts the run, then CompleteRunAsync's terminal
            // ledger write throws before it ever reaches PublishToAllAsync.
            var runTask = agent.RunAsync(CancellationToken.None);
            await Task.Delay(200);

            // Assert: the failed run loop must not have published a RunCompletedMessage — the REST
            // status API's ledger-as-source-of-truth invariant would otherwise be violated (a
            // subscriber sees "completed" while GET /status still reports InProgress forever).
            received.OfType<RunCompletedMessage>().Should().BeEmpty(
                "the terminal ledger write failed, so CompleteRunAsync must never reach the publish call");

            // Assert: the failure actually propagated out of the run loop rather than being
            // swallowed.
            var awaitRun = async () => await runTask;
            await awaitRun.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*simulated terminal-ledger-write failure*");
        }
        finally
        {
            await subscribeCts.CancelAsync();

            // The run loop is already faulted by design here — DisposeAsync's own StopAsync would
            // re-observe (and rethrow) that same fault, which isn't what this test is checking.
            try
            {
                await agent.DisposeAsync();
            }
            catch (InvalidOperationException)
            {
            }
        }
    }

    /// <summary>
    /// Test-double agent whose run loop counts how many inputs it drains, so a test can assert
    /// (behaviorally) that nothing reached the input channel. Threads <c>persistRunLedger</c> and
    /// <c>inputChannelCapacity</c> to the base so tests can enable ledger durability and force a
    /// full-queue condition with a tiny capacity.
    /// </summary>
    private sealed class LedgerTestAgent : MultiTurnAgentBase
    {
        public int DrainedInputCount { get; private set; }

        public LedgerTestAgent(
            string threadId,
            IConversationStore store,
            bool persistRunLedger,
            int inputChannelCapacity = 100)
            : base(
                threadId,
                store: store,
                inputChannelCapacity: inputChannelCapacity,
                persistRunLedger: persistRunLedger)
        {
        }

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

                DrainedInputCount += batch.Count;

                var assignment = await StartRunAsync(batch, ct: ct);
                await CompleteRunAsync(assignment.RunId, assignment.GenerationId, false, null, 0, ct: ct);
            }
        }
    }

    /// <summary>
    /// Composition wrapper over <see cref="InMemoryConversationStore"/> implementing both store
    /// interfaces. Adds a fault hook on <see cref="RecordAcceptedInputAsync"/> and a call-order log
    /// so durability/rollback ordering can be asserted. Every method forwards to the inner store.
    /// </summary>
    private sealed class FaultInjectingLedgerStore : IConversationStore, IRunLedgerStore
    {
        private readonly InMemoryConversationStore _inner = new();

        /// <summary>Runs first inside <see cref="RecordAcceptedInputAsync"/>; may throw to simulate a store failure.</summary>
        public Func<string, Task>? OnRecordAcceptedInput { get; set; }

        /// <summary>Runs first inside <see cref="UpsertRunLedgerAsync"/>; may throw to simulate a store failure for a specific entry (e.g. only the terminal Completed/Errored write, leaving the earlier Queued/InProgress writes untouched).</summary>
        public Func<RunLedgerEntry, Task>? OnUpsertRunLedger { get; set; }

        /// <summary>Records the order of accepted-input record/remove calls (e.g. "RecordAccepted:id", "RemoveAccepted:id").</summary>
        public List<string> CallLog { get; } = [];

        // === IRunLedgerStore: accepted-input tracking (the surface under test) ===

        public async Task RecordAcceptedInputAsync(
            string threadId,
            string inputId,
            DateTimeOffset acceptedAt,
            CancellationToken ct = default)
        {
            CallLog.Add("RecordAccepted:" + inputId);

            if (OnRecordAcceptedInput != null)
            {
                await OnRecordAcceptedInput(inputId);
            }

            await _inner.RecordAcceptedInputAsync(threadId, inputId, acceptedAt, ct);
        }

        public Task RemoveAcceptedInputAsync(string threadId, string inputId, CancellationToken ct = default)
        {
            CallLog.Add("RemoveAccepted:" + inputId);
            return _inner.RemoveAcceptedInputAsync(threadId, inputId, ct);
        }

        public Task<IReadOnlySet<string>> ListAcceptedInputIdsAsync(string threadId, CancellationToken ct = default) =>
            _inner.ListAcceptedInputIdsAsync(threadId, ct);

        // === IRunLedgerStore: run ledger (plain forwarding) ===

        public async Task UpsertRunLedgerAsync(RunLedgerEntry entry, CancellationToken ct = default)
        {
            if (OnUpsertRunLedger != null)
            {
                await OnUpsertRunLedger(entry);
            }

            await _inner.UpsertRunLedgerAsync(entry, ct);
        }

        public Task<RunLedgerEntry?> LoadRunLedgerAsync(string runId, CancellationToken ct = default) =>
            _inner.LoadRunLedgerAsync(runId, ct);

        public Task<IReadOnlyList<RunLedgerEntry>> ListRunLedgerAsync(string threadId, CancellationToken ct = default) =>
            _inner.ListRunLedgerAsync(threadId, ct);

        // === IConversationStore (plain forwarding) ===

        public Task AppendMessagesAsync(
            string threadId,
            IReadOnlyList<PersistedMessage> messages,
            CancellationToken ct = default) =>
            _inner.AppendMessagesAsync(threadId, messages, ct);

        public Task ReplaceMessageAsync(string threadId, PersistedMessage replacement, CancellationToken ct = default) =>
            _inner.ReplaceMessageAsync(threadId, replacement, ct);

        public Task<IReadOnlyList<PersistedMessage>> LoadMessagesAsync(string threadId, CancellationToken ct = default) =>
            _inner.LoadMessagesAsync(threadId, ct);

        public Task SaveMetadataAsync(string threadId, ThreadMetadata metadata, CancellationToken ct = default) =>
            _inner.SaveMetadataAsync(threadId, metadata, ct);

        public Task<ThreadMetadata?> LoadMetadataAsync(string threadId, CancellationToken ct = default) =>
            _inner.LoadMetadataAsync(threadId, ct);

        public Task UpdateMetadataAsync(
            string threadId,
            Func<ThreadMetadata?, ThreadMetadata> update,
            CancellationToken ct = default) =>
            _inner.UpdateMetadataAsync(threadId, update, ct);

        public Task DeleteThreadAsync(string threadId, CancellationToken ct = default) =>
            _inner.DeleteThreadAsync(threadId, ct);

        public Task<IReadOnlyList<ThreadMetadata>> ListThreadsAsync(
            int limit = 50,
            int offset = 0,
            CancellationToken ct = default) =>
            _inner.ListThreadsAsync(limit, offset, ct);
    }
}
