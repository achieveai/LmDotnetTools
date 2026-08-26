namespace AchieveAi.LmDotnetTools.LmAgentInfra.Agents;

/// <summary>
/// Thrown when an operation that requires a thread's live pooled agent finds none pooled for it.
/// </summary>
/// <remarks>
/// <para>
/// Derives from <see cref="InvalidOperationException"/> because that is what this condition has
/// always been thrown as, and callers that catch the base type keep working unchanged. The subtype
/// exists so a caller can distinguish "the entry is gone" from every OTHER invalid operation a call
/// might raise. That distinction is load-bearing on the WebSocket message path: releasing a pooled
/// agent for an authorized grantee (#376) opens a window in which the entry is removed and not yet
/// recreated, and a socket refreshing inside that window must answer its client rather than let the
/// exception abort the connection. Catching bare <see cref="InvalidOperationException"/> there would
/// swallow unrelated bugs with the same handler.
/// </para>
/// </remarks>
public sealed class AgentNotPooledException : InvalidOperationException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="threadId">The thread that has no pooled agent.</param>
    public AgentNotPooledException(string threadId)
        : base($"No pooled agent exists for thread '{threadId}'.")
    {
        ThreadId = threadId;
    }

    /// <summary>The thread that has no pooled agent.</summary>
    public string ThreadId { get; }
}
