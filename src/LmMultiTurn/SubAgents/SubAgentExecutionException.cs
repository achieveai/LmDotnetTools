namespace AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;

/// <summary>
/// Thrown when a synchronously-awaited sub-agent run completes with an error.
/// Carries the failing agent's id and template so the parent tool handler can
/// surface a clear failure result to the calling LLM.
/// </summary>
public sealed class SubAgentExecutionException : Exception
{
    public SubAgentExecutionException(string agentId, string templateName, string? errorMessage)
        : base(
            $"Sub-agent '{templateName}' ({agentId}) failed: " +
            $"{errorMessage ?? "(no error message)"}")
    {
        AgentId = agentId;
        TemplateName = templateName;
    }

    /// <summary>The id of the sub-agent whose run failed.</summary>
    public string AgentId { get; }

    /// <summary>The template the failing sub-agent was spawned from.</summary>
    public string TemplateName { get; }
}
