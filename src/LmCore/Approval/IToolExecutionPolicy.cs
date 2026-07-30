namespace AchieveAi.LmDotnetTools.LmCore.Approval;

/// <summary>
/// A cheap, local rule about whether a tool call is permitted at all — evaluated before any
/// approval gate is opened.
/// </summary>
/// <remarks>
/// <para>
/// The point of a policy is that it costs nothing and reaches nobody. A call that a policy already
/// refuses never becomes a notification on someone's phone, so policies should not perform network
/// calls or wait on humans; that is what <see cref="IToolApprovalGate"/> is for. Policies are
/// nevertheless asynchronous so a host can consult a cached lookup without blocking a thread.
/// </para>
/// <para>
/// A policy is consulted in one of two slots — <see cref="ToolApprovalOptions.ProviderPolicy"/> or
/// <see cref="ToolApprovalOptions.HostPolicy"/> — and a plain
/// <see cref="ToolApprovalVerdict.Deny"/> is reported as that slot's code
/// (<see cref="ToolApprovalOutcomes.ProviderPolicyDenied"/> or
/// <see cref="ToolApprovalOutcomes.HostPolicyDenied"/>), so an implementation does not have to
/// remember which slot it was registered in. A policy that returns a more specific code — say
/// <see cref="ToolApprovalOutcomes.Revoked"/> — keeps it. Any non-allowing verdict blocks
/// regardless of the code it carries; the code only determines what the caller is told.
/// </para>
/// <para>
/// A policy that throws blocks the call with <see cref="ToolApprovalOutcomes.HookError"/>.
/// </para>
/// </remarks>
public interface IToolExecutionPolicy
{
    /// <summary>
    /// Decides whether <paramref name="context"/> may proceed to the next check.
    /// </summary>
    /// <param name="context">The call being evaluated, including its frozen arguments.</param>
    /// <param name="cancellationToken">Cancelled when the run is cancelled.</param>
    /// <returns>
    /// <see cref="ToolApprovalVerdict.Allow"/> to continue; any other verdict blocks the call.
    /// </returns>
    ValueTask<ToolApprovalVerdict> EvaluateAsync(
        ToolApprovalContext context,
        CancellationToken cancellationToken);
}
