using System.Security.Cryptography;

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
}
