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
///     <para>
///         <b>Scan/delete interleaving (PR #245 review — a late scan writeback must not resurrect a
///         delete).</b> <see cref="Forget"/> above closes the gap for a scan that STARTS after the
///         delete. It does not by itself close the reverse ordering: a scan already in flight when
///         <see cref="Forget"/> runs is not cancelled by it — <c>AgentHierarchyService</c> still holds a
///         completed, in-hand roster from BEFORE the delete and, without a caller-provided sequencing
///         signal, would call <see cref="RecordRecovered"/> with it right after <see cref="Forget"/>
///         returns, silently re-inserting the deleted thread's stale roster. Every entry — including a
///         <see cref="Forget"/> tombstone — therefore also carries a <c>Generation</c> counter.
///         <see cref="CaptureGeneration"/> reads the counter BEFORE the scan starts; <see cref="Forget"/>
///         increments it (and replaces the entry with a tombstone recorded under a private sentinel
///         owner no real caller can ever reference-equal, so it is an ordinary cache MISS from every
///         external caller's point of view — "removes the entry" from <see cref="TryGetRecovered"/>'s
///         perspective, while still remembering that a delete happened); <see cref="RecordRecovered"/>
///         only commits the write if the current generation still equals the one captured before the
///         scan started, otherwise it rejects the write outright (returns <see langword="false"/>) and
///         leaves the tombstone alone. An owner-keyed reset (mode/provider switch, pool eviction+reopen,
///         restart) does NOT bump the generation — only an explicit <see cref="Forget"/> does — so a
///         race between a reset and an in-flight scan is unaffected by this counter and continues to
///         resolve purely through the owner check documented above (last write wins, both answers are
///         equally valid). Plain capacity eviction (below) also never bumps the generation; it just
///         removes whichever entry — real or tombstone — is oldest by last write, same as it always has.
///     </para>
///     <para>
///         The generation counter piggybacks on the SAME bounded <c>_byThread</c>/<c>_writeOrder</c>
///         structure capacity eviction already maintains, rather than a second, separately-retained map —
///         a tombstone is just an entry like any other and ages out under ordinary capacity pressure
///         exactly like a real one, so a deleted thread never pins memory forever. The trade-off this
///         accepts: if capacity pressure evicts a thread's tombstone before its interleaved in-flight
///         scan writes back, the generation counter for that thread resets to the 0 baseline and the
///         stale write is no longer distinguishable from a legitimate first-ever recording. This can only
///         happen under simultaneous delete + eviction + in-flight-scan pressure on the SAME thread id,
///         and is the accepted cost of keeping this cache's memory bounded rather than unbounded.
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

    /// <summary>
    ///     Private owner sentinel <see cref="Forget"/> tombstones are recorded under. No external caller
    ///     can ever hold a reference to this instance, so a tombstoned entry is an unconditional MISS from
    ///     <see cref="TryGetRecovered"/> regardless of which owner the caller passes — including
    ///     <see cref="NoLiveManager"/> itself.
    /// </summary>
    private static readonly object TombstoneOwner = new();

    private sealed record CacheEntry(object Owner, IReadOnlyList<SubAgentSummary> Rows, long Generation);

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
    ///     Returns the generation counter currently associated with <paramref name="threadId"/> — 0 if
    ///     the thread has no entry at all (never recorded, or aged out of the bounded cache). Call this
    ///     BEFORE starting a persisted-store scan and pass the result back into the matching
    ///     <see cref="RecordRecovered"/> call once the scan completes (see the scan/delete interleaving
    ///     remarks above) — this is what lets a <see cref="Forget"/> that lands while the scan is in
    ///     flight cause that scan's writeback to be rejected instead of resurrecting a deleted thread.
    /// </summary>
    public long CaptureGeneration(string threadId)
    {
        lock (_gate)
        {
            return _byThread.TryGetValue(threadId, out var node) ? node.Value.Value.Generation : 0L;
        }
    }

    /// <summary>
    ///     Records the roster a completed scan reconstructed for <paramref name="threadId"/> under
    ///     <paramref name="owner"/>, so no later call for the same (thread, owner) pays for the scan
    ///     again. Call only after the scan has finished successfully — never for a cancelled or failed
    ///     attempt. Overwrites any entry already recorded for this thread (under any owner) and marks it
    ///     as the most-recently-written entry, so bounded eviction below evicts the least-recently-WRITTEN
    ///     thread first.
    /// </summary>
    /// <param name="threadId">The thread whose recovered roster is being recorded.</param>
    /// <param name="owner">
    ///     The live-manager identity (or <see cref="NoLiveManager"/>) the caller resolved for this thread —
    ///     see the owner/generation keying remarks above.
    /// </param>
    /// <param name="rows">The roster the scan reconstructed.</param>
    /// <param name="generation">
    ///     The value <see cref="CaptureGeneration"/> returned for <paramref name="threadId"/> immediately
    ///     BEFORE the scan that produced <paramref name="rows"/> was started. Defaults to 0 (the baseline
    ///     for a thread that has never been forgotten), which is correct for any caller that does not need
    ///     to guard against an interleaved <see cref="Forget"/> — every existing call site that predates
    ///     this parameter keeps its original unconditional-write behavior unchanged. If the current
    ///     generation no longer matches (a <see cref="Forget"/> ran for this thread after the caller's
    ///     scan started), the write is rejected and this method returns <see langword="false"/> without
    ///     touching the cache — the tombstone <see cref="Forget"/> left behind is preserved.
    /// </param>
    /// <returns>
    ///     <see langword="true"/> if the roster was recorded; <see langword="false"/> if the write was
    ///     rejected because the generation captured before the scan is stale.
    /// </returns>
    public bool RecordRecovered(
        string threadId,
        object owner,
        IReadOnlyList<SubAgentSummary> rows,
        long generation = 0)
    {
        lock (_gate)
        {
            var currentGeneration = _byThread.TryGetValue(threadId, out var existing)
                ? existing.Value.Value.Generation
                : 0L;

            if (currentGeneration != generation)
            {
                return false;
            }

            if (existing is not null)
            {
                _writeOrder.Remove(existing);
            }

            var node = _writeOrder.AddLast(
                new KeyValuePair<string, CacheEntry>(threadId, new CacheEntry(owner, rows, generation)));
            _byThread[threadId] = node;

            while (_byThread.Count > _capacity)
            {
                var oldest = _writeOrder.First!;
                _writeOrder.RemoveFirst();
                _ = _byThread.Remove(oldest.Value.Key);
            }

            return true;
        }
    }

    /// <summary>
    ///     Removes any recorded entry for <paramref name="threadId"/>, regardless of owner, from every
    ///     external caller's point of view (<see cref="TryGetRecovered"/> misses unconditionally
    ///     afterward). Called on conversation delete (see remarks above) so a later reuse of the same
    ///     thread id never resurrects a deleted conversation's recovered rows.
    /// </summary>
    /// <remarks>
    ///     Internally this does not fully erase the slot: it replaces whatever was there with a
    ///     tombstone — recorded under <see cref="TombstoneOwner"/> (so it can never match a real caller's
    ///     owner) at one generation past whatever was there before. The generation bump is what lets
    ///     <see cref="RecordRecovered"/> reject a scan that was already in flight when this call landed
    ///     (see the scan/delete interleaving remarks above); the tombstone still occupies one slot in the
    ///     SAME bounded structure capacity eviction maintains, so it ages out under ordinary capacity
    ///     pressure like any other entry rather than pinning memory for this thread id forever.
    /// </remarks>
    public void Forget(string threadId)
    {
        lock (_gate)
        {
            var nextGeneration = 1L;
            if (_byThread.TryGetValue(threadId, out var existing))
            {
                nextGeneration = existing.Value.Value.Generation + 1;
                _writeOrder.Remove(existing);
            }

            var node = _writeOrder.AddLast(
                new KeyValuePair<string, CacheEntry>(threadId, new CacheEntry(TombstoneOwner, [], nextGeneration)));
            _byThread[threadId] = node;

            while (_byThread.Count > _capacity)
            {
                var oldest = _writeOrder.First!;
                _writeOrder.RemoveFirst();
                _ = _byThread.Remove(oldest.Value.Key);
            }
        }
    }
}
