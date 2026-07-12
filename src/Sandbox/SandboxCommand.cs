using AchieveAi.LmDotnetTools.Sandbox.Command;

namespace AchieveAi.LmDotnetTools.Sandbox;

/// <summary>
/// An immutable, constructor-validated description of one non-interactive command to run in a
/// gateway Bash/POSIX-capable sandbox. V1 targets POSIX shells only: the ordered
/// <see cref="Arguments"/> are POSIX-quoted into a single <c>/bin/sh -c</c> command string by the
/// SDK — the API deliberately does not claim native cross-platform argv semantics.
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
/// the same operation after an ambiguous transport failure. It is never used as a filesystem path
/// directly — it is hashed into a fixed-length artifact directory name.
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
    /// control-character-free when supplied; never used directly as a filesystem path.
    /// </summary>
    public string? OperationId { get; }

    /// <summary>
    /// The <see cref="WorkingDirectory"/> normalized to a clean, forward-slash, workspace-relative
    /// path (empty string = workspace root). Internal: production code uses this normalized form for
    /// the canonical digest and the Bash wrapper, while the public <see cref="WorkingDirectory"/>
    /// preserves what the caller passed.
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
