using CodeReviewDaemon.Sample.Orchestration;
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
            GitHubRepo,
            reviewBotRepoUrl: "https://github.com/acme/reviewbot.git"
        );

        // The target fetch must be confined to the run's repo — not a sibling under the same host.
        Fetch(policy, SandboxOperation.FetchTarget, "github.com", "/acme/widgets.git/info/refs?service=git-upload-pack")
            .IsAllowed.Should()
            .BeTrue();
        Fetch(
            policy,
            SandboxOperation.FetchTarget,
            "github.com",
            "/acme/other-repo.git/info/refs?service=git-upload-pack"
        )
            .IsAllowed.Should()
            .BeFalse("the policy is scoped to the run repo, not the whole host");
    }

    [Fact]
    public void GitHub_run_policy_validates_the_api_repo_route_not_just_host_and_method()
    {
        var policy = DaemonOperationPolicy.BuildForRun(
            GitHubRepo,
            reviewBotRepoUrl: "https://github.com/acme/reviewbot.git"
        );

        // A metadata GET on the run's own repo route is allowed.
        Api(
            policy,
            SandboxOperation.ReadProviderMetadata,
            "api.github.com",
            "GET",
            "/repos/acme/widgets/pulls?state=open"
        )
            .IsAllowed.Should()
            .BeTrue();
        // The same method + host but a DIFFERENT repo route is denied (off-repo with the bot credential).
        Api(policy, SandboxOperation.ReadProviderMetadata, "api.github.com", "GET", "/repos/acme/secret-repo/pulls")
            .IsAllowed.Should()
            .BeFalse("provider-API ops must be scoped to the run's repo route");
    }

    [Fact]
    public void GitHub_run_policy_scopes_the_reviewbot_push_to_the_configured_remote()
    {
        // The grant is explicit because the parameter defaults to false since #536 — this case is about
        // WHERE a write-capable policy may push, so it has to be handed the capability to have a question.
        var policy = DaemonOperationPolicy.BuildForRun(
            GitHubRepo,
            reviewBotRepoUrl: "https://github.com/acme/reviewbot.git",
            allowWriteOperations: true
        );

        Receive(policy, "github.com", "/acme/reviewbot.git/git-receive-pack").IsAllowed.Should().BeTrue();
        Receive(policy, "github.com", "/acme/widgets.git/git-receive-pack")
            .IsAllowed.Should()
            .BeFalse("push is confined to the ReviewBot remote, not the target");
    }

    [Fact]
    public void Ado_run_policy_scopes_the_api_route_to_the_project_repo()
    {
        var policy = DaemonOperationPolicy.BuildForRun(
            AdoRepo,
            reviewBotRepoUrl: "https://dev.azure.com/contoso/Platform/_git/reviewbot"
        );

        Api(
            policy,
            SandboxOperation.ReadProviderMetadata,
            "dev.azure.com",
            "GET",
            "/contoso/Platform/_apis/git/repositories/core/pullrequests?searchCriteria.status=active"
        )
            .IsAllowed.Should()
            .BeTrue();
        Api(
            policy,
            SandboxOperation.ReadProviderMetadata,
            "dev.azure.com",
            "GET",
            "/contoso/Platform/_apis/git/repositories/other/pullrequests"
        )
            .IsAllowed.Should()
            .BeFalse("the ADO api route is scoped to the run's repository");
    }

    /// <summary>
    /// The work-item routes <c>AdoWorkItemContextReader</c> needs are reachable read-only. Only ONE root had
    /// to be added: ADO keys work items to a PROJECT, so <c>_apis/wit/workitems</c> cannot sit under the
    /// repository route, while the PR's own list of linked items already does and needed no widening at all.
    /// <para>
    /// Before this the reviewer could not judge whether a diff did what was asked, and the cause was
    /// structural rather than a model choice: the capability was offered in the PROMPT while across 644
    /// observed review sub-agent spawns ZERO carried a tool that could reach ADO.
    /// </para>
    /// </summary>
    [Fact]
    public void Ado_run_policy_allows_reading_the_runs_own_work_item_routes()
    {
        var policy = DaemonOperationPolicy.BuildForRun(
            AdoRepo,
            reviewBotRepoUrl: "https://dev.azure.com/contoso/Platform/_git/reviewbot"
        );

        Api(
            policy,
            SandboxOperation.ReadProviderMetadata,
            "dev.azure.com",
            "GET",
            "/contoso/Platform/_apis/wit/workitems?ids=1234&$expand=relations&api-version=7.1"
        )
            .IsAllowed.Should()
            .BeTrue("the batch read is what walks the chain up to the Epic");
        Api(
            policy,
            SandboxOperation.ReadProviderMetadata,
            "dev.azure.com",
            "GET",
            "/contoso/Platform/_apis/git/repositories/core/pullRequests/5505458/workitems?api-version=7.1"
        )
            .IsAllowed.Should()
            .BeTrue("the PR's own links were already under the run's repo route");
        Api(
            policy,
            SandboxOperation.ReadProviderMetadata,
            "dev.azure.com",
            "GET",
            "/contoso/Platform/_apis/git/repositories/other/pullRequests/1/workitems?api-version=7.1"
        )
            .IsAllowed.Should()
            .BeFalse("and being already in scope does not mean unscoped — a sibling repo's links stay denied");
    }

    /// <summary>
    /// The exception names ONE route root, not the project's whole <c>_apis</c> surface. A read no reader
    /// makes — a saved query, arbitrary WIQL search, the work-item TYPE metadata, another repo's blobs — is
    /// still outside the run's route, so widening a reader later stays a deliberate edit in
    /// <c>DaemonOperationPolicy</c> rather than something already granted.
    /// </summary>
    [Fact]
    public void Ado_run_policy_does_not_open_the_projects_whole_api_surface()
    {
        var policy = DaemonOperationPolicy.BuildForRun(
            AdoRepo,
            reviewBotRepoUrl: "https://dev.azure.com/contoso/Platform/_git/reviewbot"
        );

        Api(
            policy,
            SandboxOperation.ReadProviderMetadata,
            "dev.azure.com",
            "GET",
            "/contoso/Platform/_apis/wit/wiql?api-version=7.1"
        )
            .IsAllowed.Should()
            .BeFalse("a WIQL query is arbitrary search, not this PR's own items");
        Api(
            policy,
            SandboxOperation.ReadProviderMetadata,
            "dev.azure.com",
            "GET",
            "/contoso/Platform/_apis/wit/workitemtypes?api-version=7.1"
        )
            .IsAllowed.Should()
            .BeFalse("a sibling whose name merely starts with the granted root is outside it");
        Api(
            policy,
            SandboxOperation.ReadProviderMetadata,
            "dev.azure.com",
            "GET",
            "/contoso/Platform/_apis/git/repositories/other/items?path=/secrets.txt"
        )
            .IsAllowed.Should()
            .BeFalse();
    }

    /// <summary>
    /// The work-item exception is honoured for exactly one operation and exactly one project. Both halves
    /// matter: the confinement exists so untrusted PR code cannot steer the daemon somewhere else carrying
    /// the bot credential, and a route that widened either the method or the project would hand that back.
    /// </summary>
    [Fact]
    public void Ado_run_policy_denies_the_work_item_routes_to_writes_and_to_other_projects()
    {
        // Write-capable ON PURPOSE: the question here is whether the READ-ONLY route exception leaks into the
        // write arm. A collect-only policy would deny on the capability check and never reach DecideApi, so
        // the POST assertion would pass regardless of how the exception is gated — the vacuous shape.
        var policy = DaemonOperationPolicy.BuildForRun(
            AdoRepo,
            reviewBotRepoUrl: "https://dev.azure.com/contoso/Platform/_git/reviewbot",
            allowWriteOperations: true
        );

        Api(
            policy,
            SandboxOperation.PostReviewComment,
            "dev.azure.com",
            "POST",
            "/contoso/Platform/_apis/wit/workitems/1234"
        )
            .IsAllowed.Should()
            .BeFalse(
                "the work-item routes are readable, never writable — no item can be created, updated or "
                    + "commented on through this"
            );
        Api(
            policy,
            SandboxOperation.ReadProviderMetadata,
            "dev.azure.com",
            "GET",
            "/contoso/OtherProject/_apis/wit/workitems?ids=1234&api-version=7.1"
        )
            .IsAllowed.Should()
            .BeFalse("only the run's own project is in scope");
        Api(
            policy,
            SandboxOperation.ReadProviderMetadata,
            "dev.azure.com",
            "GET",
            "/contoso/Platform-Secrets/_apis/wit/workitems?ids=1&api-version=7.1"
        )
            .IsAllowed.Should()
            .BeFalse("a project whose name merely starts with the run's is not the run's");
    }

    [Fact]
    public void GitHub_run_policy_has_no_work_item_routes()
    {
        // The work-item routes are an ADO shape. GitHub's linked issues hang off the repo route the policy
        // already scopes, so there is nothing to except and nothing is excepted.
        var policy = DaemonOperationPolicy.BuildForRun(
            GitHubRepo,
            reviewBotRepoUrl: "https://github.com/acme/reviewbot.git"
        );

        Api(policy, SandboxOperation.ReadProviderMetadata, "api.github.com", "GET", "/_apis/wit/workitems?ids=1")
            .IsAllowed.Should()
            .BeFalse();
    }

    [Fact]
    public void A_collect_only_run_policy_denies_writes_regardless_of_route()
    {
        var policy = DaemonOperationPolicy.BuildForRun(
            GitHubRepo,
            reviewBotRepoUrl: "https://github.com/acme/reviewbot.git",
            allowWriteOperations: false
        );

        Api(
            policy,
            SandboxOperation.PostReviewComment,
            "api.github.com",
            "POST",
            "/repos/acme/widgets/issues/7/comments"
        )
            .IsAllowed.Should()
            .BeFalse("a collect-only (B) variant has no post capability");
        Receive(policy, "github.com", "/acme/reviewbot.git/git-receive-pack")
            .IsAllowed.Should()
            .BeFalse("a collect-only (B) variant has no push capability");
    }

    [Fact]
    public void Without_a_reviewbot_url_push_is_denied_but_fetch_and_metadata_work()
    {
        // Write-capable ON PURPOSE. This case is about the ROUTE half of the push decision — that a missing
        // ReviewBot URL leaves ParseReviewBotRemote's unmatchable fallback host as the only destination. Since
        // #536 the capability check runs first, so a policy built without an explicit grant would deny on the
        // capability and never reach DecideReceivePack at all: the assertion below would pass no matter what
        // ParseReviewBotRemote did, which is the vacuous shape this comment exists to prevent recurring.
        var policy = DaemonOperationPolicy.BuildForRun(GitHubRepo, reviewBotRepoUrl: null, allowWriteOperations: true);

        Fetch(policy, SandboxOperation.FetchTarget, "github.com", "/acme/widgets.git/info/refs?service=git-upload-pack")
            .IsAllowed.Should()
            .BeTrue();
        Receive(policy, "github.com", "/acme/reviewbot.git/git-receive-pack")
            .IsAllowed.Should()
            .BeFalse("no ReviewBot remote is configured, so push has no destination");
    }

    [Fact]
    public void GitHub_run_policy_scopes_the_graphql_carve_out_to_the_run_repo()
    {
        // Issue #666 review — BuildForRun must thread the run's own owner/repo into
        // ReviewScope.GraphQlOwner/GraphQlRepo, or the GraphQL scope check has nothing of the run's OWN
        // repo to validate a request against and the carve-out degrades back to "byte-identical query
        // text, any owner/repo".
        var policy = DaemonOperationPolicy.BuildForRun(
            GitHubRepo,
            reviewBotRepoUrl: "https://github.com/acme/reviewbot.git",
            allowWriteOperations: false
        );
        var inScope = new GitHubGraphQlRequestScope("acme", "widgets", 7);
        var offScope = new GitHubGraphQlRequestScope("someone-else", "other-repo", 7);

        GraphQl(policy, inScope).IsAllowed.Should().BeTrue("the tag/body match the run's own repo");
        GraphQl(policy, offScope)
            .IsAllowed.Should()
            .BeFalse("a tag/body pair for a DIFFERENT owner/repo must not ride this run's carve-out");
    }

    [Fact]
    public void Ado_run_policy_has_no_graphql_scope_to_validate_against()
    {
        // ADO has no GraphQL carve-out (issue #647 is GitHub-only); BuildForRun leaves
        // GraphQlOwner/GraphQlRepo null for an ADO run, so even a same-shaped GraphQL POST at the run's own
        // API host can never validate — there is no owner/repo for the tag/body to agree with.
        var policy = DaemonOperationPolicy.BuildForRun(
            AdoRepo,
            reviewBotRepoUrl: "https://dev.azure.com/contoso/Platform/_git/reviewbot",
            allowWriteOperations: false
        );
        var scope = new GitHubGraphQlRequestScope("contoso", "core", 7);

        var decision = policy.Decide(
            new OperationRequest(
                SandboxOperation.ReadProviderMetadata,
                "github",
                "dev.azure.com",
                "POST",
                "/graphql",
                GitHubIssueContextReader.Query,
                scope
            )
        );

        decision.IsAllowed.Should().BeFalse("an ADO run has no GraphQL owner/repo scope to validate against");
    }

    private static PolicyDecision Fetch(OperationPolicy policy, SandboxOperation op, string host, string path) =>
        policy.Decide(new OperationRequest(op, "github", host, "GET", path));

    private static PolicyDecision Api(
        OperationPolicy policy,
        SandboxOperation op,
        string host,
        string method,
        string path
    ) => policy.Decide(new OperationRequest(op, "github", host, method, path));

    private static PolicyDecision Receive(OperationPolicy policy, string host, string path) =>
        policy.Decide(new OperationRequest(SandboxOperation.PushReviewBot, "github", host, "POST", path));

    private static PolicyDecision GraphQl(OperationPolicy policy, GitHubGraphQlRequestScope variables) =>
        policy.Decide(
            new OperationRequest(
                SandboxOperation.ReadProviderMetadata,
                "github",
                "api.github.com",
                "POST",
                "/graphql",
                GitHubIssueContextReader.Query,
                variables
            )
        );
}
