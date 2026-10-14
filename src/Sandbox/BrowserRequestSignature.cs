using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace AchieveAi.LmDotnetTools.Sandbox;

/// <summary>Outcome of <see cref="BrowserRequestSignature.Verify"/>.</summary>
public enum BrowserSignatureVerdict
{
    /// <summary>The signature matches and its timestamp is within the allowed skew.</summary>
    Valid,

    /// <summary>No signature was supplied.</summary>
    Missing,

    /// <summary>The value is not <c>t=&lt;unix seconds&gt;,v1=&lt;64 hex&gt;</c>.</summary>
    Malformed,

    /// <summary>Well-formed, but the HMAC does not match the signed fields under this secret.</summary>
    Mismatch,

    /// <summary>The HMAC matches, but its timestamp is more than <see cref="BrowserRequestSignature.MaxClockSkew"/> away from now.</summary>
    Stale,
}

/// <summary>
/// The gateway's pinned-browser-egress signature (gateway ADR 0038, <c>browser/egress.rs</c>). Every request a
/// leased browser page makes reaches the app's service with <c>X-Sbx-App-Id</c>, <c>X-Sbx-Browser-Id</c>,
/// <c>X-Sbx-Browser-Instance</c>, <c>X-Sbx-Original-Url</c> and, when the operator configured a
/// <c>signing_secret</c>, <c>X-Sbx-Browser-Signature: t=&lt;unix&gt;,v1=&lt;hex&gt;</c> where <c>v1</c> is
/// HMAC-SHA256 over <c>v1\n{t}\n{METHOD}\n{original_url}\n{app}\n{browser_id}\n{instance}</c>, keyed by the
/// secret's UTF-8 bytes. The request body is not covered. The gateway strips the whole <c>x-sbx-*</c> namespace
/// from page requests, so a valid signature proves the gateway, not the page, set these headers.
/// </summary>
public static class BrowserRequestSignature
{
    /// <summary>Header carrying the signature.</summary>
    public const string SignatureHeader = "X-Sbx-Browser-Signature";

    /// <summary>Header carrying the browser's app id.</summary>
    public const string AppIdHeader = "X-Sbx-App-Id";

    /// <summary>Header carrying the browser id.</summary>
    public const string BrowserIdHeader = "X-Sbx-Browser-Id";

    /// <summary>Header carrying the browser instance.</summary>
    public const string BrowserInstanceHeader = SandboxClient.BrowserInstanceHeader;

    /// <summary>Header carrying the URL the page requested.</summary>
    public const string OriginalUrlHeader = "X-Sbx-Original-Url";

    /// <summary>Largest accepted distance between the signature's timestamp and now, in either direction.</summary>
    public static readonly TimeSpan MaxClockSkew = TimeSpan.FromSeconds(300);

    private const int MacHexLength = 64;

    /// <summary>Computes the header value the gateway would send.</summary>
    public static string Sign(
        string secret,
        long unixSeconds,
        string method,
        string originalUrl,
        string appId,
        string browserId,
        string instance
    )
    {
        var mac = Mac(secret, unixSeconds, method, originalUrl, appId, browserId, instance);
        return $"t={unixSeconds.ToString(CultureInfo.InvariantCulture)},v1={Convert.ToHexString(mac).ToLowerInvariant()}";
    }

    /// <summary>
    /// Checks <paramref name="signature"/> against the signed fields, in constant time. The fields are the
    /// exact header values the service received; <paramref name="method"/> is the request's HTTP method.
    /// </summary>
    public static BrowserSignatureVerdict Verify(
        string secret,
        string? signature,
        string method,
        string originalUrl,
        string appId,
        string browserId,
        string instance,
        DateTimeOffset now
    )
    {
        ArgumentException.ThrowIfNullOrEmpty(secret);
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(originalUrl);
        ArgumentNullException.ThrowIfNull(appId);
        ArgumentNullException.ThrowIfNull(browserId);
        ArgumentNullException.ThrowIfNull(instance);
        if (string.IsNullOrEmpty(signature))
        {
            return BrowserSignatureVerdict.Missing;
        }

        var parts = signature.Split(',');
        if (
            parts.Length != 2
            || !parts[0].StartsWith("t=", StringComparison.Ordinal)
            || !parts[1].StartsWith("v1=", StringComparison.Ordinal)
            || parts[1].Length != 3 + MacHexLength
            || !long.TryParse(parts[0].AsSpan(2), NumberStyles.None, CultureInfo.InvariantCulture, out var timestamp)
        )
        {
            return BrowserSignatureVerdict.Malformed;
        }

        byte[] presented;
        try
        {
            presented = Convert.FromHexString(parts[1].AsSpan(3));
        }
        catch (FormatException)
        {
            return BrowserSignatureVerdict.Malformed;
        }

        var expected = Mac(secret, timestamp, method, originalUrl, appId, browserId, instance);
        if (!CryptographicOperations.FixedTimeEquals(expected, presented))
        {
            return BrowserSignatureVerdict.Mismatch;
        }

        var skew = (long)MaxClockSkew.TotalSeconds;
        var nowSeconds = now.ToUnixTimeSeconds();
        return timestamp < nowSeconds - skew || timestamp > nowSeconds + skew
            ? BrowserSignatureVerdict.Stale
            : BrowserSignatureVerdict.Valid;
    }

    private static byte[] Mac(
        string secret,
        long unixSeconds,
        string method,
        string originalUrl,
        string appId,
        string browserId,
        string instance
    ) =>
        HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(secret),
            Encoding.UTF8.GetBytes(
                $"v1\n{unixSeconds.ToString(CultureInfo.InvariantCulture)}\n{method}\n{originalUrl}\n{appId}\n{browserId}\n{instance}"
            )
        );
}
