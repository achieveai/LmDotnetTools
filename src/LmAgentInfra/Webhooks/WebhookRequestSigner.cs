using System.Globalization;

namespace AchieveAi.LmDotnetTools.LmAgentInfra.Webhooks;

/// <summary>
/// The three header values that authenticate one outbound delivery. Carries no key material — every
/// field is sent on the wire — so a record (and its generated <c>ToString</c>) is safe here, unlike
/// <see cref="WebhookSigningSecret"/>.
/// </summary>
/// <param name="Signature">Lowercase-hex HMAC-SHA256 for <see cref="WebhookHeaderNames.Signature"/>.</param>
/// <param name="Timestamp">Send time for <see cref="WebhookHeaderNames.Timestamp"/>, in Unix seconds.</param>
/// <param name="DeliveryId">Per-delivery id for <see cref="WebhookHeaderNames.DeliveryId"/>.</param>
public sealed record WebhookSignatureHeaders(string Signature, string Timestamp, string DeliveryId)
{
    /// <summary>
    /// Writes the three headers onto <paramref name="request"/>, so a caller never has to spell the
    /// header names itself. Existing values for those headers are replaced.
    /// </summary>
    /// <param name="request">The outbound request to stamp.</param>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is null.</exception>
    public void ApplyTo(HttpRequestMessage request)
    {
        ArgumentNullException.ThrowIfNull(request);

        Set(request, WebhookHeaderNames.Signature, Signature);
        Set(request, WebhookHeaderNames.Timestamp, Timestamp);
        Set(request, WebhookHeaderNames.DeliveryId, DeliveryId);

        static void Set(HttpRequestMessage request, string name, string value)
        {
            _ = request.Headers.Remove(name);
            request.Headers.TryAddWithoutValidation(name, value);
        }
    }
}

/// <summary>
/// The outbound counterpart of <see cref="WebhookRequestVerifier"/> (ADR 0005): given the exact body
/// bytes a sender is about to transmit, produce the signature/timestamp/delivery-id headers the
/// receiver checks. Signing the raw bytes rather than a re-serialized payload is what lets a retry
/// re-send an identical signed body.
/// <para>
/// The clock is an injected <see cref="TimeProvider"/> so the emitted timestamp — and therefore the
/// signature — is deterministic under test.
/// </para>
/// </summary>
public sealed class WebhookRequestSigner
{
    private readonly WebhookSigningSecret _signingSecret;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates a signer over one signing secret; deliveries are signed with its current key.</summary>
    /// <param name="signingSecret">The shared secret whose current key signs each delivery.</param>
    /// <param name="timeProvider">Clock the send timestamp is read from; defaults to the system clock.</param>
    /// <exception cref="ArgumentNullException"><paramref name="signingSecret"/> is null.</exception>
    public WebhookRequestSigner(WebhookSigningSecret signingSecret, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(signingSecret);

        _signingSecret = signingSecret;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Signs <paramref name="body"/> under a freshly generated delivery id. Use this for a first
    /// attempt; a retry must re-send the <em>original</em> headers via <see cref="Sign(ReadOnlySpan{byte}, string)"/>
    /// with the original delivery id, or the receiver will treat it as a new delivery.
    /// </summary>
    /// <param name="body">Raw body bytes exactly as they will be transmitted.</param>
    /// <returns>The three header values to stamp on the request.</returns>
    public WebhookSignatureHeaders Sign(ReadOnlySpan<byte> body) => Sign(body, Guid.NewGuid().ToString("n"));

    /// <summary>
    /// Signs <paramref name="body"/> under the caller's <paramref name="deliveryId"/>, stamping the
    /// current time. The delivery id is bound into the signature, so the receiver can trust it as a
    /// replay-cache key.
    /// </summary>
    /// <param name="body">Raw body bytes exactly as they will be transmitted.</param>
    /// <param name="deliveryId">Unique id for this delivery; reuse it verbatim when retrying.</param>
    /// <returns>The three header values to stamp on the request.</returns>
    /// <exception cref="ArgumentException"><paramref name="deliveryId"/> is null, empty, or whitespace.</exception>
    public WebhookSignatureHeaders Sign(ReadOnlySpan<byte> body, string deliveryId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deliveryId);

        // Unix seconds is the wire form the verifier parses first, and the string is what gets signed —
        // so the signature covers the exact characters the receiver will see, not a re-formatted value.
        var timestamp = _timeProvider.GetUtcNow().ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        return new WebhookSignatureHeaders(
            _signingSecret.ComputeHex(timestamp, deliveryId, body),
            timestamp,
            deliveryId
        );
    }
}
