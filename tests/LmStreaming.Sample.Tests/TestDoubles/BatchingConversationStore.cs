namespace LmStreaming.Sample.Tests.TestDoubles;

/// <summary>
/// A store that can batch metadata reads, over any store that cannot. It records each batch and lets a test
/// hold, fail or inspect it: <see cref="OnBatch"/> runs after the rows are read and before they are returned.
/// </summary>
internal sealed class BatchingConversationStore(IConversationStore inner)
    : IConversationStore,
        IConversationMetadataBatchStore
{
    private readonly List<IReadOnlyList<string>> _batches = [];

    /// <summary>The ids of every batch read so far, in call order.</summary>
    public IReadOnlyList<IReadOnlyList<string>> Batches
    {
        get
        {
            lock (_batches)
            {
                return [.. _batches];
            }
        }
    }

    /// <summary>Runs with each batch's call number (from 1) and token once its rows are read.</summary>
    public Func<int, CancellationToken, Task>? OnBatch { get; set; }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, ThreadMetadata>> LoadMetadataBatchAsync(
        IReadOnlyCollection<string> threadIds,
        CancellationToken ct = default
    )
    {
        int call;
        lock (_batches)
        {
            _batches.Add([.. threadIds]);
            call = _batches.Count;
        }

        var rows = new Dictionary<string, ThreadMetadata>(StringComparer.Ordinal);
        foreach (var threadId in threadIds.Distinct(StringComparer.Ordinal))
        {
            if (await inner.LoadMetadataAsync(threadId, ct) is { } row)
            {
                rows[threadId] = row;
            }
        }

        if (OnBatch is { } onBatch)
        {
            await onBatch(call, ct);
        }

        return rows;
    }

    /// <inheritdoc />
    public Task AppendMessagesAsync(
        string threadId,
        IReadOnlyList<PersistedMessage> messages,
        CancellationToken ct = default
    ) => inner.AppendMessagesAsync(threadId, messages, ct);

    /// <inheritdoc />
    public Task<IReadOnlyList<PersistedMessage>> LoadMessagesAsync(string threadId, CancellationToken ct = default) =>
        inner.LoadMessagesAsync(threadId, ct);

    /// <inheritdoc />
    public Task ReplaceMessageAsync(string threadId, PersistedMessage replacement, CancellationToken ct = default) =>
        inner.ReplaceMessageAsync(threadId, replacement, ct);

    /// <inheritdoc />
    public Task SaveMetadataAsync(string threadId, ThreadMetadata metadata, CancellationToken ct = default) =>
        inner.SaveMetadataAsync(threadId, metadata, ct);

    /// <inheritdoc />
    public Task<ThreadMetadata?> LoadMetadataAsync(string threadId, CancellationToken ct = default) =>
        inner.LoadMetadataAsync(threadId, ct);

    /// <inheritdoc />
    public Task UpdateMetadataAsync(
        string threadId,
        Func<ThreadMetadata?, ThreadMetadata> update,
        CancellationToken ct = default
    ) => inner.UpdateMetadataAsync(threadId, update, ct);

    /// <inheritdoc />
    public Task DeleteThreadAsync(string threadId, CancellationToken ct = default) =>
        inner.DeleteThreadAsync(threadId, ct);

    /// <inheritdoc />
    public Task<IReadOnlyList<ThreadMetadata>> ListThreadsAsync(
        int limit = 50,
        int offset = 0,
        ConversationListOptions? options = null,
        CancellationToken ct = default
    ) => inner.ListThreadsAsync(limit, offset, options, ct);
}
