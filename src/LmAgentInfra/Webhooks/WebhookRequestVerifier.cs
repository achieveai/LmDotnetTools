using System.Globalization;

namespace AchieveAi.LmDotnetTools.LmAgentInfra.Webhooks;

/// <summary>Why a webhook callback was rejected by <see cref="WebhookRequestVerifier"/> (ADR 0005).</summary>
public enum WebhookRejection
{
    /// <summary>The request passed every check.</summary>
    None,

    /// <summary>The <c>{provider}</c> route segment is not in the receiver's allow-list.</summary>
    UnknownProvider,

    /// <summary>The <c>Content-Type</c> is not <c>application/json</c>.</summary>
    UnsupportedContentType,

    /// <summary>The body exceeds the maximum accepted size.</summary>
    BodyTooLarge,

    /// <summary>A required signature/timestamp/delivery-id header was missing or blank.</summary>
    MissingHeaders,

    /// <summary>The timestamp is unparseable or outside the accepted tolerance window.</summary>
    StaleTimestamp,

    /// <summary>The HMAC signature did not match the body under the presented timestamp.</summary>
    InvalidSignature,
}

/// <summary>The outcome of verifying one webhook callback.</summary>
/// <param name="Rejection">Why the callback was rejected, or <see cref="WebhookRejection.None"/>.</param>
public sealed record WebhookVerificationResult(WebhookRejection Rejection)
{
    /// <summary>Whether the callback passed every check and may be processed.</summary>
    public bool IsValid => Rejection == WebhookRejection.None;

    /// <summary>The shared "passed every check" result.</summary>
    public static readonly WebhookVerificationResult Valid = new(WebhookRejection.None);
}

/// <summary>
/// Tunable limits for the webhook security layer (ADR 0005), shared by the verifier, the replay cache,
/// and the middleware so the size cap and freshness window are defined once. Conservative defaults: a
/// ±5-minute timestamp tolerance and a 1 MiB body cap.
/// </summary>
public sealed record WebhookVerificationLimits
{
    /// <summary>
    /// How far a presented timestamp may sit from the receiver's clock in either direction. ±5 minutes
    /// absorbs ordinary NTP drift without widening the window a captured callback can be replayed in.
    /// </summary>
    public TimeSpan TimestampTolerance { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Largest accepted body, in bytes. 1 MiB is far above any legitimate callback payload, so reaching
    /// it means something is wrong rather than merely large.
    /// </summary>
    public long MaxBodyBytes { get; init; } = 1_048_576;

    /// <summary>
    /// How long a delivery id is remembered: the full acceptance window (both timestamp edges), so a
    /// replay can never outlive the freshness check that would otherwise catch it.
    /// </summary>
    public TimeSpan ReplayWindow => TimestampTolerance + TimestampTolerance;
}

/// <summary>The fields <see cref="WebhookRequestVerifier"/> needs to decide one callback.</summary>
/// <param name="Provider">The <c>{provider}</c> route segment being called.</param>
/// <param name="ContentType">The request's <c>Content-Type</c>, or null when absent.</param>
/// <param name="Body">Raw body bytes exactly as received — the signature covers these.</param>
/// <param name="Signature">The presented <see cref="WebhookHeaderNames.Signature"/> value, or null.</param>
/// <param name="Timestamp">The presented <see cref="WebhookHeaderNames.Timestamp"/> value, or null.</param>
/// <param name="DeliveryId">The presented <see cref="WebhookHeaderNames.DeliveryId"/> value, or null.</param>
public sealed record WebhookVerificationInput(
    string Provider,
    string? ContentType,
    byte[] Body,
    string? Signature,
    string? Timestamp,
    string? DeliveryId);

/// <summary>
/// The deterministic half of the webhook security layer (ADR 0005): given a callback's classification
/// fields and the current time, decide whether to accept it. Pure and clock-injected so every branch is
/// unit-testable; the stateful replay check (<see cref="DeliveryReplayCache"/>) and the raw-body
/// plumbing (<see cref="WebhookVerificationMiddleware"/>) live alongside it. Checks run cheapest-first
/// and fail closed: an unknown provider, wrong content-type, oversized or unsigned body, a stale
/// timestamp, or a signature that does not cover the exact bytes is rejected before token resolution.
/// </summary>
public sealed class WebhookRequestVerifier
{
    private const string JsonContentType = "application/json";

