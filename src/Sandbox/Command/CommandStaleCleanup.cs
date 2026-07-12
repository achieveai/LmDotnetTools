namespace AchieveAi.LmDotnetTools.Sandbox.Command;

/// <summary>
/// The pure stale-cleanup selection rule, kept separate from the gateway plumbing so it can be
/// exercised directly. An artifact directory is eligible for deletion only when BOTH its lease has
/// expired AND it is STRICTLY older than <see cref="CommandArtifactLayout.StaleAgeSeconds"/> (24 hours,
/// the operation-id idempotency/recovery retention window) — so a still-leased (active) operation is
/// protected regardless of age, an operation exactly at the 24-hour boundary is still retained (a
/// same-id retry there is answered, never re-run), and only an operation past the window is reclaimed.
/// </summary>
internal static class CommandStaleCleanup
{
    /// <summary>
    /// Returns the directory names, from a bounded listing, that are safe to delete at
    /// <paramref name="nowUnixSeconds"/>. The input is already bounded by the caller's GC scan limit, so
    /// this performs no scanning itself. Entries whose lease or created timestamp was never established
    /// (≤ 0) are skipped, so a directory still claiming is never selected.
    /// </summary>
    public static IReadOnlyList<string> SelectStale(
        IReadOnlyList<(string Name, long Lease, long Created)> entries,
        long nowUnixSeconds
    )
    {
        var stale = new List<string>();
        foreach (var (name, lease, created) in entries)
        {
            // A never-established lease or created timestamp (≤ 0) marks a directory that is still
            // claiming or is otherwise anomalous — never treat it as stale, or cleanup would race a
            // winner that has not finished establishing its claim.
            if (lease <= 0 || created <= 0)
            {
                continue;
            }

            // The 24-hour retention window is INCLUSIVE of its boundary: an operation exactly 24h old is
            // still recoverable (kept); only one strictly past the window is reclaimed. This keeps the
            // sweep honest against the documented "within the window a same-id retry never re-runs".
            var leaseExpired = nowUnixSeconds > lease;
            var pastRetentionWindow = nowUnixSeconds - created > CommandArtifactLayout.StaleAgeSeconds;
            if (leaseExpired && pastRetentionWindow)
            {
                stale.Add(name);
            }
        }

        return stale;
    }
}
