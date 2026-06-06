using System.Security.Cryptography;
using System.Text;

namespace LmStreaming.Sample.Services.Auth;

/// <summary>
/// Resolves the gateway↔webhook shared secret exactly once for the process lifetime.
/// Registered as a singleton so the webhook controller and the sandbox registry observe
/// the same value: it comes from <see cref="WebhookOptions.GatewaySharedSecret"/> when
/// configured, otherwise a cryptographically-random value generated at startup.
/// </summary>
/// <remarks>
/// SECRET — the resolved <see cref="Value"/> authenticates gateway callbacks and must never
/// be logged.
/// </remarks>
public sealed class AuthSharedSecret
{
    /// <summary>
    /// Resolves the shared secret from the supplied <paramref name="options"/>, falling back
    /// to a freshly generated cryptographically-random value when none is configured.
    /// </summary>
    /// <param name="options">Auth options carrying the optional configured secret.</param>
    public AuthSharedSecret(AuthOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var configured = options.Webhook.GatewaySharedSecret;
        Value = string.IsNullOrWhiteSpace(configured)
            ? RandomNumberGenerator.GetHexString(64)
            : configured;
    }

    /// <summary>
    /// The resolved shared secret. SECRET — never log this value.
    /// </summary>
    public string Value { get; }

    /// <summary>
    /// Constant-time comparison of <paramref name="presented"/> against the shared secret. Both
    /// sides are hashed to a fixed-width SHA-256 digest first, so the comparison neither throws
    /// on a length mismatch nor leaks the secret's length via an early-exit. Returns false when
    /// the presented value is null or empty (the "missing header" case).
    /// </summary>
    /// <param name="presented">The raw value from the inbound <c>Authorization</c> header.</param>
    public bool Matches(string? presented)
    {
        if (string.IsNullOrEmpty(presented))
        {
            return false;
        }

        var presentedHash = SHA256.HashData(Encoding.UTF8.GetBytes(presented));
        var expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(Value));
        return CryptographicOperations.FixedTimeEquals(presentedHash, expectedHash);
    }
}
