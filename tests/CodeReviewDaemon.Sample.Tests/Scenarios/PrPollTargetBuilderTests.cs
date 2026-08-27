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

    [Fact]
    public void ValidateEnabledRepos_refuses_an_embedded_slash_entry_naming_it()
    {
        // "owner//repo" carries an embedded '/' that collapses to an empty middle segment. Build's
        // RemoveEmptyEntries split would silently read it as the 2-segment "owner/repo" and poll a different
        // repo than the operator wrote; validation must refuse startup and name the entry instead.
        var options = new CodeReviewDaemonOptions { EnabledRepos = ["owner//repo"] };

        var act = () => PrPollTargetBuilder.ValidateEnabledRepos(options);

        act.Should().Throw<InvalidOperationException>().WithMessage("*owner//repo*");
    }

    [Fact]
    public void ValidateEnabledRepos_refuses_a_wrong_segment_count_naming_it()
    {
        var options = new CodeReviewDaemonOptions { EnabledRepos = ["a/b/c/d"] };

        var act = () => PrPollTargetBuilder.ValidateEnabledRepos(options);

        act.Should().Throw<InvalidOperationException>().WithMessage("*a/b/c/d*");
    }

    [Fact]
    public void ValidateEnabledRepos_refuses_a_url_delimiter_in_a_segment_naming_it()
    {
        var options = new CodeReviewDaemonOptions { EnabledRepos = ["owner/re?po"] };

        var act = () => PrPollTargetBuilder.ValidateEnabledRepos(options);

        act.Should().Throw<InvalidOperationException>().WithMessage("*owner/re?po*");
    }

    /// <summary>
    /// Issue #491 requires the refusal to identify the offending configured element. A blank entry has no
    /// content to quote back, so the INDEX is the only thing that can point an operator at it — with three
    /// entries configured, a message that merely said "contains an empty entry" would leave them checking all
    /// three. The whitespace case additionally needs its raw value QUOTED: unquoted, "   " renders as a gap and
    /// the operator cannot tell a blank entry from a truncated message.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void ValidateEnabledRepos_refuses_a_blank_entry_naming_its_index_and_raw_value(string blank)
    {
        var options = new CodeReviewDaemonOptions
        {
            EnabledRepos = ["achieveai/LmDotnetTools", "contoso/Platform/widgets", blank],
        };

        var act = () => PrPollTargetBuilder.ValidateEnabledRepos(options);

        var message = act.Should().Throw<InvalidOperationException>().Which.Message;
        message.Should().Contain("EnabledRepos[2]", "the operator needs the index of the offending element");
        message.Should().Contain($"'{blank}'", "the raw value must be quoted so whitespace is visible");
    }

    [Fact]
    public void ValidateEnabledRepos_names_the_index_of_a_blank_segment_inside_an_entry()
    {
        // "owner/ /repo": three segments, the middle one whitespace-only. Quoting is what makes it legible.
        var options = new CodeReviewDaemonOptions { EnabledRepos = ["owner/ /repo"] };

        var act = () => PrPollTargetBuilder.ValidateEnabledRepos(options);

        var message = act.Should().Throw<InvalidOperationException>().Which.Message;
        message.Should().Contain("EnabledRepos[0]");
        message.Should().Contain("owner/ /repo", "the entry itself must be named");
        message.Should().Contain("position 1", "the operator needs which segment failed");
        message.Should().Contain("' '", "the blank segment's raw value must be quoted");
    }

    [Fact]
    public void ValidateEnabledRepos_accepts_a_spaced_ado_org_and_project()
    {
        // Azure DevOps org/project/repo names may legitimately contain spaces; validation must NOT reject them
        // (Build percent-encodes them). This is the false-positive guard for the character rule.
        var options = new CodeReviewDaemonOptions
        {
            EnabledRepos = ["Fabrikam Fiber/Contoso Project/Widgets", "achieveai/LmDotnetTools"],
        };

        var act = () => PrPollTargetBuilder.ValidateEnabledRepos(options);

        act.Should().NotThrow();
    }
}
