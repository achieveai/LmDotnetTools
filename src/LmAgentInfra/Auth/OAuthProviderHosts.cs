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
        // OAuth-token-injected hosts ONLY. The user's GitHub access token is attached (as an
        // Authorization header) by the auth webhook on egress to every host in this list, so it must
        // contain nothing but GitHub's own API/git endpoints. The Actions run-log download redirect
        // chain (results-receiver -> a SAS-signed Azure blob) is deliberately NOT here — those hops
        // already carry their own authorization and must be reached WITHOUT the GitHub token; they get
        // a separate network-only allow rule (see <see cref="GithubEgressOnlyHosts"/>).
        ["github"] = [
            "github.com",
            "api.github.com",
            "codeload.github.com",
        ],
        ["ado"] = ["dev.azure.com", "*.dev.azure.com", "*.visualstudio.com"],
        ["m365"] = ["graph.microsoft.com"],
    };

    /// <summary>
    /// Hosts the GitHub Actions run-log download redirect chain hops through
    /// (<c>api.github.com</c> -&gt; <c>results-receiver</c> -&gt; a per-shard SAS-signed Azure blob
    /// URL) that must be reachable but must NEVER receive the user's OAuth token: the SAS URL is
    /// already authorized and the receiver is not a GitHub API endpoint. These are emitted as a
    /// network-only allow rule (no <c>authProvider</c>) by
    /// <c>SandboxSessionRegistry.BuildAuthProviders</c>, so the gateway permits egress without ever
    /// calling the auth webhook — and because no token is injected here, they are intentionally kept
    /// out of <see cref="HostsByProvider"/> so the webhook's <see cref="IsAllowed"/> gate also refuses
    /// GitHub-token injection to them. <c>*.blob.core.windows.net</c> is the suffix GitHub documents
    /// for log/artifact/cache transfers, so it is used instead of pinning a single shard (e.g.
    /// <c>productionresultssa7</c>) that would break whenever GitHub rotates shards; because this rule
    /// injects no credential, the broad suffix carries no token-leak risk.
    /// </summary>
    public static readonly IReadOnlyList<string> GithubEgressOnlyHosts =
    [
        "results-receiver.actions.githubusercontent.com",
        "*.blob.core.windows.net",
    ];

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
