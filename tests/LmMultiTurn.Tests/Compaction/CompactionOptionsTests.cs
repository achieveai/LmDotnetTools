using AchieveAi.LmDotnetTools.LmMultiTurn.Compaction;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace LmMultiTurn.Tests.Compaction;

public class CompactionOptionsTests
{
    [Fact]
    public void ResolveMode_PrefersTheRouteOverride_ThenTheModel_ThenTheDefault()
    {
        var options = new CompactionOptions
        {
            Mode = CompactionMode.Compact,
            ModeByRoute = new Dictionary<string, CompactionMode>
            {
                ["openai/gpt-4o"] = CompactionMode.Shadow,
                ["claude-sonnet"] = CompactionMode.Warn,
            },
        };

        options.ResolveMode("openai", "gpt-4o").Should().Be(CompactionMode.Shadow);
        options.ResolveMode("anthropic", "claude-sonnet").Should().Be(CompactionMode.Warn);
        options.ResolveMode("anthropic", "claude-opus").Should().Be(CompactionMode.Compact);
        options.ResolveMode(null, null).Should().Be(CompactionMode.Compact);
    }

    [Fact]
    public void WithModeCeiling_OnlyLowersTheMode()
    {
        var options = new CompactionOptions { Mode = CompactionMode.Shadow };

        options.WithModeCeiling(CompactionMode.Warn).Mode.Should().Be(CompactionMode.Warn);
        options.WithModeCeiling(CompactionMode.Compact).Mode.Should().Be(CompactionMode.Shadow);
    }

    [Fact]
    public void IsKilled_ReadsTheConfigFlagOrTheEnvironmentVariable()
    {
        new CompactionOptions { KillSwitch = true }
            .IsKilled(_ => null)
            .Should()
            .BeTrue();
        new CompactionOptions().IsKilled(_ => "1").Should().BeTrue();
        new CompactionOptions().IsKilled(_ => "true").Should().BeTrue();
        new CompactionOptions().IsKilled(_ => "0").Should().BeFalse();
        new CompactionOptions().IsKilled(_ => null).Should().BeFalse();
    }

    [Fact]
    public void Defaults_MatchTheSpecHypotheses()
    {
        var o = new CompactionOptions();

        o.Mode.Should().Be(CompactionMode.Off);
        (o.WarnRatio, o.CompactRatio, o.HardRatio, o.TargetRatio).Should().Be((0.70, 0.80, 0.90, 0.45));
        o.ReserveMarginTokens.Should().Be(2048);
        (o.MinTailTokens, o.MaxTailTokens).Should().Be((8_000L, 24_000L));
        (o.NarrativeTokenCap, o.CheckpointTokenCap).Should().Be((2_000L, 6_000L));
        (o.CooldownGenerations, o.CooldownNewTokens).Should().Be((3, 10_000L));
        o.MaxCompactionsPerRun.Should().Be(2);
        o.ExpectedFutureGenerations.Should().Be(3);
        o.CorrectionLookbackRuns.Should().Be(3);
        o.CacheTtl.Should().Be(TimeSpan.FromMinutes(5));
        o.WarnAbsoluteTokens.Should().Be(100_000);
        o.ObservationHistoryLength.Should().Be(50);
        (o.Recall.DefaultLimit, o.Recall.MaxLimit).Should().Be((10, 40));
        (o.Recall.DefaultMaxChars, o.Recall.MaxMaxChars, o.Recall.RowCharCap).Should().Be((8_000, 32_000, 1_500));
        (o.ToolResultViewCapRatio, o.ToolResultViewCapMinChars, o.ToolResultViewCapMaxChars)
            .Should()
            .Be((0.20, 4_000, 100_000));
        o.ClearToolResultsKeepTurns.Should().Be(3);
        (o.MinTailRatio, o.MaxTailRatio, o.CheckpointTokenCapRatio).Should().Be((0.15, 0.60, 0.15));
        o.MinCompactionGainRatio.Should().Be(0.10);
        (o.MaxCompactionsPerThreadWindow, o.ThreadCompactionWindow).Should().Be((6, TimeSpan.FromMinutes(10)));
        o.FailureBackoffGenerations.Should().Be(2);
        (o.SummaryTimeout, o.SummaryAttempts).Should().Be((TimeSpan.FromMinutes(2), 2));
        (o.SummaryMaxOutputTokens, o.SummaryRowCharCap, o.SummaryPromptMaxChars).Should().Be((8_000, 4_000, 400_000));
    }

