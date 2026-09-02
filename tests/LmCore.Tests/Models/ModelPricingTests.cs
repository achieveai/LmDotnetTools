using AchieveAi.LmDotnetTools.LmCore.Models;
using FluentAssertions;

namespace AchieveAi.LmDotnetTools.LmCore.Tests.Models;

public class ModelPricingTests
{
    [Fact]
    public void EstimateMicros_ComputesCostInMicroUnits()
    {
        // $2 / M input, $8 / M output. 1000 input + 500 output => $0.006 => 6000 micro-units.
        var pricing = new ModelPricing
        {
            ModelId = "model-A",
            PromptPerMillion = 2m,
            CompletionPerMillion = 8m,
        };

        pricing.EstimateMicros(inputTokens: 1000, outputTokens: 500).Should().Be(6000);
    }

    [Fact]
    public void EstimateMicros_IsZero_ForNoTokens()
    {
        var pricing = new ModelPricing
        {
            ModelId = "model-A",
            PromptPerMillion = 2m,
            CompletionPerMillion = 8m,
        };

        pricing.EstimateMicros(0, 0).Should().Be(0);
    }

    // --- Category-complete estimation (#682). Rates below are round numbers so every expected figure
    // can be checked by hand: prompt $3/M, cache read $0.30/M, 5m write $3.75/M, 1h write $6/M,
    // completion $15/M, reasoning (when set) $30/M. ---

    private static ModelPricing Anthropic(bool with1h = true) =>
        new()
        {
            ModelId = "claude",
            PromptPerMillion = 3m,
            CompletionPerMillion = 15m,
            CacheReadPerMillion = 0.30m,
            CacheWrite5mPerMillion = 3.75m,
            CacheWrite1hPerMillion = with1h ? 6m : null,
            CacheAccounting = CacheAccounting.Additive,
        };

    private static ModelPricing OpenAi() =>
        new()
        {
            ModelId = "gpt",
            PromptPerMillion = 3m,
            CompletionPerMillion = 15m,
            CacheReadPerMillion = 0.30m,
            CacheAccounting = CacheAccounting.SubsetOfInput,
        };

    private static UsageRecord Record(
        long input = 0,
        long output = 0,
        long cacheRead = 0,
        long cacheWrite = 0,
        long? cacheWrite1h = null,
        long reasoning = 0
    ) =>
        new()
        {
            LogicalCallId = "call-1",
            ProviderAttemptId = "attempt-1",
            RootConversationId = "conv-1",
            RequestedModel = "any",
            InputTokens = input,
            OutputTokens = output,
            CacheReadTokens = cacheRead,
            CacheWriteTokens = cacheWrite,
            CacheWrite1hTokens = cacheWrite1h,
            ReasoningTokens = reasoning,
        };

    [Fact]
    public void Estimate_AdditiveAccounting_PricesInputCacheReadAndCacheWriteAsSeparateCategories()
    {
        // Anthropic shape: input_tokens EXCLUDES cache reads and cache writes, so nothing is subtracted.
        // 1,000 uncached @ $3 + 4,000 reads @ $0.30 + 2,000 writes @ $3.75 (TTL known: all 5m) + 100 out @ $15
        // = 3,000 + 1,200 + 7,500 + 1,500 = 13,200 micros.
        var estimate = Anthropic()
            .Estimate(Record(input: 1_000, cacheRead: 4_000, cacheWrite: 2_000, cacheWrite1h: 0, output: 100));

        estimate.Micros.Should().Be(13_200);
        estimate.Completeness.Should().Be(CostCompleteness.Complete);
        estimate.MissingCategories.Should().BeEmpty();
    }

    [Fact]
    public void Estimate_SubsetAccounting_DoesNotChargeCachedTokensAtTheBaseRateToo()
    {
        // OpenAI shape: cached_tokens is a SUBSET of prompt_tokens. 5,000 input of which 4,000 cached:
        // 1,000 uncached @ $3 + 4,000 reads @ $0.30 + 100 out @ $15 = 3,000 + 1,200 + 1,500 = 5,700 micros.
        // Charging all 5,000 at the base rate AND 4,000 at the read rate would be 16,200 — the double count.
        var estimate = OpenAi().Estimate(Record(input: 5_000, cacheRead: 4_000, output: 100));

        estimate.Micros.Should().Be(5_700);
        estimate.Completeness.Should().Be(CostCompleteness.Complete);
    }

