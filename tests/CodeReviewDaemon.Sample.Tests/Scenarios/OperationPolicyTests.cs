using System.Net;
using System.Text;
using CodeReviewDaemon.Sample.Orchestration;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using CodeReviewDaemon.Sample.Workspace;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeReviewDaemon.Sample.Tests.Scenarios;

/// <summary>
/// P3.1 — the <see cref="OperationPolicy"/> is the single fail-closed source of truth shared by the
/// sandbox network rules and the webhook token resolver (plan §4). These tests pin the allow/deny
/// matrix in both directions: legitimate operations are permitted on exactly their scoped repos, and
/// every cross-repo / wrong-service / off-allow-list / malicious-path variant is denied — with
/// credential injection mirroring the deny so a blocked request can never leak a credential.
/// </summary>
public sealed class OperationPolicyTests
{
    private static OperationPolicy CreatePolicy(bool allowWriteOperations = true) =>
        new(
            new ReviewScope(
                Provider: "github",
                TargetHost: "github.com",
                TargetRepoPath: "/acme/widgets",
                ForkHost: "github.com",
                ForkRepoPath: "/contributor/widgets",
                ReviewBotHost: "github.com",
                ReviewBotRepoPath: "/acme/reviewbot",
                ApiHost: "api.github.com",
                AllowedSubmodules: [new SubmoduleAllowRule("github.com", "/acme/shared-lib")]
            ),
            allowWriteOperations
        );

    [Fact]
    public void FetchTarget_allows_upload_pack_on_the_target_repo()
    {
        var policy = CreatePolicy();

        var advertise = policy.Decide(
            new OperationRequest(
                SandboxOperation.FetchTarget,
                "github",
                "github.com",
                "GET",
                "/acme/widgets.git/info/refs?service=git-upload-pack"
            )
        );
        var negotiate = policy.Decide(
            new OperationRequest(
                SandboxOperation.FetchTarget,
                "github",
                "github.com",
                "POST",
                "/acme/widgets.git/git-upload-pack"
            )
        );

        advertise.IsAllowed.Should().BeTrue();
        negotiate.IsAllowed.Should().BeTrue();
    }

    [Fact]
    public void FetchTarget_denies_push_service_on_the_target_repo()
    {
        var policy = CreatePolicy();

        var decision = policy.Decide(
            new OperationRequest(
                SandboxOperation.FetchTarget,
                "github",
                "github.com",
                "POST",
                "/acme/widgets.git/git-receive-pack"
            )
        );

        decision.IsAllowed.Should().BeFalse("the target repo is read-only — no push");
    }

    [Fact]
    public void FetchTarget_denies_a_different_host()
    {
        var policy = CreatePolicy();

        var decision = policy.Decide(
            new OperationRequest(
                SandboxOperation.FetchTarget,
                "github",
                "evil.example.com",
                "GET",
                "/acme/widgets.git/info/refs?service=git-upload-pack"
            )
        );

        decision.IsAllowed.Should().BeFalse();
    }

    [Fact]
    public void FetchTarget_denies_a_sibling_repo_sharing_a_name_prefix()
    {
        var policy = CreatePolicy();

        // "/acme/widgets-secrets" must not match because "/acme/widgets" is a prefix of it.
        var decision = policy.Decide(
            new OperationRequest(
                SandboxOperation.FetchTarget,
                "github",
                "github.com",
                "GET",
                "/acme/widgets-secrets.git/info/refs?service=git-upload-pack"
            )
        );

        decision.IsAllowed.Should().BeFalse();
    }

    [Fact]
    public void FetchTarget_denies_path_traversal()
    {
        var policy = CreatePolicy();

        var decision = policy.Decide(
            new OperationRequest(
                SandboxOperation.FetchTarget,
                "github",
                "github.com",
                "GET",
                "/acme/widgets.git/../../acme/reviewbot.git/info/refs?service=git-upload-pack"
            )
        );

        decision.IsAllowed.Should().BeFalse();
    }

    [Fact]
    public void PushReviewBot_allows_receive_pack_only_on_the_reviewbot_repo()
    {
        var policy = CreatePolicy();

        var allowed = policy.Decide(
            new OperationRequest(
                SandboxOperation.PushReviewBot,
                "github",
                "github.com",
                "POST",
                "/acme/reviewbot.git/git-receive-pack"
            )
        );
        var deniedFetchService = policy.Decide(
            new OperationRequest(
                SandboxOperation.PushReviewBot,
                "github",
                "github.com",
                "POST",
                "/acme/reviewbot.git/git-upload-pack"
            )
        );

        allowed.IsAllowed.Should().BeTrue();
        deniedFetchService.IsAllowed.Should().BeFalse();
    }

