using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.DualLayer;

namespace LmStreaming.Sample.Services;

/// <summary>
/// A dual-layer conversation is one agent to the user, but its sub-agents and collaboration belong to
/// the executor loop, not to the planner the pool holds. Live read paths that reach a conversation's
/// agents through its pooled loop look through the pair with this.
/// </summary>
/// <remarks>
/// The persisted half of the same rule lives in <see cref="Persistence.SubAgentProvenance"/>, which reads a
/// child the executor spawned as the conversation's child.
/// </remarks>
internal static class DualLayerConversation
{
    /// <summary>
    /// The loop that owns <paramref name="agent"/>'s sub-agents: the executor when it is a dual-layer
    /// planner, otherwise the loop itself. Null when it is not a <see cref="MultiTurnAgentLoop"/>.
    /// </summary>
    public static MultiTurnAgentLoop? SubAgentHost(object? agent) =>
        agent is MultiTurnAgentBase planner && DualLayerInputRouting.TryGetExecutorLoop(planner, out var executorLoop)
            ? executorLoop as MultiTurnAgentLoop
            : agent as MultiTurnAgentLoop;
}
