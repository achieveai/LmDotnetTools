using System.Collections.Concurrent;

namespace AchieveAi.LmDotnetTools.LmAgentInfra.Webhooks;

/// <summary>
/// The stateful half of the webhook replay defence (ADR 0005): a bounded, TTL-expiring set of delivery
/// ids the receiver has already accepted. <see cref="TryRegister"/> records a previously-unseen id and
/// returns <c>true</c>; a duplicate (within the TTL) returns <c>false</c> so the middleware can reject
/// the replay. Thread-safe — webhook callbacks arrive concurrently, so the cache must not assume serial
/// access.
/// <para>
/// Bounded both by time (the <c>±timestamp tolerance</c> window past which a replay is already rejected
/// as stale) and by entry count (a hard cap evicts the oldest), so a flood of unique ids cannot grow it
/// without limit.
/// </para>
/// <para>
/// Expiry and cap enforcement are <em>amortized</em>: a full sweep runs at most once per TTL, or once
/// per <c>maxEntries/8</c> registrations, never on every call. Correctness does not depend on the
/// sweep — <see cref="TryRegister"/> compares each entry's own age, so an id whose TTL has lapsed is
/// treated as fresh whether or not the sweep has collected it yet. The sweep only reclaims memory.
/// </para>
/// </summary>
public sealed class DeliveryReplayCache
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _seen = new(StringComparer.Ordinal);
    private readonly TimeSpan _ttl;
    private readonly int _maxEntries;

    /// <summary>Registrations between forced sweeps, and the headroom a sweep leaves below the cap.</summary>
    private readonly int _sweepInterval;

    private int _sinceLastSweep;
    private long _nextSweepAtUtcTicks;

    /// <summary>
    /// Creates a replay cache. Both bounds must be positive: a non-positive TTL would expire every
    /// entry instantly (accepting all replays) and a non-positive cap has no meaning.
    /// </summary>
    /// <param name="ttl">How long an accepted delivery id is remembered.</param>
    /// <param name="maxEntries">Hard cap on retained ids; the oldest are evicted past it.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="ttl"/> or <paramref name="maxEntries"/> is not positive.</exception>
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
        _sweepInterval = Math.Max(1, maxEntries / 8);
    }

    /// <summary>
    /// Records <paramref name="deliveryId"/> as seen at <paramref name="nowUtc"/>. Returns <c>true</c>
    /// when it was not already present within the TTL (accept), <c>false</c> when it is a replay (reject).
    /// </summary>
    /// <param name="deliveryId">The authenticated delivery id from the callback's headers.</param>
    /// <param name="nowUtc">The receiver's current time.</param>
    /// <returns><c>true</c> for a fresh delivery; <c>false</c> for a replay inside the TTL.</returns>
    /// <exception cref="ArgumentException"><paramref name="deliveryId"/> is null, empty, or whitespace.</exception>
    public bool TryRegister(string deliveryId, DateTimeOffset nowUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deliveryId);

        SweepIfDue(nowUtc);

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

    /// <summary>
    /// Decides whether this registration pays for a sweep. The scan is O(n), so charging it to every
    /// call made the cost of a request proportional to the cache's size; gating it on a registration
    /// count and a time deadline makes it amortized O(1) per registration instead.
    /// </summary>
    private void SweepIfDue(DateTimeOffset nowUtc)
    {
        if (Interlocked.Increment(ref _sinceLastSweep) < _sweepInterval
            && nowUtc.UtcTicks < Interlocked.Read(ref _nextSweepAtUtcTicks))
        {
            return;
        }

        // Claim the sweep: whichever caller zeroes the counter runs the scan, so a concurrent burst
        // costs one scan rather than one per request.
        if (Interlocked.Exchange(ref _sinceLastSweep, 0) == 0)
        {
            return;
        }

        Interlocked.Exchange(ref _nextSweepAtUtcTicks, (nowUtc + _ttl).UtcTicks);
        Sweep(nowUtc);
    }

    private void Sweep(DateTimeOffset nowUtc)
    {
        var liveCount = 0;
        foreach (var entry in _seen)
        {
            if (nowUtc - entry.Value > _ttl)
            {
                _ = _seen.TryRemove(entry.Key, out _);
            }
            else
            {
                liveCount++;
            }
        }

        if (liveCount <= _maxEntries)
        {
            return;
        }

        // Hard cap: TTL pruning did not bring the cache under budget, so evict the oldest survivors —
        // down to a low-water mark, not merely to the cap, so the very next registration does not force
        // another scan. Ordering by sighting time is what makes "oldest" well-defined here.
        var target = Math.Max(0, _maxEntries - _sweepInterval);
        foreach (var stale in _seen.OrderBy(e => e.Value).Take(liveCount - target))
        {
            _ = _seen.TryRemove(stale.Key, out _);
        }
    }
}
