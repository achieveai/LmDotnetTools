using AchieveAi.LmDotnetTools.LmCore.Messages;

namespace AchieveAi.LmDotnetTools.LmCore.Approval;

/// <summary>
/// What the caller knows about a tool call before it is prepared.
/// </summary>
/// <remarks>
/// Distinct from <see cref="ToolApprovalContext"/> on purpose: this is the mutable-source view the
/// executor assembles, whereas the context is what policies and approvers see <i>after</i> the
/// arguments have been frozen.
/// </remarks>
public sealed record ToolInvocationRequest
{
    /// <summary>The tool being called.</summary>
    public required string ToolName { get; init; }

    /// <summary>Raw argument text from the call. Null or empty is normalized to <c>{}</c>.</summary>
    public string? ArgumentsJson { get; init; }

    /// <summary>The model-assigned tool_call_id, when the call path carries one.</summary>
    public string? ToolCallId { get; init; }

    /// <summary>Whether the host executes this tool or a provider does.</summary>
    public ExecutionTarget ExecutionTarget { get; init; } = ExecutionTarget.LocalFunction;

    /// <summary>The conversation this call belongs to, when known.</summary>
    public string? ThreadId { get; init; }

    /// <summary>The run this call belongs to, when known.</summary>
    public string? RunId { get; init; }

    /// <summary>The generation (turn) this call belongs to, when known.</summary>
    public string? GenerationId { get; init; }

    /// <summary>
    /// A provider or host deadline for the operation this call belongs to. When present and
    /// earlier than <see cref="ToolApprovalOptions.MaxApprovalWait"/> allows, it wins: a decision
    /// is worthless once the turn it belongs to has expired.
    /// </summary>
    public DateTimeOffset? OperationDeadline { get; init; }
}
