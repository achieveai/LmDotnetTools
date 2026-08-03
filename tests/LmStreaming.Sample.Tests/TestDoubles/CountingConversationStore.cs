namespace LmStreaming.Sample.Tests.TestDoubles;

/// <summary>
/// A forwarding <see cref="IConversationStore"/> view that counts <see cref="ListThreadsAsync"/>
/// calls, so a test can assert a code path never pays for the bounded-but-still-expensive
/// persisted-thread scan (<c>AgentHierarchyService.ScanPersistedSubAgentChildrenAsync</c>).
/// </summary>
/// <param name="inner">The real store every call is forwarded to.</param>
internal sealed class CountingConversationStore(IConversationStore inner) : IConversationStore
{
    private int _listThreadsCalls;

    /// <summary>How many times <see cref="ListThreadsAsync"/> has been called.</summary>
    public int ListThreadsCallCount => Volatile.Read(ref _listThreadsCalls);

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
    public Task<IReadOnlyList<ThreadMetadata>> ListThreadsAsync(
        int limit = 50,
        int offset = 0,
        CancellationToken ct = default)
    {
        Interlocked.Increment(ref _listThreadsCalls);
        return inner.ListThreadsAsync(limit, offset, ct);
    }
}
