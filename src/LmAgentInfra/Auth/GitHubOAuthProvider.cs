using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;

namespace AchieveAi.LmDotnetTools.LmAgentInfra.Auth;

/// <summary>
/// GitHub OAuth provider using the browser-based <em>authorization-code</em> ("web application")
/// flow with a loopback redirect — the same flow the GitHub CLI uses when a browser is available.
/// The system browser is opened to GitHub's authorize page; GitHub redirects back to an ephemeral
/// <c>http://127.0.0.1:&lt;port&gt;/callback</c> the provider listens on, and the code is exchanged
/// for a token server-side.
/// </summary>
/// <remarks>
/// <para>
/// GitHub requires the client <em>secret</em> in the code→token exchange even with PKCE (it does not
/// distinguish public vs confidential clients), so a client secret is configured. The default
/// id/secret are the GitHub CLI's first-party values, which GitHub publishes as safe to embed.
/// </para>
/// <para>
/// The reused CLI app is a classic OAuth App, so its tokens are <em>non-expiring</em> with no
/// <c>refresh_token</c>; the token is stored with a far-future expiry and returned as-is. A GitHub
/// App that issues expiring tokens + a refresh token is also handled (refresh-token grant).
/// </para>
/// </remarks>
public sealed class GitHubOAuthProvider : OAuthProviderBase
{
    private const string UserAgent = "LmStreaming.Sample";
    private const string AuthorizeEndpoint = "https://github.com/login/oauth/authorize";
    private const string TokenEndpoint = "https://github.com/login/oauth/access_token";
    private static readonly TimeSpan ExpirySkew = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan SignInTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan CallbackReadTimeout = TimeSpan.FromSeconds(10);
    private static DateTimeOffset NonExpiringSentinel => DateTimeOffset.UtcNow.AddYears(100);

    private readonly GitHubAuthOptions _options;
    private readonly IOAuthTokenStore _store;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    /// <summary>Creates the GitHub web-app-flow provider.</summary>
    /// <param name="options">GitHub client id, client secret + scopes.</param>
    /// <param name="store">Token persistence (gitignored).</param>
    /// <param name="http">HTTP client for the OAuth + API calls.</param>
    /// <param name="logger">Logger; token material is never written to it.</param>
    public GitHubOAuthProvider(
        GitHubAuthOptions options,
        IOAuthTokenStore store,
        HttpClient http,
        ILogger<GitHubOAuthProvider> logger)
        : base(logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _http = http ?? throw new ArgumentNullException(nameof(http));
    }

    /// <inheritdoc />
    public override string ProviderId => "github";

    /// <inheritdoc />
    public override async Task HydrateFromStoreAsync(CancellationToken ct = default)
    {
        try
        {
            var record = await _store.GetAsync(ProviderId, ct).ConfigureAwait(false);
            if (record is null)
            {
                return;
            }

            SetStatus(new OAuthStatus(OAuthSignInState.SignedIn, record.Account, record.Scopes, record.AccessTokenExpiresAtUtc, Error: null));
            Logger.LogInformation("Restored persisted GitHub sign-in (account {Account}).", record.Account ?? "(unknown)");
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to restore persisted GitHub sign-in.");
        }
    }

