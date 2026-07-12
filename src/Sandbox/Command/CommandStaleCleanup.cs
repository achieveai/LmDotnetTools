namespace AchieveAi.LmDotnetTools.Sandbox.Command;

/// <summary>
/// The pure stale-cleanup selection rule, kept separate from the gateway plumbing so it can be
/// exercised directly. An artifact directory is eligible for deletion only when BOTH its lease has
/// expired AND it is at least <see cref="CommandArtifactLayout.StaleAgeSeconds"/> (24 hours) old — so
/// a still-leased (active) operation is protected regardless of age, and a recently-created operation
/// is protected regardless of its lease.
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

            var leaseExpired = nowUnixSeconds > lease;
            var oldEnough = nowUnixSeconds - created >= CommandArtifactLayout.StaleAgeSeconds;
            if (leaseExpired && oldEnough)
            {
                stale.Add(name);
            }
        }

        return stale;
    }
}
