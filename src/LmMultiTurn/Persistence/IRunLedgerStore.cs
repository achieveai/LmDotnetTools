namespace AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;

/// <summary>
/// A record that an input has been durably accepted for a thread, prior to any run being
/// assigned to it. Exists so that a caller polling immediately after acceptance (before the
/// agent loop drains the input into a run) can be told "queued", and so that a crash between
/// acceptance and run assignment is recoverable on restart — see
/// <see cref="IRunLedgerStore.RecordAcceptedInputAsync"/>.
/// </summary>
/// <param name="ThreadId">The thread the input was accepted for.</param>
/// <param name="InputId">The caller-supplied (or generated) input identifier.</param>
/// <param name="AcceptedAt">When the input was durably accepted.</param>
public sealed record AcceptedInputEntry(string ThreadId, string InputId, DateTimeOffset AcceptedAt);

/// <summary>
/// Companion persistence interface to <see cref="IConversationStore"/> for tracking run status
/// and pre-run input acceptance. Kept separate from <see cref="IConversationStore"/> because
/// neither concept fits that interface's message/metadata/lifecycle shape: a run does not exist
/// until an input is assigned to it, and "was this input ever accepted" is a pre-run concept
/// that predates any run existing at all.
/// </summary>
public interface IRunLedgerStore
{
    // === Run ledger ===

    /// <summary>
    /// Saves or updates a run ledger entry. Uses upsert semantics.
    /// </summary>
    /// <param name="entry">The run ledger entry to save.</param>
    /// <param name="ct">Cancellation token.</param>
    Task UpsertRunLedgerAsync(RunLedgerEntry entry, CancellationToken ct = default);

    /// <summary>
    /// Loads a single run ledger entry by its run ID.
    /// </summary>
    /// <param name="runId">The run identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The run ledger entry, or null if not found.</returns>
    Task<RunLedgerEntry?> LoadRunLedgerAsync(string runId, CancellationToken ct = default);

    /// <summary>
    /// Lists all run ledger entries for a thread, ordered by creation time descending.
    /// </summary>
    /// <param name="threadId">The thread identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>All run ledger entries for the thread, or empty list if none exist.</returns>
    Task<IReadOnlyList<RunLedgerEntry>> ListRunLedgerAsync(string threadId, CancellationToken ct = default);

    // === Accepted-input tracking (pre-run) ===

    /// <summary>
    /// Durably records that <paramref name="inputId"/> has been accepted for
    /// <paramref name="threadId"/>, before any run has been assigned to it.
    /// </summary>
    /// <param name="threadId">The thread the input was accepted for.</param>
    /// <param name="inputId">The input identifier.</param>
    /// <param name="acceptedAt">When the input was accepted.</param>
    /// <param name="ct">Cancellation token.</param>
    Task RecordAcceptedInputAsync(
        string threadId,
        string inputId,
        DateTimeOffset acceptedAt,
        CancellationToken ct = default);

    /// <summary>
    /// Records acceptance ONLY IF no entry already exists for
    /// (<paramref name="threadId"/>, <paramref name="inputId"/>), atomically with respect to concurrent
    /// callers, and reports whether this caller is the one that recorded it.
    /// <para>
    /// This is the primitive that makes an input id an admission ticket rather than a label. A caller that
    /// checks "has this been accepted?" and then enqueues has a window in which a second caller does the
    /// same and both enqueue; winning this reservation is what lets exactly one of them proceed. The winner
    /// owns the enqueue AND the compensation: if it then fails to queue the input it must call
    /// <see cref="RemoveAcceptedInputAsync"/>, or the id stays reserved for work that never ran.
    /// </para>
    /// <para>
    /// A loser reconciles against the thread's existing accepted/run state rather than being handed the
    /// entry, because by then the input may already have been drained into a run and have no
    /// accepted-input entry left at all.
    /// </para>
    /// </summary>
    /// <param name="threadId">The thread the input is being accepted for.</param>
    /// <param name="inputId">The input identifier to reserve.</param>
    /// <param name="acceptedAt">When the input was accepted.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><c>true</c> if this call recorded the acceptance; <c>false</c> if one already existed.</returns>
    Task<bool> TryReserveAcceptedInputAsync(
        string threadId,
        string inputId,
        DateTimeOffset acceptedAt,
        CancellationToken ct = default);

    /// <summary>
    /// Removes a previously-recorded accepted-input entry. Idempotent — a no-op if no such
    /// entry exists (e.g. it was already removed, or never recorded).
    /// </summary>
    /// <param name="threadId">The thread the input was accepted for.</param>
    /// <param name="inputId">The input identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    Task RemoveAcceptedInputAsync(string threadId, string inputId, CancellationToken ct = default);

    /// <summary>
    /// Lists the input IDs currently recorded as accepted (but not necessarily yet assigned to
    /// a run) for a thread.
    /// </summary>
    /// <param name="threadId">The thread identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The set of accepted input IDs for the thread, or an empty set if none exist.</returns>
    Task<IReadOnlySet<string>> ListAcceptedInputIdsAsync(string threadId, CancellationToken ct = default);
}
