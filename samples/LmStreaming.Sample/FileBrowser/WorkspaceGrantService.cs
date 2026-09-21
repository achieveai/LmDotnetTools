using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace LmStreaming.Sample.FileBrowser;

/// <summary>One minted workspace read grant: the opaque token and the instant it stops validating.</summary>
/// <param name="Token">The opaque, signed, time-limited token. Goes in a URL PATH segment.</param>
/// <param name="ExpiresAt">When the token expires, so the client can refresh ahead of it.</param>
public sealed record WorkspaceGrant(string Token, DateTimeOffset ExpiresAt);

/// <summary>Why a presented grant was refused, or <see cref="None"/> when it was accepted.</summary>
public enum WorkspaceGrantFailure
{
    /// <summary>Accepted.</summary>
    None = 0,

    /// <summary>
    /// Unreadable: malformed, tampered with, minted under another purpose or key ring, or EXPIRED. The four
    /// are deliberately one code — see <see cref="WorkspaceGrantService"/>'s remarks.
    /// </summary>
    Invalid = 1,

    /// <summary>Readable and unexpired, but minted for a DIFFERENT conversation than the route names.</summary>
    ThreadMismatch = 2,

    /// <summary>Readable and unexpired, but minted for a DIFFERENT principal than the one now asking.</summary>
    PrincipalMismatch = 3,
}

/// <summary>
/// Mints and validates the short-lived, read-only grant that lets a HEADER-LESS browser fetch address the
/// workspace (Bug#15): an <c>&lt;iframe src&gt;</c>, an <c>&lt;img src&gt;</c>, and every relative
/// <c>&lt;link&gt;</c>/<c>&lt;script&gt;</c> inside a rendered workspace page. None of those can carry the
/// <c>Authorization</c> header the client's <c>apiFetch</c> attaches, so the credential has to travel in the
/// URL — and it has to travel in the URL's PATH, because RFC 3986 relative-reference resolution replaces the
/// query, which would strip a <c>?grant=</c> from every relative subresource the page loads.
/// </summary>
/// <remarks>
/// <para>
/// This is NOT an authorization decision and never replaces one. A raw request that presents a valid grant
/// still runs the same <c>ResolveSessionAsync(AccessAction.Read)</c> prologue — the same
/// <c>ConversationAuthorizer</c> — as every other file route. What the grant adds is the one thing the
/// authorizer cannot see on a header-less subresource fetch: proof that the URL was constructed by this
/// application for this caller, rather than typed by someone who guessed a thread id.
/// </para>
/// <para>
/// Expiry collapses into <see cref="WorkspaceGrantFailure.Invalid"/> on purpose. The time-limited protector
/// signals an expired payload with the same <see cref="CryptographicException"/> it raises for a forged one,
/// and discriminating them would mean matching on an exception message. It also would not help anyone: the
/// client's answer to every refusal is identical — drop the cached grant, mint a fresh one, retry once — and
/// a distinct "expired" answer would tell an attacker that a token they hold was at least structurally
/// genuine.
/// </para>
/// </remarks>
public sealed class WorkspaceGrantService
{
    /// <summary>
    /// The Data Protection purpose. Versioned, and part of the key derivation, so a token protected for any
    /// other purpose in this app cannot be replayed here — and so a future payload change can be made by
    /// bumping this rather than by trying to parse both shapes.
    /// </summary>
    private const string ProtectorPurpose = "LmStreaming.Sample.FileBrowser.WorkspaceGrant.v1";

    /// <summary>The principal id recorded when the request carries none (the normal signed-out state).</summary>
    public const string AnonymousPrincipalId = "anonymous";

    private readonly ITimeLimitedDataProtector _protector;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates the service over the host's data protection key ring and clock.</summary>
    /// <param name="provider">Data protection provider; the key ring signs and encrypts the payload.</param>
    /// <param name="timeProvider">Clock the grant's expiry is measured from.</param>
    public WorkspaceGrantService(IDataProtectionProvider provider, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _protector = provider.CreateProtector(ProtectorPurpose).ToTimeLimitedDataProtector();
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// Mints a grant binding <paramref name="threadId"/> and <paramref name="principalId"/> for
    /// <see cref="FileBrowserLimits.WorkspaceGrantLifetime"/>. A null/blank principal is recorded as
    /// <see cref="AnonymousPrincipalId"/>, which is a VALUE like any other — an anonymous grant does not
    /// validate for a named principal.
    /// </summary>
    public WorkspaceGrant Mint(string threadId, string? principalId)
    {
        ArgumentNullException.ThrowIfNull(threadId);
        var expiresAt = _timeProvider.GetUtcNow() + FileBrowserLimits.WorkspaceGrantLifetime;
        return new WorkspaceGrant(_protector.Protect(Payload(threadId, principalId), expiresAt), expiresAt);
    }

    /// <summary>
    /// Validates <paramref name="token"/> against the conversation and principal now asking. Signature and
    /// expiry are checked by the protector; thread and principal are compared ordinally against the payload.
    /// </summary>
    public WorkspaceGrantFailure Validate(string? token, string threadId, string? principalId)
    {
        ArgumentNullException.ThrowIfNull(threadId);
        if (string.IsNullOrWhiteSpace(token))
        {
            return WorkspaceGrantFailure.Invalid;
        }

        string payload;
        try
        {
            payload = _protector.Unprotect(token);
        }
        catch (CryptographicException)
        {
            // Tampered, truncated, foreign purpose, rotated-away key, or expired. One answer, by design.
            return WorkspaceGrantFailure.Invalid;
        }
        catch (FormatException)
        {
            // Not even base64url — a token the caller invented rather than one this app minted.
            return WorkspaceGrantFailure.Invalid;
        }

        var fields = payload.Split('|');
        if (fields.Length != 3 || !string.Equals(fields[0], PayloadVersion, StringComparison.Ordinal))
        {
            return WorkspaceGrantFailure.Invalid;
        }

        // Compared on the ESCAPED form, so the comparison never depends on unescaping being lossless.
        if (!string.Equals(fields[1], Escape(threadId), StringComparison.Ordinal))
        {
            return WorkspaceGrantFailure.ThreadMismatch;
        }

        if (!string.Equals(fields[2], Escape(NormalizePrincipal(principalId)), StringComparison.Ordinal))
        {
            return WorkspaceGrantFailure.PrincipalMismatch;
        }

        return WorkspaceGrantFailure.None;
    }

    private const string PayloadVersion = "v1";

    /// <summary>
    /// The plaintext payload: <c>v1|{escaped thread}|{escaped principal}</c>. Both fields are percent-escaped
    /// so neither can contain the <c>|</c> delimiter — without that, a thread id spelled
    /// <c>t1|EndUser:someone</c> would split into a thread and a principal the caller chose.
    /// </summary>
    private static string Payload(string threadId, string? principalId) =>
        $"{PayloadVersion}|{Escape(threadId)}|{Escape(NormalizePrincipal(principalId))}";

    private static string NormalizePrincipal(string? principalId) =>
        string.IsNullOrWhiteSpace(principalId) ? AnonymousPrincipalId : principalId;

    private static string Escape(string value) => Uri.EscapeDataString(value);
}
