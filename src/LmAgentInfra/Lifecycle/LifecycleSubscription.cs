using AchieveAi.LmDotnetTools.LmAgentInfra.Webhooks;
using AchieveAi.LmDotnetTools.LmLifecycle;

namespace AchieveAi.LmDotnetTools.LmAgentInfra.Lifecycle;

/// <summary>
/// What a caller asks for when registering a lifecycle subscription.
/// <para>
/// Note what is <b>absent</b>: there is no owner field and no signing secret. The owner is resolved
/// server-side from the authenticated caller (<see cref="ILifecycleOwnerResolver"/>), so a caller
/// cannot name the tenancy it wants to receive — not because the runtime validates such a field, but
/// because there is nowhere to put one. The secret is minted by the host (see
/// <see cref="LifecycleSubscriptionGrant"/>) rather than supplied here, which removes the entire
/// class of weak or cross-owner-reused caller-chosen keys the host would otherwise have no way to
/// detect.
/// </para>
/// </summary>
public sealed record LifecycleSubscriptionRequest
{
    /// <summary>
    /// Where deliveries are POSTed. Validated against
    /// <see cref="LifecycleDeliveryOptions.AllowedCallbackHosts"/> and
    /// <see cref="LifecycleDeliveryOptions.RequireHttpsCallbacks"/> — an arbitrary URL is refused, so
    /// registration cannot turn the host into an open outbound relay.
    /// </summary>
    public required Uri CallbackUri { get; init; }

    /// <summary>
    /// Capabilities the caller is asking for, from <see cref="LifecycleCapabilities"/>. A capability
    /// the host does not grant is simply absent from the resulting subscription, and absent means
    /// denied. An unrecognized name — including any wildcard — is rejected outright rather than
    /// ignored, because silently dropping an unknown grant is how a caller ends up believing it has
    /// permission it was never given.
    /// </summary>
    public IReadOnlyList<string> Capabilities { get; init; } = [];

    /// <summary>
    /// Event types to receive, from <see cref="LifecycleEventTypes"/>. Empty means every event type
    /// <em>within the owner's own scope</em> — safe because the owner filter already bounds what can
    /// ever be delivered, and enumerating would silently exclude event types added later. A wildcard
    /// token is rejected: it reads as "everything, including things not yet invented," which is a
    /// broader grant than the caller can have been assessed for.
    /// </summary>
    public IReadOnlyList<string> EventTypes { get; init; } = [];
}

/// <summary>
/// A live subscription: one owner's callback endpoint, its granted capabilities, and the signing
/// secret its deliveries are authenticated with.
/// </summary>
public sealed class LifecycleSubscription
{
    private readonly HashSet<string> _capabilities;
    private readonly HashSet<string> _eventTypes;

    /// <summary>Creates a subscription. Constructed by the registry, never from request data alone.</summary>
    /// <param name="subscriptionId">Server-minted identifier for this subscription.</param>
    /// <param name="owner">The resolved owner this subscription is scoped to.</param>
    /// <param name="ownerAppId">
    /// The authenticated app id the owner was resolved from, retained so later operations can
    /// re-resolve it and fail closed if the host's answer has changed since registration.
    /// </param>
    /// <param name="callbackUri">Validated callback endpoint.</param>
    /// <param name="signingSecret">Secret whose current key signs this subscription's deliveries.</param>
    /// <param name="capabilities">Granted capabilities; anything not listed is denied.</param>
    /// <param name="eventTypes">Event types to deliver; empty means all within the owner's scope.</param>
    /// <param name="createdAtUtc">When the subscription was registered.</param>
    public LifecycleSubscription(
        string subscriptionId,
        LifecycleOwnerKey owner,
        string ownerAppId,
        Uri callbackUri,
        WebhookSigningSecret signingSecret,
        IEnumerable<string> capabilities,
        IEnumerable<string> eventTypes,
        DateTimeOffset createdAtUtc
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subscriptionId);
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerAppId);
        ArgumentNullException.ThrowIfNull(callbackUri);
        ArgumentNullException.ThrowIfNull(signingSecret);
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentNullException.ThrowIfNull(eventTypes);

        SubscriptionId = subscriptionId;
        Owner = owner;
        OwnerAppId = ownerAppId;
        CallbackUri = callbackUri;
        SigningSecret = signingSecret;
        CreatedAtUtc = createdAtUtc;
        _capabilities = new HashSet<string>(capabilities, StringComparer.Ordinal);
        _eventTypes = new HashSet<string>(eventTypes, StringComparer.Ordinal);
    }

    /// <summary>Server-minted identifier, used to rotate or revoke this subscription.</summary>
    public string SubscriptionId { get; }

    /// <summary>The owner this subscription is scoped to. Events from any other owner never reach it.</summary>
    public LifecycleOwnerKey Owner { get; }

    /// <summary>
    /// The authenticated app id <see cref="Owner"/> was resolved from at registration time. Kept so
    /// every later operation can re-resolve and compare: a subscription registered under an app id
    /// that no longer maps to the same owner is treated as unauthorized rather than grandfathered.
    /// </summary>
    public string OwnerAppId { get; }

    /// <summary>Validated endpoint deliveries are POSTed to.</summary>
    public Uri CallbackUri { get; }

    /// <summary>
    /// Signing secret for this subscription's deliveries. Per-subscription, not per-host: revoking or
    /// rotating one subscriber's key leaves every other subscriber unaffected.
    /// </summary>
    public WebhookSigningSecret SigningSecret { get; }

    /// <summary>When the subscription was registered.</summary>
    public DateTimeOffset CreatedAtUtc { get; }

    /// <summary>Capabilities granted to this subscription. Anything absent is denied.</summary>
    public IReadOnlyCollection<string> Capabilities => _capabilities;

    /// <summary>
    /// Event types this subscription receives, or empty for all types within the owner's scope.
    /// </summary>
    public IReadOnlyCollection<string> EventTypes => _eventTypes;

    /// <summary>Whether <paramref name="capability"/> was granted. Absent means denied.</summary>
    /// <param name="capability">A name from <see cref="LifecycleCapabilities"/>.</param>
    public bool HasCapability(string capability) => _capabilities.Contains(capability);

    /// <summary>
    /// Whether this subscription's <em>type</em> filter accepts <paramref name="eventType"/>. This is
    /// a preference filter only — it says nothing about ownership. The owner check is separate and
    /// must always be applied as well; conflating the two is how a filter change turns into a
    /// cross-tenant leak.
    /// </summary>
    /// <param name="eventType">A value from <see cref="LifecycleEventTypes"/>.</param>
    public bool AcceptsEventType(string eventType) => _eventTypes.Count == 0 || _eventTypes.Contains(eventType);

    /// <summary>Redacted by design — this object holds a signing secret.</summary>
    /// <returns>An identifier-only marker containing no key material.</returns>
    public override string ToString() => $"{nameof(LifecycleSubscription)}[{SubscriptionId}, owner={Owner.Value}]";
}

