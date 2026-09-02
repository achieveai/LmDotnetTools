using AchieveAi.LmDotnetTools.LmCore.Models;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;

namespace AchieveAi.LmDotnetTools.LmMultiTurn.Compaction;

/// <summary>
///     Answers "when did this thread last do anything?" from DURABLE state only - the metadata, the
///     newest row, the run ledger and the run lifecycle - so a cache temperature derived from it is
///     the same answer before and after a restart (#680; spec 679 §4.4).
/// </summary>
/// <remarks>
///     Nothing here starts work. A Cold answer only marks that the next request is expected to pay
///     for its prompt cache again; the policy (#684) decides what, if anything, to do with that.
/// </remarks>
public static class ConversationActivity
{
    /// <summary>
    ///     The newest of: the metadata's <see cref="ThreadMetadata.LastUpdated" />, the timestamp of the
    ///     most recently appended row, the run ledger's newest <c>UpdatedAt</c>, and the run lifecycle's
    ///     newest <c>UpdatedAt</c> / <c>TerminalAt</c>. Null when the thread has none of them.
    /// </summary>
    public static async Task<DateTimeOffset?> GetLastActivityAsync(
        IConversationStore store,
        string threadId,
        CancellationToken ct = default
    )
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(threadId);

        DateTimeOffset? latest = null;

        var metadata = await store.LoadMetadataAsync(threadId, ct).ConfigureAwait(false);
        if (metadata is { LastUpdated: > 0 })
        {
            latest = Newest(latest, DateTimeOffset.FromUnixTimeMilliseconds(metadata.LastUpdated));
        }

        // The row at the watermark is the last one appended. A legacy thread (watermark 0) has no
        // sequence to index by yet, so it is read in full - once; its first append numbers it.
        var watermark = await store.GetMessageWatermarkAsync(threadId, ct).ConfigureAwait(false);
        var rows =
            watermark > 0
                ? await store.LoadMessageRangeAsync(threadId, watermark, watermark, 1, ct).ConfigureAwait(false)
                : await store.LoadMessagesAsync(threadId, ct).ConfigureAwait(false);
        foreach (var row in rows)
        {
            latest = Newest(latest, DateTimeOffset.FromUnixTimeMilliseconds(row.Timestamp));
        }

        if (store is IRunLedgerStore ledger)
        {
            foreach (var entry in await ledger.ListRunLedgerAsync(threadId, ct).ConfigureAwait(false))
            {
                latest = Newest(latest, entry.UpdatedAt);
            }
        }

        if (store is IRunLifecycleStore lifecycle)
        {
            foreach (var run in await lifecycle.ListRunLifecycleAsync(threadId, ct).ConfigureAwait(false))
            {
                latest = Newest(latest, run.UpdatedAt);
                if (run.TerminalAt is { } terminalAt)
                {
                    latest = Newest(latest, terminalAt);
                }
            }
        }

        return latest;
    }

    /// <summary>
    ///     <see cref="CacheTemperature.Hot" /> when <paramref name="lastActivity" /> is strictly inside the
    ///     model's cache <paramref name="ttl" /> of <paramref name="now" />; <see cref="CacheTemperature.Cold" />
    ///     otherwise, including when nothing durable has ever happened; <see cref="CacheTemperature.Unknown" />
    ///     when prompt caching is off, because then there is no cache to be hot or cold.
    /// </summary>
    public static CacheTemperature ResolveCacheTemperature(
        DateTimeOffset? lastActivity,
        DateTimeOffset now,
        TimeSpan ttl,
        bool cachingEnabled
    )
    {
        if (!cachingEnabled)
        {
            return CacheTemperature.Unknown;
        }

        return lastActivity is { } at && now - at < ttl ? CacheTemperature.Hot : CacheTemperature.Cold;
    }

    private static DateTimeOffset? Newest(DateTimeOffset? current, DateTimeOffset candidate) =>
        current is null || candidate > current ? candidate : current;
}
