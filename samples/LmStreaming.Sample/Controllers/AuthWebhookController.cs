using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using LmStreaming.Sample.Services.Auth;
using Microsoft.AspNetCore.Mvc;

namespace LmStreaming.Sample.Controllers;

/// <summary>
/// Implements the sandbox gateway's auth-webhook contract. The gateway calls this endpoint when an
/// outbound request from a sandboxed app matches a rule that requires an OAuth-backed credential;
/// this controller decides whether to <c>allow</c> (and injects the bearer token) or <c>deny</c>.
/// </summary>
/// <remarks>
/// SECURITY: callers are authenticated by a shared secret carried in the <c>Authorization</c>
/// header, compared in constant time. This controller never logs the token, the incoming
/// Authorization header value, or the shared secret — only provider id, destination host, and the
/// allow/deny decision.
/// </remarks>
[ApiController]
[Route("api/auth/webhook")]
public sealed class AuthWebhookController(
    IEnumerable<IOAuthTokenProvider> providers,
    AuthSharedSecret sharedSecret,
    ILogger<AuthWebhookController> logger) : ControllerBase
{
    /// <summary>
    /// Gateway callback: returns an allow decision (with a <c>Bearer</c> Authorization header and the
    /// token's real expiry) when the named provider has a valid token, otherwise a deny decision.
    /// Both outcomes return HTTP 200 — the decision lives in the response body, not the status code.
    /// </summary>
    /// <param name="provider">Route provider id, e.g. "github" or "ado".</param>
    /// <param name="body">The gateway's webhook request describing the outbound call.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpPost("{provider}")]
    public async Task<IActionResult> Evaluate(
        string provider,
        [FromBody] AuthWebhookRequest body,
        CancellationToken ct = default)
    {
        if (!IsAuthorized())
        {
            // Do not reveal whether the header was missing, malformed, or simply wrong.
            logger.LogWarning("Rejected unauthorized auth-webhook call for provider {Provider}.", provider);
            return Unauthorized();
        }

        ArgumentNullException.ThrowIfNull(body);

        var tokenProvider = Resolve(provider);
        if (tokenProvider is null)
        {
            logger.LogWarning(
                "Auth-webhook deny for unknown provider {Provider} (host {DestinationHost}).",
                provider,
                body.DestinationHost);
            return Ok(AuthWebhookResponse.Deny("unknown provider"));
        }

        try
        {
            var token = await tokenProvider.GetAccessTokenAsync(body.RequiredScopes, ct);
            logger.LogInformation(
                "Auth-webhook allow for provider {ProviderId} (host {DestinationHost}).",
                tokenProvider.ProviderId,
                body.DestinationHost);
            return Ok(AuthWebhookResponse.Allow(token));
        }
        catch (InvalidOperationException ex)
        {
            // Not signed in / refresh failed. Keep the provider's error detail in the server log
            // ONLY — never echo it back to the gateway, where it could surface in aggregated logs.
            logger.LogInformation(
                ex,
                "Auth-webhook deny for provider {ProviderId} (host {DestinationHost}).",
                tokenProvider.ProviderId,
                body.DestinationHost);
            return Ok(AuthWebhookResponse.Deny($"no valid token for provider '{tokenProvider.ProviderId}'; sign in required"));
        }
    }

    /// <summary>
    /// Constant-time comparison of the incoming <c>Authorization</c> header against the shared secret.
    /// Both sides are hashed to a fixed-width SHA-256 digest first, so the comparison neither throws
    /// on a length mismatch nor leaks the secret's length via an early-exit.
    /// </summary>
    private bool IsAuthorized()
    {
        var presented = Request.Headers.Authorization.ToString();
        if (string.IsNullOrEmpty(presented))
        {
            return false;
        }

        var presentedHash = SHA256.HashData(Encoding.UTF8.GetBytes(presented));
        var expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(sharedSecret.Value));
        return CryptographicOperations.FixedTimeEquals(presentedHash, expectedHash);
    }

    /// <summary>Resolves a registered provider by the route id, matching <see cref="IOAuthTokenProvider.ProviderId"/> case-insensitively.</summary>
    private IOAuthTokenProvider? Resolve(string provider) =>
        providers.FirstOrDefault(p => string.Equals(p.ProviderId, provider, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// The gateway → webhook request body. Field names are the gateway's wire contract (snake_case),
/// pinned via <see cref="JsonPropertyNameAttribute"/> so they bind regardless of the app's JSON
/// naming defaults.
/// </summary>
public sealed record AuthWebhookRequest
{
    [JsonPropertyName("session_id")]
    public string? SessionId { get; init; }

    [JsonPropertyName("app_id")]
    public string? AppId { get; init; }

    [JsonPropertyName("provider_id")]
    public string? ProviderId { get; init; }

    [JsonPropertyName("rule_id")]
    public string? RuleId { get; init; }

    [JsonPropertyName("destination_host")]
    public string? DestinationHost { get; init; }

    [JsonPropertyName("destination_port")]
    public int DestinationPort { get; init; }

    [JsonPropertyName("method")]
    public string? Method { get; init; }

    [JsonPropertyName("path")]
    public string? Path { get; init; }

    [JsonPropertyName("required_scopes")]
    public string[]? RequiredScopes { get; init; }
}

/// <summary>
/// The webhook → gateway response body. Serialized with snake_case property names (pinned via
/// <see cref="JsonPropertyNameAttribute"/>) to match the gateway contract regardless of the app's
/// camelCase JSON defaults. <see cref="Headers"/> and <see cref="ExpiresAt"/> are populated only on
/// an allow; <see cref="Reason"/> only on a deny.
/// </summary>
public sealed record AuthWebhookResponse
{
    [JsonPropertyName("decision")]
    public required string Decision { get; init; }

    [JsonPropertyName("headers")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string[][]? Headers { get; init; }

    [JsonPropertyName("expires_at")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? ExpiresAt { get; init; }

    [JsonPropertyName("reason")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Reason { get; init; }

    /// <summary>Builds an allow decision injecting the bearer token and the token's real expiry.</summary>
    internal static AuthWebhookResponse Allow(OAuthAccessToken token) => new()
    {
        Decision = "allow",
        Headers = [["Authorization", $"Bearer {token.Value}"]],
        ExpiresAt = token.ExpiresAtUtc,
    };

    /// <summary>Builds a deny decision carrying a (token-free) reason.</summary>
    internal static AuthWebhookResponse Deny(string reason) => new()
    {
        Decision = "deny",
        Reason = reason,
    };
}
