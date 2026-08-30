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

    /// <summary>
    /// #628: reviews provision in the dedicated system mode by default — not the generic
    /// <c>workspace-agent</c>. The judge and knowledge arms share this field, which is why the mode
    /// carries only identity + discipline (arm-agnostic) and methodology stays in the per-arm
    /// appendix. Config override retained.
    /// </summary>
    [Fact]
    public void LmStreamingModeId_DefaultsToTheDedicatedCodeReviewDaemonMode()
    {
        new CodeReviewDaemonOptions().LmStreamingModeId.Should().Be("code-review-daemon");
    }

    /// <summary>
    /// The cross-project half of the #628 provision pin: the mode id the daemon defaults to must
    /// exist in the review host's shipped Prompts.yaml, or every S2S provision 404s at runtime.
    /// The daemon test project deliberately does not reference the host project, so this reads the
    /// host's source-of-truth file from the repository — the same file
    /// <c>LmStreaming.Sample.Tests.SystemChatModesTests</c> parses through the production binding.
    /// </summary>
    [Fact]
    public void The_default_mode_id_is_defined_by_the_review_hosts_shipped_PromptsYaml()
    {
        var promptsPath = FindRepoFile(Path.Combine("samples", "LmStreaming.Sample", "Prompts.yaml"));
        promptsPath.Should().NotBeNull("the repository must contain the review host's Prompts.yaml");

        var yaml = File.ReadAllText(promptsPath!);
        yaml.Should()
            .Contain(
                $"- id: {new CodeReviewDaemonOptions().LmStreamingModeId}",
                "the daemon's default mode must resolve on a host that loads the shipped Prompts.yaml"
            );
    }

    private static string? FindRepoFile(string relativePath)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
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
        o.MaxConcurrentSubAgents.Should()
            .Be(5, "default matches the library's SubAgentOptions default; a profile raises it to fan out wider");
    }

    [Fact]
    public void Retry_governance_defaults_bound_the_context_stage()
    {
        var o = new CodeReviewDaemonOptions();

        o.MaxContextRetries.Should().Be(5, "a stuck ContextReady is parked after a bounded number of attempts");
        o.RetryBackoffBaseSeconds.Should().Be(30, "the first backoff replaces the old ~30s hot-loop");
        o.RetryBackoffCapSeconds.Should().Be(900, "the exponential backoff is capped at 15m");
    }

    [Fact]
    public void Model_role_knobs_default_to_empty_so_secondary_agents_inherit_the_primary()
    {
        var o = new CodeReviewDaemonOptions();

        o.ReviewModelId.Should().Be("claude-sonnet-5", "the primary dispatcher always has a concrete model");
        o.SubAgentModelId.Should().BeEmpty("empty ⇒ review sub-agents inherit ReviewModelId");
        o.KnowledgeModelId.Should()
            .BeEmpty(
                "empty ⇒ the at-close knowledge-extraction loop inherits ReviewModelId; set it (e.g. claude-opus-4.8) to run extraction on a dedicated model"
            );
    }
}
