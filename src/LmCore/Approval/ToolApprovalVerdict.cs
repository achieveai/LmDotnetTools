namespace AchieveAi.LmDotnetTools.LmCore.Approval;

/// <summary>
/// One approver's or policy's answer for a single tool call: a code from
/// <see cref="ToolApprovalOutcomes"/> and an optional human-readable reason.
/// </summary>
/// <remarks>
/// <para>
/// The default value of this struct has a null <see cref="Outcome"/> and is therefore <b>not</b>
/// allowed. That is deliberate: an implementation that forgets to set a result, or a
/// <c>default</c> returned from a stub, blocks execution rather than permitting it.
/// </para>
/// </remarks>
public readonly record struct ToolApprovalVerdict
{
    /// <summary>
    /// Creates a verdict carrying an explicit outcome code.
    /// </summary>
    /// <param name="outcome">A value from <see cref="ToolApprovalOutcomes"/>.</param>
    /// <param name="reason">Optional detail shown to operators and returned to the model.</param>
    public ToolApprovalVerdict(string outcome, string? reason = null)
    {
        Outcome = outcome;
        Reason = reason;
    }

    /// <summary>
    /// The decision code. Only <see cref="ToolApprovalOutcomes.Allowed"/> permits execution;
    /// every other value — including one this build does not recognize — blocks it.
    /// </summary>
    public string? Outcome { get; init; }

    /// <summary>Optional detail explaining the decision.</summary>
    public string? Reason { get; init; }

    /// <summary>Whether this verdict permits the handler to run.</summary>
    public bool IsAllowed => ToolApprovalOutcomes.IsAllowed(Outcome);

    /// <summary>Permits the call.</summary>
    public static ToolApprovalVerdict Allow() => new(ToolApprovalOutcomes.Allowed);

    /// <summary>
    /// Refuses the call as an explicit approver denial
    /// (<see cref="ToolApprovalOutcomes.Denied"/>).
    /// </summary>
    /// <param name="reason">Optional detail explaining the refusal.</param>
    public static ToolApprovalVerdict Deny(string? reason = null) =>
        new(ToolApprovalOutcomes.Denied, reason);

    /// <summary>
    /// Refuses the call with a specific code — used by hosts whose policies distinguish
    /// <see cref="ToolApprovalOutcomes.HostPolicyDenied"/> from
    /// <see cref="ToolApprovalOutcomes.ProviderPolicyDenied"/>, and by the preparer when it
    /// derives an outcome from what happened.
    /// </summary>
    /// <param name="outcome">A blocking code from <see cref="ToolApprovalOutcomes"/>.</param>
    /// <param name="reason">Optional detail explaining the refusal.</param>
    public static ToolApprovalVerdict Blocked(string outcome, string? reason = null) =>
        new(outcome, reason);
}
