using LmStreaming.Sample.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace LmStreaming.Sample.Controllers;

/// <summary>
/// What the SPA needs to decide whether to sign in, and against which directory.
/// </summary>
/// <param name="Enforce">Whether an authenticated principal is required for <c>/api</c> routes.</param>
/// <param name="ClientId">Entra app registration the SPA authenticates against. Null when unconfigured.</param>
/// <param name="Authority">Entra authority URL the SPA acquires tokens from. Null when unconfigured.</param>
/// <param name="Scopes">Scopes the SPA must request for its access token.</param>
public sealed record IdentityConfigResponse(
    bool Enforce,
    string? ClientId,
    string? Authority,
    IReadOnlyList<string> Scopes);

/// <summary>
/// Serves the SPA's identity configuration. Anonymous by necessity: the client must read this
/// before it can sign in, so requiring a principal here would be a deadlock.
/// </summary>
/// <remarks>
/// Everything returned is public by construction - a client id and an authority URL are values the
/// browser sends to Entra in a URL bar. No secret is served here; the app registration is a public
/// SPA client, which is why it has no secret to leak in the first place.
/// </remarks>
[ApiController]
[Route("api/identity")]
public sealed class IdentityController : ControllerBase
{
    /// <summary>Configuration section the Entra app registration binds from.</summary>
    public const string AzureAdSectionName = "AzureAd";

    private readonly IOptions<IdentityOptions> _options;
    private readonly IConfiguration _configuration;

    /// <summary>Creates the controller.</summary>
    /// <param name="options">Identity configuration.</param>
    /// <param name="configuration">Root configuration, read for the <c>AzureAd</c> section.</param>
    public IdentityController(IOptions<IdentityOptions> options, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(configuration);

        _options = options;
        _configuration = configuration;
    }

    /// <summary>Returns the SPA's identity configuration.</summary>
    [HttpGet("config")]
    public ActionResult<IdentityConfigResponse> GetConfig()
    {
        var azureAd = _configuration.GetSection(AzureAdSectionName);
        var clientId = azureAd["ClientId"];
        var tenantId = azureAd["TenantId"];

        // "organizations" rather than a single directory: the deployment serves many customers, and
        // which tenant a user belongs to is discovered from their token's `tid`, not pinned here.
        var authority = string.IsNullOrWhiteSpace(clientId)
            ? null
            : $"{(azureAd["Instance"] ?? "https://login.microsoftonline.com/").TrimEnd('/')}/"
                + $"{(string.IsNullOrWhiteSpace(tenantId) ? "organizations" : tenantId)}";

        var scopes = string.IsNullOrWhiteSpace(clientId)
            ? []
            : new[] { $"api://{clientId}/access_as_user" };

        return Ok(new IdentityConfigResponse(_options.Value.Enforce, clientId, authority, scopes));
    }
}
