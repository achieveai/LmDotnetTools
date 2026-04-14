using System.Collections.Concurrent;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;

namespace AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;

/// <summary>
/// Lifecycle status of a sub-agent.
/// </summary>
public enum SubAgentStatus
{
    Running,
    Completed,
    Error,
    Stopped,
}

/// <summary>
/// Summary of a single turn within a sub-agent's execution.
/// Used to provide lightweight progress visibility to the parent.
/// </summary>
public record SubAgentTurnSummary
{
    public required string MessageType { get; init; }
    public string? ToolName { get; init; }
    public string? ToolArgsPreview { get; init; }
    public string? TextPreview { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Mutable state tracker for a running sub-agent instance.
/// Internal to the SubAgents module; not exposed to consumers.
/// </summary>
internal class SubAgentState
{
    public required string AgentId { get; init; }
    public required string TemplateName { get; init; }
    public required string Task { get; init; }
    public required IMultiTurnAgent Agent { get; init; }

    public Task? RunTask { get; set; }
    public CancellationTokenSource Cts { get; set; } = new();
    public ConcurrentQueue<SubAgentTurnSummary> TurnBuffer { get; } = new();

    private volatile SubAgentStatus _status = SubAgentStatus.Running;
    public SubAgentStatus Status { get => _status; set => _status = value; }

    public IConversationStore? Store { get; init; }

    /// <summary>
    /// Set to true when SendToParentAsync fails, so CheckAgent/Peek can surface the error.
    /// </summary>
    public bool SendToParentFailed { get; set; }

    /// <summary>
    /// Error message from the most recent failed SendToParentAsync call.
    /// </summary>
    public string? SendToParentError { get; set; }

    /// <summary>
    /// Stores the final text result after the sub-agent completes.
    /// Populated from the last assistant TextMessage before RunCompletedMessage.
    /// </summary>
    public string? LastResult { get; set; }
}
