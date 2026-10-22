namespace CodeReviewDaemon.Sample.Agents;

/// <summary>
/// One shared copy of the review host's conversation listing (<c>GET api/conversations</c>), so the reviews in a
/// process read it instead of each paging the whole list. Paging it per lookup overloaded the host on 2026-10-22:
/// with 13k conversations a lookup was 100 page calls, and the burst of launches after a batch restart took the
/// list call from 2 s to 120 s timeouts.
/// <para>
/// The first lookup that needs a copy fetches it; lookups arriving meanwhile wait for that fetch rather than start
/// their own. A copy older than <paramref name="maxAge"/> is fetched again. A lookup for a thread the copy lacks
/// fetches again only once the copy is older than <paramref name="missRefreshAge"/>, so a thread that is really
/// absent costs at most one fetch per that interval. The client's own writes (provision, title, provider switch,
/// delete) change the copy at once, and are applied again to a fetch already running, whose pages may predate them.
/// </para>
/// </summary>
internal sealed class ConversationListingCache(
    TimeSpan maxAge,
    TimeSpan missRefreshAge,
    Func<DateTimeOffset>? clock = null
)
{
    /// <summary>The listed fields the review client reads.</summary>
    internal sealed record Row(
        string ThreadId,
        string? Title,
        string? Workspace,
        string? Mode,
        string? Provider,
        long LastUpdated,
        bool ClientToolsDisabled
    );

    private readonly Func<DateTimeOffset> _now = clock ?? (() => DateTimeOffset.UtcNow);
    private readonly SemaphoreSlim _fetching = new(1, 1);
    private readonly object _gate = new();
    private IReadOnlyList<Row>? _rows;
    private DateTimeOffset _fetchedAt;
    private List<(string ThreadId, Func<Row?, Row?> Change)>? _writesDuringFetch;

    /// <summary>
    /// The listing, fetched through <paramref name="fetch"/> when there is no usable copy. With
    /// <paramref name="threadId"/>, a copy that lacks that thread is usable only while younger than the miss age.
    /// </summary>
    public async Task<IReadOnlyList<Row>> RowsAsync(
        Func<CancellationToken, Task<IReadOnlyList<Row>>> fetch,
        string? threadId,
        CancellationToken ct
    )
    {
        if (Usable(threadId) is { } rows)
            return rows;
        await _fetching.WaitAsync(ct);
        try
        {
            // A fetch that finished while this lookup waited serves it too.
            if (Usable(threadId) is { } fetchedMeanwhile)
                return fetchedMeanwhile;
            var started = _now();
            lock (_gate)
                _writesDuringFetch = [];
            IReadOnlyList<Row> fetched;
            try
            {
                fetched = await fetch(ct);
            }
            catch
            {
                lock (_gate)
                    _writesDuringFetch = null;
                throw;
            }
            lock (_gate)
            {
                var current = fetched;
                foreach (var (thread, change) in _writesDuringFetch!)
                    current = Changed(current, thread, change);
                _writesDuringFetch = null;
                _rows = current;
                _fetchedAt = started;
                return current;
            }
        }
        finally
        {
            _fetching.Release();
        }
    }

    /// <summary>
    /// Applies one of the client's own writes to <paramref name="threadId"/>'s row. <paramref name="change"/> gets the
    /// row (null when absent) and returns its replacement, or null to drop it.
    /// </summary>
    public void Apply(string threadId, Func<Row?, Row?> change)
    {
        lock (_gate)
        {
            if (_rows is not null)
                _rows = Changed(_rows, threadId, change);
            _writesDuringFetch?.Add((threadId, change));
        }
    }

    private IReadOnlyList<Row>? Usable(string? threadId)
    {
        lock (_gate)
        {
            if (_rows is null)
                return null;
            var age = _now() - _fetchedAt;
            if (age >= maxAge)
                return null;
            if (threadId is not null && age >= missRefreshAge && !_rows.Any(row => row.ThreadId == threadId))
                return null;
            return _rows;
        }
    }

    // Copy on write: a caller may still be reading the previous list.
    private static IReadOnlyList<Row> Changed(IReadOnlyList<Row> rows, string threadId, Func<Row?, Row?> change)
    {
        var list = rows.ToList();
        var index = list.FindIndex(row => row.ThreadId == threadId);
        var replacement = change(index < 0 ? null : list[index]);
        if (index >= 0 && replacement is null)
            list.RemoveAt(index);
        else if (index >= 0)
            list[index] = replacement!;
        else if (replacement is not null)
            list.Insert(0, replacement); // The listing is newest first, and a write creates only the newest row.
        return list;
    }
}
