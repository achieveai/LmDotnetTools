namespace AchieveAi.LmDotnetTools.LmMultiTurn.Triggers;

/// <summary>
/// The one extensibility seam for the Wait/trigger primitive. A source knows how to observe one
/// kind of event (a timer elapsing, a file line matching, a process exiting, …) and report it to
/// a runtime-owned sink. Adding a new wake kind is "implement this interface + register it" — no
/// change to the agent loop or the tool surface.
/// </summary>
/// <remarks>
/// Implementations MUST be safe to reuse across many concurrent arms (each <see cref="ArmAsync"/>
/// call returns an independent <see cref="IArmedTrigger"/> handle and must hold no per-wait state
/// on the source itself). A source must never resolve tool calls, enforce timeouts, or count
/// fires — those are runtime concerns.
/// </remarks>
public interface ITriggerSource
{
    /// <summary>
    /// Arm one wait. Parse <see cref="TriggerArmRequest.ArgsJson"/>, begin observing, and invoke
    /// <paramref name="eventSink"/> when the event fires. Return a handle whose disposal stops the
    /// observation.
    /// </summary>
    ValueTask<IArmedTrigger> ArmAsync(
        TriggerArmRequest request,
        ITriggerEventSink eventSink,
        CancellationToken cancellationToken);
}