    /// <inheritdoc />
    public override async Task<SignInChallenge> BeginSignInAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_options.ClientId))
        {
            throw new InvalidOperationException("GitHub OAuth is not configured (missing ClientId).");
        }

        if (string.IsNullOrWhiteSpace(_options.ClientSecret))
        {
            throw new InvalidOperationException("GitHub OAuth is not configured (missing ClientSecret).");
        }

        // Bind a loopback listener on an ephemeral port, then advertise it as the redirect uri.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var redirectUri = $"http://127.0.0.1:{port}/callback";

        var verifier = PkceHelper.CreateCodeVerifier();
        var challenge = PkceHelper.CreateCodeChallenge(verifier);
        var state = PkceHelper.CreateState();

        var authorizeUrl = BuildAuthorizeUrl(_options.ClientId, redirectUri, _options.Scopes, state, challenge);

        SetStatus(new OAuthStatus(OAuthSignInState.Pending, Account: null, _options.Scopes, ExpiresAtUtc: null, Error: null));
        var launched = OpenBrowser(authorizeUrl);
        Logger.LogInformation("GitHub sign-in started (browser launched: {Launched}); awaiting loopback callback on port {Port}.", launched, port);

        await StartBackgroundSignInAsync(async token =>
        {
            // Overall deadline: without it an abandoned browser tab leaves the provider Pending
            // forever — the callback simply never arrives and nothing else fails.
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(SignInTimeout);
            try
            {
                var query = await WaitForLoopbackCallbackAsync(listener, deadline.Token).ConfigureAwait(false);
                if (!query.TryGetValue("state", out var returnedState) || returnedState != state)
                {
                    SetFailed("state_mismatch");
                    Logger.LogWarning("GitHub sign-in aborted: OAuth state did not match.");
                    return;
                }

                if (query.TryGetValue("error", out var oauthError))
                {
                    SetFailed(oauthError ?? "authorization_error");
                    Logger.LogWarning("GitHub sign-in failed at authorize step: {Error}.", oauthError);
                    return;
                }

                if (!query.TryGetValue("code", out var code) || string.IsNullOrEmpty(code))
                {
                    SetFailed("no_code");
                    Logger.LogWarning("GitHub sign-in failed: authorization response carried no code.");
                    return;
                }

                await ExchangeCodeAndPersistAsync(code, verifier, redirectUri, deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                SetFailed("sign_in_timeout");
                Logger.LogWarning("GitHub sign-in timed out after {Timeout:c} awaiting the loopback callback.", SignInTimeout);
            }
            finally
            {
                listener.Stop();
            }
        }).ConfigureAwait(false);

        return new SignInChallenge(authorizeUrl, launched);
    }

    /// <inheritdoc />
    public override async Task SignOutAsync(CancellationToken ct = default)
    {
        await CancelSignInAsync().ConfigureAwait(false);
        await _store.RemoveAsync(ProviderId, ct).ConfigureAwait(false);
        SetStatus(new OAuthStatus(OAuthSignInState.NotStarted, Account: null, Scopes: [], ExpiresAtUtc: null, Error: null));
        Logger.LogInformation("Signed out of GitHub.");
    }

    /// <inheritdoc />
    public override async Task<OAuthAccessToken> GetAccessTokenAsync(IReadOnlyList<string>? scopes = null, CancellationToken ct = default)
    {
        var record = await ReadRecordAsync(ct).ConfigureAwait(false);
        if (TryGetValidToken(record) is { } token)
        {
            return token;
        }

        // Single-flight the refresh: GitHub-App refresh tokens are SINGLE-USE, so concurrent
        // refreshes would race — the loser's grant comes back `invalid_grant` after the stored
        // refresh token was already burned, leaving the provider dead until a manual re-sign-in.
        await _refreshGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Re-read: a concurrent caller may have refreshed and persisted while we waited.
            record = await ReadRecordAsync(ct).ConfigureAwait(false);
            if (TryGetValidToken(record) is { } refreshed)
            {
                return refreshed;
            }

            return await RefreshAccessTokenAsync(record, ct).ConfigureAwait(false);
        }
        finally
        {
            _ = _refreshGate.Release();
        }
    }

    /// <summary>Reads the persisted token record, throwing when the provider is not signed in.</summary>
    private async Task<OAuthTokenRecord> ReadRecordAsync(CancellationToken ct)
    {
        var record = await _store.GetAsync(ProviderId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("GitHub provider is not signed in.");

        if (string.IsNullOrEmpty(record.AccessToken))
        {
            throw new InvalidOperationException("GitHub provider has no stored access token; sign in again.");
        }

        return record;
    }

    /// <summary>
    /// Returns the stored token when no refresh is needed (classic non-expiring OAuth-App token, or
    /// an expiring token still comfortably within its lifetime); null when a refresh is required.
    /// The record must come from <see cref="ReadRecordAsync"/>, which guarantees a non-empty token.
    /// </summary>
    private static OAuthAccessToken? TryGetValidToken(OAuthTokenRecord record)
    {
        var expiry = record.AccessTokenExpiresAtUtc ?? NonExpiringSentinel;
        var canRefresh = !string.IsNullOrEmpty(record.RefreshToken);

        // Classic OAuth-App tokens never expire and can't be refreshed — return what we have.
        return !canRefresh || expiry - ExpirySkew > DateTimeOffset.UtcNow
            ? new OAuthAccessToken(record.AccessToken!, expiry)
            : null;
    }

    /// <summary>Exchanges the authorization code (with the PKCE verifier) for a token and persists it.</summary>
    private async Task ExchangeCodeAndPersistAsync(string code, string verifier, string redirectUri, CancellationToken ct)
    {
        var form = new Dictionary<string, string>
        {
            ["client_id"] = _options.ClientId!,
            ["client_secret"] = _options.ClientSecret!,
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["code_verifier"] = verifier,
        };

        var token = await PostTokenAsync(form, ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(token.AccessToken))
        {
            SetFailed(token.Error ?? "exchange_failed");
            Logger.LogWarning("GitHub code exchange failed: {Error}.", token.Error ?? "no access_token");
            return;
        }

        var account = await SafeResolveLoginAsync(token.AccessToken, ct).ConfigureAwait(false);
        var record = new OAuthTokenRecord(
            ProviderId,
            account,
            token.RefreshToken ?? string.Empty,
            token.AccessToken,
            token.ExpiresIn > 0 ? DateTimeOffset.UtcNow.AddSeconds(token.ExpiresIn) : NonExpiringSentinel,
            _options.Scopes);

        await _store.SaveAsync(record, ct).ConfigureAwait(false);
        SetStatus(new OAuthStatus(OAuthSignInState.SignedIn, account, _options.Scopes, record.AccessTokenExpiresAtUtc, Error: null));
        Logger.LogInformation("Signed in to GitHub as {Account}.", account ?? "<unknown>");
    }

    /// <summary>Renews an expiring token via the refresh-token grant (GitHub App tokens only).</summary>
    private async Task<OAuthAccessToken> RefreshAccessTokenAsync(OAuthTokenRecord record, CancellationToken ct)
    {
        var form = new Dictionary<string, string>
        {
            ["client_id"] = _options.ClientId!,
            ["client_secret"] = _options.ClientSecret!,
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = record.RefreshToken,
        };

        var token = await PostTokenAsync(form, ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(token.AccessToken))
        {
            SetFailed(token.Error ?? "refresh_failed");
            throw new InvalidOperationException($"GitHub token refresh failed: {token.Error ?? "no access_token"}.");
        }

        var expiry = token.ExpiresIn > 0 ? DateTimeOffset.UtcNow.AddSeconds(token.ExpiresIn) : NonExpiringSentinel;
        var updated = record with
        {
            AccessToken = token.AccessToken,
            AccessTokenExpiresAtUtc = expiry,
            RefreshToken = string.IsNullOrEmpty(token.RefreshToken) ? record.RefreshToken : token.RefreshToken,
        };
        await _store.SaveAsync(updated, ct).ConfigureAwait(false);
        SetStatus(new OAuthStatus(OAuthSignInState.SignedIn, updated.Account, updated.Scopes, expiry, Error: null));
        Logger.LogInformation("Refreshed GitHub access token (expires {ExpiresAt:o}).", expiry);
        return new OAuthAccessToken(token.AccessToken, expiry);
    }

    /// <summary>POSTs to the token endpoint (Accept: json) and parses the token/error response.</summary>
    private async Task<TokenResponse> PostTokenAsync(Dictionary<string, string> form, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(form),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(body))
        {
            return new TokenResponse(null, null, 0, "empty_response");
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            return new TokenResponse(
                root.TryGetProperty("access_token", out var at) ? at.GetString() : null,
                root.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null,
                root.TryGetProperty("expires_in", out var ei) && ei.TryGetInt32(out var seconds) ? seconds : 0,
                root.TryGetProperty("error", out var er) ? er.GetString() : null);
        }
        catch (JsonException ex)
        {
            Logger.LogWarning(ex, "GitHub token endpoint returned an unparseable body (HTTP {Status}).", (int)response.StatusCode);
            return new TokenResponse(null, null, 0, "unparseable_response");
        }
    }

    /// <summary>Best-effort resolution of the GitHub login; returns null on any failure.</summary>
    private async Task<string?> SafeResolveLoginAsync(string accessToken, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/user");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            request.Headers.UserAgent.ParseAdd(UserAgent);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                Logger.LogDebug("GitHub /user lookup returned HTTP {Status}; account name unavailable.", (int)response.StatusCode);
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            return doc.RootElement.TryGetProperty("login", out var login) ? login.GetString() : null;
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "GitHub account resolution failed (best-effort).");
            return null;
        }
    }

    /// <summary>
    /// Accepts loopback connections until the <c>/callback</c> request arrives, then parses its
    /// query string and writes a small "you can close this tab" HTML response. Stray connections
    /// (favicon probes, port scanners) get a 404 and are skipped rather than consuming the callback
    /// wait, and each connection's read is bounded so a stalled client cannot wedge the sign-in.
    /// </summary>
    private static async Task<IReadOnlyDictionary<string, string?>> WaitForLoopbackCallbackAsync(TcpListener listener, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            using var client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            try
            {
                await using var stream = client.GetStream();
                using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                readCts.CancelAfter(CallbackReadTimeout);

                var buffer = new byte[8192];
                var read = await stream.ReadAsync(buffer, readCts.Token).ConfigureAwait(false);
                var requestLine = Encoding.ASCII.GetString(buffer, 0, read).Split('\n').FirstOrDefault() ?? string.Empty;

                // Request line: "GET /callback?code=...&state=... HTTP/1.1"
                var parts = requestLine.Split(' ');
                var target = parts.Length > 1 ? parts[1] : "/";
                var queryStart = target.IndexOf('?', StringComparison.Ordinal);
                var path = queryStart >= 0 ? target[..queryStart] : target;
                var query = queryStart >= 0 ? target[queryStart..] : string.Empty;

                if (!string.Equals(path, "/callback", StringComparison.Ordinal))
                {
                    const string notFound = "<!doctype html><html><body>Not found.</body></html>";
                    await WriteHttpResponseAsync(stream, "404 Not Found", notFound, readCts.Token).ConfigureAwait(false);
                    continue;
                }

                const string html = "<!doctype html><html><body style=\"font-family:sans-serif\">"
                    + "<h3>Sign-in complete</h3><p>You can close this tab and return to the app.</p></body></html>";
                await WriteHttpResponseAsync(stream, "200 OK", html, readCts.Token).ConfigureAwait(false);

                return QueryHelpers.ParseQuery(query).ToDictionary(kvp => kvp.Key, kvp => (string?)kvp.Value.ToString());
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Per-connection read timeout: drop the stalled connection, keep awaiting the callback.
            }
            catch (IOException)
            {
                // Connection reset by a stray client — keep awaiting the callback.
            }
        }
    }

    /// <summary>Writes a minimal HTTP/1.1 response with an HTML body to the raw loopback stream.</summary>
    private static async Task WriteHttpResponseAsync(NetworkStream stream, string status, string html, CancellationToken ct)
    {
        var response = $"HTTP/1.1 {status}\r\nContent-Type: text/html; charset=utf-8\r\n"
            + $"Content-Length: {Encoding.UTF8.GetByteCount(html)}\r\nConnection: close\r\n\r\n{html}";
        await stream.WriteAsync(Encoding.UTF8.GetBytes(response), ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Builds the GitHub authorize URL with PKCE (S256), the loopback redirect, scopes and state.
    /// Exposed internally so the URL shape can be asserted without driving the interactive flow.
    /// </summary>
    internal static string BuildAuthorizeUrl(
        string clientId,
        string redirectUri,
        IReadOnlyList<string> scopes,
        string state,
        string codeChallenge) =>
        QueryHelpers.AddQueryString(AuthorizeEndpoint, new Dictionary<string, string?>
        {
            ["client_id"] = clientId,
            ["redirect_uri"] = redirectUri,
            ["scope"] = string.Join(' ', scopes),
            ["state"] = state,
            ["code_challenge"] = codeChallenge,
            ["code_challenge_method"] = "S256",
        });

    /// <summary>Parsed token-endpoint response (success or OAuth error).</summary>
    private sealed record TokenResponse(string? AccessToken, string? RefreshToken, int ExpiresIn, string? Error);
}
