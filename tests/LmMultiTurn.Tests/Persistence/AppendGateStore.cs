using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;

namespace LmMultiTurn.Tests.Persistence;

/// <summary>
/// Controls message appends; everything else goes straight to <paramref name="inner"/>. A row that
/// <paramref name="holds"/> picks waits until <see cref="Release"/>, like a store on a slow disk, or for good
/// if it is never released. With <paramref name="refuse"/> every append throws at once, the way a locked
/// SQLite database does.
/// </summary>
internal sealed class AppendGateStore(
    IConversationStore inner,
    Func<PersistedMessage, bool>? holds = null,
    bool refuse = false
) : IConversationStore
{
    private readonly TaskCompletionSource _held = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _refused;

    public IConversationStore Inner => inner;

    /// <summary>Completes when the first held row has reached the store and is waiting there.</summary>
    public Task FirstHeld => _held.Task;

    /// <summary>How many appends were refused.</summary>
    public int Refused => Volatile.Read(ref _refused);

    /// <summary>Lets every held row through, and every later one without waiting.</summary>
    public void Release() => _release.TrySetResult();

    public async Task AppendMessagesAsync(
        string threadId,
        IReadOnlyList<PersistedMessage> messages,
        CancellationToken ct = default
    )
    {
        if (refuse)
        {
            _ = Interlocked.Increment(ref _refused);
            throw new InvalidOperationException("SQLite Error 5: 'database is locked'.");
        }

        if (holds is not null && messages.Any(holds))
        {
            _ = _held.TrySetResult();
            await _release.Task.WaitAsync(ct);
        }

        await inner.AppendMessagesAsync(threadId, messages, ct);
    }

    public Task<IReadOnlyList<PersistedMessage>> LoadMessagesAsync(string threadId, CancellationToken ct = default) =>
        inner.LoadMessagesAsync(threadId, ct);

    public Task<long> GetMessageWatermarkAsync(string threadId, CancellationToken ct = default) =>
        inner.GetMessageWatermarkAsync(threadId, ct);

    public Task<IReadOnlyList<PersistedMessage>> LoadMessageRangeAsync(
        string threadId,
        long fromSeq,
        long toSeq,
        int limit,
        CancellationToken ct = default
    ) => inner.LoadMessageRangeAsync(threadId, fromSeq, toSeq, limit, ct);

    public Task ReplaceMessageAsync(string threadId, PersistedMessage replacement, CancellationToken ct = default) =>
        inner.ReplaceMessageAsync(threadId, replacement, ct);

    public Task SaveMetadataAsync(string threadId, ThreadMetadata metadata, CancellationToken ct = default) =>
        inner.SaveMetadataAsync(threadId, metadata, ct);

    public Task<ThreadMetadata?> LoadMetadataAsync(string threadId, CancellationToken ct = default) =>
        inner.LoadMetadataAsync(threadId, ct);

    public Task UpdateMetadataAsync(
        string threadId,
        Func<ThreadMetadata?, ThreadMetadata> update,
        CancellationToken ct = default
    ) => inner.UpdateMetadataAsync(threadId, update, ct);

    public Task DeleteThreadAsync(string threadId, CancellationToken ct = default) =>
        inner.DeleteThreadAsync(threadId, ct);

    public Task<IReadOnlyList<ThreadMetadata>> ListThreadsAsync(
        int limit = 50,
        int offset = 0,
        ConversationListOptions? options = null,
        CancellationToken ct = default
    ) => inner.ListThreadsAsync(limit, offset, options, ct);
}
