namespace AchieveAi.LmDotnetTools.LmAgentInfra.Auth;

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
        // results-receiver + the blob host below are hops in GitHub's Actions run-log download
        // redirect chain (api.github.com -> results-receiver -> a per-shard SAS-signed blob URL).
        // The blob host is exact-listed (not *.blob.core.windows.net) since that suffix covers
        // every Azure Blob Storage tenant, not just GitHub's; if GitHub rotates to a new shard,
        // this will need the new host added.
        ["github"] = [
            "github.com",
            "api.github.com",
            "codeload.github.com",
            "results-receiver.actions.githubusercontent.com",
            "productionresultssa7.blob.core.windows.net",
        ],
        ["ado"] = ["dev.azure.com", "*.dev.azure.com", "*.visualstudio.com"],
        ["m365"] = ["graph.microsoft.com"],
    };

    /// <summary>The allowed destination hosts for <paramref name="providerId"/> (empty when unknown).</summary>
    public static IReadOnlyList<string> For(string providerId) =>
        HostsByProvider.TryGetValue(providerId, out var hosts) ? hosts : [];

    /// <summary>
    /// True when <paramref name="destinationHost"/> matches one of the provider's allowed hosts —
    /// exact (case-insensitive) or a <c>*.</c> wildcard suffix match. Fails closed: an unknown
    /// provider or a missing host is not allowed. Matching is delegated to
    /// <see cref="EgressHostMatcher.IsAllowed"/> so the OAuth path and the predefined-key path share
    /// one algorithm.
    /// </summary>
    public static bool IsAllowed(string providerId, string? destinationHost) =>
        HostsByProvider.TryGetValue(providerId, out var hosts)
        && EgressHostMatcher.IsAllowed(hosts, destinationHost);

    /// <summary>
    /// True when a user-entered predefined-key host <paramref name="pattern"/> would match (or be
    /// matched by) a managed OAuth provider's host — i.e. it collides with the github/ado/m365 egress
    /// scope. Predefined-key entries are rejected in that case so a static key can never silently
    /// shadow (or be shadowed by) a managed OAuth credential on the same host.
    /// </summary>
    public static bool CollidesWithManagedHost(string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return false;
        }

        // Strip a single leading "*." so a wildcard entry is compared by its concrete suffix host.
        var candidate = pattern.StartsWith("*.", StringComparison.Ordinal) ? pattern[2..] : pattern;

        foreach (var hosts in HostsByProvider.Values)
        {
            // Collision either way: the entry matches a managed host, or a managed *.wildcard matches
            // the entry's host.
            if (EgressHostMatcher.IsAllowed(hosts, candidate)
                || hosts.Any(h => EgressHostMatcher.IsAllowed([pattern], h)))
            {
                return true;
            }
        }

        return false;
    }
}
