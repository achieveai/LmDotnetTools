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

    /// <summary>
    /// Optional caller-supplied handle so SendMessage can address this agent by name.
    /// </summary>
    public string? Name { get; init; }

    /// <summary>
    /// When true (background spawn/continuation), run completion is relayed to the
    /// parent as an injected user message. When false (synchronous call), the tool
    /// handler awaiting <see cref="Completion"/> returns the result directly instead.
    /// </summary>
    public bool NotifyParentOnCompletion { get; set; }

    public Task? RunTask { get; set; }
    public Task? MonitorTask { get; set; }
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

    /// <summary>
    /// Signal resolved when the current run completes: the final assistant text on
    /// success, faulted on error, cancelled when the run ends without completing.
    /// Synchronous Agent/SendMessage calls await this to return the result as the
    /// tool result.
    /// </summary>
    public TaskCompletionSource<string> Completion { get; private set; } = CreateCompletionSource();

    /// <summary>
    /// Replaces an already-resolved completion with a fresh one so a follow-up
    /// run (SendMessage continuation) can be awaited. A pending (unresolved)
    /// completion is kept — existing waiters observe the next resolution.
    /// </summary>
    public void ResetCompletionIfFinished()
    {
        if (Completion.Task.IsCompleted)
        {
            Completion = CreateCompletionSource();
        }
    }

    private static TaskCompletionSource<string> CreateCompletionSource()
    {
        // RunContinuationsAsynchronously: the monitor task resolves this signal;
        // running waiter continuations inline would execute tool-handler code on
        // the monitor's subscription thread.
        return new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
