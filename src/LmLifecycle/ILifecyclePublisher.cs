namespace AchieveAi.LmDotnetTools.LmLifecycle;

/// <summary>
/// The seam a producer publishes lifecycle events through.
/// </summary>
/// <remarks>
/// <para>
/// Publishing is <b>best-effort and must not fail the caller</b>. An implementation drops rather
/// than blocks when its buffers are full, and never lets a subscriber's slowness or failure
/// propagate into the agent loop that produced the event. A dropped event is observable as a gap in
/// <see cref="LifecycleEventEnvelope.SourceSequence"/>, which is the intended way for a consumer to
/// notice loss. This is not an audit log: there is no durable outbox, no replay, and no backfill.
/// </para>
/// <para>
/// Implementations must tolerate concurrent publication and must not re-enter the lifecycle
/// pipeline from their own failure diagnostics.
/// </para>
/// </remarks>
public interface ILifecyclePublisher
{
    /// <summary>
    /// Publishes one event to whatever subscribers are entitled to it.
    /// </summary>
    /// <param name="envelope">The event to publish. Its identity is already assigned.</param>
    /// <param name="cancellationToken">Cancels the enqueue, not the delivery.</param>
    /// <returns>A task that completes once the event has been accepted or deliberately dropped.</returns>
    /// <remarks>
    /// Completion means the producer is free to continue — never that any subscriber received the
    /// event. Delivery, retries, and drops happen behind this call.
    /// </remarks>
    ValueTask PublishAsync(LifecycleEventEnvelope envelope, CancellationToken cancellationToken = default);
}
