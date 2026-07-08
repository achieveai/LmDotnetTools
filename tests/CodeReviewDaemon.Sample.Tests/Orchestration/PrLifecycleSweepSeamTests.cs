using CodeReviewDaemon.Sample.Orchestration;
using CodeReviewDaemon.Sample.Persistence;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Tests.Infrastructure;

namespace CodeReviewDaemon.Sample.Tests.Orchestration;

/// <summary>
/// Task 9 — the sweep seam the daemon composes the <see cref="PrLifecycleSweeper"/> from: mapping a
/// <see cref="ReviewedPrRow"/> to a <see cref="ReviewedPr"/> (provider mapped, notes branch named the same
/// way commit-notes does) and routing a PR's lifecycle lookup to the matching provider.
/// </summary>
public sealed class PrLifecycleSweepSeamTests
{
    [Fact]
    public void MapReviewedPr_names_the_github_notes_branch_the_same_way_commit_notes_does()
    {
        var row = new ReviewedPrRow(
            new RepoIdentity { Provider = "github", OrgOrOwner = "acme", RepoName = "widgets" }, "github", "42");

        var pr = PrLifecycleSweepSeam.MapReviewedPr(row);

        pr.Should().NotBeNull();
        pr!.Provider.Should().Be("github");
        pr.PrId.Should().Be("42");
        pr.Branch.Should().Be("review/github/acme-widgets/42");
    }

    [Fact]
    public void MapReviewedPr_maps_azure_devops_to_the_ado_branch_and_poll_namespace()
    {
        var row = new ReviewedPrRow(
            new RepoIdentity { Provider = "azure-devops", OrgOrOwner = "acme", Project = "Platform", RepoName = "widgets" },
            "azure-devops",
            "7");

        var pr = PrLifecycleSweepSeam.MapReviewedPr(row);

        pr.Should().NotBeNull();
        pr!.Provider.Should().Be("ado", "the sweeper polls/branches under 'ado', not the 'azure-devops' storage namespace");
        pr.Branch.Should().Be("review/ado/acme-platform-widgets/7");
    }

    [Fact]
    public void MapReviewedPr_skips_a_non_numeric_pr_id_that_cannot_name_a_branch()
    {
        var row = new ReviewedPrRow(
            new RepoIdentity { Provider = "github", OrgOrOwner = "acme", RepoName = "widgets" }, "github", "not-a-number");

        PrLifecycleSweepSeam.MapReviewedPr(row).Should().BeNull();
    }

    [Fact]
    public async Task ResolveLifecycleAsync_routes_each_pr_to_the_provider_matching_its_namespace()
    {
        var github = new MockPrProvider("github", [], Cursor("github")) { PrState = PrLifecycle.Merged };
        var ado = new MockPrProvider("ado", [], Cursor("ado")) { PrState = PrLifecycle.Abandoned };
        IReadOnlyList<IPrProvider> providers = [github, ado];
        var repo = new RepoIdentity { Provider = "github", OrgOrOwner = "acme", RepoName = "widgets" };

        var githubState = await PrLifecycleSweepSeam.ResolveLifecycleAsync(
            providers, new ReviewedPr(repo, "github", "42", "review/github/acme-widgets/42"), CancellationToken.None);
        var adoState = await PrLifecycleSweepSeam.ResolveLifecycleAsync(
            providers, new ReviewedPr(repo, "ado", "7", "review/ado/acme-widgets/7"), CancellationToken.None);

        githubState.Should().Be(PrLifecycle.Merged, "the github PR routes to the github provider's state");
        adoState.Should().Be(PrLifecycle.Abandoned, "the ado PR routes to the ado provider's state");
    }

    [Fact]
    public async Task ResolveLifecycleAsync_throws_when_no_provider_matches()
    {
        IReadOnlyList<IPrProvider> providers = [new MockPrProvider("github", [], Cursor("github"))];
        var repo = new RepoIdentity { Provider = "github", OrgOrOwner = "acme", RepoName = "widgets" };

        var act = () => PrLifecycleSweepSeam.ResolveLifecycleAsync(
            providers, new ReviewedPr(repo, "ado", "7", "review/ado/acme-widgets/7"), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*ado*");
    }

    private static OpaqueCursor Cursor(string provider) => new()
    {
        Provider = provider,
        Scope = "scope",
        CursorVersion = 1,
        CursorPayload = "{}",
    };
}
