using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using LmStreaming.Sample.Models;
using LmStreaming.Sample.Persistence;

namespace LmStreaming.Sample.Services;

/// <summary>
///     Reconstructs the persisted descendant graph (children, grandchildren, ...) reachable from one root
///     conversation, and remembers the answer per root so repeated readers do not each pay for a full
///     store scan.
/// </summary>
/// <remarks>
///     <para>
///         The traversal is lifted verbatim out of <c>ConversationsController.BuildDescendantTreeAsync</c>
///         (issue #251, phase 2): one bounded <see cref="IConversationStore.ListThreadsAsync(int, int, ConversationListOptions, CancellationToken)"/> paging
///         scan, one in-memory parent→children index built from it, then a visited-set BFS from the root.
///         Deliberately persisted-only — no live <c>SubAgentManager</c> union — because no current spawn
///         path creates a depth-&gt;1 tree anyway (nested live Agent delegation stays disabled); this
///         reader answers "what does the persisted graph say", which is exactly what a restarted host or
///         a finished run still has. The root itself is never emitted as a node, only its descendants.
///     </para>
///     <para>
///         <b>Why the cache is mandatory, not an optimisation.</b>
///         <see cref="FileConversationStore.ListThreadsAsync(int, int, ConversationListOptions, CancellationToken)"/> has no offset index: every call enumerates
///         EVERY thread directory, deserializes every <c>metadata.json</c> (falling back to deserializing
///         a whole <c>messages.json</c> when metadata is missing), sorts the lot, and only then applies
///         <c>Skip(offset).Take(limit)</c>. A scan therefore costs TotalThreadsInStore file reads, all
///         serialized behind that store's single process-wide semaphore — so an uncached scan on every
///         transcript flush would stall every other conversation in the host, not just this one.
///     </para>
///     <para>
///         <b>Why this does NOT reuse <see cref="SubAgentScanCoverageCache"/>.</b> That cache answers a
///         different question and reusing it here would be a correctness bug, not just a poor fit:
///         (1) it caches the DIRECT-CHILD roster
///         (<see cref="AgentHierarchyService.ScanPersistedSubAgentChildrenAsync"/>), not the transitive
///         descendant graph this class builds; (2) it is keyed by (threadId, live-manager owner) with
///         reference-equality owner matching, and its entries live for the process lifetime — the writer
///         calling in here has no <c>SubAgentManager</c> to key on and would collapse onto the shared
///         <see cref="SubAgentScanCoverageCache.NoLiveManager"/> sentinel; and (3) it deliberately caches
///         EMPTY rosters as a valid "genuinely childless" answer, so a hit taken on a conversation that
///         had no children at first flush would keep reporting "no descendants" forever and never
///         discover a sub-agent spawned later in the same run. This cache is keyed by root alone and is
///         explicitly refreshable (<see cref="NoteAgentActivity"/>) precisely so that a newly spawned
///         sub-agent is discovered.
///     </para>
///     <para>
///         <b>Refresh policy.</b> Discovery happens ONCE per conversation and is repeated only when a
///         caller reports it observed new Agent-tool activity on that root — not once per turn, and never
///         once per flush. <see cref="Forget"/> drops a root outright and is wired to
///         <c>MultiTurnAgentPool.ThreadRemoved</c> by the composition root (the pool is deliberately not a
///         constructor dependency: this type depends only on the store, so nothing in the pool's own
///         construction graph can cycle back into it).
///     </para>
///     <para>
///         <b>Bounded retention.</b> <see cref="_capacity"/> caps the number of distinct roots tracked and
///         the LEAST RECENTLY USED entry is evicted first — every read, every write-back and every
///         reported activity renews its root, so a conversation that is actively mirroring cannot be
///         dropped ahead of an idle one that merely happens to have been created later. Losing an entry
///         only costs the next caller one extra scan — it does not lose data — which is why the renewal is
///         an O(1) list splice and nothing more elaborate.
///     </para>
/// </remarks>
public sealed class ConversationDescendantScanner
{
    /// <summary>Default cap on distinct roots tracked; mirrors <c>WorkflowRunRegistry</c>'s default.</summary>
    public const int DefaultCapacity = WorkflowRunRegistry.DefaultMaxPersistedEntriesPerConversation;

