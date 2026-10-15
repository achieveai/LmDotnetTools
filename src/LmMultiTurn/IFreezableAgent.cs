namespace AchieveAi.LmDotnetTools.LmMultiTurn;

/// <summary>
/// An agent whose run loop can be <em>frozen</em> — quiesced without being torn down — and later unfrozen.
/// </summary>
/// <remarks>
/// Freeze is NOT cancellation. <see cref="IMultiTurnAgent.StopAsync"/> cancels the loop and disposes the run;
/// a frozen loop stays alive and resumable. While frozen, the loop finishes any in-flight turn (so its tool
/// results are captured into history) and then stops advancing: no new turn starts and no delayed-result
/// continuation runs until <see cref="Unfreeze"/>. This lets an owner make a conversation provably quiescent
/// (within the time the current turn takes to settle) so the review slot it holds can be reclaimed.
/// </remarks>
public interface IFreezableAgent : IMultiTurnAgent
{
    /// <summary>
    /// Freeze the loop. The current in-flight turn (if any) runs to completion and its tool results are
    /// captured; thereafter the loop does not start a new turn or a delayed-result continuation until
    /// <see cref="Unfreeze"/>. Idempotent. Never tears the loop down.
    /// </summary>
    void Freeze();

    /// <summary>
    /// Resume a frozen loop and drain any continuation that queued while it was frozen. Idempotent; a no-op
    /// on a loop that is not frozen.
    /// </summary>
    void Unfreeze();

    /// <summary>Whether the loop is currently frozen.</summary>
    bool IsFrozen { get; }
}
