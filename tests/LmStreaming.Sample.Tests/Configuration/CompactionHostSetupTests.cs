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
            }
        );

        var options = setup!.Options;
        options.ReserveMarginTokens.Should().Be(0);
        options.MinTailTokens.Should().Be(500);
        options.MaxCompactionsPerRun.Should().Be(5);
        options.CacheTtl.Should().Be(TimeSpan.Zero);
        options.KillSwitch.Should().BeTrue();
        options.Recall.DefaultLimit.Should().Be(3);
        options.WarnRatio.Should().Be(0.70, "unset knobs keep the library defaults");
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

    private sealed class FixedCapacity(string modelId, long window) : IModelCapacityResolver
    {
        public ModelCapacity? Resolve(string model) =>
            string.Equals(model, modelId, StringComparison.Ordinal) ? new ModelCapacity(window, null) : null;
    }
}
