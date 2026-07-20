namespace AchieveAi.LmDotnetTools.LmAgentInfra.Auth;

/// <summary>
/// An <see cref="IOAuthTokenProvider"/> for ONE predefined egress key. A single instance is kept per
/// entry id for its whole lifetime (the registry mutates it in place via <see cref="UpdateEntry"/> on
/// edit) so a held deferred-auth prompt polling <see cref="GetAccessTokenAsync"/> observes an update
/// the moment the user saves it. Resolves through <c>AuthWebhookController</c> exactly like the OAuth
/// providers, but the controller reads <see cref="Hosts"/> / <see cref="BuildHeaders"/> /
/// <see cref="IncludeExpiry"/> off it to inject a custom header list (or a minted <c>Bearer</c> token).
/// </summary>
/// <remarks>
/// <para>Three kinds (<see cref="PredefinedKeyKind"/>):</para>
/// <list type="bullet">
/// <item><c>CustomHeaders</c> — no token; <see cref="GetAccessTokenAsync"/> returns a sentinel when the
/// header list is present (the controller uses <see cref="BuildHeaders"/>), or throws when it is empty
/// so the deferred prompt fires.</item>
/// <item><c>RefreshToken</c> / <c>ClientCredentials</c> — mints + auto-refreshes an access token via
/// <see cref="OAuthTokenEndpointClient"/>, persists it (and any rotated refresh token) through
/// <see cref="IOAuthTokenStore"/>, and marks itself invalid on a definitive credential rejection.</item>
/// </list>
/// <para>SECRET: never logs the header values, client secret, refresh token, or minted access token.</para>
/// </remarks>
internal sealed class PredefinedKeyProvider : IOAuthTokenProvider
{
    /// <summary>Refresh a token this long before its real expiry (matches the GitHub provider's skew).</summary>
    private static readonly TimeSpan ExpirySkew = TimeSpan.FromSeconds(120);

    /// <summary>Sentinel expiry for values with no lifetime (custom headers, or an endpoint that omits <c>expires_in</c>).</summary>
    private static readonly DateTimeOffset NonExpiring = new(9999, 12, 31, 23, 59, 59, TimeSpan.Zero);

    /// <summary>OAuth error codes that mean the credential itself is bad → invalidate + prompt (vs. a transient server error).</summary>
    private static readonly HashSet<string> CredentialRejectionErrors = new(StringComparer.OrdinalIgnoreCase)
    {
        "invalid_grant",
        "invalid_client",
        "unauthorized_client",
        "access_denied",
        "invalid_scope",
    };

    private readonly string _id;
    private readonly IOAuthTokenStore _tokenStore;
    private readonly OAuthTokenEndpointClient _endpoint;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    private volatile PredefinedKeyEntry _entry;
    private volatile bool _invalidated;
    private volatile OAuthStatus _status;
    private OAuthAccessToken? _cachedToken;

    public PredefinedKeyProvider(
        PredefinedKeyEntry entry,
        IOAuthTokenStore tokenStore,
        OAuthTokenEndpointClient endpoint,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(entry);
        _id = entry.Id;
        _entry = entry;
        _tokenStore = tokenStore ?? throw new ArgumentNullException(nameof(tokenStore));
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _status = new OAuthStatus(OAuthSignInState.NotStarted, null, entry.Scopes, null, null);
    }

    /// <inheritdoc />
    public string ProviderId => $"predefined-{_id}";

    /// <inheritdoc />
    public OAuthStatus Status => _status;

    /// <summary>The entry as it currently stands (secret-bearing — for the registry's rule building only).</summary>
    public PredefinedKeyEntry Entry => _entry;

    /// <summary>The single destination host this key authenticates egress to, as a one-element list.</summary>
    public IReadOnlyList<string> Hosts => [_entry.Host];

    /// <summary>True for the token-minting kinds (their token carries a real expiry the gateway caches on).</summary>
    public bool IncludeExpiry => _entry.Kind != PredefinedKeyKind.CustomHeaders;

    /// <summary>
    /// The header(s) to inject: the stored list verbatim for custom-headers, or a single
    /// <c>Bearer &lt;token&gt;</c> under the configured header name for the OAuth kinds.
    /// </summary>
    public IReadOnlyList<KeyValuePair<string, string>> BuildHeaders(OAuthAccessToken? token)
    {
        var entry = _entry;
        if (entry.Kind == PredefinedKeyKind.CustomHeaders)
        {
            return [.. entry.Headers.Select(h => new KeyValuePair<string, string>(h.Name, h.Value))];
        }

        var value = token is null ? string.Empty : $"Bearer {token.Value}";
        return [new KeyValuePair<string, string>(entry.HeaderName, value)];
    }

