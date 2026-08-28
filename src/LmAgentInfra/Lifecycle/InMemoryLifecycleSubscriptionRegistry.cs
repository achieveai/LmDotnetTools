using System.Collections.Concurrent;
using System.Security.Cryptography;
using AchieveAi.LmDotnetTools.LmAgentInfra.Webhooks;
using AchieveAi.LmDotnetTools.LmLifecycle;

namespace AchieveAi.LmDotnetTools.LmAgentInfra.Lifecycle;

/// <summary>
/// The process-local <see cref="ILifecycleSubscriptionRegistry"/> — the storage behind the
/// register / rotate / revoke control plane (ADR 0005).
/// <para>
/// Holding subscriptions in memory matches storage lifetime to delivery semantics rather than
/// settling for less. Delivery is best-effort with no durable outbox, replay, or backfill, so a
/// subscription that survived a restart would promise a continuity the pipeline behind it cannot
/// honour: the subscriber would keep its secret and its endpoint while silently missing every event
/// produced before the process came back. Losing the registration instead makes the discontinuity
/// unmissable — the subscriber re-registers, and gets a fresh secret and a fresh delivery sequence
/// with it.
/// </para>
/// <para>
/// There is deliberately no lookup by subscription id alone anywhere in this type. Every read and
/// every mutation goes through an owner-scoped path, so no code path exists that could act on
/// another owner's subscription even by accident, and a later refactor cannot introduce one without
/// adding a method that does not currently exist.
/// </para>
/// <para>
/// The owner passed to every method must come from the host's own authentication — the
/// <c>HttpContext.User</c> principal an endpoint resolved through
/// <see cref="ILifecycleOwnerResolver"/> — and never from a request header or body. This type
/// cannot tell a verified identity from an asserted one and does not try: it treats the owner it is
/// handed as already proven, so a host that derives one from a header has given every caller the
/// ability to name its own owner. Webhook signature verification does not satisfy this requirement.
/// It authenticates deliveries leaving the host and populates no principal at all, so an endpoint in
/// front of this registry refuses every call until the host wires a real authentication scheme —
/// which is the intended direction, and is stated here so it is not met as an unexplained 403.
/// </para>
/// </summary>
public sealed class InMemoryLifecycleSubscriptionRegistry : ILifecycleSubscriptionRegistry
{
    /// <summary>
    /// Hex length of a minted signing secret — 256 bits, matching the random fallback
    /// <see cref="WebhookSigningSecret"/> generates for itself, so a minted key and a generated one
    /// are the same strength.
    /// </summary>
    private const int SecretHexLength = 64;

    /// <summary>
    /// Hex length of a minted subscription id — 128 bits from a CSPRNG. Every lookup here is
    /// owner-scoped, so the id is not a bearer token today; it is minted this way because a
    /// guessable id is one missing owner check away from becoming one, and <c>Guid.NewGuid</c> makes
    /// no randomness guarantee worth relying on for that.
    /// </summary>
    private const int SubscriptionIdHexLength = 32;

    /// <summary>
    /// The single refusal text for a subscription that does not exist and for one that belongs to
    /// someone else. A constant rather than an interpolated message on purpose: any per-case detail,
    /// down to the phrasing, would let a caller enumerate which subscription ids are real.
    /// </summary>
    private const string NotAuthorizedMessage = "The subscription is unknown or is not owned by the caller.";

    private readonly ConcurrentDictionary<string, LifecycleSubscription> _subscriptions = new(StringComparer.Ordinal);

    // Serializes the capacity check with the insert it guards. Reading Count and then adding without
    // this gate lets N concurrent registrations all observe the same last free slot and all take it,
    // which is how a configured maximum quietly degrades into a suggestion. Only the mutation is
    // inside the gate: reads stay lock-free, and a removal only ever frees a slot, so it can never
    // invalidate a check in progress.
    private readonly object _capacityGate = new();

    private readonly LifecycleDeliveryOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<InMemoryLifecycleSubscriptionRegistry> _logger;

