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

    /// <summary>
    /// Thread id of the conversation that spawned this sub-agent, or null when the node has no
    /// persisted parent link. Additive: populated by the recursive descendant-graph reader
    /// (<c>GET .../subagents?recursive=true</c>); the flat (non-recursive) listing does not set it.
    /// </summary>
    public string? ParentThreadId { get; init; }

    /// <summary>
    /// Distance from the requested root in the recursive descendant graph — the root's direct
    /// children are depth 1. Additive: null outside the recursive contract.
    /// </summary>
    public int? Depth { get; init; }

    /// <summary>
    /// UTC instant the sub-agent reached a terminal status, or null while running or unknown.
    /// Additive: populated by the recursive descendant-graph reader from the same stamped value
    /// <see cref="LastActivityUtc"/> already falls back to for a persisted-only child.
    /// </summary>
    public DateTimeOffset? TerminalAtUtc { get; init; }

    /// <summary>
    /// Machine-readable failure reason, when known. Additive and reserved for future use — no
    /// current write path stamps a failure code, so this is always null today.
    /// </summary>
    public string? FailureCode { get; init; }
}

/// <summary>
/// Versioned envelope for the recursive descendant graph
/// (<c>GET /api/conversations/{threadId}/subagents?recursive=true</c>). <see cref="SchemaVersion"/>
/// lets a consumer (e.g. a daemon-side completion barrier) fail closed on an old/incompatible
/// response rather than silently misreading a flat array as a tree. The plain, non-recursive
/// endpoint is unaffected — it keeps returning a bare <c>SubAgentSummary[]</c>.
/// </summary>
public sealed record SubAgentTreeResponse(int SchemaVersion, IReadOnlyList<SubAgentSummary> Nodes);