    [Fact]
    public void Estimate_TheAccountingModeIsWhatChangesTheMath_ForTheSameRecord()
    {
        // The distinguishing case: one record, two catalog entries that differ only in CacheAccounting.
        // Additive treats the 5,000 input as all uncached (15,000 + 1,200 = 16,200); SubsetOfInput treats
        // 4,000 of it as the cache reads already counted (3,000 + 1,200 = 4,200). If the mode were ignored
        // both would agree, and an Anthropic record priced under a SubsetOfInput entry would be undercounted.
        var record = Record(input: 5_000, cacheRead: 4_000);

        Anthropic().Estimate(record).Micros.Should().Be(16_200);
        (Anthropic() with { CacheAccounting = CacheAccounting.SubsetOfInput })
            .Estimate(record)
            .Micros.Should()
            .Be(4_200);
    }

    [Fact]
    public void Estimate_SubsetAccounting_WithMoreCacheReadsThanInput_IsPartialNotNegative()
    {
        // An Anthropic-shaped record (reads exceed input because input excludes them) priced under a
        // SubsetOfInput entry contradicts the subset assumption. Rather than a negative uncached count the
        // uncached input is clamped to zero and the estimate is flagged, so the operator can see the entry's
        // accounting mode is wrong for the provider that produced the record.
        var estimate = OpenAi().Estimate(Record(input: 1_000, cacheRead: 4_000));

        estimate.Micros.Should().Be(1_200); // reads only; nothing negative
        estimate.Completeness.Should().Be(CostCompleteness.Partial);
        estimate.MissingCategories.Should().Contain("cache_accounting_mismatch");
    }

    [Fact]
    public void Estimate_CacheWriteTtlUnknown_PricesAtThe5mRate_AndIsPartial()
    {
        // The Anthropic provider reports cache_creation_input_tokens without the 5m/1h split, so
        // CacheWrite1hTokens is null. The 5m rate is the lower bound (1h is 2x), and the estimate says so.
        var estimate = Anthropic().Estimate(Record(cacheWrite: 2_000));

        estimate.Micros.Should().Be(7_500); // 2,000 @ $3.75
        estimate.Completeness.Should().Be(CostCompleteness.Partial);
        estimate.MissingCategories.Should().Equal("cache_write_ttl_unknown");
    }

    [Fact]
    public void Estimate_CacheWriteTtlKnown_SplitsWritesAcrossThe5mAnd1hRates()
    {
        // 2,000 writes of which 500 were 1h: 1,500 @ $3.75 + 500 @ $6 = 5,625 + 3,000 = 8,625 micros.
        var estimate = Anthropic().Estimate(Record(cacheWrite: 2_000, cacheWrite1h: 500));

        estimate.Micros.Should().Be(8_625);
        estimate.Completeness.Should().Be(CostCompleteness.Complete);
    }

    [Fact]
    public void Estimate_CacheWriteTtlKnown_ButThe1hRateIsMissing_IsPartial()
    {
        // The 5m portion is priced (1,500 @ $3.75 = 5,625); the 1h portion has no rate and is NOT priced at
        // the 5m rate or the base rate in its place.
        var estimate = Anthropic(with1h: false).Estimate(Record(cacheWrite: 2_000, cacheWrite1h: 500));

        estimate.Micros.Should().Be(5_625);
        estimate.Completeness.Should().Be(CostCompleteness.Partial);
        estimate.MissingCategories.Should().Equal("cache_write");
    }

    [Fact]
    public void Estimate_CategoryWithTokensButNoRate_IsPartial_AndIsNeitherZeroNorBaseRate()
    {
        // A legacy two-rate entry (no cache rates at all) sees 4,000 cache reads. The reads must not be
        // priced at the base rate (that would be 12,000 extra) and the whole estimate must not be zero.
        var legacy = OpenAi() with
        {
            CacheReadPerMillion = null,
        };

        var estimate = legacy.Estimate(Record(input: 5_000, cacheRead: 4_000, output: 100));

        estimate.Micros.Should().Be(4_500); // 1,000 uncached @ $3 + 100 out @ $15; reads unpriced
        estimate.Completeness.Should().Be(CostCompleteness.Partial);
        estimate.MissingCategories.Should().Equal("cache_read");
    }

