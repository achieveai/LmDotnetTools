using AchieveAi.LmDotnetTools.LmCore.Models;
using FluentAssertions;

namespace AchieveAi.LmDotnetTools.LmCore.Tests.Models;

public class ConversationUsageAggregateTests
{
    private static UsageRecord Record(
        string attemptId,
        string model,
        long input,
        long output,
        long revision = 0,
        long cacheWrite = 0,
        long? estimated = null,
        long? reported = null,
        DateTimeOffset? occurredAt = null
    ) =>
        new()
        {
            LogicalCallId = attemptId,
            ProviderAttemptId = attemptId,
            RootConversationId = "conv-1",
            RequestedModel = model,
            Revision = revision,
            InputTokens = input,
            OutputTokens = output,
            CacheWriteTokens = cacheWrite,
            EstimatedPublicCostMicros = estimated,
            ProviderReportedCostMicros = reported,
            OccurredAtUtc = occurredAt,
        };

    private static readonly DateTimeOffset DayOne = new(2026, 8, 22, 23, 50, 0, TimeSpan.Zero);

    [Fact]
    public void Fold_SumsAdditively_GroupedByModel()
    {
        var records = new[]
        {
            Record("a1", "model-A", input: 100, output: 40),
            Record("a2", "model-A", input: 200, output: 60),
            Record("b1", "model-B", input: 10, output: 5),
        };

        var agg = ConversationUsageAggregate.Fold("conv-1", records, foldedRevision: 3);

        agg.PerModel.Should().HaveCount(2);
        var a = agg.PerModel.Single(m => m.ModelId == "model-A");
        a.InputTokens.Should().Be(300);
        a.OutputTokens.Should().Be(100);
        a.TotalTokens.Should().Be(400);
        a.AttemptCount.Should().Be(2);
        agg.TotalTokens.Should().Be(415); // 400 (A) + 15 (B)
    }

    [Fact]
    public void Fold_DedupsByProviderAttemptId_LatestRevisionWins()
    {
        // Same attempt seen twice (cumulative streaming): rev 1 then rev 2 — only the latest counts.
        var records = new[]
        {
            Record("a1", "model-A", input: 50, output: 20, revision: 1),
            Record("a1", "model-A", input: 120, output: 55, revision: 2),
        };

        var agg = ConversationUsageAggregate.Fold("conv-1", records, foldedRevision: 2);

        agg.PerModel.Should().ContainSingle();
        agg.PerModel[0].InputTokens.Should().Be(120);
        agg.PerModel[0].OutputTokens.Should().Be(55);
        agg.PerModel[0].AttemptCount.Should().Be(1);
        agg.TotalTokens.Should().Be(175);
    }

    [Fact]
    public void Fold_GroupsByEffectiveModel_NotRequestedModel()
    {
        var records = new[]
        {
            Record("a1", "alias", input: 10, output: 10) with
            {
                EffectiveModel = "real-model",
            },
            Record("a2", "real-model", input: 5, output: 5),
        };

        var agg = ConversationUsageAggregate.Fold("conv-1", records, foldedRevision: 2);

        agg.PerModel.Should().ContainSingle(m => m.ModelId == "real-model");
        agg.PerModel[0].TotalTokens.Should().Be(30);
    }

    [Fact]
    public void Fold_UnknownCosts_YieldNull_NotZero()
    {
        UsageRecord[] records = [Record("a1", "model-A", input: 100, output: 40)];

        var agg = ConversationUsageAggregate.Fold("conv-1", records, foldedRevision: 1);

        agg.EstimatedPublicCostMicros.Should().BeNull();
        agg.ProviderReportedCostMicros.Should().BeNull();
        agg.PerModel[0].EstimatedPublicCostMicros.Should().BeNull();
    }

    [Fact]
    public void Fold_DualCosts_TrackedIndependently()
    {
        // One attempt has only a public estimate, another only a provider-reported cost.
        var records = new[]
        {
            Record("a1", "model-A", input: 100, output: 40, estimated: 6000),
            Record("a2", "model-A", input: 100, output: 40, reported: 5000),
        };

        var agg = ConversationUsageAggregate.Fold("conv-1", records, foldedRevision: 2);

        agg.EstimatedPublicCostMicros.Should().Be(6000);
        agg.ProviderReportedCostMicros.Should().Be(5000);
    }

