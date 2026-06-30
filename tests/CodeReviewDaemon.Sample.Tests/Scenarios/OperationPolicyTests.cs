using CodeReviewDaemon.Sample.Workspace;

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
                AllowedSubmodules:
                [
                    new SubmoduleAllowRule("github.com", "/acme/shared-lib"),
                ]),
            allowWriteOperations);

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
                "/acme/widgets.git/info/refs?service=git-upload-pack"));
        var negotiate = policy.Decide(
            new OperationRequest(
                SandboxOperation.FetchTarget,
                "github",
                "github.com",
                "POST",
                "/acme/widgets.git/git-upload-pack"));

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
                "/acme/widgets.git/git-receive-pack"));

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
                "/acme/widgets.git/info/refs?service=git-upload-pack"));

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
                "/acme/widgets-secrets.git/info/refs?service=git-upload-pack"));

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
                "/acme/widgets.git/../../acme/reviewbot.git/info/refs?service=git-upload-pack"));

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
                "/acme/reviewbot.git/git-receive-pack"));
        var deniedFetchService = policy.Decide(
            new OperationRequest(
                SandboxOperation.PushReviewBot,
                "github",
                "github.com",
                "POST",
                "/acme/reviewbot.git/git-upload-pack"));

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
                "/acme/widgets.git/git-receive-pack"));

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
                "/contributor/widgets.git/git-upload-pack"));
        var push = policy.Decide(
            new OperationRequest(
                SandboxOperation.FetchForkHead,
                "github",
                "github.com",
                "POST",
                "/contributor/widgets.git/git-receive-pack"));

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
                "/acme/shared-lib.git/info/refs?service=git-upload-pack"));
        var denied = policy.Decide(
            new OperationRequest(
                SandboxOperation.FetchSubmodule,
                "github",
                "github.com",
                "GET",
                "/random/private.git/info/refs?service=git-upload-pack"));

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
                "/repos/acme/widgets/pulls/7/comments"));
        var wrongMethod = policy.Decide(
            new OperationRequest(
                SandboxOperation.PostReviewComment,
                "github",
                "api.github.com",
                "GET",
                "/repos/acme/widgets/pulls/7/comments"));
        var wrongHost = policy.Decide(
            new OperationRequest(
                SandboxOperation.PostReviewComment,
                "github",
                "github.com",
                "POST",
                "/repos/acme/widgets/pulls/7/comments"));

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
                "/acme/reviewbot.git/git-receive-pack"));
        var post = collectOnly.Decide(
            new OperationRequest(
                SandboxOperation.PostReviewComment,
                "github",
                "api.github.com",
                "POST",
                "/repos/acme/widgets/pulls/7/comments"));

        push.IsAllowed.Should().BeFalse("a collect-only B variant has no push capability");
        post.IsAllowed.Should().BeFalse("a collect-only B variant has no post capability");
    }

    [Fact]
    public void CollectOnlyVariant_is_never_handed_a_write_credential()
    {
        // The credential decision mirrors the deny, so the B variant is also never injected with a
        // push/post token (fail closed both ways) — there is no token for it to misuse.
        var collectOnly = CreatePolicy(allowWriteOperations: false);

        collectOnly.ShouldInjectCredential(
            new OperationRequest(
                SandboxOperation.PushReviewBot,
                "github",
                "github.com",
                "POST",
                "/acme/reviewbot.git/git-receive-pack"))
            .Should().BeFalse();
        collectOnly.ShouldInjectCredential(
            new OperationRequest(
                SandboxOperation.PostReviewComment,
                "github",
                "api.github.com",
                "POST",
                "/repos/acme/widgets/pulls/7/comments"))
            .Should().BeFalse();
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
                "/acme/widgets.git/info/refs?service=git-upload-pack"));

        fetch.IsAllowed.Should().BeTrue("fetching the target repo is read-only, not a write operation");
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
            "/acme/widgets.git/git-receive-pack");
        var allowedRequest = new OperationRequest(
            SandboxOperation.PushReviewBot,
            "github",
            "github.com",
            "POST",
            "/acme/reviewbot.git/git-receive-pack");

        policy.ShouldInjectCredential(deniedRequest).Should().BeFalse();
        policy.ShouldInjectCredential(allowedRequest).Should().BeTrue();
    }
}
