using AchieveAi.LmDotnetTools.LmAgentInfra.Auth;
using Microsoft.AspNetCore.Mvc;

namespace AchieveAi.LmDotnetTools.LmAgentInfra.Controllers;

/// <summary>
/// CRUD for predefined egress keys (issue #210). Entries are created at runtime here and persisted by
/// the <see cref="PredefinedKeyRegistry"/>; they drive the sandbox <c>auth_providers</c>/<c>network</c>
/// blocks on the NEXT session create.
/// </summary>
/// <remarks>
/// SECURITY / THREAT MODEL: like the rest of this local-dev sample (the OAuth sign-in controllers, the
/// workspaces / providers / conversations endpoints — all unauthenticated) this management API assumes a
/// trusted local user on a localhost-bound host; it is not a hardened, network-exposed service. It is
/// deliberately NOT reachable from the sandbox: default-deny egress opens no allow-rule for the app's own
/// host, so a sandboxed agent cannot call it. GET masks all secret values and nothing here logs them.
/// An enforced management-auth boundary for the sample as a whole is out of scope for #210.
/// </remarks>
[ApiController]
[Route("api/auth/egress-keys")]
public sealed class EgressKeysController(PredefinedKeyRegistry registry, ILogger<EgressKeysController> logger)
    : ControllerBase
{
    /// <summary>Lists the configured entries with all secret material masked/omitted.</summary>
    [HttpGet]
    public IActionResult List() => Ok(registry.Entries.Select(ToView).ToList());

    /// <summary>Creates a new entry (blank id) or updates an existing one (secrets left blank are preserved).</summary>
    [HttpPost]
    public async Task<IActionResult> Upsert([FromBody] EgressKeyRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Host: SSRF-safe pattern + no collision with a managed OAuth provider's egress scope.
        var hostError = EgressHostMatcher.ValidateHostPattern(request.Host);
        if (hostError is not null)
        {
            return BadRequest(new { error = hostError });
        }

        if (OAuthProviderHosts.CollidesWithManagedHost(request.Host))
        {
            return BadRequest(new { error = "Host collides with a managed OAuth provider (GitHub / Azure DevOps / M365)." });
        }

        if (!TryParseKind(request.Kind, out var kind))
        {
            return BadRequest(new { error = $"Unknown kind '{request.Kind}'. Expected custom-headers, refresh-token, or client-credentials." });
        }

        var existing = string.IsNullOrEmpty(request.Id) ? null : registry.Find(request.Id);
        if (!string.IsNullOrEmpty(request.Id) && existing is null)
        {
            return NotFound(new { error = $"No egress key with id '{request.Id}'." });
        }

        var id = existing?.Id ?? Guid.NewGuid().ToString("N");

        var (entry, error) = kind == PredefinedKeyKind.CustomHeaders
            ? BuildCustomHeadersEntry(id, request.Host, request, existing)
            : BuildOAuthEntry(id, request.Host, kind, request, existing);

        if (error is not null)
        {
            return BadRequest(new { error });
        }

        await registry.UpsertAsync(entry!, ct).ConfigureAwait(false);
        logger.LogInformation("Egress key {ProviderId} {Action} (host {Host}, kind {Kind}).",
            $"{PredefinedKeyRegistry.ProviderIdPrefix}{id}", existing is null ? "created" : "updated", request.Host, request.Kind);
        return Ok(ToView(entry!));
    }

    /// <summary>Deletes an entry and its persisted secret. 404 when the id is unknown.</summary>
    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id, CancellationToken ct = default)
    {
        var removed = await registry.RemoveAsync(id, ct).ConfigureAwait(false);
        return removed ? NoContent() : NotFound();
    }

    private static (PredefinedKeyEntry? entry, string? error) BuildCustomHeadersEntry(
        string id, string host, EgressKeyRequest request, PredefinedKeyEntry? existing)
    {
        // Edit that omits the header list entirely (e.g. changing only the host) preserves the stored headers.
        if (request.Headers is not { Count: > 0 })
        {
            if (existing is null || existing.Headers.Count == 0)
            {
                return (null, "At least one header is required.");
            }

            return (existing with { Id = id, Host = host }, null);
        }

        // GET masks header values, so the edit form round-trips each header NAME with a blank value.
        // Per-header preserve: a blank value on an EXISTING header name keeps the stored secret; a
        // blank value on a NEW header name is an error.
        var resolved = new List<PredefinedHeader>(request.Headers.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in request.Headers)
        {
            var nameError = EgressHostMatcher.ValidateHeaderName(header.Name);
            if (nameError is not null)
            {
                return (null, nameError);
            }

            if (!seen.Add(header.Name))
            {
                return (null, $"Duplicate header '{header.Name}'.");
            }

            var value = header.Value;
            if (string.IsNullOrEmpty(value))
            {
                var stored = existing?.Headers.FirstOrDefault(
                    h => string.Equals(h.Name, header.Name, StringComparison.OrdinalIgnoreCase));
                value = stored?.Value ?? string.Empty;
            }

            var valueError = EgressHostMatcher.ValidateHeaderValue(value);
            if (valueError is not null)
            {
                return (null, valueError);
            }

            resolved.Add(new PredefinedHeader(header.Name, value));
        }

        return (new PredefinedKeyEntry { Id = id, Host = host, Kind = PredefinedKeyKind.CustomHeaders, Headers = resolved }, null);
    }

    private static (PredefinedKeyEntry? entry, string? error) BuildOAuthEntry(
        string id, string host, PredefinedKeyKind kind, EgressKeyRequest request, PredefinedKeyEntry? existing)
    {
        var headerName = Coalesce(request.HeaderName, existing?.HeaderName) ?? "Authorization";
        var headerError = EgressHostMatcher.ValidateHeaderName(headerName);
        if (headerError is not null)
        {
            return (null, headerError);
        }

        var tokenEndpoint = Coalesce(request.TokenEndpoint, existing?.TokenEndpoint);
        if (!Uri.TryCreate(tokenEndpoint, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            return (null, "Token endpoint must be an absolute https URL.");
        }

        var clientId = Coalesce(request.ClientId, existing?.ClientId);
        if (string.IsNullOrWhiteSpace(clientId))
        {
            return (null, "Client id is required.");
        }

        // Secrets: a blank value on an update preserves the stored one.
        var clientSecret = Coalesce(request.ClientSecret, existing?.ClientSecret);
        var refreshToken = Coalesce(request.RefreshToken, existing?.RefreshToken);

        if (kind == PredefinedKeyKind.ClientCredentials && string.IsNullOrWhiteSpace(clientSecret))
        {
            return (null, "Client secret is required for client-credentials.");
        }

        if (kind == PredefinedKeyKind.RefreshToken && string.IsNullOrWhiteSpace(refreshToken))
        {
            return (null, "Refresh token is required for the refresh-token kind.");
        }

        var scopes = request.Scopes ?? existing?.Scopes ?? [];

        return (new PredefinedKeyEntry
        {
            Id = id,
            Host = host,
            Kind = kind,
            HeaderName = headerName,
            TokenEndpoint = tokenEndpoint,
            ClientId = clientId,
            ClientSecret = clientSecret,
            RefreshToken = refreshToken,
            Scopes = scopes,
        }, null);
    }

    /// <summary>Maps an internal entry to the masked, secret-free view returned to the UI.</summary>
    private static EgressKeyView ToView(PredefinedKeyEntry entry) => new(
        Id: entry.Id,
        Host: entry.Host,
        Kind: KindToWire(entry.Kind),
        HeaderName: entry.HeaderName,
        HeaderNames: [.. entry.Headers.Select(h => h.Name)],
        HasClientSecret: !string.IsNullOrEmpty(entry.ClientSecret),
        HasRefreshToken: !string.IsNullOrEmpty(entry.RefreshToken),
        Scopes: entry.Scopes);

    private static string? Coalesce(string? preferred, string? fallback) =>
        string.IsNullOrWhiteSpace(preferred) ? fallback : preferred;

    private static bool TryParseKind(string? wire, out PredefinedKeyKind kind)
    {
        switch (wire)
        {
            case "custom-headers":
                kind = PredefinedKeyKind.CustomHeaders;
                return true;
            case "refresh-token":
                kind = PredefinedKeyKind.RefreshToken;
                return true;
            case "client-credentials":
                kind = PredefinedKeyKind.ClientCredentials;
                return true;
            default:
                kind = default;
                return false;
        }
    }

    private static string KindToWire(PredefinedKeyKind kind) => kind switch
    {
        PredefinedKeyKind.CustomHeaders => "custom-headers",
        PredefinedKeyKind.RefreshToken => "refresh-token",
        PredefinedKeyKind.ClientCredentials => "client-credentials",
        _ => "custom-headers",
    };
}

/// <summary>Create/update request for a predefined egress key. Secret fields left blank on an update preserve the stored value.</summary>
public sealed record EgressKeyRequest(
    string? Id,
    string Host,
    string Kind,
    IReadOnlyList<EgressHeaderInput>? Headers,
    string? HeaderName,
    string? TokenEndpoint,
    string? ClientId,
    string? ClientSecret,
    string? RefreshToken,
    IReadOnlyList<string>? Scopes);

/// <summary>One custom header name/value pair from the CRUD request. SECRET: <see cref="Value"/> is credential material.</summary>
public sealed record EgressHeaderInput(string Name, string Value);

/// <summary>The masked, secret-free view of an egress key returned to the UI.</summary>
public sealed record EgressKeyView(
    string Id,
    string Host,
    string Kind,
    string HeaderName,
    IReadOnlyList<string> HeaderNames,
    bool HasClientSecret,
    bool HasRefreshToken,
    IReadOnlyList<string> Scopes);
