namespace AchieveAi.LmDotnetTools.LmAgentInfra.Lifecycle;

/// <summary>
/// Configuration for service-to-service lifecycle delivery (ADR 0005). Bound from the
/// <c>Lifecycle:Delivery</c> section.
/// <para>
/// Every limit here exists because the runtime hands a slow, hostile, or simply absent subscriber
/// endpoint the ability to consume host resources. Left unbounded, one wedged subscriber is enough
/// to stall the agent that produced the events. The defaults are deliberately conservative: a host
/// that wants more headroom opts into it explicitly.
/// </para>
/// </summary>
public sealed class LifecycleDeliveryOptions
{
    /// <summary>Configuration section name these options are bound from.</summary>
    public const string SectionName = "Lifecycle:Delivery";

    /// <summary>
    /// Master switch. <b>Default off.</b> Lifecycle delivery pushes agent-internal events to an
    /// external endpoint, so it stays dormant until a host deliberately turns it on; nothing about
    /// merely referencing this assembly should start shipping events anywhere.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Maximum live subscriptions across all owners. A registration beyond this is refused rather
    /// than accepted-and-starved, so a caller learns immediately instead of silently receiving
    /// nothing.
    /// </summary>
    public int MaxSubscriptions { get; set; } = 32;

    /// <summary>
    /// Capacity of the single intake channel <c>PublishAsync</c> writes into. Publishing never
    /// blocks the agent: when the intake is full the event is dropped and the loss surfaces as a
    /// gap in <c>source_sequence</c>, which is why subscribers must treat that field as
    /// gap-detecting rather than merely ordering.
    /// </summary>
    public int IntakeQueueCapacity { get; set; } = 1024;

    /// <summary>
    /// Maximum deliveries queued for one subscriber. Per-subscriber (not global) so a slow endpoint
    /// exhausts only its own budget — the bulkhead that keeps one bad subscriber from degrading the
    /// others.
    /// </summary>
    public int MaxQueuedDeliveriesPerSubscriber { get; set; } = 256;

    /// <summary>
    /// Byte ceiling on one subscriber's queue, enforced alongside
    /// <see cref="MaxQueuedDeliveriesPerSubscriber"/>. A count limit alone is not a memory limit:
    /// a few hundred large tool payloads can dwarf a few thousand small ones.
    /// </summary>
    public long MaxQueuedBytesPerSubscriber { get; set; } = 8L * 1024 * 1024;

    /// <summary>
    /// Total attempts per delivery, including the first. Exceeding it abandons the delivery, which
    /// the subscriber observes as a <c>delivery_sequence</c> gap.
    /// </summary>
    public int MaxAttempts { get; set; } = 4;

