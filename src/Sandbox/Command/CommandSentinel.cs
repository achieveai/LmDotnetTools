namespace AchieveAi.LmDotnetTools.Sandbox.Command;

/// <summary>
/// The tiny line-oriented protocol a command wrapper prints on its (small, never-truncated) standard
/// output, and the SDK parses out of the gateway Bash result text. Keeping the signal on a single
/// marker-prefixed line — rather than relying on the wrapper's raw stdout — is what lets the SDK stay
/// correct even though the gateway's <c>exec</c> combines stdout/stderr and truncates at 20&#160;KB /
/// 500 lines: the wrapper writes the real command output to files and emits only this compact line.
/// </summary>
internal static class CommandSentinel
{
    /// <summary>Prefix of the single status line a RUN/PROBE wrapper emits.</summary>
    public const string Marker = "@@LMSBX-SENTINEL@@";

    /// <summary>Prefix of each line a stale-cleanup listing emits (one per candidate artifact directory).</summary>
    public const string GcMarker = "@@LMSBX-GC@@";

    /// <summary>The operation completed: payload is base64 of the persisted manifest JSON.</summary>
    public const string KindManifest = "MANIFEST";

    /// <summary>Another submitter holds the claim and has not yet committed a manifest — the caller should poll.</summary>
    public const string KindPending = "PENDING";

    /// <summary>No artifact exists yet for the operation.</summary>
    public const string KindNone = "NONE";

    public static string Manifest(string base64Manifest) => $"{Marker} {KindManifest} {base64Manifest}";

    public static string Pending() => $"{Marker} {KindPending}";

    public static string None() => $"{Marker} {KindNone}";

    public static string GcLine(string directoryName, long leaseUnixSeconds, long createdUnixSeconds) =>
        $"{GcMarker} {directoryName} {leaseUnixSeconds} {createdUnixSeconds}";

    /// <summary>
    /// Extracts the single status line from a Bash result body, returning its kind and optional payload.
    /// Throws <see cref="SandboxException"/> (<see cref="SandboxErrorKind.Protocol"/>) when no marker
    /// line is present — a wrapper that ran always emits exactly one, so its absence is a protocol
    /// violation rather than a recoverable state.
    /// </summary>
    public static (string Kind, string? Payload) Parse(string text)
    {
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (!line.StartsWith(Marker, StringComparison.Ordinal))
            {
                continue;
            }

            var parts = line.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            var kind = parts.Length >= 2 ? parts[1] : string.Empty;
            var payload = parts.Length >= 3 ? parts[2] : null;
            return (kind, payload);
        }

        throw new SandboxException(
            SandboxErrorKind.Protocol,
            "Sandbox command wrapper returned no recognizable status line."
        );
    }

    /// <summary>
    /// Parses the stale-cleanup listing into <c>(directoryName, leaseUnixSeconds, createdUnixSeconds)</c>
    /// tuples, skipping any line that is not a well-formed GC line. A malformed line is ignored rather
    /// than fatal — cleanup is best-effort maintenance and must never fail a command.
    /// </summary>
    public static IReadOnlyList<(string Name, long Lease, long Created)> ParseGcListing(string text)
    {
        var entries = new List<(string, long, long)>();
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (!line.StartsWith(GcMarker, StringComparison.Ordinal))
            {
                continue;
            }

            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 4 && long.TryParse(parts[2], out var lease) && long.TryParse(parts[3], out var created))
            {
                entries.Add((parts[1], lease, created));
            }
        }

        return entries;
    }
}
