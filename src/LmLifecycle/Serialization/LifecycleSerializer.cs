using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using AchieveAi.LmDotnetTools.LmLifecycle.Payloads;

namespace AchieveAi.LmDotnetTools.LmLifecycle.Serialization;

/// <summary>
/// Encodes and decodes lifecycle envelopes through <see cref="LifecycleJsonContext"/>.
/// </summary>
/// <remarks>
/// Callers should route every lifecycle encode and decode through this type rather than reaching
/// for <see cref="JsonSerializer"/> directly. A second set of options would be a second wire
/// format, and only one of them would match the bytes a signature was computed over.
/// </remarks>
public static class LifecycleSerializer
{
    /// <summary>The canonical options. Treat as read-only; mutating them changes the wire format.</summary>
    public static JsonSerializerOptions Options => LifecycleJsonContext.Default.Options;

    /// <summary>Encodes an event to its canonical UTF-8 bytes.</summary>
    /// <param name="envelope">The event to encode.</param>
    /// <returns>The canonical bytes. Sign and transmit these, not a re-serialization.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="envelope"/> is <see langword="null"/>.</exception>
    public static byte[] SerializeToUtf8Bytes(LifecycleEventEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        return JsonSerializer.SerializeToUtf8Bytes(envelope, LifecycleJsonContext.Default.LifecycleEventEnvelope);
    }

    /// <summary>Encodes an event to its canonical JSON text.</summary>
    /// <param name="envelope">The event to encode.</param>
    /// <returns>The canonical JSON.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="envelope"/> is <see langword="null"/>.</exception>
    public static string Serialize(LifecycleEventEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        return JsonSerializer.Serialize(envelope, LifecycleJsonContext.Default.LifecycleEventEnvelope);
    }

