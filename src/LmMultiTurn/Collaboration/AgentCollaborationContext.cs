using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.LmMultiTurn.Collaboration;

/// <summary>
/// What kind of node an agent is within a collaboration hierarchy.
/// </summary>
/// <remarks>
/// This is structural, not behavioural: it tells a reader of the directory <em>why</em> a node exists,
/// which is what makes an orchestration tree legible in <c>GetAgents</c> output. It is never used as a
/// routing key.
/// <para>
/// The attribute-scoped converter pins the wire shape to the member names, so a persisted hierarchy row
/// stays readable and does not silently change meaning if a member is ever inserted.
/// </para>
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AgentKind
{
    /// <summary>The single agent that owns the collaboration. Depth 0 on both axes.</summary>
    Root,

    /// <summary>An ordinary spawned sub-agent. Costs one hop of delegation budget.</summary>
    SubAgent,

    /// <summary>
    /// A workflow controller. Visible as a hierarchy node but a zero-cost delegation hop, so
    /// orchestration structure appears in the directory without spending delegation budget.
    /// </summary>
    WorkflowController,

    /// <summary>An ordinary agent a workflow controller delegated to. Costs one hop.</summary>
    WorkflowDelegate,
}

/// <summary>
/// One agent's immutable place in a collaboration: who it is, who is above it, and how much
/// delegation budget it has already spent.
/// </summary>
/// <remarks>
/// <para>
/// Like <see cref="AgentLineage"/>, this is fixed for the life of an agent and supplied once at
/// construction rather than recomputed per run — and for the same reason: it has to travel intact
/// through every composition root that can build an agent, and loose parameters are how a field
/// quietly gets dropped. A restart reuses the identical context, which is what keeps a restarted
/// agent addressable at the same identity.
/// </para>
/// <para>
/// <see cref="AncestorAgentIds"/> is precomputed at spawn rather than walked at query time. Transcript
/// authorization is a containment check against it, so precomputing turns every such check into an
/// O(depth) scan of an immutable array instead of a walk through mutable shared directory state that
/// could change mid-walk.
/// </para>
/// <para>
/// This type is independent of every optional lifecycle service. An agent has a place in the
/// hierarchy whether or not anything is publishing, persisting, or gating.
/// </para>
/// </remarks>
public sealed record AgentCollaborationContext
{
    /// <summary>Shortest allowed role, in Unicode scalar values.</summary>
    public const int MinRoleLength = 1;

    /// <summary>
    /// Longest allowed role, in Unicode scalar values. Role is collaboration-visible metadata and
    /// therefore a disclosure surface, so it is bounded rather than free-form.
    /// </summary>
    public const int MaxRoleLength = 80;

    /// <summary>Shortest allowed description, in Unicode scalar values.</summary>
    public const int MinDescriptionLength = 1;

    /// <summary>Longest allowed description, in Unicode scalar values.</summary>
    public const int MaxDescriptionLength = 200;

    /// <summary>
    /// Identifier of the collaboration this agent belongs to. Scoped to the root thread, so no second
    /// persisted identifier is minted.
    /// </summary>
    public required string CollaborationId { get; init; }

    /// <summary>Canonical, stable identifier of this agent. The only safe addressing key.</summary>
    public required string AgentId { get; init; }

    /// <summary>The agent directly above this one, or null for the root.</summary>
    public string? ParentAgentId { get; init; }

    /// <summary>
    /// Every agent above this one, root first, excluding this agent. Empty for the root.
    /// </summary>
    public ImmutableArray<string> AncestorAgentIds { get; init; } = [];

    /// <summary>
    /// How many hierarchy levels lie between the root and this agent. Counts every node, including
    /// zero-cost workflow controllers, so it describes shape rather than budget.
    /// </summary>
    public int StructuralDepth { get; init; }

    /// <summary>
    /// How much delegation budget has been spent reaching this agent. Root is 0, and a workflow
    /// controller inherits its caller's value unchanged.
    /// </summary>
    public int DelegationDepth { get; init; }

    /// <summary>What kind of node this is.</summary>
    public AgentKind Kind { get; init; } = AgentKind.SubAgent;

