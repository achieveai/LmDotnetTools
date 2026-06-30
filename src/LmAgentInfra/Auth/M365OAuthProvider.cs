using System.Collections.Concurrent;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Identity.Client;

namespace AchieveAi.LmDotnetTools.LmAgentInfra.Auth;

/// <summary>
/// Microsoft 365 (Microsoft Graph) OAuth provider backed by MSAL's
/// <see cref="IConfidentialClientApplication"/> with the authorization-code + PKCE flow. The
/// callback is hosted by the app on its primary port (<see cref="M365AuthOptions.RedirectPath"/>,
/// served by <c>M365AuthController</c>) — no standalone <c>HttpListener</c> and no fixed port 3333.
/// MSAL owns the token cache + silent refresh; the provider only hand-rolls the authorize-URL
/// builder and the in-memory state→{verifier, account, expiry} flow map.
/// </summary>
/// <remarks>
/// <para>
/// SECURITY: the MSAL token cache (which contains refresh tokens) is serialized to a gitignored
/// <c>msal-m365.bin</c> file. The client secret comes from user-secrets / env only and is NEVER
/// logged. Token material never crosses this type's logging boundary — only provider id, sign-in
/// state, account, expiry, and OAuth error codes.
/// </para>
/// <para>
/// SCALABILITY: the state map is per-process / in-memory; not horizontally scalable. Acceptable for
/// the single-Kestrel-process sample. Each pending sign-in has a 10-minute hard expiry and is
/// single-use (replay attempts after redemption return null).
/// </para>
/// </remarks>
public sealed class M365OAuthProvider : OAuthProviderBase
{
    /// <summary>Maximum lifetime of a pending sign-in (state + verifier) before the entry is swept.</summary>
    internal static readonly TimeSpan PendingSignInTtl = TimeSpan.FromMinutes(10);

    private readonly M365AuthOptions _options;
    private readonly string _cacheFilePath;
    private readonly IConfidentialClientApplication? _app;
    private readonly string[] _scopes;
    private readonly ConcurrentDictionary<string, PendingSignIn> _pending = new(StringComparer.Ordinal);
    private readonly object _cacheLock = new();
    private readonly TimeProvider _time;

    /// <summary>Creates the M365 provider.</summary>
    /// <param name="options">Entra confidential client id, secret, tenant, scopes, redirect path.</param>
    /// <param name="callbackBaseUrl">App primary-port base URL (e.g. <c>http://localhost:5000</c>) — the callback redirect uri is built from this + <see cref="M365AuthOptions.RedirectPath"/>.</param>
    /// <param name="tokenCacheFilePath">Gitignored file the MSAL token cache is serialized to.</param>
    /// <param name="logger">Logger; token material is never written to it.</param>
    /// <param name="time">Clock — overridable for tests (defaults to <see cref="TimeProvider.System"/>).</param>
    /// <param name="msalHttpClientFactory">MSAL HTTP transport override — only used in tests to stub the
    /// Entra token endpoint without hitting the network. Production leaves it null so MSAL provisions
    /// its default HTTP client.</param>
    public M365OAuthProvider(
        M365AuthOptions options,
        string callbackBaseUrl,
        string tokenCacheFilePath,
        ILogger<M365OAuthProvider> logger,
        TimeProvider? time = null,
        IMsalHttpClientFactory? msalHttpClientFactory = null)
        : base(logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _cacheFilePath = tokenCacheFilePath ?? throw new ArgumentNullException(nameof(tokenCacheFilePath));
        _time = time ?? TimeProvider.System;

        _scopes = StripReservedScopes(_options.Scopes);
        CallbackBaseUrl = (callbackBaseUrl ?? throw new ArgumentNullException(nameof(callbackBaseUrl))).TrimEnd('/');

        // Leave the provider disabled (no MSAL app) when unconfigured — sign-in/token calls then
        // surface a clear "not configured" error on demand instead of failing host startup.
        if (string.IsNullOrWhiteSpace(_options.ClientId) || string.IsNullOrWhiteSpace(_options.ClientSecret))
        {
            return;
        }

        var authority = $"https://login.microsoftonline.com/{_options.TenantId}";
        var builder = ConfidentialClientApplicationBuilder
            .Create(_options.ClientId)
            .WithClientSecret(_options.ClientSecret)
            .WithAuthority(authority)
            .WithRedirectUri(BuildRedirectUri());

        if (msalHttpClientFactory is not null)
        {
            // Tests intercept the Entra token endpoint via this factory; instance discovery would
            // otherwise reach out to login.microsoftonline.com on the first call.
            builder = builder.WithHttpClientFactory(msalHttpClientFactory).WithInstanceDiscovery(false);
        }

        _app = builder.Build();

        _app.UserTokenCache.SetBeforeAccess(OnBeforeCacheAccess);
        _app.UserTokenCache.SetAfterAccess(OnAfterCacheAccess);
    }