    /// <summary>
    /// Creates the registry and validates <paramref name="options"/> immediately, so a
    /// misconfiguration surfaces at startup rather than at the first registration — or, worse, at
    /// the first delivery attempt against a subscriber that already believes it is subscribed.
    /// </summary>
    /// <param name="options">Delivery configuration; validated here.</param>
    /// <param name="logger">Sink for control-plane diagnostics. Never receives key material.</param>
    /// <param name="timeProvider">
    /// Clock behind subscription timestamps and every minted secret's rotation overlap. Defaults to
    /// <see cref="TimeProvider.System"/>; tests inject a manual clock so an overlap can be expired
    /// deterministically instead of waited out.
    /// </param>
    /// <exception cref="InvalidOperationException">A configured value is out of range.</exception>
    public InMemoryLifecycleSubscriptionRegistry(
        LifecycleDeliveryOptions options,
        ILogger<InMemoryLifecycleSubscriptionRegistry> logger,
        TimeProvider? timeProvider = null
    )
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        // Deliberately not gated on options.Enabled. Whether lifecycle delivery runs at all is the
        // host's wiring decision — a host that leaves the feature off simply never registers this
        // service. Re-checking the flag here would mean a host that constructed the registry on
        // purpose (a staged rollout, a test harness) got a store that refuses everything for a
        // reason no error message would explain.
        options.Validate();

        _options = options;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public LifecycleSubscriptionGrant Register(
        LifecycleOwnerKey owner,
        string ownerAppId,
        LifecycleSubscriptionRequest request
    )
    {
        ArgumentNullException.ThrowIfNull(owner);

        // An argument fault rather than a rejection: the app id is host-resolved and the request is
        // materialized by the caller's own endpoint, so a blank or absent one is a wiring defect on
        // this side of the boundary, not something a remote party can provoke.
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerAppId);
        ArgumentNullException.ThrowIfNull(request);

        var callbackUri = ValidateCallback(request.CallbackUri);

        // A JSON null for either list arrives here as a null reference despite the non-nullable
        // declaration. Normalizing before anything reads them keeps a malformed body on a rejection
        // path instead of surfacing as a NullReferenceException from inside validation.
        var capabilities = request.Capabilities ?? [];
        var eventTypes = request.EventTypes ?? [];

        // Wildcards are screened across both lists before membership is checked, so `*` is reported
        // as the deliberate over-ask it is rather than as a mistyped name.
        RejectWildcards(capabilities);
        RejectWildcards(eventTypes);
        ValidateCapabilities(capabilities);
        ValidateEventTypes(eventTypes);

        // Minted outside the capacity gate: key generation has nothing to serialize against, and
        // holding a lock across it would widen the only contended section in the type. The cost is
        // one discarded secret on a registration that is then refused for capacity.
        var secret = RandomNumberGenerator.GetHexString(SecretHexLength);
        var subscription = new LifecycleSubscription(
            RandomNumberGenerator.GetHexString(SubscriptionIdHexLength, lowercase: true),
            owner,
            ownerAppId,
            callbackUri,
            // The registry's clock, not the ambient one — that is what lets a test expire this
            // subscription's rotation overlap without sleeping.
            new WebhookSigningSecret(secret, _timeProvider),
            capabilities,
            eventTypes,
            _timeProvider.GetUtcNow()
        );

        lock (_capacityGate)
        {
            if (_subscriptions.Count >= _options.MaxSubscriptions)
            {
                throw Rejected(
                    LifecycleSubscriptionRejection.CapacityExceeded,
                    $"The registry already holds its maximum of {_options.MaxSubscriptions} subscriptions."
                );
            }

            // Conditional rather than an indexer assignment: a 128-bit id colliding is not worth a
            // retry loop, but silently replacing whatever that id already pointed at would be.
            if (!_subscriptions.TryAdd(subscription.SubscriptionId, subscription))
            {
                throw new InvalidOperationException("A minted subscription id was already in use.");
            }
        }

        // Host, never the full callback URL: a path or query can carry a per-subscriber token, and
        // ADR 0005 keeps diagnostics to opaque identifiers for exactly that reason.
        _logger.LogInformation(
            "Lifecycle subscription {SubscriptionId} registered for owner {Owner} delivering to host {CallbackHost}",
            subscription.SubscriptionId,
            owner.Value,
            callbackUri.Host
        );

