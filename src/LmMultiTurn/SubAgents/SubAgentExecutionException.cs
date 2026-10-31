namespace AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;

/// <summary>
/// Thrown when a synchronously-awaited sub-agent run completes with an error.
/// Carries the failing agent's id and template so the parent tool handler can
/// surface a clear failure result to the calling LLM.
/// </summary>
public sealed class SubAgentExecutionException : Exception
{
    public SubAgentExecutionException(string agentId, string templateName, string? errorMessage)
        : base($"Sub-agent '{templateName}' ({agentId}) failed: " + $"{errorMessage ?? "(no error message)"}")
    {
        AgentId = agentId;
        TemplateName = templateName;
    }

    private SubAgentExecutionException(string message, string agentId, string templateName, Exception? innerException)
        : base(message, innerException)
    {
        AgentId = agentId;
        TemplateName = templateName;
    }

    /// <summary>
    /// The failure of a run that ended before its parent was rebuilt (#868), carrying the message a waiter
    /// saw then word for word rather than wrapping it a second time.
    /// </summary>
    internal static SubAgentExecutionException Restored(string agentId, string templateName, string message) =>
        new(message, agentId, templateName, innerException: null);

    /// <summary>The id of the sub-agent whose run failed.</summary>
    public string AgentId { get; }

    /// <summary>The template the failing sub-agent was spawned from.</summary>
    public string TemplateName { get; }
}
