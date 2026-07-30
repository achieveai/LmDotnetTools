namespace AchieveAi.LmDotnetTools.LmLifecycle;

/// <summary>
/// Allocates the monotonic per-stream ordinals that give a lifecycle stream its canonical order.
/// </summary>
/// <remarks>
/// <para>
/// Each source stream (see <see cref="LifecycleSourceStream"/>) has its own counter, so a gap in
/// one stream is meaningful on its own and unrelated traffic in another stream cannot disturb it.
/// </para>
/// <para>
/// A counter lives only as long as its producer. <see cref="ProducerEpoch"/> changes whenever the
/// producer restarts, which is what lets a subscriber tell a genuine gap apart from a counter that
/// simply started over.
/// </para>
/// </remarks>
public interface ILifecycleSequenceAllocator
{
    /// <summary>
    /// Identifies this producer incarnation. Stable for the life of the producer and different
    /// after any restart.
    /// </summary>
    string ProducerEpoch { get; }

    /// <summary>
    /// Allocates the next ordinal for <paramref name="sourceStreamId"/>.
    /// </summary>
    /// <param name="sourceStreamId">The source stream to allocate within.</param>
    /// <returns>
    /// A strictly increasing value, starting at <c>1</c> for the first event of a stream within
    /// this producer epoch.
    /// </returns>
    /// <remarks>Implementations must be safe for concurrent use.</remarks>
    long Next(string sourceStreamId);
}
