using System.Text;

namespace CodeReviewDaemon.Sample.Workspace.Sandbox;

/// <summary>
/// Builds the environment variables that authenticate the daemon's HOST-side git to github.com without
/// the token ever appearing in a process argument or in an on-disk git config. Git reads ad-hoc config
/// from GIT_CONFIG_COUNT/KEY_n/VALUE_n, so we inject an <c>http.&lt;url&gt;.extraHeader</c> that carries a
/// Basic credential (username <c>x-access-token</c>, password = the OAuth token — GitHub's documented
/// scheme). GIT_TERMINAL_PROMPT=0 fails fast rather than hanging on a credential prompt.
/// </summary>
internal static class HostGitCredentialEnv
{
    public static IReadOnlyDictionary<string, string> Build(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes("x-access-token:" + token));
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["GIT_CONFIG_COUNT"] = "1",
            ["GIT_CONFIG_KEY_0"] = "http.https://github.com/.extraHeader",
            ["GIT_CONFIG_VALUE_0"] = "Authorization: Basic " + basic,
            ["GIT_TERMINAL_PROMPT"] = "0",
        };
    }
}
