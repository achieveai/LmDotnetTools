using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.Collaboration;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using FluentAssertions;
using Xunit;

namespace LmMultiTurn.Tests;

/// <summary>
/// Covers issue #434: accepts that reach a POOLED agent from inside this assembly bypassed the
/// host's accepted-input ledger, so a handoff landing between such an accept and the run that would
/// start it read the agent as idle and disposed it with the turn on it — the #418 shape, reopened by
/// three paths that could not close it themselves.
/// </summary>
/// <remarks>
/// <para>
/// The fix is structural rather than per-call-site. Every accept, on every path, mints its receipt
/// id in exactly two places — <c>MultiTurnAgentBase.SendAsync</c> and
/// <c>MultiTurnAgentBase.TrySendAsync</c> — so reporting
/// from THERE covers each of them by construction. That is what these tests pin: not that three
/// particular callers remembered to do something, but that the two places an acceptance can come into
/// existence both announce it.
/// </para>
/// <para>
/// The per-site tests below are therefore regression guards, not the mechanism's proof. What they
/// catch is a site being rerouted around the mint sites — this loop has an internal enqueue path that
/// bypasses <c>SendAsync</c> entirely, and a site moved onto it would lose ledger coverage silently.
/// </para>
/// </remarks>
public class InputAcceptanceObserverTests
{
    private static List<IMessage> UserMessages(string text) =>
        [new TextMessage { Text = text, Role = Role.User }];

    [Fact]
    public async Task SendAsync_ReportsTheAccept_CarryingTheIdTheSenderWasGiven()
    {
        var observer = new RecordingObserver();
        await using var agent = new ObservedTestAgent("thread-send") { InputAcceptanceObserver = observer };

        var receipt = await agent.SendAsync(UserMessages("hello"), inputId: "input-1");

        observer.Accepted.Should().ContainSingle().Which
            .Should().Be(("thread-send", "input-1"));
        receipt.ReceiptId.Should().Be("input-1",
            "the reported id must be the one the caller can correlate on — a receipt the sender holds "
                + "and a ledger entry under a different id would retire nothing");
        observer.Rescinded.Should().BeEmpty();
        observer.AcceptedBy.Should().ContainSingle().Which.Should().BeSameAs(agent,
            "an observer that tracks agents by conversation compares this reference, so reporting the "
                + "wrong instance would mark an agent that never took the input");
    }

    [Fact]
    public async Task SendAsync_WithNoInputId_ReportsTheMintedReceiptId()
    {
        // The three sites this issue is about all send WITHOUT an input id and discard the receipt.
        // The id they never named still exists — it is minted at the send — and it is that minted id
        // the ledger has to hold, or those sites stay exactly as uncovered as they were.
        var observer = new RecordingObserver();
        await using var agent = new ObservedTestAgent("thread-minted") { InputAcceptanceObserver = observer };

        var receipt = await agent.SendAsync(UserMessages("relayed"));

        observer.Accepted.Should().ContainSingle();
        observer.Accepted[0].InputId.Should().Be(receipt.ReceiptId).And.NotBeNullOrEmpty();
    }

    [Fact]
    public async Task TrySendAsync_ReportsTheAccept_CarryingTheIdTheSenderWasGiven()
    {
        var observer = new RecordingObserver();
        await using var agent = new ObservedTestAgent("thread-try") { InputAcceptanceObserver = observer };

        var receipt = await agent.TrySendAsync(UserMessages("hello"), inputId: "input-1");

        receipt.Should().NotBeNull();
        observer.Accepted.Should().ContainSingle().Which.Should().Be(("thread-try", "input-1"));
        observer.Rescinded.Should().BeEmpty();
    }

