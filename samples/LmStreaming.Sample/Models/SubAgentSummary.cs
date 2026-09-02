using System.Text.Json.Serialization;
using AchieveAi.LmDotnetTools.LmCore.Models;
using AchieveAi.LmDotnetTools.LmMultiTurn.Collaboration;
// The record publishes an AgentKind *property* (the wire name the client reads), which would shadow
// the same-named enum in every expression below. The alias keeps both usable without renaming either.
using CollaborationAgentKind = AchieveAi.LmDotnetTools.LmMultiTurn.Collaboration.AgentKind;

namespace LmStreaming.Sample.Models;

/// <summary>
/// Presentation-only summary of a single sub-agent spawned by a conversation's parent agent.
/// Projected from <c>SubAgentManager.ListAgents()</c> snapshots for the read-only
/// <c>GET /api/conversations/{threadId}/subagents</c> endpoint so the client can display a
/// conversation's children without touching sub-agent execution (WI #194).
/// </summary>
/// <remarks>
/// The collaboration members below were added by #244 and are <em>additive and optional</em>: a row
/// written by a pre-#244 build still deserializes, and a host that never enabled collaboration keeps
/// emitting exactly the pre-#244 field set. That is why none of them is <c>required</c>, and why the
/// viewer-scoped flags are omitted from the wire when false.
/// </remarks>
public sealed record SubAgentSummary
{
    /// <summary>Tab kind for a node the model spawned directly, or a workflow's own delegate.</summary>
    public const string SubAgentTabKind = "subagent";

    /// <summary>Tab kind for a workflow run, whose controller loop is surfaced as its tab.</summary>
    public const string WorkflowTabKind = "workflow";

    /// <summary>Status given to a row that was still in flight when its host stopped.</summary>
    public const string InterruptedStatus = "interrupted";

    /// <summary>Stable id assigned to the sub-agent at spawn time.</summary>
    public required string AgentId { get; init; }

    /// <summary>
    ///     What kind of child this row represents: <c>subagent</c> (an Agent-tool spawn, the default) or
    ///     <c>workflow</c> (a StartWorkflowAgent run whose isolated controller loop is surfaced as a tab).
    /// </summary>
    public string Kind { get; init; } = SubAgentTabKind;

    /// <summary>Caller-supplied display name, or null when the spawn provided none.</summary>
    public string? Name { get; init; }

    /// <summary>
    ///     Name of the template the sub-agent was spawned from.
    /// </summary>
    /// <remarks>
    ///     Kept permanently as the backward-compatible alias of <see cref="AgentType"/> — the two always
    ///     carry the same value. Every pre-#244 client and every persisted index file reads this name, so
    ///     it is a stable part of the contract rather than migration scaffolding to be removed later.
    /// </remarks>
    public required string Template { get; init; }

    /// <summary>The task prompt the sub-agent was dispatched with.</summary>
    public required string Task { get; init; }

    /// <summary>Lifecycle status, lower-cased (e.g. <c>running</c>, <c>completed</c>).</summary>
    public required string Status { get; init; }

    /// <summary>The sub-agent's own conversation thread id.</summary>
    public required string ThreadId { get; init; }

    /// <summary>UTC timestamp of the sub-agent's last observed activity, or null if none yet.</summary>
    public DateTimeOffset? LastActivityUtc { get; init; }

    /// <summary>Persisted parent thread id; required on recursive-tree nodes.</summary>
    public string? ParentThreadId { get; init; }

    /// <summary>Distance from the requested root in the recursive descendant graph.</summary>
    public int? Depth { get; init; }

    /// <summary>UTC instant the sub-agent reached a terminal status.</summary>
    public DateTimeOffset? TerminalAtUtc { get; init; }

    /// <summary>Machine-readable failure reason, when known.</summary>
    public string? FailureCode { get; init; }

    /// <summary>The concrete model used to build the child provider after all routing precedence.</summary>
    public string? EffectiveModelId { get; init; }

    /// <summary>The intelligence tier that selected the effective model, when selection was tier-based.</summary>
    public int? EffectiveModelIntelligence { get; init; }