    [Fact]
    public void Fold_StampsWatermarkAndCompleteness()
    {
        var agg = ConversationUsageAggregate.Fold(
            "conv-1",
            [Record("a1", "model-A", input: 1, output: 1)],
            foldedRevision: 42,
            completeness: UsageCompleteness.Complete
        );

        agg.FoldedRevision.Should().Be(42);
        agg.Completeness.Should().Be(UsageCompleteness.Complete);
        agg.RootConversationId.Should().Be("conv-1");
    }

    [Fact]
    public void DedupeByAttempt_KeepsTheHighestRevisionsCounts_ButTheEarliestOccurredAtUtc()
    {
        // Cumulative streaming re-observes one attempt many times. The counts must come from the highest
        // revision (they replace, never add), but the timestamp must not: a later revision records when the
        // last chunk arrived. Here that is the next UTC day, so taking it would bill the attempt to
        // 23 August when it actually happened on the 22nd (#307 per-day rollup).
        var records = new[]
        {
            Record("a1", "model-A", input: 50, output: 20, revision: 1, occurredAt: DayOne),
            Record("a1", "model-A", input: 120, output: 55, revision: 2, occurredAt: DayOne.AddMinutes(20)),
        };

        var deduped = ConversationUsageAggregate.DedupeByAttempt(records);

        deduped.Should().ContainSingle();
        deduped[0].InputTokens.Should().Be(120);
        deduped[0].OutputTokens.Should().Be(55);
        deduped[0].OccurredAtUtc.Should().Be(DayOne);
    }

    [Fact]
    public void DedupeByAttempt_TakesTheKnownTimestamp_WhenTheWinningRevisionHasNone()
    {
        // A legacy record (persisted before the field existed) can carry the highest revision. Adopting its
        // null would throw away the one time we do know for that attempt.
        var records = new[]
        {
            Record("a1", "model-A", input: 50, output: 20, revision: 1, occurredAt: DayOne),
            Record("a1", "model-A", input: 120, output: 55, revision: 2),
        };

        var deduped = ConversationUsageAggregate.DedupeByAttempt(records);

        deduped.Should().ContainSingle();
        deduped[0].InputTokens.Should().Be(120);
        deduped[0].OccurredAtUtc.Should().Be(DayOne);
    }

    [Fact]
    public void DedupeByAttempt_LeavesTimestampNull_WhenNoRevisionKnowsOne()
    {
        var records = new[]
        {
            Record("a1", "model-A", input: 50, output: 20, revision: 1),
            Record("a1", "model-A", input: 120, output: 55, revision: 2),
        };

        ConversationUsageAggregate.DedupeByAttempt(records)[0].OccurredAtUtc.Should().BeNull();
    }

    [Fact]
    public void Fold_MixedPricing_ConversationCost_IsNull_NotAConfidentUndercount()
    {
        // model-A is priced; model-B is not (its cost fields are null). The per-model rows keep their own
        // subtotals, but the CONVERSATION total must not present a confident partial that silently omits B —
        // for a cost figure, "cheaper than it was" is the direction that goes unquestioned (#377).
        var records = new[]
        {
            Record("a1", "model-A", input: 100, output: 40, estimated: 6000, reported: 3000),
            Record("b1", "model-B", input: 200, output: 60),
        };

        var agg = ConversationUsageAggregate.Fold("conv-1", records, foldedRevision: 2);

        // The priced model still reports its own subtotal; the unpriced model reports null.
        agg.PerModel.Single(m => m.ModelId == "model-A").EstimatedPublicCostMicros.Should().Be(6000);
        agg.PerModel.Single(m => m.ModelId == "model-B").EstimatedPublicCostMicros.Should().BeNull();

        // Any unpriced model poisons the conversation total for BOTH cost dimensions.
        agg.EstimatedPublicCostMicros.Should().BeNull();
        agg.ProviderReportedCostMicros.Should().BeNull();
    }

    [Fact]
    public void Fold_AllModelsPriced_ConversationCost_SumsAcrossModels()
    {
        // The complement of the mixed-pricing case: when every model is priced, the strict conversation
        // fold still sums across models rather than collapsing to null.
        var records = new[]
        {
            Record("a1", "model-A", input: 100, output: 40, estimated: 6000, reported: 3000),
            Record("b1", "model-B", input: 200, output: 60, estimated: 1500, reported: 900),
        };

        var agg = ConversationUsageAggregate.Fold("conv-1", records, foldedRevision: 2);

        agg.EstimatedPublicCostMicros.Should().Be(7500);
        agg.ProviderReportedCostMicros.Should().Be(3900);
    }
}