    [Fact]
    public async Task TrySendAsync_WhenTheChannelIsFull_RescindsTheAcceptItReported()
    {
        // The rollback partner, and the reason reporting BEFORE the enqueue costs nothing. A refused
        // send that left its report standing would leave an id nothing can ever retire — no run will
        // name an input the agent never received — so the conversation reads busy until the host's
        // grace expires: real seconds of refused handoffs bought for a turn that was never queued.
        var observer = new RecordingObserver();
        await using var agent = new ObservedTestAgent("thread-full", inputChannelCapacity: 2)
        {
            InputAcceptanceObserver = observer,
        };

        // Fill the channel. Nothing drains it — the loop is never started.
        (await agent.TrySendAsync(UserMessages("first"), inputId: "queued-1")).Should().NotBeNull();
        (await agent.TrySendAsync(UserMessages("second"), inputId: "queued-2")).Should().NotBeNull();

        var rejected = await agent.TrySendAsync(UserMessages("third"), inputId: "rejected-3");

        rejected.Should().BeNull();
        observer.Rescinded.Should().ContainSingle().Which
            .Should().Be(("thread-full", "rejected-3"));

        // Only the rejected one is withdrawn: the two that really are queued stay reported, or the
        // rollback would clear work the agent genuinely holds.
        observer.Accepted.Select(a => a.InputId)
            .Should().BeEquivalentTo(["queued-1", "queued-2", "rejected-3"]);
        observer.Rescinded.Select(r => r.InputId).Should().NotContain(["queued-1", "queued-2"]);
    }

    [Fact]
    public async Task SendAsync_WhenTheBackpressuredWriteFails_RescindsTheAcceptItReported()
    {
        // SendAsync's OTHER exit, and the one TrySendAsync does not have. A full channel does not
        // refuse here — it parks on WriteAsync — and that await can still fail: a cancelled token, or
        // a channel completed by disposal underneath the waiter. The accept was announced before the
        // TryWrite, so a failure there leaves a reported id with nothing queued behind it, and no run
        // will ever name an input the agent did not receive. Same shape as
        // TrySendAsync_WhenTheChannelIsFull_RescindsTheAcceptItReported; the only difference is that
        // this exit throws rather than returning null.
        //
        // There is no durable accepted-input write on this path (SendAsync does not touch
        // RunLedgerStore), so the withdrawal IS the whole rollback rather than half of it.
        var observer = new RecordingObserver();
        await using var agent = new ObservedTestAgent("thread-backpressure", inputChannelCapacity: 1)
        {
            InputAcceptanceObserver = observer,
        };

        // Fill the channel so the next send takes the backpressure branch. Nothing drains it — the
        // loop is never started.
        (await agent.TrySendAsync(UserMessages("filler"), inputId: "filler-1")).Should().NotBeNull();

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = () => agent.SendAsync(UserMessages("blocked"), inputId: "blocked-1", ct: cts.Token).AsTask();

        await act.Should().ThrowAsync<OperationCanceledException>();

        // Non-vacuity: the accept really was reported, so the withdrawal below is a withdrawal of
        // something rather than a match against an id that never existed.
        observer.Accepted.Select(a => a.InputId).Should().Contain("blocked-1");
        observer.Rescinded.Should().ContainSingle().Which
            .Should().Be(("thread-backpressure", "blocked-1"));

        agent.QueuedInputCount.Should().Be(1,
            "only the filler is queued — the send that failed left nothing behind, which is why its "
                + "report must not be left standing either");
    }

    [Fact]
    public async Task TrySendAsync_WhenTheDurableWriteFails_ReportsNothingAtAll()
    {
        // Ordering. The durable accepted-input write runs first and a failure there means the input
        // was never accepted by anyone, so there is nothing to report and nothing to withdraw.
        // Reporting ahead of that write would leave the host holding an entry busy for a send that
        // failed outright.
        var store = new ThrowingLedgerStore();
        var observer = new RecordingObserver();
        await using var agent = new ObservedTestAgent("thread-store-fail", store: store, persistRunLedger: true)
        {
            InputAcceptanceObserver = observer,
        };

        var act = () => agent.TrySendAsync(UserMessages("hello"), inputId: "input-1").AsTask();

        await act.Should().ThrowAsync<InvalidOperationException>();
        observer.Accepted.Should().BeEmpty();
        observer.Rescinded.Should().BeEmpty();
    }

    [Fact]
    public async Task AThrowingObserver_FailsTheSend_AndLeavesNothingQueued()
    {
        // Fail closed, deliberately. The report happens before the enqueue, so a throw means the
        // input is not in the channel and no acceptance is recorded anywhere — the caller gets an
        // error for a turn that genuinely was not taken, which is recoverable by retrying.
        //
        // Swallowing would produce the opposite: the input sitting in the channel with the host
        // believing the agent idle. That is release-with-work-queued — the exact defect the whole
        // mechanism exists to prevent, reintroduced by the mechanism itself.
        await using var agent = new ObservedTestAgent("thread-throwing-observer")
        {
            InputAcceptanceObserver = new ThrowingObserver(),
        };

        var act = () => agent.SendAsync(UserMessages("hello"), inputId: "input-1").AsTask();

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*simulated observer failure*");

        agent.QueuedInputCount.Should().Be(0,
            "a failed report must block the enqueue entirely — an input in the channel that the host "
                + "does not know about is exactly the state this mechanism exists to make impossible");
    }

