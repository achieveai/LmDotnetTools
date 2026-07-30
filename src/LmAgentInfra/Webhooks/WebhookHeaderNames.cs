namespace AchieveAi.LmDotnetTools.LmAgentInfra.Webhooks;

/// <summary>
/// The three headers that carry a signed webhook delivery (ADR 0005). Defined once so the outbound
/// <see cref="WebhookRequestSigner"/> and the inbound <see cref="WebhookVerificationMiddleware"/>
/// cannot drift apart — the wire contract is a single source of truth, not two spellings.
/// </summary>
public static class WebhookHeaderNames
{
    /// <summary>
    /// Lowercase-hex HMAC-SHA256 over <c>{timestamp}.{deliveryId}.{rawBody}</c>. The delivery id is part
    /// of the signed payload, which is what makes the replay-cache key trustworthy.
    /// </summary>
    public const string Signature = "X-Sandbox-Signature";

    /// <summary>Unix-seconds (or ISO-8601) send time, bound into the signature.</summary>
    public const string Timestamp = "X-Sandbox-Timestamp";

    /// <summary>Unique per-callback id, bound into the signature; a repeat within the TTL is a rejected replay.</summary>
    public const string DeliveryId = "X-Sandbox-Delivery-Id";
}
