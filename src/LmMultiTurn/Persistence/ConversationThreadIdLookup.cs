namespace AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;

/// <summary>
/// Prefix lookup over any <see cref="IConversationStore"/>: uses
/// <see cref="IConversationThreadIdLookupStore"/> when the store has it, otherwise pages through the
/// full listing. Decorators that always advertise the capability forward through this so a wrapped
/// store without it still answers instead of throwing.
/// </summary>
public static class ConversationThreadIdLookup
{
    private const int ListingPageSize = 500;

    /// <summary>
    /// Lists thread ids in <paramref name="store"/> beginning with <paramref name="prefix"/>, ordered
    /// ordinally.
    /// </summary>
    public static async Task<IReadOnlyList<string>> ListThreadIdsByPrefixAsync(
        IConversationStore store,
        string prefix,
        CancellationToken ct = default
    )
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(prefix);

        if (store is IConversationThreadIdLookupStore lookup)
        {
            return await lookup.ListThreadIdsByPrefixAsync(prefix, ct).ConfigureAwait(false);
        }

        var threadIds = new List<string>();
        for (var offset = 0; ; offset += ListingPageSize)
        {
            var page = await store.ListThreadsAsync(ListingPageSize, offset, options: null, ct).ConfigureAwait(false);
            foreach (var row in page)
            {
                if (row.ThreadId.StartsWith(prefix, StringComparison.Ordinal))
                {
                    threadIds.Add(row.ThreadId);
                }
            }

            if (page.Count < ListingPageSize)
            {
                break;
            }
        }

        threadIds.Sort(StringComparer.Ordinal);
        return threadIds;
    }
}
