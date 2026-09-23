namespace AchieveAi.LmDotnetTools.LmAgentInfra.Agents;

/// <summary>
/// Thrown by an agent factory when it is asked to switch a conversation's mode or provider IN PLACE
/// while that conversation is mid-run, and so cannot serve the switch at all.
/// </summary>
/// <remarks>
/// <para>
/// It exists because the two possible answers to a busy switch are not equivalent. Recreating instead
/// would "succeed" by destroying the running turn and everything the live agent owns — its sub-agents,
/// armed waits and tool sessions — which is precisely what the in-place path exists to preserve. So a
/// factory that can only serve this switch in place refuses it, and the host turns the refusal into a
/// <c>409</c> the user can act on by waiting.
/// </para>
/// <para>
/// The pool does not raise it and does not catch it. A factory throwing is already the transactional
/// case the switch is built around (see <c>MultiTurnAgentPool.SwapAgentUnderLockAsync</c>): the
/// replacement is constructed before anything is evicted, so a throw leaves the thread exactly as it
/// was — same agent, same mode, same provider, nothing disposed. This type only lets a caller tell
/// "busy" apart from every other construction failure, which otherwise all look like a 503.
/// </para>
/// <para>
/// Derives from <see cref="InvalidOperationException"/> for the same reason
/// <see cref="AgentNotPooledException"/> does: callers that already catch the base type keep working.
/// </para>
/// </remarks>
public sealed class AgentBusyException : InvalidOperationException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="threadId">The conversation whose agent is mid-run.</param>
    public AgentBusyException(string threadId)
        : base(
            $"The agent for thread '{threadId}' has a run in progress, so its mode/provider cannot be "
                + "switched in place. Retry once the run completes."
        )
    {
        ThreadId = threadId;
    }

    /// <summary>The conversation whose agent is mid-run.</summary>
    public string ThreadId { get; }
}
