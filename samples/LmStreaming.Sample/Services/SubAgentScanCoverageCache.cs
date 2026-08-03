using LmStreaming.Sample.Models;

namespace LmStreaming.Sample.Services;

/// <summary>
///     Process-lifetime memory of the persisted Agent-tool child roster
///     <see cref="AgentHierarchyService.ScanPersistedSubAgentChildrenAsync"/> reconstructs for one
///     conversation, keyed by <c>threadId</c> AND the live-manager identity ("owner") that asked for it.
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
///         <b>Owner/generation keying (PR #245 review — cache must invalidate on manager reset).</b> A
///         mode switch, provider switch, pool eviction+reopen, or restart-rehydration each construct a
///         brand-new agent via <c>MultiTurnAgentPool.CreateAgentEntry</c> — which means a brand-new
///         <c>MultiTurnAgentLoop</c> and a brand-new <c>SubAgentManager</c> instance. A cache keyed on
///         <c>threadId</c> alone would keep serving the FIRST manager's recovered roster forever, even
///         after that manager is gone and a fresh one (which may have since gained its own new,
///         not-yet-persisted-at-scan-time children) has taken its place. Every recorded entry therefore
///         also carries the <c>owner</c> reference the caller resolved for that thread — the live
///         <c>SubAgentManager</c> instance, or the shared <see cref="NoLiveManager"/> sentinel when no
///         live agent covers the thread at all. <see cref="TryGetRecovered"/> only reports a hit when the
///         caller's current owner reference-equals the one the entry was recorded under; any other owner
///         (including the very first call after a reset) is treated as a miss, so
///         <see cref="AgentHierarchyService.GetOrScanPersistedSubAgentChildrenAsync"/> rescans and
///         overwrites the entry with the new owner. This covers every reset path automatically — no call
///         site (mode switch, provider switch, restart, pool eviction) needs to remember to invalidate the
///         cache itself, and <c>MultiTurnAgentPool</c> (library code in LmAgentInfra) never needs to know
///         this sample-layer cache exists.
///     </para>
///     <para>
///         Because the owner reference changes on every reset, entries for a still-live thread stop
///         accumulating: there is exactly one slot per <c>threadId</c>, just re-keyed to the new owner
///         and overwritten in place. The SAME-owner empty→populated transition this cache exists to
///         survive (see PRRT_kwDOOPysWM6V1mjj above) still works exactly as before, since the owner does
///         NOT change just because the live manager spawns a new child.
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
///         <b>Bounded retention.</b> Even with one slot per thread, a process that answers hierarchy
///         requests for an unbounded number of distinct conversations over a long enough lifetime would
///         otherwise grow this cache forever. <see cref="_capacity"/> (defaulted from the same
///         <c>AgentCollaboration:MaxPersistedHierarchyEntries</c> knob <c>WorkflowRunRegistry</c> already
///         uses for its own retention ceiling — see <c>Program.cs</c>) caps the number of distinct
///         threads tracked; the oldest entry BY LAST WRITE (not last read — no recency bump on a cache
///         hit, which keeps the policy simple and deterministic to test) is evicted first. Losing a cold
///         entry only costs the next poll for that thread one extra rescan — it does not lose data.
///     </para>
///     <para>
///         <b>Delete eviction.</b> <see cref="Forget"/> removes a thread's entry outright, regardless of
///         owner. Owner-keying alone handles every RESET, but a deleted conversation whose thread id is
///         later reused (client-supplied ids are possible in this sample) would otherwise start "cold"
///         (no live manager) with the same <see cref="NoLiveManager"/> sentinel owner the deleted
///         conversation's cold entry was ALSO recorded under — a coincidental owner match that would
///         incorrectly resurrect the deleted conversation's stale recovered rows. <c>ConversationsController.Delete</c>
///         calls <see cref="Forget"/> after a successful delete to close that gap.
///     </para>
/// </remarks>
public sealed class SubAgentScanCoverageCache
{
    /// <summary>Default cap on distinct threads tracked; mirrors <c>WorkflowRunRegistry</c>'s default.</summary>
    public const int DefaultCapacity = WorkflowRunRegistry.DefaultMaxPersistedEntriesPerConversation;