    [Fact]
    public void Defaults_LeaveEveryEvalCheckOff()
    {
        var o = new CompactionOptions();

        o.Checks.Should().Be(CompactionChecks.None);
        o.Checks.Any.Should().BeFalse();
        o.ToolKnowledge.Should().BeNull();
        o.SummaryPromptPath.Should().BeNull();
        o.SummaryPrefixMode.Should().Be(SummaryPrefixMode.Cold);
        o.ShellTrimChars.Should().Be(1_500);
    }

    [Fact]
    public void Validate_RejectsANonPositiveShellTrim()
    {
        var act = () => new CompactionOptions { ShellTrimChars = 0 }.Validate();

        act.Should()
            .Throw<ArgumentOutOfRangeException>()
            .Which.ParamName.Should()
            .Be(nameof(CompactionOptions.ShellTrimChars));
    }

    [Fact]
    public void Checks_BindFromConfiguration()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Compaction:Checks:Rc1ResourceDedupe"] = "true",
                    ["Compaction:Checks:Rc3OpenExchanges"] = "true",
                    ["Compaction:ToolKnowledge:MyTool:Kind"] = "Resource",
                    ["Compaction:ToolKnowledge:MyTool:Identity:0"] = "id",
                    ["Compaction:SummaryPromptPath"] = "prompts/v1.md",
                    ["Compaction:SummaryPrefixMode"] = "CachedPrefix",
                }
            )
            .Build();

        var o = configuration.GetSection("Compaction").Get<CompactionOptions>()!;

        o.Checks.Rc1ResourceDedupe.Should().BeTrue();
        o.Checks.Rc2ShellRetention.Should().BeFalse();
        o.Checks.Rc3OpenExchanges.Should().BeTrue();
        o.ToolKnowledge!["MyTool"].Kind.Should().Be(ToolKind.Resource);
        o.ToolKnowledge["MyTool"].Identity.Should().Equal("id");
        o.SummaryPromptPath.Should().Be("prompts/v1.md");
        o.SummaryPrefixMode.Should().Be(SummaryPrefixMode.CachedPrefix);
    }

    [Fact]
    public void FailureBackoff_DoublesWithEachConsecutiveFailure_UpToTheCap()
    {
        var o = new CompactionOptions();

        Enumerable.Range(0, 8).Select(o.FailureBackoff).Should().Equal(0, 2, 4, 8, 16, 32, 32, 32);
        new CompactionOptions { FailureBackoffGenerations = 0 }
            .FailureBackoff(3)
            .Should()
            .Be(0, "0 turns backoff off");
    }

    [Fact]
    public void SummaryPromptCharBudget_IsThreeQuartersOfTheSummaryWindowAfterTheOutput_WithinTheBounds()
    {
        var o = new CompactionOptions();

        o.SummaryPromptCharBudget(null).Should().Be(400_000, "an unknown window gets the ceiling");
        o.SummaryPromptCharBudget(32_000).Should().Be(72_000, "(32,000 - 8,000) x 4 chars x 0.75");
        o.SummaryPromptCharBudget(1_000_000).Should().Be(400_000);
        o.SummaryPromptCharBudget(9_000).Should().Be(20_000, "never below the floor");
    }

    [Fact]
    public void ToolResultViewCapChars_IsAFifthOfTheUsableWindowInChars_WithinTheFloorAndCeiling()
    {
        var o = new CompactionOptions();

        o.ToolResultViewCapChars(27_904).Should().Be(22_323, "0.20 x 27,904 tokens x 4 chars per token");
        o.ToolResultViewCapChars(2_300).Should().Be(4_000, "a tiny window still shows a useful head and tail");
        o.ToolResultViewCapChars(1_000_000).Should().Be(100_000, "a huge window never admits a megabyte result");
        o.ToolResultViewCapChars(null).Should().Be(100_000, "an unknown window gets the ceiling, deterministically");
    }

    [Fact]
    public void WindowScaledKnobs_ShrinkForASmallWindow_AndKeepTheAbsoluteValuesForALargeOne()
    {
        var o = new CompactionOptions();

        // 32k window, 4,096 reserve: the absolute 8k floor plus a 6k envelope cannot fit beside a 17k prefix.
        o.EffectiveMinTailTokens(27_904).Should().Be(4_185);
        o.EffectiveMaxTailTokens(27_904).Should().Be(16_742);
        o.EffectiveCheckpointTokenCap(27_904).Should().Be(4_185);

        o.EffectiveMinTailTokens(190_000).Should().Be(8_000);
        o.EffectiveMaxTailTokens(190_000).Should().Be(24_000);
        o.EffectiveCheckpointTokenCap(190_000).Should().Be(6_000);

        o.EffectiveCheckpointTokenCap(2_300).Should().Be(1_000, "the envelope cap never scales below its floor");
        o.EffectiveMinTailTokens(null).Should().Be(8_000, "an unknown window keeps the absolute values");
    }

    [Theory]
    [InlineData(nameof(CompactionOptions.ToolResultViewCapRatio))]
    [InlineData(nameof(CompactionOptions.ToolResultViewCapMinChars))]
    [InlineData(nameof(CompactionOptions.ToolResultViewCapMaxChars))]
    [InlineData(nameof(CompactionOptions.ClearToolResultsKeepTurns))]
    [InlineData(nameof(CompactionOptions.MinTailRatio))]
    [InlineData(nameof(CompactionOptions.MaxTailRatio))]
    [InlineData(nameof(CompactionOptions.CheckpointTokenCapRatio))]
    [InlineData(nameof(CompactionOptions.MinCompactionGainRatio))]
    [InlineData(nameof(CompactionOptions.MaxCompactionsPerThreadWindow))]
    [InlineData(nameof(CompactionOptions.ThreadCompactionWindow))]
    [InlineData(nameof(CompactionOptions.FailureBackoffGenerations))]
    [InlineData(nameof(CompactionOptions.SummaryTimeout))]
    [InlineData(nameof(CompactionOptions.SummaryAttempts))]
    [InlineData(nameof(CompactionOptions.SummaryMaxOutputTokens))]
    [InlineData(nameof(CompactionOptions.SummaryRowCharCap))]
    [InlineData(nameof(CompactionOptions.SummaryPromptMaxChars))]
    public void Validate_NamesTheOutOfRangeFitKnob(string option)
    {
        var invalid = option switch
        {
            nameof(CompactionOptions.ToolResultViewCapRatio) => new CompactionOptions { ToolResultViewCapRatio = 0 },
            nameof(CompactionOptions.ToolResultViewCapMinChars) => new CompactionOptions
            {
                ToolResultViewCapMinChars = 200_000,
            },
            nameof(CompactionOptions.ToolResultViewCapMaxChars) => new CompactionOptions
            {
                ToolResultViewCapMaxChars = 0,
            },
            nameof(CompactionOptions.ClearToolResultsKeepTurns) => new CompactionOptions
            {
                ClearToolResultsKeepTurns = 0,
            },
            nameof(CompactionOptions.MinTailRatio) => new CompactionOptions { MinTailRatio = 1.5 },
            nameof(CompactionOptions.MaxTailRatio) => new CompactionOptions { MaxTailRatio = -0.1 },
            nameof(CompactionOptions.MinCompactionGainRatio) => new CompactionOptions { MinCompactionGainRatio = 1 },
            nameof(CompactionOptions.MaxCompactionsPerThreadWindow) => new CompactionOptions
            {
                MaxCompactionsPerThreadWindow = -1,
            },
            nameof(CompactionOptions.ThreadCompactionWindow) => new CompactionOptions
            {
                ThreadCompactionWindow = TimeSpan.Zero,
            },
            nameof(CompactionOptions.FailureBackoffGenerations) => new CompactionOptions
            {
                FailureBackoffGenerations = -1,
            },
            nameof(CompactionOptions.SummaryTimeout) => new CompactionOptions { SummaryTimeout = TimeSpan.Zero },
            nameof(CompactionOptions.SummaryAttempts) => new CompactionOptions { SummaryAttempts = 0 },
            nameof(CompactionOptions.SummaryMaxOutputTokens) => new CompactionOptions { SummaryMaxOutputTokens = 0 },
            nameof(CompactionOptions.SummaryRowCharCap) => new CompactionOptions { SummaryRowCharCap = 10 },
            nameof(CompactionOptions.SummaryPromptMaxChars) => new CompactionOptions { SummaryPromptMaxChars = 1_000 },
            _ => new CompactionOptions { CheckpointTokenCapRatio = 0 },
        };

        new CompactionOptions().Invoking(o => o.Validate()).Should().NotThrow();
        new CompactionOptions { ClearToolResultsKeepTurns = null }
            .Invoking(o => o.Validate())
            .Should()
            .NotThrow("null turns tool-result clearing off");
        invalid
            .Invoking(o => o.Validate())
            .Should()
            .Throw<ArgumentOutOfRangeException>()
            .Which.ParamName.Should()
            .Be(option);
    }
}
