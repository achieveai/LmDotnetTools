using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Workspace;
using CodeReviewDaemon.Sample.Workspace.Git;

namespace CodeReviewDaemon.Sample.Tests.Workspace;

/// <summary>
/// Task #82 requirement 1 — the daemon pushes nothing by default. The ONLY thing that can authorize a
/// review-artifact branch push is a command-scoped capability naming exactly one repo, PR and head, and
/// that capability can never authorize a write to the source PR.
/// </summary>
public sealed class ReviewArtifactBranchCapabilityTests
{
    private const string Head = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string OtherHead = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    private static readonly RepoIdentity Repo = new()
    {
        Provider = "github",
        OrgOrOwner = "example",
        RepoName = "widgets",
    };

    private static ReviewRun Run(string prId = "7", string headSha = Head) =>
        new()
        {
            RepoId = 1,
            PrId = prId,
            HeadSha = headSha,
            BaseSha = "base",
            TriggerWatermark = "1",
            ReviewKind = "new_head",
            VariantId = "primary",
            Mode = "collect-only",
            Stage = ReviewStage.Discovered,
            WorkflowStatus = WorkflowStatus.Pending,
            PrLifecycleState = PrLifecycleState.Open,
        };

    [Fact]
    public void Denied_is_the_default_posture_and_authorizes_nothing()
    {
        var capability = ReviewArtifactBranchCapability.Denied;

        capability.IsGranted.Should().BeFalse();
        capability.ExportsFullReviewOutput.Should().BeFalse();
        capability.AuthorizesPush(Repo, Run()).Should().BeFalse();
    }

    [Fact]
    public void Grant_authorizes_exactly_the_named_repo_pr_and_head()
    {
        var capability = ReviewArtifactBranchCapability.Grant(Repo.NormalizedKey, "7", Head);

        capability.IsGranted.Should().BeTrue();
        capability.ExportsFullReviewOutput.Should().BeTrue();
        capability.AuthorizesPush(Repo, Run()).Should().BeTrue();
    }

    [Fact]
    public void A_display_name_key_names_the_same_single_repository()
    {
        ReviewArtifactBranchCapability.Grant(Repo.DisplayName, "7", Head).AuthorizesPush(Repo, Run()).Should().BeTrue();
    }

    [Theory]
    [InlineData("8", Head)]
    [InlineData("7", OtherHead)]
    public void Grant_refuses_any_run_that_is_not_the_one_it_names(string prId, string headSha)
    {
        var capability = ReviewArtifactBranchCapability.Grant(Repo.NormalizedKey, "7", Head);

        capability.AuthorizesPush(Repo, Run(prId, headSha)).Should().BeFalse();
    }

    [Fact]
    public void Grant_refuses_a_different_repository_even_at_the_same_pr_number()
    {
        var capability = ReviewArtifactBranchCapability.Grant(Repo.NormalizedKey, "7", Head);

        capability.AuthorizesPush(Repo with { RepoName = "gadgets" }, Run()).Should().BeFalse();
    }

    [Fact]
    public void No_capability_can_authorize_a_write_to_the_source_pull_request()
    {
        ReviewArtifactBranchCapability.Grant(Repo.NormalizedKey, "7", Head).AuthorizesSourcePrWrite.Should().BeFalse();
        ReviewArtifactBranchCapability.Denied.AuthorizesSourcePrWrite.Should().BeFalse();
    }

    /// <summary>
    /// The grant is not a write capability. The daemon's policy for a collect-only run denies posting and
    /// replying on the source PR and denies every push, and minting a grant does not change that — the
    /// capability is not an input to the policy at all. It is read in exactly one place, the host-side
    /// retention gate, and the two surfaces are independent by construction.
    /// </summary>
    [Fact]
    public void Minting_a_grant_does_not_open_any_source_pull_request_write()
    {
        var granted = ReviewArtifactBranchCapability.Grant(Repo.NormalizedKey, "7", Head);
        var policy = DaemonOperationPolicy.BuildForRun(
            Repo,
            "https://github.com/example/reviewbot",
            allowWriteOperations: false
        );

        granted.AuthorizesSourcePrWrite.Should().BeFalse();
        policy.AllowsWriteOperations.Should().BeFalse();
        foreach (
            var path in new[] { "/repos/example/widgets/pulls/7/reviews", "/repos/example/widgets/issues/7/comments" }
        )
        {
            var request = new OperationRequest(
                SandboxOperation.PostReviewComment,
                "github",
                "api.github.com",
                "POST",
                path
            );
            policy.Decide(request).IsAllowed.Should().BeFalse();
            policy.ShouldInjectCredential(request).Should().BeFalse();
        }
    }

    [Theory]
    [InlineData("/example/widgets.git/git-receive-pack")]
    [InlineData("/example/other-bot.git/git-receive-pack")]
    [InlineData("/example/reviewbot.git/git-receive-pack")]
    public void Minting_a_grant_does_not_open_any_sandbox_push(string path)
    {
        _ = ReviewArtifactBranchCapability.Grant(Repo.NormalizedKey, "7", Head);

        DaemonOperationPolicy
            .BuildForRun(Repo, "https://github.com/example/reviewbot", allowWriteOperations: false)
            .Decide(new OperationRequest(SandboxOperation.PushReviewBot, "github", "github.com", "POST", path))
            .IsAllowed.Should()
            .BeFalse();
    }

    [Fact]
    public void The_branch_a_grant_reaches_is_derived_from_the_repo_and_pr_not_supplied_by_the_operator()
    {
        var capability = ReviewArtifactBranchCapability.Grant(Repo.NormalizedKey, "7", Head);

        // There is no member that accepts a branch or a ref: the only branch expressible is this one.
        capability.AuthorizedBranch(Repo).Should().Be("review/widgets-7");
        capability.AuthorizedBranch(Repo with { RepoName = "gadgets" }).Should().Be("review/gadgets-7");
    }

    [Fact]
    public void Authorized_branch_is_the_single_review_branch_of_that_pr()
    {
        ReviewArtifactBranchCapability
            .Grant(Repo.NormalizedKey, "7", Head)
            .AuthorizedBranch(Repo)
            .Should()
            .Be(ReviewBranchManager.BuildReviewBranchName(Repo, 7));
    }

    /// <summary>
    /// There is no command-line flag, and that is the point: an earlier revision took the grant from its own
    /// <c>--artifact-branch-pilot</c> flag, which meant two independently-typed descriptions of "which run"
    /// had to agree. The grant is now derived from the run command's already-validated identity, so this
    /// type exposes no parser at all and there is nothing to mismatch.
    /// </summary>
    [Fact]
    public void There_is_no_flag_parser_on_the_capability_at_all()
    {
        typeof(ReviewArtifactBranchCapability)
            .GetMethods()
            .Select(method => method.Name)
            .Should()
            .NotContain(["Extract", "Parse", "TryParse", "FromArgs"]);
    }

    [Theory]
    [InlineData("", "7", Head)]
    [InlineData("   ", "7", Head)]
    [InlineData("example/widgets", "not-a-number", Head)]
    [InlineData("example/widgets", "0", Head)]
    [InlineData("example/widgets", "-1", Head)]
    [InlineData("example/widgets", "7", "short")]
    [InlineData("example/widgets", "7", "zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    public void A_grant_that_does_not_name_exactly_one_run_is_refused_rather_than_narrowed(
        string repoKey,
        string prId,
        string headSha
    )
    {
        var mint = () => ReviewArtifactBranchCapability.Grant(repoKey, prId, headSha);

        mint.Should().Throw<ArgumentException>();
    }
}
