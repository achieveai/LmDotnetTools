using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Workspace.Git;

namespace CodeReviewDaemon.Sample.Workspace;

/// <summary>
/// Builds the <see cref="OperationPolicy"/> the daemon enforces, both at the outbound HTTP seam
/// (Thread #1 / plan §4) and at the sandbox/git seam (PR #121 H1/H2). The host-only
/// <see cref="ForGitHub"/>/<see cref="ForAdo"/> factories scope only the provider's API/git host and are
/// used where the concrete repo route is not yet known; the per-run <see cref="BuildForRun"/> factory
/// scopes the policy to <b>exactly</b> the repos one review legitimately touches — the target repo path is
/// derived from the run's <see cref="RepoIdentity"/>, the ReviewBot host/path from the configured
/// <c>ReviewBotRepoUrl</c>, and the provider-API path prefix from the repo — so a review of untrusted PR
/// code can never reach an off-repo git remote or API route with the bot credential.
/// </summary>
internal static class DaemonOperationPolicy
{
    private const string GitHubGitHost = "github.com";
    private const string GitHubApiHost = "api.github.com";
    private const string AdoHost = "dev.azure.com";

    /// <summary>The primary (write-capable) host-only policy for GitHub provider-API requests.</summary>
    public static OperationPolicy ForGitHub() =>
        new(
            new ReviewScope(
                Provider: "github",
                TargetHost: GitHubGitHost,
                TargetRepoPath: "/",
                ForkHost: null,
                ForkRepoPath: null,
                ReviewBotHost: GitHubGitHost,
                ReviewBotRepoPath: "/",
                ApiHost: GitHubApiHost,
                AllowedSubmodules: []
            ),
            allowWriteOperations: true
        );

    /// <summary>The primary (write-capable) host-only policy for Azure DevOps provider-API requests.</summary>
    public static OperationPolicy ForAdo() =>
        new(
            new ReviewScope(
                Provider: "ado",
                TargetHost: AdoHost,
                TargetRepoPath: "/",
                ForkHost: null,
                ForkRepoPath: null,
                ReviewBotHost: AdoHost,
                ReviewBotRepoPath: "/",
                ApiHost: AdoHost,
                AllowedSubmodules: []
            ),
            allowWriteOperations: true
        );

    /// <summary>
    /// Builds the per-run policy scoped to exactly the repos this review touches (PR #121 H2). Both the
    /// HTTP seam (provider-API ops) and the sandbox/git seam (target fetch + ReviewBot push +
    /// submodules) share this single instance, so a denied op is both egress-blocked and
    /// credential-withheld no matter which seam it arrives at.
    /// </summary>
    /// <param name="repo">The run's target repository identity.</param>
    /// <param name="reviewBotRepoUrl">
    /// The configured ReviewBot remote URL, or <c>null</c> when retention is disabled (push then has no
    /// destination and is denied).
    /// </param>
    /// <param name="allowWriteOperations">
    /// <c>true</c> for the primary variant (may post + push); <c>false</c> for a collect-only A/B
    /// variant, which is denied both writes regardless of route.
    /// <para>
    /// Defaults to <c>false</c> — absence of an explicit grant means no writes. It defaulted to <c>true</c>
    /// until #536, which is how <see cref="PolicyEnforcedHttpClientFactory"/> came to hand every provider
    /// client full write capability on a collect-only run without a single line of code saying so. A
    /// write-capable policy is now something a caller has to ask for by name.
    /// </para>
    /// </param>
    /// <param name="allowedSubmodules">Per-run allow-listed submodules (empty by default).</param>
    public static OperationPolicy BuildForRun(
        RepoIdentity repo,
        string? reviewBotRepoUrl,
        bool allowWriteOperations = false,
        IReadOnlyList<SubmoduleAllowRule>? allowedSubmodules = null
    )
    {
        ArgumentNullException.ThrowIfNull(repo);

        var isAdo =
            string.Equals(repo.Provider, "azure-devops", StringComparison.Ordinal)
            || string.Equals(repo.Provider, "ado", StringComparison.Ordinal);

        var (targetHost, targetRepoPath, apiHost, apiRepoPathPrefix, providerKey) = isAdo
            ? (AdoHost, AdoGitRepoPath(repo), AdoHost, AdoApiRepoPrefix(repo), "ado")
            : (GitHubGitHost, GitHubGitRepoPath(repo), GitHubApiHost, GitHubApiRepoPrefix(repo), "github");

        var (reviewBotHost, reviewBotRepoPath) = ParseReviewBotRemote(reviewBotRepoUrl, targetHost);

        return new OperationPolicy(
            new ReviewScope(
                Provider: providerKey,
                TargetHost: targetHost,
                TargetRepoPath: targetRepoPath,
                ForkHost: null,
                ForkRepoPath: null,
                ReviewBotHost: reviewBotHost,
                ReviewBotRepoPath: reviewBotRepoPath,
                ApiHost: apiHost,
                AllowedSubmodules: allowedSubmodules ?? []
            )
            {
                ApiRepoPathPrefix = apiRepoPathPrefix,
                ApiWorkItemPaths = isAdo ? AdoApiWorkItemPaths(repo) : [],
            },
            allowWriteOperations
        );
    }

