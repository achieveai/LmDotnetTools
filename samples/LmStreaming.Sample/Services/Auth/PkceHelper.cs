using System.Security.Cryptography;
using System.Text;

namespace LmStreaming.Sample.Services.Auth;

/// <summary>
/// PKCE (RFC 7636) helpers shared by the OAuth providers that hand-roll their authorize-URL
/// construction (GitHub web-app flow, M365 confidential-client flow). MSAL-driven providers
/// (ADO) compute PKCE themselves and don't use these.
/// </summary>
/// <remarks>
/// All randomness comes from <see cref="RandomNumberGenerator"/> (256 bits of entropy). Outputs
/// are URL-safe base64 (no padding) so they can be passed through query strings without further
/// encoding.
/// </remarks>
internal static class PkceHelper
{
    /// <summary>
    /// Creates a high-entropy, URL-safe PKCE code verifier (RFC 7636 §4.1; 32 random bytes →
    /// 43-char base64url string, well within the 43–128 character bound).
    /// </summary>
    public static string CreateCodeVerifier() => Base64Url(RandomNumberGenerator.GetBytes(32));

    /// <summary>
    /// Derives the S256 PKCE code challenge from <paramref name="verifier"/> (RFC 7636 §4.2):
    /// base64url(sha256(ASCII(verifier))).
    /// </summary>
    public static string CreateCodeChallenge(string verifier) =>
        Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    /// <summary>
    /// Creates a high-entropy, URL-safe OAuth <c>state</c> value (RFC 6749 §10.12). Same shape as
    /// the verifier — 256 bits of entropy is overkill for state, but consistency with the verifier
    /// makes the call sites symmetric.
    /// </summary>
    public static string CreateState() => Base64Url(RandomNumberGenerator.GetBytes(32));

    /// <summary>Base64url-encodes (no padding) per RFC 7636 §A — the encoding RFC 4648 §5 specifies for PKCE.</summary>
    public static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
