using CodeReviewDaemon.Sample.Configuration;

namespace CodeReviewDaemon.Sample.Tests.Configuration;

public class CodeReviewDaemonOptionsTests
{
    [Fact]
    public void Defaults_AreConservativeAndToolAssistedIsOff()
    {
        var options = new CodeReviewDaemonOptions();

        options.EnableToolAssistedReview.Should().BeFalse();
        options.Marketplaces.Should().Equal("gb-plugins", "superpowers");
        options.ReadOnlyToolAllowList.Should().BeEquivalentTo(["Read", "Grep", "Glob", "Skill"]);
        options.WorkspaceHostRoot.Should().BeNull();
    }

    [Fact]
    public void ToolAssistedPath_RaisesTokenBudgetAndDefaultsAnAboveLowEffort()
    {
        var options = new CodeReviewDaemonOptions();

        options
            .ReviewMaxTokens.Should()
            .BeGreaterThan(
                16000,
                "a multi-turn tool-assisted + sub-agent loop needs a larger token budget than the single-pass diff-only reviewer"
            );
        options
            .ToolAssistedReasoningEffort.Should()
            .Be(
                "medium",
                "the diff-only default of 'low' is tuned for a single-pass review; a multi-turn + sub-agent loop wants more reasoning headroom"
            );
    }

    [Fact]
    public void Pool_and_scoped_tool_defaults_are_conservative()
    {
        var o = new CodeReviewDaemonOptions();
        o.ReviewPoolSize.Should().Be(2);
        o.EnableReviewerWrites.Should().BeFalse("writes are an explicit opt-in");
        o.WritableToolAllowList.Should().BeEquivalentTo(["Write", "Edit", "Bash"]);
        o.MergeNotesBranchOnClose.Should().BeTrue();
        o.ScratchDirName.Should().Be("scratch");
    }

    [Fact]
    public void BotName_defaults_to_Revobot()
    {
        var o = new CodeReviewDaemonOptions();

        o.BotName.Should().Be("Revobot", "an operator can override the display name, e.g. \"GB's Revobot\"");
    }

    [Fact]
    public void Model_role_knobs_default_to_empty_so_secondary_agents_inherit_the_primary()
    {
        var o = new CodeReviewDaemonOptions();

        o.ReviewModelId.Should().Be("claude-sonnet-5", "the primary dispatcher always has a concrete model");
        o.SubAgentModelId.Should().BeEmpty("empty ⇒ review sub-agents inherit ReviewModelId");
        o.KnowledgeModelId.Should()
            .BeEmpty("empty ⇒ the at-close knowledge-extraction loop inherits ReviewModelId; set it (e.g. claude-opus-4.8) to run extraction on a dedicated model");
    }
}