    [Fact]
    public void Estimate_WhenNoCategoryCouldBePriced_HasNoMicrosAtAll()
    {
        // Only cache-write tokens, and no cache-write rate: there is no lower bound worth reporting, and a
        // zero here would read as "free" — Partial with no number is the honest state.
        var noWriteRate = Anthropic() with
        {
            CacheWrite5mPerMillion = null,
            CacheWrite1hPerMillion = null,
        };

        var estimate = noWriteRate.Estimate(Record(cacheWrite: 2_000));

        estimate.Micros.Should().BeNull();
        estimate.Completeness.Should().Be(CostCompleteness.Partial);
        estimate.MissingCategories.Should().Contain("cache_write");
    }

    [Fact]
    public void Estimate_ReasoningTokens_AreBilledAsCompletion_WhenNoReasoningRate()
    {
        // Reasoning is a subset of output. 1,000 output of which 400 reasoning, all @ $15 = 15,000 micros.
        var estimate = OpenAi().Estimate(Record(output: 1_000, reasoning: 400));

        estimate.Micros.Should().Be(15_000);
        estimate.Completeness.Should().Be(CostCompleteness.Complete);
    }

    [Fact]
    public void Estimate_ReasoningTokens_UseTheReasoningRate_WhenOneIsSet()
    {
        // 600 plain output @ $15 + 400 reasoning @ $30 = 9,000 + 12,000 = 21,000 micros.
        var estimate = (OpenAi() with { ReasoningPerMillion = 30m }).Estimate(Record(output: 1_000, reasoning: 400));

        estimate.Micros.Should().Be(21_000);
    }

    [Fact]
    public void Estimate_RoundsOnceOnTheTotal_HalfToEven()
    {
        // 1 uncached token @ $2.50/M = 2.5 micros; 1 cache read @ $0.25/M = 0.25 micros. Rounding each
        // category first gives 2 + 0 = 2; rounding the total (2.75) gives 3. The rule is one deterministic
        // half-even rounding of the total, so the same record always yields the same integer.
        var pricing = OpenAi() with
        {
            PromptPerMillion = 2.5m,
            CacheReadPerMillion = 0.25m,
        };

        pricing.Estimate(Record(input: 2, cacheRead: 1)).Micros.Should().Be(3);

        // And half-even, not half-up: 2.5 micros rounds to 2, 7.5 rounds to 8.
        pricing.Estimate(Record(input: 1)).Micros.Should().Be(2);
        pricing.Estimate(Record(input: 3)).Micros.Should().Be(8);
    }

    [Fact]
    public void Estimate_NoTokens_IsAKnownZero()
    {
        // No tokens is a fact, not a gap: nothing is missing, and the cost really is zero.
        var estimate = Anthropic().Estimate(Record());

        estimate.Micros.Should().Be(0);
        estimate.Completeness.Should().Be(CostCompleteness.Complete);
    }

    [Fact]
    public void Estimate_MissingCategories_AreListedOnceEach()
    {
        var legacy = Anthropic(with1h: false) with { CacheWrite5mPerMillion = null };

        // Both the 5m and the 1h slices lack a rate; the category is still named once.
        var estimate = legacy.Estimate(Record(cacheWrite: 2_000, cacheWrite1h: 500));

        estimate.MissingCategories.Should().Equal("cache_write");
    }

    [Fact]
    public void CacheAccounting_DefaultsToSubsetOfInput_MatchingUsageRecordsNormativeSemantics()
    {
        // A two-rate entry written before CacheAccounting existed carries the default. UsageRecord documents
        // CacheReadTokens as a subset of InputTokens, so that is the default the estimator assumes.
        var legacy = new ModelPricing
        {
            ModelId = "m",
            PromptPerMillion = 1m,
            CompletionPerMillion = 1m,
        };

        legacy.CacheAccounting.Should().Be(CacheAccounting.SubsetOfInput);
    }
}
