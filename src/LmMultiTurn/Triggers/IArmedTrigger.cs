namespace AchieveAi.LmDotnetTools.LmMultiTurn.Triggers;

/// <summary>
/// Handle to a single armed trigger, returned by <see cref="ITriggerSource.ArmAsync"/>. It is a
/// pure resource handle: disposing it stops the underlying watcher/timer and guarantees no
/// further <see cref="ITriggerEventSink.FireAsync"/> callback occurs. All lifecycle policy
/// (single-resolution latch, ceiling timeout, cancellation semantics, delivery) lives in the
/// runtime, not here.
/// </summary>
public interface IArmedTrigger : IAsyncDisposable
{
    /// <summary>The wait this handle belongs to.</summary>
    string WaitId { get; }
}
