namespace AchieveAi.LmDotnetTools.Sandbox;

/// <summary>
/// The verified outcome of a <see cref="SandboxClient.ExecuteAsync"/> call: the command's exit code
/// and its exact captured output, reassembled from the sandbox beyond the gateway's
/// 20&#160;KB/500-line <c>exec</c> truncation and integrity-checked before being handed back.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="StandardOutput"/> and <see cref="StandardError"/> are the exact bytes each stream
/// produced (decoded as UTF-8), captured to separate files by the SDK's command wrapper.
/// <see cref="CombinedOutput"/> is their concatenation (stdout then stderr) — a convenience view, not
/// a true interleaving: V1 does not order the two streams against each other in real time (native
/// stderr separation/interleaving is out of scope).
/// </para>
/// <para>
/// <see cref="OperationId"/> is the resolved operation id (the caller's, or the one the SDK
/// generated). Passing it back on a later <see cref="SandboxClient.ExecuteAsync"/> call recovers
/// this same result without re-running the command.
/// </para>
/// <para>
/// <b>Delivery is at-least-once.</b> The SDK submits the command exactly once, but the gateway may
/// rematerialize a lost container and retry the underlying invocation once, so a non-idempotent
/// command can run more than once even though this result is produced only once.
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

    /// <summary>The resolved operation id, usable to recover this result on a later call.</summary>
    public required string OperationId { get; init; }

    /// <summary>
    /// <see cref="StandardOutput"/> followed by <see cref="StandardError"/> — a convenience
    /// concatenation, not a real-time interleaving of the two streams.
    /// </summary>
    public string CombinedOutput => StandardOutput + StandardError;
}