    private readonly WebhookSigningSecret _signingSecret;
    private readonly HashSet<string> _allowedProviders;
    private readonly TimeSpan _timestampTolerance;
    private readonly long _maxBodyBytes;

    /// <summary>Creates a verifier bound to one signing secret and one provider allow-list.</summary>
    /// <param name="signingSecret">Key set the presented signature is checked against.</param>
    /// <param name="allowedProviders">Provider route segments this receiver accepts (case-insensitive).</param>
    /// <param name="timestampTolerance">Maximum accepted clock skew, in either direction.</param>
    /// <param name="maxBodyBytes">Largest accepted body, in bytes.</param>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    public WebhookRequestVerifier(
        WebhookSigningSecret signingSecret,
        IEnumerable<string> allowedProviders,
        TimeSpan timestampTolerance,
        long maxBodyBytes)
    {
        _signingSecret = signingSecret ?? throw new ArgumentNullException(nameof(signingSecret));
        _allowedProviders = new HashSet<string>(
            allowedProviders ?? throw new ArgumentNullException(nameof(allowedProviders)),
            StringComparer.OrdinalIgnoreCase);
        _timestampTolerance = timestampTolerance;
        _maxBodyBytes = maxBodyBytes;
    }

    /// <summary>Decides one callback against the receiver's rules as of <paramref name="nowUtc"/>.</summary>
    /// <param name="input">The callback's classification fields and raw body.</param>
    /// <param name="nowUtc">The receiver's current time, used for the freshness check.</param>
    /// <returns>A valid result, or the first rejection reason encountered.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="input"/> is null.</exception>
    public WebhookVerificationResult Verify(WebhookVerificationInput input, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (string.IsNullOrWhiteSpace(input.Provider) || !_allowedProviders.Contains(input.Provider))
        {
            return new WebhookVerificationResult(WebhookRejection.UnknownProvider);
        }

        if (!IsJsonContentType(input.ContentType))
        {
            return new WebhookVerificationResult(WebhookRejection.UnsupportedContentType);
        }

        if (input.Body.LongLength > _maxBodyBytes)
        {
            return new WebhookVerificationResult(WebhookRejection.BodyTooLarge);
        }

        if (string.IsNullOrWhiteSpace(input.Signature)
            || string.IsNullOrWhiteSpace(input.Timestamp)
            || string.IsNullOrWhiteSpace(input.DeliveryId))
        {
            return new WebhookVerificationResult(WebhookRejection.MissingHeaders);
        }

        if (!IsTimestampFresh(input.Timestamp, nowUtc))
        {
            return new WebhookVerificationResult(WebhookRejection.StaleTimestamp);
        }

        // Delivery id is non-null here (the MissingHeaders guard above). Signing over it authenticates
        // the replay-cache key, so a captured callback cannot be replayed under a fresh delivery id.
        return _signingSecret.Matches(input.Signature, input.Timestamp, input.DeliveryId, input.Body)
            ? WebhookVerificationResult.Valid
            : new WebhookVerificationResult(WebhookRejection.InvalidSignature);
    }

    private static bool IsJsonContentType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return false;
        }

        // Tolerate parameters like "application/json; charset=utf-8".
        var mediaType = contentType.Split(';', 2)[0].Trim();
        return string.Equals(mediaType, JsonContentType, StringComparison.OrdinalIgnoreCase);
    }

    private bool IsTimestampFresh(string timestamp, DateTimeOffset nowUtc)
    {
        // Accept Unix seconds (the gateway's wire form) or a round-trip ISO-8601 timestamp.
        DateTimeOffset sent;
        if (long.TryParse(timestamp, NumberStyles.Integer, CultureInfo.InvariantCulture, out var unixSeconds))
        {
            sent = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
        }
        else if (!DateTimeOffset.TryParse(
            timestamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out sent))
        {
            return false;
        }

        var skew = nowUtc - sent;
        if (skew < TimeSpan.Zero)
        {
            skew = skew.Negate();
        }

        return skew <= _timestampTolerance;
    }
}
