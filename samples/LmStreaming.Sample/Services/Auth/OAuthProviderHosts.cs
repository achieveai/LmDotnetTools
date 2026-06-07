namespace LmStreaming.Sample.Services.Auth;

/// <summary>
/// Single source of truth for the destination hosts each OAuth provider's token may be injected
/// into. Consumed by both the sandbox network rules (<c>SandboxSessionRegistry.BuildAuthProviders</c>)
/// and the auth webhook's defense-in-depth host check (<c>AuthWebhookController</c>) so the two
/// can never drift apart. Entries are exact hosts or <c>*.</c> wildcard suffixes.
/// </summary>
internal static class OAuthProviderHosts
{
    private static readonly Dictionary<string, string[]> HostsByProvider = new(StringComparer.OrdinalIgnoreCase)
    {
        ["github"] = ["github.com", "api.github.com", "codeload.github.com"],
        ["ado"] = ["dev.azure.com", "*.dev.azure.com", "*.visualstudio.com"],
        ["m365"] = ["graph.microsoft.com"],
    };

    /// <summary>The allowed destination hosts for <paramref name="providerId"/> (empty when unknown).</summary>
    public static IReadOnlyList<string> For(string providerId) =>
        HostsByProvider.TryGetValue(providerId, out var hosts) ? hosts : [];

    /// <summary>
    /// True when <paramref name="destinationHost"/> matches one of the provider's allowed hosts —
    /// exact (case-insensitive) or a <c>*.</c> wildcard suffix match. Fails closed: an unknown
    /// provider or a missing host is not allowed.
    /// </summary>
    public static bool IsAllowed(string providerId, string? destinationHost)
    {
        if (string.IsNullOrWhiteSpace(destinationHost) || !HostsByProvider.TryGetValue(providerId, out var hosts))
        {
            return false;
        }

        foreach (var host in hosts)
        {
            var allowed = host.StartsWith("*.", StringComparison.Ordinal)
                ? destinationHost.EndsWith(host[1..], StringComparison.OrdinalIgnoreCase)
                : string.Equals(destinationHost, host, StringComparison.OrdinalIgnoreCase);
            if (allowed)
            {
                return true;
            }
        }

        return false;
    }
}
