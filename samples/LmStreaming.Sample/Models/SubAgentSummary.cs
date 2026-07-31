namespace LmStreaming.Sample.Models;

/// <summary>
/// Presentation-only summary of a single sub-agent spawned by a conversation's parent agent.
/// Projected from <c>SubAgentManager.ListAgents()</c> snapshots for the read-only
/// <c>GET /api/conversations/{threadId}/subagents</c> endpoint so the client can display a
/// conversation's children without touching sub-agent execution (WI #194).
/// </summary>
public sealed record SubAgentSummary
{
    /// <summary>Stable id assigned to the sub-agent at spawn time.</summary>
    public required string AgentId { get; init; }

    /// <summary>
    ///     What kind of child this row represents: <c>subagent</c> (an Agent-tool spawn, the default) or
    ///     <c>workflow</c> (a StartWorkflowAgent run whose isolated controller loop is surfaced as a tab).
    /// </summary>
    public string Kind { get; init; } = "subagent";

    /// <summary>Caller-supplied display name, or null when the spawn provided none.</summary>
    public string? Name { get; init; }

    /// <summary>Name of the template the sub-agent was spawned from.</summary>
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
}

/// <summary>Versioned recursive descendant graph response.</summary>
public sealed record SubAgentTreeResponse(int SchemaVersion, IReadOnlyList<SubAgentSummary> Nodes);