    /// <summary>Stable source label such as parent, spawn-model, spawn-tier, template-model, or template-tier.</summary>
    public string? ModelSelectionSource { get; init; }

    /// <summary>Normalized effort requested before provider capability shaping.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RequestedReasoningEffort { get; init; }

    /// <summary>Provider capability-shaped effort placed on the request, or null when omitted.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ShapedReasoningEffort { get; init; }

    /// <summary>
    ///     Schema version of the persisted row, shared with <see cref="CollaborationNodeRecord"/> so the
    ///     index file and the collaboration node record cannot drift apart silently.
    /// </summary>
    /// <remarks>
    ///     Rows written before #244 carry no version and deserialize as <c>0</c>, which is how a reader
    ///     tells "old but valid" from "written by a newer build". Every member from here down is omitted
    ///     from the wire when it has nothing to say, so a host with collaboration switched off emits the
    ///     pre-#244 field set exactly — additive means additive, not "eleven new nulls".
    /// </remarks>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int SchemaVersion { get; init; }

    /// <summary>Collaboration this node belongs to, or null when collaboration is not enabled.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CollaborationId { get; init; }

    /// <summary>
    ///     The identifier this agent is known by <em>inside the collaboration</em> — the vocabulary
    ///     <see cref="ParentAgentId"/> and <see cref="AncestorAgentIds"/> are expressed in.
    /// </summary>
    /// <remarks>
    ///     Equal to <see cref="AgentId"/> for every agent the model spawned. It differs for a workflow
    ///     tab, whose <see cref="AgentId"/> is the workflow handle the tab has always been addressed by
    ///     while its collaboration node is the controller derived from that handle. Publishing both is
    ///     what lets a client link a delegate to the workflow tab above it without either identifier
    ///     having to change meaning.
    /// </remarks>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AgentNodeId { get; init; }

    /// <summary>
    ///     Template the agent was spawned from, under the collaboration's own name for it. Always equal to
    ///     <see cref="Template"/>; both are published so neither vocabulary has to win.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AgentType { get; init; }

    /// <summary>Structural kind of the node (<c>Root</c>, <c>SubAgent</c>, <c>WorkflowController</c>, <c>WorkflowDelegate</c>).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AgentKind { get; init; }

    /// <summary>Short statement of what this agent is for.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Role { get; init; }

    /// <summary>Longer guidance on when to contact this agent.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; init; }

    /// <summary>The agent directly above this one, or null for the conversation root.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ParentAgentId { get; init; }

    /// <summary>Every agent above this one, root first, excluding this agent.</summary>
    /// <remarks>Null — not empty — when the hierarchy is unknown, which is how the root's genuinely
    /// empty ancestry stays distinguishable from a row that never had any.</remarks>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? AncestorAgentIds { get; init; }

    /// <summary>How many hierarchy levels lie between the root and this agent.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? StructuralDepth { get; init; }

    /// <summary>How much delegation budget has been spent reaching this agent.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? DelegationDepth { get; init; }

    /// <summary>
    ///     Whether the agent is still addressable. Null when collaboration is off; false for a row
    ///     recovered from the index after the agent left memory.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IsLive { get; init; }

    /// <summary>Whether this row is the agent the reader is currently looking at.</summary>
    /// <remarks>
    ///     Viewer-scoped, so it is recomputed on every projection and never written to the index — a
    ///     persisted "you" would be a lie the moment a different reader loaded the file.
    /// </remarks>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IsCurrent { get; init; }

    /// <summary>Whether the reader is allowed to fetch this agent's transcript.</summary>
    /// <remarks>Viewer-scoped for the same reason as <see cref="IsCurrent"/>.</remarks>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IsReadable { get; init; }

    /// <summary>The tab kind a structural agent kind is surfaced under.</summary>
    /// <remarks>
    ///     Workflow controllers own a run tab; everything else (including a workflow's own delegates) is a
    ///     plain sub-agent tab. Keeping this mapping in one place is what stops the merge key
    ///     <c>(Kind, AgentId)</c> from splitting one agent across two rows.
    /// </remarks>
    public static string TabKindFor(CollaborationAgentKind kind) =>
        kind == CollaborationAgentKind.WorkflowController ? WorkflowTabKind : SubAgentTabKind;

