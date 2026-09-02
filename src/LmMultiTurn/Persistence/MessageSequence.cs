namespace AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;

/// <summary>
/// The one definition of append order and of legacy backfill, shared by the stores that keep a
/// thread's rows as a list (file, memory) so the three backends cannot drift on either (#680).
/// </summary>
internal static class MessageSequence
{
    /// <summary>
    /// Append order: sequenced rows first, by <see cref="PersistedMessage.Seq"/>; then any legacy
    /// rows without one, by <c>(timestamp, message_order_idx)</c>. The second half is also the order
    /// a backfill assigns, so a thread reads the same before and after its rows are numbered.
    /// </summary>
    public static List<PersistedMessage> Order(IEnumerable<PersistedMessage> messages) =>
        [
            .. messages
                .OrderBy(m => m.Seq is null ? 1 : 0)
                .ThenBy(m => m.Seq ?? 0)
                .ThenBy(m => m.Timestamp)
                .ThenBy(m => m.MessageOrderIdx ?? 0),
        ];

    /// <summary>
    /// The order one append call's rows are numbered in: <c>(timestamp, message_order_idx)</c>, ties
    /// kept in the order given. A batch is one logical write - callers hand over a generation's rows
    /// at once, sometimes assembled out of order - so within it the row order the store always kept
    /// still holds; only ACROSS calls does Seq (append order) win over the clock.
    /// </summary>
    public static List<PersistedMessage> BatchOrder(IEnumerable<PersistedMessage> incoming) =>
        [.. incoming.OrderBy(m => m.Timestamp).ThenBy(m => m.MessageOrderIdx ?? 0)];

    /// <summary>
    /// Returns <paramref name="existing"/> with every legacy row numbered in load order, followed by
    /// <paramref name="incoming"/> numbered after them in <see cref="BatchOrder"/>. A caller-supplied
    /// Seq on an incoming row is ignored: the store owns the sequence.
    /// </summary>
    public static List<PersistedMessage> Append(
        IReadOnlyList<PersistedMessage> existing,
        IReadOnlyList<PersistedMessage> incoming
    )
    {
        var ordered = Order(existing);
        var next = ordered.Count == 0 ? 0 : ordered.Max(m => m.Seq ?? 0);

        for (var i = 0; i < ordered.Count; i++)
        {
            if (ordered[i].Seq is null)
            {
                ordered[i] = ordered[i] with { Seq = ++next };
            }
        }

        foreach (var message in BatchOrder(incoming))
        {
            ordered.Add(message with { Seq = ++next });
        }

        return ordered;
    }

    /// <summary>The highest Seq in <paramref name="messages"/>, or 0 when none is numbered.</summary>
    public static long Watermark(IEnumerable<PersistedMessage> messages) => messages.Max(m => m.Seq ?? 0);

    /// <summary>The rows with a Seq in <c>[fromSeq, toSeq]</c>, ascending, at most <paramref name="limit"/>.</summary>
    public static List<PersistedMessage> Range(
        IEnumerable<PersistedMessage> messages,
        long fromSeq,
        long toSeq,
        int limit
    ) =>
        limit <= 0 || toSeq < fromSeq
            ? []
            :
            [
                .. messages
                    .Where(m => m.Seq is { } seq && seq >= fromSeq && seq <= toSeq)
                    .OrderBy(m => m.Seq)
                    .Take(limit),
            ];
}
