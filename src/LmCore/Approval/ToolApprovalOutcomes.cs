namespace AchieveAi.LmDotnetTools.LmCore.Approval;

/// <summary>
/// The stable codes that say whether a tool call may execute, and if not, why.
/// </summary>
/// <remarks>
/// <para>
/// Exactly one value — <see cref="Allowed"/> — permits execution. Every other value, <b>including
/// any value this build does not recognize</b>, blocks it. Approval is the one place where an
/// unknown value must fail closed: "preserve and ignore", applied to an authorization decision, is
/// indistinguishable from "allow".
/// </para>
/// <para>
/// These strings are deliberately identical to the ones on the lifecycle wire contract
/// (<c>AchieveAi.LmDotnetTools.LmLifecycle.ToolApprovalOutcomes</c>) so a decision made here can be
/// reported verbatim without a translation table. They are mirrored rather than shared because this
/// assembly must not take a dependency on the wire contract — a host can gate tool calls without
/// ever publishing a lifecycle event. A test pins the two sets to the same values.
/// </para>
/// </remarks>
public static class ToolApprovalOutcomes
{
    /// <summary>
    /// Every configured approver explicitly allowed the call. The <b>only</b> value that permits
    /// execution.
    /// </summary>
    public const string Allowed = "allowed";

    /// <summary>An approver explicitly refused the call.</summary>
    public const string Denied = "denied";

    /// <summary>
    /// The execution target, handler registration, or provider-native policy refused the call. This
    /// check runs first and never opens an approval gate, so a call a local policy already refuses
    /// never reaches a human or a remote approver.
    /// </summary>
    public const string ProviderPolicyDenied = "provider_policy_denied";

    /// <summary>
    /// The host's own execution policy refused the call. Runs after the provider check and, like
    /// it, never opens an approval gate.
    /// </summary>
    public const string HostPolicyDenied = "host_policy_denied";

    /// <summary>
    /// No decision arrived before the effective expiry — the earliest of the configured maximum
    /// wait, any provider or host deadline, and run or turn cancellation.
    /// </summary>
    public const string Timeout = "timeout";

    /// <summary>Too many approvals were already pending for this process.</summary>
    public const string Overload = "overload";

    /// <summary>A previously granted approval was withdrawn before the handler was invoked.</summary>
    public const string Revoked = "revoked";

    /// <summary>An approver threw. A gate that fails is a gate that blocks.</summary>
    public const string HookError = "hook_error";

    /// <summary>
    /// Approval was required but no approver was reachable. Absent an approver there is no allow,
    /// and no allow means no execution.
    /// </summary>
    public const string MissingApprover = "missing_approver";

    /// <summary>The run or turn was cancelled while the decision was outstanding.</summary>
    public const string Cancelled = "cancelled";

    /// <summary>
    /// Determines whether <paramref name="outcome"/> permits the handler to run.
    /// </summary>
    /// <param name="outcome">The decision code to evaluate.</param>
    /// <returns>
    /// <see langword="true"/> only for exactly <see cref="Allowed"/>. Null, empty, unknown, and
    /// differently cased values all return <see langword="false"/>.
    /// </returns>
    /// <remarks>
    /// The comparison is ordinal and case-sensitive on purpose. A case-insensitive or lenient match
    /// would let a malformed or hostile approver approach "allow" by accident.
    /// </remarks>
    public static bool IsAllowed(string? outcome) => string.Equals(outcome, Allowed, StringComparison.Ordinal);
}