    /// <summary>
    /// Swaps the entry in place (edit) and makes the new credential take effect immediately:
    /// serializes with any in-flight mint through <see cref="_refreshGate"/> (so a mint from the
    /// PRE-edit credential can't publish a token after the swap), clears the in-memory cache + the
    /// invalidated flag, and DROPS the persisted token record so the next acquisition re-mints with
    /// the new credential instead of reloading the stale access/rotated-refresh token by provider id.
    /// </summary>
    public async Task UpdateEntry(PredefinedKeyEntry entry, bool credentialChanged, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        await _refreshGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _entry = entry;
            _invalidated = false;
            // Only drop the in-memory minted token when the credential changed; a pure host/header edit
            // keeps the still-valid cached token (re-injected under the new host/header). Invalidating the
            // PERSISTED token — and its ordering relative to the durable definition write — is the
            // registry's job (see PredefinedKeyRegistry.UpsertAsync), so a partial failure degrades safely.
            if (credentialChanged)
            {
                _cachedToken = null;
                _status = new OAuthStatus(OAuthSignInState.NotStarted, null, entry.Scopes, null, null);
            }
        }
        finally
        {
            _ = _refreshGate.Release();
        }
    }

    /// <summary>
    /// True when the credential material (kind, token endpoint, client id/secret, refresh token, scopes)
    /// differs — the only case that invalidates a minted/persisted token. A pure host or header-name edit
    /// preserves the credential and, crucially, the latest ROTATED refresh token in the persisted record.
    /// </summary>
    internal static bool CredentialChanged(PredefinedKeyEntry oldEntry, PredefinedKeyEntry newEntry) =>
        oldEntry.Kind != newEntry.Kind
        || !string.Equals(oldEntry.TokenEndpoint, newEntry.TokenEndpoint, StringComparison.Ordinal)
        || !string.Equals(oldEntry.ClientId, newEntry.ClientId, StringComparison.Ordinal)
        || !string.Equals(oldEntry.ClientSecret, newEntry.ClientSecret, StringComparison.Ordinal)
        || !string.Equals(oldEntry.RefreshToken, newEntry.RefreshToken, StringComparison.Ordinal)
        || !oldEntry.Scopes.SequenceEqual(newEntry.Scopes, StringComparer.Ordinal);

    /// <inheritdoc />
    public async Task<OAuthAccessToken> GetAccessTokenAsync(
        IReadOnlyList<string>? scopes = null,
        CancellationToken ct = default)
    {
        // Everything runs under the gate so a captured entry can never be minted with a credential that
        // an interleaved UpdateEntry (also gated) has since superseded: the kind/invalidation/credential
        // decision and the mint all read one locked snapshot.
        await _refreshGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var entry = _entry;

            if (entry.Kind == PredefinedKeyKind.CustomHeaders)
            {
                if (entry.Headers.Count == 0)
                {
                    SetStatus(OAuthSignInState.NotStarted, "no headers configured");
                    throw new InvalidOperationException($"predefined key '{_id}' has no headers configured; add them.");
                }

                SetStatus(OAuthSignInState.SignedIn, null);
                // Sentinel: the controller injects BuildHeaders() for custom-headers, not this value.
                return new OAuthAccessToken(string.Empty, NonExpiring);
            }

            if (_invalidated)
            {
                throw new InvalidOperationException($"predefined key '{_id}' is marked invalid; update the credential.");
            }

            _cachedToken ??= await LoadCachedFromStoreAsync(ct).ConfigureAwait(false);

            if (_cachedToken is { } cached && cached.ExpiresAtUtc - ExpirySkew > DateTimeOffset.UtcNow)
            {
                return cached;
            }

            return await MintAsync(entry, ct).ConfigureAwait(false);
        }
        finally
        {
            _ = _refreshGate.Release();
        }
    }

    /// <summary>Mints (or refreshes) an access token via the token endpoint. Caller holds <see cref="_refreshGate"/>.</summary>
    private async Task<OAuthAccessToken> MintAsync(PredefinedKeyEntry entry, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(entry.TokenEndpoint))
        {
            throw new InvalidOperationException($"predefined key '{_id}' has no token endpoint configured.");
        }

        // The current refresh token: a rotated value persisted in the store wins over the entry's original.
        var refreshToken = entry.RefreshToken;
        if (entry.Kind == PredefinedKeyKind.RefreshToken)
        {
            var stored = await _tokenStore.GetAsync(ProviderId, ct).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(stored?.RefreshToken))
            {
                refreshToken = stored.RefreshToken;
            }
        }

        var form = new Dictionary<string, string>();
        if (!string.IsNullOrEmpty(entry.ClientId))
        {
            form["client_id"] = entry.ClientId;
        }

        if (!string.IsNullOrEmpty(entry.ClientSecret))
        {
            form["client_secret"] = entry.ClientSecret;
        }

        if (entry.Scopes.Count > 0)
        {
            form["scope"] = string.Join(' ', entry.Scopes);
        }

        if (entry.Kind == PredefinedKeyKind.RefreshToken)
        {
            form["grant_type"] = "refresh_token";
            form["refresh_token"] = refreshToken ?? string.Empty;
        }
        else
        {
            form["grant_type"] = "client_credentials";
        }

        OAuthTokenEndpointResponse resp;
        try
        {
            resp = await _endpoint.PostAsync(entry.TokenEndpoint, form, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Transient transport failure — prompt/retry, but do NOT invalidate (the credential may be fine).
            SetStatus(OAuthSignInState.Failed, "token_endpoint_unreachable");
            throw new InvalidOperationException($"predefined key '{_id}': token endpoint unreachable.", ex);
        }

        if (string.IsNullOrEmpty(resp.AccessToken))
        {
            if (IsCredentialRejection(resp.Error))
            {
                // Definitive rejection: mark invalid so we prompt for an update instead of re-sending a bad credential.
                _invalidated = true;
                SetStatus(OAuthSignInState.Failed, resp.Error);
                _logger.LogWarning("Predefined key {ProviderId} rejected by token endpoint ({Error}); marked invalid.", ProviderId, resp.Error);
                throw new InvalidOperationException($"predefined key '{_id}' rejected: {resp.Error}");
            }

            SetStatus(OAuthSignInState.Failed, resp.Error ?? "mint_failed");
            throw new InvalidOperationException($"predefined key '{_id}' token mint failed: {resp.Error ?? "no access_token"}");
        }

        var expiry = resp.ExpiresIn > 0 ? DateTimeOffset.UtcNow.AddSeconds(resp.ExpiresIn) : NonExpiring;
        var token = new OAuthAccessToken(resp.AccessToken, expiry);
        _cachedToken = token;

        var persistedRefresh = entry.Kind == PredefinedKeyKind.RefreshToken
            ? (string.IsNullOrEmpty(resp.RefreshToken) ? refreshToken : resp.RefreshToken)
            : null;
        await _tokenStore.SaveAsync(
            new OAuthTokenRecord(ProviderId, null, persistedRefresh ?? string.Empty, token.Value, expiry, entry.Scopes),
            ct).ConfigureAwait(false);

        SetStatus(OAuthSignInState.SignedIn, null);
        _logger.LogInformation("Predefined key {ProviderId} minted an access token (expires {ExpiresAt:o}).", ProviderId, expiry);
        return token;
    }

    /// <summary>Best-effort load of a previously-minted, still-valid access token from the store.</summary>
    private async Task<OAuthAccessToken?> LoadCachedFromStoreAsync(CancellationToken ct)
    {
        var record = await _tokenStore.GetAsync(ProviderId, ct).ConfigureAwait(false);
        if (record?.AccessToken is { Length: > 0 } access && record.AccessTokenExpiresAtUtc is { } expiry)
        {
            return new OAuthAccessToken(access, expiry);
        }

        return null;
    }

    /// <inheritdoc />
    public async Task HydrateFromStoreAsync(CancellationToken ct = default)
    {
        if (_entry.Kind == PredefinedKeyKind.CustomHeaders)
        {
            return;
        }

        await _refreshGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _cachedToken = await LoadCachedFromStoreAsync(ct).ConfigureAwait(false);
            if (_cachedToken is not null)
            {
                SetStatus(OAuthSignInState.SignedIn, null);
            }
        }
        finally
        {
            _ = _refreshGate.Release();
        }
    }

    /// <inheritdoc />
    public Task<SignInChallenge> BeginSignInAsync(CancellationToken ct = default) =>
        throw new NotSupportedException("Predefined egress keys are configured directly, not via an interactive sign-in.");

    /// <inheritdoc />
    public async Task SignOutAsync(CancellationToken ct = default)
    {
        await _refreshGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _cachedToken = null;
            await _tokenStore.RemoveAsync(ProviderId, ct).ConfigureAwait(false);
            SetStatus(OAuthSignInState.NotStarted, null);
        }
        finally
        {
            _ = _refreshGate.Release();
        }
    }

    private static bool IsCredentialRejection(string? error) =>
        !string.IsNullOrEmpty(error) && CredentialRejectionErrors.Contains(error);

    private void SetStatus(OAuthSignInState state, string? error) =>
        _status = new OAuthStatus(state, null, _entry.Scopes, _cachedToken?.ExpiresAtUtc, error);
}
