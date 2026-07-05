using CodeReviewDaemon.Sample.Configuration;

namespace CodeReviewDaemon.Sample.Tests.Configuration;

public class CodeReviewDaemonOptionsTests
{
    [Fact]
    public void Defaults_AreConservativeAndToolAssistedIsOff()
    {
        var options = new CodeReviewDaemonOptions();

        options.EnableToolAssistedReview.Should().BeFalse();
        options.Marketplaces.Should().ContainSingle().Which.Should().Be("gb-plugins");
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
}
