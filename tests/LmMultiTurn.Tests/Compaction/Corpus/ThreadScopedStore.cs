using System.Collections.Concurrent;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;

namespace LmMultiTurn.Tests.Compaction.Corpus;

/// <summary>One store call as seen by a <see cref="ThreadScopedStore"/> handle.</summary>
public sealed record StoreCall(string Handle, string Method, string? ThreadId);

/// <summary>Every store call made through every handle of one corpus run.</summary>
internal sealed class StoreCallLog
{
    private readonly ConcurrentQueue<StoreCall> _calls = new();

    public void Record(string handle, string method, string? threadId) =>
        _calls.Enqueue(new StoreCall(handle, method, threadId));

    public IReadOnlyList<StoreCall> Calls => [.. _calls];

    /// <summary>The message-row operations that read or wrote a thread other than the handle's own (D5).</summary>
    public IReadOnlyList<StoreCall> CrossThreadRowAccess =>
        [
            .. _calls.Where(c =>
                c.ThreadId is not null
                && !string.Equals(c.ThreadId, c.Handle, StringComparison.Ordinal)
                && c.Method
                    is nameof(IConversationStore.LoadMessagesAsync)
                        or nameof(IConversationStore.LoadMessageRangeAsync)
                        or nameof(IConversationStore.AppendMessagesAsync)
                        or nameof(IConversationStore.ReplaceMessageAsync)
            ),
        ];
}

