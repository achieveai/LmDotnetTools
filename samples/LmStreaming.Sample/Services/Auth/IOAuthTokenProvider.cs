namespace LmStreaming.Sample.Services.Auth;

/// <summary>Sign-in lifecycle state for an OAuth provider.</summary>
public enum OAuthSignInState
{
    NotStarted,
    Pending,
    SignedIn,
    Failed
}

/// <summary>A short-lived access token with its absolute UTC expiry.</summary>
public sealed record OAuthAccessToken(string Value, DateTimeOffset ExpiresAtUtc);

/// <summary>The device-code challenge to show the user during sign-in.</summary>
public sealed record DeviceCodeChallenge(
    string UserCode,
    string VerificationUri,
    string? VerificationUriComplete,
    int ExpiresInSeconds,
    int IntervalSeconds);

/// <summary>Current sign-in status (safe to expose to the UI; contains no secrets).</summary>
public sealed record OAuthStatus(
    OAuthSignInState State,
    string? Account,
    IReadOnlyList<string> Scopes,
    DateTimeOffset? ExpiresAtUtc,
    string? Error);

/// <summary>
/// Owns the OAuth device-code sign-in + refresh-token lifecycle for one provider
/// (e.g. "github" or "ado"). Implementations persist the refresh token and refresh
/// the access token on demand.
/// </summary>
public interface IOAuthTokenProvider
{
    /// <summary>Stable provider id, e.g. "github" or "ado".</summary>
    string ProviderId { get; }

    /// <summary>Current sign-in status (no secrets).</summary>
    OAuthStatus Status { get; }

    /// <summary>Starts the device-code flow and begins background polling; returns the challenge to show the user.</summary>
    Task<DeviceCodeChallenge> BeginSignInAsync(CancellationToken ct = default);

    /// <summary>Clears stored tokens and resets to NotStarted.</summary>
    Task SignOutAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns a valid access token, transparently refreshing via the stored refresh token when
    /// the current one is missing or within the expiry skew. Throws InvalidOperationException
    /// when not signed in / refresh fails.
    /// </summary>
    Task<OAuthAccessToken> GetAccessTokenAsync(IReadOnlyList<string>? scopes = null, CancellationToken ct = default);
}
