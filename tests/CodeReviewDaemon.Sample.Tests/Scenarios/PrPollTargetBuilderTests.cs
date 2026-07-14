using AchieveAi.LmDotnetTools.LmTestUtils.Logging;
using CodeReviewDaemon.Sample.Configuration;
using CodeReviewDaemon.Sample.Orchestration;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace CodeReviewDaemon.Sample.Tests.Scenarios;

/// <summary>
/// P4.4 — the allow-list (<c>EnabledRepos</c>) is the single consumer that turns operator config into the
/// poller's <see cref="PrPollTarget"/> set. These tests pin: a 2-segment entry is a GitHub repo and a
/// 3-segment entry is an ADO repo; the post/collect-only mode follows <c>EnableCommentPosting</c>; ADO
/// targets are emitted only when <c>EnableAdoProvider</c> is set (no provider is registered otherwise);
/// and the default (empty allow-list) yields no targets so the daemon reviews nothing.
/// </summary>
public sealed class PrPollTargetBuilderTests : LoggingTestBase
{
    public PrPollTargetBuilderTests(ITestOutputHelper output)
        : base(output)
    {
    }

    private IReadOnlyList<PrPollTarget> Build(CodeReviewDaemonOptions options) =>
        PrPollTargetBuilder.Build(options, LoggerFactory.CreateLogger<PrPollTargetBuilderTests>());

    [Fact]
    public void Empty_allow_list_yields_no_targets()
    {
        Build(new CodeReviewDaemonOptions()).Should().BeEmpty();
    }

    [Fact]
    public void A_two_segment_entry_becomes_a_github_target()
    {
        var targets = Build(new CodeReviewDaemonOptions { EnabledRepos = ["achieveai/LmDotnetTools"] });

        var target = targets.Should().ContainSingle().Subject;
        target.Provider.Should().Be("github");
        target.Repo.Provider.Should().Be("github");
        target.Repo.OrgOrOwner.Should().Be("achieveai");
        target.Repo.Project.Should().BeNull();
        target.Repo.RepoName.Should().Be("LmDotnetTools");
        target.Scope.Should().Be("achieveai/LmDotnetTools:open-prs");
        target.Mode.Should().Be("collect-only", "comment posting is off by default");
        target.VariantId.Should().Be("primary");
    }

    [Fact]
    public void A_three_segment_entry_becomes_an_ado_target_when_ado_is_enabled()
    {
        var targets = Build(new CodeReviewDaemonOptions
        {
            EnableAdoProvider = true,
            EnabledRepos = ["contoso/Platform/widgets"],
        });

        var target = targets.Should().ContainSingle().Subject;
        target.Provider.Should().Be("ado");
        target.Repo.Provider.Should().Be("azure-devops");
        target.Repo.OrgOrOwner.Should().Be("contoso");
        target.Repo.Project.Should().Be("Platform");
        target.Repo.RepoName.Should().Be("widgets");
        target.Scope.Should().Be("contoso/Platform/widgets:active-prs");
    }

    [Fact]
    public void Ado_entries_are_skipped_when_the_ado_provider_is_disabled()
    {
        var targets = Build(new CodeReviewDaemonOptions
        {
            EnabledRepos = ["achieveai/LmDotnetTools", "contoso/Platform/widgets"],
        });

        var target = targets.Should().ContainSingle("the ADO repo is dropped when its provider is off").Subject;
        target.Provider.Should().Be("github");
    }

    [Fact]
    public void Enabling_comment_posting_makes_targets_post_mode()
    {
        var targets = Build(new CodeReviewDaemonOptions
        {
            EnableCommentPosting = true,
            EnabledRepos = ["achieveai/LmDotnetTools"],
        });

        targets.Should().ContainSingle().Which.Mode.Should().Be("post");
    }

    [Fact]
    public void Malformed_entries_are_skipped_not_thrown()
    {
        var targets = Build(new CodeReviewDaemonOptions
        {
            EnabledRepos = ["just-one-segment", "", "  ", "a/b/c/d", "achieveai/LmDotnetTools"],
        });

        targets.Should().ContainSingle().Which.Repo.RepoName.Should().Be("LmDotnetTools");
    }

    [Fact]
    public void The_recency_bound_flows_from_options_onto_each_target()
    {
        var targets = Build(new CodeReviewDaemonOptions
        {
            EnableAdoProvider = true,
            MaxPrAgeDays = 5,
            EnabledRepos = ["achieveai/LmDotnetTools", "contoso/Platform/widgets"],
        });

        targets.Should().HaveCount(2);
        targets.Should().OnlyContain(t => t.MaxPrAgeDays == 5, "the operator recency bound is stamped onto every target");
    }

    [Fact]
    public void The_recency_bound_defaults_to_zero_off()
    {
        var targets = Build(new CodeReviewDaemonOptions { EnabledRepos = ["achieveai/LmDotnetTools"] });

        targets.Should().ContainSingle().Which.MaxPrAgeDays.Should().Be(0, "the filter is off unless an operator sets it");
    }
}
