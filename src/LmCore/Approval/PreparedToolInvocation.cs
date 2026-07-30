using AchieveAi.LmDotnetTools.LmCore.Messages;

namespace AchieveAi.LmDotnetTools.LmCore.Approval;

/// <summary>
/// The settled decision for one tool call, together with the exact arguments that will run if it
/// was allowed.
/// </summary>
/// <remarks>
/// Produced by <see cref="ToolInvocationPreparer.PrepareAsync"/> and consumed by
/// <see cref="ToolInvocationPreparer.InvokeAsync"/>. Splitting the two is what lets a batch of
/// calls have their approvals in flight simultaneously while still executing in the caller's
/// original order.
/// </remarks>
public sealed record PreparedToolInvocation
{
    /// <summary>The tool this decision is about.</summary>
    public required string ToolName { get; init; }

    /// <summary>The model-assigned tool_call_id, when the call path carries one.</summary>
    public string? ToolCallId { get; init; }

    /// <summary>
    /// The frozen arguments. Present whether or not the call was allowed, so a caller can report
    /// the hash of what was refused.
    /// </summary>
    public required CanonicalToolArguments Arguments { get; init; }

    /// <summary>Whether the host executes this tool or a provider does.</summary>
    public ExecutionTarget ExecutionTarget { get; init; } = ExecutionTarget.LocalFunction;

    /// <summary>
    /// The settled code from <see cref="ToolApprovalOutcomes"/>. Only
    /// <see cref="ToolApprovalOutcomes.Allowed"/> permits execution.
    /// </summary>
    public required string Outcome { get; init; }

    /// <summary>Optional detail explaining a refusal.</summary>
    public string? Reason { get; init; }

    /// <summary>Whether the handler may run.</summary>
    public bool IsApproved => ToolApprovalOutcomes.IsAllowed(Outcome);

    /// <summary>
    /// The message returned to the model when this call is refused, naming the outcome code so the
    /// refusal is machine-readable without parsing prose.
    /// </summary>
    public string BlockedMessage =>
        Reason is { Length: > 0 } reason
            ? $"Tool call '{ToolName}' was not executed ({Outcome}): {reason}"
            : $"Tool call '{ToolName}' was not executed ({Outcome}).";

    /// <summary>
    /// Renders this refusal as the error result the loop already knows how to handle — the same
    /// shape used for an unknown function, so a denial needs no special case downstream.
    /// </summary>
    /// <exception cref="InvalidOperationException">The invocation was approved.</exception>
    public ToolCallResult ToBlockedResult()
    {
        if (IsApproved)
        {
            throw new InvalidOperationException(
                "ToBlockedResult was called on an approved invocation — this would report an "
                    + "allowed call as refused.");
        }

        return new ToolCallResult(ToolCallId, BlockedMessage)
        {
            ToolName = ToolName,
            ExecutionTarget = ExecutionTarget,
            IsError = true,
            ErrorCode = Outcome,
        };
    }
}
