using LmStreaming.Sample.Services;
using Microsoft.Extensions.Configuration;

namespace LmStreaming.Sample.Tests.Services;

/// <summary>
///     Coverage for the sample pricing resolver (#196, BUG 4): a model with a configured rate resolves to
///     pricing that computes cost, an unconfigured (e.g. flat-rate Copilot) model resolves to null so cost
///     is reported "unavailable" rather than a bogus zero, and lookup is case-insensitive.
/// </summary>
public class AppSettingsPricingResolverTests
{
    private static IConfiguration Config(params (string Key, string Value)[] entries)
    {
        var dict = entries.ToDictionary(e => e.Key, e => (string?)e.Value);
        return new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
    }

    [Fact]
    public void Resolve_ReturnsConfiguredRate_AndComputesCost()
    {
        var resolver = AppSettingsPricingResolver.FromConfiguration(Config(
            ("Pricing:Models:gpt-test:PromptPerMillion", "2.5"),
            ("Pricing:Models:gpt-test:CompletionPerMillion", "10")));

        var pricing = resolver.Resolve("gpt-test");

        pricing.Should().NotBeNull();
        pricing!.PromptPerMillion.Should().Be(2.5m);
        pricing.CompletionPerMillion.Should().Be(10m);
        // 1M prompt tokens at $2.5/M + 0.5M completion at $10/M = $2.5 + $5 = $7.5 = 7,500,000 micros.
        pricing.EstimateMicros(1_000_000, 500_000).Should().Be(7_500_000);
    }

    [Fact]
    public void Resolve_ReturnsNull_ForUnconfiguredModel()
    {
        var resolver = AppSettingsPricingResolver.FromConfiguration(Config(
            ("Pricing:Models:gpt-test:PromptPerMillion", "2.5"),
            ("Pricing:Models:gpt-test:CompletionPerMillion", "10")));

        // A flat-rate model with no configured public rate — cost must be "unavailable", not zero.
        resolver.Resolve("gpt-5.6-terra").Should().BeNull();
    }

    [Fact]
    public void Resolve_IsCaseInsensitive()
    {
        var resolver = AppSettingsPricingResolver.FromConfiguration(Config(
            ("Pricing:Models:GPT-Test:PromptPerMillion", "2.5"),
            ("Pricing:Models:GPT-Test:CompletionPerMillion", "10")));

        resolver.Resolve("gpt-test").Should().NotBeNull();
    }

    [Fact]
    public void FromConfiguration_SkipsEntriesMissingARate_AndAbsentSectionYieldsEmpty()
    {
        var resolver = AppSettingsPricingResolver.FromConfiguration(Config(
            ("Pricing:Models:partial:PromptPerMillion", "2.5")));

        resolver.Resolve("partial").Should().BeNull();
        resolver.Resolve("anything").Should().BeNull();
    }
}
