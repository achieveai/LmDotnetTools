using System.Globalization;

namespace CodeReviewDaemon.Sample.Auth;

/// <summary>Why a webhook callback was rejected by <see cref="WebhookRequestVerifier"/> (plan §9).</summary>
internal enum WebhookRejection
{
    /// <summary>The request passed every check.</summary>
    None,

    /// <summary>The <c>{provider}</c> route segment is not in the daemon's allow-list.</summary>
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
internal sealed record WebhookVerificationResult(WebhookRejection Rejection)
{
    public bool IsValid => Rejection == WebhookRejection.None;

    public static readonly WebhookVerificationResult Valid = new(WebhookRejection.None);
}

/// <summary>
/// Tunable limits for the webhook security layer (plan §9), shared by the verifier, the replay cache,
/// and the middleware so the size cap and freshness window are defined once. Conservative defaults match
/// the plan: a ±5-minute timestamp tolerance and a 1 MiB body cap.
/// </summary>
internal sealed record WebhookVerificationLimits
{
    public TimeSpan TimestampTolerance { get; init; } = TimeSpan.FromMinutes(5);

    public long MaxBodyBytes { get; init; } = 1_048_576;

    /// <summary>How long a delivery id is remembered: the full acceptance window (both timestamp edges).</summary>
    public TimeSpan ReplayWindow => TimestampTolerance + TimestampTolerance;
}

/// <summary>The fields <see cref="WebhookRequestVerifier"/> needs to decide one callback.</summary>
internal sealed record WebhookVerificationInput(
    string Provider,
    string? ContentType,
    byte[] Body,
    string? Signature,
    string? Timestamp,
    string? DeliveryId);

/// <summary>
/// The deterministic half of the daemon's webhook security layer (plan §9): given a callback's
/// classification fields and the current time, decide whether to accept it. Pure and clock-injected so
/// every branch is unit-testable; the stateful replay check (<see cref="DeliveryReplayCache"/>) and the
/// raw-body plumbing (<c>WebhookVerificationMiddleware</c>) live alongside it. Checks run cheapest-first
/// and fail closed: an unknown provider, wrong content-type, oversized or unsigned body, a stale
/// timestamp, or a signature that does not cover the exact bytes is rejected before token resolution.
/// </summary>
internal sealed class WebhookRequestVerifier
{
    private const string JsonContentType = "application/json";

    private readonly WebhookSigningSecret _signingSecret;
    private readonly HashSet<string> _allowedProviders;
    private readonly TimeSpan _timestampTolerance;
    private readonly long _maxBodyBytes;

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
