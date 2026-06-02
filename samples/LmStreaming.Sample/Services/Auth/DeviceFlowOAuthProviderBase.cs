using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LmStreaming.Sample.Services.Auth;

/// <summary>
/// Shared implementation of the OAuth 2.0 <em>device authorization grant</em>
/// (<see href="https://www.rfc-editor.org/rfc/rfc8628">RFC 8628</see>) plus the refresh-token
/// grant. Concrete providers (GitHub, Entra/ADO) supply only the endpoints, client id, scopes and
/// the best-effort account-resolution hook; this base owns the device-code request, the background
/// polling loop, token persistence and on-demand refresh.
/// </summary>
/// <remarks>
/// SECURITY: access tokens and refresh tokens are secrets. This type never logs token material —
/// only provider id, sign-in state, expiry, HTTP status and OAuth error codes.
/// </remarks>
public abstract partial class DeviceFlowOAuthProviderBase : IOAuthTokenProvider
{
    /// <summary>The device-code grant type used when polling the token endpoint.</summary>
    private const string DeviceCodeGrantType = "urn:ietf:params:oauth:grant-type:device_code";

    /// <summary>The refresh-token grant type used to renew an access token.</summary>
    private const string RefreshTokenGrantType = "refresh_token";

    /// <summary>Refresh the access token this many seconds before it actually expires.</summary>
    private static readonly TimeSpan ExpirySkew = TimeSpan.FromSeconds(120);

    /// <summary>Fallback device-code lifetime when the server omits <c>expires_in</c>.</summary>
    private static readonly TimeSpan DefaultDeviceCodeLifetime = TimeSpan.FromSeconds(900);

    /// <summary>Used as the stored expiry for non-expiring tokens (e.g. classic GitHub OAuth Apps). Computed at use so it is always ~100 years from issuance, not from process start.</summary>
    private static DateTimeOffset NonExpiringSentinel => DateTimeOffset.UtcNow.AddYears(100);

    private readonly IOAuthTokenStore _store;
    private readonly object _statusGate = new();
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    private OAuthStatus _status = new(OAuthSignInState.NotStarted, Account: null, Scopes: [], ExpiresAtUtc: null, Error: null);
    private CancellationTokenSource? _pollCts;
    private Task? _pollTask;

    /// <summary>The HTTP client used for every OAuth call (injected for testability).</summary>
    protected HttpClient Http { get; }

    /// <summary>Logger scoped to the concrete provider.</summary>
    protected ILogger Logger { get; }

    /// <summary>Creates the base provider.</summary>
    /// <param name="store">Persistence for the refresh/access token record.</param>
    /// <param name="http">HTTP client used for all OAuth endpoints.</param>
    /// <param name="logger">Logger; token material is never written to it.</param>
    protected DeviceFlowOAuthProviderBase(IOAuthTokenStore store, HttpClient http, ILogger logger)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        Http = http ?? throw new ArgumentNullException(nameof(http));
        Logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public abstract string ProviderId { get; }

    /// <summary>Client id of the registered app, or null/empty when the provider is disabled.</summary>
    protected abstract string? ClientId { get; }

    /// <summary>Scopes requested during sign-in. Space-joined when sent to the server.</summary>
    protected abstract IReadOnlyList<string> Scopes { get; }

    /// <summary>Absolute URL of the device-authorization (<c>device/code</c>) endpoint.</summary>
    protected abstract string DeviceCodeEndpoint { get; }

    /// <summary>Absolute URL of the token endpoint (device-code + refresh grants).</summary>
    protected abstract string TokenEndpoint { get; }

    /// <summary>
    /// When true the request carries <c>Accept: application/json</c> so the server returns a JSON
    /// body (GitHub defaults to form-encoded responses without it). Entra always returns JSON.
    /// </summary>
    protected virtual bool SendJsonAcceptHeader => false;

    /// <summary>
    /// Best-effort resolution of a human-readable account/display name from a freshly issued access
    /// token. Implementations must swallow failures and return <c>null</c> rather than throw.
    /// </summary>
    /// <param name="accessToken">The newly issued access token (secret — do not log).</param>
    /// <param name="ct">Cancellation token.</param>
    protected abstract Task<string?> ResolveAccountAsync(string accessToken, CancellationToken ct);

