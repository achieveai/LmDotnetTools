using System.Net;
using System.Text;
using CodeReviewDaemon.Sample.Configuration;
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
            )
            {
                GraphQlOwner = "acme",
                GraphQlRepo = "widgets",
            },
            allowWriteOperations
        );

    /// <summary>
    /// The scope this run's GraphQL requests must carry to be allowed: matches
    /// <see cref="CreatePolicy"/>'s <c>ReviewScope.GraphQlOwner</c>/<c>GraphQlRepo</c>. Passed as the parsed
    /// body (<c>GraphQlVariables</c>) wherever a test wants a request that satisfies the configured boundary.
    /// </summary>
    private static readonly GitHubGraphQlRequestScope ScopedTarget = new("acme", "widgets", 7);

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
        // Body must carry the one reviewed-safe document exactly — the carve-out is document-gated, not
        // shape-gated alone (issue #647 follow-up, MUST #1).
        var collectOnly = CreatePolicy(allowWriteOperations: false);

        var decision = collectOnly.Decide(
            new OperationRequest(
                SandboxOperation.ReadProviderMetadata,
                "github",
                "api.github.com",
                "POST",
                "/graphql",
                GitHubIssueContextReader.Query,
                ScopedTarget
            )
        );

        decision.IsAllowed.Should().BeTrue("reading linked issues over GraphQL is a read, not a write");
    }

    [Fact]
    public void CollectOnlyVariant_denies_graphql_post_whose_body_is_not_the_reviewed_safe_document()
    {
        // Issue #647 follow-up (MUST #1) — same provider/host/method/path as the legitimate carve-out, but
        // a body that is NOT the exact document GitHubIssueContextReader.Query defines. A same-shaped
        // mutation (or any other GraphQL document) must not ride the carve-out on transport shape alone.
        // ScopedTarget is supplied for BOTH the tag and the parsed variables (i.e. scope validation would
        // otherwise pass) so this test isolates the body-equality check on its own — a scope-check
        // regression could not turn it green.
        var collectOnly = CreatePolicy(allowWriteOperations: false);

        var decision = collectOnly.Decide(
            new OperationRequest(
                SandboxOperation.ReadProviderMetadata,
                "github",
                "api.github.com",
                "POST",
                "/graphql",
                "mutation { addComment(input: { body: \"pwned\" }) { clientMutationId } }",
                ScopedTarget
            )
        );

        decision
            .IsAllowed.Should()
            .BeFalse("only the one reviewed-safe query document is carved out, not any GraphQL body");
    }

    [Fact]
    public void CollectOnlyVariant_denies_graphql_post_carrying_a_hidden_mutation_alongside_the_safe_query()
    {
        // A document that starts with (or contains) the benign query text but smuggles a second, named
        // mutation operation alongside it must still be denied — an exact byte comparison against the one
        // reviewed-safe document is what defeats this, not a substring/prefix check. ScopedTarget matches
        // on both sides for the same isolation reason as the sibling test above.
        var collectOnly = CreatePolicy(allowWriteOperations: false);

        var decision = collectOnly.Decide(
            new OperationRequest(
                SandboxOperation.ReadProviderMetadata,
                "github",
                "api.github.com",
                "POST",
                "/graphql",
                GitHubIssueContextReader.Query
                    + "\nmutation Evil { addComment(input: { body: \"pwned\" }) { clientMutationId } }",
                ScopedTarget
            )
        );

        decision.IsAllowed.Should().BeFalse("a hidden mutation appended alongside the safe query must still be denied");
    }

    [Fact]
    public void CollectOnlyVariant_denies_graphql_post_with_no_body_captured()
    {
        // A GraphQL-shaped POST whose body could not be read/parsed (OperationRequest.Body left null) must
        // deny exactly like a wrong document — never treated as "shape matches, so allow". ScopedTarget is
        // supplied for both tag and variables so this isolates the null-body check itself.
        var collectOnly = CreatePolicy(allowWriteOperations: false);

        var decision = collectOnly.Decide(
            new OperationRequest(
                SandboxOperation.ReadProviderMetadata,
                "github",
                "api.github.com",
                "POST",
                "/graphql",
                Body: null,
                GraphQlVariables: ScopedTarget
            )
        );

        decision.IsAllowed.Should().BeFalse("an unreadable/absent body must fail closed, not fail open");
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
    public void CollectOnlyVariant_denies_graphql_post_whose_variables_could_not_be_parsed()
    {
        // The body's variables could not be parsed into a scope (handler-level parse failure surfaces here
        // as a null GraphQlVariables) — there is nothing to compare against this policy's own boundary, so
        // this must deny too.
        var collectOnly = CreatePolicy(allowWriteOperations: false);

        var decision = collectOnly.Decide(
            new OperationRequest(
                SandboxOperation.ReadProviderMetadata,
                "github",
                "api.github.com",
                "POST",
                "/graphql",
                GitHubIssueContextReader.Query,
                GraphQlVariables: null
            )
        );

        decision.IsAllowed.Should().BeFalse("unparseable body variables must fail closed, not fail open");
    }

    [Theory]
    [InlineData("evil", "widgets")]
    [InlineData("acme", "evil-repo")]
    public void CollectOnlyVariant_denies_graphql_post_whose_body_variables_are_outside_the_configured_repo(
        string owner,
        string repo
    )
    {
        // Issue #666 second review — pins the owner-vs-configured-scope and repo-vs-configured-scope
        // conjuncts of Decide's own boundary check, each varied alone so neither can mask the other's
        // mutation. The PR number this policy never checks (it has none of its own to compare against) —
        // that is OperationPolicyHandler's separate, mandatory, constructor-bound canonical-scope gate,
        // covered end-to-end by
        // OperationPolicyHandlerTests.Denies_a_graphql_post_whose_body_variables_target_a_different_pr_than_the_clients_canonical_scope.
        var collectOnly = CreatePolicy(allowWriteOperations: false);

        var decision = collectOnly.Decide(
            new OperationRequest(
                SandboxOperation.ReadProviderMetadata,
                "github",
                "api.github.com",
                "POST",
                "/graphql",
                GitHubIssueContextReader.Query,
                GraphQlVariables: new GitHubGraphQlRequestScope(owner, repo, 7)
            )
        );

        decision.IsAllowed.Should().BeFalse("body variables outside this policy's configured owner/repo must deny");
    }

    [Fact]
    public void CollectOnlyVariant_denies_graphql_post_whose_owner_differs_only_by_case_from_the_configured_scope()
    {
        // The comparison must be Ordinal, not case-insensitive: "ACME" is not "acme" even though GitHub
        // itself treats owner names case-insensitively at the API layer — this policy must not.
        var collectOnly = CreatePolicy(allowWriteOperations: false);

        var decision = collectOnly.Decide(
            new OperationRequest(
                SandboxOperation.ReadProviderMetadata,
                "github",
                "api.github.com",
                "POST",
                "/graphql",
                GitHubIssueContextReader.Query,
                GraphQlVariables: new GitHubGraphQlRequestScope("ACME", "widgets", 7)
            )
        );

        decision.IsAllowed.Should().BeFalse("owner comparison must be case-sensitive (Ordinal), not case-folded");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void CollectOnlyVariant_denies_graphql_post_with_a_non_positive_pr_number(int number)
    {
        // A non-positive PR number can never be legitimate — pinned even though owner/repo agree with this
        // policy's own scope, isolating the ">0" requirement from the owner/repo requirement above.
        var collectOnly = CreatePolicy(allowWriteOperations: false);
        var nonPositiveScope = new GitHubGraphQlRequestScope("acme", "widgets", number);

        var decision = collectOnly.Decide(
            new OperationRequest(
                SandboxOperation.ReadProviderMetadata,
                "github",
                "api.github.com",
                "POST",
                "/graphql",
                GitHubIssueContextReader.Query,
                GraphQlVariables: nonPositiveScope
            )
        );

        decision.IsAllowed.Should().BeFalse("a non-positive PR number must be denied even if owner/repo agree");
    }

    [Fact]
    public void CollectOnlyVariant_denies_graphql_post_whose_body_targets_a_different_run_scope_entirely()
    {
        // Issue #647 follow-up (MUST #1) — the body's parsed variables name neither half of this policy's
        // own configured owner/repo ("acme"/"widgets"). Pins that matching a DIFFERENT run's scope
        // internally-consistently is not enough; the body must match THIS policy's own configured scope.
        var collectOnly = CreatePolicy(allowWriteOperations: false);
        var otherRunScope = new GitHubGraphQlRequestScope("someone-else", "other-repo", 7);

        var decision = collectOnly.Decide(
            new OperationRequest(
                SandboxOperation.ReadProviderMetadata,
                "github",
                "api.github.com",
                "POST",
                "/graphql",
                GitHubIssueContextReader.Query,
                GraphQlVariables: otherRunScope
            )
        );

        decision.IsAllowed.Should().BeFalse("body variables must match this policy's own configured scope");
    }

    [Theory]
    [InlineData("someone-else", "widgets")]
    [InlineData("acme", "other-repo")]
    public void CollectOnlyVariant_denies_body_variables_that_match_only_one_half_of_the_configured_scope(
        string owner,
        string repo
    )
    {
        // Issue #666 review follow-up — pins the owner-vs-scope and repo-vs-scope comparisons SEPARATELY.
        // The test above (different_run_scope_entirely) varies both owner AND repo together, so removing
        // either scope-comparison conjunct on its own still denies via the other one — a redundant-conjunct
        // gap where the two halves mask each other's mutation. Each InlineData here changes exactly one
        // half away from CreatePolicy's own scope ("acme"/"widgets") while leaving the other matching, so
        // only ONE conjunct is what makes this deny.
        var collectOnly = CreatePolicy(allowWriteOperations: false);
        var oneHalfOffScope = new GitHubGraphQlRequestScope(owner, repo, 7);

        var decision = collectOnly.Decide(
            new OperationRequest(
                SandboxOperation.ReadProviderMetadata,
                "github",
                "api.github.com",
                "POST",
                "/graphql",
                GitHubIssueContextReader.Query,
                GraphQlVariables: oneHalfOffScope
            )
        );

        decision
            .IsAllowed.Should()
            .BeFalse("body variables must match BOTH halves of the configured scope, not just one");
    }

    [Fact]
    public async Task RealPolicy_allows_the_exact_request_GitHubIssueContextReader_emits_for_a_collect_only_run()
    {
        // Issue #666 redesign — proves a collect-only run (write operations denied) still completes a
        // GraphQL read end-to-end through the REAL production pipeline: PolicyEnforcedHttpClientFactory
        // .CreateForGitHubGraphQl builds the client GitHubIssueContextReader.ReadAsync asks for per call,
        // binding this run's own (repo, PR) as the client's canonical scope. Nothing here hand-builds an
        // OperationRequest or a policy — the real send either goes through or the read comes back Failed.
        var handler = new FakeHttpMessageHandler().On(
            req => req.Method == HttpMethod.Post && req.RequestUri is not null,
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"data":{"repository":{"pullRequest":{"closingIssuesReferences":{"pageInfo":{"hasNextPage":false,"endCursor":null},"nodes":[]}}}}}""",
                    Encoding.UTF8,
                    "application/json"
                ),
            }
        );
        var factory = new PolicyEnforcedHttpClientFactory(
            new CodeReviewDaemonOptions { EnabledRepos = ["acme/widgets"], EnableCommentPosting = false },
            NullLogger<OperationPolicyHandler>.Instance,
            NullLogger<RetryHandler>.Instance,
            innerHandlerFactory: () => handler
        );
        var reader = new GitHubIssueContextReader(
            factory,
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

        var result = await reader.ReadAsync(repo, "7", CancellationToken.None);

        result
            .Outcome.Should()
            .Be(
                GitHubIssueLookup.NoneLinked,
                "the reader's own GraphQL read must remain reachable under a collect-only (B) variant"
            );
        handler.Requests.Should().ContainSingle();
        handler.Requests[0].Authorization.Should().Be("Bearer gh-token-xyz");
    }
}
