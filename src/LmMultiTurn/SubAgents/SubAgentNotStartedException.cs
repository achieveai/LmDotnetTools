namespace AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;

/// <summary>
/// Thrown when a message is addressed to a sub-agent that has been spawned but is still waiting for a
/// concurrency permit, so it has no loop yet to receive anything.
/// </summary>
/// <remarks>
/// <para>
/// The condition is recoverable and entirely ordinary — the spawn receipt that gave the caller this id
/// said "queued" — so callers that can turn a failure into a result for the model need to recognise it
/// WITHOUT string-matching the message. That is the whole reason this type exists.
/// </para>
/// <para>
/// Derives from <see cref="InvalidOperationException"/> rather than from <see cref="Exception"/> so
/// existing handlers keep their behaviour: notably the sub-agent write endpoint, which already maps an
/// <see cref="InvalidOperationException"/> from a delivery to a refusal the collaboration ledger records
/// as retryable.
/// </para>
/// </remarks>
public sealed class SubAgentNotStartedException : InvalidOperationException
{
    /// <summary>Creates the exception for a target that is queued.</summary>
    /// <param name="target">The id or name the caller addressed, quoted back so the model recognises it.</param>
    /// <param name="agentId">The canonical id the target resolved to.</param>
    public SubAgentNotStartedException(string target, string agentId)
        : base(
            $"Sub-agent '{target}' is queued and cannot receive messages until it starts. "
                + "Poll it with CheckAgent/CheckAgents and retry when status is running."
        )
    {
        Target = target;
        AgentId = agentId;
    }

    /// <summary>The id or name the caller addressed the message to.</summary>
    public string Target { get; }

    /// <summary>The canonical id of the queued sub-agent.</summary>
    public string AgentId { get; }
}
