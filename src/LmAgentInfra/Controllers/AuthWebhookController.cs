using System.Text;
using System.Text.Json.Serialization;
using AchieveAi.LmDotnetTools.LmAgentInfra.Auth;
using Microsoft.AspNetCore.Mvc;

namespace AchieveAi.LmDotnetTools.LmAgentInfra.Controllers;

/// <summary>
/// Implements the sandbox gateway's auth-webhook contract. The gateway calls this endpoint when an
/// outbound request from a sandboxed app matches a rule that requires an OAuth-backed credential;
/// this controller decides whether to <c>allow</c> (and injects the bearer token) or <c>deny</c>.
/// </summary>
/// <remarks>
/// SECURITY: callers are authenticated by a shared secret carried in the <c>Authorization</c>
/// header, compared in constant time, and tokens are only injected toward the provider's own
/// destination hosts (<see cref="OAuthProviderHosts"/> — the same lists the sandbox network rules
/// are built from). This controller never logs the token, the incoming Authorization header value,
/// or the shared secret — only provider id, destination host, and the allow/deny decision.
/// </remarks>
[ApiController]
[Route("api/auth/webhook")]
public sealed class AuthWebhookController(
    IEnumerable<IOAuthTokenProvider> providers,
    AuthSharedSecret sharedSecret,
    IAuthResolutionPolicy authPolicy,
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
        if (!sharedSecret.Matches(Request.Headers.Authorization.ToString()))
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

        // Defense-in-depth: the egress proxy already validates the destination per its rules, but a
        // rules misconfiguration must not turn this endpoint into an open token-minting oracle —
        // only inject the provider's token toward that provider's own hosts.
        if (!OAuthProviderHosts.IsAllowed(tokenProvider.ProviderId, body.DestinationHost))
        {
            logger.LogWarning(
                "Auth-webhook deny for provider {ProviderId}: destination host {DestinationHost} is not in the provider's allowlist.",
                tokenProvider.ProviderId,
                body.DestinationHost);
            return Ok(AuthWebhookResponse.Deny($"destination host not allowed for provider '{tokenProvider.ProviderId}'"));
        }

        // First attempt: a token may already be available (signed in / refreshable). Only the
        // "not signed in" case (InvalidOperationException) falls through to the auth policy — and that
        // decision is hoisted OUT of the catch so the policy runs under its own try/catch.
        // An await inside a catch block is not protected by the sibling catches of the same try,
        // so running the policy there would let any non-OCE failure escape as a 500, breaking the
        // always-200 allow/deny contract the broad catch below exists to guarantee.
        bool defer = false;
        try
        {
            var token = await tokenProvider.GetAccessTokenAsync(body.RequiredScopes, ct);
            logger.LogInformation(
                "Auth-webhook allow for provider {ProviderId} (host {DestinationHost}).",
                tokenProvider.ProviderId,
                body.DestinationHost);
            return Ok(AuthWebhookResponse.Allow(tokenProvider.ProviderId, body.DestinationHost, token));
        }
        catch (InvalidOperationException ex)
        {
            // Not signed in / refresh failed. Keep the provider's error detail in the server log
            // ONLY — never echo it back to the gateway, where it could surface in aggregated logs.
            // Hand off to the host's auth-resolution policy (hold-and-prompt vs. fail-fast).
            logger.LogInformation(
                ex,
                "Auth-webhook found no valid token for provider {ProviderId} (host {DestinationHost}); applying auth-resolution policy.",
                tokenProvider.ProviderId,
                body.DestinationHost);
            defer = true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The gateway aborted the call — nobody is waiting for a decision.
            throw;
        }
        catch (Exception ex)
        {
            // Any other failure (token-store IO, refresh HTTP call, MSAL, JSON parsing) must still
            // honor the always-200 allow/deny contract: an unhandled 500 surfaces gateway-side as an
            // opaque webhook failure instead of a clean deny. Reason stays generic and token-free.
            logger.LogWarning(
                ex,
                "Auth-webhook unexpected failure for provider {ProviderId} (host {DestinationHost}); denying.",
                tokenProvider.ProviderId,
                body.DestinationHost);
            return Ok(AuthWebhookResponse.Deny($"token acquisition failed for provider '{tokenProvider.ProviderId}'"));
        }

        if (!defer)
        {
            // Unreachable in practice (every branch above returns or sets defer), but keeps the
            // compiler's definite-assignment analysis happy without a throw.
            return Ok(AuthWebhookResponse.Deny($"no valid token for provider '{tokenProvider.ProviderId}'; sign in required"));
        }

        // No immediately-available token: defer to the host's auth-resolution policy. The attended
        // app holds the call open and prompts a connected client to sign in (allow once a token
        // lands, deny on timeout); the unattended daemon raises an operator "auth required" signal
        // and denies at once. Runs under its own try/catch so the always-200 contract holds here too.
        try
        {
            var resolved = await authPolicy.ResolveAsync(tokenProvider, body.RequiredScopes, ct);
            if (resolved is not null)
            {
                logger.LogInformation(
                    "Auth-webhook allow for provider {ProviderId} (host {DestinationHost}) after policy resolution.",
                    tokenProvider.ProviderId,
                    body.DestinationHost);
                return Ok(AuthWebhookResponse.Allow(tokenProvider.ProviderId, body.DestinationHost, resolved));
            }

            logger.LogInformation(
                "Auth-webhook deny for provider {ProviderId} (host {DestinationHost}).",
                tokenProvider.ProviderId,
                body.DestinationHost);
            return Ok(AuthWebhookResponse.Deny($"no valid token for provider '{tokenProvider.ProviderId}'; sign in required"));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The gateway aborted the held call — nobody is waiting for a decision.
            throw;
        }
        catch (Exception ex)
        {
            // Same always-200 guarantee for the policy path: a token-store/MSAL/HTTP failure
            // surfaced during resolution becomes a clean, token-free deny, not an opaque 500.
            logger.LogWarning(
                ex,
                "Auth-webhook policy-resolution failure for provider {ProviderId} (host {DestinationHost}); denying.",
                tokenProvider.ProviderId,
                body.DestinationHost);
            return Ok(AuthWebhookResponse.Deny($"token acquisition failed for provider '{tokenProvider.ProviderId}'"));
        }
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

    /// <summary>
    /// Builds an allow decision injecting a host-appropriate Authorization header and the token's real
    /// expiry. Most endpoints take <c>Bearer</c>, but GitHub's git-over-HTTPS endpoint
    /// (<c>github.com</c>) rejects Bearer with 401 and requires HTTP Basic — so git operations get
    /// <c>Basic base64("x-access-token:&lt;token&gt;")</c> instead. See <see cref="BuildAuthorizationHeaderValue"/>.
    /// </summary>
    internal static AuthWebhookResponse Allow(string providerId, string? destinationHost, OAuthAccessToken token) => new()
    {
        Decision = "allow",
        Headers = [["Authorization", BuildAuthorizationHeaderValue(providerId, destinationHost, token)]],
        ExpiresAt = token.ExpiresAtUtc,
    };

    /// <summary>
    /// Selects the Authorization scheme for the destination. GitHub's REST API (<c>api.github.com</c>)
    /// and archive host (<c>codeload.github.com</c>) accept <c>Bearer</c>, but its Git smart-HTTP
    /// endpoint (<c>github.com</c>, used by <c>git clone/fetch/push</c>) only accepts HTTP Basic auth
    /// with username <c>x-access-token</c> and the token as the password. Everything else (the GitHub
    /// REST API, Azure DevOps, …) keeps <c>Bearer</c>.
    /// </summary>
    internal static string BuildAuthorizationHeaderValue(string providerId, string? destinationHost, OAuthAccessToken token)
    {
        if (IsGitHubGitHost(providerId, destinationHost))
        {
            var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"x-access-token:{token.Value}"));
            return $"Basic {basic}";
        }

        return $"Bearer {token.Value}";
    }

    /// <summary>True only for the GitHub provider's Git smart-HTTP host (<c>github.com</c>), which needs Basic auth.</summary>
    private static bool IsGitHubGitHost(string providerId, string? destinationHost) =>
        string.Equals(providerId, "github", StringComparison.OrdinalIgnoreCase)
        && string.Equals(destinationHost, "github.com", StringComparison.OrdinalIgnoreCase);

    /// <summary>Builds a deny decision carrying a (token-free) reason.</summary>
    internal static AuthWebhookResponse Deny(string reason) => new()
    {
        Decision = "deny",
        Reason = reason,
    };
}
