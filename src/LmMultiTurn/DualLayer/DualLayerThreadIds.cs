using AchieveAi.LmDotnetTools.LmCore.Models;

namespace AchieveAi.LmDotnetTools.LmMultiTurn.DualLayer;

/// <summary>
/// The thread-id convention for an executor's own history: <c>executor-{plannerThreadId}</c>. It is a
/// reserved prefix, so a host can keep executor threads out of its conversation list, the same way it
/// keeps <c>subagent-</c> threads out. The executor's sub-agents share the conversation's
/// <see cref="SubAgentThreadIds.ScopeTag"/>, so their threads are the ones a reader forms from the
/// conversation's id.
/// </summary>
public static class DualLayerThreadIds
{
    /// <summary>Reserved prefix of every executor thread id.</summary>
    public const string ExecutorPrefix = SubAgentThreadIds.PairedExecutorPrefix;

    /// <summary>The executor thread paired with <paramref name="plannerThreadId"/>.</summary>
    public static string ExecutorFor(string plannerThreadId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(plannerThreadId);
        return ExecutorPrefix + plannerThreadId;
    }

    /// <summary>
    /// The planner thread that owns <paramref name="threadId"/> when it is an executor thread. A
    /// sub-agent the executor spawns records the executor thread as its parent; a host that shows the
    /// pair as one agent reads that parent as the planner's conversation.
    /// </summary>
    public static bool TryGetPlannerThreadId(
        string? threadId,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? plannerThreadId
    )
    {
        plannerThreadId =
            threadId is not null
            && threadId.Length > ExecutorPrefix.Length
            && threadId.StartsWith(ExecutorPrefix, StringComparison.Ordinal)
                ? threadId[ExecutorPrefix.Length..]
                : null;
        return plannerThreadId is not null;
    }
}