    /// <summary>
    ///     Total cap for the persisted sub-agent scan. <see cref="IConversationStore"/> has no property
    ///     index, so rebuilding the roster means scanning thread metadata; the cap bounds the work on a
    ///     long-lived store and truncation is logged rather than silently swallowed.
    /// </summary>
    private const int SubAgentScanMaxThreads = 2000;

    /// <summary>
    ///     One root's cache slot. <see cref="Version"/> is bumped by <see cref="NoteAgentActivity"/>;
    ///     <see cref="Nodes"/> is only served when <see cref="NodesVersion"/> still matches, so a refresh
    ///     signalled WHILE a scan was in flight discards that scan's now-stale answer instead of recording
    ///     it as fresh.
    /// </summary>
    /// <remarks>
    ///     The INSTANCE is the slot's lifecycle identity and a scan in flight is bound to the one it
    ///     started against — <see cref="Version"/> is a refresh counter, not a generation, and restarts at
    ///     zero on every replacement slot, so it cannot by itself tell a slot apart from its successor.
    /// </remarks>
    private sealed class RootState
    {
        public long Version;
        public long NodesVersion = -1;
        public IReadOnlyList<SubAgentSummary>? Nodes;
    }

    private readonly IConversationStore _store;
    private readonly ILogger<ConversationDescendantScanner> _logger;
    private readonly int _capacity;

    /// <summary>Guards both collections below; only in-memory bookkeeping is done under it, never a
    /// store scan, so contention is negligible.</summary>
    private readonly object _gate = new();
    private readonly Dictionary<string, LinkedListNode<KeyValuePair<string, RootState>>> _byRoot = new(
        StringComparer.Ordinal
    );
    private readonly LinkedList<KeyValuePair<string, RootState>> _usageOrder = new();

    public ConversationDescendantScanner(
        IConversationStore store,
        ILogger<ConversationDescendantScanner> logger,
        int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _store = store;
        _logger = logger;
        _capacity = capacity;
    }

