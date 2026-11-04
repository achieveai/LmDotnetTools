namespace LmStreaming.Sample.Tests.TestDoubles;

/// <summary>
/// A forwarding <see cref="IConversationStore"/> that can stop one metadata update after its callback has
/// built the new record and before the wrapped store writes it. That is the window in which a decorator has
/// already judged what the write changes, but the write has not landed.
/// </summary>
/// <param name="inner">The real store every call is forwarded to.</param>
internal sealed class HoldingConversationStore(IConversationStore inner) : IConversationStore
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);
    private readonly ManualResetEventSlim _entered = new();
    private readonly ManualResetEventSlim _released = new();
    private string? _held;

    /// <summary>
    /// Starts <paramref name="write"/>, which must update <paramref name="threadId"/> through this store, and
    /// returns once that update is held. The returned task completes after <see cref="Release"/>.
    /// </summary>
    public async Task<Task> HoldUpdateAsync(string threadId, Func<Task> write)
    {
        _entered.Reset();
        _released.Reset();
        Volatile.Write(ref _held, threadId);
        var pending = Task.Run(write);
        var entered = Task.Run(() => _entered.Wait(Limit));
        if (await Task.WhenAny(pending, entered) == pending)
        {
            await pending;
            throw new InvalidOperationException($"The write finished without updating {threadId}.");
        }
        if (!await entered)
            throw new TimeoutException($"The update of {threadId} was never held.");
        return pending;
    }

    /// <summary>Lets the held update write.</summary>
    public void Release() => _released.Set();

    /// <inheritdoc />
    public Task UpdateMetadataAsync(
        string threadId,
        Func<ThreadMetadata?, ThreadMetadata> update,
        CancellationToken ct = default
    ) =>
        inner.UpdateMetadataAsync(
            threadId,
            row =>
            {
                var next = update(row);
                var held = Volatile.Read(ref _held);
                if (held == threadId && Interlocked.CompareExchange(ref _held, null, held) == held)
                {
                    _entered.Set();
                    if (!_released.Wait(Limit))
                        throw new TimeoutException($"The test never released the update of {threadId}.");
                }
                return next;
            },
            ct
        );

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
