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

    /// <summary>
    /// The run's own PROJECT-metadata route is reachable read-only. ADO's PR-list payload omits
    /// <c>repository.project.visibility</c>, so the confidentiality trust signal can only be established
    /// from <c>GET /{org}/_apis/projects/{project}</c> — which is org-scoped and therefore falls outside
    /// the repo route prefix every other provider-API call is confined to. Without this the poller's own
    /// lookup is egress-blocked and the sibling gate stays shut for a reason no configuration can fix.
    /// <para>
    /// The exception is exactly one project (the run's) and exactly GET. A different project is still off
    /// the allow-list, and no WRITE reaches the route: the whole point of the repo-route confinement is
    /// that untrusted PR code cannot steer the daemon somewhere else with the bot credential, and an
    /// exception that widened the method or the project would give back what it protects.
    /// </para>
    /// </summary>
    [Fact]
    public void Ado_run_policy_allows_reading_only_the_runs_own_project_metadata()
    {
        var policy = DaemonOperationPolicy.BuildForRun(
            AdoRepo, reviewBotRepoUrl: "https://dev.azure.com/contoso/Platform/_git/reviewbot");

        Api(policy, SandboxOperation.ReadProviderMetadata, "dev.azure.com", "GET",
                "/contoso/_apis/projects/Platform?api-version=7.1")
            .IsAllowed.Should().BeTrue("the run's project visibility is the trust signal the gate depends on");
        Api(policy, SandboxOperation.ReadProviderMetadata, "dev.azure.com", "GET",
                "/contoso/_apis/projects/OtherProject?api-version=7.1")
            .IsAllowed.Should().BeFalse("only the run's own project is in scope");
        Api(policy, SandboxOperation.PostReviewComment, "dev.azure.com", "POST",
                "/contoso/_apis/projects/Platform")
            .IsAllowed.Should().BeFalse("the project route is readable, never writable");
    }

    /// <summary>
    /// The three CI routes <c>AdoCiStatusReader</c> needs are reachable read-only. Every one of them is
    /// PROJECT-scoped — ADO publishes a PR's build verdict, its test totals and the name of the failing test
    /// project under <c>/{org}/{project}/_apis/…</c>, never under the repository route — so before this
    /// exception existed all three were denied and the reviewer could not see CI at all. That is not
    /// hypothetical: PR 5505458's pipeline had 45,051 tests with 1 failure sitting in ADO while the review
    /// said nothing about it.
    /// </summary>
    [Fact]
    public void Ado_run_policy_allows_reading_the_runs_own_ci_status_routes()
    {
        var policy = DaemonOperationPolicy.BuildForRun(
            AdoRepo, reviewBotRepoUrl: "https://dev.azure.com/contoso/Platform/_git/reviewbot");

        Api(policy, SandboxOperation.ReadProviderMetadata, "dev.azure.com", "GET",
                "/contoso/Platform/_apis/policy/evaluations"
                    + "?artifactId=vstfs:///CodeReview/CodeReviewId/proj-guid/5505458&api-version=7.1-preview.1")
            .IsAllowed.Should().BeTrue("the policy evaluation is what names the PR's build at all");
        Api(policy, SandboxOperation.ReadProviderMetadata, "dev.azure.com", "GET",
                "/contoso/Platform/_apis/build/builds/39168345?api-version=7.1")
            .IsAllowed.Should().BeTrue();
        Api(policy, SandboxOperation.ReadProviderMetadata, "dev.azure.com", "GET",
                "/contoso/Platform/_apis/build/builds/39168345/timeline?api-version=7.1")
            .IsAllowed.Should().BeTrue("the timeline is the only place ADO names the failing test project");
        Api(policy, SandboxOperation.ReadProviderMetadata, "dev.azure.com", "GET",
                "/contoso/Platform/_apis/test/ResultSummaryByBuild?buildId=39168345&api-version=7.1-preview.1")
            .IsAllowed.Should().BeTrue();
    }

    /// <summary>
    /// The CI exception is honoured for exactly one operation and exactly one project. Both halves matter:
    /// the confinement exists so untrusted PR code cannot steer the daemon somewhere else carrying the bot
    /// credential, and a route that widened either the method or the project would hand that back.
    /// </summary>
    [Fact]
    public void Ado_run_policy_denies_the_ci_routes_to_writes_and_to_other_projects()
    {
        var policy = DaemonOperationPolicy.BuildForRun(
            AdoRepo, reviewBotRepoUrl: "https://dev.azure.com/contoso/Platform/_git/reviewbot");

        Api(policy, SandboxOperation.PostReviewComment, "dev.azure.com", "POST",
                "/contoso/Platform/_apis/build/builds/39168345")
            .IsAllowed.Should().BeFalse("the CI routes are readable, never writable");
        Api(policy, SandboxOperation.ReadProviderMetadata, "dev.azure.com", "GET",
                "/contoso/OtherProject/_apis/build/builds/39168345?api-version=7.1")
            .IsAllowed.Should().BeFalse("only the run's own project is in scope");
        Api(policy, SandboxOperation.ReadProviderMetadata, "dev.azure.com", "GET",
                "/contoso/OtherProject/_apis/test/ResultSummaryByBuild?buildId=1&api-version=7.1-preview.1")
            .IsAllowed.Should().BeFalse("only the run's own project is in scope");

        // A sibling project whose name merely STARTS with the run's must not slip through on the prefix.
        Api(policy, SandboxOperation.ReadProviderMetadata, "dev.azure.com", "GET",
                "/contoso/Platform-Secrets/_apis/build/builds/1?api-version=7.1")
            .IsAllowed.Should().BeFalse();
    }

    /// <summary>
    /// The exception names three route roots, not the project's whole <c>_apis</c> surface. A read the CI
    /// reader does not make — a work-item query, another repo's blobs — is still outside the run's route,
    /// so widening the reader later is a deliberate edit here rather than something already granted.
    /// </summary>
    [Fact]
    public void Ado_run_policy_does_not_open_the_projects_whole_api_surface()
    {
        var policy = DaemonOperationPolicy.BuildForRun(
            AdoRepo, reviewBotRepoUrl: "https://dev.azure.com/contoso/Platform/_git/reviewbot");

        Api(policy, SandboxOperation.ReadProviderMetadata, "dev.azure.com", "GET",
                "/contoso/Platform/_apis/wit/workitems?ids=1&api-version=7.1")
            .IsAllowed.Should().BeFalse();
        Api(policy, SandboxOperation.ReadProviderMetadata, "dev.azure.com", "GET",
                "/contoso/Platform/_apis/git/repositories/other/items?path=/secrets.txt")
            .IsAllowed.Should().BeFalse();
    }

    [Fact]
    public void GitHub_run_policy_has_no_ci_status_routes()
    {
        // The CI routes are an ADO shape. GitHub's checks hang off the repo route the policy already scopes,
        // so there is nothing to except and nothing is excepted.
        var policy = DaemonOperationPolicy.BuildForRun(
            GitHubRepo, reviewBotRepoUrl: "https://github.com/acme/reviewbot.git");

        Api(policy, SandboxOperation.ReadProviderMetadata, "api.github.com", "GET", "/repos/acme/widgets/check-runs")
            .IsAllowed.Should().BeTrue("that route is under the run's own repo prefix, not an exception");
        Api(policy, SandboxOperation.ReadProviderMetadata, "api.github.com", "GET", "/_apis/build/builds/1")
            .IsAllowed.Should().BeFalse();
    }

    [Fact]
    public void GitHub_run_policy_has_no_project_metadata_route()
    {
        // GitHub has no project layer, so there is nothing to except and the repo route stays the only way in.
        var policy = DaemonOperationPolicy.BuildForRun(
            GitHubRepo, reviewBotRepoUrl: "https://github.com/acme/reviewbot.git");

        Api(policy, SandboxOperation.ReadProviderMetadata, "api.github.com", "GET", "/orgs/acme")
            .IsAllowed.Should().BeFalse();
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
