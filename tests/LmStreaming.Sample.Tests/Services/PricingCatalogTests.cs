using AchieveAi.LmDotnetTools.LmConfig.Models;
using AchieveAi.LmDotnetTools.LmCore.Models;
using AchieveAi.LmDotnetTools.LmMultiTurn.UsageAccounting;
using LmStreaming.Sample.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace LmStreaming.Sample.Tests.Services;

/// <summary>
///     The host-side half of #328/#378. #365 gave <c>PricingConfigResolver</c> a producer, but the
///     registration only fires for a host that calls <c>AddLmConfig</c>, and no host did — so a review run's
///     <c>UsageRecord.EstimatedPublicCostMicros</c> stayed null however good the supply side got.
///     <para>
///         These tests assert the consequence rather than the registration: a record fed through the same
///         <see cref="UsageLedger" /> production uses comes back with a cost, computed from configured rates,
///         stamped with the configured catalog version, and resolvable under every name the model answers to.
///         A test that merely asserted "an IPricingResolver is registered" would pass over an empty catalog,
///         which is indistinguishable from no registration at all.
///     </para>
/// </summary>
public class PricingCatalogTests
{
    private static IConfiguration Config(params (string Key, string Value)[] entries)
    {
        var dict = entries.ToDictionary(e => e.Key, e => (string?)e.Value);
        return new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
    }

    private static IPricingResolver ResolverFrom(IConfiguration configuration)
    {
        var services = new ServiceCollection();
        _ = services.AddLogging();
        _ = services.AddConfiguredPricing(configuration);
        return services.BuildServiceProvider().GetRequiredService<IPricingResolver>();
    }

    private static UsageRecord Observation(string effectiveModel, long input, long output) => new()
    {
        LogicalCallId = "call-1",
        ProviderAttemptId = "attempt-1",
        RootConversationId = "conv-1",
        RequestedModel = effectiveModel,
        EffectiveModel = effectiveModel,
        InputTokens = input,
        OutputTokens = output,
    };

    [Fact]
    public void AReviewRunsUsageRecord_CarriesACostWhenTheHostConfiguresTheModelItRan()
    {
        var resolver = ResolverFrom(Config(
            ("Pricing:Models:claude-sonnet-4-5:PromptPerMillion", "3"),
            ("Pricing:Models:claude-sonnet-4-5:CompletionPerMillion", "15")));
        var ledger = new UsageLedger("conv-1", resolver);

        var record = ledger.UpsertAttempt(Observation("claude-sonnet-4-5", 1_000_000, 200_000));

        // 1M prompt at $3/M + 0.2M completion at $15/M = $3 + $3 = $6 = 6,000,000 micros.
        record.EstimatedPublicCostMicros.Should().Be(6_000_000);
        record.CostProvenance.Should().Be(CostProvenance.PublicEstimate);
    }

    [Fact]
    public void TheConfiguredCatalogVersion_IsStampedOnEveryResolvedRate()
    {
        var resolver = ResolverFrom(Config(
            ("Pricing:Version", "2026-08-01"),
            ("Pricing:Models:claude-sonnet-4-5:PromptPerMillion", "3"),
            ("Pricing:Models:claude-sonnet-4-5:CompletionPerMillion", "15")));

        var pricing = resolver.Resolve("claude-sonnet-4-5");

        // Nothing in this repository refreshes a configured catalog, so a cost that records only WHERE its
        // rate came from cannot be told from one that is two years stale. AddLmConfig's own registration
        // leaves this null, which is the whole reason the host registers its own resolver.
        pricing.Should().NotBeNull();
        pricing!.Version.Should().Be("2026-08-01");
        pricing.Source.Should().Be(PricingCatalog.Source);
    }

    [Fact]
    public void AModelStampedUnderAnAliasStillResolves()
    {
        var resolver = ResolverFrom(Config(
            ("Pricing:Models:claude-sonnet-4-5:PromptPerMillion", "3"),
            ("Pricing:Models:claude-sonnet-4-5:CompletionPerMillion", "15"),
            ("Pricing:Models:claude-sonnet-4-5:Aliases:0", "anthropic/claude-sonnet-4.5")));

        // A usage record carries only the effective model id, and the id a host stamps is frequently the
        // provider's name for the model rather than the catalog key. A catalog that indexes only the key
        // yields null here and is indistinguishable from an unconfigured host.
        resolver.Resolve("anthropic/claude-sonnet-4.5").Should().NotBeNull();
        resolver.Resolve("claude-sonnet-4-5").Should().NotBeNull();
    }

    [Fact]
    public void AnUnconfiguredModel_ResolvesToUnavailable_NotToZero()
    {
        var resolver = ResolverFrom(Config(
            ("Pricing:Models:claude-sonnet-4-5:PromptPerMillion", "3"),
            ("Pricing:Models:claude-sonnet-4-5:CompletionPerMillion", "15")));
        var ledger = new UsageLedger("conv-1", resolver);

        // Flat-rate ids (Copilot) carry no public per-token price. "Unavailable" is the correct state; a
        // zero would be summed and reported as a real, cheap number.
        var record = ledger.UpsertAttempt(Observation("copilot/gpt-5", 1_000_000, 200_000));

        record.EstimatedPublicCostMicros.Should().BeNull();
        record.CostProvenance.Should().Be(CostProvenance.Unavailable);
    }

    [Fact]
    public void AnEntryMissingHalfItsRate_IsSkipped_RatherThanPricedAtZeroForTheMissingHalf()
    {
        var resolver = ResolverFrom(Config(
            ("Pricing:Models:partial:PromptPerMillion", "2.5")));

        resolver.Resolve("partial").Should().BeNull();
    }

    [Fact]
    public void AnAbsentPricingSection_LeavesEveryCostUnavailable_AndStillBoots()
    {
        var resolver = ResolverFrom(Config(("AllowedHosts", "*")));

        // The shipped default. It must not throw, and it must not invent a number.
        resolver.Resolve("claude-sonnet-4-5").Should().BeNull();
    }

    [Fact]
    public void TheCatalogIsRegisteredWithLmConfig_NotJustHandedToTheResolver()
    {
        var services = new ServiceCollection();
        _ = services.AddLogging();
        _ = services.AddConfiguredPricing(Config(
            ("Pricing:Models:claude-sonnet-4-5:PromptPerMillion", "3"),
            ("Pricing:Models:claude-sonnet-4-5:CompletionPerMillion", "15")));

        var catalog = services.BuildServiceProvider().GetRequiredService<IOptions<AppConfig>>().Value;

        catalog.Models.Should().ContainSingle(m => m.Id == "claude-sonnet-4-5");
    }

    [Fact]
    public void OneNamePricedTwoWays_IsDropped_RatherThanResolvedToWhicheverCameFirst()
    {
        var resolver = ResolverFrom(Config(
            ("Pricing:Models:model-a:PromptPerMillion", "3"),
            ("Pricing:Models:model-a:CompletionPerMillion", "15"),
            ("Pricing:Models:model-a:Aliases:0", "shared-name"),
            ("Pricing:Models:model-b:PromptPerMillion", "1"),
            ("Pricing:Models:model-b:CompletionPerMillion", "5"),
            ("Pricing:Models:model-b:Aliases:0", "shared-name")));

        // A confident wrong cost is worse than an absent one: it is summed, reported and believed. The
        // unambiguously-priced ids either side of the conflict still resolve.
        resolver.Resolve("shared-name").Should().BeNull();
        resolver.Resolve("model-a").Should().NotBeNull();
        resolver.Resolve("model-b").Should().NotBeNull();
    }
}
