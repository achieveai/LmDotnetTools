using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Workspace.Git;

namespace CodeReviewDaemon.Sample.Tests.Scenarios;

/// <summary>
/// The pure branch-name helpers on <see cref="ReviewBranchManager"/>: the <c>{repo}-{pr}</c> slug/branch
/// builders and their reverse parser, which the orphan-branch reconciler relies on.
/// </summary>
public sealed class ReviewBranchNameTests
{
    [Theory]
    [InlineData("review/widgets-42", "widgets", 42)]
    [InlineData("review/my-repo-7", "my-repo", 7)] // repo slugs may themselves contain '-'
    public void TryParseReviewBranch_round_trips_a_new_scheme_branch(string branch, string expectedSlug, int expectedPr)
    {
        ReviewBranchManager.TryParseReviewBranch(branch, out var slug, out var pr).Should().BeTrue();
        slug.Should().Be(expectedSlug);
        pr.Should().Be(expectedPr);
    }

    [Theory]
    [InlineData("review/github/acme-widgets/42")] // legacy nested form
    [InlineData("review/widgets-")] // no PR number
    [InlineData("review/widgets")] // no dash/number
    [InlineData("feature/widgets-42")] // not a review branch
    [InlineData("review/-42")] // empty slug
    public void TryParseReviewBranch_rejects_non_new_scheme_names(string branch)
    {
        ReviewBranchManager.TryParseReviewBranch(branch, out _, out _).Should().BeFalse();
    }

    [Fact]
    public void RepoSlug_and_BuildReviewBranchName_agree()
    {
        var repo = new RepoIdentity { Provider = "github", OrgOrOwner = "achieveai", RepoName = "LmDotnetTools" };
        ReviewBranchManager.RepoSlug(repo).Should().Be("lmdotnettools");
        ReviewBranchManager.BuildReviewBranchName(repo, 156).Should().Be("review/lmdotnettools-156");
    }

    [Theory]
    [InlineData("review/github/acme-widgets/42", "acme-widgets", 42)]
    [InlineData("review/ado/acme-platform-widgets/7", "acme-platform-widgets", 7)]
    public void TryParseLegacyReviewBranch_round_trips_a_legacy_branch(string branch, string expectedSlug, int expectedPr)
    {
        ReviewBranchManager.TryParseLegacyReviewBranch(branch, out var slug, out var pr).Should().BeTrue();
        slug.Should().Be(expectedSlug);
        pr.Should().Be(expectedPr);
    }

    [Theory]
    [InlineData("review/lmdotnettools-156")] // new single-segment form
    [InlineData("review/github/acme-widgets")] // no PR segment
    [InlineData("review/github/acme-widgets/x")] // non-numeric PR
    [InlineData("feature/github/acme-widgets/42")] // not a review branch
    public void TryParseLegacyReviewBranch_rejects_non_legacy_names(string branch)
    {
        ReviewBranchManager.TryParseLegacyReviewBranch(branch, out _, out _).Should().BeFalse();
    }

    [Fact]
    public void LegacyRepoSlug_matches_the_legacy_branch_slug()
    {
        var repo = new RepoIdentity { Provider = "github", OrgOrOwner = "achieveai", RepoName = "LmDotnetTools" };
        ReviewBranchManager.LegacyRepoSlug(repo).Should().Be("achieveai-lmdotnettools");
    }
}
