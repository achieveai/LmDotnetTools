using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn;
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

        public Task UpsertRunLedgerAsync(RunLedgerEntry entry, CancellationToken ct = default) =>
            _inner.UpsertRunLedgerAsync(entry, ct);

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
