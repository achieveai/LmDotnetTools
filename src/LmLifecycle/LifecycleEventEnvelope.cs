using System.Text.Json;
using System.Text.Json.Serialization;
using AchieveAi.LmDotnetTools.LmLifecycle.Serialization;

namespace AchieveAi.LmDotnetTools.LmLifecycle;

/// <summary>
/// One lifecycle event as it appears on the wire: stable identity, its position in a source
/// stream, when it happened, what it correlates to, and an opaque payload.
/// </summary>
/// <remarks>
/// <para>
/// <b>Source identity, not delivery identity.</b> Everything on this type describes the event as
/// the producer created it. It is minted once, before fan-out, and every subscriber receives the
/// same values — which is what makes a retry a re-send of an identical body rather than a new
/// event, and what keeps a signature computed over those bytes valid. Per-subscriber identity
/// lives on <see cref="LifecycleDeliveryEnvelope"/> and is assigned <em>after</em> filtering.
/// </para>
/// <para>
/// <b>The payload is deliberately opaque.</b> Holding it as raw JSON is what lets a subscriber
/// forward, store, and re-emit an event whose type it was never compiled against. Use
/// <see cref="LifecycleSerializer.TryReadPayload{T}"/> to project it onto a typed payload when the
/// event type is one this build knows.
/// </para>
/// <para>
/// Lifecycle envelopes are not conversation messages. They deliberately do not implement the core
/// message abstraction and never enter message persistence — this is a closed wire vocabulary, not
/// another message kind flowing through the agent loop.
/// </para>
/// </remarks>
public sealed record LifecycleEventEnvelope
{
    /// <summary>
    /// The protocol major that governs this envelope's shape. See <see cref="LifecycleProtocol"/>.
    /// </summary>
    [JsonPropertyName("schema_major")]
    [JsonRequired]
    public int SchemaMajor { get; set; } = LifecycleProtocol.CurrentMajor;

    /// <summary>
    /// Globally unique identity for this event, assigned once at creation and never regenerated.
    /// </summary>
    /// <remarks>
    /// Two deliveries carrying the same value are the same event. A subscriber may use it to
    /// deduplicate a retry.
    /// </remarks>
    [JsonPropertyName("event_id")]
    [JsonRequired]
    public string EventId { get; set; } = string.Empty;

    /// <summary>
    /// The open discriminator naming the payload shape. See <see cref="LifecycleEventTypes"/>.
    /// </summary>
    [JsonPropertyName("event_type")]
    [JsonRequired]
    public string EventType { get; set; } = string.Empty;

    /// <summary>
    /// The stream whose ordering this event participates in — <c>thread:{id}</c> or
    /// <c>sandbox:{id}</c>. See <see cref="LifecycleSourceStream"/>.
    /// </summary>
    /// <remarks>
    /// This is an ordering key, never an authorization subject. An owner is never inferred from it.
    /// </remarks>
    [JsonPropertyName("source_stream_id")]
    [JsonRequired]
    public string SourceStreamId { get; set; } = string.Empty;

    /// <summary>
    /// This event's position within <see cref="SourceStreamId"/>, starting at <c>1</c> and
    /// increasing by one per event within a producer epoch.
    /// </summary>
    /// <remarks>
    /// A missing ordinal is a dropped event. Because delivery is best-effort and bounded, gaps are
    /// expected and are the intended way to notice loss.
    /// </remarks>
    [JsonPropertyName("source_sequence")]
    [JsonRequired]
    public long SourceSequence { get; set; }

    /// <summary>
    /// Identifies the producer incarnation that allocated <see cref="SourceSequence"/>.
    /// </summary>
    /// <remarks>
    /// Counters restart when a producer restarts. A changed epoch tells a subscriber the ordinals
    /// started over, so a reset is never mistaken for a gap.
    /// </remarks>
    [JsonPropertyName("producer_epoch")]
    [JsonRequired]
    public string ProducerEpoch { get; set; } = string.Empty;

    /// <summary>When the event occurred, as observed by the producer, in UTC.</summary>
    /// <remarks>
    /// Encoded to a fixed ISO 8601 UTC form so the same instant always produces the same bytes.
    /// Wall-clock time is descriptive; <see cref="SourceSequence"/>, not this, defines order.
    /// </remarks>
    [JsonPropertyName("occurred_at")]
    [JsonRequired]
    [JsonConverter(typeof(CanonicalTimestampConverter))]
    public DateTimeOffset OccurredAt { get; set; }

    /// <summary>The identifiers this event correlates to. Absent when none apply.</summary>
    [JsonPropertyName("correlation")]
    public LifecycleCorrelation? Correlation { get; set; }

    /// <summary>
    /// The event body, held as raw JSON so an unrecognized event type survives a round trip intact.
    /// </summary>
    [JsonPropertyName("payload")]
    public JsonElement? Payload { get; set; }

    /// <summary>
    /// Indicates whether this build can project <see cref="Payload"/> onto a typed payload.
    /// </summary>
    /// <remarks>
    /// A <see langword="false"/> result is not an error and must never stop an event from being
    /// forwarded or stored.
    /// </remarks>
    [JsonIgnore]
    public bool IsKnownEventType => LifecycleEventTypes.IsKnown(EventType);

    /// <summary>
    /// Throws when the envelope is structurally unusable.
    /// </summary>
    /// <exception cref="LifecycleContractException">
    /// A required identifier is empty, <see cref="SourceSequence"/> is not positive,
    /// <see cref="SourceStreamId"/> is not a <c>{kind}:{id}</c> pair, or
    /// <see cref="SchemaMajor"/> is not supported by this build.
    /// </exception>
    /// <remarks>
    /// This checks the envelope, not the payload. An unrecognized <see cref="EventType"/> is valid
    /// by design.
    /// </remarks>
    public void EnsureValid()
    {
        RequireNonEmpty(EventId, nameof(EventId));
        RequireNonEmpty(EventType, nameof(EventType));
        RequireNonEmpty(ProducerEpoch, nameof(ProducerEpoch));

        if (!LifecycleSourceStream.TryParse(SourceStreamId, out _, out _))
        {
            throw new LifecycleContractException(
                $"'{nameof(SourceStreamId)}' must have the form '{{kind}}:{{id}}' with both parts non-empty."
            );
        }

        if (SourceSequence <= 0)
        {
            throw new LifecycleContractException($"'{nameof(SourceSequence)}' must be positive; ordinals start at 1.");
        }

        if (!LifecycleProtocol.IsSupported(SchemaMajor))
        {
            throw new LifecycleContractException(
                $"Protocol major {SchemaMajor} is not supported by this build. "
                    + "Majors must be agreed at registration, not discovered per event."
            );
        }
    }

    private static void RequireNonEmpty(string value, string memberName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new LifecycleContractException($"'{memberName}' must be non-empty.");
        }
    }
}