    [Fact]
    public async Task TheCollaborationWriteEndpoint_ReportsItsAccept()
    {
        // Issue site 3: a peer or sub-agent using the SendMessage collaboration tool against the ROOT
        // delivers through this endpoint, straight into the pooled root's input channel. It passes no
        // input id and reads only whether the receipt was null, so before the fix nothing about this
        // accept was visible to the host.
        var observer = new RecordingObserver();
        await using var agent = new ObservedTestAgent("thread-collab") { InputAcceptanceObserver = observer };
        var endpoint = new AgentLoopWriteEndpoint(agent);

        var outcome = await endpoint.DeliverAsync(new AgentMessage
        {
            MessageId = "msg-1",
            AgentMessageType = AgentMessageType.TaskUpdate,
            FromAgentId = "peer",
            FromName = "Peer",
            Body = "ping",
        });

        outcome.Disposition.Should().Be(AgentDeliveryDisposition.Delivered);
        observer.Accepted.Should().ContainSingle()
            .Which.ThreadId.Should().Be("thread-collab");
    }

    [Fact]
    public async Task TheCollaborationWriteEndpoint_RescindsWhenTheRootRefusesTheDelivery()
    {
        // The same site's refused path. The endpoint reports the refusal to its caller as
        // input_queue_full; the host must not be left holding a turn that was never queued.
        var observer = new RecordingObserver();
        await using var agent = new ObservedTestAgent("thread-collab-full", inputChannelCapacity: 1)
        {
            InputAcceptanceObserver = observer,
        };
        var endpoint = new AgentLoopWriteEndpoint(agent);

        (await agent.TrySendAsync(UserMessages("fills the channel"), inputId: "filler")).Should().NotBeNull();

        var outcome = await endpoint.DeliverAsync(new AgentMessage
        {
            MessageId = "msg-2",
            AgentMessageType = AgentMessageType.TaskUpdate,
            FromAgentId = "peer",
            FromName = "Peer",
            Body = "ping",
        });

        outcome.Disposition.Should().Be(AgentDeliveryDisposition.Refused);
        observer.Rescinded.Should().ContainSingle(
            "a refused collaboration delivery must withdraw the acceptance it reported");
    }

    #region Doubles

    // RecordingObserver, ThrowingObserver and ObservedTestAgent live in
    // TestDoubles/InputAcceptanceDoubles.cs: the per-site relay guards in
    // SubAgents/SubAgentParentRelayObserverTests.cs need the same three, and two copies of a
    // recording observer are two things that can drift apart.

    /// <summary>A run-ledger store whose accepted-input write always fails.</summary>
    private sealed class ThrowingLedgerStore : IConversationStore, IRunLedgerStore
    {
        private readonly InMemoryConversationStore _inner = new();

        public Task RecordAcceptedInputAsync(
            string threadId,
            string inputId,
            DateTimeOffset acceptedAt,
            CancellationToken ct = default) =>
            throw new InvalidOperationException("simulated durable accepted-input write failure");

        public Task RemoveAcceptedInputAsync(string threadId, string inputId, CancellationToken ct = default) =>
            _inner.RemoveAcceptedInputAsync(threadId, inputId, ct);

        public Task<IReadOnlySet<string>> ListAcceptedInputIdsAsync(string threadId, CancellationToken ct = default) =>
            _inner.ListAcceptedInputIdsAsync(threadId, ct);

        public Task UpsertRunLedgerAsync(RunLedgerEntry entry, CancellationToken ct = default) =>
            _inner.UpsertRunLedgerAsync(entry, ct);

        public Task<RunLedgerEntry?> LoadRunLedgerAsync(string runId, CancellationToken ct = default) =>
            _inner.LoadRunLedgerAsync(runId, ct);

        public Task<IReadOnlyList<RunLedgerEntry>> ListRunLedgerAsync(string threadId, CancellationToken ct = default) =>
            _inner.ListRunLedgerAsync(threadId, ct);

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

    #endregion
}
