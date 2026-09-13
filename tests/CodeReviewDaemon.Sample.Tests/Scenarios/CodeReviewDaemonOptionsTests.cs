using CodeReviewDaemon.Sample.Configuration;
using Microsoft.Extensions.Configuration;

namespace CodeReviewDaemon.Sample.Tests.Scenarios;

/// <summary>
/// P2.3 — the daemon's feature flags are <b>conservative by default</b> (collect-only, GitHub-only,
/// review nothing until allow-listed) and bind from the <c>CodeReviewDaemon</c> section.
/// </summary>
public sealed class CodeReviewDaemonOptionsTests
{
    [Theory]
    [InlineData("EnableJudgeAgent", "false")]
    [InlineData("EnableKnowledgeAgent", "true")]
    [InlineData("JudgeModelId", "old-model")]
    [InlineData("ReviewMaxTurns", "150")]
    [InlineData("EnableToolAssistedReview", "true")]
    [InlineData("EnableReviewerWrites", "true")]
    [InlineData("MergeNotesBranchOnClose", "false")]
    [InlineData("EnableHostSummaryFallback", "true")]
    [InlineData("EnableReviewFeedbackAgent", "true")]
    [InlineData("EnableABVariants", "true")]
    [InlineData("OverflowEscalationModelId", "old-model")]
    [InlineData("KnowledgeModelId", "old-model")]
    [InlineData("VariantModelId", "old-model")]
    [InlineData("VariantReasoningEffort", "high")]
    [InlineData("ReviewMaxTokens", "500")]
    [InlineData("ReviewReasoningEffort", "high")]
    [InlineData("ReviewSubAgentUnknownQuiescenceSeconds", "10")]
    [InlineData("RequireSkillSupport", "false")]
    public void Retired_stage_settings_are_ignored_with_migration_warning(string name, string value)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["CodeReviewDaemon:" + name] = value,
                    ["CodeReviewDaemon:WorkflowPath"] = "trusted/workflow.yaml",
                }
            )
            .Build();
        var warnings = new List<string>();
        var section = config.GetSection("CodeReviewDaemon");
        CodeReviewDaemonOptions.ValidateWorkflowConfiguration(section, warnings.Add);
        warnings.Should().ContainSingle().Which.Should().Contain(name).And.Contain("WorkflowPath");
        var options = section.Get<CodeReviewDaemonOptions>()!;
        options.WorkflowPath.Should().Be("trusted/workflow.yaml");
        options.EnableCommentPosting.Should().BeFalse();
    }

    [Fact]
    public void Defaults_are_all_conservative()
    {
        var options = new CodeReviewDaemonOptions();

        options.EnableCommentPosting.Should().BeFalse("posting to a live PR is outward-facing — opt-in only");
        options.EnableAdoProvider.Should().BeFalse("the daemon is GitHub-only until ADO is enabled");
        options.EnabledRepos.Should().BeEmpty("no repo is reviewed until explicitly allow-listed");
        options.DatabasePath.Should().BeNull("the default database path is resolved at startup, not bound");
    }

    [Fact]
    public void Binds_every_flag_from_the_CodeReviewDaemon_section()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["CodeReviewDaemon:EnableCommentPosting"] = "true",
                    ["CodeReviewDaemon:EnableAdoProvider"] = "true",
                    ["CodeReviewDaemon:EnabledRepos:0"] = "achieveai/LmDotnetTools",
                    ["CodeReviewDaemon:EnabledRepos:1"] = "contoso/widgets",
                    ["CodeReviewDaemon:WorkflowPath"] = ".review/workflow.yaml",
                }
            )
            .Build();

        var options = config.GetSection(CodeReviewDaemonOptions.SectionName).Get<CodeReviewDaemonOptions>();

        options.Should().NotBeNull();
        options!.EnableCommentPosting.Should().BeTrue();
        options.EnableAdoProvider.Should().BeTrue();
        options.EnabledRepos.Should().Equal("achieveai/LmDotnetTools", "contoso/widgets");
        // A misspelled key binds to "" without complaint, and "" is precisely the value that keeps the
        // judge on the reviewer's own model — the failure this option exists to make visible.
        options.WorkflowPath.Should().Be(".review/workflow.yaml");
    }

    [Fact]
    public void Binds_the_pooled_review_workspace_options_from_the_CodeReviewDaemon_section()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["CodeReviewDaemon:ReviewPoolSize"] = "4",
                    ["CodeReviewDaemon:ReviewPoolHostRoot"] = "/var/crd/review-pool",
                    ["CodeReviewDaemon:ScratchDirName"] = "work",
                    ["CodeReviewDaemon:WritableToolAllowList:0"] = "PrNotes",
                }
            )
            .Build();

        var options = config.GetSection(CodeReviewDaemonOptions.SectionName).Get<CodeReviewDaemonOptions>();

        options.Should().NotBeNull();
        options!.ReviewPoolSize.Should().Be(4);
        options.ReviewPoolHostRoot.Should().Be("/var/crd/review-pool");
        options.ScratchDirName.Should().Be("work");
        // A distinctive value (not one of the ["Write","Edit","Bash"] defaults) proves the list bound. Note
        // the config binder APPENDS bound items onto a non-empty default collection rather than replacing it,
        // so the configured entry is asserted via Contain rather than exact equality.
        options.WritableToolAllowList.Should().Contain("PrNotes");
    }

    [Fact]
    public void Review_sub_agent_barrier_options_default_and_bind_from_the_CodeReviewDaemon_section()
    {
        new CodeReviewDaemonOptions().ReviewStageDeadlineMinutes.Should().Be(30);
        new CodeReviewDaemonOptions().ReviewSubAgentBarrierQuietSeconds.Should().Be(2);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["CodeReviewDaemon:ReviewStageDeadlineMinutes"] = "45",
                    ["CodeReviewDaemon:ReviewSubAgentBarrierQuietSeconds"] = "5",
                }
            )
            .Build();

        var options = config.GetSection(CodeReviewDaemonOptions.SectionName).Get<CodeReviewDaemonOptions>();

        options.Should().NotBeNull();
        options!.ReviewStageDeadlineMinutes.Should().Be(45);
        options.ReviewSubAgentBarrierQuietSeconds.Should().Be(5);
    }

    // ── eval corpus sweep (#400) ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Off by default, and the default window is a usable number rather than zero.
    /// </summary>
    [Fact]
    public void The_eval_corpus_sweep_is_off_by_default_with_a_usable_default_window()
    {
        var options = new CodeReviewDaemonOptions();

        options
            .EvalCorpusSweepIntervalMinutes.Should()
            .Be(0, "nothing about the sweep runs until an operator asks for it");
        options
            .EvalCorpusSweepWindow.Should()
            .Be(
                1000,
                "the window default must be usable on its own — an interval alone has to be enough "
                    + "configuration to turn the sweep on"
            );
    }

    [Fact]
    public void Binds_the_eval_corpus_sweep_options_from_the_CodeReviewDaemon_section()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["CodeReviewDaemon:EvalCorpusSweepIntervalMinutes"] = "90",
                    ["CodeReviewDaemon:EvalCorpusSweepWindow"] = "250",
                }
            )
            .Build();

        var options = config.GetSection(CodeReviewDaemonOptions.SectionName).Get<CodeReviewDaemonOptions>();

        options.Should().NotBeNull();
        options!.EvalCorpusSweepIntervalMinutes.Should().Be(90);
        options.EvalCorpusSweepWindow.Should().Be(250);
    }

    /// <summary>
    /// The case the binder gets wrong if the property is written the way C# would suggest. The
    /// configuration binder does <b>not</b> enforce the <c>required</c> keyword — it binds whatever
    /// it finds and leaves the rest — so a <c>required int</c> absent from configuration arrives as
    /// <c>0</c>, silently, and the schedule refuses a zero window at construction. A missing line in
    /// a JSON file would then be a daemon that will not start.
    /// <para>
    /// Both halves are pinned because they fail differently: an <b>absent</b> key must leave the
    /// default alone, and an <b>explicitly null</b> key must too — the binder skips a null value
    /// rather than writing <c>default(int)</c> over the initializer, and reading it as zero is the
    /// same "unknown widened into a measurement" the eval code refuses everywhere else.
    /// </para>
    /// </summary>
    [Fact]
    public void An_absent_or_null_eval_sweep_key_keeps_its_default_rather_than_binding_zero()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    // The section exists and names a sibling knob, so this is a bound section with the
                    // sweep's keys missing — not an absent section, which would be a weaker case.
                    ["CodeReviewDaemon:EnableAdoProvider"] = "true",
                    ["CodeReviewDaemon:EvalCorpusSweepWindow"] = null,
                }
            )
            .Build();

        var options = config.GetSection(CodeReviewDaemonOptions.SectionName).Get<CodeReviewDaemonOptions>();

        options.Should().NotBeNull();
        options!.EnableAdoProvider.Should().BeTrue("the section genuinely bound");
        options.EvalCorpusSweepWindow.Should().Be(1000, "an explicit null must not overwrite the default with 0");
        options
            .EvalCorpusSweepIntervalMinutes.Should()
            .Be(0, "an absent interval is the off default, which is what 0 means here");
    }
}
