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

        var options = config.GetSection(CodeReviewDaemonOptions.SectionName).Get<CodeReviewDaemonOptions>();

        options.Should().NotBeNull();
        options!.EnableCommentPosting.Should().BeTrue();
        options.EnableKnowledgeAgent.Should().BeTrue();
        options.EnableJudgeAgent.Should().BeTrue();
        options.EnableABVariants.Should().BeTrue();
        options.EnableAdoProvider.Should().BeTrue();
        options.EnabledRepos.Should().Equal("achieveai/LmDotnetTools", "contoso/widgets");
    }
}
