using LmStreaming.Sample.Services.Discovery;
using LmStreaming.Sample.Tests.TestDoubles;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace LmStreaming.Sample.Tests.Services.Discovery;

public sealed class SubAgentIntelligenceOptionsTests
{
    [Fact]
    public void ConfigurationBinding_PreservesTierCandidateOrder()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SubAgentIntelligence:Tiers:3:0"] = "model-a",
                ["SubAgentIntelligence:Tiers:3:1"] = "model-b",
                ["SubAgentIntelligence:Tiers:3:2"] = "model-c",
            })
            .Build();

        var options = SubAgentIntelligenceOptions.Load(
            configuration,
            new CapturingLogger<SubAgentIntelligenceOptions>());

        options.Tiers[3].Should().Equal("model-a", "model-b", "model-c");
    }

    [Fact]
    public void Load_LogsAndSkipsMalformedAndOutOfRangeKeysWithoutDroppingValidMappings()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SubAgentIntelligence:Tiers:not-an-integer:0"] = "bad-model",
                ["SubAgentIntelligence:Tiers:7:0"] = "out-of-range-model",
                ["SubAgentIntelligence:Tiers:4:0"] = "valid-model",
            })
            .Build();
        var logger = new CapturingLogger<SubAgentIntelligenceOptions>();

        var options = SubAgentIntelligenceOptions.Load(configuration, logger);

        options.Tiers.Should().ContainSingle().Which.Key.Should().Be(4);
        options.Tiers[4].Should().Equal("valid-model");
        logger.Entries.Count(entry => entry.Level == LogLevel.Error).Should().Be(2);
    }

    [Fact]
    public void Load_LogsAndSkipsDuplicateNormalizedTierKeyWhileKeepingTheFirst()
    {
        // "3" and "03" both normalize to integer tier 3; the second is a duplicate and must be
        // logged and skipped, leaving a single tier-3 mapping.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SubAgentIntelligence:Tiers:3:0"] = "first-model",
                ["SubAgentIntelligence:Tiers:03:0"] = "duplicate-model",
            })
            .Build();
        var logger = new CapturingLogger<SubAgentIntelligenceOptions>();

        var options = SubAgentIntelligenceOptions.Load(configuration, logger);

        options.Tiers.Should().ContainSingle().Which.Key.Should().Be(3);
        options.Tiers[3].Should().NotBeEmpty();
        logger.Entries.Count(entry => entry.Level == LogLevel.Error).Should().Be(1);
    }

    [Fact]
    public void Appsettings_TierStubUsesEmptyOrderedArraysForEverySupportedTier()
    {
        var appsettingsPath = Path.GetFullPath(
            Path.Combine(
                AppContext.BaseDirectory,
                "..",
                "..",
                "..",
                "..",
                "..",
                "samples",
                "LmStreaming.Sample",
                "appsettings.json"));
        using var document = JsonDocument.Parse(File.ReadAllText(appsettingsPath));

        var tiers = document.RootElement
            .GetProperty("SubAgentIntelligence")
            .GetProperty("Tiers");

        tiers.ValueKind.Should().Be(JsonValueKind.Object);
        tiers.EnumerateObject().Select(tier => tier.Name)
            .Should().Equal("0", "1", "2", "3", "4", "5", "6");
        tiers.EnumerateObject().Should().OnlyContain(tier =>
            tier.Value.ValueKind == JsonValueKind.Array
            && tier.Value.GetArrayLength() == 0);
    }
}
