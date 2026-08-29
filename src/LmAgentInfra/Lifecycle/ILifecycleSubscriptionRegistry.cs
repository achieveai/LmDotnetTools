namespace AchieveAi.LmDotnetTools.LmAgentInfra.Lifecycle;

/// <summary>
/// The lifecycle delivery control plane: register, rotate, revoke. Deliberately minimal — there is
/// no broad inspection API (ADR 0005), because "list every subscription" is a cross-tenant
/// disclosure surface that nothing in the design needs.
/// <para>
/// Every method takes the caller's server-resolved <see cref="LifecycleOwnerKey"/> as its first
/// argument rather than reading an owner off the request. That is the whole authorization model: an
/// implementation must never locate a subscription by id alone, because doing so would let anyone
/// holding an id act on someone else's subscription.
/// </para>
/// </summary>
public interface ILifecycleSubscriptionRegistry
{
    /// <summary>
    /// Registers a subscription for <paramref name="owner"/> and mints its signing secret.
    /// </summary>
    /// <param name="owner">The caller's server-resolved owner. Never taken from the request body.</param>
    /// <param name="ownerAppId">
    /// The authenticated app id <paramref name="owner"/> was resolved from, retained on the
    /// subscription so later operations can re-resolve and fail closed on disagreement.
    /// </param>
    /// <param name="request">The requested callback, capabilities, and event types.</param>
    /// <returns>The subscription plus its plaintext secret — returned once and never again.</returns>
    /// <exception cref="LifecycleSubscriptionRejectedException">
    /// The callback, a capability, an event type, or the subscription count was refused.
    /// </exception>
    LifecycleSubscriptionGrant Register(
        LifecycleOwnerKey owner,
        string ownerAppId,
        LifecycleSubscriptionRequest request
    );

    /// <summary>
    /// Mints a new signing secret for a subscription, keeping the outgoing key acceptable for
    /// <see cref="LifecycleDeliveryOptions.KeyRotationOverlap"/> so deliveries already in flight are
    /// not rejected mid-rotation.
    /// </summary>
    /// <param name="owner">The caller's server-resolved owner.</param>
    /// <param name="subscriptionId">The subscription to rotate.</param>
    /// <returns>The subscription plus its new plaintext secret.</returns>
    /// <exception cref="LifecycleSubscriptionRejectedException">
    /// <see cref="LifecycleSubscriptionRejection.NotAuthorized"/> when the subscription does not
    /// exist <em>or</em> belongs to another owner — the two are indistinguishable on purpose.
    /// </exception>
    LifecycleSubscriptionGrant Rotate(LifecycleOwnerKey owner, string subscriptionId);

    /// <summary>
    /// Ends a rotation overlap immediately, dropping the previous key from the active set. This is
    /// the compromise response: it stops a leaked outgoing key from verifying now rather than when
    /// the window happens to close.
    /// </summary>
    /// <param name="owner">The caller's server-resolved owner.</param>
    /// <param name="subscriptionId">The subscription whose previous key is dropped.</param>
    /// <exception cref="LifecycleSubscriptionRejectedException">
    /// <see cref="LifecycleSubscriptionRejection.NotAuthorized"/> when unknown or not the caller's.
    /// </exception>
    void RevokePreviousKey(LifecycleOwnerKey owner, string subscriptionId);

    /// <summary>
    /// Removes a subscription. Deliveries already queued for it are abandoned rather than flushed:
    /// the caller has said it no longer wants events, and continuing to send them to a revoked
    /// endpoint is the opposite of what revocation means.
    /// </summary>
    /// <param name="owner">The caller's server-resolved owner.</param>
    /// <param name="subscriptionId">The subscription to remove.</param>
    /// <exception cref="LifecycleSubscriptionRejectedException">
    /// <see cref="LifecycleSubscriptionRejection.NotAuthorized"/> when unknown or not the caller's.
    /// </exception>
    void Unregister(LifecycleOwnerKey owner, string subscriptionId);

    /// <summary>
    /// The live subscriptions belonging to <paramref name="owner"/>. This is the delivery pipeline's
    /// fan-out lookup, not a caller-facing listing — it is reached only after an event's owner has
    /// been resolved, so it never widens anyone's view.
    /// </summary>
    /// <param name="owner">The owner whose subscriptions should receive an event.</param>
    /// <returns>A snapshot; concurrent registration or revocation does not disturb an iteration.</returns>
    IReadOnlyList<LifecycleSubscription> ForOwner(LifecycleOwnerKey owner);

    /// <summary>
    /// Looks up one subscription, scoped to its owner.
    /// </summary>
    /// <param name="owner">The caller's server-resolved owner.</param>
    /// <param name="subscriptionId">The subscription to find.</param>
    /// <param name="subscription">The subscription when found and owned by the caller.</param>
    /// <returns>
    /// <c>true</c> when the subscription exists <em>and</em> belongs to <paramref name="owner"/>;
    /// otherwise <c>false</c>, with no distinction between "absent" and "someone else's".
    /// </returns>
    bool TryGet(LifecycleOwnerKey owner, string subscriptionId, out LifecycleSubscription? subscription);
}
