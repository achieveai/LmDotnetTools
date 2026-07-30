using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.LmLifecycle;

/// <summary>
/// One event as delivered to one subscriber: the producer's event, wrapped in that subscriber's own
/// delivery identity.
/// </summary>
/// <remarks>
/// <para>
/// Source identity and delivery identity are separate on purpose. The inner
/// <see cref="LifecycleEventEnvelope"/> is identical for every recipient; the delivery id and
/// ordinal here belong to one subscriber alone.
/// </para>
/// <para>
/// <b>Delivery numbering happens after filtering.</b> A subscriber is numbered only across the
/// events it was entitled to receive, so its <see cref="DeliverySequence"/> is contiguous and any
/// gap in it means loss that is specific to it — without disclosing that events it was never
/// entitled to see exist at all.
/// </para>
/// </remarks>
public sealed record LifecycleDeliveryEnvelope
{
    /// <summary>
    /// Unique identity for this delivery, minted once before the first attempt.
    /// </summary>
    /// <remarks>
    /// A retry reuses this value and re-sends a byte-identical body — that is what keeps a
    /// signature computed over the original bytes valid, and what lets a receiver recognize a
    /// retry rather than double-processing it.
    /// </remarks>
    [JsonPropertyName("delivery_id")]
    [JsonRequired]
    public string DeliveryId { get; set; } = string.Empty;

    /// <summary>
    /// This delivery's position in the subscriber's own stream, starting at <c>1</c>.
    /// </summary>
    [JsonPropertyName("delivery_sequence")]
    [JsonRequired]
    public long DeliverySequence { get; set; }

    /// <summary>The producer's event, unchanged.</summary>
    [JsonPropertyName("event")]
    [JsonRequired]
    public LifecycleEventEnvelope Event { get; set; } = new();

    /// <summary>
    /// Throws when the delivery wrapper or the event it carries is structurally unusable.
    /// </summary>
    /// <exception cref="LifecycleContractException">
    /// <see cref="DeliveryId"/> is empty, <see cref="DeliverySequence"/> is not positive, or the
    /// inner event fails <see cref="LifecycleEventEnvelope.EnsureValid"/>.
    /// </exception>
    public void EnsureValid()
    {
        if (string.IsNullOrWhiteSpace(DeliveryId))
        {
            throw new LifecycleContractException($"'{nameof(DeliveryId)}' must be non-empty.");
        }

        if (DeliverySequence <= 0)
        {
            throw new LifecycleContractException(
                $"'{nameof(DeliverySequence)}' must be positive; ordinals start at 1."
            );
        }

        Event.EnsureValid();
    }
}