    /// <summary>Encodes a delivery to its canonical UTF-8 bytes.</summary>
    /// <param name="delivery">The delivery to encode.</param>
    /// <returns>The canonical bytes.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="delivery"/> is <see langword="null"/>.</exception>
    public static byte[] SerializeToUtf8Bytes(LifecycleDeliveryEnvelope delivery)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        return JsonSerializer.SerializeToUtf8Bytes(delivery, LifecycleJsonContext.Default.LifecycleDeliveryEnvelope);
    }

    /// <summary>Encodes a delivery to its canonical JSON text.</summary>
    /// <param name="delivery">The delivery to encode.</param>
    /// <returns>The canonical JSON.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="delivery"/> is <see langword="null"/>.</exception>
    public static string Serialize(LifecycleDeliveryEnvelope delivery)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        return JsonSerializer.Serialize(delivery, LifecycleJsonContext.Default.LifecycleDeliveryEnvelope);
    }

    /// <summary>Decodes an event from UTF-8 bytes.</summary>
    /// <param name="utf8Json">The bytes to decode.</param>
    /// <returns>The decoded event.</returns>
    /// <exception cref="LifecycleContractException">
    /// The bytes are not valid JSON, or a required member is missing. An unrecognized
    /// <see cref="LifecycleEventEnvelope.EventType"/> is not an error.
    /// </exception>
    public static LifecycleEventEnvelope DeserializeEvent(ReadOnlySpan<byte> utf8Json)
    {
        try
        {
            return JsonSerializer.Deserialize(utf8Json, LifecycleJsonContext.Default.LifecycleEventEnvelope)
                ?? throw new LifecycleContractException("Event body decoded to null.");
        }
        catch (JsonException ex)
        {
            throw new LifecycleContractException("Event body is not a valid envelope.", ex);
        }
    }

    /// <summary>Decodes an event from JSON text.</summary>
    /// <param name="json">The JSON to decode.</param>
    /// <returns>The decoded event.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="json"/> is <see langword="null"/>.</exception>
    /// <exception cref="LifecycleContractException">The text is not a valid envelope.</exception>
    public static LifecycleEventEnvelope DeserializeEvent(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            return JsonSerializer.Deserialize(json, LifecycleJsonContext.Default.LifecycleEventEnvelope)
                ?? throw new LifecycleContractException("Event body decoded to null.");
        }
        catch (JsonException ex)
        {
            throw new LifecycleContractException("Event body is not a valid envelope.", ex);
        }
    }

    /// <summary>Decodes a delivery from UTF-8 bytes.</summary>
    /// <param name="utf8Json">The bytes to decode.</param>
    /// <returns>The decoded delivery.</returns>
    /// <exception cref="LifecycleContractException">The bytes are not a valid delivery.</exception>
    public static LifecycleDeliveryEnvelope DeserializeDelivery(ReadOnlySpan<byte> utf8Json)
    {
        try
        {
            return JsonSerializer.Deserialize(utf8Json, LifecycleJsonContext.Default.LifecycleDeliveryEnvelope)
                ?? throw new LifecycleContractException("Delivery body decoded to null.");
        }
        catch (JsonException ex)
        {
            throw new LifecycleContractException("Delivery body is not a valid delivery.", ex);
        }
    }

    /// <summary>Decodes a delivery from JSON text.</summary>
    /// <param name="json">The JSON to decode.</param>
    /// <returns>The decoded delivery.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="json"/> is <see langword="null"/>.</exception>
    /// <exception cref="LifecycleContractException">The text is not a valid delivery.</exception>
    public static LifecycleDeliveryEnvelope DeserializeDelivery(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            return JsonSerializer.Deserialize(json, LifecycleJsonContext.Default.LifecycleDeliveryEnvelope)
                ?? throw new LifecycleContractException("Delivery body decoded to null.");
        }
        catch (JsonException ex)
        {
            throw new LifecycleContractException("Delivery body is not a valid delivery.", ex);
        }
    }

    /// <summary>
    /// Encodes a typed payload into the opaque form an envelope carries.
    /// </summary>
    /// <typeparam name="TPayload">A payload type registered on <see cref="LifecycleJsonContext"/>.</typeparam>
    /// <param name="payload">The payload to encode.</param>
    /// <returns>The payload as raw JSON.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="payload"/> is <see langword="null"/>.</exception>
    /// <exception cref="LifecycleContractException">
    /// <typeparamref name="TPayload"/> is not registered on the canonical context.
    /// </exception>
    public static JsonElement ToPayloadElement<TPayload>(TPayload payload)
        where TPayload : class
    {
        ArgumentNullException.ThrowIfNull(payload);
        return JsonSerializer.SerializeToElement(payload, GetTypeInfo<TPayload>());
    }

    /// <summary>
    /// Projects an envelope's opaque payload onto a typed payload.
    /// </summary>
    /// <typeparam name="TPayload">A payload type registered on <see cref="LifecycleJsonContext"/>.</typeparam>
    /// <param name="envelope">The envelope whose payload to read.</param>
    /// <param name="payload">The decoded payload, when this method returns <see langword="true"/>.</param>
    /// <returns>
    /// <see langword="false"/> when the envelope has no payload, when its
    /// <see cref="LifecycleEventEnvelope.EventType"/> is not the one that carries
    /// <typeparamref name="TPayload"/>, or when the payload does not decode.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Returning <see langword="false"/> is an ordinary outcome, not an error: it is exactly what a
    /// subscriber sees for an event type introduced after it was compiled. The envelope remains
    /// intact and can still be forwarded, stored, and re-emitted byte-for-byte.
    /// </para>
    /// <para>
    /// The event type is checked first, and that check is what makes the result trustworthy. Payload
    /// records have no required members, so an unrelated JSON object decodes into one perfectly well
    /// — every property simply takes its default. Without the discriminator check a caller would get
    /// <see langword="true"/> and a payload of empty strings and zeros, which is a far worse answer
    /// than "this build does not know this event."
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="envelope"/> is <see langword="null"/>.</exception>
    public static bool TryReadPayload<TPayload>(
        LifecycleEventEnvelope envelope,
        [NotNullWhen(true)] out TPayload? payload
    )
        where TPayload : class
    {
        ArgumentNullException.ThrowIfNull(envelope);
        payload = null;

        if (GetPayloadType(envelope.EventType) != typeof(TPayload))
        {
            return false;
        }

        if (envelope.Payload is not { } element || element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        try
        {
            payload = element.Deserialize(GetTypeInfo<TPayload>());
        }
        catch (JsonException)
        {
            return false;
        }

        return payload is not null;
    }

    /// <summary>
    /// Maps an event-type discriminator to the payload type this build decodes it as.
    /// </summary>
    /// <param name="eventType">The discriminator carried by an envelope.</param>
    /// <returns>The payload type, or <see langword="null"/> when this build does not know the event type.</returns>
    public static Type? GetPayloadType(string? eventType) =>
        eventType switch
        {
            LifecycleEventTypes.RunStarted => typeof(RunStartedPayload),
            LifecycleEventTypes.ContextLoaded => typeof(ContextLoadedPayload),
            LifecycleEventTypes.TurnCompleted => typeof(TurnCompletedPayload),
            LifecycleEventTypes.ToolCompleted => typeof(ToolCompletedPayload),
            LifecycleEventTypes.RunCompleted => typeof(RunCompletedPayload),
            LifecycleEventTypes.SandboxCreated => typeof(SandboxCreatedPayload),
            LifecycleEventTypes.ContextMeasured => typeof(ContextMeasuredPayload),
            LifecycleEventTypes.CompactionDecided
            or LifecycleEventTypes.CompactionApplied
            or LifecycleEventTypes.CompactionFailed => typeof(CompactionPayload),
            _ => null,
        };

    /// <summary>
    /// Builds an envelope, assigning its identity and stream position once.
    /// </summary>
    /// <typeparam name="TPayload">A payload type registered on <see cref="LifecycleJsonContext"/>.</typeparam>
    /// <param name="eventType">The discriminator naming the payload shape.</param>
    /// <param name="payload">The payload to carry.</param>
    /// <param name="sourceStreamId">The stream this event orders within.</param>
    /// <param name="allocator">Supplies the stream ordinal and the producer epoch.</param>
    /// <param name="occurredAt">When the event occurred.</param>
    /// <param name="correlation">The identifiers this event correlates to, if any.</param>
    /// <param name="eventId">
    /// An explicit event id. Supplying one is intended for tests and for producers that derive
    /// identity from their own storage; otherwise a fresh id is minted.
    /// </param>
    /// <returns>An envelope ready for fan-out.</returns>
    /// <remarks>
    /// Identity is assigned here, once, before any subscriber sees the event — never per delivery
    /// and never per retry. That is what makes a retry a re-send of an identical body rather than a
    /// new event.
    /// </remarks>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="eventType"/> is null or whitespace.</exception>
    public static LifecycleEventEnvelope CreateEnvelope<TPayload>(
        string eventType,
        TPayload payload,
        string sourceStreamId,
        ILifecycleSequenceAllocator allocator,
        DateTimeOffset occurredAt,
        LifecycleCorrelation? correlation = null,
        string? eventId = null
    )
        where TPayload : class
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(allocator);

        if (string.IsNullOrWhiteSpace(eventType))
        {
            throw new ArgumentException("An event type must be non-empty.", nameof(eventType));
        }

        return new LifecycleEventEnvelope
        {
            SchemaMajor = LifecycleProtocol.CurrentMajor,
            EventId = eventId ?? Guid.NewGuid().ToString("N"),
            EventType = eventType,
            SourceStreamId = sourceStreamId,
            SourceSequence = allocator.Next(sourceStreamId),
            ProducerEpoch = allocator.ProducerEpoch,
            OccurredAt = occurredAt,
            Correlation = correlation,
            Payload = ToPayloadElement(payload),
        };
    }

    private static JsonTypeInfo<TPayload> GetTypeInfo<TPayload>()
        where TPayload : class
    {
        if (LifecycleJsonContext.Default.GetTypeInfo(typeof(TPayload)) is JsonTypeInfo<TPayload> info)
        {
            return info;
        }

        throw new LifecycleContractException(
            $"'{typeof(TPayload).FullName}' is not registered on the canonical lifecycle serializer context. "
                + "Every lifecycle type must be declared there so one authority owns the wire format."
        );
    }
}
