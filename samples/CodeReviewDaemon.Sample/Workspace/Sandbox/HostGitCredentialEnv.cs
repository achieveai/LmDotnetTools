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
/// <para>
/// For each configured ADO org we also emit a git <c>url.&lt;base&gt;.insteadOf</c> rewrite
/// (<c>url.https://dev.azure.com/{org}/.insteadOf = https://{org}.visualstudio.com/</c>) so an actual
/// <c>git submodule update --init</c> against a repo's legacy <c>{org}.visualstudio.com</c> URL is fetched
/// from <c>dev.azure.com</c> instead — where the ADO <c>extraHeader</c> above applies. <c>insteadOf</c> cannot
/// do the org-extraction generically, so each rewrite is keyed to a known org supplied by the caller.
/// </para>
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
    /// <see cref="Build(IReadOnlyList{GitProviderToken}, IReadOnlyCollection{string})"/> with one
    /// <c>github</c> entry and no ADO orgs.
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
    /// fast instead of prompting. Each org in <paramref name="adoOrgs"/> additionally contributes a
    /// legacy→modern <c>url.&lt;base&gt;.insteadOf</c> rewrite so a <c>{org}.visualstudio.com</c> fetch is
    /// redirected to <c>dev.azure.com</c> (reusing the ADO credential above); a <c>null</c>/empty set emits
    /// none. Orgs are de-duplicated case-insensitively.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Build(
        IReadOnlyList<GitProviderToken> tokens,
        IReadOnlyCollection<string>? adoOrgs = null)
    {
        ArgumentNullException.ThrowIfNull(tokens);

        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        var index = 0;
        foreach (var token in tokens)
        {
            if (string.IsNullOrWhiteSpace(token.Token)
                || !ProviderGitHosts.TryGetValue(token.ProviderId, out var mapping))
            {
                continue;
            }

            var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{mapping.Username}:{token.Token}"));
            env[$"GIT_CONFIG_KEY_{index}"] = $"http.https://{mapping.Host}/.extraHeader";
            env[$"GIT_CONFIG_VALUE_{index}"] = "Authorization: Basic " + basic;
            index++;
        }

        if (adoOrgs is not null)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var org in adoOrgs)
            {
                if (string.IsNullOrWhiteSpace(org) || !seen.Add(org))
                {
                    continue;
                }

                // Rewrite the legacy org host to the modern one so the dev.azure.com extraHeader authenticates
                // the fetch. VALUE is a prefix git matches against; KEY's <base> is what it substitutes in.
                env[$"GIT_CONFIG_KEY_{index}"] = $"url.https://dev.azure.com/{org}/.insteadOf";
                env[$"GIT_CONFIG_VALUE_{index}"] = $"https://{org}.visualstudio.com/";
                index++;
            }
        }

        env["GIT_CONFIG_COUNT"] = index.ToString(CultureInfo.InvariantCulture);
        env["GIT_TERMINAL_PROMPT"] = "0";
        return env;
    }
}
