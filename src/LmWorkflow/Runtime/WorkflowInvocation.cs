using System.Text.Json.Nodes;
using AchieveAi.LmDotnetTools.LmWorkflow.Model;

namespace AchieveAi.LmDotnetTools.LmWorkflow.Runtime;

/// <summary>A durable occurrence handed to an adapter. Identity is stable across reconciliation.</summary>
public sealed record WorkflowInvocation
{
    public required string InstanceId { get; init; }
    public required string InvocationId { get; init; }
    public required string UnitName { get; init; }
    public required WorkflowTask Task { get; init; }
    public required JsonNode Input { get; init; }
    public string? SessionId { get; init; }
    public bool IsCorrection { get; init; }
    public int Attempt { get; init; } = 1;
    public string? ValidationError { get; init; }
    public DateTimeOffset? DeadlineUtc { get; init; }
}

/// <summary>Unknown means an invocation must be reconciled; it is never permission to replay it.</summary>
public enum WorkflowInvocationStatus
{
    Completed,
    Failed,
    Unknown,
}

/// <summary>Transport completion, before the runtime validates and records the output.</summary>
public sealed record WorkflowInvocationResult(
    WorkflowInvocationStatus Status,
    string? Output = null,
    string? Error = null
);

/// <summary>Adapters own execution and authoritative reconciliation, not routing or workflow state.</summary>
public interface IWorkflowTaskInvoker
{
    Task<WorkflowInvocationResult> InvokeAsync(WorkflowInvocation invocation, CancellationToken ct = default);
    Task<WorkflowInvocationResult> ReconcileAsync(WorkflowInvocation invocation, CancellationToken ct = default);
}
