namespace AchieveAi.LmDotnetTools.LmAgentInfra.Agents;

/// <summary>
/// Read-only view of whether a conversation thread currently has a run in flight.
/// </summary>
/// <remarks>
/// This exists purely as a seam. The workspace plugin-selection migration must not tear a sandbox
/// session down underneath a running turn, so it needs exactly one fact — "is this thread busy?" —
/// from <see cref="MultiTurnAgentPool"/>. The pool is sealed with no virtual members, so a test that
/// wants to hold a migration open against a busy thread cannot substitute one. Depending on this
/// interface instead makes that scenario expressible without weakening the pool's own design.
/// <para>
/// The pool implements it with no added behaviour: this is a narrower view of an existing capability,
/// not a new one.
/// </para>
/// </remarks>
public interface IAgentRunActivityProbe
{
    /// <summary>
    /// Returns <c>true</c> while <paramref name="threadId"/> has a run in progress.
    /// </summary>
    /// <remarks>
    /// A thread the implementation has never seen is idle, not an error — nothing can be running on a
    /// thread that was never started, and callers use this to decide whether to WAIT, so reporting an
    /// unknown thread as busy would stall them forever.
    /// </remarks>
    bool IsRunInProgress(string threadId);
}
