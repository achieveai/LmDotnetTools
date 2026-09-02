namespace AchieveAi.LmDotnetTools.Sandbox;

/// <summary>
/// The outcome of a <see cref="SandboxClient.ExecuteAsync"/> call: the command's exit code and its
/// exact captured output, downloaded byte-for-byte from the operation's stdout/stderr artifacts once
/// the operation reaches a terminal state. Output is never truncated — the gateway terminalizes an
/// operation that would exceed its output cap rather than silently cutting it.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="StandardOutput"/> and <see cref="StandardError"/> are the exact bytes each stream
/// produced (decoded as strict UTF-8), captured to separate artifact files by the gateway and fetched
/// through its direct file API. <see cref="CombinedOutput"/> is their concatenation (stdout then
/// stderr) — a convenience view, not a true interleaving: the two streams are not ordered against each
/// other in real time (native stderr interleaving is out of scope).
/// </para>
/// <para>
/// <see cref="OperationId"/> is the resolved operation id (the caller's, or the one the SDK
/// generated). Passing it back on a later <see cref="SandboxClient.ExecuteAsync"/> call replays the
/// existing operation without re-running the command — the gateway's idempotency key. Idempotency is
/// process-local on the gateway: a gateway restart drops the record, after which reusing the id is
/// treated as a new operation, so recovery is not promised across a restart.
/// </para>
/// </remarks>
public sealed record SandboxCommandResult
{
    /// <summary>The command's process exit code.</summary>
    public required int ExitCode { get; init; }

    /// <summary>The exact standard-output bytes the command produced, decoded as UTF-8.</summary>
    public required string StandardOutput { get; init; }

    /// <summary>The exact standard-error bytes the command produced, decoded as UTF-8.</summary>
    public required string StandardError { get; init; }

    /// <summary>The resolved operation id, usable to replay this operation on a later call (the gateway's idempotency key).</summary>
    public required string OperationId { get; init; }

    /// <summary>
    /// Whether the gateway's operation RECORD for this command was released — its
    /// <c>DELETE .../operations/{operation_id}</c> either succeeded or reported the record already gone
    /// (<c>404</c>: nothing left to reclaim). <see cref="SandboxClient.ExecuteAsync"/> issues that delete
    /// for an operation id IT MINTED, once BOTH artifacts have been downloaded, because the same delete
    /// also removes the on-disk artifact directory.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is always <c>false</c> when the caller supplied <see cref="SandboxCommand.OperationId"/>: an
    /// explicit id is the caller's statement that it owns the record's lifecycle (replay, artifact reads,
    /// and the eventual <see cref="SandboxClient.DeleteOperationAsync"/>), so the SDK never reclaims it.
    /// The flag is a cap-pressure signal only for the ids the SDK mints — which is every command that
    /// leaves <see cref="SandboxCommand.OperationId"/> null.
    /// </para>
    /// <para>
    /// For those, <c>false</c> means the gateway still holds this record (and its stdout/stderr files)
    /// until the terminal TTL prunes the record. It is never an error — the command itself succeeded and
    /// its output above is complete — but it IS the early warning a long-lived session wants: a retained
    /// record keeps its slot in the gateway's per-session record cap, and a session that retains one per
    /// command is eventually refused with <c>503 operation_capacity_exhausted</c> (issue #725). A caller
    /// running many commands on one session should report a persistent <c>false</c> ONCE per session —
    /// logging it per call buries the signal under one line per command, which is how that failure
    /// originally presented.
    /// </para>
    /// </remarks>
    public bool OperationRecordReleased { get; init; }

    /// <summary>
    /// <see cref="StandardOutput"/> followed by <see cref="StandardError"/> — a convenience
    /// concatenation, not a real-time interleaving of the two streams.
    /// </summary>
    public string CombinedOutput => StandardOutput + StandardError;
}
