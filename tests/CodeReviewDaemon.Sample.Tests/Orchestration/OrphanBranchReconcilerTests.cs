using CodeReviewDaemon.Sample.Orchestration;
using CodeReviewDaemon.Sample.Persistence.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeReviewDaemon.Sample.Tests.Orchestration;

/// <summary>
/// The sweep watch-set reconciler: it must surface orphaned <c>review/*</c> branches (whose review row is
/// missing from the DB) as pollable PRs resolved back to a configured repo identity, while never dropping or
/// duplicating the DB-derived set, and skipping any branch it cannot resolve.
/// </summary>
public sealed class OrphanBranchReconcilerTests
{
    private static readonly RepoIdentity Widgets = new()
    {
        Provider = "github",
        OrgOrOwner = "acme",
        RepoName = "widgets",
    };

    private static readonly PrPollTarget WidgetsTarget = new()
    {
        Provider = "github",
        Repo = Widgets,
        Scope = "acme/widgets:open-prs",
    };

    [Fact]
    public void Adds_an_orphaned_branch_that_matches_a_configured_repo()
    {
        var result = OrphanBranchReconciler.Reconcile(
            fromDb: [],
            remoteReviewBranches: ["review/widgets-42"],
            configuredTargets: [WidgetsTarget],
            NullLogger.Instance);

        var pr = result.Should().ContainSingle().Subject;
        pr.Provider.Should().Be("github");
        pr.PrId.Should().Be("42");
        pr.Branch.Should().Be("review/widgets-42");
        pr.Repo.RepoName.Should().Be("widgets", "the identity is recovered from the configured target, not the branch name");
    }

    [Fact]
    public void Keeps_the_db_set_and_does_not_duplicate_a_branch_already_covered()
    {
        ReviewedPr[] fromDb = [new(Widgets, "github", "42", "review/widgets-42")];

        var result = OrphanBranchReconciler.Reconcile(
            fromDb,
            remoteReviewBranches: ["review/widgets-42"],
            configuredTargets: [WidgetsTarget],
            NullLogger.Instance);

        result.Should().ContainSingle().Which.Branch.Should().Be("review/widgets-42");
    }

    [Fact]
    public void Skips_a_branch_whose_slug_matches_no_configured_repo()
    {
        var result = OrphanBranchReconciler.Reconcile(
            fromDb: [],
            remoteReviewBranches: ["review/unknown-7"],
            configuredTargets: [WidgetsTarget],
            NullLogger.Instance);

        result.Should().BeEmpty();
    }

    [Fact]
    public void Resolves_a_legacy_nested_branch_via_the_owner_repo_slug()
    {
        // Left over from before the {repo}-{pr} rename: review/{provider}/{owner-repo}/{pr}.
        var result = OrphanBranchReconciler.Reconcile(
            fromDb: [],
            remoteReviewBranches: ["review/github/acme-widgets/42"],
            configuredTargets: [WidgetsTarget],
            NullLogger.Instance);

        var pr = result.Should().ContainSingle().Subject;
        pr.Provider.Should().Be("github");
        pr.PrId.Should().Be("42");
        pr.Branch.Should().Be("review/github/acme-widgets/42", "the sweep must act on the actual legacy branch name");
        pr.Repo.RepoName.Should().Be("widgets");
    }

    [Fact]
    public void Skips_a_legacy_branch_whose_slug_matches_no_configured_repo()
    {
        var result = OrphanBranchReconciler.Reconcile(
            fromDb: [],
            remoteReviewBranches: ["review/github/other-owner-other-repo/9"],
            configuredTargets: [WidgetsTarget],
            NullLogger.Instance);

        result.Should().BeEmpty();
    }
}