    /// <summary>
    ///     Shared owner sentinel for a thread with no live manager covering it (evicted/idle pool entry,
    ///     or a live agent that is not a collaboration-off <c>MultiTurnAgentLoop</c> with a
    ///     <c>SubAgentManager</c>). A single shared instance lets repeated "cold" polls for the SAME
    ///     thread keep hitting the cache, since they all resolve the same owner reference.
    /// </summary>
    public static readonly object NoLiveManager = new();

    private sealed record CacheEntry(object Owner, IReadOnlyList<SubAgentSummary> Rows);

    /// <summary>Guards both collections below; operations are in-memory dictionary/list bookkeeping only,
    /// never held across the store scan itself, so contention is negligible.</summary>
    private readonly object _gate = new();
    private readonly Dictionary<string, LinkedListNode<KeyValuePair<string, CacheEntry>>> _byThread = [];
    private readonly LinkedList<KeyValuePair<string, CacheEntry>> _writeOrder = new();
    private readonly int _capacity;

    public SubAgentScanCoverageCache(int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _capacity = capacity;
    }

    /// <summary>
    ///     Returns the roster recorded for <paramref name="threadId"/> under <paramref name="owner"/> by
    ///     an earlier <see cref="RecordRecovered"/>, if the persisted scan has already run for this exact
    ///     (thread, owner) pair this process lifetime. An empty list is a valid, cached "genuinely
    ///     childless" answer — distinct from a miss, which returns <see langword="false"/> and means
    ///     either the scan has never completed for this thread, or it last completed for a DIFFERENT
    ///     owner (the live manager was reset since).
    /// </summary>
    public bool TryGetRecovered(string threadId, object owner, out IReadOnlyList<SubAgentSummary> rows)
    {
        lock (_gate)
        {
            if (_byThread.TryGetValue(threadId, out var node) && ReferenceEquals(node.Value.Value.Owner, owner))
            {
                rows = node.Value.Value.Rows;
                return true;
            }
        }

        rows = [];
        return false;
    }

    /// <summary>
    ///     Records the roster a completed scan reconstructed for <paramref name="threadId"/> under
    ///     <paramref name="owner"/>, so no later call for the same (thread, owner) pays for the scan
    ///     again. Call only after the scan has finished successfully — never for a cancelled or failed
    ///     attempt. Overwrites any entry already recorded for this thread (under any owner) and marks it
    ///     as the most-recently-written entry, so bounded eviction below evicts the least-recently-WRITTEN
    ///     thread first.
    /// </summary>
    public void RecordRecovered(string threadId, object owner, IReadOnlyList<SubAgentSummary> rows)
    {
        lock (_gate)
        {
            if (_byThread.TryGetValue(threadId, out var existing))
            {
                _writeOrder.Remove(existing);
            }

            var node = _writeOrder.AddLast(new KeyValuePair<string, CacheEntry>(threadId, new CacheEntry(owner, rows)));
            _byThread[threadId] = node;

            while (_byThread.Count > _capacity)
            {
                var oldest = _writeOrder.First!;
                _writeOrder.RemoveFirst();
                _ = _byThread.Remove(oldest.Value.Key);
            }
        }
    }

    /// <summary>
    ///     Removes any recorded entry for <paramref name="threadId"/> outright, regardless of owner.
    ///     Called on conversation delete (see remarks above) so a later reuse of the same thread id never
    ///     resurrects a deleted conversation's recovered rows.
    /// </summary>
    public void Forget(string threadId)
    {
        lock (_gate)
        {
            if (_byThread.Remove(threadId, out var node))
            {
                _writeOrder.Remove(node);
            }
        }
    }
}
