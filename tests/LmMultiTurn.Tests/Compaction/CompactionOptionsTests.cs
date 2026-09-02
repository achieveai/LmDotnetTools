using AchieveAi.LmDotnetTools.LmMultiTurn.Compaction;
using FluentAssertions;
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
    }
}
