using AchieveAi.LmDotnetTools.LmMultiTurn.Collaboration;
using AchieveAi.LmDotnetTools.LmWorkflow.Collaboration;
using LmStreaming.Sample.Models;
using LmStreaming.Sample.Persistence;

namespace LmStreaming.Sample.Services;

/// <summary>
///     Projects one conversation's whole agent hierarchy (#244) into the tab rows the client renders.
/// </summary>
/// <remarks>
///     <para>
///         Pure and static on purpose. Every reader of the hierarchy — the <c>/subagents</c> listing, the
///         transcript endpoint, and the <c>GetAgentTranscript</c> tool — must agree on who exists, what
///         they are called, and who may read whom. A function of two snapshots plus a viewer is a
///         projection that can be tested as a truth table, and, more importantly, one that cannot drift
///         between those three call sites because there is only one of it.
///     </para>
///     <para>
///         <b>Two sources, one row each.</b> The <em>tabs</em> are what the sample already knew about:
///         the live sub-agent/workflow snapshot unioned with the durable index. The <em>nodes</em> are the
///         collaboration directory, which additionally sees agents owned by some other manager deeper in
///         the tree. A tab that matches a node is enriched with that node's hierarchy metadata (live wins
///         — the directory is the fresher of the two); a node with no tab becomes a row of its own; a tab
///         with no node is passed through completely untouched, which is what keeps a host that never
///         enabled collaboration on exactly its pre-#244 behaviour.
///     </para>
///     <para>
///         <b>The viewer-scoped flags are computed here and nowhere else.</b> "Is this me" and "may I read
///         this" are answered per reader, so they are never read back from storage and never inherited
///         from a previous poll.
///     </para>
/// </remarks>
public static class AgentHierarchyProjection
{
    /// <summary>Builds the rows one viewer should see for a conversation.</summary>
    /// <param name="tabs">The live ∪ persisted tab rows the sample already assembled.</param>
    /// <param name="nodes">The collaboration directory snapshot (live and retained entries).</param>
    /// <param name="viewerAgentId">The agent the answer is for; unknown ids simply see nothing readable.</param>
    /// <param name="visibility">The collaboration's configured transcript visibility.</param>
    /// <exception cref="ArgumentNullException"><paramref name="tabs"/> or <paramref name="nodes"/> is null.</exception>
    public static IReadOnlyList<SubAgentSummary> Project(
        IReadOnlyList<SubAgentSummary> tabs,
        IReadOnlyList<AgentDirectoryEntry> nodes,
        string? viewerAgentId,
        TranscriptVisibilityMode visibility
    )
    {
        ArgumentNullException.ThrowIfNull(tabs);
        ArgumentNullException.ThrowIfNull(nodes);

        var byAgentId = new Dictionary<string, AgentDirectoryEntry>(StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            byAgentId[node.AgentId] = node;
        }

        var viewer = viewerAgentId is null ? null : byAgentId.GetValueOrDefault(viewerAgentId);
        var matched = new HashSet<string>(StringComparer.Ordinal);
        var rows = new List<SubAgentSummary>(tabs.Count + nodes.Count);

        foreach (var tab in tabs)
        {
            var node = byAgentId.GetValueOrDefault(NodeIdFor(tab));
            if (node is not null)
            {
                _ = matched.Add(node.AgentId);
            }

            // An unresolved provenance placeholder (only the parent link was ever stamped — see
            // SubAgentProvenance.Build) carries no real Name/Template of its own. When the live directory
            // has a node for the same id, that node is a strictly better source of identity than the
            // placeholder: prefer building the row from it entirely, rather than layering the node's
            // hierarchy metadata onto a tab whose own Name/Template WithCollaboration deliberately never
            // touches (so the placeholder's blanks would otherwise survive into the presented row).
            var row =
                node is null ? tab
                : IsUnresolvedPlaceholder(tab) ? SubAgentSummary.FromDirectoryEntry(node)
                : tab.WithCollaboration(node);

            // A tab with no live node may still describe a collaboration agent: the durable index keeps
            // the hierarchy metadata, so a retained row can be authorized from its own persisted shape.
            rows.Add(WithViewerFlags(row, node ?? row.ToNodeRecord()?.ToEntry(), viewer, visibility));
        }

        foreach (var row in UnmatchedDescendantRows(tabs, nodes))
        {
            // The row was built by FromDirectoryEntry(node), so its AgentNodeId is exactly that node's
            // AgentId — the lookup below always resolves it back to the same node this method skipped.
            var target = byAgentId.GetValueOrDefault(row.AgentNodeId ?? row.AgentId);
            rows.Add(WithViewerFlags(row, target, viewer, visibility));
        }

        return rows;
    }

