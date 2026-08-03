using AchieveAi.LmDotnetTools.LmAgentInfra.Agents;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.Collaboration;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using LmStreaming.Sample.Models;
using LmStreaming.Sample.Persistence;

namespace LmStreaming.Sample.Services;

/// <summary>How a transcript read resolved.</summary>
public enum AgentTranscriptOutcome
{
    /// <summary>The conversation itself is not known to this host.</summary>
    UnknownThread,

    /// <summary>The host never enabled collaboration, so there is no hierarchy to read across.</summary>
    CollaborationUnavailable,

    /// <summary>The reader may not see this agent; <see cref="AgentTranscriptResult.DenialCode"/> says why.</summary>
    Denied,

    /// <summary>The read was permitted and <see cref="AgentTranscriptResult.Messages"/> carries the transcript.</summary>
    Allowed,
}

/// <summary>
///     The content-free reason codes a transcript read answers with when there is no hierarchy to read —
///     the counterpart of <see cref="TranscriptAccessReasons"/>, which covers refusals.
/// </summary>
/// <remarks>
///     Named once so the HTTP route and the in-agent <c>GetAgentTranscript</c> tool report the same
///     vocabulary for the same outcome. They previously diverged (the tool said
///     <c>hierarchy_unavailable</c> where the route said <c>collaboration_unavailable</c>), which makes a
///     denial impossible to reason about from either side and impossible to document as one contract.
///     Like every code on this path these carry no name, thread, or task.
/// </remarks>
public static class AgentTranscriptReasons
{
    /// <summary>The conversation itself is not known to this host.</summary>
    public const string UnknownThread = "unknown_thread";

    /// <summary>The host never enabled collaboration, so there is no hierarchy to read across.</summary>
    public const string CollaborationUnavailable = "collaboration_unavailable";
}

/// <summary>The result of a transcript read, in the vocabulary both the API and the tool map from.</summary>
public sealed record AgentTranscriptResult
{
    /// <summary>How the read resolved.</summary>
    public required AgentTranscriptOutcome Outcome { get; init; }

    /// <summary>
    ///     A content-free <see cref="TranscriptAccessReasons"/> code when the read was denied. It carries
    ///     no name, thread, or task: a refusal must not disclose what it is refusing.
    /// </summary>
    public string? DenialCode { get; init; }

    /// <summary>The row that was read, when the read was allowed.</summary>
    public SubAgentSummary? Agent { get; init; }

    /// <summary>The normalized, reasoning-free transcript, when the read was allowed.</summary>
    public IReadOnlyList<PersistedMessage> Messages { get; init; } = [];
}