    /// <summary>Base delay for the exponential backoff between attempts.</summary>
    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Ceiling the exponential backoff saturates at.</summary>
    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Ceiling on a subscriber-supplied <c>Retry-After</c>. The header is honored as a hint, not an
    /// instruction: without a clamp a subscriber could pin a delivery worker for hours by returning
    /// 429 with an enormous value.
    /// </summary>
    public TimeSpan MaxRetryAfter { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Wall-clock budget for a delivery across all its attempts. Retries stop when it is exhausted
    /// even if <see cref="MaxAttempts"/> remains, so a long backoff cannot outlive the event's
    /// usefulness.
    /// </summary>
    public TimeSpan DeliveryDeadline { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Timeout for a single HTTP attempt.</summary>
    public TimeSpan AttemptTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long shutdown waits for in-flight deliveries to drain before abandoning them. Bounded
    /// deliberately: an unreachable subscriber must not be able to hang host shutdown.
    /// </summary>
    public TimeSpan ShutdownDrainTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Accepted clock skew on the signed request timestamp, matching the inbound webhook
    /// verifier's window so both directions agree on what "too old to replay" means.
    /// </summary>
    public TimeSpan TimestampTolerance { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Window during which a rotated-out signing secret is still accepted for verification. After
    /// it elapses — or immediately, on explicit revocation — the previous secret stops verifying.
    /// </summary>
    public TimeSpan KeyRotationOverlap { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// How long a quarantined callback destination stays quarantined, including for subscriptions
    /// registered after the quarantine was imposed.
    /// <para>
    /// Quarantine is held against the destination (scheme, host, port) rather than the subscription
    /// id, because subscription ids are server-minted and unique: a client that re-registers on
    /// failure — which is ordinary client behavior, not an attack — would otherwise be handed a fresh
    /// queue every time and never stay quarantined at all.
    /// </para>
    /// <para>
    /// Bounded rather than permanent so an endpoint that has genuinely been repaired can return by
    /// re-registering instead of requiring a host restart. Set it long enough that an auto-retrying
    /// client gives up first, and short enough that it is not an outage in its own right.
    /// </para>
    /// </summary>
    public TimeSpan QuarantineCooloff { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Hosts a callback URL may target. <b>Empty means no callback is allowed</b>, which is what
    /// keeps this fail-closed: an operator who enables delivery without naming a destination gets
    /// refused registrations, not an open outbound relay pointed at whatever a caller supplies.
    /// <para>
    /// The default is empty rather than pre-populated because the .NET configuration binder
    /// <i>appends</i> to a non-empty array instead of replacing it, so a seeded default would
    /// silently survive an operator's attempt to override it.
    /// </para>
    /// </summary>
    public string[] AllowedCallbackHosts { get; set; } = [];

    /// <summary>
    /// Requires callback URLs to use HTTPS. Deliveries carry conversation content and are
    /// authenticated by a shared secret, so plaintext is only ever acceptable against a loopback
    /// endpoint in local development — which is the sole reason this is a switch rather than a
    /// constant.
    /// </summary>
    public bool RequireHttpsCallbacks { get; set; } = true;

    /// <summary>
    /// Validates the configured values and throws on anything the runtime cannot honor, so a
    /// misconfiguration fails at startup instead of at the first delivery.
    /// </summary>
    /// <exception cref="InvalidOperationException">A value is out of range or internally
    /// inconsistent.</exception>
    public void Validate()
    {
        RequirePositive(MaxSubscriptions, nameof(MaxSubscriptions));
        RequirePositive(IntakeQueueCapacity, nameof(IntakeQueueCapacity));
        RequirePositive(MaxQueuedDeliveriesPerSubscriber, nameof(MaxQueuedDeliveriesPerSubscriber));
        RequirePositive(MaxAttempts, nameof(MaxAttempts));

        if (MaxQueuedBytesPerSubscriber <= 0)
        {
            throw Invalid(nameof(MaxQueuedBytesPerSubscriber), "must be greater than zero");
        }

        RequirePositive(RetryBaseDelay, nameof(RetryBaseDelay));
        RequirePositive(MaxRetryDelay, nameof(MaxRetryDelay));
        RequirePositive(MaxRetryAfter, nameof(MaxRetryAfter));
        RequirePositive(DeliveryDeadline, nameof(DeliveryDeadline));
        RequirePositive(AttemptTimeout, nameof(AttemptTimeout));
        RequirePositive(TimestampTolerance, nameof(TimestampTolerance));

        if (ShutdownDrainTimeout < TimeSpan.Zero)
        {
            throw Invalid(nameof(ShutdownDrainTimeout), "must not be negative");
        }

        if (KeyRotationOverlap < TimeSpan.Zero)
        {
            throw Invalid(nameof(KeyRotationOverlap), "must not be negative");
        }

        // Zero is legal and means "quarantine the queue but never hold the destination", which is the
        // pre-cool-off behavior. Negative is not: it would expire a quarantine before it was imposed.
        if (QuarantineCooloff < TimeSpan.Zero)
        {
            throw Invalid(nameof(QuarantineCooloff), "must not be negative");
        }

        if (MaxRetryDelay < RetryBaseDelay)
        {
            throw Invalid(
                nameof(MaxRetryDelay),
                $"must not be less than {nameof(RetryBaseDelay)} ({RetryBaseDelay})"
            );
        }

        // An attempt that cannot finish inside the delivery's own budget can never succeed, so this
        // combination would burn the deadline on a single doomed attempt.
        if (AttemptTimeout > DeliveryDeadline)
        {
            throw Invalid(
                nameof(AttemptTimeout),
                $"must not exceed {nameof(DeliveryDeadline)} ({DeliveryDeadline})"
            );
        }
    }

    private static void RequirePositive(int value, string name)
    {
        if (value <= 0)
        {
            throw Invalid(name, "must be greater than zero");
        }
    }

    private static void RequirePositive(TimeSpan value, string name)
    {
        if (value <= TimeSpan.Zero)
        {
            throw Invalid(name, "must be greater than zero");
        }
    }

    private static InvalidOperationException Invalid(string name, string requirement) =>
        new($"{SectionName}:{name} {requirement}.");
}
