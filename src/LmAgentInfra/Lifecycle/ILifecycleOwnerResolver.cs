using AchieveAi.LmDotnetTools.LmLifecycle;

namespace AchieveAi.LmDotnetTools.LmAgentInfra.Lifecycle;

/// <summary>
/// The host's answer to "who owns this?" — the only source of
/// <see cref="LifecycleOwnerKey"/> values the delivery runtime will accept (ADR 0005).
/// <para>
/// The runtime deliberately cannot answer either question itself. It sees envelopes and HTTP
/// requests, and everything on them — thread ids, run ids, sandbox session ids, workspace ids,
/// source stream ids — describes what happened rather than who is entitled to hear about it.
/// Inferring an owner from any of those would let a caller who can guess or observe an identifier
/// widen its own scope. Only the host knows which app a thread was created for.
/// </para>
/// <para>
/// <b>Both methods fail closed.</b> Returning <c>null</c> means "no owner" and every call site
/// treats that as a denial: an event with no resolvable owner is dropped rather than broadcast, and
/// a caller with no resolvable owner is refused rather than defaulted. An implementation that
/// cannot decide must return <c>null</c>; it must not guess, and it must not throw as a way of
/// saying "deny" — a throw is treated as a fault and is also denied, but it is logged as a defect.
/// </para>
/// </summary>
public interface ILifecycleOwnerResolver
{
    /// <summary>
    /// Resolves the owner that produced <paramref name="lifecycleEvent"/>, deciding who — if
    /// anyone — is entitled to receive it.
    /// </summary>
    /// <param name="lifecycleEvent">The event about to be fanned out. Treat its identifiers as
    /// correlation, not authorization.</param>
    /// <param name="cancellationToken">Cancels the resolution.</param>
    /// <returns>The owning key, or <c>null</c> to drop the event undelivered.</returns>
    ValueTask<LifecycleOwnerKey?> ResolveEventOwnerAsync(
        LifecycleEventEnvelope lifecycleEvent,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Resolves the owner of a conversation thread, so an approval request can be scoped before any
    /// approver is asked about it.
    /// <para>
    /// Separate from <see cref="ResolveEventOwnerAsync"/> because an approval is raised from a tool
    /// call, not from an envelope, and synthesizing a fake envelope just to reuse that method would
    /// make the host resolver answer a question it was never really asked.
    /// </para>
    /// </summary>
    /// <param name="threadId">
    /// The thread the tool call belongs to. A tool call whose thread is unknown resolves to
    /// <c>null</c> and therefore cannot be approved remotely — the fail-closed direction, since an
    /// unscoped approval request is one any approver could answer.
    /// </param>
    /// <param name="cancellationToken">Cancels the resolution.</param>
    /// <returns>The owning key, or <c>null</c> to refuse remote approval for this call.</returns>
    ValueTask<LifecycleOwnerKey?> ResolveThreadOwnerAsync(
        string? threadId,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Resolves the owner behind an <b>already-authenticated</b> app identity, for an inbound
    /// subscription-management or approval-decision call.
    /// <para>
    /// This method does not authenticate. The caller must have verified the request's signature or
    /// credential first; passing an app id lifted from an unverified header would let anyone name
    /// any owner.
    /// </para>
    /// </summary>
    /// <param name="appId">The authenticated app identity.</param>
    /// <param name="cancellationToken">Cancels the resolution.</param>
    /// <returns>The caller's owner key, or <c>null</c> to refuse the call.</returns>
    ValueTask<LifecycleOwnerKey?> ResolveCallerAsync(string appId, CancellationToken cancellationToken = default);
}
