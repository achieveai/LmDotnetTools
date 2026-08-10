namespace CodeReviewDaemon.Sample.Orchestration;

/// <summary>
/// This daemon process's identity, used to record which process owns a review run (task 29).
/// <para>
/// Deliberately a per-PROCESS GUID rather than a pid. A pid only identifies a process on one machine, so
/// a pid-based owner would have to be right about pid recycling AND about machine identity — and the
/// second has no sound answer once a database is reachable from more than one host, which the daemon
/// supports. A GUID minted at startup removes the question instead of answering it: it is unique across
/// machines, containers and restarts by construction, and a recycled pid can never collide with it.
/// </para>
/// <para>
/// Static rather than injected because it IS process-scoped state, in the same category as the machine
/// name — there is exactly one per process by definition, and threading it through DI would add a
/// registration to every call site that claims a run without making any of them more testable.
/// </para>
/// </summary>
internal static class DaemonInstance
{
    /// <summary>This process's owner id. Stable for the lifetime of the process, unique across processes.</summary>
    public static string Id { get; } = Guid.NewGuid().ToString("N");
}
