namespace AchieveAi.LmDotnetTools.LmCore.Models;

/// <summary>
///     The attribution key for one framework-owned agent loop inside a conversation tree (#681; spec 679
///     §4.3, decision Q5): which root it belongs to, which thread it runs on, its human-facing agent id, its
///     parent, and how it was produced. Offered to #670 as the shared key; a rollup groups usage by
///     <see cref="ThreadId" /> because thread id and execution id are the same string today
///     (<c>UsageRecordMapper</c>).
/// </summary>
/// <param name="RootThreadId">The root conversation the loop is attributed to.</param>
/// <param name="ThreadId">The loop's own thread — its execution id.</param>
/// <param name="AgentId"><see cref="RootAgentId" /> for the root loop, else the sub-agent id.</param>
/// <param name="ParentAgentId">The spawning agent's id, or null for the root.</param>
/// <param name="ExecutionKind">How the loop was produced.</param>
public sealed record AgentExecutionRef(
    string RootThreadId,
    string ThreadId,
    string AgentId,
    string? ParentAgentId,
    UsageExecutionKind ExecutionKind
)
{
    /// <summary>The agent id of the root loop.</summary>
    public const string RootAgentId = "root";

    /// <summary>The thread-id prefix a spawned sub-agent's own loop runs under.</summary>
    public const string SubAgentThreadIdPrefix = "subagent-";

    /// <summary>The identity of a conversation's root loop.</summary>
    public static AgentExecutionRef Root(string rootThreadId)
    {
        ArgumentNullException.ThrowIfNull(rootThreadId);
        return new AgentExecutionRef(rootThreadId, rootThreadId, RootAgentId, null, UsageExecutionKind.Primary);
    }

    /// <summary>
    ///     The execution that produced <paramref name="record" />: the ledger stamps
    ///     <see cref="UsageRecord.ParentExecutionId" /> with the emitting execution's own id for every
    ///     non-root attempt and leaves it null for the root's own attempts, so this is the one rule every
    ///     per-agent grouping shares.
    /// </summary>
    public static string ExecutionIdOf(UsageRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return record.ParentExecutionId ?? record.RootConversationId;
    }

    /// <summary>
    ///     The agent id a sub-agent thread id encodes (<c>subagent-{agentId}</c>), or the thread id itself
    ///     when it carries no such prefix.
    /// </summary>
    public static string AgentIdFromThreadId(string threadId)
    {
        ArgumentNullException.ThrowIfNull(threadId);
        return threadId.StartsWith(SubAgentThreadIdPrefix, StringComparison.Ordinal)
            ? threadId[SubAgentThreadIdPrefix.Length..]
            : threadId;
    }
}
