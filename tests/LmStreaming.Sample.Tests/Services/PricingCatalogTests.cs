using AchieveAi.LmDotnetTools.LmConfig.Models;
using AchieveAi.LmDotnetTools.LmCore.Models;
using AchieveAi.LmDotnetTools.LmMultiTurn.UsageAccounting;
using LmStreaming.Sample.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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

    [Theory]
    [InlineData("-3", "15")]
    [InlineData("3", "-15")]
    [InlineData("-3", "-15")]
    public void ANegativeRate_IsSkipped_RatherThanSummedIntoANegativeCost(string prompt, string completion)
    {
        var resolver = ResolverFrom(Config(
            ("Pricing:Models:typo:PromptPerMillion", prompt),
            ("Pricing:Models:typo:CompletionPerMillion", completion)));

        // #378 shipped with no rates in the repository precisely so a wrong number can never be summed and
        // believed. An operator's stray minus sign reintroduces exactly that: a cost that is reported, and
        // is not merely wrong but the wrong SIGN. Unusable is the same category as absent.
        resolver.Resolve("typo").Should().BeNull();
    }

    [Theory]
    [InlineData("NaN", "15")]
    [InlineData("3", "NaN")]
    [InlineData("Infinity", "15")]
    [InlineData("3", "Infinity")]
    [InlineData("-Infinity", "15")]
    public void ANonFiniteRate_IsSkipped_RatherThanOverflowingTheConversionToDecimal(
        string prompt,
        string completion)
    {
        var resolver = ResolverFrom(Config(
            ("Pricing:Models:stray-e:PromptPerMillion", prompt),
            ("Pricing:Models:stray-e:CompletionPerMillion", completion)));

        // NaN and infinity have no decimal representation, so PricingConfigResolver's (decimal) cast throws
        // OverflowException — one mistyped rate takes down pricing for every model the host runs, not just
        // the mistyped one. Skipping keeps the blast radius at the bad entry.
        resolver.Resolve("stray-e").Should().BeNull();
    }

    [Theory]
    [InlineData("NaN", "15")]
    [InlineData("3", "NaN")]
    [InlineData("Infinity", "15")]
    [InlineData("3", "Infinity")]
    [InlineData("-Infinity", "15")]
    public void ANonFiniteRate_IsRefusedByTheCatalogItself_NotOnlyByTheResolverDownstream(
        string prompt,
        string completion)
    {
        // The sibling test above asserts through PricingConfigResolver, which discards a non-finite rate for
        // reasons of its OWN: its ambiguity check compares candidate rates with `==`, and NaN != NaN, so a
        // NaN reaching the resolver is dropped as "ambiguous" whatever BuildCatalog decided. A rule pinned
        // only there is pinned to somebody else's accident (#431). This asserts the drop where the decision
        // is actually made, so BuildCatalog's own refusal survives a change to the resolver.
        var catalog = PricingCatalog.BuildCatalog(Config(
            ("Pricing:Models:stray-e:PromptPerMillion", prompt),
            ("Pricing:Models:stray-e:CompletionPerMillion", completion),
            ("Pricing:Models:sound:PromptPerMillion", "3"),
            ("Pricing:Models:sound:CompletionPerMillion", "15")));

        // Which row pins which conjunct, so a later reader does not over-claim: the INFINITY rows are what
        // distinguish `double.IsFinite` — delete it and +Infinity sails through `value >= 0`, turning these
        // red. The NaN rows distinguish nothing on their own, because every comparison against NaN is false
        // in C#: `NaN >= 0` refuses it too, so NaN is refused twice over and no single-clause deletion can
        // turn a NaN row red. They are kept anyway — they pin that NaN never reaches a (decimal) cast, and
        // they are the case that would go red if `value >= 0` were ever rewritten as `!(value < 0)`, which
        // reads identically and admits NaN.
        //
        // The sound sibling is the non-vacuity proof: an empty catalog satisfies the NotContain on its own,
        // and an empty catalog is exactly what a BuildCatalog reading the wrong section would return.
        catalog.Models.Should().Contain(m => m.Id == "sound");
        catalog.Models.Should().NotContain(m => m.Id == "stray-e");
    }

    [Fact]
    public void AZeroRate_IsAFreeModel_AndMustStillResolve()
    {
        var resolver = ResolverFrom(Config(
            ("Pricing:Models:free-model:PromptPerMillion", "0"),
            ("Pricing:Models:free-model:CompletionPerMillion", "0")));
        var ledger = new UsageLedger("conv-1", resolver);

        // The case most likely to be broken by an over-eager "reject falsy rates" fix. Zero is a real,
        // knowable rate — a free model — and "$0.00, priced" is a different fact from "cost unavailable".
        var pricing = resolver.Resolve("free-model");
        pricing.Should().NotBeNull();
        pricing!.PromptPerMillion.Should().Be(0m);

        var record = ledger.UpsertAttempt(Observation("free-model", 1_000_000, 200_000));
        record.EstimatedPublicCostMicros.Should().Be(0);
        record.CostProvenance.Should().Be(CostProvenance.PublicEstimate);
    }

    [Fact]
    public void ASkippedEntry_IsLogged_SoAnOperatorLearnsTheirRateWasDropped()
    {
        var sink = new CapturingLoggerProvider();
        var services = new ServiceCollection();
        _ = services.AddLogging(b => b.AddProvider(sink));
        _ = services.AddConfiguredPricing(Config(
            ("Pricing:Models:typo:PromptPerMillion", "-3"),
            ("Pricing:Models:typo:CompletionPerMillion", "15")));

        _ = services.BuildServiceProvider().GetRequiredService<IPricingResolver>();

        // Silently dropping the entry leaves the operator with null costs and no way to tell a typo from an
        // unconfigured model — the same indistinguishable state #378 was filed against.
        sink.Messages.Should().ContainSingle(m => m.Contains("typo", StringComparison.Ordinal));
    }

    /// <summary>Captures log messages so the operator-facing skip warning can be asserted rather than assumed.</summary>
    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public List<string> Messages { get; } = [];

        public ILogger CreateLogger(string categoryName) => new Capturing(Messages);

        public void Dispose() { }

        private sealed class Capturing(List<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                lock (messages)
                {
                    messages.Add(formatter(state, exception));
                }
            }
        }
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
