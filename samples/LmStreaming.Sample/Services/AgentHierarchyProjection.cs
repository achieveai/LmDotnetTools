using AchieveAi.LmDotnetTools.LmMultiTurn.Collaboration;
using AchieveAi.LmDotnetTools.LmWorkflow.Collaboration;
using LmStreaming.Sample.Models;

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
        TranscriptVisibilityMode visibility)
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

            var row = node is null ? tab : tab.WithCollaboration(node);

            // A tab with no live node may still describe a collaboration agent: the durable index keeps
            // the hierarchy metadata, so a retained row can be authorized from its own persisted shape.
            rows.Add(WithViewerFlags(row, node ?? row.ToNodeRecord()?.ToEntry(), viewer, visibility));
        }

        foreach (var node in nodes)
        {
            // The root is the conversation itself, not one of its children. A workflow controller's tab
            // id and transcript thread belong to the run that produced it, so a controller with no run
            // left to speak for it is skipped rather than given an invented thread to open.
            if (matched.Contains(node.AgentId)
                || node.Kind is AgentKind.Root or AgentKind.WorkflowController)
            {
                continue;
            }

            rows.Add(WithViewerFlags(SubAgentSummary.FromDirectoryEntry(node), node, viewer, visibility));
        }

        return rows;
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
            || string.Equals(row.AgentNodeId, agentId, StringComparison.Ordinal));
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
    ///     Answers "is this me" and "may I read this" for one row, or leaves both unclaimed when the row
    ///     carries no collaboration identity at all (a pre-#244 or collaboration-off row).
    /// </summary>
    private static SubAgentSummary WithViewerFlags(
        SubAgentSummary row,
        AgentDirectoryEntry? target,
        AgentDirectoryEntry? viewer,
        TranscriptVisibilityMode visibility)
    {
        if (target is null)
        {
            return row;
        }

        return row with
        {
            IsCurrent = viewer is not null
                && string.Equals(target.AgentId, viewer.AgentId, StringComparison.Ordinal),
            IsReadable = TranscriptVisibilityPolicy.Evaluate(viewer, target, visibility).IsAllowed,
        };
    }
}
