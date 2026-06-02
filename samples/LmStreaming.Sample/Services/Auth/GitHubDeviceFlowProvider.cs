using System.Net.Http.Headers;
using System.Text.Json;

namespace LmStreaming.Sample.Services.Auth;

/// <summary>
/// GitHub device-code OAuth provider.
/// </summary>
/// <remarks>
/// <para>
/// GitHub's device-flow endpoints respond with form-encoded bodies by default; this provider sends
/// <c>Accept: application/json</c> so the base class can parse JSON.
/// </para>
/// <para>
/// Refresh tokens are only issued by a <em>GitHub App</em> with "Expire user authorization tokens"
/// enabled. Classic OAuth Apps issue a non-expiring <c>access_token</c> with no <c>refresh_token</c>
/// and no <c>expires_in</c>. Both are handled: when no refresh token is returned the base stores an
/// empty refresh token with a far-future expiry, and <c>GetAccessTokenAsync</c> simply returns the
/// stored access token (there is nothing to refresh).
/// </para>
/// </remarks>
public sealed class GitHubDeviceFlowProvider : DeviceFlowOAuthProviderBase
{
    /// <summary>GitHub requires a User-Agent on API calls; identify this sample app.</summary>
    private const string UserAgent = "LmStreaming.Sample";

    private readonly GitHubAuthOptions _options;

    /// <summary>Creates the GitHub device-code provider.</summary>
    /// <param name="options">GitHub client id + scopes.</param>
    /// <param name="store">Token persistence.</param>
    /// <param name="http">HTTP client for the OAuth + API calls.</param>
    /// <param name="logger">Logger; token material is never written to it.</param>
    public GitHubDeviceFlowProvider(
        GitHubAuthOptions options,
        IOAuthTokenStore store,
        HttpClient http,
        ILogger<GitHubDeviceFlowProvider> logger)
        : base(store, http, logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <inheritdoc />
    public override string ProviderId => "github";

    /// <inheritdoc />
    protected override string? ClientId => _options.ClientId;

    /// <inheritdoc />
    protected override IReadOnlyList<string> Scopes => _options.Scopes;

    /// <inheritdoc />
    protected override string DeviceCodeEndpoint => "https://github.com/login/device/code";

    /// <inheritdoc />
    protected override string TokenEndpoint => "https://github.com/login/oauth/access_token";

    /// <summary>GitHub returns form-encoded bodies unless we ask for JSON.</summary>
    protected override bool SendJsonAcceptHeader => true;

    /// <summary>
    /// Best-effort resolution of the GitHub login via <c>GET https://api.github.com/user</c>.
    /// Returns the <c>login</c> field, or <c>null</c> on any failure (the base treats this as optional).
    /// </summary>
    protected override async Task<string?> ResolveAccountAsync(string accessToken, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/user");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.UserAgent.ParseAdd(UserAgent);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            Logger.LogDebug("GitHub /user lookup returned HTTP {Status}; account name unavailable.", (int)response.StatusCode);
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        return doc.RootElement.TryGetProperty("login", out var login) ? login.GetString() : null;
    }
}