    /// <summary>
    ///     The uncached descendant graph for <paramref name="rootThreadId"/>, in the response order the
    ///     recursive listing contract promises (depth, then parent thread id, then thread id — all
    ///     ordinal). Always pays for a full store scan; callers that poll should use
    ///     <see cref="GetOrScanAsync"/> instead.
    /// </summary>
    public async Task<IReadOnlyList<SubAgentSummary>> ScanAsync(
        string rootThreadId,
        CancellationToken ct = default)
    {
        var allNodes = await ScanAllPersistedSubAgentNodesAsync(rootThreadId, ct);
        var childrenByParent = allNodes
            .GroupBy(n => n.ParentThreadId!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        var discovered = new List<SubAgentSummary>();
        var visited = new HashSet<string>(StringComparer.Ordinal) { rootThreadId };
        var queue = new Queue<(string ThreadId, int Depth)>();
        queue.Enqueue((rootThreadId, 0));

        while (queue.Count > 0)
        {
            var (parentId, depth) = queue.Dequeue();
            if (!childrenByParent.TryGetValue(parentId, out var children))
            {
                continue;
            }

            foreach (var child in children)
            {
                if (!visited.Add(child.ThreadId))
                {
                    // Cycle cut (or a diamond re-reachable via a second path) — the visited set has
                    // already placed this thread at an earlier, shallower position. Log opaque ids
                    // only: never Name/Template/Task, which may carry caller-supplied free text.
                    _logger.LogWarning(
                        "Sub-agent recursive scan for {RootThreadId} cut a repeat visit at thread "
                            + "{ThreadId} (parent {ParentThreadId})",
                        rootThreadId,
                        child.ThreadId,
                        parentId);
                    continue;
                }

                var childDepth = depth + 1;
                discovered.Add(child with { Depth = childDepth });
                queue.Enqueue((child.ThreadId, childDepth));
            }
        }

        return
        [
            .. discovered
                .OrderBy(n => n.Depth)
                .ThenBy(n => n.ParentThreadId, StringComparer.Ordinal)
                .ThenBy(n => n.ThreadId, StringComparer.Ordinal),
        ];
    }

    /// <summary>
    ///     The descendant graph for <paramref name="rootThreadId"/>, scanned once and then served from
    ///     memory until <see cref="NoteAgentActivity"/> or <see cref="Forget"/> says otherwise. An empty
    ///     result is cached like any other: a root with no sub-agents must not re-scan on every call, and
    ///     the refresh signal — not a cache miss — is what makes a later spawn visible.
    /// </summary>
    public async Task<IReadOnlyList<SubAgentSummary>> GetOrScanAsync(
        string rootThreadId,
        CancellationToken ct = default)
    {
        RootState observed;
        long observedVersion;
        lock (_gate)
        {
            observed = GetOrAddState(rootThreadId);
            if (observed.Nodes is not null && observed.NodesVersion == observed.Version)
            {
                return observed.Nodes;
            }

            observedVersion = observed.Version;
        }

        // Scanned outside the lock: two callers that both miss simply both scan (redundant, not
        // incorrect — equivalent data, last write wins), which beats holding a lock across store IO.
        var nodes = await ScanAsync(rootThreadId, ct);

        lock (_gate)
        {
            // The SLOT INSTANCE, not the key, is what this scan's answer belongs to. A version match
            // alone is not enough: Version starts at 0 on every new RootState, so if this root's slot was
            // dropped mid-scan — by Forget, or by the bound-eviction in GetOrAddState — a slot re-created
            // afterwards compares equal to the version observed before the drop, and this scan's answer
            // (the PREVIOUS conversation's descendants, since a thread id is client-suppliable and may be
            // reused) would be committed as the replacement's cached graph, then served to every later
            // reader and mirrored into the wrong workspace.
            //
            // Looked up rather than GetOrAddState'd on purpose: when the original slot is gone there is
            // nothing this answer may be written into, and creating one only to discard the result would
            // grow the cache and can bound-evict a live root for nothing.
            if (_byRoot.TryGetValue(rootThreadId, out var node)
                && ReferenceEquals(node.Value.Value, observed))
            {
                // A completed write-back is USE, so the slot is renewed even when the answer is stale.
                Renew(node);
                if (observed.Version == observedVersion)
                {
                    observed.Nodes = nodes;
                    observed.NodesVersion = observedVersion;
                }
            }
        }

        return nodes;
    }

    /// <summary>
    ///     Signals that new Agent-tool activity was observed on <paramref name="rootThreadId"/>, so the
    ///     next <see cref="GetOrScanAsync"/> rediscovers its descendants. This is the ONLY refresh trigger
    ///     besides <see cref="Forget"/> — callers must not call it once per turn or once per flush, only
    ///     when they actually saw agent activity, since each call costs one full store scan.
    /// </summary>
    public void NoteAgentActivity(string rootThreadId)
    {
        lock (_gate)
        {
            // A root with no slot has nothing cached to invalidate, and its first GetOrScanAsync scans
            // anyway — so recording a version for it would only grow the cache for no benefit.
            if (_byRoot.TryGetValue(rootThreadId, out var node))
            {
                node.Value.Value.Version++;

                // Activity is USE, and the strongest signal there is that this conversation is live: the
                // caller only raises it when it actually saw an Agent tool run. Renewing keeps a root that
                // is spawning sub-agents right now from being evicted ahead of an idle one.
                Renew(node);
            }
        }
    }

    /// <summary>
    ///     Drops any remembered graph for <paramref name="rootThreadId"/>. Wired to
    ///     <c>MultiTurnAgentPool.ThreadRemoved</c> so an evicted or deleted conversation cannot keep a
    ///     stale graph alive — and so a later reuse of the same (client-suppliable) thread id never
    ///     resurrects the previous conversation's descendants.
    /// </summary>
    public void Forget(string rootThreadId)
    {
        lock (_gate)
        {
            if (_byRoot.Remove(rootThreadId, out var node))
            {
                _usageOrder.Remove(node);
            }
        }
    }

    /// <summary>Returns the slot for <paramref name="rootThreadId"/>, renewing it as most-recently-used and
    /// creating (and bound-evicting) one on first use. Callers must hold <see cref="_gate"/>.</summary>
    private RootState GetOrAddState(string rootThreadId)
    {
        if (_byRoot.TryGetValue(rootThreadId, out var existing))
        {
            Renew(existing);
            return existing.Value.Value;
        }

        var state = new RootState();
        _byRoot[rootThreadId] = _usageOrder.AddLast(
            new KeyValuePair<string, RootState>(rootThreadId, state));

        while (_byRoot.Count > _capacity)
        {
            var oldest = _usageOrder.First!;
            _usageOrder.RemoveFirst();
            _ = _byRoot.Remove(oldest.Value.Key);
        }

        return state;
    }

    /// <summary>Moves a resident slot to the most-recently-used end. Callers must hold
    /// <see cref="_gate"/>.</summary>
    /// <remarks>
    ///     An unlink and a re-link of a node the caller already holds — O(1), no rehash and no copy — which
    ///     is the whole reason the order is a linked list beside the dictionary rather than a timestamp
    ///     someone has to sort.
    /// </remarks>
    private void Renew(LinkedListNode<KeyValuePair<string, RootState>> node)
    {
        _usageOrder.Remove(node);
        _usageOrder.AddLast(node);
    }

    /// <summary>
    ///     The single bounded store scan the descendant graph is built from — every stamped sub-agent
    ///     thread, projected via the no-filter
    ///     <see cref="SubAgentProvenance.TryProject(ThreadMetadata)"/> overload, regardless of who its
    ///     parent is. The caller indexes the result in memory; nothing here queries the store again per
    ///     node, satisfying the "one bounded scan per request" requirement even for the recursive,
    ///     arbitrary-depth graph.
    /// </summary>
    /// <remarks>
    ///     <b>One call, deliberately — not a paging loop.</b> Offset pagination over this store is unsound
    ///     here, and not only in theory: <see cref="IConversationStore.ListThreadsAsync(int, int, ConversationListOptions, CancellationToken)"/> is CONTRACTUALLY
    ///     "ordered by last updated descending", so every implementation sorts on a column that the live
    ///     conversations being scanned are mutating underneath the scan. A thread touched between page N
    ///     and page N+1 moves toward the front and pushes an unread neighbour backwards across the offset
    ///     boundary, where the next page's offset skips it for good — and the roster that skip corrupts is
    ///     then CACHED, so one sub-agent silently loses its transcript for the life of the entry. A single
    ///     call has no boundary to shift across. Asking for one more than the cap is what keeps the
    ///     truncation warning honest, since a full page is otherwise indistinguishable from a truncated
    ///     one. It is also cheaper on the store this class is most afraid of: per the note above,
    ///     <see cref="FileConversationStore"/> re-enumerates EVERY thread directory per call, so paging
    ///     multiplied that cost by the page count for no benefit.
    /// </remarks>
    private async Task<IReadOnlyList<SubAgentSummary>> ScanAllPersistedSubAgentNodesAsync(
        string requestingThreadId,
        CancellationToken ct)
    {
        // Narrowed by the database, not in memory (#388a). The scope comes from the ROOT row's own
        // tenant - see SubAgentScanScope for why a principal is not available here and would be the
        // wrong thing to use if it were. A root with no tenant falls back to the unscoped overload
        // rather than guessing at a sentinel one.
        var scope = await SubAgentScanScope.ForRootAsync(_store, requestingThreadId, ct);
        var threads = (scope is null
            ? await _store.ListThreadsAsync(SubAgentScanMaxThreads + 1, 0, ct: ct)
            : await _store.ListThreadsAsync(scope, SubAgentScanMaxThreads + 1, 0, ct: ct)) ?? [];

        var scanned = Math.Min(threads.Count, SubAgentScanMaxThreads);
        var found = new List<SubAgentSummary>();
        for (var i = 0; i < scanned; i++)
        {
            var node = SubAgentProvenance.TryProject(threads[i]);
            if (node is not null)
            {
                found.Add(node);
            }
        }

        if (threads.Count > SubAgentScanMaxThreads)
        {
            _logger.LogWarning(
                "Sub-agent scan for {ThreadId} stopped at the {MaxThreads}-thread cap; "
                    + "children persisted beyond that point are not listed.",
                requestingThreadId,
                SubAgentScanMaxThreads);
        }

        return found;
    }
}
