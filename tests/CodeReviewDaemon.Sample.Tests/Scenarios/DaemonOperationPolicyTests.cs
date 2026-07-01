using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Workspace;

namespace CodeReviewDaemon.Sample.Tests.Scenarios;

/// <summary>
/// PR #121 H2 — the per-run <see cref="OperationPolicy"/> builder. Instead of the old hard-coded
/// <c>TargetRepoPath = "/"</c>, a run's policy is scoped to exactly the repos that one review legitimately
/// touches: the target repo path is derived from the run's <see cref="RepoIdentity"/>, the ReviewBot
/// host/path from the configured <c>ReviewBotRepoUrl</c>, and the provider API path prefix from the repo.
/// Provider-API operations validate the expected repo route (not just host + method), so a review of
/// untrusted PR code can never coax the daemon into hitting an <i>off-repo</i> API path with the bot
/// credential.
/// </summary>
public sealed class DaemonOperationPolicyTests
{
    private static readonly RepoIdentity GitHubRepo = new()
    {
        Provider = "github",
        OrgOrOwner = "acme",
        RepoName = "widgets",
        RepoStableId = "R_node_1",
    };

    private static readonly RepoIdentity AdoRepo = new()
    {
        Provider = "azure-devops",
        OrgOrOwner = "contoso",
        Project = "Platform",
        RepoName = "core",
    };

    [Fact]
    public void GitHub_run_policy_scopes_the_target_repo_path_to_the_run_repo()
    {
        var policy = DaemonOperationPolicy.BuildForRun(
            GitHubRepo, reviewBotRepoUrl: "https://github.com/acme/reviewbot.git");

        // The target fetch must be confined to the run's repo — not a sibling under the same host.
        Fetch(policy, SandboxOperation.FetchTarget, "github.com", "/acme/widgets.git/info/refs?service=git-upload-pack")
            .IsAllowed.Should().BeTrue();
        Fetch(policy, SandboxOperation.FetchTarget, "github.com", "/acme/other-repo.git/info/refs?service=git-upload-pack")
            .IsAllowed.Should().BeFalse("the policy is scoped to the run repo, not the whole host");
    }

    [Fact]
    public void GitHub_run_policy_validates_the_api_repo_route_not_just_host_and_method()
    {
        var policy = DaemonOperationPolicy.BuildForRun(
            GitHubRepo, reviewBotRepoUrl: "https://github.com/acme/reviewbot.git");

        // A metadata GET on the run's own repo route is allowed.
        Api(policy, SandboxOperation.ReadProviderMetadata, "api.github.com", "GET", "/repos/acme/widgets/pulls?state=open")
            .IsAllowed.Should().BeTrue();
        // The same method + host but a DIFFERENT repo route is denied (off-repo with the bot credential).
        Api(policy, SandboxOperation.ReadProviderMetadata, "api.github.com", "GET", "/repos/acme/secret-repo/pulls")
            .IsAllowed.Should().BeFalse("provider-API ops must be scoped to the run's repo route");
    }

    [Fact]
    public void GitHub_run_policy_scopes_the_reviewbot_push_to_the_configured_remote()
    {
        var policy = DaemonOperationPolicy.BuildForRun(
            GitHubRepo, reviewBotRepoUrl: "https://github.com/acme/reviewbot.git");

        Receive(policy, "github.com", "/acme/reviewbot.git/git-receive-pack").IsAllowed.Should().BeTrue();
        Receive(policy, "github.com", "/acme/widgets.git/git-receive-pack")
            .IsAllowed.Should().BeFalse("push is confined to the ReviewBot remote, not the target");
    }

    [Fact]
    public void Ado_run_policy_scopes_the_api_route_to_the_project_repo()
    {
        var policy = DaemonOperationPolicy.BuildForRun(
            AdoRepo, reviewBotRepoUrl: "https://dev.azure.com/contoso/Platform/_git/reviewbot");

        Api(policy, SandboxOperation.ReadProviderMetadata, "dev.azure.com", "GET",
                "/contoso/Platform/_apis/git/repositories/core/pullrequests?searchCriteria.status=active")
            .IsAllowed.Should().BeTrue();
        Api(policy, SandboxOperation.ReadProviderMetadata, "dev.azure.com", "GET",
                "/contoso/Platform/_apis/git/repositories/other/pullrequests")
            .IsAllowed.Should().BeFalse("the ADO api route is scoped to the run's repository");
    }

    [Fact]
    public void A_collect_only_run_policy_denies_writes_regardless_of_route()
    {
        var policy = DaemonOperationPolicy.BuildForRun(
            GitHubRepo,
            reviewBotRepoUrl: "https://github.com/acme/reviewbot.git",
            allowWriteOperations: false);

        Api(policy, SandboxOperation.PostReviewComment, "api.github.com", "POST", "/repos/acme/widgets/issues/7/comments")
            .IsAllowed.Should().BeFalse("a collect-only (B) variant has no post capability");
        Receive(policy, "github.com", "/acme/reviewbot.git/git-receive-pack")
            .IsAllowed.Should().BeFalse("a collect-only (B) variant has no push capability");
    }

    [Fact]
    public void Without_a_reviewbot_url_push_is_denied_but_fetch_and_metadata_work()
    {
        var policy = DaemonOperationPolicy.BuildForRun(GitHubRepo, reviewBotRepoUrl: null);

        Fetch(policy, SandboxOperation.FetchTarget, "github.com", "/acme/widgets.git/info/refs?service=git-upload-pack")
            .IsAllowed.Should().BeTrue();
        Receive(policy, "github.com", "/acme/reviewbot.git/git-receive-pack")
            .IsAllowed.Should().BeFalse("no ReviewBot remote is configured, so push has no destination");
    }

    private static PolicyDecision Fetch(OperationPolicy policy, SandboxOperation op, string host, string path) =>
        policy.Decide(new OperationRequest(op, "github", host, "GET", path));

    private static PolicyDecision Api(OperationPolicy policy, SandboxOperation op, string host, string method, string path) =>
        policy.Decide(new OperationRequest(op, "github", host, method, path));

    private static PolicyDecision Receive(OperationPolicy policy, string host, string path) =>
        policy.Decide(new OperationRequest(SandboxOperation.PushReviewBot, "github", host, "POST", path));
}
