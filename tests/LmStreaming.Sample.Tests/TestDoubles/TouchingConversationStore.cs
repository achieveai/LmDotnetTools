namespace LmStreaming.Sample.Tests.TestDoubles;

/// <summary>
/// A forwarding <see cref="IConversationStore"/> view that models the one thing a persisted-thread scan
/// cannot assume away: the store keeps changing while the scan reads it. After every
/// <see cref="ListThreadsAsync"/> call it bumps one thread's <c>LastUpdated</c>, which — because
/// <see cref="IConversationStore.ListThreadsAsync"/> is contractually "ordered by last updated
/// descending" — moves that thread to the front of the ordering the next call will see.
/// </summary>
/// <remarks>
///     The bump happens AFTER the page has been read, so it can never corrupt the call that triggered it.
///     That is deliberate: a scan that reads the store exactly once is unaffected by this double no matter
///     how much the store churns, while an offset-paged scan loses the touched thread — it slid forward
///     past an offset the scan has already stepped over. The double therefore isolates the paging, not the
///     mutation.
/// </remarks>
/// <param name="inner">The real store every call is forwarded to.</param>
/// <param name="threadIdToTouch">The thread whose <c>LastUpdated</c> is bumped after each listing.</param>
internal sealed class TouchingConversationStore(IConversationStore inner, string threadIdToTouch)
    : IConversationStore
{
    private long _stamp = long.MaxValue / 2;

    /// <inheritdoc />
    public Task AppendMessagesAsync(
        string threadId,
        IReadOnlyList<PersistedMessage> messages,
        CancellationToken ct = default) => inner.AppendMessagesAsync(threadId, messages, ct);

    /// <inheritdoc />
    public Task<IReadOnlyList<PersistedMessage>> LoadMessagesAsync(
        string threadId,
        CancellationToken ct = default) => inner.LoadMessagesAsync(threadId, ct);

    /// <inheritdoc />
    public Task ReplaceMessageAsync(
        string threadId,
        PersistedMessage replacement,
        CancellationToken ct = default) => inner.ReplaceMessageAsync(threadId, replacement, ct);

    /// <inheritdoc />
    public Task SaveMetadataAsync(
        string threadId,
        ThreadMetadata metadata,
        CancellationToken ct = default) => inner.SaveMetadataAsync(threadId, metadata, ct);

    /// <inheritdoc />
    public Task<ThreadMetadata?> LoadMetadataAsync(string threadId, CancellationToken ct = default) =>
        inner.LoadMetadataAsync(threadId, ct);

    /// <inheritdoc />
    public Task UpdateMetadataAsync(
        string threadId,
        Func<ThreadMetadata?, ThreadMetadata> update,
        CancellationToken ct = default) => inner.UpdateMetadataAsync(threadId, update, ct);

    /// <inheritdoc />
    public Task DeleteThreadAsync(string threadId, CancellationToken ct = default) =>
        inner.DeleteThreadAsync(threadId, ct);

    /// <inheritdoc />
    public async Task<IReadOnlyList<ThreadMetadata>> ListThreadsAsync(
        int limit = 50,
        int offset = 0,
        CancellationToken ct = default)
    {
        var page = await inner.ListThreadsAsync(limit, offset, ct);

        var current = await inner.LoadMetadataAsync(threadIdToTouch, ct);
        if (current is not null)
        {
            await inner.SaveMetadataAsync(
                threadIdToTouch,
                current with { LastUpdated = Interlocked.Increment(ref _stamp) },
                ct);
        }

        return page;
    }
}
