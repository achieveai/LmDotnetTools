using System.Collections.Concurrent;

namespace AchieveAi.LmDotnetTools.LmWorkflow.Persistence;

/// <summary>
///     In-memory <see cref="IWorkflowStore"/> for tests and single-process hosting. Snapshots live in a
///     <see cref="ConcurrentDictionary{TKey,TValue}"/>; every write is serialized by a single lock — mirroring
///     <c>InMemoryConversationStore</c> — so the store never exposes a half-written entry. Both
///     <see cref="SaveAsync"/> and <see cref="LoadAsync"/> deep-copy the snapshot (serialize/deserialize)
///     so the stored value can never alias the live runtime's <see cref="System.Text.Json.Nodes.JsonNode"/>
///     channels, and a later mutation of the runtime cannot retroactively change a persisted snapshot.
/// </summary>
public sealed class InMemoryWorkflowStore : IWorkflowStore
{
    private readonly ConcurrentDictionary<string, WorkflowInstanceSnapshot> _snapshots = new(
        StringComparer.Ordinal
    );
    private readonly object _writeLock = new();

    /// <inheritdoc />
    public Task SaveAsync(
        string instanceId,
        WorkflowInstanceSnapshot snapshot,
        CancellationToken ct = default
    )
    {
        ArgumentException.ThrowIfNullOrEmpty(instanceId);
        ArgumentNullException.ThrowIfNull(snapshot);

        // Copy BEFORE taking the lock so the (potentially non-trivial) serialization does not serialize
        // every concurrent writer; the lock only guards the dictionary swap.
        var isolated = snapshot.DeepCopy();
        lock (_writeLock)
        {
            _snapshots[instanceId] = isolated;
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<WorkflowInstanceSnapshot?> LoadAsync(
        string instanceId,
        CancellationToken ct = default
    )
    {
        ArgumentException.ThrowIfNullOrEmpty(instanceId);

        // Hand back an isolated copy so a caller mutating the returned snapshot cannot corrupt the store.
        return Task.FromResult(
            _snapshots.TryGetValue(instanceId, out var snapshot) ? snapshot.DeepCopy() : null
        );
    }

    /// <inheritdoc />
    public Task DeleteAsync(string instanceId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(instanceId);

        lock (_writeLock)
        {
            _ = _snapshots.TryRemove(instanceId, out _);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> ListAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<string>>([.. _snapshots.Keys]);
}
