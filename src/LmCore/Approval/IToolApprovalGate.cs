namespace AchieveAi.LmDotnetTools.LmCore.Approval;

/// <summary>
/// An approver that may be asked, asynchronously, whether a tool call should run — a human in a
/// UI, a service, a policy engine.
/// </summary>
/// <remarks>
/// <para>
/// Gates are consulted only after the cheap checks pass, and only when
/// <see cref="ToolApprovalOptions.RequireApproval"/> is set or at least one gate is configured. A
/// call that a provider or host policy already refused never reaches a gate.
/// </para>
/// <para>
/// <b>Every configured gate must allow.</b> A single denial blocks the call, and so does a gate
/// that throws, returns <c>default</c>, or fails to answer before the effective expiry. An
/// implementation that cannot reach its decision-maker should return a blocking verdict rather
/// than guess.
/// </para>
/// <para>
/// Implementations must honour the supplied <see cref="CancellationToken"/>: it is already linked
/// to the run's cancellation and to the effective expiry, so awaiting it is how a gate learns that
/// its answer is no longer wanted.
/// </para>
/// </remarks>
public interface IToolApprovalGate
{
    /// <summary>
    /// Asks for a decision on <paramref name="context"/>.
    /// </summary>
    /// <param name="context">The call being decided, including its frozen arguments.</param>
    /// <param name="cancellationToken">
    /// Cancelled when the run is cancelled or the effective expiry elapses.
    /// </param>
    /// <returns>
    /// <see cref="ToolApprovalVerdict.Allow"/> to permit the call; any other verdict blocks it.
    /// </returns>
    ValueTask<ToolApprovalVerdict> RequestApprovalAsync(
        ToolApprovalContext context,
        CancellationToken cancellationToken);
}
