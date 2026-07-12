using System.Globalization;
using System.Text;

namespace CodeReviewDaemon.Sample.Workspace.Sandbox;

/// <summary>
/// A single provider's git credential input: the OAuth <paramref name="ProviderId"/> (e.g. <c>github</c>,
/// <c>ado</c>) and its freshly-minted access <paramref name="Token"/>. <see cref="HostGitCredentialEnv"/>
/// maps the id to the git host + Basic-auth scheme that host expects.
/// </summary>
internal readonly record struct GitProviderToken(string ProviderId, string Token);

/// <summary>
/// Builds the environment variables that authenticate the daemon's HOST-side git to a remote without the
/// token ever appearing in a process argument or in an on-disk git config. Git reads ad-hoc config from
/// GIT_CONFIG_COUNT/KEY_n/VALUE_n, so we inject one <c>http.&lt;url&gt;.extraHeader</c> per signed-in
/// provider, each carrying a Basic credential: GitHub uses its documented username <c>x-access-token</c> +
/// token password; Azure DevOps sends the Entra token in the password field with an empty (ignored)
/// username — the same scheme <c>AdoPrProvider</c>/<c>AdoReviewCommentPublisher</c> use for ADO REST.
/// GIT_TERMINAL_PROMPT=0 fails fast rather than hanging on a credential prompt. A provider with no git-host
/// mapping is skipped, so an unrelated provider (e.g. M365) never contributes a bogus header.
/// </summary>
internal static class HostGitCredentialEnv
{
    /// <summary>
    /// The git host + Basic-auth username each supported OAuth provider authenticates with. All ADO URLs are
    /// normalized to <c>dev.azure.com</c> upstream (see <c>DaemonReviewStageExecutor.TargetRemoteUrl</c>), so
    /// a single host entry covers ADO clones/fetches.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, (string Host, string Username)> ProviderGitHosts =
        new Dictionary<string, (string Host, string Username)>(StringComparer.OrdinalIgnoreCase)
        {
            ["github"] = ("github.com", "x-access-token"),
            ["ado"] = ("dev.azure.com", ""),
        };

    /// <summary>
    /// GitHub-only convenience for callers that hold a single GitHub token — equivalent to
    /// <see cref="Build(IReadOnlyList{GitProviderToken})"/> with one <c>github</c> entry.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Build(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        return Build([new GitProviderToken("github", token)]);
    }

    /// <summary>
    /// Builds the git credential env for every provider in <paramref name="tokens"/> that has a known git
    /// host, one <c>GIT_CONFIG_KEY_n/VALUE_n</c> pair each (in list order). An empty or all-unmapped list
    /// emits no credential entries — only GIT_TERMINAL_PROMPT=0 — so an unauthenticated git command fails
    /// fast instead of prompting. At most one header is emitted per git host: git treats a repeated config
    /// key as multi-valued and would apply BOTH <c>Authorization</c> headers, so a duplicate provider id for
    /// the same host is undefined for an auth path — the first mapped token per host wins and later
    /// duplicates are ignored.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Build(IReadOnlyList<GitProviderToken> tokens)
    {
        ArgumentNullException.ThrowIfNull(tokens);

        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        var seenHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var index = 0;
        foreach (var token in tokens)
        {
            if (string.IsNullOrWhiteSpace(token.Token)
                || !ProviderGitHosts.TryGetValue(token.ProviderId, out var mapping)
                || !seenHosts.Add(mapping.Host))
            {
                // Blank token, an unmapped provider (e.g. m365), or a host we already emitted a header for.
                // `index` advances ONLY on emit, so GIT_CONFIG_COUNT stays consistent with a contiguous
                // KEY_0..COUNT-1 even when an entry in the middle is skipped.
                continue;
            }

            var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{mapping.Username}:{token.Token}"));
            env[$"GIT_CONFIG_KEY_{index}"] = $"http.https://{mapping.Host}/.extraHeader";
            env[$"GIT_CONFIG_VALUE_{index}"] = "Authorization: Basic " + basic;
            index++;
        }

        env["GIT_CONFIG_COUNT"] = index.ToString(CultureInfo.InvariantCulture);
        env["GIT_TERMINAL_PROMPT"] = "0";
        return env;
    }
}