    /// <summary>
    ///     The nodes in <paramref name="nodes"/> that no tab in <paramref name="tabs"/> already accounts
    ///     for, each as a row of its own — an ordinary descendant owned by another manager's
    ///     <c>SubAgentManager</c> (this conversation's own tabs cannot see it, only the shared directory
    ///     can), or, after a restart, any node the live snapshot no longer explains. The root (the
    ///     conversation itself, not one of its children) and a workflow controller with no run left to
    ///     speak for it (its tab id and transcript belong to the run that produced it, not to a formula)
    ///     are never invented a row.
    /// </summary>
    /// <remarks>
    ///     Shared by <see cref="Project"/> (the live, per-viewer answer) and by whatever persists the
    ///     durable tab index (see the write-through in <c>AgentHierarchyService.BuildAsync</c>), so a
    ///     descendant that would appear in today's live listing is also written through BEFORE any
    ///     particular viewer is known — mirroring why <see cref="Enrich"/> exists standalone. Viewer-scoped
    ///     flags are never set here; a caller that needs them (<see cref="Project"/>) computes them
    ///     afterward, per reader.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="tabs"/> or <paramref name="nodes"/> is null.</exception>
    public static IReadOnlyList<SubAgentSummary> UnmatchedDescendantRows(
        IReadOnlyList<SubAgentSummary> tabs,
        IReadOnlyList<AgentDirectoryEntry> nodes
    )
    {
        ArgumentNullException.ThrowIfNull(tabs);
        ArgumentNullException.ThrowIfNull(nodes);

        // Every id a tab already accounts for. Includes an entry for a tab whose NodeIdFor names no real
        // node in `nodes` at all — harmless, since the loop below only ever tests membership for an
        // actual node.AgentId, which by construction is exactly what a real match would have added here.
        var matched = new HashSet<string>(tabs.Select(NodeIdFor), StringComparer.Ordinal);
        var rows = new List<SubAgentSummary>();

        foreach (var node in nodes)
        {
            if (matched.Contains(node.AgentId) || node.Kind is AgentKind.Root or AgentKind.WorkflowController)
            {
                continue;
            }

            rows.Add(SubAgentSummary.FromDirectoryEntry(node));
        }

        return rows;
    }

    /// <summary>
    ///     Stamps each tab's collaboration hierarchy metadata (<see cref="SubAgentSummary.WithCollaboration"/>)
    ///     from a matching live node, leaving a tab with no matching node untouched.
    /// </summary>
    /// <remarks>
    ///     The structural half of what <see cref="Project"/> does, exposed standalone so a caller can run
    ///     it BEFORE persistence — before <see cref="Project"/> is called, before any particular reader is
    ///     even known. A row written to the durable index in its raw, unenriched shape carries no
    ///     <c>CollaborationId</c>/<c>AgentKind</c>/ancestry, so <see cref="SubAgentSummary.ToNodeRecord"/>
    ///     returns null for it after the live node that used to back it is gone (a restart, or the owning
    ///     manager evicted) — which is exactly the case <see cref="Project"/> relies on that fallback for.
    ///     Deliberately does not touch <see cref="SubAgentSummary.IsCurrent"/> or
    ///     <see cref="SubAgentSummary.IsReadable"/>: those are viewer-scoped and must always be computed
    ///     fresh, per request, by <see cref="Project"/> — never baked in for whichever reader happened to
    ///     trigger a persist.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="tabs"/> or <paramref name="nodes"/> is null.</exception>
    public static IReadOnlyList<SubAgentSummary> Enrich(
        IReadOnlyList<SubAgentSummary> tabs,
        IReadOnlyList<AgentDirectoryEntry> nodes
    )
    {
        ArgumentNullException.ThrowIfNull(tabs);
        ArgumentNullException.ThrowIfNull(nodes);

        var byAgentId = new Dictionary<string, AgentDirectoryEntry>(StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            byAgentId[node.AgentId] = node;
        }

        return
        [
            .. tabs.Select(tab =>
                byAgentId.TryGetValue(NodeIdFor(tab), out var node) ? tab.WithCollaboration(node) : tab
            ),
        ];
    }

    /// <summary>
    ///     Finds the projected row for <paramref name="agentId"/>, accepting either identifier the row
    ///     publishes: its tab id or its collaboration node id (see <see cref="SubAgentSummary.AgentNodeId"/>).
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="rows"/> is null.</exception>
    public static SubAgentSummary? Find(IReadOnlyList<SubAgentSummary> rows, string agentId)
    {
        ArgumentNullException.ThrowIfNull(rows);

        return rows.FirstOrDefault(row =>
            string.Equals(row.AgentId, agentId, StringComparison.Ordinal)
            || string.Equals(row.AgentNodeId, agentId, StringComparison.Ordinal)
        );
    }

    /// <summary>
    ///     The collaboration node id a tab row corresponds to. A workflow tab is addressed by the
    ///     workflow handle, while its node is the controller composed from that handle — asking
    ///     LmWorkflow to compose it keeps the two vocabularies joined by its own rule rather than by a
    ///     string literal copied over here.
    /// </summary>
    private static string NodeIdFor(SubAgentSummary tab) =>
        tab.Kind == SubAgentSummary.WorkflowTabKind
            ? WorkflowCollaboration.ComposeControllerAgentId(tab.AgentId)
            : tab.AgentId;

    /// <summary>
    ///     A tab produced from <see cref="SubAgentProvenance.Build"/> with no resolved snapshot — only the
    ///     parent link was stamped, so <c>Name</c> was never set and <c>Template</c> fell back to the
    ///     sentinel. Real tabs (live snapshots, retained rows, persisted workflow tabs) always carry a
    ///     resolved <c>Name</c>.
    /// </summary>
    private static bool IsUnresolvedPlaceholder(SubAgentSummary tab) =>
        tab.Name is null && tab.Template == SubAgentProvenance.UnknownTemplate;

    /// <summary>
    ///     Answers "is this me" and "may I read this" for one row, or leaves both unclaimed when the row
    ///     carries no collaboration identity at all (a pre-#244 or collaboration-off row).
    /// </summary>
    private static SubAgentSummary WithViewerFlags(
        SubAgentSummary row,
        AgentDirectoryEntry? target,
        AgentDirectoryEntry? viewer,
        TranscriptVisibilityMode visibility
    )
    {
        if (target is null)
        {
            return row;
        }

        return row with
        {
            IsCurrent = viewer is not null && string.Equals(target.AgentId, viewer.AgentId, StringComparison.Ordinal),
            IsReadable = TranscriptVisibilityPolicy.Evaluate(viewer, target, visibility).IsAllowed,
        };
    }
}
