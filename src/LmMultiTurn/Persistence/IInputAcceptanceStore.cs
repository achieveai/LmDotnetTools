namespace AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;

/// <summary>
/// How far an <see cref="InputAcceptance"/> has got, and therefore how much of its
/// <see cref="InputAcceptance.SpawningSuppressed"/> value is proven rather than merely undertaken.
/// </summary>
public enum InputAcceptanceState
{
    /// <summary>
    /// Reserved and being handed to the agent. <see cref="InputAcceptance.SpawningSuppressed"/> is the
    /// guarantee the host UNDERTOOK when it admitted the input — already checked against the agent's
    /// declared capability, but not yet acknowledged for this specific input.
    /// </summary>
    Pending,

    /// <summary>
    /// The agent acknowledged the input and confirmed the undertaking.
    /// <see cref="InputAcceptance.SpawningSuppressed"/> is proven.
    /// </summary>
    Enforced,

    /// <summary>
    /// The agent acknowledged the input but did NOT confirm the undertaking, so
    /// <see cref="InputAcceptance.SpawningSuppressed"/> was forced to <c>false</c>. Terminal: a record in
    /// this state must never be upgraded, which is what stops a later retry of the same key from being
    /// told a guarantee held.
    /// </summary>
    Unenforced,
}

/// <summary>
/// The authoritative, durable outcome of admitting one input onto a thread.
/// <para>
/// Deliberately NOT the same fact as <see cref="AcceptedInputEntry"/>: an accepted-input entry is a
/// pre-run marker that the agent loop DELETES the moment it folds the input into a run, so nothing
/// derived from it can survive a drain. This record is never deleted by the drain, so a caller retrying
/// after its first response was lost still gets the same answer the first response gave — including for
/// a turn that has since started, finished, or failed.
/// </para>
/// <para>
/// It is also what makes the answer a stored FACT rather than a re-derivation. Recomputing an outcome
/// from the request (or from the shape of the input id) on a retry can only ever restate what was ASKED
/// for; only a record can state what was actually granted.
/// </para>
/// </summary>
/// <param name="ThreadId">The thread the input was admitted onto.</param>
/// <param name="InputId">The durable input identifier the admission is keyed by.</param>
/// <param name="AcceptedAt">When the admission was taken (from the admitting host's clock).</param>
/// <param name="State">How far the admission has got; see <see cref="InputAcceptanceState"/>.</param>
/// <param name="SpawningSuppressed">
/// Whether sub-agent spawning is suppressed for this input's turn — undertaken while
/// <see cref="InputAcceptanceState.Pending"/>, proven once <see cref="InputAcceptanceState.Enforced"/>,
/// and permanently <c>false</c> once <see cref="InputAcceptanceState.Unenforced"/>.
/// </param>
/// <param name="IdempotencyHonored">
/// Whether this admission is acting as a dedupe ticket — i.e. whether a repeat of the same key is
/// promised to reconcile here instead of queueing a second turn.
/// </param>
/// <param name="ReservationId">
/// Token minted by the caller that took the reservation. Every mutation is conditioned on it, so a
/// request can only ever retract or complete ITS OWN admission — a compensating release that arrives
/// late (after the id was released and re-reserved by someone else) is rejected rather than deleting
/// the new owner's record.
/// </param>
public sealed record InputAcceptance(
    string ThreadId,
    string InputId,
    DateTimeOffset AcceptedAt,
    InputAcceptanceState State,
    bool SpawningSuppressed,
    bool IdempotencyHonored,
    Guid ReservationId
);

/// <summary>
/// Opt-in capability for stores that can admit an input EXACTLY ONCE and remember the outcome durably.
/// <para>
/// Deliberately separate from <see cref="IRunLedgerStore"/> rather than an addition to it. Adding an
/// abstract member to that published interface would break every store outside this repository that
/// implements it, and — worse — would make every existing store silently advertise a guarantee it has no
/// implementation for. A store opts in by implementing this interface, so "can this host honor an
/// idempotency key?" is answered by the presence of a real implementation and nothing else.
/// </para>
/// <para>
/// Implementations MUST make <see cref="TryReserveAcceptanceAsync"/> atomic against every other writer
/// that can reach the same storage — that is the entire point of the interface, and an implementation
/// that can only serialize callers inside one process must NOT implement it.
/// </para>
/// </summary>
public interface IInputAcceptanceStore
{
    /// <summary>
    /// Admits <paramref name="acceptance"/> if, and only if, its (thread, input) pair has not been
    /// admitted before — atomically with respect to every concurrent caller.
    /// <para>
    /// The winner owns the work AND the compensation: if it then fails to queue the input it must call
    /// <see cref="TryReleaseAcceptanceAsync"/>, or the id stays admitted for work that never ran.
    /// </para>
    /// </summary>
    /// <param name="acceptance">The admission to take. Its reservation token guards later mutations.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// <c>null</c> when this caller took the admission; otherwise the record that already existed, which
    /// is the authoritative answer to give instead of queueing.
    /// </returns>
    Task<InputAcceptance?> TryReserveAcceptanceAsync(InputAcceptance acceptance, CancellationToken ct = default);

    /// <summary>
    /// Reads the admission record for a (thread, input) pair, or null if the input was never admitted.
    /// </summary>
    /// <param name="threadId">The thread identifier.</param>
    /// <param name="inputId">The input identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<InputAcceptance?> GetAcceptanceAsync(string threadId, string inputId, CancellationToken ct = default);

    /// <summary>
    /// Replaces the stored record with <paramref name="acceptance"/> — the resolved outcome — but only
    /// when the stored record carries the same <see cref="InputAcceptance.ReservationId"/>.
    /// </summary>
    /// <param name="acceptance">The resolved record, carrying the reservation token it was taken under.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><c>true</c> when the outcome was applied; <c>false</c> when the record is gone or is owned by another reservation.</returns>
    Task<bool> TryRecordOutcomeAsync(InputAcceptance acceptance, CancellationToken ct = default);

    /// <summary>
    /// Retracts an admission whose work never became queued — but only when the stored record carries
    /// <paramref name="reservationId"/>, so a request can never retract an admission it does not own.
    /// </summary>
    /// <param name="threadId">The thread identifier.</param>
    /// <param name="inputId">The input identifier.</param>
    /// <param name="reservationId">The token the caller reserved under.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><c>true</c> when this caller's record was removed; <c>false</c> when there was nothing of its own to remove.</returns>
    Task<bool> TryReleaseAcceptanceAsync(
        string threadId,
        string inputId,
        Guid reservationId,
        CancellationToken ct = default
    );
}
