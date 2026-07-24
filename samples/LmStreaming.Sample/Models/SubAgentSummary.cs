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
}
