using AchieveAi.LmDotnetTools.Sandbox.Command;

namespace AchieveAi.LmDotnetTools.Sandbox;

/// <summary>
/// An immutable, constructor-validated description of one non-interactive command to run in a
/// gateway sandbox via the direct operations API (ADR 0031 / issue #119). The ordered
/// <see cref="Arguments"/> are a native argv vector — program name first, arguments passed verbatim
/// with NO shell involved. The program name is resolved on the sandbox <c>PATH</c> when it is a bare
/// name (a value containing a path separator is instead validated against the sandbox mounts), so a
/// shell is invoked, when wanted, explicitly as <c>["sh", "-c", "…"]</c>.
/// </summary>
/// <remarks>
/// <para>
/// Everything is validated at construction so a malformed request fails fast, before any gateway
/// call: <see cref="Arguments"/> must be a non-empty ordered vector whose elements contain no NUL
/// byte; <see cref="WorkingDirectory"/>, when supplied, must be a workspace-relative POSIX path
/// (rooted, drive/UNC/device-qualified, backslash-bearing, or <c>..</c>-escaping values are rejected
/// — see <see cref="WorkspaceRelativePath"/>); and <see cref="OperationId"/>, when supplied, is
/// length-bounded and control-character-free.
/// </para>
/// <para>
/// <see cref="OperationId"/> is optional; when it is <c>null</c> the SDK generates a
/// collision-resistant id at execution time and surfaces it on the result so the caller can recover
/// the same operation after an ambiguous transport failure. It is the gateway's idempotency key for
/// the operations API: resubmitting the same id with an identical request replays the existing
/// operation rather than running it again.
/// </para>
/// </remarks>
public sealed record SandboxCommand
{
    /// <summary>
    /// The ordered argument vector, program name first. Non-empty; no element contains a NUL byte.
    /// Empty-string elements are allowed and survive as distinct arguments.
    /// </summary>
    public IReadOnlyList<string> Arguments { get; }

    /// <summary>
    /// Optional working directory, relative to the sandbox workspace root, that the command runs in.
    /// <c>null</c> (the default) runs in the workspace root. The value is validated as a
    /// workspace-relative POSIX path at construction; the gateway remains authoritative for symlink
    /// containment.
    /// </summary>
    public string? WorkingDirectory { get; }

    /// <summary>
    /// Optional caller-chosen operation id used to recover a result after an ambiguous transport
    /// failure. <c>null</c> (the default) lets the SDK generate one. Length-bounded and
    /// control-character-free when supplied. It is the gateway's idempotency key: resubmitting the
    /// same id with an identical request replays the existing operation instead of running it again.
    /// Idempotency is process-local on the gateway (a gateway restart drops the record; see
    /// <see cref="SandboxClient.ExecuteAsync"/>), so recovery is not promised across a restart.
    /// </summary>
    public string? OperationId { get; }

    /// <summary>
    /// The <see cref="WorkingDirectory"/> normalized to a clean, forward-slash, workspace-relative
    /// path (empty string = workspace root). Internal: production code uses this normalized form as
    /// the operation's <c>cwd</c> path, while the public <see cref="WorkingDirectory"/> preserves what
    /// the caller passed.
    /// </summary>
    internal string NormalizedWorkingDirectory { get; }

    public SandboxCommand(IReadOnlyList<string> arguments, string? workingDirectory = null, string? operationId = null)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Count == 0)
        {
            throw new ArgumentException(
                "A command must have at least one argument (the program name).",
                nameof(arguments)
            );
        }

        var copy = new string[arguments.Count];
        for (var i = 0; i < arguments.Count; i++)
        {
            var token =
                arguments[i]
                ?? throw new ArgumentException($"Command argument at index {i} is null.", nameof(arguments));
            if (token.Contains('\0', StringComparison.Ordinal))
            {
                throw new ArgumentException($"Command argument at index {i} contains a NUL byte.", nameof(arguments));
            }

            copy[i] = token;
        }

        Arguments = Array.AsReadOnly(copy);
        NormalizedWorkingDirectory = WorkspaceRelativePath.Normalize(workingDirectory, nameof(workingDirectory));
        WorkingDirectory = workingDirectory;

        if (operationId is not null)
        {
            CommandOperation.ValidateOperationId(operationId, nameof(operationId));
        }

        OperationId = operationId;
    }
}
