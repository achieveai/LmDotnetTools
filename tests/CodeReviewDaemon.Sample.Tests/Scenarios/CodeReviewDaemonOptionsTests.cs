using CodeReviewDaemon.Sample.Configuration;
using Microsoft.Extensions.Configuration;

namespace CodeReviewDaemon.Sample.Tests.Scenarios;

/// <summary>
/// P2.3 — the daemon's feature flags are <b>conservative by default</b> (collect-only, GitHub-only,
/// review nothing until allow-listed) and bind from the <c>CodeReviewDaemon</c> section.
/// </summary>
public sealed class CodeReviewDaemonOptionsTests
{
    [Fact]
    public void Defaults_are_all_conservative()
    {
        var options = new CodeReviewDaemonOptions();

        options.EnableCommentPosting.Should().BeFalse("posting to a live PR is outward-facing — opt-in only");
        options.EnableKnowledgeAgent.Should().BeFalse();
        options.EnableJudgeAgent.Should().BeFalse();
        options.EnableABVariants.Should().BeFalse();
        options.EnableAdoProvider.Should().BeFalse("the daemon is GitHub-only until ADO is enabled");
        options.EnabledRepos.Should().BeEmpty("no repo is reviewed until explicitly allow-listed");
        options.DatabasePath.Should().BeNull("the default database path is resolved at startup, not bound");
    }

