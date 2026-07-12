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
    /// this performs no scanning itself.
    /// </summary>
    public static IReadOnlyList<string> SelectStale(
        IReadOnlyList<(string Name, long Lease, long Created)> entries,
        long nowUnixSeconds
    )
    {
        var stale = new List<string>();
        foreach (var (name, lease, created) in entries)
        {
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
