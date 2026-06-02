namespace LmStreaming.Sample.Services.Auth;

/// <summary>
/// Azure DevOps device-code OAuth provider backed by the Microsoft Entra (Azure AD) v2.0 endpoints.
/// </summary>
/// <remarks>
/// <para>
/// Entra's device-code and token endpoints accept <c>application/x-www-form-urlencoded</c> requests
/// and return JSON, so no <c>Accept</c> override is needed. A <c>refresh_token</c> is only issued
/// when the requested scopes include <c>offline_access</c> (carried in <see cref="AdoAuthOptions.Scopes"/>),
/// and the refresh grant must repeat the <c>scope</c> parameter — see <see cref="AddRefreshScope"/>.
/// </para>
/// <para>
/// Entra does not return <c>verification_uri_complete</c> for the device-code flow, so the challenge
/// surfaces only the <c>verification_uri</c> + <c>user_code</c>.
/// </para>
/// </remarks>
public sealed class AdoDeviceFlowProvider : DeviceFlowOAuthProviderBase
{
    private readonly AdoAuthOptions _options;

    /// <summary>Creates the Azure DevOps (Entra) device-code provider.</summary>
    /// <param name="options">Entra client id, tenant + scopes (include <c>offline_access</c>).</param>
    /// <param name="store">Token persistence.</param>
    /// <param name="http">HTTP client for the OAuth calls.</param>
    /// <param name="logger">Logger; token material is never written to it.</param>
    public AdoDeviceFlowProvider(
        AdoAuthOptions options,
        IOAuthTokenStore store,
        HttpClient http,
        ILogger<AdoDeviceFlowProvider> logger)
        : base(store, http, logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <inheritdoc />
    public override string ProviderId => "ado";

    /// <inheritdoc />
    protected override string? ClientId => _options.ClientId;

    /// <inheritdoc />
    protected override IReadOnlyList<string> Scopes => _options.Scopes;

    /// <inheritdoc />
    protected override string DeviceCodeEndpoint =>
        $"https://login.microsoftonline.com/{_options.TenantId}/oauth2/v2.0/devicecode";

    /// <inheritdoc />
    protected override string TokenEndpoint =>
        $"https://login.microsoftonline.com/{_options.TenantId}/oauth2/v2.0/token";

    /// <summary>Entra's refresh grant requires the original space-joined scopes.</summary>
    protected override void AddRefreshScope(Dictionary<string, string> form) =>
        form["scope"] = string.Join(' ', Scopes);

    /// <summary>
    /// Account resolution is best-effort and not required for Azure DevOps; the access token is an
    /// opaque Entra token that must not be parsed by clients, so we return <c>null</c>.
    /// </summary>
    protected override Task<string?> ResolveAccountAsync(string accessToken, CancellationToken ct) =>
        Task.FromResult<string?>(null);
}
