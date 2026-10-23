using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Utils;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using LmStreaming.Sample.Models;

namespace LmStreaming.Sample.Persistence;

/// <summary>
/// The sample's shared store with a durable, relation-only child index. Metadata remains authoritative;
/// every host instance rebuilds before admitting mutations or hierarchy reads, so a crashed publication
/// can never make an old sidecar certify completeness. All writers must use this instance.
/// </summary>
public sealed class IndexedConversationStore(IConversationStore inner, string indexPath)
    : IConversationStore,
        IConversationThreadIdLookupStore,
        IConversationOwnershipStore,
        IRunLedgerStore,
        IRunLifecycleStore,
        IInputAcceptanceStore,
        IConversationListingStore
{
    private readonly SemaphoreSlim _initialize = new(1, 1);
    private readonly SemaphoreSlim _publish = new(1, 1);
    private static readonly StringComparer StorageComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _mutationGates = new(StorageComparer);
    private readonly object _gate = new();
    private Dictionary<string, string> _parents = new(StringComparer.Ordinal);
    private Dictionary<string, HashSet<string>> _children = new(StringComparer.Ordinal);

    // How many ids in _parents share each storage key, so a write that changes nothing skips the scan.
    private Dictionary<string, int> _aliasCounts = new(StorageComparer);
    private bool _ready;
    private long _epoch;
    private int _pending;
    private bool _faulted;

    public sealed class HierarchyBusyException()
        : InvalidOperationException("Hierarchy metadata is changing; retry discovery.")
    {
        public string Reason { get; init; } = "unspecified";
        public int PendingCount { get; init; }
        public int Attempts { get; init; }
        public long SnapshotEpoch { get; init; }
        public long CurrentEpoch { get; init; }
    }

    private T Capability<T>() =>
        inner is T capability
            ? capability
            : throw new NotSupportedException($"The wrapped store does not implement {typeof(T).Name}.");

    /// <summary>One complete startup backfill; a disk snapshot is derived state, not readiness proof.</summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await _initialize.WaitAsync(ct);
        try
        {
            if (_ready)
                return;
            var metadata = await inner.ListThreadsAsync(int.MaxValue, 0, ct: ct);
            var parents = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var row in metadata)
            {
                if (SubAgentProvenance.TryProject(row) is { ParentThreadId: { } parent })
                    parents.Add(row.ThreadId, parent);
            }
            await PersistAsync(parents, ct);
            lock (_gate)
            {
                _parents = parents;
                RebuildDerived();
                _ready = true;
                _epoch++;
            }
        }
        finally
        {
            _initialize.Release();
        }
    }

    private Task PersistAsync(Dictionary<string, string> parents, CancellationToken ct) =>
        AtomicFile.WriteJsonAsync(indexPath, new { schemaVersion = 1, parents }, new JsonSerializerOptions(), ct);

    private void RebuildDerived()
    {
        _children = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        _aliasCounts = new Dictionary<string, int>(StorageComparer);
        foreach (var (child, parent) in _parents)
        {
            if (!_children.TryGetValue(parent, out var children))
                _children[parent] = children = new HashSet<string>(StringComparer.Ordinal);
            children.Add(child);
            var key = StorageKey(child);
            _aliasCounts[key] = _aliasCounts.GetValueOrDefault(key) + 1;
        }
    }

    private static string StorageKey(string threadId) =>
        Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(string.Join("_", threadId.Split(Path.GetInvalidFileNameChars())))
        );

    private async Task MutateAsync(string threadId, Func<Task> mutate, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(threadId);
        await InitializeAsync(ct);
        // Use the file store's sanitization identity so aliases cannot reorder reconciliation.
        var key = StorageKey(threadId);
        var mutationGate = _mutationGates.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));
        await mutationGate.WaitAsync(ct);
        lock (_gate)
        {
            _pending++;
            _epoch++;
        }
        try
        {
            ExceptionDispatchInfo? failure = null;
            try
            {
                await mutate();
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }

            // A canceled/failed atomic write may already have landed. Reread actual state after
            // the inner directory gate is released; never infer rollback from the exception.
            try
            {
                await ReconcileAsync(threadId);
            }
            catch (Exception) when (failure is not null)
            {
                // Preserve the original mutation error; unresolved index state still blocks reads.
                failure.Throw();
            }
            failure?.Throw();
        }
        finally
        {
            mutationGate.Release();
        }
    }

    private async Task ReconcileAsync(string threadId)
    {
        await _publish.WaitAsync();
        try
        {
            var metadata = await inner.LoadMetadataAsync(threadId, CancellationToken.None);
            var parent = metadata is null ? null : SubAgentProvenance.TryProject(metadata)?.ParentThreadId;
            var childId = metadata?.ThreadId ?? threadId;
            var key = StorageKey(threadId);
            Dictionary<string, string> updated;
            bool changed;
            lock (_gate)
            {
                // Most writes leave the relation as it is: no entry for a root, or exactly this child
                // under the same parent. Only a write that may change it pays for the copy and scan.
                var aliasCount = _aliasCounts.GetValueOrDefault(key);
                var unchanged = parent is null
                    ? aliasCount == 0
                    : aliasCount == 1
                        && _parents.TryGetValue(childId, out var current)
                        && current == parent
                        && StorageComparer.Equals(StorageKey(childId), key);
                if (unchanged)
                {
                    updated = _parents;
                    changed = false;
                }
                else
                {
                    updated = new Dictionary<string, string>(_parents, StringComparer.Ordinal);
                    var aliases = updated.Keys.Where(id => StorageComparer.Equals(StorageKey(id), key)).ToArray();
                    foreach (var alias in aliases)
                        updated.Remove(alias);
                    if (parent is not null)
                        updated[childId] = parent;
                    changed =
                        updated.Count != _parents.Count
                        || updated.Any(pair =>
                            !_parents.TryGetValue(pair.Key, out var previous) || previous != pair.Value
                        );
                }
            }
            if (changed)
                await PersistAsync(updated, CancellationToken.None);
            lock (_gate)
            {
                if (changed)
                {
                    _parents = updated;
                    RebuildDerived();
                }
                _epoch++;
            }
        }
        catch
        {
            lock (_gate)
                _faulted = true;
            throw;
        }
        finally
        {
            lock (_gate)
                _pending--;
            _publish.Release();
        }
        // Publication failure is latched separately from active writes. Only restart reconstructs
        // authoritative metadata; later successful mutations cannot clear the fault.
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ConversationListingRow>> ListConversationRowsAsync(
        IReadOnlySet<string> propertyKeys,
        ConversationListScope? scope = null,
        int limit = 50,
        int offset = 0,
        ConversationListOptions? options = null,
        CancellationToken ct = default
    ) => inner.ListConversationRowsAsync(propertyKeys, scope, limit, offset, options, ct);

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> ListThreadIdsByPrefixAsync(string prefix, CancellationToken ct = default) =>
        ConversationThreadIdLookup.ListThreadIdsByPrefixAsync(inner, prefix, ct);

    /// <summary>Reads current, tenant-eligible persisted nodes without a historical corpus scan.</summary>
    public async Task<IReadOnlyList<SubAgentSummary>> ReadChildrenAsync(
        string rootThreadId,
        bool recursive,
        CancellationToken ct = default
    )
    {
        await InitializeAsync(ct);
        long lastSnapshotEpoch = 0;
        long lastCurrentEpoch = 0;
        var lastPendingCount = 0;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            long epoch;
            var candidates = new Dictionary<string, string[]>(StringComparer.Ordinal);
            lock (_gate)
            {
                if (_faulted)
                    throw new InvalidOperationException(
                        "Hierarchy discovery has an unresolved metadata publication failure."
                    );
                if (_pending != 0)
                    throw new HierarchyBusyException
                    {
                        Reason = "pending_publication",
                        PendingCount = _pending,
                        Attempts = attempt + 1,
                        SnapshotEpoch = _epoch,
                        CurrentEpoch = _epoch,
                    };
                epoch = _epoch;
                var queue = new Queue<string>();
                var visited = new HashSet<string>(StringComparer.Ordinal) { rootThreadId };
                queue.Enqueue(rootThreadId);
                while (queue.TryDequeue(out var parent))
                {
                    var children = _children.TryGetValue(parent, out var ids) ? ids.ToArray() : [];
                    candidates[parent] = children;
                    if (!recursive)
                        break;
                    foreach (var child in children)
                        if (visited.Add(child))
                            queue.Enqueue(child);
                }
            }
            var root = await inner.LoadMetadataAsync(rootThreadId, ct);
            var scope = string.IsNullOrWhiteSpace(root?.TenantId)
                ? null
                : ConversationListScope.ForTenantIncludingUntenanted(root.TenantId);
            var found = new List<SubAgentSummary>();
            var discovered = new HashSet<string>(StringComparer.Ordinal) { rootThreadId };
            var traversal = new Queue<(string Parent, int Depth)>();
            traversal.Enqueue((rootThreadId, 0));
            while (traversal.TryDequeue(out var current))
            {
                ct.ThrowIfCancellationRequested();
                if (!candidates.TryGetValue(current.Parent, out var children))
                    continue;
                foreach (var child in children)
                {
                    if (!discovered.Add(child))
                        continue;
                    var row = await inner.LoadMetadataAsync(child, ct);
                    if (row is null || (scope is not null && !scope.Admits(row)))
                        continue;
                    var node = SubAgentProvenance.TryProject(row, current.Parent);
                    if (node is null)
                        continue;
                    found.Add(node with { Depth = current.Depth + 1 });
                    if (recursive)
                        traversal.Enqueue((child, current.Depth + 1));
                }
            }
            lock (_gate)
            {
                if (_faulted)
                    throw new InvalidOperationException(
                        "Hierarchy discovery has an unresolved metadata publication failure."
                    );
                if (_pending == 0 && epoch == _epoch)
                    return
                    [
                        .. found
                            .OrderBy(n => n.Depth)
                            .ThenBy(n => n.ParentThreadId, StringComparer.Ordinal)
                            .ThenBy(n => n.ThreadId, StringComparer.Ordinal),
                    ];
                lastSnapshotEpoch = epoch;
                lastCurrentEpoch = _epoch;
                lastPendingCount = _pending;
            }
        }
        throw new HierarchyBusyException
        {
            Reason = "retry_exhausted",
            PendingCount = lastPendingCount,
            Attempts = 3,
            SnapshotEpoch = lastSnapshotEpoch,
            CurrentEpoch = lastCurrentEpoch,
        };
    }

    public Task SaveMetadataAsync(string threadId, ThreadMetadata metadata, CancellationToken ct = default) =>
        MutateAsync(threadId, () => inner.SaveMetadataAsync(threadId, metadata, ct), ct);

    public Task UpdateMetadataAsync(
        string threadId,
        Func<ThreadMetadata?, ThreadMetadata> update,
        CancellationToken ct = default
    ) => MutateAsync(threadId, () => inner.UpdateMetadataAsync(threadId, update, ct), ct);

    public Task DeleteThreadAsync(string threadId, CancellationToken ct = default) =>
        MutateAsync(threadId, () => inner.DeleteThreadAsync(threadId, ct), ct);

    public Task<ThreadMetadata?> LoadMetadataAsync(string threadId, CancellationToken ct = default) =>
        inner.LoadMetadataAsync(threadId, ct);

    public Task AppendMessagesAsync(
        string threadId,
        IReadOnlyList<PersistedMessage> messages,
        CancellationToken ct = default
    ) => inner.AppendMessagesAsync(threadId, messages, ct);

    public Task<IReadOnlyList<PersistedMessage>> LoadMessagesAsync(string threadId, CancellationToken ct = default) =>
        inner.LoadMessagesAsync(threadId, ct);

    public Task ReplaceMessageAsync(string threadId, PersistedMessage replacement, CancellationToken ct = default) =>
        inner.ReplaceMessageAsync(threadId, replacement, ct);

    public Task<long> GetMessageWatermarkAsync(string threadId, CancellationToken ct = default) =>
        inner.GetMessageWatermarkAsync(threadId, ct);

    public Task<IReadOnlyList<PersistedMessage>> LoadMessageRangeAsync(
        string threadId,
        long fromSeq,
        long toSeq,
        int limit,
        CancellationToken ct = default
    ) => inner.LoadMessageRangeAsync(threadId, fromSeq, toSeq, limit, ct);

    public Task<IReadOnlyList<ThreadMetadata>> ListThreadsAsync(
        int limit = 50,
        int offset = 0,
        ConversationListOptions? options = null,
        CancellationToken ct = default
    ) => inner.ListThreadsAsync(limit, offset, options, ct);

    public Task<IReadOnlyList<ThreadMetadata>> ListThreadsAsync(
        ConversationListScope scope,
        int limit = 50,
        int offset = 0,
        ConversationListOptions? options = null,
        CancellationToken ct = default
    ) => inner.ListThreadsAsync(scope, limit, offset, options, ct);

    public async Task<int> StampUnownedThreadsAsync(string quarantineTenantId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(quarantineTenantId);
        var rows = await ListThreadsAsync(int.MaxValue, ct: ct);
        var count = 0;
        foreach (var row in rows.Where(m => m.TenantId is null))
        {
            await UpdateMetadataAsync(
                row.ThreadId,
                existing => (existing ?? row) with { TenantId = quarantineTenantId },
                ct
            );
            count++;
        }
        return count;
    }

    public Task<IReadOnlyList<string>> ListThreadIdsByTenantAsync(
        string tenantId,
        IReadOnlyCollection<string>? threadIds,
        CancellationToken ct = default
    ) => Capability<IConversationOwnershipStore>().ListThreadIdsByTenantAsync(tenantId, threadIds, ct);

    public async Task<int> AdoptThreadsAsync(
        string fromTenantId,
        string toTenantId,
        string? ownerUserId,
        IReadOnlyCollection<string>? threadIds,
        CancellationToken ct = default
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fromTenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(toTenantId);
        var ids = await ListThreadIdsByTenantAsync(fromTenantId, threadIds, ct);
        foreach (var id in ids)
            await UpdateMetadataAsync(
                id,
                existing =>
                    (
                        existing
                        ?? new ThreadMetadata
                        {
                            ThreadId = id,
                            LastUpdated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        }
                    ) with
                    {
                        TenantId = toTenantId,
                        OwnerUserId = ownerUserId ?? existing?.OwnerUserId,
                    },
                ct
            );
        return ids.Count;
    }

    public Task UpsertRunLedgerAsync(RunLedgerEntry entry, CancellationToken ct = default) =>
        Capability<IRunLedgerStore>().UpsertRunLedgerAsync(entry, ct);

    public Task<RunLedgerEntry?> LoadRunLedgerAsync(string runId, CancellationToken ct = default) =>
        Capability<IRunLedgerStore>().LoadRunLedgerAsync(runId, ct);

    public Task<IReadOnlyList<RunLedgerEntry>> ListRunLedgerAsync(string threadId, CancellationToken ct = default) =>
        Capability<IRunLedgerStore>().ListRunLedgerAsync(threadId, ct);

    public Task RecordAcceptedInputAsync(
        string threadId,
        string inputId,
        DateTimeOffset acceptedAt,
        CancellationToken ct = default
    ) => Capability<IRunLedgerStore>().RecordAcceptedInputAsync(threadId, inputId, acceptedAt, ct);

    public Task RemoveAcceptedInputAsync(string threadId, string inputId, CancellationToken ct = default) =>
        Capability<IRunLedgerStore>().RemoveAcceptedInputAsync(threadId, inputId, ct);

    public Task<IReadOnlySet<string>> ListAcceptedInputIdsAsync(string threadId, CancellationToken ct = default) =>
        Capability<IRunLedgerStore>().ListAcceptedInputIdsAsync(threadId, ct);

    public Task RecordRunStartedAsync(RunLifecycleState state, CancellationToken ct = default) =>
        Capability<IRunLifecycleStore>().RecordRunStartedAsync(state, ct);

    public Task<RunLifecycleState?> LoadRunLifecycleAsync(string runId, CancellationToken ct = default) =>
        Capability<IRunLifecycleStore>().LoadRunLifecycleAsync(runId, ct);

    public Task<IReadOnlyList<RunLifecycleState>> ListRunLifecycleAsync(
        string threadId,
        CancellationToken ct = default
    ) => Capability<IRunLifecycleStore>().ListRunLifecycleAsync(threadId, ct);

    public Task<IReadOnlyList<RunLifecycleState>> ListNonTerminalRunsAsync(
        string threadId,
        CancellationToken ct = default
    ) => Capability<IRunLifecycleStore>().ListNonTerminalRunsAsync(threadId, ct);

    public Task<bool> TryMarkRunTerminalAsync(
        string runId,
        string outcome,
        int turnCount,
        DateTimeOffset terminalAt,
        CancellationToken ct = default
    ) => Capability<IRunLifecycleStore>().TryMarkRunTerminalAsync(runId, outcome, turnCount, terminalAt, ct);

    public Task<DeferredToolCallRecord> RecordDeferredToolCallAsync(
        string runId,
        DeferredToolCallRecord record,
        CancellationToken ct = default
    ) => Capability<IRunLifecycleStore>().RecordDeferredToolCallAsync(runId, record, ct);

    public Task<DeferredResolutionOutcome> TryResolveDeferredToolCallAsync(
        string threadId,
        string toolCallId,
        string resolutionFingerprint,
        string? childRunId,
        DateTimeOffset resolvedAt,
        CancellationToken ct = default
    ) =>
        Capability<IRunLifecycleStore>()
            .TryResolveDeferredToolCallAsync(threadId, toolCallId, resolutionFingerprint, childRunId, resolvedAt, ct);

    public Task<string?> AttachDeferredChildRunAsync(
        string threadId,
        string toolCallId,
        string childRunId,
        DateTimeOffset attachedAt,
        CancellationToken ct = default
    ) => Capability<IRunLifecycleStore>().AttachDeferredChildRunAsync(threadId, toolCallId, childRunId, attachedAt, ct);

    public Task<InputAcceptance?> TryReserveAcceptanceAsync(
        InputAcceptance acceptance,
        CancellationToken ct = default
    ) => Capability<IInputAcceptanceStore>().TryReserveAcceptanceAsync(acceptance, ct);

    public Task<InputAcceptance?> GetAcceptanceAsync(string threadId, string inputId, CancellationToken ct = default) =>
        Capability<IInputAcceptanceStore>().GetAcceptanceAsync(threadId, inputId, ct);

    public Task<bool> TryRecordOutcomeAsync(InputAcceptance acceptance, CancellationToken ct = default) =>
        Capability<IInputAcceptanceStore>().TryRecordOutcomeAsync(acceptance, ct);

    public Task<bool> TryReleaseAcceptanceAsync(
        string threadId,
        string inputId,
        Guid reservationId,
        CancellationToken ct = default
    ) => Capability<IInputAcceptanceStore>().TryReleaseAcceptanceAsync(threadId, inputId, reservationId, ct);
}