    /// <summary>
    /// Short statement of what this agent is for, shown to peers deciding whom to contact. Null for a
    /// root, which nothing needs to choose between.
    /// </summary>
    public string? Role { get; init; }

    /// <summary>Longer guidance on when to contact this agent. Null for a root.</summary>
    public string? Description { get; init; }

    /// <summary>Creates the context for the agent that owns a collaboration.</summary>
    /// <exception cref="ArgumentException">An identifier is blank.</exception>
    public static AgentCollaborationContext ForRoot(string collaborationId, string agentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(collaborationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);

        return new AgentCollaborationContext
        {
            CollaborationId = collaborationId,
            AgentId = agentId,
            Kind = AgentKind.Root,
        };
    }

    /// <summary>
    /// Derives the context for an agent spawned by this one, doing the ancestry and depth arithmetic
    /// in exactly one place so no call site can get it subtly wrong.
    /// </summary>
    /// <param name="childAgentId">Canonical identifier of the new agent.</param>
    /// <param name="kind">What kind of node the new agent is.</param>
    /// <param name="role">Short statement of what the new agent is for.</param>
    /// <param name="description">Longer guidance on when to contact the new agent.</param>
    /// <remarks>
    /// A <see cref="AgentKind.WorkflowController"/> child keeps this agent's
    /// <see cref="DelegationDepth"/>, because orchestration structure should be visible without
    /// costing delegation budget. Every other kind spends one hop.
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// The identifier is blank, or the role or description is outside its bounds.
    /// </exception>
    public AgentCollaborationContext CreateChild(string childAgentId, AgentKind kind, string role, string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(childAgentId);
        ValidateRole(role);
        ValidateDescription(description);

        return new AgentCollaborationContext
        {
            CollaborationId = CollaborationId,
            AgentId = childAgentId,
            ParentAgentId = AgentId,
            AncestorAgentIds = AncestorAgentIds.Add(AgentId),
            StructuralDepth = StructuralDepth + 1,
            DelegationDepth = kind == AgentKind.WorkflowController ? DelegationDepth : DelegationDepth + 1,
            Kind = kind,
            Role = role,
            Description = description,
        };
    }

    /// <summary>
    /// Throws when a role is outside <see cref="MinRoleLength"/>..<see cref="MaxRoleLength"/> Unicode
    /// scalar values.
    /// </summary>
    /// <exception cref="ArgumentException">The role is blank or out of bounds.</exception>
    public static void ValidateRole(string role)
    {
        ValidateBounded(role, nameof(role), MinRoleLength, MaxRoleLength);
    }

    /// <summary>
    /// Throws when a description is outside
    /// <see cref="MinDescriptionLength"/>..<see cref="MaxDescriptionLength"/> Unicode scalar values.
    /// </summary>
    /// <exception cref="ArgumentException">The description is blank or out of bounds.</exception>
    public static void ValidateDescription(string description)
    {
        ValidateBounded(description, nameof(description), MinDescriptionLength, MaxDescriptionLength);
    }

    /// <summary>
    /// Whether a role and description are both admissible, without throwing. The directory admits an
    /// agent only when everything about it validates, so it needs to ask rather than catch.
    /// </summary>
    public static bool IsMetadataValid(string? role, string? description)
    {
        return IsBounded(role, MinRoleLength, MaxRoleLength)
            && IsBounded(description, MinDescriptionLength, MaxDescriptionLength);
    }

    private static void ValidateBounded(string value, string paramName, int min, int max)
    {
        if (!IsBounded(value, min, max))
        {
            // The value itself is collaboration-visible content, so it is deliberately absent from the
            // message: a validation failure must not become the way content reaches a log.
            throw new ArgumentException($"Value must be between {min} and {max} Unicode scalar values.", paramName);
        }
    }

    private static bool IsBounded(string? value, int min, int max)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        // Counted in scalar values, not UTF-16 code units, so an emoji or other astral character costs
        // one against the bound rather than two.
        var scalars = 0;
        foreach (var _ in value.AsSpan().EnumerateRunes())
        {
            scalars++;
            if (scalars > max)
            {
                return false;
            }
        }

        return scalars >= min;
    }
}
