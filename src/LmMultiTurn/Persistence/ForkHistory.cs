namespace AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;

/// <summary>
/// The one definition of how a fork's history is read, shared by every store so the backends cannot
/// drift. A fork stores only its own rows; its history is the forked conversation's rows up to the
/// <see cref="ForkPoint"/> followed by its own (see <see cref="ConversationLineage"/>).
/// </summary>
/// <remarks>
/// Each helper takes the store's already-read OWN rows (or a delegate for them) and reaches the
/// forked conversation through the store's PUBLIC read methods, so a fork of a fork resolves by
/// recursion. Callers must not hold a store lock across these calls: the recursion re-enters the
/// store.
/// </remarks>
internal static class ForkHistory
{
    /// <summary>
    /// The fork point of <paramref name="threadId"/>, or <c>null</c> when it is not a fork. A lineage
    /// that names the thread itself is ignored rather than followed into endless recursion.
    /// </summary>
    public static async Task<ForkPoint?> ForkPointAsync(IConversationStore store, string threadId, CancellationToken ct)
    {
        var metadata = await store.LoadMetadataAsync(threadId, ct).ConfigureAwait(false);
        var point = ConversationLineage.Read(metadata)?.ForkedFrom;
        return point is null || string.Equals(point.ThreadId, threadId, StringComparison.Ordinal) ? null : point;
    }

    /// <summary>The full history: the forked conversation's rows up to the fork point, then <paramref name="own"/>.</summary>
    public static async Task<IReadOnlyList<PersistedMessage>> LoadAsync(
        IConversationStore store,
        string threadId,
        IReadOnlyList<PersistedMessage> own,
        CancellationToken ct
    )
    {
        var point = await ForkPointAsync(store, threadId, ct).ConfigureAwait(false);
        if (point is null)
        {
            return MessageSequence.WithParents(own);
        }

        var shared =
            point.Seq <= 0
                ? []
                : await store
                    .LoadMessageRangeAsync(point.ThreadId, 1, point.Seq, int.MaxValue, ct)
                    .ConfigureAwait(false);

        return MessageSequence.WithParents([.. shared, .. own]);
    }

    /// <summary>
    /// The rows of the full history with a Seq in <c>[fromSeq, toSeq]</c>, ascending, at most
    /// <paramref name="limit"/>: the shared part from the forked conversation, the rest from
    /// <paramref name="ownRange"/>.
    /// </summary>
    public static async Task<IReadOnlyList<PersistedMessage>> RangeAsync(
        IConversationStore store,
        string threadId,
        long fromSeq,
        long toSeq,
        int limit,
        Func<long, long, int, Task<IReadOnlyList<PersistedMessage>>> ownRange,
        CancellationToken ct
    )
    {
        if (limit <= 0 || toSeq < fromSeq)
        {
            return [];
        }

        var point = await ForkPointAsync(store, threadId, ct).ConfigureAwait(false);
        if (point is null)
        {
            return await ownRange(fromSeq, toSeq, limit).ConfigureAwait(false);
        }

        var rows = new List<PersistedMessage>();
        if (fromSeq <= point.Seq)
        {
            rows.AddRange(
                await store
                    .LoadMessageRangeAsync(point.ThreadId, fromSeq, Math.Min(toSeq, point.Seq), limit, ct)
                    .ConfigureAwait(false)
            );
        }

        if (rows.Count < limit && toSeq > point.Seq)
        {
            rows.AddRange(
                await ownRange(Math.Max(fromSeq, point.Seq + 1), toSeq, limit - rows.Count).ConfigureAwait(false)
            );
        }

        return rows;
    }

    /// <summary>
    /// The highest Seq of the full history: the own watermark once the fork has rows of its own (they
    /// are numbered after the fork point), else the fork point's Seq.
    /// </summary>
    public static async Task<long> WatermarkAsync(
        IConversationStore store,
        string threadId,
        long ownWatermark,
        CancellationToken ct
    ) => ownWatermark > 0 ? ownWatermark : (await ForkPointAsync(store, threadId, ct).ConfigureAwait(false))?.Seq ?? 0;
}