    [Fact]
    public void Binds_every_flag_from_the_CodeReviewDaemon_section()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CodeReviewDaemon:EnableCommentPosting"] = "true",
                ["CodeReviewDaemon:EnableKnowledgeAgent"] = "true",
                ["CodeReviewDaemon:EnableJudgeAgent"] = "true",
                ["CodeReviewDaemon:EnableABVariants"] = "true",
                ["CodeReviewDaemon:EnableAdoProvider"] = "true",
                ["CodeReviewDaemon:EnabledRepos:0"] = "achieveai/LmDotnetTools",
                ["CodeReviewDaemon:EnabledRepos:1"] = "contoso/widgets",
            })
            .Build();

        var options = CodeReviewDaemonOptions.Bind(
            config.GetSection(CodeReviewDaemonOptions.SectionName));

        options.Should().NotBeNull();
        options!.EnableCommentPosting.Should().BeTrue();
        options.EnableKnowledgeAgent.Should().BeTrue();
        options.EnableJudgeAgent.Should().BeTrue();
        options.EnableABVariants.Should().BeTrue();
        options.EnableAdoProvider.Should().BeTrue();
        options.EnabledRepos.Should().Equal("achieveai/LmDotnetTools", "contoso/widgets");
    }

    [Fact]
    public void Binds_the_pooled_review_workspace_options_from_the_CodeReviewDaemon_section()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CodeReviewDaemon:ReviewPoolSize"] = "4",
                ["CodeReviewDaemon:ReviewPoolHostRoot"] = "/var/crd/review-pool",
                ["CodeReviewDaemon:ScratchDirName"] = "work",
                ["CodeReviewDaemon:EnableReviewerWrites"] = "true",
                ["CodeReviewDaemon:WritableToolAllowList:0"] = "PrNotes",
                ["CodeReviewDaemon:MergeNotesBranchOnClose"] = "false",
            })
            .Build();

        var options = CodeReviewDaemonOptions.Bind(
            config.GetSection(CodeReviewDaemonOptions.SectionName));

        options.Should().NotBeNull();
        options!.ReviewPoolSize.Should().Be(4);
        options.ReviewPoolHostRoot.Should().Be("/var/crd/review-pool");
        options.ScratchDirName.Should().Be("work");
        options.EnableReviewerWrites.Should().BeTrue();
        options.MergeNotesBranchOnClose.Should().BeFalse();
        // A distinctive value (not one of the ["Write","Edit","Bash"] defaults) proves the list bound, and
        // exact equality proves it REPLACED the default rather than being appended to it — see
        // Stated_lists_replace_their_defaults_instead_of_appending_to_them.
        options.WritableToolAllowList.Should().Equal("PrNotes");
    }

    [Fact]
    public void Stated_lists_replace_their_defaults_instead_of_appending_to_them()
    {
        // The configuration binder builds a collection by KEEPING what the property already holds and
        // appending the configured entries, so every list here with a non-empty initializer silently becomes
        // "the default PLUS what the operator asked for". That is not a tidiness complaint: the nova profile
        // states SubAgentMarketplaces ["gb"], the daemon probed [gb-plugins,gb], and a gateway that publishes
        // no gb-plugins alias answers 400 — leaving skill support unverified on every run under
        // RequireSkillSupport, which is precisely the silent degradation that flag exists to prevent.
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CodeReviewDaemon:Marketplaces:0"] = "gb",
                ["CodeReviewDaemon:SubAgentMarketplaces:0"] = "gb",
                ["CodeReviewDaemon:ReadOnlyToolAllowList:0"] = "Read",
                ["CodeReviewDaemon:WritableToolAllowList:0"] = "PrNotes",
            })
            .Build();

        var options = CodeReviewDaemonOptions.Bind(
            config.GetSection(CodeReviewDaemonOptions.SectionName));

        options.Marketplaces.Should().Equal("gb");
        options.SubAgentMarketplaces.Should().Equal("gb");
        options.ReadOnlyToolAllowList.Should().Equal("Read");
        options.WritableToolAllowList.Should().Equal("PrNotes");
    }

    [Fact]
    public void Unstated_lists_keep_the_documented_default()
    {
        // The other half of the seeding: replacement must not turn "the operator said nothing" into an empty
        // list. ReadOnlyToolAllowList is the one that would hurt — it is what the review agent is allowed to
        // call, so emptying it by accident produces an agent that can read nothing and reviews on the diff
        // alone, which still looks like a review.
        var options = CodeReviewDaemonOptions.Bind(
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["CodeReviewDaemon:EnableAdoProvider"] = "true",
                })
                .Build()
                .GetSection(CodeReviewDaemonOptions.SectionName));

        options.EnableAdoProvider.Should().BeTrue("the section was bound at all");
        options.Marketplaces.Should().Equal(CodeReviewDaemonOptions.DefaultMarketplaces);
        options.SubAgentMarketplaces.Should().Equal(CodeReviewDaemonOptions.DefaultSubAgentMarketplaces);
        options.ReadOnlyToolAllowList.Should().Equal(CodeReviewDaemonOptions.DefaultReadOnlyToolAllowList);
        options.WritableToolAllowList.Should().Equal(CodeReviewDaemonOptions.DefaultWritableToolAllowList);
    }

    [Fact]
    public void Binding_an_absent_section_yields_the_conservative_defaults()
    {
        var options = CodeReviewDaemonOptions.Bind(
            new ConfigurationBuilder().Build().GetSection(CodeReviewDaemonOptions.SectionName));

        options.EnableCommentPosting.Should().BeFalse();
        options.EnabledRepos.Should().BeEmpty();
        options.ReadOnlyToolAllowList.Should().Equal(CodeReviewDaemonOptions.DefaultReadOnlyToolAllowList);
    }

    [Fact]
    public void Review_sub_agent_barrier_options_default_and_bind_from_the_CodeReviewDaemon_section()
    {
        new CodeReviewDaemonOptions().ReviewStageDeadlineMinutes.Should().Be(30);
        new CodeReviewDaemonOptions().ReviewSubAgentBarrierQuietSeconds.Should().Be(2);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CodeReviewDaemon:ReviewStageDeadlineMinutes"] = "45",
                ["CodeReviewDaemon:ReviewSubAgentBarrierQuietSeconds"] = "5",
            })
            .Build();

        var options = CodeReviewDaemonOptions.Bind(
            config.GetSection(CodeReviewDaemonOptions.SectionName));

        options.Should().NotBeNull();
        options!.ReviewStageDeadlineMinutes.Should().Be(45);
        options.ReviewSubAgentBarrierQuietSeconds.Should().Be(5);
    }
}