    [Fact]
    public void PushReviewBot_denies_pushing_to_the_target_repo()
    {
        var policy = CreatePolicy();

        var decision = policy.Decide(
            new OperationRequest(
                SandboxOperation.PushReviewBot,
                "github",
                "github.com",
                "POST",
                "/acme/widgets.git/git-receive-pack"
            )
        );

        decision.IsAllowed.Should().BeFalse("the daemon must never push to the repo under review");
    }

    [Fact]
    public void FetchForkHead_allows_the_fork_remote_but_denies_push()
    {
        var policy = CreatePolicy();

        var fetch = policy.Decide(
            new OperationRequest(
                SandboxOperation.FetchForkHead,
                "github",
                "github.com",
                "POST",
                "/contributor/widgets.git/git-upload-pack"
            )
        );
        var push = policy.Decide(
            new OperationRequest(
                SandboxOperation.FetchForkHead,
                "github",
                "github.com",
                "POST",
                "/contributor/widgets.git/git-receive-pack"
            )
        );

        fetch.IsAllowed.Should().BeTrue();
        push.IsAllowed.Should().BeFalse();
    }

    [Fact]
    public void FetchSubmodule_allows_allow_listed_and_denies_everything_else()
    {
        var policy = CreatePolicy();

        var allowed = policy.Decide(
            new OperationRequest(
                SandboxOperation.FetchSubmodule,
                "github",
                "github.com",
                "GET",
                "/acme/shared-lib.git/info/refs?service=git-upload-pack"
            )
        );
        var denied = policy.Decide(
            new OperationRequest(
                SandboxOperation.FetchSubmodule,
                "github",
                "github.com",
                "GET",
                "/random/private.git/info/refs?service=git-upload-pack"
            )
        );

        allowed.IsAllowed.Should().BeTrue();
        denied.IsAllowed.Should().BeFalse();
    }

    [Fact]
    public void PostReviewComment_requires_the_api_host_and_post()
    {
        var policy = CreatePolicy();

        var ok = policy.Decide(
            new OperationRequest(
                SandboxOperation.PostReviewComment,
                "github",
                "api.github.com",
                "POST",
                "/repos/acme/widgets/pulls/7/comments"
            )
        );
        var wrongMethod = policy.Decide(
            new OperationRequest(
                SandboxOperation.PostReviewComment,
                "github",
                "api.github.com",
                "GET",
                "/repos/acme/widgets/pulls/7/comments"
            )
        );
        var wrongHost = policy.Decide(
            new OperationRequest(
                SandboxOperation.PostReviewComment,
                "github",
                "github.com",
                "POST",
                "/repos/acme/widgets/pulls/7/comments"
            )
        );

        ok.IsAllowed.Should().BeTrue();
        wrongMethod.IsAllowed.Should().BeFalse();
        wrongHost.IsAllowed.Should().BeFalse();
    }

    [Fact]
    public void CollectOnlyVariant_hard_denies_push_and_post_on_their_own_scoped_repos()
    {
        // P4.2 — the A/B comparison (B) variant runs under a collect-only policy. Even the operations
        // that the primary variant is legitimately allowed (push to the ReviewBot repo, post to the API
        // host) are HARD-denied here: the capability is withheld before host/path is ever considered.
        var collectOnly = CreatePolicy(allowWriteOperations: false);

        var push = collectOnly.Decide(
            new OperationRequest(
                SandboxOperation.PushReviewBot,
                "github",
                "github.com",
                "POST",
                "/acme/reviewbot.git/git-receive-pack"
            )
        );
        var post = collectOnly.Decide(
            new OperationRequest(
                SandboxOperation.PostReviewComment,
                "github",
                "api.github.com",
                "POST",
                "/repos/acme/widgets/pulls/7/comments"
            )
        );

        push.IsAllowed.Should().BeFalse("a collect-only B variant has no push capability");
        post.IsAllowed.Should().BeFalse("a collect-only B variant has no post capability");
    }

    [Fact]
    public void CollectOnlyVariant_is_never_handed_a_write_credential()
    {
        // The credential decision mirrors the deny, so the B variant is also never injected with a
        // push/post token (fail closed both ways) — there is no token for it to misuse.
        var collectOnly = CreatePolicy(allowWriteOperations: false);

        collectOnly
            .ShouldInjectCredential(
                new OperationRequest(
                    SandboxOperation.PushReviewBot,
                    "github",
                    "github.com",
                    "POST",
                    "/acme/reviewbot.git/git-receive-pack"
                )
            )
            .Should()
            .BeFalse();
        collectOnly
            .ShouldInjectCredential(
                new OperationRequest(
                    SandboxOperation.PostReviewComment,
                    "github",
                    "api.github.com",
                    "POST",
                    "/repos/acme/widgets/pulls/7/comments"
                )
            )
            .Should()
            .BeFalse();
    }

