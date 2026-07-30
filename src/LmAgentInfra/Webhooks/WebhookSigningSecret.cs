using System.Security.Cryptography;
using System.Text;

namespace AchieveAi.LmDotnetTools.LmAgentInfra.Webhooks;

/// <summary>
/// The HMAC-SHA256 key a service shares with its webhook peer to authenticate callbacks at the body
/// level (ADR 0005). It is distinct from a transport shared secret such as the sandbox gateway's
/// <c>Authorization</c> header: that secret authenticates the <em>caller</em>, this one proves the
/// <em>body was not tampered with</em> and (by binding the timestamp and delivery id into the signed
/// payload) that the call is not a replay under a fresh clock or a fresh delivery id.
/// <para>
/// Resolved from configuration, falling back to a cryptographically-random value when none is
/// configured, so an unconfigured deployment fails closed: nothing verifies, rather than everything
/// being accepted. SECRET — never log <see cref="Value"/>.
/// </para>
/// <para>
/// Supports dual-key rotation. <see cref="ComputeHex"/> always signs with the <em>current</em> key;
/// <see cref="Matches"/> accepts the current key or, during a bounded overlap window opened by
/// <see cref="Rotate"/>, the immediately-previous key, so rotating does not reject deliveries already
/// in flight. <see cref="RevokePrevious"/> drops the previous key at once, which is the compromise
/// response. The overlap deadline is measured against an injected <see cref="TimeProvider"/> rather
/// than a caller-supplied instant: that keeps <see cref="Matches"/>'s signature (and therefore
/// <see cref="WebhookRequestVerifier"/>) unchanged while still making expiry deterministically
/// testable with a manual clock.
/// </para>
/// <para>
/// Deliberately a class and not a record: a generated <c>ToString</c>/equality would put key material
/// into logs and diagnostics. <see cref="ToString"/> is overridden to emit a redacted marker.
/// </para>
/// </summary>
public sealed class WebhookSigningSecret
{
    private readonly TimeProvider _timeProvider;
    private readonly object _rotationGate = new();

    // Read as a single reference so a concurrent rotation is never observed half-applied.
    private volatile KeySet _keys;

    /// <summary>
    /// Creates a secret from configuration using the system clock. When
    /// <paramref name="configuredSecret"/> is null/blank a cryptographically-random key is generated,
    /// so an unconfigured deployment verifies nothing (fail-closed).
    /// </summary>
    /// <param name="configuredSecret">The shared signing secret. SECRET — never log this value.</param>
    public WebhookSigningSecret(string? configuredSecret)
        : this(configuredSecret, TimeProvider.System) { }

    /// <summary>
    /// Creates a secret from configuration with an explicit clock, which the rotation overlap window is
    /// measured against. Tests inject a manual clock to expire an overlap without sleeping.
    /// </summary>
    /// <param name="configuredSecret">The shared signing secret. SECRET — never log this value.</param>
    /// <param name="timeProvider">Clock used to decide whether a rotation overlap is still open.</param>
    public WebhookSigningSecret(string? configuredSecret, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);

