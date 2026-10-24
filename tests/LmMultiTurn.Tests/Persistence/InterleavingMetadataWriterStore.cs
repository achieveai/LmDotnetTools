using System.Collections.Immutable;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;

namespace LmMultiTurn.Tests.Persistence;

/// <summary>
/// Lets another writer land immediately before every metadata write that records a completed run
/// (a result carrying <see cref="ThreadMetadata.LatestRunId"/>), which is exactly where a read-then-save
/// loses it: the writer read the row before this property existed and saves that stale copy back. An
/// atomic <see cref="IConversationStore.UpdateMetadataAsync"/> sees the property inside its callback and
/// keeps it. Each landing writes a new value, so <see cref="Injections"/> is the value that must survive.
/// </summary>
/// <remarks>
/// To decide whether an atomic update records a completed run, the update callback is evaluated once
/// against the current row before the real call. Every callback the loops pass is a pure function of
/// its input, so the extra evaluation changes nothing; a callback that throws is not intercepted.
/// </remarks>
internal sealed class InterleavingMetadataWriterStore(IConversationStore inner, string threadId) : IConversationStore
{
    /// <summary>The property the other writer sets.</summary>
    public const string OtherWriterKey = "test.other-writer";

    private int _injections;

    /// <summary>How many times the other writer landed; also the value it wrote last.</summary>
    public int Injections => Volatile.Read(ref _injections);

    public async Task SaveMetadataAsync(string id, ThreadMetadata metadata, CancellationToken ct = default)
    {
        if (id == threadId && metadata.LatestRunId is not null)
        {
            await LandOtherWriterAsync();
        }

        await inner.SaveMetadataAsync(id, metadata, ct);
    }

    public async Task UpdateMetadataAsync(
        string id,
        Func<ThreadMetadata?, ThreadMetadata> update,
        CancellationToken ct = default
    )
    {
        if (id == threadId && await RecordsCompletedRunAsync(id, update))
        {
            await LandOtherWriterAsync();
        }

        await inner.UpdateMetadataAsync(id, update, ct);
    }

    private async Task<bool> RecordsCompletedRunAsync(string id, Func<ThreadMetadata?, ThreadMetadata> update)
    {
        try
        {
            return update(await inner.LoadMetadataAsync(id)).LatestRunId is not null;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private Task LandOtherWriterAsync()
    {
        var value = Interlocked.Increment(ref _injections);
        return inner.UpdateMetadataAsync(
            threadId,
            existing =>
                (existing ?? new ThreadMetadata { ThreadId = threadId, LastUpdated = 0 }) with
                {
                    Properties = (existing?.Properties ?? ImmutableDictionary<string, object>.Empty).SetItem(
                        OtherWriterKey,
                        value
                    ),
                }
        );
    }

    public Task<ThreadMetadata?> LoadMetadataAsync(string id, CancellationToken ct = default) =>
        inner.LoadMetadataAsync(id, ct);

    public Task AppendMessagesAsync(
        string id,
        IReadOnlyList<PersistedMessage> messages,
        CancellationToken ct = default
    ) => inner.AppendMessagesAsync(id, messages, ct);

    public Task<IReadOnlyList<PersistedMessage>> LoadMessagesAsync(string id, CancellationToken ct = default) =>
        inner.LoadMessagesAsync(id, ct);

    public Task<long> GetMessageWatermarkAsync(string id, CancellationToken ct = default) =>
        inner.GetMessageWatermarkAsync(id, ct);

    public Task<IReadOnlyList<PersistedMessage>> LoadMessageRangeAsync(
        string id,
        long fromSeq,
        long toSeq,
        int limit,
        CancellationToken ct = default
    ) => inner.LoadMessageRangeAsync(id, fromSeq, toSeq, limit, ct);

    public Task ReplaceMessageAsync(string id, PersistedMessage replacement, CancellationToken ct = default) =>
        inner.ReplaceMessageAsync(id, replacement, ct);

    public Task NumberLegacyMessagesAsync(string id, CancellationToken ct = default) =>
        inner.NumberLegacyMessagesAsync(id, ct);

    public Task DeleteThreadAsync(string id, CancellationToken ct = default) => inner.DeleteThreadAsync(id, ct);

    public Task<IReadOnlyList<ThreadMetadata>> ListThreadsAsync(
        int limit = 50,
        int offset = 0,
        ConversationListOptions? options = null,
        CancellationToken ct = default
    ) => inner.ListThreadsAsync(limit, offset, options, ct);
}