        return new LifecycleSubscriptionGrant(subscription, secret);
    }

    /// <inheritdoc />
    public LifecycleSubscriptionGrant Rotate(LifecycleOwnerKey owner, string subscriptionId)
    {
        var subscription = Authorize(owner, subscriptionId);

        var secret = RandomNumberGenerator.GetHexString(SecretHexLength);
        subscription.SigningSecret.Rotate(secret, _options.KeyRotationOverlap);

        _logger.LogInformation(
            "Lifecycle subscription {SubscriptionId} signing key rotated for owner {Owner} with a {OverlapSeconds}s overlap",
            subscription.SubscriptionId,
            owner.Value,
            _options.KeyRotationOverlap.TotalSeconds
        );

        // The plaintext leaves the process exactly once, here. Nothing on the subscription can
        // return it again, so a caller that loses it has to rotate rather than ask.
        return new LifecycleSubscriptionGrant(subscription, secret);
    }

    /// <inheritdoc />
    public void RevokePreviousKey(LifecycleOwnerKey owner, string subscriptionId)
    {
        var subscription = Authorize(owner, subscriptionId);

        subscription.SigningSecret.RevokePrevious();

        // Warning rather than Information: this is the compromise response, and an operator reading
        // back through the log wants it to stand out from routine rotation.
        _logger.LogWarning(
            "Previous signing key revoked for lifecycle subscription {SubscriptionId} of owner {Owner}",
            subscription.SubscriptionId,
            owner.Value
        );
    }

    /// <inheritdoc />
    public void Unregister(LifecycleOwnerKey owner, string subscriptionId)
    {
        var subscription = Authorize(owner, subscriptionId);

        // A false return means a concurrent Unregister for the same subscription won the race.
        // Removal is idempotent, and the log line belongs to whichever call actually removed it.
        if (_subscriptions.TryRemove(subscription.SubscriptionId, out _))
        {
            _logger.LogInformation(
                "Lifecycle subscription {SubscriptionId} removed for owner {Owner}",
                subscription.SubscriptionId,
                owner.Value
            );
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<LifecycleSubscription> ForOwner(LifecycleOwnerKey owner)
    {
        ArgumentNullException.ThrowIfNull(owner);

        // Materialized rather than a deferred LINQ filter: the delivery pipeline iterates this while
        // registrations and revocations keep arriving, and a lazy sequence would let the fan-out set
        // shift under it mid-event.
        //
        // A scan rather than an owner-keyed index: MaxSubscriptions bounds the entire table (32 by
        // default), so a second index would cost more in invalidation complexity than the scan costs
        // to run.
        var matches = new List<LifecycleSubscription>();
        foreach (var entry in _subscriptions)
        {
            if (entry.Value.Owner == owner)
            {
                matches.Add(entry.Value);
            }
        }

        return matches;
    }

    /// <inheritdoc />
    public bool TryGet(LifecycleOwnerKey owner, string subscriptionId, out LifecycleSubscription? subscription)
    {
        ArgumentNullException.ThrowIfNull(owner);

        subscription = null;

        // A blank id is a refusal, not an argument fault: unlike the owner it arrives from a remote
        // caller's route or body, and throwing would turn a malformed request into a host error.
        if (string.IsNullOrWhiteSpace(subscriptionId))
        {
            return false;
        }

        if (!_subscriptions.TryGetValue(subscriptionId, out var found))
        {
            return false;
        }

        // This comparison is the entire authorization model. LifecycleOwnerKey is a record, so the
        // generated equality is ordinal string equality over its opaque value — and a mismatch
        // answers exactly as an absent id does, leaving nothing for a caller to distinguish.
        if (found.Owner != owner)
        {
            return false;
        }

        subscription = found;
        return true;
    }

    /// <summary>
    /// Resolves a subscription for a mutating operation, or refuses. "Unknown" and "not yours"
    /// produce the identical rejection — same reason, same message — so the control plane cannot be
    /// turned into an oracle for which subscription ids exist.
    /// </summary>
    private LifecycleSubscription Authorize(LifecycleOwnerKey owner, string subscriptionId)
    {
        ArgumentNullException.ThrowIfNull(owner);

        if (TryGet(owner, subscriptionId, out var subscription) && subscription is not null)
        {
            return subscription;
        }

        // Logged here rather than at each call site because a refused control-plane operation is the
        // probing signal an operator cares about. The log carries the owner and the id; the caller's
        // refusal deliberately carries neither.
        _logger.LogWarning(
            "Refused a lifecycle subscription operation for owner {Owner} on subscription {SubscriptionId}",
            owner.Value,
            subscriptionId
        );

        throw Rejected(LifecycleSubscriptionRejection.NotAuthorized, NotAuthorizedMessage);
    }

    /// <summary>
    /// Validates a callback endpoint against the fail-closed rules registration exists to enforce,
    /// returning it unchanged when it passes.
    /// <para>
    /// The rules themselves live in <see cref="LifecycleDestinationPolicy"/> because the delivery
    /// pipeline re-applies them at enqueue and on every attempt; this method only maps a verdict onto
    /// the typed rejection a caller sees.
    /// </para>
    /// </summary>
    private Uri ValidateCallback(Uri callbackUri)
    {
        var verdict = LifecycleDestinationPolicy.Evaluate(callbackUri, _options);
        return verdict switch
        {
            LifecycleDestinationVerdict.Allowed => callbackUri,

            LifecycleDestinationVerdict.NotAbsolute => throw Rejected(
                LifecycleSubscriptionRejection.InvalidCallback,
                "The callback must be an absolute URL."
            ),

            LifecycleDestinationVerdict.UnsupportedScheme => throw Rejected(
                LifecycleSubscriptionRejection.InvalidCallback,
                "The callback must use the http or https scheme."
            ),

            LifecycleDestinationVerdict.CarriesUserInfo => throw Rejected(
                LifecycleSubscriptionRejection.InvalidCallback,
                "The callback must not carry userinfo credentials."
            ),

            LifecycleDestinationVerdict.NotHttps => throw Rejected(
                LifecycleSubscriptionRejection.CallbackNotHttps,
                "The callback must use https."
            ),

            // The host is named because it is the one part of the URL that is safe to disclose and
            // the only part an operator needs in order to fix the allow-list.
            _ => throw Rejected(
                LifecycleSubscriptionRejection.CallbackNotAllowed,
                $"The callback host '{callbackUri.Host}' is not an allowed callback host."
            ),
        };
    }

    /// <summary>
    /// Refuses any wildcard token. A wildcard reads as "everything, including what does not exist
    /// yet", which is a broader grant than the caller can have been assessed for — and unlike an
    /// unknown name it cannot be a typo, so there is nothing to be lenient about.
    /// </summary>
    private static void RejectWildcards(IReadOnlyList<string> values)
    {
        foreach (var value in values)
        {
            if (value is not null && value.Contains('*'))
            {
                throw Rejected(
                    LifecycleSubscriptionRejection.WildcardNotGranted,
                    "A wildcard capability or event type is never granted."
                );
            }
        }
    }

    private static void ValidateCapabilities(IReadOnlyList<string> capabilities)
    {
        foreach (var capability in capabilities)
        {
            // Membership comes from LifecycleCapabilities rather than a copy held here: a private
            // list would go stale the day a capability is added there, and silently — it would
            // simply start refusing a grant that had become valid.
            //
            // Unknown is rejected, never quietly dropped, and unlike an unknown event type it is not
            // treated as forward compatibility. A caller whose request succeeded believes it holds
            // every capability it asked for and will build on that belief — sending approval
            // decisions that are then refused one at a time, far from the registration that actually
            // failed. Refusing here puts the failure where the mistake is.
            if (!LifecycleCapabilities.IsKnown(capability))
            {
                throw Rejected(
                    LifecycleSubscriptionRejection.UnknownCapability,
                    $"Capability '{capability ?? "(null)"}' is not granted by this host."
                );
            }
        }
    }

    private static void ValidateEventTypes(IReadOnlyList<string> eventTypes)
    {
        // An empty list is legal and means every type within the owner's scope, so there is nothing
        // to check — the loop simply does not run. The owner filter is what bounds that grant.
        foreach (var eventType in eventTypes)
        {
            if (!LifecycleEventTypes.IsKnown(eventType))
            {
                throw Rejected(
                    LifecycleSubscriptionRejection.UnknownEventType,
                    $"Event type '{eventType ?? "(null)"}' is not recognized by this host."
                );
            }
        }
    }

    /// <summary>
    /// Builds a rejection. Returned rather than thrown so call sites read as <c>throw</c>
    /// statements, which keeps the compiler's flow analysis exact.
    /// </summary>
    private static LifecycleSubscriptionRejectedException Rejected(
        LifecycleSubscriptionRejection reason,
        string message
    ) => new(reason, message);
}
