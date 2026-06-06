using Microsoft.Identity.Client;

namespace LmStreaming.Sample.Services.Auth;

/// <summary>
/// Azure DevOps OAuth provider backed by <strong>MSAL.NET</strong> (Microsoft Entra). Sign-in is the
/// interactive authorization-code + PKCE flow with a loopback redirect — MSAL opens the system
/// browser, runs its own loopback listener, exchanges the code, and owns the token cache (including
/// silent refresh). This mirrors how the <c>azure-devops-mcp</c> authenticates via
/// <c>@azure/msal-node</c>'s <c>acquireTokenInteractive</c>/<c>acquireTokenSilent</c>.
/// </summary>
/// <remarks>
/// SECURITY: MSAL's token cache (which contains refresh tokens) is serialized to a gitignored file
/// under <c>oauth-tokens/</c>. As with <see cref="FileOAuthTokenStore"/>, the blob is plaintext at
/// rest — acceptable for a local-dev sample; harden with OS-protected storage for production. Token
/// material is never logged.
/// </remarks>
public sealed class AdoOAuthProvider : OAuthProviderBase
{
    // Reserved OIDC scopes MSAL injects itself; passing them through throws "does not accept reserved scopes".
    private static readonly HashSet<string> ReservedScopes = new(StringComparer.OrdinalIgnoreCase)
    {
        "openid",
        "profile",
        "offline_access",
    };

    private readonly AdoAuthOptions _options;
    private readonly IPublicClientApplication? _app;
    private readonly string[] _scopes;
    private readonly string _cacheFilePath;
    private readonly object _cacheLock = new();

    /// <summary>Creates the Azure DevOps (Entra/MSAL) provider.</summary>
    /// <param name="options">Entra client id, tenant + scopes.</param>
    /// <param name="tokenCacheFilePath">Gitignored file the MSAL token cache is serialized to.</param>
    /// <param name="logger">Logger; token material is never written to it.</param>
    public AdoOAuthProvider(
        AdoAuthOptions options,
        string tokenCacheFilePath,
        ILogger<AdoOAuthProvider> logger)
        : base(logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _cacheFilePath = tokenCacheFilePath ?? throw new ArgumentNullException(nameof(tokenCacheFilePath));

        // MSAL handles refresh itself, so strip reserved scopes (e.g. offline_access) from config.
        _scopes = StripReservedScopes(_options.Scopes);

        // Leave the provider disabled (no MSAL app) when unconfigured, rather than failing host
        // startup — sign-in/token calls then surface a clear "not configured" error on demand.
        if (string.IsNullOrWhiteSpace(_options.ClientId))
        {
            return;
        }

        var authority = $"https://login.microsoftonline.com/{_options.TenantId}";
        _app = PublicClientApplicationBuilder
            .Create(_options.ClientId)
            .WithAuthority(authority)
            .WithRedirectUri("http://localhost")
            .Build();

        _app.UserTokenCache.SetBeforeAccess(OnBeforeCacheAccess);
        _app.UserTokenCache.SetAfterAccess(OnAfterCacheAccess);
    }

