using LmStreaming.Sample.Services.Discovery;
using Microsoft.Extensions.Configuration;

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

        var options = configuration
            .GetSection(SubAgentIntelligenceOptions.SectionName)
            .Get<SubAgentIntelligenceOptions>();

        options.Should().NotBeNull();
        options!.Tiers[3].Should().Equal("model-a", "model-b", "model-c");
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
