using AchieveAi.LmDotnetTools.LmCore.Messages;

namespace AchieveAi.LmDotnetTools.LmMultiTurn;

/// <summary>
/// Outcome of attempting to deliver out-of-band context (e.g. a sandbox-discovered
/// directory <c>CLAUDE.md</c>/<c>AGENTS.md</c>) to a specific sub-agent that is owned
/// by an <see cref="ISubAgentContextSink"/>.
/// </summary>
public enum SubAgentContextDeliveryResult
{
    /// <summary>The context was injected into the target sub-agent, which was running.</summary>
    Delivered,

    /// <summary>
    /// This sink owns the target sub-agent, but it was not in a state that could accept
    /// the delivery (finished, disposing, or otherwise not safely running). The caller
    /// should drop the delivery — it must NOT fall back to the primary conversation.
    /// </summary>
    TargetNotDeliverable,

    /// <summary>This sink does not own the target sub-agent; the caller should keep looking.</summary>
    NotOwned,
}

/// <summary>
/// A capability implemented by agents that own sub-agents (currently
/// <see cref="MultiTurnAgentLoop"/> via its <c>SubAgentManager</c>), allowing an external
/// caller — the context-discovery injector — to route a discovered directory context file
/// to the specific sub-agent that opened it, rather than the primary conversation.
/// </summary>
/// <remarks>
/// This is deliberately a narrow role interface probed with <c>is ISubAgentContextSink</c>,
/// NOT a member of <see cref="IMultiTurnAgent"/>: CLI-backed loops (e.g. <c>ClaudeAgentLoop</c>)
/// own no sub-agents and must not be forced to implement it.
/// </remarks>
public interface ISubAgentContextSink
{
    /// <summary>
    /// Attempts to deliver <paramref name="messages"/> into a currently-running sub-agent
    /// identified by <paramref name="agentId"/> (matched by id or caller-supplied name).
    /// Never restarts a finished sub-agent and never triggers a spurious extra run.
    /// </summary>
    /// <returns>
    /// <see cref="SubAgentContextDeliveryResult.Delivered"/> if injected into a running sub-agent;
    /// <see cref="SubAgentContextDeliveryResult.TargetNotDeliverable"/> if this sink owns the id but
    /// it is not safely running (caller drops);
    /// <see cref="SubAgentContextDeliveryResult.NotOwned"/> if this sink does not own the id.
    /// </returns>
    Task<SubAgentContextDeliveryResult> TryDeliverContextAsync(
        string agentId,
        IReadOnlyList<IMessage> messages,
        CancellationToken cancellationToken = default);
}
