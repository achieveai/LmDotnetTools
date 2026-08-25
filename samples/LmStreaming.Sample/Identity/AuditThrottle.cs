using System.Collections.Concurrent;

namespace LmStreaming.Sample.Identity;

/// <summary>
/// Suppresses repeats of the same audit key inside a short window, so a client stuck in a retry
/// loop cannot flood the log with one identical rejection.
/// </summary>
/// <remarks>
/// Deduplication, not sampling: the first occurrence of every key is always admitted, so an
/// operator still sees that someone is waiting to be onboarded. Only the repeats behind it are
/// dropped.
/// </remarks>
public sealed class AuditThrottle
{
    /// <summary>
    /// Above this many tracked keys the map is swept of expired entries before the next insert.
    /// The bound exists because the key includes an attacker-supplied tenant id, so an unbounded
    /// map would be a memory-growth surface rather than a flood defence.
    /// </summary>
    private const int SweepThreshold = 1024;

    private readonly ConcurrentDictionary<string, long> _lastAdmittedTicks = new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _window;

    /// <summary>Creates a throttle over the given clock and window.</summary>
    /// <param name="timeProvider">Clock. Injected so tests do not wait on the wall clock.</param>
    /// <param name="window">How long a key stays suppressed after being admitted.</param>
    public AuditThrottle(TimeProvider timeProvider, TimeSpan window)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        _timeProvider = timeProvider;
        _window = window;
    }

    /// <summary>
    /// Whether this occurrence of <paramref name="key"/> should be recorded. Admitting a key
    /// starts its suppression window.
    /// </summary>
    /// <param name="key">Deduplication key, e.g. the claimed tenant id plus the refusal reason.</param>
    public bool ShouldRecord(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        // A non-positive window disables suppression entirely rather than admitting nothing - an
        // operator who zeroes the knob wants every record, not silence.
        if (_window <= TimeSpan.Zero)
        {
            return true;
        }

        var nowTicks = _timeProvider.GetUtcNow().UtcTicks;
        var windowTicks = _window.Ticks;

        if (_lastAdmittedTicks.Count > SweepThreshold)
        {
            Sweep(nowTicks, windowTicks);
        }

        // Under contention ConcurrentDictionary may invoke the update factory more than once
        // before a CAS succeeds, so two racing callers can both observe admitted == true. That
        // errs toward writing an EXTRA audit record rather than dropping one, which is the only
        // direction an audit trail may be wrong in.
        var admitted = false;
        _ = _lastAdmittedTicks.AddOrUpdate(
            key,
            _ =>
            {
                admitted = true;
                return nowTicks;
            },
            (_, previousTicks) =>
            {
                if (nowTicks - previousTicks < windowTicks)
                {
                    return previousTicks;
                }

                admitted = true;
                return nowTicks;
            });

        return admitted;
    }

    private void Sweep(long nowTicks, long windowTicks)
    {
        foreach (var (key, ticks) in _lastAdmittedTicks)
        {
            if (nowTicks - ticks >= windowTicks)
            {
                _ = _lastAdmittedTicks.TryRemove(new KeyValuePair<string, long>(key, ticks));
            }
        }
    }
}