    /// <inheritdoc />
    public override string ProviderId => "m365";

    /// <summary>The app's primary-port base URL the callback is served from. Test-visible.</summary>
    internal string CallbackBaseUrl { get; }

    /// <summary>True only when the provider is configured (client id + secret present).</summary>
    internal bool IsConfigured => _app is not null;

    /// <inheritdoc />
    public override async Task HydrateFromStoreAsync(CancellationToken ct = default)
    {
        try
        {
            var account = await GetAccountAsync().ConfigureAwait(false);
            if (account is null)
            {
                return;
            }

            SetStatus(new OAuthStatus(OAuthSignInState.SignedIn, account.Username, _options.Scopes, ExpiresAtUtc: null, Error: null));
            Logger.LogInformation("Restored persisted M365 sign-in (account {Account}).", account.Username);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to restore persisted M365 sign-in.");
        }
    }

    /// <inheritdoc />
    public override async Task<SignInChallenge> BeginSignInAsync(CancellationToken ct = default)
    {
        if (_app is null)
        {
            throw new InvalidOperationException("M365 OAuth is not configured (missing ClientId or ClientSecret).");
        }

        SweepExpired();

        var verifier = PkceHelper.CreateCodeVerifier();
        var challenge = PkceHelper.CreateCodeChallenge(verifier);
        var state = PkceHelper.CreateState();

        var redirectUri = BuildRedirectUri();
        var authorizeUrl = BuildAuthorizeUrl(_options.ClientId!, _options.TenantId, redirectUri, _scopes, state, challenge);

        // Single-use, time-bounded. AddOrUpdate isn't needed since state is RNG-derived (collisions
        // are vanishingly improbable) — TryAdd is enough and surfaces the unexpected collision case.
        var added = _pending.TryAdd(state, new PendingSignIn(verifier, _time.GetUtcNow() + PendingSignInTtl));
        if (!added)
        {
            throw new InvalidOperationException("Failed to register pending M365 sign-in (state collision).");
        }

        SetStatus(new OAuthStatus(OAuthSignInState.Pending, Account: null, _options.Scopes, ExpiresAtUtc: null, Error: null));
        var launched = OpenBrowser(authorizeUrl);
        Logger.LogInformation("M365 sign-in started (browser launched: {Launched}); awaiting app-hosted callback.", launched);

        // Without a deadline, an abandoned browser tab leaves status Pending forever: the callback
        // simply never arrives. Mirror GitHub's pattern — schedule a background timer that flips to
        // sign_in_timeout if the state is still pending at the TTL.
        await StartBackgroundSignInAsync(token => RunSignInDeadlineAsync(state, token)).ConfigureAwait(false);

        return new SignInChallenge(authorizeUrl, launched);
    }