    /// <inheritdoc />
    public override string ProviderId => "ado";

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
            Logger.LogInformation("Restored persisted ADO sign-in (account {Account}).", account.Username);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to restore persisted ADO sign-in.");
        }
    }

    /// <inheritdoc />
    public override async Task<SignInChallenge> BeginSignInAsync(CancellationToken ct = default)
    {
        var app = _app ?? throw new InvalidOperationException("ADO OAuth is not configured (missing ClientId).");

        // MSAL computes the authorize URL asynchronously inside ExecuteAsync; capture it so we can
        // return it (and open the browser ourselves) the moment MSAL is ready.
        var urlReady = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var launchedSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var webViewOptions = new SystemWebViewOptions
        {
            OpenBrowserAsync = uri =>
            {
                urlReady.TrySetResult(uri.AbsoluteUri);
                launchedSignal.TrySetResult(OpenBrowser(uri.AbsoluteUri));
                return Task.CompletedTask;
            },
        };

        SetStatus(new OAuthStatus(OAuthSignInState.Pending, Account: null, _options.Scopes, ExpiresAtUtc: null, Error: null));

        await StartBackgroundSignInAsync(async token =>
        {
            // Cancelling the originating request aborts the WHOLE interactive sign-in (not just
            // the URL wait below): link the request ct into MSAL's flow so an abandoned/aborted
            // sign-in request doesn't leave an orphaned MSAL listener running in the background.
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, ct);
            try
            {
                var result = await app
                    .AcquireTokenInteractive(_scopes)
                    .WithSystemWebViewOptions(webViewOptions)
                    .WithUseEmbeddedWebView(false)
                    .ExecuteAsync(linked.Token)
                    .ConfigureAwait(false);

                urlReady.TrySetResult(string.Empty);
                SetStatus(new OAuthStatus(OAuthSignInState.SignedIn, result.Account.Username, _options.Scopes, result.ExpiresOn, Error: null));
                Logger.LogInformation("Signed in to ADO as {Account} (expires {ExpiresAt:o}).", result.Account.Username, result.ExpiresOn);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested && !token.IsCancellationRequested)
            {
                // Aborted by the request, not by sign-out/re-sign-in: leave the provider in a clean
                // NotStarted state instead of Pending-forever, then let the base log the cancellation.
                SetStatus(new OAuthStatus(OAuthSignInState.NotStarted, Account: null, Scopes: [], ExpiresAtUtc: null, Error: null));
                Logger.LogInformation("ADO sign-in aborted by the originating request.");
                throw;
            }
        }).ConfigureAwait(false);

        // Wait briefly for MSAL to produce the authorize URL; fall back to the authority on timeout.
        var url = await Task.WhenAny(urlReady.Task, Task.Delay(TimeSpan.FromSeconds(10), ct)).ConfigureAwait(false) == urlReady.Task
            ? urlReady.Task.Result
            : $"https://login.microsoftonline.com/{_options.TenantId}/oauth2/v2.0/authorize";
        var launched = launchedSignal.Task.IsCompletedSuccessfully && launchedSignal.Task.Result;

        Logger.LogInformation("ADO sign-in started (browser launched: {Launched}).", launched);
        return new SignInChallenge(url, launched);
    }

    /// <inheritdoc />
    public override async Task SignOutAsync(CancellationToken ct = default)
    {
        await CancelSignInAsync().ConfigureAwait(false);

        if (_app is not null)
        {
            foreach (var account in await _app.GetAccountsAsync().ConfigureAwait(false))
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
        Logger.LogInformation("Signed out of ADO.");
    }

    /// <inheritdoc />
    public override async Task<OAuthAccessToken> GetAccessTokenAsync(IReadOnlyList<string>? scopes = null, CancellationToken ct = default)
    {
        var account = await GetAccountAsync().ConfigureAwait(false)
            ?? throw new InvalidOperationException("ADO provider is not signed in.");
        var app = _app ?? throw new InvalidOperationException("ADO provider is not signed in.");

        try
        {
            var result = await app.AcquireTokenSilent(_scopes, account).ExecuteAsync(ct).ConfigureAwait(false);
            return new OAuthAccessToken(result.AccessToken, result.ExpiresOn);
        }
        catch (MsalUiRequiredException ex)
        {
            SetFailed("interaction_required");
            throw new InvalidOperationException("ADO sign-in expired or was revoked; sign in again.", ex);
        }
    }

    /// <summary>
    /// Drops MSAL's reserved OIDC scopes (e.g. <c>offline_access</c>) from a configured scope list,
    /// since MSAL injects them itself and rejects them if passed through. Exposed internally for test.
    /// </summary>
    internal static string[] StripReservedScopes(IEnumerable<string> scopes) =>
        [.. scopes.Where(s => !ReservedScopes.Contains(s))];

    /// <summary>Returns the single cached account, or null when unconfigured / none is signed in.</summary>
    private async Task<IAccount?> GetAccountAsync()
    {
        if (_app is null)
        {
            return null;
        }

        var accounts = await _app.GetAccountsAsync().ConfigureAwait(false);
        return accounts.FirstOrDefault();
    }

    private void OnBeforeCacheAccess(TokenCacheNotificationArgs args)
    {
        lock (_cacheLock)
        {
            if (File.Exists(_cacheFilePath))
            {
                args.TokenCache.DeserializeMsalV3(File.ReadAllBytes(_cacheFilePath));
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
}