/// <summary>
///     The single trusted view of one conversation's agent hierarchy (#244), and the single place a
///     cross-agent transcript read is authorized.
/// </summary>
/// <remarks>
///     <para>
///         Both readers of the hierarchy go through here: the REST endpoints
///         (<c>/subagents</c>, <c>/agents/{id}/transcript</c>) and the in-agent
///         <c>GetAgentTranscript</c> tool. That is the point — the row a reader was told is readable is
///         the row that gets authorized and opened, with no second, subtly different derivation in
///         between that could drift from the first.
///     </para>
///     <para>
///         The service holds no state of its own; it is a projection over the live pool, the durable tab
///         index, and the store, and is cheap to construct per call site.
///     </para>
/// </remarks>
public sealed class AgentHierarchyService(
    MultiTurnAgentPool agentPool,
    WorkflowRunRegistry workflowRunRegistry,
    IConversationStore store,
    ILogger<AgentHierarchyService> logger,
    SubAgentScanCoverageCache scanCoverageCache)
{
    /// <summary>
    ///     Assembles one conversation's agent rows: the live sub-agent/workflow snapshot, unioned with the
    ///     durable tab index, projected through the collaboration hierarchy when it is enabled.
    /// </summary>
    /// <param name="threadId">The conversation whose hierarchy is being read.</param>
    /// <param name="viewerAgentId">
    ///     The agent the answer is for, or null for the conversation root (the human at the keyboard). It
    ///     only affects the viewer-scoped flags; it can never widen what the listing contains.
    /// </param>
    /// <param name="ct">Cancellation for the cold-path store lookup.</param>
    /// <returns>The rows, whether the thread exists at all, and the live collaboration when there is one.</returns>
    public async Task<(IReadOnlyList<SubAgentSummary> Rows, bool IsKnown, AgentCollaborationSetup? Collaboration)>
        BuildAsync(string threadId, string? viewerAgentId, CancellationToken ct)
    {
        var summaries = new List<SubAgentSummary>();
        var isLive = agentPool.TryGet(threadId, out var agent) && agent is not null;
        var loop = isLive ? agent as MultiTurnAgentLoop : null;

        // Agent-tool sub-agents (the historical /subagents contents) — LIVE-ONLY: they live on the main
        // conversation loop's SubAgentManager, so they're gone after a restart until the loop is rehydrated.
        if (loop?.SubAgentManager is { } subAgentManager)
        {
            summaries.AddRange(subAgentManager.ListAgents().Select(ToSummary));
        }

        // StartWorkflowAgent runs + their delegates. The live snapshot (when the WorkflowManager is present)
        // is write-through-persisted to a small on-disk index, and the response is the union of live ∪
        // persisted (live wins) — so completed workflow/delegate tabs SURVIVE A SERVER RESTART that evicts
        // the in-memory manager. Delegate transcripts already persist as subagent-{id} threads, so a
        // persisted tab replays read-only.
        var workflowTabs = new List<SubAgentSummary>();
        if (isLive && workflowRunRegistry.TryGet(threadId, out var workflowManager) && workflowManager is not null)
        {
            workflowTabs.AddRange(
                workflowManager.ListRuns()
                    .Select(r => new SubAgentSummary
                    {
                        AgentId = r.WorkflowId,
                        Kind = SubAgentSummary.WorkflowTabKind,
                        Name = r.Objective,
                        Template = "workflow",
                        Task = r.Objective,
                        Status = r.Status,
                        // The controller thread is conversation-scoped (workflow-{id}-{conversationId}); use the
                        // run's real ThreadId so the ⚙ tab opens the ACTUAL persisted thread, not a stale
                        // reconstruction. Fall back to the legacy shape only for a run with no scoped id.
                        ThreadId = r.ThreadId ?? $"workflow-{r.WorkflowId}",
                        LastActivityUtc = r.LastActivityUtc ?? r.StartedUtc,
                    })
            );

            foreach (var run in workflowManager.ListRuns())
            {
                workflowTabs.AddRange(workflowManager.ListRunDelegates(run.WorkflowId).Select(ToSummary));
            }
        }

        // Write-through: fold this live snapshot into the durable index (upsert, never deletes). The
        // Agent-tool rows join the index only once collaboration is on — persisting them unconditionally
        // would start surfacing restart-surviving sub-agent tabs in a host that never opted in.
        //
        // Stamp collaboration hierarchy metadata onto each row BEFORE it is written — not after, the way
        // it used to happen (see AgentHierarchyProjection.Project below, which still runs every call for
        // the viewer-scoped flags). A row persisted in its raw, unenriched shape carries no
        // AgentNodeId/AgentKind/CollaborationId/ancestry, so SubAgentSummary.ToNodeRecord() returns null
        // for it once the live node that used to back it is gone (e.g. after a restart the fresh
        // directory holds only the root), and a transcript read for a retained child then fails closed
        // with unknown_target even for its legitimate root ancestor. Enrich() only stamps the structural
        // fields (WithCollaboration), never the viewer-scoped IsCurrent/IsReadable pair, so nothing here
        // bakes in one reader's answer for every future one.
        //
        // Enrich() alone only ever touches a tab THIS conversation already knew about. An ordinary
        // descendant owned by a child's own SubAgentManager (a grandchild the model spawned through a
        // spawn of its own) has no tab here at all — it exists only in the shared directory snapshot —
        // so Enrich() never sees it and it never reaches the index. Project() below still surfaces it for
        // THIS live answer (via its own unmatched-node pass), which is why the gap only shows up after a
        // restart: PersistableRowsFor also folds in AgentHierarchyProjection.UnmatchedDescendantRows so
        // that same descendant is written through now, before any particular viewer is known, exactly
        // like Project would show it today.
        var persistable = loop?.Collaboration is null
            ? workflowTabs
            : PersistableRowsFor([.. workflowTabs, .. summaries], loop.Collaboration.Directory.Snapshot());
        if (persistable.Count > 0)
        {
            workflowRunRegistry.PersistTabs(threadId, persistable);
        }

        // Merge live tabs with the persisted index (live wins on Kind+AgentId), so a restart that
        // evicted the in-memory runs still surfaces them from disk.
        var merged = new Dictionary<(string Kind, string AgentId), SubAgentSummary>();

        // Ordinary Agent-tool children reconstructed from persisted SubAgentProvenance metadata. This is
        // the ONLY way such a child survives pool eviction/restart for a host that never enabled
        // collaboration — collaboration-off never persists Agent-tool rows into workflowRunRegistry's tab
        // index above (see the write-through gate), yet the child's transcript/provenance is still on
        // disk. Folded in first so a live row (added below) always wins on a match, restoring the
        // pre-#244 flat-listing contract.
        //
        // Gated on whether live state could EVER cover the ordinary persisted roster on its own — NOT on
        // the concrete loop type, and NOT on whether the manager happens to be empty right now (see
        // PRRT_kwDOOPysWM6V1mjj: `ListAgents().Count == 0` is not a stable coverage signal — a rehydrated
        // loop starts empty, recovers old children via this same scan, but the moment it spawns ONE new
        // child the manager stops looking empty and a per-call gate would silently drop every recovered
        // row on the next call, since ordinary collaboration-off rows are never write-through-persisted
        // above):
        //   - a live agent that is not a MultiTurnAgentLoop (Codex/Copilot CLI loops), or a
        //     MultiTurnAgentLoop built with no SubAgentManager at all — it can never own an Agent-tool
        //     child, so scanning on its behalf would only pay a store-wide cost on every hot poll for a
        //     roster it structurally cannot have. Skip.
        //   - a live MultiTurnAgentLoop with collaboration ON — the write-through above (or the enriched
        //     persisted WorkflowRunRegistry tabs a few lines down) already accounts for every child that
        //     matters. Skip.
        //   - everything else (no live agent at all, or a live collaboration-OFF MultiTurnAgentLoop with
        //     a SubAgentManager) — the persisted roster is relevant, but is resolved through
        //     GetOrScanPersistedSubAgentChildrenAsync below rather than scanned unconditionally: that
        //     helper caches the FIRST answer for this thread's process lifetime (via
        //     SubAgentScanCoverageCache, the one thing shared across the otherwise-fresh
        //     AgentHierarchyService instance built for every call site) and reuses it afterward, so a
        //     genuinely childless loop stops paying for a rescan on every 3-second poll, and a rehydrated
        //     loop's recovered roster survives every later poll — including the one right after it
        //     spawns a new child, when the manager stops looking empty.
        var subAgentRosterRelevant = !isLive || loop is { Collaboration: null, SubAgentManager: not null };
        if (subAgentRosterRelevant)
        {
            // The cache entry is keyed on this owner alongside threadId (see SubAgentScanCoverageCache's
            // remarks): the live SubAgentManager instance when one covers this thread, or the shared
            // NoLiveManager sentinel otherwise. A mode/provider switch, pool eviction+reopen, or restart
            // rehydration each construct a brand-new SubAgentManager, so the owner here changes and the
            // cache is invalidated automatically — no recreation call site needs to remember to do it.
            var owner = loop is { Collaboration: null, SubAgentManager: { } liveSubAgentManager }
                ? liveSubAgentManager
                : SubAgentScanCoverageCache.NoLiveManager;
            foreach (var node in await GetOrScanPersistedSubAgentChildrenAsync(threadId, owner, ct))
            {
                merged[(node.Kind, node.AgentId)] = node;
            }
        }

        foreach (var tab in workflowRunRegistry.GetPersistedTabs(threadId))
        {
            merged[(tab.Kind, tab.AgentId)] = tab;
        }

        foreach (var tab in workflowTabs.Concat(summaries))
        {
            merged[(tab.Kind, tab.AgentId)] = tab;
        }

        IReadOnlyList<SubAgentSummary> rows = [.. merged.Values];
        if (loop?.Collaboration is { } collaboration)
        {
            rows = AgentHierarchyProjection.Project(
                rows,
                collaboration.Directory.Snapshot(),
                viewerAgentId ?? collaboration.AgentId,
                collaboration.Options.TranscriptVisibility);
        }

        // A live conversation, or one with persisted tabs, always answers 200. Otherwise the
        // conversation is idle (evicted from the pool, or reopened but not yet messaged this session)
        // and has no children to project — but it may still be a KNOWN thread on disk. Distinguish
        // "known but idle with no sub-agents" (→ empty 200, the common case: a plain chat you reopened)
        // from a genuinely unknown thread (→ 404) by consulting the store. Without this, every idle
        // conversation with no sub-agents gets a spurious 404 and the client's sub-agent panel logs
        // "Failed to list sub-agents" on every 3s poll. The store is only touched on this cold path,
        // so the live hot path is unchanged.
        var isKnown = isLive
            || merged.Count > 0
            || await store.LoadMetadataAsync(threadId, ct) is not null;

        return (rows, isKnown, loop?.Collaboration);
    }

    /// <summary>
    ///     Reads one agent's transcript on behalf of <paramref name="viewerAgentId"/>, applying the
    ///     collaboration's transcript policy and excluding reasoning from what is returned.
    /// </summary>
    /// <remarks>
    ///     Authorization is the projection's own <c>isReadable</c> — the very flag the listing published
    ///     to this same reader — so there is no second decision here that could disagree with the first.
    /// </remarks>
    /// <param name="threadId">The conversation the hierarchy belongs to.</param>
    /// <param name="agentId">The target, by either identifier a row publishes (tab id or node id).</param>
    /// <param name="viewerAgentId">The reader, or null for the conversation root.</param>
    /// <param name="ct">Cancellation for the store reads.</param>
    public async Task<AgentTranscriptResult> ReadTranscriptAsync(
        string threadId,
        string agentId,
        string? viewerAgentId,
        CancellationToken ct)
    {
        var (rows, isKnown, collaboration) = await BuildAsync(threadId, viewerAgentId, ct);
        if (!isKnown)
        {
            return new AgentTranscriptResult { Outcome = AgentTranscriptOutcome.UnknownThread };
        }

        if (collaboration is null)
        {
            // The feature is off (or the loop is not live), so the hierarchy this read needs does not
            // exist. Reported as absence rather than refusal — there is nothing here to be refused.
            return new AgentTranscriptResult { Outcome = AgentTranscriptOutcome.CollaborationUnavailable };
        }

        var row = AgentHierarchyProjection.Find(rows, agentId);
        if (row is null || !row.IsReadable)
        {
            return new AgentTranscriptResult
            {
                Outcome = AgentTranscriptOutcome.Denied,
                DenialCode = DenialCode(row, collaboration, viewerAgentId, agentId),
            };
        }

        var messages = await store.LoadMessagesAsync(row.ThreadId, ct);
        return new AgentTranscriptResult
        {
            Outcome = AgentTranscriptOutcome.Allowed,
            Agent = row,
            Messages = TranscriptProjection.Normalize(messages, excludeReasoning: true),
        };
    }

    /// <summary>
    ///     The content-free reason a transcript read was refused. An agent this conversation has never
    ///     heard of is reported as an unknown target, which is also exactly what a caller outside the
    ///     collaboration is told about an agent it may not know exists.
    /// </summary>
    private static string DenialCode(
        SubAgentSummary? row,
        AgentCollaborationSetup collaboration,
        string? viewerAgentId,
        string agentId) =>
        row is null
            ? TranscriptAccessReasons.UnknownTarget
            : collaboration.Bundle
                .EvaluateTranscriptAccess(viewerAgentId ?? collaboration.AgentId, row.AgentNodeId ?? agentId)
                .Reason;

    /// <summary>
    ///     Every row write-through persistence should stamp for one snapshot: <paramref name="tabs"/>
    ///     enriched with their matching node's hierarchy metadata, plus a row for every node in
    ///     <paramref name="nodes"/> that none of those tabs already accounts for — an ordinary descendant
    ///     owned by a child's own <c>SubAgentManager</c>, invisible to this conversation's own tabs but
    ///     present in the shared directory (see the remarks at the call site in <see cref="BuildAsync"/>).
    /// </summary>
    /// <remarks>
    ///     The two halves can never collide: <see cref="AgentHierarchyProjection.Enrich"/> only ever
    ///     touches a tab already present in <paramref name="tabs"/>, and
    ///     <see cref="AgentHierarchyProjection.UnmatchedDescendantRows"/> only ever produces a row for a
    ///     node none of those tabs matched.
    /// </remarks>
    private static IReadOnlyList<SubAgentSummary> PersistableRowsFor(
        IReadOnlyList<SubAgentSummary> tabs,
        IReadOnlyList<AgentDirectoryEntry> nodes) =>
        [
            .. AgentHierarchyProjection.Enrich(tabs, nodes),
            .. AgentHierarchyProjection.UnmatchedDescendantRows(tabs, nodes),
        ];

    /// <summary>Projects one sub-agent snapshot (an Agent-tool spawn or a workflow delegate) to a tab row.</summary>
    private static SubAgentSummary ToSummary(SubAgentSnapshot s) =>
        new()
        {
            AgentId = s.AgentId,
            Kind = SubAgentSummary.SubAgentTabKind,
            Name = s.Name,
            Template = s.TemplateName,
            Task = s.Task,
            Status = s.Status.ToString().ToLowerInvariant(),
            ThreadId = s.ThreadId,
            LastActivityUtc = s.LastActivityUtc,
            EffectiveModelId = s.EffectiveModelId,
            EffectiveModelIntelligence = s.EffectiveModelIntelligence,
            ModelSelectionSource = s.ModelSelectionSource,
        };

    /// <summary>
    ///     Page size and total cap for the persisted sub-agent scan. <see cref="IConversationStore"/> has
    ///     no property index, so rebuilding a persisted child roster means scanning thread metadata; the
    ///     cap bounds the work on a long-lived store rather than scanning it unboundedly per request.
    /// </summary>
    private const int SubAgentScanPageSize = 200;
    private const int SubAgentScanMaxThreads = 2000;

    /// <summary>
    ///     Returns the persisted Agent-tool child roster for <paramref name="threadId"/>, scanning the
    ///     store at most once per (thread, <paramref name="owner"/>) pair for this process's lifetime
    ///     (see <see cref="SubAgentScanCoverageCache"/>). A cache hit — including a cached EMPTY roster
    ///     for a genuinely childless thread — never touches the store again; a miss (including one caused
    ///     by <paramref name="owner"/> no longer matching a previously-recorded owner, i.e. the live
    ///     manager was reset) runs <see cref="ScanPersistedSubAgentChildrenAsync"/> and records the result
    ///     only once it completes successfully, so a cancelled or failed attempt leaves the thread
    ///     uncached for the next caller to retry rather than poisoning the cache with a partial answer.
    /// </summary>
    /// <remarks>
    ///     PR #245 review — scan/delete interleaving: the store scan below can run concurrently with a
    ///     <c>ConversationsController.Delete</c> for the SAME <paramref name="threadId"/>. Without a
    ///     sequencing signal, a delete that lands WHILE this scan is in flight (after the miss above, before
    ///     the record below) would otherwise be silently undone: the scan already has its answer in hand
    ///     from before the delete and would write it back regardless, resurrecting the deleted thread's
    ///     roster in the cache. <see cref="SubAgentScanCoverageCache.CaptureGeneration"/> /
    ///     <see cref="SubAgentScanCoverageCache.RecordRecovered"/>'s generation parameter close this: the
    ///     generation is captured immediately before the scan starts, and the writeback is rejected if
    ///     <see cref="SubAgentScanCoverageCache.Forget"/> ran for this thread in the meantime (bumping the
    ///     generation). The scanned rows are still returned to THIS caller either way — only the shared
    ///     cache entry is affected — since this caller's request legitimately started before the delete.
    /// </remarks>
    private async Task<IReadOnlyList<SubAgentSummary>> GetOrScanPersistedSubAgentChildrenAsync(
        string threadId,
        object owner,
        CancellationToken ct)
    {
        if (scanCoverageCache.TryGetRecovered(threadId, owner, out var cached))
        {
            return cached;
        }

        var generation = scanCoverageCache.CaptureGeneration(threadId);
        var scanned = await ScanPersistedSubAgentChildrenAsync(threadId, ct);
        scanCoverageCache.RecordRecovered(threadId, owner, scanned, generation);
        return scanned;
    }

    /// <summary>
    ///     Bounded, parent-filtered reconstruction of ordinary Agent-tool children from persisted
    ///     <see cref="SubAgentProvenance"/> metadata — the pre-#244 flat-listing contract this restores
    ///     (see the call site in <see cref="BuildAsync"/>). Scoped to direct children of
    ///     <paramref name="threadId"/> only: unlike the recursive descendant reader
    ///     (<c>ConversationsController.BuildDescendantTreeAsync</c>), the flat listing never needs the
    ///     whole persisted graph, just this one conversation's own children.
    /// </summary>
    private async Task<IReadOnlyList<SubAgentSummary>> ScanPersistedSubAgentChildrenAsync(
        string threadId,
        CancellationToken ct)
    {
        var found = new List<SubAgentSummary>();
        var scanned = 0;

        while (scanned < SubAgentScanMaxThreads)
        {
            var page = await store.ListThreadsAsync(SubAgentScanPageSize, scanned, ct) ?? [];
            if (page.Count == 0)
            {
                return found;
            }

            scanned += page.Count;
            foreach (var metadata in page)
            {
                var node = SubAgentProvenance.TryProject(metadata, threadId);
                if (node is not null)
                {
                    found.Add(node);
                }
            }

            if (page.Count < SubAgentScanPageSize)
            {
                return found;
            }
        }

        logger.LogWarning(
            "Sub-agent scan for {ThreadId} stopped at the {MaxThreads}-thread cap; "
                + "children persisted beyond that point are not listed.",
            threadId,
            SubAgentScanMaxThreads);
        return found;
    }
}
