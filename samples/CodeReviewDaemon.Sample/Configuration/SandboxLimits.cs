namespace CodeReviewDaemon.Sample.Configuration;

/// <summary>
/// Bounds on sandbox command output and persisted artifacts (PR #121 H4). The daemon runs commands in a
/// sandbox over untrusted PR code, so a command can emit unbounded stdout/stderr and a diff can be huge;
/// without caps the gateway response is fully materialized, persisted to SQLite, and fed to the agent.
/// These limits cap the captured output (with an explicit truncation marker so a reader knows it was
/// trimmed), cap the persisted artifact payload, and bound each command with a timeout. Every value has
/// a conservative default and a consumer; nothing is speculative.
/// </summary>
internal sealed class SandboxLimits
{
    /// <summary>Marker appended to any output/payload that was truncated, so a reader knows it was trimmed.</summary>
    public const string TruncationMarker = "\n…[truncated by CodeReviewDaemon: output exceeded the configured limit]…";

    /// <summary>Per-command timeout. A command exceeding it is cancelled (default 5 minutes).</summary>
    public TimeSpan CommandTimeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Maximum captured stdout/stderr characters per command before truncation (default 1 MiB).</summary>
    public int MaxOutputChars { get; init; } = 1 * 1024 * 1024;

    /// <summary>Maximum persisted artifact payload characters (e.g. a diff) before truncation (default 2 MiB).</summary>
    public int MaxArtifactPayloadChars { get; init; } = 2 * 1024 * 1024;

    /// <summary>Truncates command output to <see cref="MaxOutputChars"/>, appending the marker when trimmed.</summary>
    public string CapOutput(string value) => Cap(value, MaxOutputChars);

    /// <summary>Truncates an artifact payload to <see cref="MaxArtifactPayloadChars"/>, appending the marker when trimmed.</summary>
    public string CapArtifactPayload(string value) => Cap(value, MaxArtifactPayloadChars);

    /// <summary>
    /// Truncates a RECORD-ORIENTED payload — one meaningful record per line, such as a
    /// <c>git diff --name-only</c> listing — to <see cref="MaxArtifactPayloadChars"/>, cutting between
    /// records rather than inside one.
    /// <para>
    /// Separate from <see cref="CapArtifactPayload"/> because the guarantee is only worth its cost where
    /// records exist. Cutting a listing mid-record leaves a stump in front of the marker that reads exactly
    /// like a complete path, and every consumer downstream treats it as one: the ranking matches against a
    /// file git never reported, and because the result is still non-empty nothing signals that anything went
    /// wrong. Applying the same rule to arbitrary output would instead throw the budget away — see the note
    /// on <see cref="Cap"/>.
    /// </para>
    /// The surviving records keep their own terminating newline. That is load-bearing, not tidiness: the
    /// marker opens with <c>\n</c>, so a clean cut leaves an EMPTY line in front of the marker while a
    /// character-exact cut leaves the stump there. A parser reading capped output has no other way to tell
    /// which kind of cut it is looking at.
    /// </summary>
    public string CapRecordListing(string value) => CapOnRecordBoundary(value, MaxArtifactPayloadChars);

    private static string Cap(string value, int max)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (max <= 0 || value.Length <= max)
        {
            return value;
        }

        // Character-exact, deliberately. This cap sees ALL captured output and every persisted payload —
        // build logs, test output, a diff whose file is minified — and most of it is not record-oriented.
        // Backing up to the last newline below the limit costs whatever lies between it and the cut, which
        // on a payload shaped "STATUS\n" + one enormous line is essentially the entire configured budget,
        // surrendered to protect records that output never had. Where records DO exist the caller asks for
        // them by name via <see cref="CapRecordListing"/>.
        return value[..max] + TruncationMarker;
    }

    private static string CapOnRecordBoundary(string value, int max)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (max <= 0 || value.Length <= max)
        {
            return value;
        }

        // One enormous line has no boundary to fall back to, so the hard cut stands rather than discarding
        // the entire payload. It leaves a stump, and the absent blank line in front of the marker is what
        // tells the parser so.
        var boundary = value.LastIndexOf('\n', max - 1);
        return (boundary >= 0 ? value[..(boundary + 1)] : value[..max]) + TruncationMarker;
    }
}
