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

/// <summary>
/// Result of starting an interactive browser sign-in. The provider opens the system browser
/// (the backend runs on the user's own machine, so "server-side" is the user's machine) and
/// completes the authorization-code exchange on a loopback redirect in the background; the caller
/// then polls <see cref="IOAuthTokenProvider.Status"/> until it reaches
/// <see cref="OAuthSignInState.SignedIn"/> or <see cref="OAuthSignInState.Failed"/>.
/// </summary>
/// <param name="AuthorizationUrl">The authorization URL that was opened — surfaced so the caller can
/// display/relaunch it if the browser failed to open automatically.</param>
/// <param name="BrowserLaunched">True when the app successfully launched the system browser.</param>
public sealed record SignInChallenge(string AuthorizationUrl, bool BrowserLaunched);

/// <summary>Current sign-in status (safe to expose to the UI; contains no secrets).</summary>
public sealed record OAuthStatus(
    OAuthSignInState State,
    string? Account,
    IReadOnlyList<string> Scopes,
    DateTimeOffset? ExpiresAtUtc,
    string? Error);

/// <summary>
/// Owns the OAuth sign-in + token lifecycle for one provider (e.g. "github" or "ado").
/// Sign-in is an interactive, browser-based authorization-code flow with a loopback redirect;
/// implementations persist their tokens and renew the access token on demand.
/// </summary>
public interface IOAuthTokenProvider
{
    /// <summary>Stable provider id, e.g. "github" or "ado".</summary>
    string ProviderId { get; }

    /// <summary>Current sign-in status (no secrets).</summary>
    OAuthStatus Status { get; }

    /// <summary>
    /// Loads any persisted token at startup so <see cref="Status"/> reflects a sign-in that happened
    /// in a previous run. Safe to call once at startup; a missing/invalid token leaves state NotStarted.
    /// </summary>
    Task HydrateFromStoreAsync(CancellationToken ct = default);

    /// <summary>
    /// Opens the system browser to begin the interactive authorization-code flow and starts the
    /// background loopback exchange; returns immediately with the URL that was opened. The caller
    /// polls <see cref="Status"/> for completion.
    /// </summary>
    Task<SignInChallenge> BeginSignInAsync(CancellationToken ct = default);

    /// <summary>Clears stored tokens and resets to NotStarted.</summary>
    Task SignOutAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns a valid access token, transparently refreshing when the current one is missing or
    /// within the expiry skew. Throws InvalidOperationException when not signed in / refresh fails.
    /// </summary>
    Task<OAuthAccessToken> GetAccessTokenAsync(IReadOnlyList<string>? scopes = null, CancellationToken ct = default);
}
