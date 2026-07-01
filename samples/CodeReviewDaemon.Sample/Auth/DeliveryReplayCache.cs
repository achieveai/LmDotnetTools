using System.Collections.Concurrent;

namespace CodeReviewDaemon.Sample.Auth;

/// <summary>
/// The stateful half of the webhook replay defence (plan §9): a bounded, TTL-expiring set of delivery
/// ids the daemon has already accepted. <see cref="TryRegister"/> records a previously-unseen id and
/// returns <c>true</c>; a duplicate (within the TTL) returns <c>false</c> so the middleware can reject
/// the replay. Thread-safe — a single poller drives the daemon today, but the gateway may issue webhook
/// callbacks concurrently, so the cache must not assume serial access.
/// <para>
/// Bounded both by time (the <c>±timestamp tolerance</c> window past which a replay is already rejected
/// as stale) and by entry count (a hard cap evicts the oldest), so a flood of unique ids cannot grow it
/// without limit.
/// </para>
/// </summary>
internal sealed class DeliveryReplayCache
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _seen = new(StringComparer.Ordinal);
    private readonly TimeSpan _ttl;
    private readonly int _maxEntries;

    public DeliveryReplayCache(TimeSpan ttl, int maxEntries = 10_000)
    {
        if (ttl <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(ttl), ttl, "TTL must be positive.");
        }

        if (maxEntries <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxEntries), maxEntries, "Max entries must be positive.");
        }

        _ttl = ttl;
        _maxEntries = maxEntries;
    }

    /// <summary>
    /// Records <paramref name="deliveryId"/> as seen at <paramref name="nowUtc"/>. Returns <c>true</c>
    /// when it was not already present within the TTL (accept), <c>false</c> when it is a replay (reject).
    /// </summary>
    public bool TryRegister(string deliveryId, DateTimeOffset nowUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deliveryId);

        Prune(nowUtc);

        // A genuinely fresh id is added; an existing-but-expired entry is treated as fresh and refreshed.
        var isNew = true;
        _ = _seen.AddOrUpdate(
            deliveryId,
            _ => nowUtc,
            (_, existing) =>
            {
                if (nowUtc - existing <= _ttl)
                {
                    isNew = false;
                    return existing;
                }

                return nowUtc;
            });

        return isNew;
    }

    private void Prune(DateTimeOffset nowUtc)
    {
        foreach (var entry in _seen)
        {
            if (nowUtc - entry.Value > _ttl)
            {
                _ = _seen.TryRemove(entry.Key, out _);
            }
        }

        // Hard cap: if still over budget after TTL pruning, evict the oldest entries.
        if (_seen.Count > _maxEntries)
        {
            foreach (var stale in _seen.OrderBy(e => e.Value).Take(_seen.Count - _maxEntries))
            {
                _ = _seen.TryRemove(stale.Key, out _);
            }
        }
    }
}
