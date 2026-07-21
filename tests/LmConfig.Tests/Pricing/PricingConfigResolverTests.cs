using AchieveAi.LmDotnetTools.LmConfig.Models;
using AchieveAi.LmDotnetTools.LmConfig.Pricing;

namespace AchieveAi.LmDotnetTools.LmConfig.Tests.Pricing;

public class PricingConfigResolverTests
{
    private static PricingConfigResolver Resolver() =>
        new(
            new Dictionary<string, PricingConfig>
            {
                ["model-A"] = new() { PromptPerMillion = 2.0, CompletionPerMillion = 8.0 },
            },
            source: "test-catalog",
            version: "2026-07-19");

    [Fact]
    public void Resolve_KnownModel_ReturnsPricingWithProvenance()
    {
        var pricing = Resolver().Resolve("model-A");

        Assert.NotNull(pricing);
        Assert.Equal(2.0m, pricing!.PromptPerMillion);
        Assert.Equal(8.0m, pricing.CompletionPerMillion);
        Assert.Equal("test-catalog", pricing.Source);
        Assert.Equal("2026-07-19", pricing.Version);

        // 1000 input + 500 output at $2/M + $8/M => 6000 micro-units.
        Assert.Equal(6000, pricing.EstimateMicros(1000, 500));
    }

    [Fact]
    public void Resolve_UnknownModel_ReturnsNull()
    {
        Assert.Null(Resolver().Resolve("no-such-model"));
    }
}
