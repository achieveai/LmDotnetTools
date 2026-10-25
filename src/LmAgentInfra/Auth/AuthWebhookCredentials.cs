using AchieveAi.LmDotnetTools.LmAgentInfra.Controllers;

namespace AchieveAi.LmDotnetTools.LmAgentInfra.Auth;

/// <summary>Shared provider gates and header construction for legacy and unified credential callbacks.</summary>
internal static class AuthWebhookCredentials
{
    internal static IOAuthTokenProvider? Resolve(
        IEnumerable<IOAuthTokenProvider> providers,
        PredefinedKeyRegistry? predefinedKeys,
        string provider
    ) =>
        providers.FirstOrDefault(p => string.Equals(p.ProviderId, provider, StringComparison.OrdinalIgnoreCase))
        ?? predefinedKeys?.TryResolve(provider);

    internal static bool IsDestinationAllowed(
        IOAuthTokenProvider provider,
        PredefinedKeyEntry? keyRevision,
        string? destinationHost,
        int destinationPort
    ) =>
        provider is PredefinedKeyProvider
            ? keyRevision is not null
                && destinationPort == keyRevision.Port
                && EgressHostMatcher.IsAllowed([keyRevision.Host], destinationHost)
            : OAuthProviderHosts.IsAllowed(provider.ProviderId, destinationHost);

    internal static bool KeyRevisionChanged(IOAuthTokenProvider provider, PredefinedKeyEntry? revision) =>
        provider is PredefinedKeyProvider key && !ReferenceEquals(key.Entry, revision);

    internal static Task<OAuthAccessToken> AcquireAsync(
        IOAuthTokenProvider provider,
        IReadOnlyList<string>? scopes,
        CancellationToken ct
    ) =>
        provider is AdoOAuthProvider ado
            ? ado.GetWebhookAccessTokenAsync(ct)
            : provider.GetAccessTokenAsync(scopes, ct);

    internal static AuthWebhookResponse BuildAllow(
        IOAuthTokenProvider provider,
        PredefinedKeyEntry? keyRevision,
        string? destinationHost,
        OAuthAccessToken token
    ) =>
        provider is PredefinedKeyProvider && keyRevision is not null
            ? AuthWebhookResponse.AllowCustom(
                PredefinedKeyProvider.BuildHeaders(keyRevision, token),
                PredefinedKeyProvider.IncludesExpiry(keyRevision) ? token.ExpiresAtUtc : null
            )
            : AuthWebhookResponse.Allow(provider.ProviderId, destinationHost, token);
}
