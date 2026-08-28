using System.Collections.Concurrent;

namespace AchieveAi.LmDotnetTools.LmLifecycle;

/// <summary>
/// The in-process <see cref="ILifecycleSequenceAllocator"/>: one atomically incremented counter per
/// source stream, held for the lifetime of the producer.
/// </summary>
/// <remarks>
/// Counters are not persisted. A restart therefore restarts every counter, which is exactly why a
/// new <see cref="ProducerEpoch"/> is minted at construction — a subscriber that sees the epoch
/// change knows the ordinals restarted rather than that events were lost.
/// </remarks>
public sealed class InMemoryLifecycleSequenceAllocator : ILifecycleSequenceAllocator
{
    private readonly ConcurrentDictionary<string, Counter> _counters = new(StringComparer.Ordinal);

    /// <summary>
    /// Creates an allocator with a freshly minted producer epoch.
    /// </summary>
    public InMemoryLifecycleSequenceAllocator()
        : this(Guid.NewGuid().ToString("N")) { }

    /// <summary>
    /// Creates an allocator with a caller-supplied producer epoch.
    /// </summary>
    /// <param name="producerEpoch">
    /// The epoch to report. Supplying this is intended for tests and for hosts that derive a
    /// stable epoch from their own process identity.
    /// </param>
    /// <exception cref="ArgumentException"><paramref name="producerEpoch"/> is null or whitespace.</exception>
    public InMemoryLifecycleSequenceAllocator(string producerEpoch)
    {
        if (string.IsNullOrWhiteSpace(producerEpoch))
        {
            throw new ArgumentException("A producer epoch must be non-empty.", nameof(producerEpoch));
        }

        ProducerEpoch = producerEpoch;
    }

    /// <inheritdoc />
    public string ProducerEpoch { get; }

    /// <inheritdoc />
    public long Next(string sourceStreamId)
    {
        if (string.IsNullOrWhiteSpace(sourceStreamId))
        {
            throw new ArgumentException("A source stream id must be non-empty.", nameof(sourceStreamId));
        }

        var counter = _counters.GetOrAdd(sourceStreamId, static _ => new Counter());
        return Interlocked.Increment(ref counter.Value);
    }

    private sealed class Counter
    {
        public long Value;
    }
}