        _timeProvider = timeProvider;
        var secret = string.IsNullOrWhiteSpace(configuredSecret)
            ? RandomNumberGenerator.GetHexString(64)
            : configuredSecret;
        _keys = new KeySet(secret, Encoding.UTF8.GetBytes(secret), previousKey: null, previousExpiresAtUtc: default);
    }

    /// <summary>
    /// The current signing secret — the one <see cref="ComputeHex"/> uses. SECRET — never log this value.
    /// </summary>
    public string Value => _keys.CurrentSecret;

    /// <summary>
    /// Whether a previous key is still inside its rotation overlap and therefore still accepted by
    /// <see cref="Matches"/>. Exposed so an operator or a test can observe that a rotation is in
    /// progress; it discloses no key material.
    /// </summary>
    public bool HasPreviousKey
    {
        get
        {
            var keys = _keys;
            return keys.PreviousKey is not null && _timeProvider.GetUtcNow() < keys.PreviousExpiresAtUtc;
        }
    }

    /// <summary>
    /// Computes the lowercase-hex HMAC-SHA256 over <c>{timestamp}.{deliveryId}.{body}</c> (Stripe-style)
    /// using the <em>current</em> key. Binding BOTH the timestamp and the delivery id into the signed
    /// payload means a captured callback cannot be replayed under a fresh timestamp <em>or</em> a fresh
    /// delivery id without re-signing — which an attacker cannot do without the secret. Authenticating
    /// the delivery id is what makes the replay cache's key trustworthy.
    /// </summary>
    /// <param name="timestamp">Send time, in the exact wire form the receiver will see.</param>
    /// <param name="deliveryId">Unique per-callback id, in the exact wire form the receiver will see.</param>
    /// <param name="body">Raw body bytes, signed as-sent so a retry can re-send them verbatim.</param>
    /// <returns>The lowercase-hex signature to carry in <see cref="WebhookHeaderNames.Signature"/>.</returns>
    public string ComputeHex(string timestamp, string deliveryId, ReadOnlySpan<byte> body)
    {
        var hash = HMACSHA256.HashData(_keys.CurrentKey, BuildSigned(timestamp, deliveryId, body));
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>
    /// Constant-time check that <paramref name="presentedHex"/> is a valid signature for
    /// <paramref name="timestamp"/> + <paramref name="deliveryId"/> + <paramref name="body"/> under the
    /// current key, or under a previous key whose rotation overlap has not lapsed. Returns false for a
    /// null/empty/odd-length/non-hex presented value rather than throwing (the "missing or garbage
    /// header" case).
    /// </summary>
    /// <param name="presentedHex">The signature the caller presented, or null when the header is absent.</param>
    /// <param name="timestamp">Send time exactly as presented on the wire.</param>
    /// <param name="deliveryId">Delivery id exactly as presented on the wire.</param>
    /// <param name="body">Raw body bytes exactly as received.</param>
    /// <returns><c>true</c> when the signature is valid under an active key; otherwise <c>false</c>.</returns>
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

        var keys = _keys;
        var signed = BuildSigned(timestamp, deliveryId, body);

        if (CryptographicOperations.FixedTimeEquals(presented, HMACSHA256.HashData(keys.CurrentKey, signed)))
        {
            return true;
        }

        // Dual-key acceptance: a delivery signed just before a rotation is still honoured until the
        // overlap deadline passes — or until RevokePrevious drops the key, whichever comes first.
        return keys.PreviousKey is { } previous
            && _timeProvider.GetUtcNow() < keys.PreviousExpiresAtUtc
            && CryptographicOperations.FixedTimeEquals(presented, HMACSHA256.HashData(previous, signed));
    }

    /// <summary>
    /// Makes <paramref name="newSecret"/> the current signing key and demotes the outgoing key to the
    /// previous key for <paramref name="overlap"/>, during which <see cref="Matches"/> still accepts it.
    /// Only one previous key is retained: rotating twice inside an overlap drops the older key at once,
    /// which keeps the active set at two.
    /// </summary>
    /// <param name="newSecret">The incoming signing secret. SECRET — never log this value.</param>
    /// <param name="overlap">
    /// How long the outgoing key stays acceptable. <see cref="TimeSpan.Zero"/> is a hard cutover —
    /// equivalent to rotating and revoking in one step.
    /// </param>
    /// <exception cref="ArgumentException"><paramref name="newSecret"/> is null, empty, or whitespace.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="overlap"/> is negative.</exception>
    public void Rotate(string newSecret, TimeSpan overlap)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(newSecret);
        ArgumentOutOfRangeException.ThrowIfLessThan(overlap, TimeSpan.Zero);

        lock (_rotationGate)
        {
            var outgoing = _keys;
            _keys = new KeySet(
                newSecret,
                Encoding.UTF8.GetBytes(newSecret),
                outgoing.CurrentKey,
                _timeProvider.GetUtcNow() + overlap);
        }
    }

    /// <summary>
    /// Drops the previous key from the active set immediately, ending any rotation overlap before its
    /// deadline. This is the compromise response: a leaked outgoing key stops verifying at once rather
    /// than at the end of the window. A no-op when no previous key is held.
    /// </summary>
    public void RevokePrevious()
    {
        lock (_rotationGate)
        {
            var keys = _keys;
            if (keys.PreviousKey is null)
            {
                return;
            }

            _keys = new KeySet(keys.CurrentSecret, keys.CurrentKey, previousKey: null, previousExpiresAtUtc: default);
        }
    }

    /// <summary>Redacted by design — this type holds key material that must never reach a log.</summary>
    /// <returns>A constant marker containing no key material.</returns>
    public override string ToString() => $"{nameof(WebhookSigningSecret)}[REDACTED]";

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

    /// <summary>
    /// The immutable active key set, swapped as a whole on rotation so readers never see a torn state.
    /// A class rather than a record so no generated member can render key bytes.
    /// </summary>
    private sealed class KeySet(
        string currentSecret,
        byte[] currentKey,
        byte[]? previousKey,
        DateTimeOffset previousExpiresAtUtc)
    {
        public string CurrentSecret { get; } = currentSecret;

        public byte[] CurrentKey { get; } = currentKey;

        public byte[]? PreviousKey { get; } = previousKey;

        public DateTimeOffset PreviousExpiresAtUtc { get; } = previousExpiresAtUtc;
    }
}
