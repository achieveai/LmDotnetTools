namespace LmStreaming.Sample.Tests.TestDoubles;

/// <summary>
/// A forwarding <see cref="IConversationStore"/> view that counts listing calls - BOTH overloads,
/// on one counter - so a test can assert a code path never pays for the bounded-but-still-expensive
/// persisted-thread scan (<c>AgentHierarchyService.ScanPersistedSubAgentChildrenAsync</c>).
/// </summary>
/// <param name="inner">The real store every call is forwarded to.</param>
internal sealed class CountingConversationStore(IConversationStore inner) : IConversationStore
{
    private int _listThreadsCalls;

    /// <summary>How many times either listing overload has been called.</summary>
    public int ListThreadsCallCount => Volatile.Read(ref _listThreadsCalls);

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
    )
    {
        Interlocked.Increment(ref _listThreadsCalls);
        return inner.ListThreadsAsync(limit, offset, options, ct);
    }

    /// <summary>
    /// Forwards the SCOPED listing, counting it on the same counter as the unscoped one.
    /// </summary>
    /// <remarks>
    /// One counter, not two, and deliberately: what every caller of
    /// <see cref="ListThreadsCallCount"/> asserts is that a path did not pay for a store scan at
    /// all. Counting the two overloads separately would have let #388a's switch from one to the
    /// other silently zero every one of those assertions while the scan carried on happening.
    /// </remarks>
    /// <param name="scope">The principal's tenant, identity, role and resolved grants.</param>
    /// <param name="limit">Maximum number of threads to return.</param>
    /// <param name="offset">Number of threads to skip.</param>
    /// <param name="options">Presentation shape of the listing; forwarded unchanged.</param>
    /// <param name="ct">Cancellation token.</param>
    public Task<IReadOnlyList<ThreadMetadata>> ListThreadsAsync(
        ConversationListScope scope,
        int limit = 50,
        int offset = 0,
        ConversationListOptions? options = null,
        CancellationToken ct = default
    )
    {
        Interlocked.Increment(ref _listThreadsCalls);
        return inner.ListThreadsAsync(scope, limit, offset, options, ct);
    }
}
