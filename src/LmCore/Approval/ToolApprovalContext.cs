using AchieveAi.LmDotnetTools.LmCore.Messages;

namespace AchieveAi.LmDotnetTools.LmCore.Approval;

/// <summary>
/// Everything a policy or an approver is given to decide a single tool call.
/// </summary>
/// <remarks>
/// The same instance is passed to the provider policy, the host policy, and every approval gate,
/// so all of them decide against identical facts — including
/// <see cref="ToolApprovalContext.Arguments"/>, which is already frozen by the time any of them
/// sees it.
/// </remarks>
public sealed record ToolApprovalContext
{
    /// <summary>The tool being called.</summary>
    public required string ToolName { get; init; }

    /// <summary>
    /// The model-assigned tool_call_id, when the call path carries one. Null is legitimate and
    /// must not be treated as a reason to block.
    /// </summary>
    public string? ToolCallId { get; init; }

    /// <summary>
    /// The exact arguments that will be handed to the handler if the call is allowed, plus their
    /// hash. See <see cref="CanonicalToolArguments"/> for what "canonical" does and does not mean.
    /// </summary>
    public required CanonicalToolArguments Arguments { get; init; }

    /// <summary>Whether the host executes this tool or a provider does.</summary>
    public ExecutionTarget ExecutionTarget { get; init; } = ExecutionTarget.LocalFunction;

    /// <summary>The conversation this call belongs to, when known.</summary>
    public string? ThreadId { get; init; }

    /// <summary>The run this call belongs to, when known.</summary>
    public string? RunId { get; init; }

    /// <summary>The generation (turn) this call belongs to, when known.</summary>
    public string? GenerationId { get; init; }

    /// <summary>
    /// The instant after which a decision is no longer accepted — the earliest of the configured
    /// maximum wait and any provider or host operation deadline. A gate that queues work for a
    /// human should surface this so the human knows how long they have.
    /// </summary>
    public DateTimeOffset ExpiresAt { get; init; }
}
