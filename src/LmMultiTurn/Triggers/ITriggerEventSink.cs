namespace AchieveAi.LmDotnetTools.LmMultiTurn.Triggers;

/// <summary>
/// Runtime-owned callback a <see cref="ITriggerSource"/> invokes when its event fires. The sink
/// handed to <see cref="ITriggerSource.ArmAsync"/> is already bound to the specific wait, so the
/// source does not echo the wait id — it simply reports "my event happened".
/// </summary>
public interface ITriggerEventSink
{
    /// <summary>Report that the source's observed event occurred.</summary>
    ValueTask FireAsync(TriggerFireEvent fire, CancellationToken cancellationToken);
}