    /// <inheritdoc />
    public OAuthStatus Status
    {
        get
        {
            lock (_statusGate)
            {
                return _status;
            }
        }
    }

    /// <inheritdoc />
    public async Task<DeviceCodeChallenge> BeginSignInAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(ClientId))
        {
            throw new InvalidOperationException($"OAuth provider '{ProviderId}' is not configured (missing ClientId).");
        }

        // Cancel any in-flight poll from a previous BeginSignIn before starting a new one.
        await CancelPollAsync().ConfigureAwait(false);

        var device = await RequestDeviceCodeAsync(ct).ConfigureAwait(false);

        SetStatus(new OAuthStatus(OAuthSignInState.Pending, Account: null, Scopes, ExpiresAtUtc: null, Error: null));
        Logger.LogInformation("OAuth device-code flow started for provider {ProviderId} (expires in {ExpiresIn}s).", ProviderId, device.ExpiresIn);

        // Fire-and-forget background polling; BeginSignIn must not block on user authorization.
        var cts = new CancellationTokenSource();
        _pollCts = cts;
        _pollTask = Task.Run(() => PollForTokenAsync(device, cts.Token), CancellationToken.None);

        return new DeviceCodeChallenge(
            device.UserCode,
            device.VerificationUri,
            device.VerificationUriComplete,
            device.ExpiresIn,
            Math.Max(1, device.Interval));
    }

    /// <inheritdoc />
    public async Task SignOutAsync(CancellationToken ct = default)
    {
        await CancelPollAsync().ConfigureAwait(false);
        await _store.RemoveAsync(ProviderId, ct).ConfigureAwait(false);
        SetStatus(new OAuthStatus(OAuthSignInState.NotStarted, Account: null, Scopes: [], ExpiresAtUtc: null, Error: null));
        Logger.LogInformation("Signed out of OAuth provider {ProviderId}.", ProviderId);
    }

    /// <inheritdoc />
    public async Task<OAuthAccessToken> GetAccessTokenAsync(IReadOnlyList<string>? scopes = null, CancellationToken ct = default)
    {
        var record = await _store.GetAsync(ProviderId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"OAuth provider '{ProviderId}' is not signed in.");

        if (TryGetValidAccessToken(record, out var current))
        {
            return current;
        }

        return await RefreshAccessTokenAsync(record, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns the stored access token when it is present and either non-refreshable (so the stored
    /// value is the only option) or comfortably outside the expiry skew.
    /// </summary>
    private static bool TryGetValidAccessToken(OAuthTokenRecord record, out OAuthAccessToken token)
    {
        token = default!;
        if (string.IsNullOrEmpty(record.AccessToken))
        {
            return false;
        }

        var expiry = record.AccessTokenExpiresAtUtc ?? NonExpiringSentinel;
        var canRefresh = !string.IsNullOrEmpty(record.RefreshToken);

        // A still-valid token, or a token we have no way to refresh (return what we have).
        if (!canRefresh || expiry - ExpirySkew > DateTimeOffset.UtcNow)
        {
            token = new OAuthAccessToken(record.AccessToken, expiry);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Refreshes the access token via the refresh-token grant. Serialized by a semaphore so
    /// concurrent callers don't stampede the token endpoint; the first to acquire performs the
    /// refresh and the rest observe the freshly persisted token.
    /// </summary>
    private async Task<OAuthAccessToken> RefreshAccessTokenAsync(OAuthTokenRecord record, CancellationToken ct)
    {
        await _refreshGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Re-read after acquiring the gate: another caller may have already refreshed.
            var latest = await _store.GetAsync(ProviderId, ct).ConfigureAwait(false) ?? record;
            if (TryGetValidAccessToken(latest, out var fresh))
            {
                return fresh;
            }

            if (string.IsNullOrEmpty(latest.RefreshToken))
            {
                SetFailed("no refresh token available");
                throw new InvalidOperationException($"OAuth provider '{ProviderId}' has no refresh token; sign in again.");
            }

            var form = new Dictionary<string, string>
            {
                ["grant_type"] = RefreshTokenGrantType,
                ["client_id"] = ClientId!,
                ["refresh_token"] = latest.RefreshToken,
            };
            AddRefreshScope(form);

            var response = await PostTokenAsync(form, ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(response.AccessToken))
            {
                SetFailed(response.Error ?? "refresh failed");
                throw new InvalidOperationException(
                    $"OAuth provider '{ProviderId}' token refresh failed: {response.Error ?? "no access_token returned"}.");
            }

            var updated = ApplyTokenResponse(latest, response);
            await _store.SaveAsync(updated, ct).ConfigureAwait(false);
            SetStatus(new OAuthStatus(OAuthSignInState.SignedIn, updated.Account, updated.Scopes, updated.AccessTokenExpiresAtUtc, Error: null));
            Logger.LogInformation("Refreshed access token for provider {ProviderId} (expires {ExpiresAt:o}).", ProviderId, updated.AccessTokenExpiresAtUtc);

            return new OAuthAccessToken(updated.AccessToken!, updated.AccessTokenExpiresAtUtc ?? NonExpiringSentinel);
        }
        finally
        {
            _ = _refreshGate.Release();
        }
    }

    /// <summary>
    /// Background loop that polls the token endpoint until the user authorizes, the device code
    /// expires, the user denies, or the flow is cancelled. Never throws into the runtime: terminal
    /// problems are surfaced as a <see cref="OAuthSignInState.Failed"/> status.
    /// </summary>
    private async Task PollForTokenAsync(DeviceCodeResult device, CancellationToken ct)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(1, device.Interval));
        var lifetime = TimeSpan.FromSeconds(device.ExpiresIn <= 0 ? (int)DefaultDeviceCodeLifetime.TotalSeconds : device.ExpiresIn);
        var elapsed = Stopwatch.StartNew();

        try
        {
            while (elapsed.Elapsed < lifetime)
            {
                await Task.Delay(interval, ct).ConfigureAwait(false);

                var form = new Dictionary<string, string>
                {
                    ["grant_type"] = DeviceCodeGrantType,
                    ["client_id"] = ClientId!,
                    ["device_code"] = device.DeviceCode,
                };

                var response = await PostTokenAsync(form, ct).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(response.AccessToken))
                {
                    await PersistNewSignInAsync(response, ct).ConfigureAwait(false);
                    return;
                }

                switch (response.Error)
                {
                    case "authorization_pending":
                        break;
                    case "slow_down":
                        interval += TimeSpan.FromSeconds(5);
                        Logger.LogDebug("Provider {ProviderId} device-code polling backing off to {Interval}s.", ProviderId, interval.TotalSeconds);
                        break;
                    case "access_denied":
                    case "authorization_declined":
                    case "expired_token":
                    case "bad_verification_code":
                        SetFailed(response.Error);
                        Logger.LogWarning("Provider {ProviderId} device-code flow failed: {Error}.", ProviderId, response.Error);
                        return;
                    default:
                        if (!string.IsNullOrEmpty(response.Error))
                        {
                            SetFailed(response.Error);
                            Logger.LogWarning("Provider {ProviderId} device-code flow failed: {Error}.", ProviderId, response.Error);
                            return;
                        }

                        break;
                }
            }

            SetFailed("timed_out");
            Logger.LogWarning("Provider {ProviderId} device-code flow timed out before authorization.", ProviderId);
        }
        catch (OperationCanceledException)
        {
            // Cancelled by SignOut / a subsequent BeginSignIn — leave the new status alone.
            Logger.LogDebug("Provider {ProviderId} device-code polling cancelled.", ProviderId);
        }
        catch (Exception ex)
        {
            SetFailed("polling_error");
            Logger.LogError(ex, "Provider {ProviderId} device-code polling errored.", ProviderId);
        }
    }

    /// <summary>Persists a brand-new sign-in (token + best-effort account) and marks the provider signed in.</summary>
    private async Task PersistNewSignInAsync(TokenResponse response, CancellationToken ct)
    {
        var account = await SafeResolveAccountAsync(response.AccessToken!, ct).ConfigureAwait(false);
        var record = new OAuthTokenRecord(
            ProviderId,
            account,
            response.RefreshToken ?? string.Empty,
            response.AccessToken,
            ComputeExpiry(response),
            Scopes);

        await _store.SaveAsync(record, ct).ConfigureAwait(false);
        SetStatus(new OAuthStatus(OAuthSignInState.SignedIn, account, Scopes, record.AccessTokenExpiresAtUtc, Error: null));
        Logger.LogInformation("Signed in to provider {ProviderId} as {Account} (expires {ExpiresAt:o}).", ProviderId, account ?? "<unknown>", record.AccessTokenExpiresAtUtc);
    }

    /// <summary>Wraps <see cref="ResolveAccountAsync"/> so a faulty implementation cannot break sign-in.</summary>
    private async Task<string?> SafeResolveAccountAsync(string accessToken, CancellationToken ct)
    {
        try
        {
            return await ResolveAccountAsync(accessToken, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Provider {ProviderId} could not resolve account name (best-effort).", ProviderId);
            return null;
        }
    }

    /// <summary>Merges a refresh-grant response onto the existing record, preserving the old refresh token when not rotated.</summary>
    private OAuthTokenRecord ApplyTokenResponse(OAuthTokenRecord existing, TokenResponse response) =>
        existing with
        {
            AccessToken = response.AccessToken,
            AccessTokenExpiresAtUtc = ComputeExpiry(response),
            // Entra/GitHub may rotate the refresh token; keep the previous one when none is returned.
            RefreshToken = string.IsNullOrEmpty(response.RefreshToken) ? existing.RefreshToken : response.RefreshToken,
        };

    /// <summary>Computes the absolute access-token expiry; non-expiring tokens use a far-future sentinel.</summary>
    private static DateTimeOffset ComputeExpiry(TokenResponse response) =>
        response.ExpiresIn > 0 ? DateTimeOffset.UtcNow.AddSeconds(response.ExpiresIn) : NonExpiringSentinel;

    /// <summary>POSTs the device-authorization request and parses the challenge.</summary>
    private async Task<DeviceCodeResult> RequestDeviceCodeAsync(CancellationToken ct)
    {
        var form = new Dictionary<string, string>
        {
            ["client_id"] = ClientId!,
            ["scope"] = string.Join(' ', Scopes),
        };

        using var request = BuildFormRequest(DeviceCodeEndpoint, form);
        using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
        var json = await ReadJsonOrThrowAsync(response, "device-code", ct).ConfigureAwait(false);

        var result = json.Deserialize(DeviceFlowJson.Default.DeviceCodeResult);
        if (result is null || string.IsNullOrEmpty(result.DeviceCode))
        {
            throw new InvalidOperationException($"OAuth provider '{ProviderId}' device-code endpoint returned an unusable response.");
        }

        return result;
    }

    /// <summary>POSTs to the token endpoint and parses the (possibly error) token response.</summary>
    private async Task<TokenResponse> PostTokenAsync(Dictionary<string, string> form, CancellationToken ct)
    {
        using var request = BuildFormRequest(TokenEndpoint, form);
        using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);

        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(body))
        {
            Logger.LogWarning("Provider {ProviderId} token endpoint returned empty body (HTTP {Status}).", ProviderId, (int)response.StatusCode);
            return new TokenResponse { Error = "empty_response" };
        }

        try
        {
            var parsed = JsonSerializer.Deserialize(body, DeviceFlowJson.Default.TokenResponse);
            return parsed ?? new TokenResponse { Error = "empty_response" };
        }
        catch (JsonException ex)
        {
            // The token endpoint always speaks JSON for our providers; a parse failure is unexpected.
            Logger.LogWarning(ex, "Provider {ProviderId} token endpoint returned unparseable body (HTTP {Status}).", ProviderId, (int)response.StatusCode);
            return new TokenResponse { Error = "unparseable_response" };
        }
    }

    /// <summary>Builds a form-urlencoded POST, adding the JSON Accept header for providers that need it.</summary>
    private HttpRequestMessage BuildFormRequest(string endpoint, Dictionary<string, string> form)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new FormUrlEncodedContent(form),
        };
        if (SendJsonAcceptHeader)
        {
            request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
        }

        return request;
    }

    /// <summary>Ensures a successful HTTP status and returns the parsed JSON document, logging (without secrets) on failure.</summary>
    private async Task<JsonElement> ReadJsonOrThrowAsync(HttpResponseMessage response, string what, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var error = TryExtractError(body);
            Logger.LogWarning("Provider {ProviderId} {What} request failed: HTTP {Status} {Error}.", ProviderId, what, (int)response.StatusCode, error ?? "<no error>");
            throw new InvalidOperationException($"OAuth provider '{ProviderId}' {what} request failed (HTTP {(int)response.StatusCode}{(error is null ? "" : $": {error}")}).");
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            Logger.LogWarning(ex, "Provider {ProviderId} {What} response was not valid JSON.", ProviderId, what);
            throw new InvalidOperationException($"OAuth provider '{ProviderId}' {what} response was not valid JSON.", ex);
        }
    }

    /// <summary>Best-effort extraction of the OAuth <c>error</c> field from an error body for logging.</summary>
    private static string? TryExtractError(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("error", out var error) ? error.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Hook for providers whose refresh grant requires a <c>scope</c> field (Entra). No-op by default.</summary>
    protected virtual void AddRefreshScope(Dictionary<string, string> form)
    {
    }

    /// <summary>Atomically swaps the current status.</summary>
    private void SetStatus(OAuthStatus status)
    {
        lock (_statusGate)
        {
            _status = status;
        }
    }

    /// <summary>Marks the provider failed, preserving the current account/expiry for context.</summary>
    private void SetFailed(string error)
    {
        lock (_statusGate)
        {
            _status = _status with { State = OAuthSignInState.Failed, Error = error };
        }
    }

    /// <summary>Cancels and disposes the current polling task/CTS, awaiting clean shutdown.</summary>
    private async Task CancelPollAsync()
    {
        var cts = _pollCts;
        var task = _pollTask;
        _pollCts = null;
        _pollTask = null;

        if (cts is null)
        {
            return;
        }

        try
        {
            await cts.CancelAsync().ConfigureAwait(false);
            if (task is not null)
            {
                await task.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when the poll loop observes cancellation.
        }
        finally
        {
            cts.Dispose();
        }
    }

    /// <summary>The device-authorization response (RFC 8628 §3.2).</summary>
    private sealed record DeviceCodeResult
    {
        [JsonPropertyName("device_code")]
        public string DeviceCode { get; init; } = string.Empty;

        [JsonPropertyName("user_code")]
        public string UserCode { get; init; } = string.Empty;

        [JsonPropertyName("verification_uri")]
        public string VerificationUri { get; init; } = string.Empty;

        [JsonPropertyName("verification_uri_complete")]
        public string? VerificationUriComplete { get; init; }

        [JsonPropertyName("expires_in")]
        public int ExpiresIn { get; init; }

        [JsonPropertyName("interval")]
        public int Interval { get; init; }
    }

    /// <summary>The token-endpoint response (device-code success/error and refresh success/error).</summary>
    private sealed record TokenResponse
    {
        [JsonPropertyName("access_token")]
        public string? AccessToken { get; init; }

        [JsonPropertyName("refresh_token")]
        public string? RefreshToken { get; init; }

        [JsonPropertyName("expires_in")]
        public int ExpiresIn { get; init; }

        [JsonPropertyName("token_type")]
        public string? TokenType { get; init; }

        [JsonPropertyName("scope")]
        public string? Scope { get; init; }

        [JsonPropertyName("error")]
        public string? Error { get; init; }
    }

    /// <summary>Source-generated JSON context for the private DTOs (trim/AOT friendly, zero reflection warnings).</summary>
    [JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
    [JsonSerializable(typeof(DeviceCodeResult))]
    [JsonSerializable(typeof(TokenResponse))]
    private sealed partial class DeviceFlowJson : JsonSerializerContext
    {
    }
}
