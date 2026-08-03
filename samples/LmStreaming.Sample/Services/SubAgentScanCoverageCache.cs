using System.Collections.Concurrent;
using LmStreaming.Sample.Models;

namespace LmStreaming.Sample.Services;

/// <summary>
///     Process-lifetime memory of the persisted Agent-tool child roster
///     <see cref="AgentHierarchyService.ScanPersistedSubAgentChildrenAsync"/> reconstructs for one
///     conversation, keyed by <c>threadId</c>.
/// </summary>
/// <remarks>
///     <para>
///         <see cref="AgentHierarchyService"/> is never a shared instance: it is built fresh at every
///         call site (once per HTTP request in <c>ConversationsController</c>, once per spawned agent's
///         <c>GetAgentTranscript</c> tool registration in <c>Program.cs</c>). An instance field on that
///         service could not remember "already scanned this thread" from one poll to the next — this
///         cache is the one thing both call sites share (registered singleton), so it is what actually
///         makes the scan-once guarantee hold across repeated requests.
///     </para>
///     <para>
///         It exists to close PRRT_kwDOOPysWM6V1mjj: gating the cold scan on
///         <c>SubAgentManager.ListAgents().Count == 0</c> is not a stable coverage signal. A rehydrated
///         collaboration-off loop starts with an empty manager (recovers old, pre-restart children via
///         the scan), but the moment it spawns ONE new child the manager becomes non-empty and the old
///         gate stopped scanning — silently dropping every recovered row, because ordinary
///         collaboration-off rows are never write-through-persisted to <see cref="WorkflowRunRegistry"/>.
///         Recording the recovered roster here — once, the first time this thread is ever seen needing
///         it — means every later call (empty manager or not) can union the SAME recovered rows with
///         whatever the live manager reports right now, and live always wins on a matching key
///         (<see cref="AgentHierarchyService.BuildAsync"/> adds live rows to the merge last).
///     </para>
///     <para>
///         A miss is recorded only after <see cref="AgentHierarchyService.ScanPersistedSubAgentChildrenAsync"/>
///         RUNS TO COMPLETION — a cancelled or failed scan throws before the result reaches
///         <see cref="RecordRecovered"/>, so the thread stays uncached and the next call retries instead
///         of being poisoned by a partial/failed answer. Concurrent callers that both miss the cache
///         simply both scan (redundant, not incorrect — the last write wins with equivalent data), which
///         is an acceptable one-time cost against the alternative of a lock held across a store scan.
///     </para>
///     <para>
///         Memory is bounded by the number of distinct conversations this process ever answers a
///         hierarchy request for — proportional to live/reopened conversation count, not to the size of
///         the underlying store, and cleared for free on the next process restart along with every other
///         in-memory pool/registry state this service already depends on.
///     </para>
/// </remarks>
public sealed class SubAgentScanCoverageCache
{
    private readonly ConcurrentDictionary<string, IReadOnlyList<SubAgentSummary>> _recovered = new();

    /// <summary>
    ///     Returns the roster recorded for <paramref name="threadId"/> by an earlier
    ///     <see cref="RecordRecovered"/>, if the persisted scan has already run for it this process
    ///     lifetime. An empty list is a valid, cached "genuinely childless" answer — distinct from a
    ///     miss, which returns <see langword="false"/> and means the scan has never completed for this
    ///     thread yet.
    /// </summary>
    public bool TryGetRecovered(string threadId, out IReadOnlyList<SubAgentSummary> rows) =>
        _recovered.TryGetValue(threadId, out rows!);

    /// <summary>
    ///     Records the roster a completed scan reconstructed for <paramref name="threadId"/>, so no
    ///     later call for the same thread pays for the scan again. Call only after the scan has finished
    ///     successfully — never for a cancelled or failed attempt.
    /// </summary>
    public void RecordRecovered(string threadId, IReadOnlyList<SubAgentSummary> rows) =>
        _recovered[threadId] = rows;
}
