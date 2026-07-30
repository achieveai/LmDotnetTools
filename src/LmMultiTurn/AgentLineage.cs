namespace AchieveAi.LmDotnetTools.LmMultiTurn;

/// <summary>
/// Where an agent came from — the thread, run, and tool call that spawned it.
/// </summary>
/// <remarks>
/// <para>
/// Lineage is fixed for the life of an agent: a sub-agent's parent does not change between its
/// runs, so this is supplied once at construction rather than recomputed per run. It exists as a
/// value object because it has to travel intact through every composition root that can build an
/// agent — a pool, a manager, a factory, a sample's provider wiring — and four loose nullable
/// strings threaded through each of those is how a field quietly gets dropped.
/// </para>
/// <para>
/// A top-level agent has no lineage and uses <see cref="None"/>. Absent means "this agent was not
/// spawned by another", never "unknown".
/// </para>
/// <para>
/// This is lineage only. It says nothing about whether the child inherited provider-side context;
/// that is a per-run property of the run assignment.
/// </para>
/// </remarks>
public sealed record AgentLineage
{
    /// <summary>The lineage of an agent that nothing spawned.</summary>
    public static AgentLineage None { get; } = new();

    /// <summary>The thread of the agent that spawned this one.</summary>
    public string? ParentThreadId { get; init; }

    /// <summary>The run, on the parent thread, that spawned this agent.</summary>
    public string? ParentRunId { get; init; }

    /// <summary>
    /// The tool call that spawned this agent, when a tool call did.
    /// </summary>
    /// <remarks>
    /// Nullable even for a genuine sub-agent: a host may create one directly rather than in
    /// response to a model-requested tool call, in which case there is a parent but no spawning
    /// call.
    /// </remarks>
    public string? SpawningToolCallId { get; init; }

    /// <summary>
    /// The sub-agent's own identifier, when this agent is a sub-agent.
    /// </summary>
    public string? SubAgentId { get; init; }

    /// <summary>
    /// Whether this agent was spawned by another one.
    /// </summary>
    /// <remarks>
    /// True when any lineage member is present. An agent spawned by a host with no tool call and
    /// no sub-agent identity still has a parent thread, and still is not top-level.
    /// </remarks>
    public bool IsSpawned =>
        ParentThreadId != null
        || ParentRunId != null
        || SpawningToolCallId != null
        || SubAgentId != null;
}