    /// <summary>GitHub git remote path: <c>/{owner}/{repo}</c>.</summary>
    private static string GitHubGitRepoPath(RepoIdentity repo) => $"/{repo.OrgOrOwner}/{repo.RepoName}";

    /// <summary>GitHub REST repo route prefix: <c>/repos/{owner}/{repo}</c>.</summary>
    private static string GitHubApiRepoPrefix(RepoIdentity repo) => $"/repos/{repo.OrgOrOwner}/{repo.RepoName}";

    /// <summary>ADO git remote path: <c>/{org}/{project}/_git/{repo}</c>.</summary>
    private static string AdoGitRepoPath(RepoIdentity repo) =>
        $"/{repo.OrgOrOwner}/{repo.Project}/_git/{repo.RepoName}";

    /// <summary>ADO REST repo route prefix: <c>/{org}/{project}/_apis/git/repositories/{repo}</c>.</summary>
    private static string AdoApiRepoPrefix(RepoIdentity repo) =>
        $"/{repo.OrgOrOwner}/{repo.Project}/_apis/git/repositories/{repo.RepoName}";

    /// <summary>
    /// The ADO REST route root <see cref="Orchestration.AdoWorkItemContextReader"/> reads a PR's linked work
    /// items and their ancestry from. Empty when the repo carries no project, since without one there is no
    /// route to name.
    /// <para>
    /// EXACTLY ONE root, and the count is the point. The other half of that reader's traffic — the PR's own
    /// list of linked items at <c>_apis/git/repositories/{repo}/pullRequests/{id}/workitems</c> — already sits
    /// under <see cref="AdoApiRepoPrefix"/> and needed no widening at all; only <c>_apis/wit/workitems</c> is
    /// project-scoped, because ADO keys work items to a PROJECT and not to the repository a PR happens to live
    /// in. Naming the git route here as well would have granted nothing and implied the surface was twice its
    /// actual size.
    /// </para>
    /// <para>
    /// The root covers the batch form the reader calls (<c>?ids=…&amp;$expand=relations</c>) and, as a
    /// directory boundary, the per-item form under it — but NOT <c>_apis/wit/wiql</c>, <c>_apis/wit/queries</c>
    /// or any other <c>wit</c> sibling: <see cref="OperationPolicy"/> matches a root, so <c>workitemtypes</c>
    /// is outside it. It is reachable only through the read-only
    /// <see cref="SandboxOperation.ReadProviderMetadata"/> arm, so this grants no way to create, update or
    /// comment on a work item.
    /// </para>
    /// </summary>
    private static IReadOnlyList<string> AdoApiWorkItemPaths(RepoIdentity repo) =>
        string.IsNullOrEmpty(repo.Project) ? [] : [$"/{repo.OrgOrOwner}/{repo.Project}/_apis/wit/workitems"];

    /// <summary>
    /// Parses the configured ReviewBot remote into a (host, repo-path) the push policy matches against.
    /// A missing/unparseable URL yields a host that cannot match (so push is denied — there is no
    /// legitimate push destination).
    /// </summary>
    private static (string Host, string RepoPath) ParseReviewBotRemote(string? reviewBotRepoUrl, string targetHost)
    {
        if (string.IsNullOrWhiteSpace(reviewBotRepoUrl))
        {
            // No remote: a host that never matches a real request keeps push fail-closed.
            return ("no-reviewbot-remote", "/no-reviewbot-remote");
        }

        var parsed = GitRemoteUrl.Parse(reviewBotRepoUrl);
        return string.IsNullOrEmpty(parsed.Host) || string.IsNullOrEmpty(parsed.RepoPath)
            ? ("no-reviewbot-remote", "/no-reviewbot-remote")
            : (parsed.Host, parsed.RepoPath);
    }
}