    [Fact]
    public void CollectOnlyVariant_still_allows_read_only_fetches()
    {
        // Collect-only removes WRITE capability only — the B variant must still fetch the code to review.
        var collectOnly = CreatePolicy(allowWriteOperations: false);

        var fetch = collectOnly.Decide(
            new OperationRequest(
                SandboxOperation.FetchTarget,
                "github",
                "github.com",
                "GET",
                "/acme/widgets.git/info/refs?service=git-upload-pack"
            )
        );

        fetch.IsAllowed.Should().BeTrue("fetching the target repo is read-only, not a write operation");
    }

    [Fact]
    public void CollectOnlyVariant_still_denies_a_non_graphql_post_to_provider_metadata()
    {
        // Regression pin for the carve-out added below: a collect-only policy must still hard-deny a
        // plain (non-GraphQL) POST to the API host under ReadProviderMetadata — the exception is for the
        // one GraphQL route, not for "any POST classified as ReadProviderMetadata".
        var collectOnly = CreatePolicy(allowWriteOperations: false);

        var decision = collectOnly.Decide(
            new OperationRequest(
                SandboxOperation.ReadProviderMetadata,
                "github",
                "api.github.com",
                "POST",
                "/repos/acme/widgets/pulls/7"
            )
        );

        decision.IsAllowed.Should().BeFalse("only the GraphQL metadata route is carved out, not every POST");
    }

    [Fact]
    public void CollectOnlyVariant_allows_graphql_post_for_reading_provider_metadata()
    {
        // Issue #647 — GitHubIssueContextReader is the first GraphQL consumer, and GraphQL reads are
        // POSTs by protocol. A collect-only (B) variant must still be able to run this READ, or issue
        // #647 becomes unavailable in exactly the run where the daemon most needs a second opinion.
        var collectOnly = CreatePolicy(allowWriteOperations: false);

        var decision = collectOnly.Decide(
            new OperationRequest(SandboxOperation.ReadProviderMetadata, "github", "api.github.com", "POST", "/graphql")
        );

        decision.IsAllowed.Should().BeTrue("reading linked issues over GraphQL is a read, not a write");
    }

    [Fact]
    public void PostReviewComment_does_not_inherit_the_graphql_carve_out()
    {
        // The carve-out is keyed to ReadProviderMetadata's own arm, not to "POST /graphql on the API
        // host" in the abstract — a write operation must not be able to route through it.
        var collectOnly = CreatePolicy(allowWriteOperations: false);

        var decision = collectOnly.Decide(
            new OperationRequest(SandboxOperation.PostReviewComment, "github", "api.github.com", "POST", "/graphql")
        );

        decision.IsAllowed.Should().BeFalse("PostReviewComment has no write capability in a collect-only policy");
    }

    [Fact]
    public void ReadProviderMetadata_denies_post_to_a_non_graphql_path_even_with_write_capability()
    {
        // Even with write capability granted, ReadProviderMetadata's arm must still reject a POST to
        // anything other than the GraphQL path — it is still declared a GET-only operation apart from
        // that one carved-out route.
        var policy = CreatePolicy();

        var decision = policy.Decide(
            new OperationRequest(
                SandboxOperation.ReadProviderMetadata,
                "github",
                "api.github.com",
                "POST",
                "/repos/acme/widgets/pulls/7"
            )
        );

        decision.IsAllowed.Should().BeFalse("only the /graphql route is carved out of the GET-only rule");
    }

    [Fact]
    public void ShouldInjectCredential_mirrors_the_deny_decision()
    {
        var policy = CreatePolicy();

        // A denied push to the target must ALSO withhold the credential (fail closed both ways).
        var deniedRequest = new OperationRequest(
            SandboxOperation.PushReviewBot,
            "github",
            "github.com",
            "POST",
            "/acme/widgets.git/git-receive-pack"
        );
        var allowedRequest = new OperationRequest(
            SandboxOperation.PushReviewBot,
            "github",
            "github.com",
            "POST",
            "/acme/reviewbot.git/git-receive-pack"
        );

        policy.ShouldInjectCredential(deniedRequest).Should().BeFalse();
        policy.ShouldInjectCredential(allowedRequest).Should().BeTrue();
    }