    /// <summary>
    ///     Copies one directory entry's hierarchy metadata onto this row, leaving every presentation field
    ///     (task, thread, model routing) untouched.
    /// </summary>
    /// <param name="entry">The live or retained snapshot describing this same agent.</param>
    /// <exception cref="ArgumentNullException"><paramref name="entry"/> is null.</exception>
    public SubAgentSummary WithCollaboration(AgentDirectoryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        return this with
        {
            SchemaVersion = CollaborationNodeRecord.CurrentSchemaVersion,
            CollaborationId = entry.CollaborationId,
            AgentNodeId = entry.AgentId,
            AgentType = entry.AgentType ?? AgentType ?? Template,
            AgentKind = entry.Kind.ToString(),
            Role = entry.Role,
            Description = entry.Description,
            ParentAgentId = entry.ParentAgentId,
            AncestorAgentIds = [.. entry.AncestorAgentIds],
            StructuralDepth = entry.StructuralDepth,
            DelegationDepth = entry.DelegationDepth,
            IsLive = entry.IsLive,
        };
    }

    /// <summary>
    ///     Builds a row for an agent that exists in the hierarchy but has no live tab of its own — a
    ///     descendant owned by another manager, or a node recovered after a restart.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="entry"/> is null.</exception>
    public static SubAgentSummary FromDirectoryEntry(AgentDirectoryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var template = entry.AgentType ?? entry.Kind.ToString();
        return new SubAgentSummary
        {
            AgentId = entry.AgentId,
            Kind = TabKindFor(entry.Kind),
            Name = entry.Name,
            Template = template,
            // The role is the closest thing to a task a directory entry carries; the actual spawn prompt
            // belongs to the manager that owns the agent and is deliberately not republished here.
            Task = entry.Role,
            Status = entry.Status,
            ThreadId = ThreadIdFor(entry),
        }.WithCollaboration(entry);
    }

    /// <summary>
    ///     Reconciles a row that came back from storage into what it honestly is: a retained snapshot of
    ///     an agent that is no longer running here.
    /// </summary>
    /// <remarks>
    ///     A row written while the agent was <c>running</c> or <c>queued</c> describes a state that ended
    ///     the moment the host stopped, so it is reported as <see cref="InterruptedStatus"/> rather than
    ///     as work that is still happening. Liveness is only asserted for rows that carry collaboration
    ///     metadata — a pre-#244 row never claimed to know, and inventing <c>false</c> for it would add a
    ///     field the legacy contract does not have. The viewer-scoped flags are cleared because the reader
    ///     who wrote them is not the reader asking now.
    /// </remarks>
    public SubAgentSummary AsRetained()
    {
        var isInFlight =
            string.Equals(Status, AgentCollaborationStatuses.Running, StringComparison.OrdinalIgnoreCase)
            || string.Equals(Status, AgentCollaborationStatuses.Queued, StringComparison.OrdinalIgnoreCase);

        return this with
        {
            Status = isInFlight ? InterruptedStatus : Status,
            IsLive = CollaborationId is null ? null : false,
            IsCurrent = false,
            IsReadable = false,
        };
    }

    /// <summary>Projects this row into the shared, versioned persisted node shape.</summary>
    /// <remarks>
    ///     The sample's index and the collaboration core converge here: whatever the index stores, it can
    ///     always be read back as the same <see cref="CollaborationNodeRecord"/> the rest of the system
    ///     speaks. Returns null for a row that carries no collaboration metadata (collaboration disabled,
    ///     or a pre-#244 persisted row), because inventing a hierarchy for it would be a fabrication.
    /// </remarks>
    public CollaborationNodeRecord? ToNodeRecord()
    {
        if (string.IsNullOrEmpty(CollaborationId) || AgentKind is null)
        {
            return null;
        }

        return new CollaborationNodeRecord
        {
            AgentId = AgentNodeId ?? AgentId,
            CollaborationId = CollaborationId,
            Name = Name ?? AgentId,
            ParentAgentId = ParentAgentId,
            AncestorAgentIds = AncestorAgentIds ?? [],
            Kind = Enum.Parse<CollaborationAgentKind>(AgentKind),
            Role = Role ?? string.Empty,
            Description = Description ?? string.Empty,
            AgentType = AgentType,
            StructuralDepth = StructuralDepth ?? 0,
            DelegationDepth = DelegationDepth ?? 0,
            Status = Status,
        };
    }