/// <summary>
/// The result of registering or rotating a subscription: the subscription itself plus the plaintext
/// signing secret, which is returned <b>once</b> and never retrievable afterwards. A caller that
/// loses it must rotate.
/// <para>
/// Deliberately a class and not a record so no generated <c>ToString</c>/equality can render the
/// secret into a log — the same reasoning as
/// <see cref="AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox.SandboxCredential"/> and
/// <see cref="WebhookSigningSecret"/>.
/// </para>
/// </summary>
public sealed class LifecycleSubscriptionGrant
{
    /// <summary>Creates a grant.</summary>
    /// <param name="subscription">The registered or rotated subscription.</param>
    /// <param name="secret">The plaintext signing secret. SECRET — never log this value.</param>
    public LifecycleSubscriptionGrant(LifecycleSubscription subscription, string secret)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);

        Subscription = subscription;
        Secret = secret;
    }

    /// <summary>The subscription this grant belongs to.</summary>
    public LifecycleSubscription Subscription { get; }

    /// <summary>
    /// The plaintext signing secret the subscriber verifies deliveries with. SECRET — never log this
    /// value, and never return it from any endpoint other than the one that minted it.
    /// </summary>
    public string Secret { get; }

    /// <summary>Redacted by design — this object exists to carry a secret exactly once.</summary>
    /// <returns>An identifier-only marker containing no key material.</returns>
    public override string ToString() =>
        $"{nameof(LifecycleSubscriptionGrant)}[{Subscription.SubscriptionId}, Secret = [REDACTED]]";
}

/// <summary>
/// Why a subscription operation was refused. Enumerated rather than expressed as message text so a
/// controller can map an outcome to a status code without parsing prose.
/// </summary>
public enum LifecycleSubscriptionRejection
{
    /// <summary>The callback URL is not a well-formed absolute HTTP(S) URL.</summary>
    InvalidCallback,

    /// <summary>
    /// The callback host is not in <see cref="LifecycleDeliveryOptions.AllowedCallbackHosts"/>. An
    /// empty allow-list refuses everything, which is the intended posture when delivery is enabled
    /// without a destination configured.
    /// </summary>
    CallbackNotAllowed,

    /// <summary>The callback is plaintext HTTP while HTTPS is required.</summary>
    CallbackNotHttps,

    /// <summary>A requested capability is not a recognized <see cref="LifecycleCapabilities"/> name.</summary>
    UnknownCapability,

    /// <summary>
    /// A requested capability or event type used a wildcard. Scope is granted explicitly; a wildcard
    /// asks for permissions that do not exist yet.
    /// </summary>
    WildcardNotGranted,

    /// <summary>A requested event type is not a recognized <see cref="LifecycleEventTypes"/> value.</summary>
    UnknownEventType,

    /// <summary><see cref="LifecycleDeliveryOptions.MaxSubscriptions"/> is already reached.</summary>
    CapacityExceeded,

    /// <summary>
    /// The caller is not entitled to act on the target subscription. Returned identically for a
    /// subscription owned by someone else and for one that does not exist, so the API cannot be used
    /// to probe which subscription ids are real.
    /// </summary>
    NotAuthorized,
}

/// <summary>
/// Thrown when a subscription operation is refused. Carries a
/// <see cref="LifecycleSubscriptionRejection"/> so callers branch on the reason rather than on the
/// message.
/// </summary>
public sealed class LifecycleSubscriptionRejectedException : Exception
{
    /// <summary>Creates the exception with a machine-readable reason.</summary>
    /// <param name="reason">Why the operation was refused.</param>
    /// <param name="message">Operator-facing detail. Must not contain secrets or full callback URLs.</param>
    public LifecycleSubscriptionRejectedException(LifecycleSubscriptionRejection reason, string message)
        : base(message) => Reason = reason;

    /// <summary>Why the operation was refused.</summary>
    public LifecycleSubscriptionRejection Reason { get; }
}