    [Fact]
    public void CollectOnlyVariant_denies_an_ado_tagged_request_shaped_like_the_graphql_carve_out()
    {
        // Same host/method/path as the legitimate carve-out (CreatePolicy's own ApiHost, "api.github.com",
        // POST, "/graphql"), but tagged with a different provider. The carve-out is GitHub-only — a
        // same-shaped request from another provider must not ride through on host/path/method alone.
        var collectOnly = CreatePolicy(allowWriteOperations: false);

        var decision = collectOnly.Decide(
            new OperationRequest(
                SandboxOperation.ReadProviderMetadata,
                "azure-devops",
                "api.github.com",
                "POST",
                "/graphql"
            )
        );

        decision
            .IsAllowed.Should()
            .BeFalse("the GraphQL carve-out is GitHub-only, not any provider matching the shape");
    }

    [Fact]
    public void ReadProviderMetadata_denies_a_graphql_carve_out_request_aimed_at_a_mismatched_host()
    {
        // Same provider/method/path as the legitimate carve-out (github, POST, "/graphql") but a host that
        // is NOT CreatePolicy's own ApiHost ("api.github.com") — the carve-out must key off the run's
        // scoped API host, not off "github provider + graphql path" alone, or a compromised/misdirected
        // request could ride the carve-out to an attacker-controlled endpoint. Write capability is left ON
        // so the request reaches ReadProviderMetadata's own host check instead of being pre-empted by the
        // collect-only write gate — isolating the host predicate this test exists to pin.
        var policy = CreatePolicy();
        var mismatchedHostRequest = new OperationRequest(
            SandboxOperation.ReadProviderMetadata,
            "github",
            "evil.example.com",
            "POST",
            "/graphql"
        );

        var decision = policy.Decide(mismatchedHostRequest);

        decision.IsAllowed.Should().BeFalse("the carve-out is scoped to the run's own API host");
        decision
            .Reason.Should()
            .Contain("api.github.com", "the denial should name the host the carve-out actually requires");

        // Fail-closed both ways (plan §4): a request the carve-out denies must never get a credential either.
        policy
            .ShouldInjectCredential(mismatchedHostRequest)
            .Should()
            .BeFalse("a denied request must never receive a credential");
    }

    [Fact]
    public async Task RealPolicy_allows_the_exact_request_GitHubIssueContextReader_emits_for_a_collect_only_run()
    {
        // Issue #647 Section C — proves the ACTUAL request GitHubIssueContextReader emits (not a
        // hand-built stand-in) is accepted by the real OperationPolicy for a collect-only (B) variant. A
        // mutation to the operation tag WithOperation passes, or to the GraphQL URL's path, must each turn
        // this test red.
        HttpRequestMessage? captured = null;
        var handler = new FakeHttpMessageHandler().On(
            req => req.Method == HttpMethod.Post && req.RequestUri is not null,
            req =>
            {
                captured = req;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"data":{"repository":{"pullRequest":{"closingIssuesReferences":{"pageInfo":{"hasNextPage":false,"endCursor":null},"nodes":[]}}}}}""",
                        Encoding.UTF8,
                        "application/json"
                    ),
                };
            }
        );
        var reader = new GitHubIssueContextReader(
            new HttpClient(handler),
            new FakeOAuthTokenProvider("github", "gh-token-xyz"),
            NullLogger<GitHubIssueContextReader>.Instance
        );
        var repo = new RepoIdentity
        {
            Provider = "github",
            OrgOrOwner = "acme",
            RepoName = "widgets",
            RepoStableId = "R_node_123",
        };

        await reader.ReadAsync(repo, "7", CancellationToken.None);

        captured.Should().NotBeNull("the reader must have made its one GraphQL request");
        var operation = captured!.GetOperation();
        operation.Should().Be(SandboxOperation.ReadProviderMetadata);

        // Built the same way OperationPolicyHandler.SendAsync builds it from a real outgoing request —
        // reusing the existing policy rather than a second, duplicate policy client.
        var operationRequest = new OperationRequest(
            operation!.Value,
            "github",
            captured!.RequestUri!.Host,
            captured.Method.Method,
            captured.RequestUri.PathAndQuery
        );

        var collectOnly = CreatePolicy(allowWriteOperations: false);
        var decision = collectOnly.Decide(operationRequest);

        decision
            .IsAllowed.Should()
            .BeTrue("the reader's own GraphQL read must remain reachable under a collect-only (B) variant");
        collectOnly.ShouldInjectCredential(operationRequest).Should().BeTrue();
    }
}