    /// <summary>Reserved thread-id prefix for a sub-agent's own transcript.</summary>
    private const string SubAgentThreadPrefix = "subagent-";

    /// <summary>Reserved thread-id prefix for a workflow controller's own transcript.</summary>
    private const string WorkflowThreadPrefix = "workflow-";

    /// <summary>
    ///     Every reserved thread-id prefix an AGENT owns — the one spelling of that set, for callers
    ///     that need the prefixes themselves rather than a yes/no answer about one id.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The conversation sidebar needs the set, not the predicate: its exclusion is pushed into
    ///         the store as a <c>ConversationListOptions.ExcludedThreadIdPrefixes</c> so the page is
    ///         trimmed by the query rather than after it, and a C# delegate cannot cross into SQL. It
    ///         is exposed here rather than restated at the controller so the store-side exclusion and
    ///         <see cref="IsAgentOwnedThreadId"/> cannot drift: add a third agent-owned id space and
    ///         both move together, because the predicate below is implemented in terms of THIS list.
    ///     </para>
    ///     <para>
    ///         Ordinal comparison, matching <see cref="IsAgentOwnedThreadId"/> and the store's own
    ///         prefix test. A thread id is an opaque token, never a culture-sensitive string.
    ///     </para>
    /// </remarks>
    public static IReadOnlyList<string> AgentOwnedThreadIdPrefixes { get; } =
    [SubAgentThreadPrefix, WorkflowThreadPrefix];

    /// <summary>
    ///     True when <paramref name="threadId"/> is a thread an AGENT owns rather than a conversation a
    ///     human started — a sub-agent's or a workflow controller's transcript.
    /// </summary>
    /// <remarks>
    ///     These threads are the sample's reserved id space, and they are governed differently from an
    ///     ordinary conversation: they never appear in the conversation sidebar, and who may read one is
    ///     the collaboration's decision (#244) rather than "whoever knows the id". Naming the convention
    ///     once keeps the listing filter and the route guards from drifting apart — which is why this
    ///     is implemented over <see cref="AgentOwnedThreadIdPrefixes"/> rather than repeating the two
    ///     prefixes: the sidebar's store-side exclusion and this route guard are then, by construction,
    ///     asking about the same set.
    /// </remarks>
    public static bool IsAgentOwnedThreadId(string? threadId) =>
        threadId is not null
        && AgentOwnedThreadIdPrefixes.Any(prefix => threadId.StartsWith(prefix, StringComparison.Ordinal));

    /// <summary>
    ///     The thread a hierarchy node's transcript lives under, following the ids the rest of the sample
    ///     already forms: the root's own id for the root, <c>subagent-{scope}-{agent-N}</c> for agents
    ///     (#705, scope = the root conversation's digest — the root is the first ancestor, or the parent
    ///     itself one level down), <c>workflow-*</c> for controllers.
    /// </summary>
    private static string ThreadIdFor(AgentDirectoryEntry entry)
    {
        if (entry.Kind == CollaborationAgentKind.Root)
        {
            return entry.AgentId;
        }

        var rootThreadId = entry.AncestorAgentIds.Length > 0 ? entry.AncestorAgentIds[0] : entry.ParentAgentId;
        return SubAgentThreadIds.For(rootThreadId, entry.AgentId);
    }
}

/// <summary>Versioned recursive descendant graph response.</summary>
public sealed record SubAgentTreeResponse(int SchemaVersion, IReadOnlyList<SubAgentSummary> Nodes);