/// <summary>
/// A per-loop handle over one shared store that records every call with the thread it targeted (D5) and,
/// on request, hides sequence numbers on load to stand in for a store that has not backfilled legacy rows
/// (spec §8.3). Forwards every persistence interface the loop probes for (<c>store as IRunLedgerStore</c>
/// and friends), so decorating changes nothing about what the loop persists.
/// </summary>
internal sealed class ThreadScopedStore(
    IConversationStore inner,
    string ownerThreadId,
    StoreCallLog log,
    bool stripSeqOnLoad = false
) : IConversationStore, IConversationOwnershipStore, IRunLedgerStore, IRunLifecycleStore, IInputAcceptanceStore
{
    private IConversationOwnershipStore Ownership =>
        inner as IConversationOwnershipStore ?? throw new NotSupportedException("inner store has no ownership");

    private IRunLedgerStore Ledger =>
        inner as IRunLedgerStore ?? throw new NotSupportedException("inner store has no ledger");

    private IRunLifecycleStore Lifecycle =>
        inner as IRunLifecycleStore ?? throw new NotSupportedException("inner store has no lifecycle");

    private IInputAcceptanceStore Acceptance =>
        inner as IInputAcceptanceStore ?? throw new NotSupportedException("inner store has no acceptance");

    public string OwnerThreadId { get; } = ownerThreadId;

    private void Log(string method, string? threadId) => log.Record(OwnerThreadId, method, threadId);

    // === IConversationStore ===

    public Task AppendMessagesAsync(
        string threadId,
        IReadOnlyList<PersistedMessage> messages,
        CancellationToken ct = default
    )
    {
        Log(nameof(AppendMessagesAsync), threadId);
        return inner.AppendMessagesAsync(threadId, messages, ct);
    }

    public async Task<IReadOnlyList<PersistedMessage>> LoadMessagesAsync(
        string threadId,
        CancellationToken ct = default
    )
    {
        Log(nameof(LoadMessagesAsync), threadId);
        var rows = await inner.LoadMessagesAsync(threadId, ct);
        return stripSeqOnLoad ? [.. rows.Select(r => r with { Seq = null })] : rows;
    }

    public Task<long> GetMessageWatermarkAsync(string threadId, CancellationToken ct = default)
    {
        Log(nameof(GetMessageWatermarkAsync), threadId);
        return inner.GetMessageWatermarkAsync(threadId, ct);
    }

    public Task<IReadOnlyList<PersistedMessage>> LoadMessageRangeAsync(
        string threadId,
        long fromSeq,
        long toSeq,
        int limit,
        CancellationToken ct = default
    )
    {
        Log(nameof(LoadMessageRangeAsync), threadId);
        return inner.LoadMessageRangeAsync(threadId, fromSeq, toSeq, limit, ct);
    }

    public Task ReplaceMessageAsync(string threadId, PersistedMessage replacement, CancellationToken ct = default)
    {
        Log(nameof(ReplaceMessageAsync), threadId);
        return inner.ReplaceMessageAsync(threadId, replacement, ct);
    }

    public Task SaveMetadataAsync(string threadId, ThreadMetadata metadata, CancellationToken ct = default)
    {
        Log(nameof(SaveMetadataAsync), threadId);
        return inner.SaveMetadataAsync(threadId, metadata, ct);
    }

    public Task<ThreadMetadata?> LoadMetadataAsync(string threadId, CancellationToken ct = default)
    {
        Log(nameof(LoadMetadataAsync), threadId);
        return inner.LoadMetadataAsync(threadId, ct);
    }

    public Task UpdateMetadataAsync(
        string threadId,
        Func<ThreadMetadata?, ThreadMetadata> update,
        CancellationToken ct = default
    )
    {
        Log(nameof(UpdateMetadataAsync), threadId);
        return inner.UpdateMetadataAsync(threadId, update, ct);
    }

    public Task DeleteThreadAsync(string threadId, CancellationToken ct = default)
    {
        Log(nameof(DeleteThreadAsync), threadId);
        return inner.DeleteThreadAsync(threadId, ct);
    }

    public Task<IReadOnlyList<ThreadMetadata>> ListThreadsAsync(
        int limit = 50,
        int offset = 0,
        ConversationListOptions? options = null,
        CancellationToken ct = default
    )
    {
        Log(nameof(ListThreadsAsync), null);
        return inner.ListThreadsAsync(limit, offset, options, ct);
    }

    public Task<IReadOnlyList<ThreadMetadata>> ListThreadsAsync(
        ConversationListScope scope,
        int limit = 50,
        int offset = 0,
        ConversationListOptions? options = null,
        CancellationToken ct = default
    )
    {
        Log(nameof(ListThreadsAsync), null);
        return inner.ListThreadsAsync(scope, limit, offset, options, ct);
    }

    // === IConversationOwnershipStore ===

    public Task<int> StampUnownedThreadsAsync(string quarantineTenantId, CancellationToken ct = default)
    {
        Log(nameof(StampUnownedThreadsAsync), null);
        return Ownership.StampUnownedThreadsAsync(quarantineTenantId, ct);
    }

    public Task<IReadOnlyList<string>> ListThreadIdsByTenantAsync(
        string tenantId,
        IReadOnlyCollection<string>? threadIds,
        CancellationToken ct = default
    )
    {
        Log(nameof(ListThreadIdsByTenantAsync), null);
        return Ownership.ListThreadIdsByTenantAsync(tenantId, threadIds, ct);
    }

    public Task<int> AdoptThreadsAsync(
        string fromTenantId,
        string toTenantId,
        string? ownerUserId,
        IReadOnlyCollection<string>? threadIds,
        CancellationToken ct = default
    )
    {
        Log(nameof(AdoptThreadsAsync), null);
        return Ownership.AdoptThreadsAsync(fromTenantId, toTenantId, ownerUserId, threadIds, ct);
    }

    // === IRunLedgerStore ===

    public Task UpsertRunLedgerAsync(RunLedgerEntry entry, CancellationToken ct = default)
    {
        Log(nameof(UpsertRunLedgerAsync), entry.ThreadId);
        return Ledger.UpsertRunLedgerAsync(entry, ct);
    }

    public Task<RunLedgerEntry?> LoadRunLedgerAsync(string runId, CancellationToken ct = default)
    {
        Log(nameof(LoadRunLedgerAsync), null);
        return Ledger.LoadRunLedgerAsync(runId, ct);
    }

    public Task<IReadOnlyList<RunLedgerEntry>> ListRunLedgerAsync(string threadId, CancellationToken ct = default)
    {
        Log(nameof(ListRunLedgerAsync), threadId);
        return Ledger.ListRunLedgerAsync(threadId, ct);
    }

    public Task RecordAcceptedInputAsync(
        string threadId,
        string inputId,
        DateTimeOffset acceptedAt,
        CancellationToken ct = default
    )
    {
        Log(nameof(RecordAcceptedInputAsync), threadId);
        return Ledger.RecordAcceptedInputAsync(threadId, inputId, acceptedAt, ct);
    }

    public Task RemoveAcceptedInputAsync(string threadId, string inputId, CancellationToken ct = default)
    {
        Log(nameof(RemoveAcceptedInputAsync), threadId);
        return Ledger.RemoveAcceptedInputAsync(threadId, inputId, ct);
    }

    public Task<IReadOnlySet<string>> ListAcceptedInputIdsAsync(string threadId, CancellationToken ct = default)
    {
        Log(nameof(ListAcceptedInputIdsAsync), threadId);
        return Ledger.ListAcceptedInputIdsAsync(threadId, ct);
    }

    // === IRunLifecycleStore ===

    public Task RecordRunStartedAsync(RunLifecycleState state, CancellationToken ct = default)
    {
        Log(nameof(RecordRunStartedAsync), state.ThreadId);
        return Lifecycle.RecordRunStartedAsync(state, ct);
    }

    public Task<RunLifecycleState?> LoadRunLifecycleAsync(string runId, CancellationToken ct = default)
    {
        Log(nameof(LoadRunLifecycleAsync), null);
        return Lifecycle.LoadRunLifecycleAsync(runId, ct);
    }

    public Task<IReadOnlyList<RunLifecycleState>> ListRunLifecycleAsync(string threadId, CancellationToken ct = default)
    {
        Log(nameof(ListRunLifecycleAsync), threadId);
        return Lifecycle.ListRunLifecycleAsync(threadId, ct);
    }

    public Task<IReadOnlyList<RunLifecycleState>> ListNonTerminalRunsAsync(
        string threadId,
        CancellationToken ct = default
    )
    {
        Log(nameof(ListNonTerminalRunsAsync), threadId);
        return Lifecycle.ListNonTerminalRunsAsync(threadId, ct);
    }

    public Task<bool> TryMarkRunTerminalAsync(
        string runId,
        string outcome,
        int turnCount,
        DateTimeOffset terminalAt,
        CancellationToken ct = default
    )
    {
        Log(nameof(TryMarkRunTerminalAsync), null);
        return Lifecycle.TryMarkRunTerminalAsync(runId, outcome, turnCount, terminalAt, ct);
    }

    public Task<DeferredToolCallRecord> RecordDeferredToolCallAsync(
        string runId,
        DeferredToolCallRecord record,
        CancellationToken ct = default
    )
    {
        Log(nameof(RecordDeferredToolCallAsync), null);
        return Lifecycle.RecordDeferredToolCallAsync(runId, record, ct);
    }

    public Task<DeferredResolutionOutcome> TryResolveDeferredToolCallAsync(
        string threadId,
        string toolCallId,
        string resolutionFingerprint,
        string? childRunId,
        DateTimeOffset resolvedAt,
        CancellationToken ct = default
    )
    {
        Log(nameof(TryResolveDeferredToolCallAsync), threadId);
        return Lifecycle.TryResolveDeferredToolCallAsync(
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
    )
    {
        Log(nameof(AttachDeferredChildRunAsync), threadId);
        return Lifecycle.AttachDeferredChildRunAsync(threadId, toolCallId, childRunId, attachedAt, ct);
    }

    // === IInputAcceptanceStore ===

    public Task<InputAcceptance?> TryReserveAcceptanceAsync(InputAcceptance acceptance, CancellationToken ct = default)
    {
        Log(nameof(TryReserveAcceptanceAsync), acceptance.ThreadId);
        return Acceptance.TryReserveAcceptanceAsync(acceptance, ct);
    }

    public Task<InputAcceptance?> GetAcceptanceAsync(string threadId, string inputId, CancellationToken ct = default)
    {
        Log(nameof(GetAcceptanceAsync), threadId);
        return Acceptance.GetAcceptanceAsync(threadId, inputId, ct);
    }

    public Task<bool> TryRecordOutcomeAsync(InputAcceptance acceptance, CancellationToken ct = default)
    {
        Log(nameof(TryRecordOutcomeAsync), acceptance.ThreadId);
        return Acceptance.TryRecordOutcomeAsync(acceptance, ct);
    }

    public Task<bool> TryReleaseAcceptanceAsync(
        string threadId,
        string inputId,
        Guid reservationId,
        CancellationToken ct = default
    )
    {
        Log(nameof(TryReleaseAcceptanceAsync), threadId);
        return Acceptance.TryReleaseAcceptanceAsync(threadId, inputId, reservationId, ct);
    }
}
