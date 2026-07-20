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
    IAuthWebhookForwarder authWebhookForwarder,
    AuthOptions authOptions,
    ILogger<AuthWebhookController> logger,
    PredefinedKeyRegistry? predefinedKeys = null) : ControllerBase
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
        // only inject the credential toward that provider's/entry's own hosts. Predefined keys carry
        // their own (user-entered) host, so they gate on the entry's host, not the managed OAuth list.
        if (!IsDestinationAllowed(tokenProvider, body.DestinationHost, body.DestinationPort))
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
        var defer = false;
        try
        {
            var token = await tokenProvider.GetAccessTokenAsync(body.RequiredScopes, ct);
            logger.LogInformation(
                "Auth-webhook allow for provider {ProviderId} (host {DestinationHost}).",
                tokenProvider.ProviderId,
                body.DestinationHost);
            return Ok(BuildAllow(tokenProvider, body.DestinationHost, token));
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
        //
        // In parallel (and independent of the policy above), a session-registered webhook may also
        // want to hear about this: NotifyAuthRequiredAsync resolves that target ONCE here and the
        // captured value is reused for whichever terminal call fires below — the policy resolution
        // in between must never cause a second, possibly different, resolution.
        var capturedTarget = await TryNotifyAuthRequiredAsync(tokenProvider.ProviderId);

        try
        {
            var resolved = await authPolicy.ResolveAsync(tokenProvider, body.RequiredScopes, ct);
            if (resolved is not null)
            {
                logger.LogInformation(
                    "Auth-webhook allow for provider {ProviderId} (host {DestinationHost}) after policy resolution.",
                    tokenProvider.ProviderId,
                    body.DestinationHost);
                await TryNotifyAuthCompletedAsync(capturedTarget, tokenProvider.ProviderId);
                return Ok(BuildAllow(tokenProvider, body.DestinationHost, resolved));
            }

            logger.LogInformation(
                "Auth-webhook deny for provider {ProviderId} (host {DestinationHost}).",
                tokenProvider.ProviderId,
                body.DestinationHost);
            await TryNotifyAuthDeniedAsync(capturedTarget, tokenProvider.ProviderId, "no valid token; sign in required");
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
            await TryNotifyAuthDeniedAsync(capturedTarget, tokenProvider.ProviderId, "token acquisition failed");
            return Ok(AuthWebhookResponse.Deny($"token acquisition failed for provider '{tokenProvider.ProviderId}'"));
        }

        // Best-effort webhook-forwarder call sites: none of these may ever turn an allow/deny
        // decision into a 500. A missing session id (no session-aware caller) is a no-op, not an
        // error — the forwarder is additive to, and independent of, the WS-facing IAuthEventNotifier.
        async Task<AuthWebhookTarget?> TryNotifyAuthRequiredAsync(string providerId)
        {
            if (string.IsNullOrEmpty(body.SessionId))
            {
                return null;
            }

            try
            {
                return await authWebhookForwarder.NotifyAuthRequiredAsync(
                    body.SessionId,
                    providerId,
                    AuthSigninUrls.BuildAbsoluteSigninUrl(authOptions.Webhook.CallbackBaseUrl, providerId),
                    AuthSigninUrls.BuildReason(providerId),
                    ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // The gateway aborted the call — do not swallow it as a forwarder failure.
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "Auth-webhook forwarder failed on auth-required for provider {ProviderId}; continuing without a forwarding target.",
                    providerId);
                return null;
            }
        }

        async Task TryNotifyAuthCompletedAsync(AuthWebhookTarget? target, string providerId)
        {
            if (string.IsNullOrEmpty(body.SessionId))
            {
                return;
            }

            try
            {
                await authWebhookForwarder.NotifyAuthCompletedAsync(target, body.SessionId, providerId, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Auth-webhook forwarder failed on auth-completed for provider {ProviderId}.", providerId);
            }
        }

        async Task TryNotifyAuthDeniedAsync(AuthWebhookTarget? target, string providerId, string reason)
        {
            if (string.IsNullOrEmpty(body.SessionId))
            {
                return;
            }

            try
            {
                await authWebhookForwarder.NotifyAuthDeniedAsync(target, body.SessionId, providerId, reason, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Auth-webhook forwarder failed on auth-denied for provider {ProviderId}.", providerId);
            }
        }
    }

    /// <summary>
    /// Resolves a registered OAuth provider by the route id (case-insensitive), falling back to a
    /// runtime predefined-key provider (<c>predefined-&lt;id&gt;</c>) from the registry.
    /// </summary>
    private IOAuthTokenProvider? Resolve(string provider) =>
        providers.FirstOrDefault(p => string.Equals(p.ProviderId, provider, StringComparison.OrdinalIgnoreCase))
        ?? predefinedKeys?.TryResolve(provider);

    /// <summary>
    /// The defense-in-depth destination gate. Predefined keys gate on their own user-entered host(s)
    /// AND on port 443 — the generated rule is HTTPS/443-only, so a malformed/misbehaving gateway must
    /// not extract the credential for the same host on another (cleartext) port. Managed OAuth providers
    /// gate on their compile-time host allowlist.
    /// </summary>
    private static bool IsDestinationAllowed(IOAuthTokenProvider provider, string? destinationHost, int destinationPort) =>
        provider is PredefinedKeyProvider pk
            ? destinationPort == 443 && EgressHostMatcher.IsAllowed(pk.Hosts, destinationHost)
            : OAuthProviderHosts.IsAllowed(provider.ProviderId, destinationHost);

    /// <summary>
    /// Builds the allow decision: a predefined key injects its custom header list (or a minted
    /// <c>Bearer</c> token, with the token's real expiry); a managed OAuth provider injects the
    /// <c>Authorization</c> header as before.
    /// </summary>
    private static AuthWebhookResponse BuildAllow(IOAuthTokenProvider provider, string? destinationHost, OAuthAccessToken token) =>
        provider is PredefinedKeyProvider pk
            ? AuthWebhookResponse.AllowCustom(pk.BuildHeaders(token), pk.IncludeExpiry ? token.ExpiresAtUtc : null)
            : AuthWebhookResponse.Allow(provider.ProviderId, destinationHost, token);
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
    /// Builds an allow decision that injects an explicit list of <c>[name, value]</c> headers (used by
    /// predefined egress keys: custom headers verbatim, or a single minted <c>Bearer</c> token).
    /// <paramref name="expiresAt"/> is null for a static value with no lifetime (the gateway then falls
    /// back to the provider's cache TTL) and the token's real expiry for a minted OAuth token.
    /// </summary>
    internal static AuthWebhookResponse AllowCustom(
        IReadOnlyList<KeyValuePair<string, string>> headers,
        DateTimeOffset? expiresAt) => new()
        {
            Decision = "allow",
            Headers = [.. headers.Select(h => new[] { h.Key, h.Value })],
            ExpiresAt = expiresAt,
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
