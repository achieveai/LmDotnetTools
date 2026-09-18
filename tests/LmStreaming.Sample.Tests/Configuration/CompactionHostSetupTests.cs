using AchieveAi.LmDotnetTools.LmCore.Models;
using AchieveAi.LmDotnetTools.LmMultiTurn.Compaction;
using LmStreaming.Sample.Configuration;
using Microsoft.Extensions.Configuration;

namespace LmStreaming.Sample.Tests.Configuration;

/// <summary>
/// Covers the host-side compaction switch: the <c>Compaction</c> section binds onto the library's
/// <see cref="CompactionOptions"/>, stays absent (null setup, loop unchanged) unless some route can reach
/// a mode above Off, and sizes the window from the same capacity resolver the context panel reads.
/// </summary>
public class CompactionHostSetupTests
{
    private const string Model = "claude-sonnet-4-5-20250929";

    private static CompactionSetup? Create(
        Dictionary<string, string?> values,
        IModelCapacityResolver? capacity = null,
        string providerId = "test-anthropic"
    )
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        return CompactionHostSetup.Create(CompactionHostSetup.BindOptions(configuration), capacity, providerId);
    }

    [Fact]
    public void MissingSection_LeavesTheFeatureAbsent()
    {
        Create([]).Should().BeNull("a null setup is the library's 'feature absent' contract");
    }

    [Fact]
    public void CheckedInHostDefaults_EnableTheRecommendedProductionProfile()
    {
        var configuration = new ConfigurationBuilder().AddJsonFile(AppsettingsPath).Build();
        var options = CompactionHostSetup.BindOptions(configuration);

        options.Mode.Should().Be(CompactionMode.Compact);
        options.ClearAnsweredToolResultsOnly.Should().BeTrue();
        options.TextTokenizer.Should().Be("o200k");
        options.MeasureCompactionGainOnStoredRows.Should().BeFalse("ADR 0020 does not recommend the H8 arm");
        CompactionHostSetup
            .Create(options, capacityResolver: null, providerId: "test")!
            .TextTokens.Should()
            .NotBeNull("the checked-in profile uses the real o200k tokenizer");
    }

    [Fact]
    public void CheckedInHostDefaults_CanBeDisabledByConfigurationOverride()
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(AppsettingsPath)
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Compaction:Mode"] = "Off" })
            .Build();

        CompactionHostSetup
            .Create(CompactionHostSetup.BindOptions(configuration), capacityResolver: null, providerId: "test")
            .Should()
            .BeNull("operators retain a complete opt-out");
    }

    [Fact]
    public void ModeOffWithNoRouteAboveOff_LeavesTheFeatureAbsent()
    {
        Create(
                new Dictionary<string, string?>
                {
                    ["Compaction:Mode"] = "Off",
                    ["Compaction:ModeByRoute:test-anthropic/" + Model] = "Off",
                }
            )
            .Should()
            .BeNull();
    }

    [Fact]
    public void GlobalMode_BuildsASetupForTheProvider_WithTheWindowFromTheCapacityResolver()
    {
        var setup = Create(
            new Dictionary<string, string?> { ["Compaction:Mode"] = "compact" },
            new FixedCapacity(Model, 40_000)
        );

        setup.Should().NotBeNull();
        setup!.Options.Mode.Should().Be(CompactionMode.Compact, "the mode parses case-insensitively");
        setup.ProviderId.Should().Be("test-anthropic");
        setup.ResolveWindowTokens.Should().NotBeNull();
        setup.ResolveWindowTokens!(Model).Should().Be(40_000);
        setup.ResolveWindowTokens!("unknown-model").Should().BeNull("an unknown window must read as unknown");
        setup.ResolveWindowTokens!(null).Should().BeNull();
    }

    [Fact]
    public void NoTextTokenizer_LeavesTheHeuristicInPlace()
    {
        var setup = Create(new Dictionary<string, string?> { ["Compaction:Mode"] = "compact" });

        setup!.TextTokens.Should().BeNull("null keeps the library's length / 4 estimate");
    }

    [Fact]
    public void O200kTextTokenizer_CountsWithTheRealEncoding()
    {
        var setup = Create(
            new Dictionary<string, string?> { ["Compaction:Mode"] = "compact", ["Compaction:TextTokenizer"] = "o200k" }
        );

        setup!.TextTokens.Should().NotBeNull();
        // 84 characters of line-numbered prose: the heuristic charges 21, the encoding far fewer.
        const string LineNumberedProse =
            "     1\tThe gateway's default per-request timeout is 30000 ms.\n     2\tIt was 45000 ms.";
        var counted = setup.TextTokens!(LineNumberedProse);
        counted.Should().BeInRange(20, 40, "o200k counts words and numbers, not quarters of characters");
        setup.TextTokens!(null).Should().Be(0);
        setup.TextTokens!("").Should().Be(0);
    }

    [Fact]
    public void UnknownTextTokenizer_IsRefusedAtStartup()
    {
        var act = () =>
            Create(
                new Dictionary<string, string?>
                {
                    ["Compaction:Mode"] = "compact",
                    ["Compaction:TextTokenizer"] = "cl100k",
                }
            );

        act.Should().Throw<InvalidOperationException>().WithMessage("*cl100k*o200k*");
    }

    [Fact]
    public void NoCapacityResolver_LeavesTheWindowUnknown()
    {
        var setup = Create(new Dictionary<string, string?> { ["Compaction:Mode"] = "Warn" });

        setup.Should().NotBeNull();
        setup!.ResolveWindowTokens.Should().BeNull("the policy then answers capacity_unknown");
    }

    [Fact]
    public void RouteOverride_EnablesOnlyThatRoute()
    {
        var setup = Create(
            new Dictionary<string, string?> { ["Compaction:ModeByRoute:test-anthropic/" + Model] = "Shadow" }
        );

        setup.Should().NotBeNull("one route above Off is enough to build the setup");
        setup!.Options.ResolveMode("test-anthropic", Model).Should().Be(CompactionMode.Shadow);
        setup.Options.ResolveMode("openai", "gpt-4o").Should().Be(CompactionMode.Off);
    }

    [Fact]
    public void TestProfileKnobs_Bind()
    {
        var setup = Create(
            new Dictionary<string, string?>
            {
                ["Compaction:Mode"] = "Compact",
                ["Compaction:ReserveMarginTokens"] = "0",
                ["Compaction:MinTailTokens"] = "500",
                ["Compaction:MaxCompactionsPerRun"] = "5",
                ["Compaction:CacheTtl"] = "00:00:00",
                ["Compaction:KillSwitch"] = "true",
                ["Compaction:Recall:DefaultLimit"] = "3",
                ["Compaction:ClearAnsweredToolResultsOnly"] = "true",
                ["Compaction:MeasureCompactionGainOnStoredRows"] = "true",
            }
        );

        var options = setup!.Options;
        options.ReserveMarginTokens.Should().Be(0);
        options.MinTailTokens.Should().Be(500);
        options.MaxCompactionsPerRun.Should().Be(5);
        options.CacheTtl.Should().Be(TimeSpan.Zero);
        options.KillSwitch.Should().BeTrue();
        options.Recall.DefaultLimit.Should().Be(3);
        options.ClearAnsweredToolResultsOnly.Should().BeTrue();
        options.MeasureCompactionGainOnStoredRows.Should().BeTrue();
        options.WarnRatio.Should().Be(0.70, "unset knobs keep the library defaults");
    }

    [Fact]
    public void EmptyClearToolResultsKeepTurns_KeepsTheDefault_SoClearingCannotBeTurnedOffFromTheCommandLine()
    {
        // The binder treats an empty value as unset, so `--Compaction:ClearToolResultsKeepTurns=` keeps the default
        // of 3 rather than binding null. The eval's sum-* arms therefore pass 99 to make the proactive clear inert.
        var setup = Create(
            new Dictionary<string, string?>
            {
                ["Compaction:Mode"] = "Compact",
                ["Compaction:ClearToolResultsKeepTurns"] = "",
            }
        );

        setup!.Options.ClearToolResultsKeepTurns.Should().Be(3);
    }

    [Fact]
    public void SummaryPromptFile_IsReadIntoTheSetup_WithoutItsFrontMatter()
    {
        var path = Path.Combine(Path.GetTempPath(), $"prompt-{Guid.NewGuid():N}.md");
        File.WriteAllText(path, "---\nid: v1\n---\nSummarize briefly.");
        try
        {
            var setup = Create(
                new Dictionary<string, string?>
                {
                    ["Compaction:Mode"] = "Compact",
                    ["Compaction:SummaryPromptPath"] = path,
                }
            );

            setup!.SummarySystemPrompt.Should().Be("Summarize briefly.");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void NoSummaryPromptPath_LeavesTheBuiltInPrompt()
    {
        Create(new Dictionary<string, string?> { ["Compaction:Mode"] = "Compact" })!
            .SummarySystemPrompt.Should()
            .BeNull();
    }

    [Fact]
    public void AMissingSummaryPromptFile_Throws()
    {
        var act = () =>
            Create(
                new Dictionary<string, string?>
                {
                    ["Compaction:Mode"] = "Compact",
                    ["Compaction:SummaryPromptPath"] = "nope.md",
                }
            );

        act.Should().Throw<FileNotFoundException>();
    }

    [Fact]
    public void UnknownMode_FailsAtBind()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Compaction:Mode"] = "sometimes" })
            .Build();

        var act = () => CompactionHostSetup.BindOptions(configuration);

        act.Should().Throw<InvalidOperationException>();
    }

    private static string AppsettingsPath { get; } =
        Path.GetFullPath(
            Path.Combine(
                AppContext.BaseDirectory,
                "..",
                "..",
                "..",
                "..",
                "..",
                "samples",
                "LmStreaming.Sample",
                "appsettings.json"
            )
        );

    private sealed class FixedCapacity(string modelId, long window) : IModelCapacityResolver
    {
        public ModelCapacity? Resolve(string model) =>
            string.Equals(model, modelId, StringComparison.Ordinal) ? new ModelCapacity(window, null) : null;
    }
}
