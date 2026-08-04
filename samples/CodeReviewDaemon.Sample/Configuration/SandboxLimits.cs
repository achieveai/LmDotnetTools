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

    private static string Cap(string value, int max)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (max <= 0 || value.Length <= max)
        {
            return value;
        }

        // The cut lands between lines, never inside one. Command output here is line-oriented — the
        // changed-path listing is literally one record per line — and a cut that halves the last record
        // leaves a stump in front of the marker that reads exactly like a complete path. Every consumer
        // downstream then treats it as one: KnowledgeDigest.ParseChangedPaths keeps it, the ranking matches
        // against a file git never reported, and because the result is still non-empty nothing signals that
        // anything went wrong. A whole line is the smallest unit a reader of this output can trust.
        //
        // One enormous line (a minified file inside a diff) has no boundary to fall back to, so the hard cut
        // stands rather than discarding the entire payload.
        var boundary = value.LastIndexOf('\n', max - 1);
        return (boundary >= 0 ? value[..boundary] : value[..max]) + TruncationMarker;
    }
}
