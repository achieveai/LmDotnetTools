using System.Security.Cryptography;
using System.Text;

namespace CodeReviewDaemon.Sample.Auth;

/// <summary>
/// The HMAC-SHA256 key the daemon shares with the sandbox gateway to authenticate webhook callbacks at
/// the body level (plan §9). It is distinct from the gateway <c>Authorization</c> shared secret: that
/// secret authenticates the <em>caller</em>, this one proves the <em>body was not tampered with</em> and
/// (by binding the timestamp into the signed payload) that the call is not a replay with a fresh clock.
/// <para>
/// Resolved once for the process lifetime from configuration, falling back to a cryptographically-random
/// value when none is configured (so an unconfigured daemon fails closed — only a gateway that knows the
/// secret can produce a valid signature). SECRET — never log <see cref="Value"/>.
/// </para>
/// </summary>
internal sealed class WebhookSigningSecret
{
    private readonly byte[] _key;

    public WebhookSigningSecret(string? configuredSecret)
    {
        Value = string.IsNullOrWhiteSpace(configuredSecret)
            ? RandomNumberGenerator.GetHexString(64)
            : configuredSecret;
        _key = Encoding.UTF8.GetBytes(Value);
    }

    /// <summary>The resolved signing secret. SECRET — never log this value.</summary>
    public string Value { get; }

    /// <summary>
    /// Computes the lowercase-hex HMAC-SHA256 over <c>{timestamp}.{deliveryId}.{body}</c> (Stripe-style).
    /// Binding BOTH the timestamp and the delivery id into the signed payload means a captured callback
    /// cannot be replayed under a fresh timestamp <em>or</em> a fresh delivery id without re-signing —
    /// which an attacker cannot do without the secret. Authenticating the delivery id is what makes the
    /// replay cache's key trustworthy.
    /// </summary>
    public string ComputeHex(string timestamp, string deliveryId, ReadOnlySpan<byte> body)
    {
        var hash = HMACSHA256.HashData(_key, BuildSigned(timestamp, deliveryId, body));
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>
    /// Constant-time check that <paramref name="presentedHex"/> is the valid signature for
    /// <paramref name="timestamp"/> + <paramref name="deliveryId"/> + <paramref name="body"/>. Returns
    /// false for a null/empty/odd-length presented value rather than throwing (the "missing/garbage
    /// header" case).
    /// </summary>
    public bool Matches(string? presentedHex, string timestamp, string deliveryId, ReadOnlySpan<byte> body)
    {
        if (string.IsNullOrEmpty(presentedHex))
        {
            return false;
        }

        byte[] presented;
        try
        {
            presented = Convert.FromHexString(presentedHex);
        }
        catch (FormatException)
        {
            return false;
        }

        var expected = HMACSHA256.HashData(_key, BuildSigned(timestamp, deliveryId, body));
        return CryptographicOperations.FixedTimeEquals(presented, expected);
    }

    private static byte[] BuildSigned(string timestamp, string deliveryId, ReadOnlySpan<byte> body)
    {
        ArgumentNullException.ThrowIfNull(timestamp);
        ArgumentNullException.ThrowIfNull(deliveryId);

        var prefix = Encoding.UTF8.GetBytes(timestamp + "." + deliveryId + ".");
        var buffer = new byte[prefix.Length + body.Length];
        prefix.CopyTo(buffer.AsSpan());
        body.CopyTo(buffer.AsSpan(prefix.Length));
        return buffer;
    }
}
