using AchieveAi.LmDotnetTools.LmAgentInfra.Controllers;
using AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox;

namespace AchieveAi.LmDotnetTools.LmAgentInfra.Auth;

/// <summary>
/// Evaluates an already authenticated unified MCP callback. Only operator bindings select providers;
/// callback metadata must match the live session's immutable owner and destination before any token lookup.
/// </summary>
public sealed class SandboxMcpAuthService(
    SandboxSessionRegistry sessionRegistry,
    IEnumerable<IOAuthTokenProvider> providers,
    PredefinedKeyRegistry? predefinedKeys = null,
    TimeProvider? timeProvider = null
)
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<AuthWebhookResponse> EvaluateAsync(AuthWebhookRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!TryMatch(request, requireServer: true, out var context, out var binding))
        {
            return AuthWebhookResponse.Deny("MCP binding or destination not allowed");
        }

        var provider = AuthWebhookCredentials.Resolve(providers, predefinedKeys, binding!.ProviderId);
        if (provider is null)
        {
            return AuthWebhookResponse.Deny("MCP credential provider not configured");
        }

        var revision = (provider as PredefinedKeyProvider)?.Entry;
        if (
            !AuthWebhookCredentials.IsDestinationAllowed(
                provider,
                revision,
                request.DestinationHost,
                request.DestinationPort
            )
        )
        {
            return AuthWebhookResponse.Deny("MCP credential destination not allowed");
        }

        try
        {
            // MCP callbacks must not wait for attended sign-in. Bound acquisition below the gateway's 15s timeout.
            using var acquisition = CancellationTokenSource.CreateLinkedTokenSource(ct);
            acquisition.CancelAfter(TimeSpan.FromSeconds(10));
            var token = await AuthWebhookCredentials
                .AcquireAsync(provider, null, acquisition.Token)
                .WaitAsync(TimeSpan.FromSeconds(10), ct)
                .ConfigureAwait(false);
            if (
                !sessionRegistry.IsCurrentMcpContext(request.SessionId!, context!)
                || AuthWebhookCredentials.KeyRevisionChanged(provider, revision)
                || !ReferenceEquals(
                    provider,
                    AuthWebhookCredentials.Resolve(providers, predefinedKeys, binding.ProviderId)
                )
            )
            {
                return AuthWebhookResponse.Deny("MCP session or credential changed; retry");
            }

            var decision = AuthWebhookCredentials.BuildAllow(provider, revision, request.DestinationHost, token);
            var requiredHeaders = context!.Policy.Servers.TryGetValue(binding.Server, out var policy)
                ? policy.Headers ?? binding.Headers
                : binding.Headers;
            var headers =
                decision
                    .Headers?.Where(h =>
                        h.Length == 2
                        && binding.Headers.Contains(h[0], StringComparer.OrdinalIgnoreCase)
                        && requiredHeaders.Contains(h[0], StringComparer.OrdinalIgnoreCase)
                    )
                    .ToArray()
                ?? [];
            if (
                headers.Length == 0
                || requiredHeaders.Any(name =>
                    !headers.Any(h => string.Equals(h[0], name, StringComparison.OrdinalIgnoreCase))
                )
                || headers.Any(h => EgressHostMatcher.ValidateHeaderValue(h[1]) is not null)
            )
            {
                return AuthWebhookResponse.Deny("MCP credential headers not available");
            }

            var now = _clock.GetUtcNow();
            var expires = now.AddSeconds(binding.CacheTtlSeconds);
            if (decision.ExpiresAt is { } tokenExpiry)
            {
                var safeTokenExpiry = tokenExpiry.AddSeconds(-30);
                expires = expires < safeTokenExpiry ? expires : safeTokenExpiry;
            }

            return expires <= now
                ? AuthWebhookResponse.Pending()
                : new AuthWebhookResponse
                {
                    Decision = "allow",
                    Headers = headers,
                    ExpiresAt = expires,
                };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException or TimeoutException)
        {
            return AuthWebhookResponse.Pending();
        }
        catch
        {
            // Provider errors can carry secrets. Neither logs nor the gateway response receive their payloads.
            return AuthWebhookResponse.Deny("MCP credential acquisition failed");
        }
    }

    /// <summary>
    /// Failure notifications have no server URL/path. Check the available identity but do not sign out,
    /// invalidate shared OAuth grants, or acquire tokens. The gateway already evicted its own cache.
    /// </summary>
    public AuthWebhookResponse EvaluateFailure(AuthWebhookRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return TryMatch(request, requireServer: false, out _, out _)
            ? AuthWebhookResponse.Pending()
            : AuthWebhookResponse.Deny("MCP binding or destination not allowed");
    }

    private bool TryMatch(
        AuthWebhookRequest request,
        bool requireServer,
        out SandboxMcpSessionContext? context,
        out SandboxMcpCredentialBinding? binding
    )
    {
        context = null;
        binding = null;
        if (
            string.IsNullOrEmpty(request.SessionId)
            || !sessionRegistry.TryGetMcpContext(request.SessionId, out context)
            || !string.Equals(context!.AppId, request.AppId, StringComparison.Ordinal)
            || request.ProviderId is null
            || !request.ProviderId.StartsWith("mcp:", StringComparison.Ordinal)
            || !context.Bindings.TryGetValue(request.ProviderId[4..], out binding)
            || !string.Equals(binding.Endpoint.IdnHost, request.DestinationHost, StringComparison.OrdinalIgnoreCase)
            || binding.Endpoint.Port != request.DestinationPort
            || !sessionRegistry.IsCurrentMcpContext(request.SessionId, context)
        )
        {
            return false;
        }

        if (!requireServer)
        {
            return true;
        }

        var server = request.McpServer;
        // Rule IDs are routing details: manual rules can have any operator-authored ID.
        // Owner, provider/server name and pinned destination are the credential authority.
        return server is not null
            && string.Equals(server.Name, binding.Server, StringComparison.Ordinal)
            && SandboxMcpConfiguration.TryCredentialEndpoint(server.Url, out var endpoint)
            && endpoint == binding.Endpoint
            && (binding.Plugin is null || string.Equals(binding.Plugin, server.Plugin, StringComparison.Ordinal))
            && (
                binding.Marketplace is null
                || string.Equals(binding.Marketplace, server.Marketplace, StringComparison.Ordinal)
            )
            && string.Equals(request.Path, binding.Endpoint.AbsolutePath, StringComparison.Ordinal)
            && request.Method is "POST" or "GET" or "DELETE";
    }
}