    private async Task RunSignInDeadlineAsync(string state, CancellationToken token)
    {
        try
        {
            await Task.Delay(PendingSignInTtl, _time, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        // Race against the callback: only flip if the state entry is still pending. CompleteSignInAsync
        // removes the entry on success/failure, so an in-flight redemption clears it before we get here.
        if (_pending.TryRemove(state, out _) && Status.State == OAuthSignInState.Pending)
        {
            SetFailed("sign_in_timeout");
            Logger.LogWarning("M365 sign-in timed out after {Timeout:c} awaiting the app-hosted callback.", PendingSignInTtl);
        }
    }

    /// <inheritdoc />
    public override async Task SignOutAsync(CancellationToken ct = default)
    {
        await CancelSignInAsync().ConfigureAwait(false);
        _pending.Clear();

        if (_app is not null)
        {
            // GetAccountsAsync is obsolete on IConfidentialClientApplication in favour of
            // GetAccountAsync(identifier) for distributed cache scenarios; this sample uses a
            // single-user file-backed MSAL cache, so enumerating accounts is the correct path.
#pragma warning disable CS0618
            var accounts = await _app.GetAccountsAsync().ConfigureAwait(false);
#pragma warning restore CS0618
            foreach (var account in accounts)
            {
                await _app.RemoveAsync(account).ConfigureAwait(false);
            }
        }

        lock (_cacheLock)
        {
            if (File.Exists(_cacheFilePath))
            {
                File.Delete(_cacheFilePath);
            }
        }

        SetStatus(new OAuthStatus(OAuthSignInState.NotStarted, Account: null, Scopes: [], ExpiresAtUtc: null, Error: null));
        Logger.LogInformation("Signed out of M365.");
    }

    /// <inheritdoc />
    public override async Task<OAuthAccessToken> GetAccessTokenAsync(IReadOnlyList<string>? scopes = null, CancellationToken ct = default)
    {
        var app = _app ?? throw new InvalidOperationException("M365 provider is not signed in.");
        var account = await GetAccountAsync().ConfigureAwait(false)
            ?? throw new InvalidOperationException("M365 provider is not signed in.");

        try
        {
            var result = await app.AcquireTokenSilent(_scopes, account).ExecuteAsync(ct).ConfigureAwait(false);
            return new OAuthAccessToken(result.AccessToken, result.ExpiresOn);
        }
        catch (MsalUiRequiredException ex)
        {
            SetFailed("interaction_required");
            throw new InvalidOperationException("M365 sign-in expired or was revoked; sign in again.", ex);
        }
    }

    /// <summary>
    /// Completes the auth-code flow for the callback at <see cref="M365AuthOptions.RedirectPath"/>:
    /// validates state (single-use + expiry), redeems the code with the stored PKCE verifier, and
    /// updates <see cref="IOAuthTokenProvider.Status"/>. Returns the OAuth error code on failure
    /// (so the controller can surface a deny landing page) and null on success.
    /// </summary>
    /// <param name="code">Authorization code returned by Entra.</param>
    /// <param name="state">OAuth state token (must match a pending sign-in).</param>
    /// <param name="ct">Cancellation token.</param>
    internal async Task<string?> CompleteSignInAsync(string? code, string? state, CancellationToken ct = default)
    {
        if (_app is null)
        {
            return "not_configured";
        }

        if (string.IsNullOrEmpty(state) || !_pending.TryRemove(state, out var pending))
        {
            // Don't demote an already-SignedIn user to Failed on a stale/replay callback —
            // the real tokens live in the MSAL cache and remain valid; downgrading the visible
            // status would make the webhook deny good tokens and force a needless re-sign-in.
            if (Status.State != OAuthSignInState.SignedIn)
            {
                SetFailed("state_mismatch");
            }
            Logger.LogWarning("M365 callback rejected: state did not match a pending sign-in (or replay).");
            return "state_mismatch";
        }

        if (_time.GetUtcNow() > pending.ExpiresAtUtc)
        {
            SetFailed("sign_in_expired");
            Logger.LogWarning("M365 callback rejected: pending sign-in expired.");
            return "sign_in_expired";
        }

        if (string.IsNullOrEmpty(code))
        {
            SetFailed("no_code");
            Logger.LogWarning("M365 callback rejected: authorization response carried no code.");
            return "no_code";
        }

        try
        {
            // Redirect URI is fixed on the confidential client at app-build time (the MSAL builder
            // call); the auth-code request reuses it implicitly.
            var result = await _app
                .AcquireTokenByAuthorizationCode(_scopes, code)
                .WithPkceCodeVerifier(pending.Verifier)
                .ExecuteAsync(ct)
                .ConfigureAwait(false);

            // Report MSAL's GRANTED scopes (what was actually consented), not the requested set:
            // this reflects what the token can do and dodges the config-binder's append-onto-array
            // duplication that would surface in the UI-facing status.
            var grantedScopes = result.Scopes?.ToArray() ?? [];
            SetStatus(new OAuthStatus(OAuthSignInState.SignedIn, result.Account.Username, grantedScopes, result.ExpiresOn, Error: null));
            Logger.LogInformation("Signed in to M365 as {Account} (expires {ExpiresAt:o}).", result.Account.Username, result.ExpiresOn);
            return null;
        }
        catch (MsalServiceException ex)
        {
            SetFailed(ex.ErrorCode ?? "exchange_failed");
            Logger.LogWarning(ex, "M365 code exchange failed: {ErrorCode}.", ex.ErrorCode);
            return ex.ErrorCode ?? "exchange_failed";
        }
        catch (Exception ex)
        {
            SetFailed("exchange_failed");
            Logger.LogWarning(ex, "M365 code exchange failed.");
            return "exchange_failed";
        }
    }

    /// <summary>True when <paramref name="state"/> is registered as a pending sign-in (test-visible).</summary>
    internal bool HasPending(string state) => _pending.ContainsKey(state);

    /// <summary>Test seam: register a pending sign-in directly without the browser dance.</summary>
    internal void RegisterPendingForTest(string state, string verifier, DateTimeOffset expiresAtUtc) =>
        _pending[state] = new PendingSignIn(verifier, expiresAtUtc);

    /// <summary>
    /// Builds the Entra authorize URL with the configured scopes + PKCE. Internal so tests can
    /// assert the URL shape without driving the browser flow.
    /// </summary>
    internal static string BuildAuthorizeUrl(
        string clientId,
        string tenantId,
        string redirectUri,
        IReadOnlyList<string> scopes,
        string state,
        string codeChallenge)
    {
        var endpoint = $"https://login.microsoftonline.com/{tenantId}/oauth2/v2.0/authorize";
        // offline_access is reserved on MSAL.Acquire* calls but REQUIRED on the authorize URL
        // request to make Entra issue a refresh token; carry it through unconditionally.
        var scopeJoined = string.Join(' ', scopes.Where(s => !string.IsNullOrEmpty(s)).Append("offline_access").Distinct(StringComparer.OrdinalIgnoreCase));
        return QueryHelpers.AddQueryString(endpoint, new Dictionary<string, string?>
        {
            ["client_id"] = clientId,
            ["response_type"] = "code",
            ["redirect_uri"] = redirectUri,
            ["response_mode"] = "query",
            ["scope"] = scopeJoined,
            ["state"] = state,
            ["code_challenge"] = codeChallenge,
            ["code_challenge_method"] = "S256",
        });
    }

    private string BuildRedirectUri()
    {
        var path = _options.RedirectPath.StartsWith('/') ? _options.RedirectPath : "/" + _options.RedirectPath;
        return CallbackBaseUrl + path;
    }

    private async Task<IAccount?> GetAccountAsync()
    {
        if (_app is null)
        {
            return null;
        }

        // See SignOutAsync — single-user file cache, GetAccountsAsync is the right call.
#pragma warning disable CS0618
        var accounts = await _app.GetAccountsAsync().ConfigureAwait(false);
#pragma warning restore CS0618
        return accounts.FirstOrDefault();
    }

    private void SweepExpired()
    {
        var now = _time.GetUtcNow();
        foreach (var (key, entry) in _pending)
        {
            if (entry.ExpiresAtUtc < now)
            {
                _ = _pending.TryRemove(key, out _);
            }
        }
    }

    private void OnBeforeCacheAccess(TokenCacheNotificationArgs args)
    {
        lock (_cacheLock)
        {
            if (!File.Exists(_cacheFilePath))
            {
                return;
            }

            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(_cacheFilePath);
            }
            catch (IOException ex)
            {
                // Cache file is locked by another process or unreadable. Skip the load and let MSAL
                // operate against an empty cache — the user will see NotStarted and re-sign-in.
                Logger.LogWarning(ex, "M365 token cache could not be read; continuing with an empty cache.");
                return;
            }

            try
            {
                args.TokenCache.DeserializeMsalV3(bytes);
            }
            catch (Exception ex)
            {
                // Corrupt / truncated cache (crash mid-write, disk full). Treat as empty rather than
                // wedging the token-acquire path — the user re-signs once and the cache is rewritten.
                Logger.LogWarning(ex, "M365 token cache at {Path} was unreadable and will be ignored.", _cacheFilePath);
            }
        }
    }

    private void OnAfterCacheAccess(TokenCacheNotificationArgs args)
    {
        if (!args.HasStateChanged)
        {
            return;
        }

        lock (_cacheLock)
        {
            var dir = Path.GetDirectoryName(_cacheFilePath);
            if (!string.IsNullOrEmpty(dir))
            {
                _ = Directory.CreateDirectory(dir);
            }

            File.WriteAllBytes(_cacheFilePath, args.TokenCache.SerializeMsalV3());
        }
    }

    private sealed record PendingSignIn(string Verifier, DateTimeOffset ExpiresAtUtc);
}
