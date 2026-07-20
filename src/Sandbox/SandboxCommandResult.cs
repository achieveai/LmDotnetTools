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
    /// <see cref="StandardOutput"/> followed by <see cref="StandardError"/> — a convenience
    /// concatenation, not a real-time interleaving of the two streams.
    /// </summary>
    public string CombinedOutput => StandardOutput + StandardError;
}
