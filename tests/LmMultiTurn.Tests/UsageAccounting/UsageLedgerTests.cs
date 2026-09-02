using AchieveAi.LmDotnetTools.LmCore.Models;
using AchieveAi.LmDotnetTools.LmMultiTurn.UsageAccounting;
using FluentAssertions;
using Xunit;

namespace AchieveAi.LmDotnetTools.LmMultiTurn.Tests.UsageAccounting;

public class UsageLedgerTests
{
    private static UsageRecord Obs(
        string attemptId,
        string model,
        long input,
        long output,
        bool finalized = false,
        DateTimeOffset? occurredAt = null
    ) =>
        new()
        {
            LogicalCallId = attemptId,
            ProviderAttemptId = attemptId,
            RootConversationId = "conv-1",
            RequestedModel = model,
            InputTokens = input,
            OutputTokens = output,
            Finalized = finalized,
            OccurredAtUtc = occurredAt,
        };

    private static readonly DateTimeOffset DayOne = new(2026, 8, 22, 23, 50, 0, TimeSpan.Zero);

    [Fact]
    public void UpsertAttempt_CollapsesCumulativeObservations_ToOneRecordPerAttempt()
    {
        var ledger = new UsageLedger("conv-1");

        // Three cumulative streaming observations for the same attempt.
        ledger.UpsertAttempt(Obs("a1", "model-A", input: 40, output: 0));
        ledger.UpsertAttempt(Obs("a1", "model-A", input: 100, output: 30));
        ledger.UpsertAttempt(Obs("a1", "model-A", input: 100, output: 55, finalized: true));

        var snap = ledger.Snapshot();

        snap.PerModel.Should().ContainSingle();
        snap.PerModel[0].AttemptCount.Should().Be(1);
        snap.PerModel[0].InputTokens.Should().Be(100);
        snap.PerModel[0].OutputTokens.Should().Be(55);
        snap.TotalTokens.Should().Be(155);
    }

    [Fact]
    public void UpsertAttempt_Replay_IsIdempotent()
    {
        var ledger = new UsageLedger("conv-1");
        var final = Obs("a1", "model-A", input: 100, output: 55, finalized: true);

        ledger.UpsertAttempt(final);
        ledger.UpsertAttempt(final); // replay

        var snap = ledger.Snapshot();
        snap.PerModel[0].AttemptCount.Should().Be(1);
        snap.TotalTokens.Should().Be(155);
    }

    [Fact]
    public void UpsertAttempt_InvokesAggregateUpdated_WithCurrentFoldedSnapshot()
    {
        // The aggregate-changed callback is the source of the live usage banner frame (#196, BUG 1b): each
        // accepted observation must fire it with the CURRENT folded total so descendant spend surfaces live.
        var totals = new List<long>();
        var ledger = new UsageLedger("conv-1", onAggregateUpdated: aggregate => totals.Add(aggregate.TotalTokens));

        ledger.UpsertAttempt(Obs("a1", "model-A", input: 100, output: 40)); // total 140
        ledger.UpsertAttempt(Obs("a2", "model-A", input: 50, output: 10)); // total 200

        totals.Should().Equal(140, 200);
    }

    [Fact]
    public void UpsertAttempt_OutOfOrder_FinalThenLateInterim_KeepsMax()
    {
        var ledger = new UsageLedger("conv-1");

        ledger.UpsertAttempt(Obs("a1", "model-A", input: 100, output: 55, finalized: true));
        ledger.UpsertAttempt(Obs("a1", "model-A", input: 90, output: 40)); // stale late interim

        var snap = ledger.Snapshot();
        snap.PerModel[0].InputTokens.Should().Be(100);
        snap.PerModel[0].OutputTokens.Should().Be(55);
    }

    [Fact]
    public void Snapshot_SumsAcrossAttemptsAndModels_WithWatermarkAtCommittedPrefix()
    {
        var ledger = new UsageLedger("conv-1");
        ledger.UpsertAttempt(Obs("a1", "model-A", input: 100, output: 40));
        ledger.UpsertAttempt(Obs("a2", "model-B", input: 10, output: 5));
        ledger.UpsertAttempt(Obs("a3", "model-A", input: 20, output: 10, finalized: true));

        var snap = ledger.Snapshot(UsageCompleteness.Complete);

        snap.PerModel.Should().HaveCount(2);
        snap.TotalTokens.Should().Be(185); // A: 170, B: 15
        snap.FoldedRevision.Should().Be(3); // three committed, gap-free
        snap.Completeness.Should().Be(UsageCompleteness.Complete);
    }

    [Fact]
    public void UpsertAttempt_FillsEstimatedPublicCost_FromResolver()
    {
        var ledger = new UsageLedger(
            "conv-1",
            new StubResolver("model-A", promptPerMillion: 2m, completionPerMillion: 8m)
        );

        ledger.UpsertAttempt(Obs("a1", "model-A", input: 1000, output: 500));

        var snap = ledger.Snapshot();
        snap.EstimatedPublicCostMicros.Should().Be(6000); // 1000*$2/M + 500*$8/M => $0.006
        snap.ProviderReportedCostMicros.Should().BeNull();
    }

    [Fact]
    public void UpsertAttempt_StampsPublicEstimateProvenance_WhenResolverFillsCost()
    {
        var ledger = new UsageLedger(
            "conv-1",
            new StubResolver("model-A", promptPerMillion: 2m, completionPerMillion: 8m)
        );

        var merged = ledger.UpsertAttempt(Obs("a1", "model-A", input: 1000, output: 500));

        merged.CostProvenance.Should().Be(CostProvenance.PublicEstimate);
    }

    [Fact]
    public void UpsertAttempt_DoesNotDowngradeProviderReportedProvenance_WhenResolverAlsoFillsAnEstimate()
    {
        var ledger = new UsageLedger(
            "conv-1",
            new StubResolver("model-A", promptPerMillion: 2m, completionPerMillion: 8m)
        );
        var withProviderCost = Obs("a1", "model-A", input: 1000, output: 500) with
        {
            ProviderReportedCostMicros = 5000,
            CostProvenance = CostProvenance.ProviderReported,
        };

        var merged = ledger.UpsertAttempt(withProviderCost);

        // The resolver still fills EstimatedPublicCostMicros (kept for comparison), but provenance must
        // stay ProviderReported — the ground truth, not the estimate that happened to run afterwards (#367).
        merged.EstimatedPublicCostMicros.Should().Be(6000);
        merged.ProviderReportedCostMicros.Should().Be(5000);
        merged.CostProvenance.Should().Be(CostProvenance.ProviderReported);
    }

    [Fact]
    public void UpsertAttempt_MergesCostProvenance_ByHigherInformationValue()
    {
        var ledger = new UsageLedger("conv-1");

        // First observation carries no cost info; second carries a provider-reported figure. The merged
        // record must adopt the higher-information provenance regardless of arrival order (#367).
        ledger.UpsertAttempt(Obs("a1", "model-A", input: 40, output: 0));
        var merged = ledger.UpsertAttempt(
            Obs("a1", "model-A", input: 100, output: 55, finalized: true) with
            {
                ProviderReportedCostMicros = 7000,
                CostProvenance = CostProvenance.ProviderReported,
            }
        );

        merged.CostProvenance.Should().Be(CostProvenance.ProviderReported);
        merged.ProviderReportedCostMicros.Should().Be(7000);
    }

    [Fact]
    public void UpsertAttempt_MergesCostProvenance_ByHigherInformationValue_ReverseOrder()
    {
        // The companion of the test above, with arrival order reversed: the FIRST observation carries the
        // provider-reported figure and the SECOND carries no cost info at all. A last-wins merge (taking
        // the incoming observation's provenance unconditionally, the way every OTHER field here does NOT)
        // would silently downgrade this to Unavailable and still pass the forward-order test above, which
        // puts ProviderReported second and so cannot distinguish "higher wins" from "last wins". This is
        // the case that can (#367).
        var ledger = new UsageLedger("conv-1");

        ledger.UpsertAttempt(
            Obs("a1", "model-A", input: 100, output: 55) with
            {
                ProviderReportedCostMicros = 7000,
                CostProvenance = CostProvenance.ProviderReported,
            }
        );
        var merged = ledger.UpsertAttempt(Obs("a1", "model-A", input: 100, output: 55, finalized: true));

        merged
            .CostProvenance.Should()
            .Be(
                CostProvenance.ProviderReported,
                "a later observation carrying no cost info must not erase an already-known provider-reported provenance"
            );
        merged.ProviderReportedCostMicros.Should().Be(7000);
    }

    [Fact]
    public void SeedFromRecords_RestoresTotals_DedupsSeededAttempts_AndContinuesWatermark()
    {
        var original = new UsageLedger("conv-1");
        original.UpsertAttempt(Obs("a1", "model-A", input: 100, output: 40));
        original.UpsertAttempt(Obs("a2", "model-B", input: 10, output: 5));
        var records = original.SnapshotRecords();
        var originalSnapshot = original.Snapshot();

        // A recreated ledger (e.g. after restart) rebuilds from the durable records.
        var rebuilt = new UsageLedger("conv-1");
        rebuilt.SeedFromRecords(records, originalSnapshot.FoldedRevision);

        rebuilt.Snapshot().TotalTokens.Should().Be(155);
        rebuilt.Snapshot().FoldedRevision.Should().Be(originalSnapshot.FoldedRevision);

        // Re-observing a seeded attempt does not double-count.
        rebuilt.UpsertAttempt(Obs("a1", "model-A", input: 100, output: 40));
        rebuilt.Snapshot().TotalTokens.Should().Be(155);

        // A genuinely new attempt adds and advances the watermark above the seeded baseline.
        rebuilt.UpsertAttempt(Obs("a3", "model-A", input: 20, output: 10));
        rebuilt.Snapshot().TotalTokens.Should().Be(185);
        rebuilt.Snapshot().FoldedRevision.Should().BeGreaterThan(originalSnapshot.FoldedRevision);
    }

    [Fact]
    public void SeedFromRecords_DerivesProvenance_FromPopulatedCost_OnLegacyRows()
    {
        // A usage row persisted before CostProvenance existed (#367) has no such field in its JSON, so it
        // deserializes to the default Unavailable — even when a real cost sits right beside it. Seeding must
        // re-derive the provenance from which cost field is populated rather than restoring the misleading
        // default verbatim (#393). Provider-reported is the higher-information source and wins over estimate.
        var legacyReported = Obs("a1", "model-A", input: 100, output: 40) with
        {
            ProviderReportedCostMicros = 5000,
            CostProvenance = CostProvenance.Unavailable,
        };
        var legacyEstimated = Obs("a2", "model-B", input: 10, output: 5) with
        {
            EstimatedPublicCostMicros = 1200,
            CostProvenance = CostProvenance.Unavailable,
        };
        var legacyBoth = Obs("a3", "model-C", input: 20, output: 10) with
        {
            EstimatedPublicCostMicros = 1200,
            ProviderReportedCostMicros = 900,
            CostProvenance = CostProvenance.Unavailable,
        };
        var genuinelyUnpriced = Obs("a4", "model-D", input: 5, output: 5);

        var ledger = new UsageLedger("conv-1");
        ledger.SeedFromRecords([legacyReported, legacyEstimated, legacyBoth, genuinelyUnpriced], foldedRevision: 4);

        var seeded = ledger.SnapshotRecords();
        seeded.Single(r => r.ProviderAttemptId == "a1").CostProvenance.Should().Be(CostProvenance.ProviderReported);
        seeded.Single(r => r.ProviderAttemptId == "a2").CostProvenance.Should().Be(CostProvenance.PublicEstimate);
        seeded.Single(r => r.ProviderAttemptId == "a3").CostProvenance.Should().Be(CostProvenance.ProviderReported);
        // A row with no cost at all stays Unavailable — there is nothing to derive from.
        seeded.Single(r => r.ProviderAttemptId == "a4").CostProvenance.Should().Be(CostProvenance.Unavailable);
    }

    [Fact]
    public void SeedFromRecords_DoesNotDowngradeAnAlreadyStampedProvenance()
    {
        // Derivation fires only on the Unavailable default. A row that already carries an explicit provenance
        // is trusted as-is, even if a different cost field also happens to be populated.
        var alreadyEstimate = Obs("a1", "model-A", input: 100, output: 40) with
        {
            EstimatedPublicCostMicros = 1200,
            ProviderReportedCostMicros = 5000,
            CostProvenance = CostProvenance.PublicEstimate,
        };

        var ledger = new UsageLedger("conv-1");
        ledger.SeedFromRecords([alreadyEstimate], foldedRevision: 1);

        ledger.SnapshotRecords().Single().CostProvenance.Should().Be(CostProvenance.PublicEstimate);
    }

    private sealed class StubResolver(string model, decimal promptPerMillion, decimal completionPerMillion)
        : IPricingResolver
    {
        private readonly ModelPricing _pricing = new()
        {
            ModelId = model,
            PromptPerMillion = promptPerMillion,
            CompletionPerMillion = completionPerMillion,
        };

        public ModelPricing? Resolve(string modelId) =>
            string.Equals(modelId, model, StringComparison.Ordinal) ? _pricing : null;
    }

    [Fact]
    public void UpsertAttempt_PreservesTheEarliestOccurredAtUtc_AcrossCumulativeObservations()
    {
        // Merge is MAX per count but must be FIRST-WINS for the timestamp: a UsageRecord is "a durable,
        // idempotent record" of one billable attempt, and a cumulative stream re-observes that attempt many
        // times. Last-wins would stamp when the final chunk arrived — the next UTC day here — and misfile
        // the attempt in a per-day rollup (#307).
        var ledger = new UsageLedger("conv-1");

        ledger.UpsertAttempt(Obs("a1", "model-A", input: 40, output: 0, occurredAt: DayOne));
        ledger.UpsertAttempt(Obs("a1", "model-A", input: 100, output: 30, occurredAt: DayOne.AddMinutes(5)));
        var merged = ledger.UpsertAttempt(
            Obs("a1", "model-A", input: 100, output: 55, finalized: true, occurredAt: DayOne.AddMinutes(20))
        );

        merged.OccurredAtUtc.Should().Be(DayOne);
        ledger.SnapshotRecords().Single().OccurredAtUtc.Should().Be(DayOne);
    }

    [Fact]
    public void UpsertAttempt_PreservesTheEarliestOccurredAtUtc_WhenObservationsArriveOutOfOrder()
    {
        // Out-of-order delivery must reach the same answer — first-wins is over the value, not arrival.
        var ledger = new UsageLedger("conv-1");

        ledger.UpsertAttempt(Obs("a1", "model-A", input: 100, output: 55, occurredAt: DayOne.AddMinutes(20)));
        ledger.UpsertAttempt(Obs("a1", "model-A", input: 100, output: 55, occurredAt: DayOne));

        ledger.SnapshotRecords().Single().OccurredAtUtc.Should().Be(DayOne);
    }

    [Fact]
    public void UpsertAttempt_KeepsTheKnownTimestamp_WhenALaterObservationHasNone()
    {
        var ledger = new UsageLedger("conv-1");

        ledger.UpsertAttempt(Obs("a1", "model-A", input: 40, output: 0, occurredAt: DayOne));
        ledger.UpsertAttempt(Obs("a1", "model-A", input: 100, output: 55, finalized: true));

        ledger.SnapshotRecords().Single().OccurredAtUtc.Should().Be(DayOne);
    }

    // --- Category-complete estimation (#682): the ledger prices every billed category through
    // ModelPricing.Estimate, keeps provider-reported and public-estimate figures separately, and stamps
    // completeness so a partial estimate is never mistaken for an exact one. ---

    private static readonly ModelPricing AnthropicPricing = new()
    {
        ModelId = "claude",
        PromptPerMillion = 3m,
        CompletionPerMillion = 15m,
        CacheReadPerMillion = 0.30m,
        CacheWrite5mPerMillion = 3.75m,
        CacheWrite1hPerMillion = 6m,
        CacheAccounting = CacheAccounting.Additive,
    };

    private static readonly ModelPricing OpenAiPricing = new()
    {
        ModelId = "gpt",
        PromptPerMillion = 3m,
        CompletionPerMillion = 15m,
        CacheReadPerMillion = 0.30m,
        CacheAccounting = CacheAccounting.SubsetOfInput,
    };

    private sealed class CatalogResolver(params ModelPricing[] entries) : IPricingResolver
    {
        public ModelPricing? Resolve(string modelId) =>
            entries.FirstOrDefault(p => string.Equals(p.ModelId, modelId, StringComparison.Ordinal));
    }

    [Fact]
    public void UpsertAttempt_PricesEveryCategory_AnthropicAdditiveShape()
    {
        var ledger = new UsageLedger("conv-1", new CatalogResolver(AnthropicPricing));

        // input EXCLUDES the 4,000 reads and 2,000 writes. TTL split unknown -> 5m rate, Partial.
        var merged = ledger.UpsertAttempt(
            Obs("a1", "claude", input: 1_000, output: 100) with
            {
                CacheReadTokens = 4_000,
                CacheWriteTokens = 2_000,
            }
        );

        // 3,000 + 1,200 + 7,500 + 1,500 = 13,200 micros.
        merged.EstimatedPublicCostMicros.Should().Be(13_200);
        merged.CostCompleteness.Should().Be(CostCompleteness.Partial);
        merged.CostProvenance.Should().Be(CostProvenance.PublicEstimate);
    }

    [Fact]
    public void UpsertAttempt_PricesEveryCategory_OpenAiSubsetShape_WithoutDoubleCounting()
    {
        var ledger = new UsageLedger("conv-1", new CatalogResolver(OpenAiPricing));

        // 5,000 input of which 4,000 cached: 1,000 @ $3 + 4,000 @ $0.30 + 100 @ $15 = 5,700 micros.
        var merged = ledger.UpsertAttempt(Obs("a1", "gpt", input: 5_000, output: 100) with { CacheReadTokens = 4_000 });

        merged.EstimatedPublicCostMicros.Should().Be(5_700);
        merged.CostCompleteness.Should().Be(CostCompleteness.Complete);
    }

    [Fact]
    public void Snapshot_MixedModels_SumsEachModelUnderItsOwnAccounting_AndCarriesTheWeakestCompleteness()
    {
        var ledger = new UsageLedger("conv-1", new CatalogResolver(AnthropicPricing, OpenAiPricing));

        ledger.UpsertAttempt(
            Obs("a1", "claude", input: 1_000, output: 100) with
            {
                CacheReadTokens = 4_000,
                CacheWriteTokens = 2_000,
            }
        );
        ledger.UpsertAttempt(Obs("b1", "gpt", input: 5_000, output: 100) with { CacheReadTokens = 4_000 });

        var snap = ledger.Snapshot();

        snap.EstimatedPublicCostMicros.Should().Be(13_200 + 5_700);
        // The Anthropic attempt is Partial (cache-write TTL unknown), so the conversation figure is too.
        snap.EstimatedCostCompleteness.Should().Be(CostCompleteness.Partial);
        snap.PerModel.Single(m => m.ModelId == "gpt").EstimatedCostCompleteness.Should().Be(CostCompleteness.Complete);
    }

    [Fact]
    public void UpsertAttempt_UnknownModel_IsUnavailable_NotZero()
    {
        var ledger = new UsageLedger("conv-1", new CatalogResolver(OpenAiPricing));

        var merged = ledger.UpsertAttempt(Obs("a1", "no-such-model", input: 5_000, output: 100));

        merged.EstimatedPublicCostMicros.Should().BeNull();
        merged.CostCompleteness.Should().Be(CostCompleteness.Unavailable);
        merged.CostProvenance.Should().Be(CostProvenance.Unavailable);
    }

    [Fact]
    public void UpsertAttempt_CategoryWithTokensButNoRate_IsPartial_NeverBaseRate()
    {
        // A legacy two-rate entry: cache reads have tokens but no rate.
        var legacy = OpenAiPricing with
        {
            CacheReadPerMillion = null,
        };
        var ledger = new UsageLedger("conv-1", new CatalogResolver(legacy));

        var merged = ledger.UpsertAttempt(Obs("a1", "gpt", input: 5_000, output: 100) with { CacheReadTokens = 4_000 });

        merged.EstimatedPublicCostMicros.Should().Be(4_500); // 1,000 uncached @ $3 + 100 @ $15
        merged.CostCompleteness.Should().Be(CostCompleteness.Partial);
    }

    [Fact]
    public void UpsertAttempt_RecomputesTheEstimate_AsCumulativeCountsGrow()
    {
        // Cumulative streaming re-observes one attempt with growing counts. The first observation's
        // estimate must not be frozen onto the record: the merged (max) counts are what get priced.
        var ledger = new UsageLedger("conv-1", new CatalogResolver(OpenAiPricing));

        ledger.UpsertAttempt(Obs("a1", "gpt", input: 1_000, output: 0));
        var merged = ledger.UpsertAttempt(Obs("a1", "gpt", input: 1_000, output: 100, finalized: true));

        merged.EstimatedPublicCostMicros.Should().Be(3_000 + 1_500);
    }

    [Fact]
    public void UpsertAttempt_KeepsProviderReportedAndEstimate_SeparatelyQueryable_AndPrefersReported()
    {
        var ledger = new UsageLedger("conv-1", new CatalogResolver(OpenAiPricing));

        var merged = ledger.UpsertAttempt(
            Obs("a1", "gpt", input: 1_000, output: 100) with
            {
                ProviderReportedCostMicros = 9_999,
            }
        );

        merged.ProviderReportedCostMicros.Should().Be(9_999);
        merged.EstimatedPublicCostMicros.Should().Be(4_500);
        merged.PreferredCostMicros.Should().Be(9_999);
        merged.CostProvenance.Should().Be(CostProvenance.ProviderReported);
    }

    [Fact]
    public void UpsertAttempt_KeepsAnEstimateTheObservationAlreadyCarries_WithItsCompleteness()
    {
        // A record relayed from a child ledger arrives already priced (and already labelled). The parent
        // must not re-price it against its own catalog — the child's stamp is the record of truth.
        var ledger = new UsageLedger("conv-1", new CatalogResolver(OpenAiPricing));

        var merged = ledger.UpsertAttempt(
            Obs("a1", "gpt", input: 1_000, output: 100) with
            {
                EstimatedPublicCostMicros = 1,
                CostCompleteness = CostCompleteness.Partial,
                CostProvenance = CostProvenance.PublicEstimate,
            }
        );

        merged.EstimatedPublicCostMicros.Should().Be(1);
        merged.CostCompleteness.Should().Be(CostCompleteness.Partial);
    }

    [Fact]
    public void UpsertAttempt_CarriesCompactionCheckpointId_AndCompactionKind()
    {
        var ledger = new UsageLedger("conv-1", new CatalogResolver(OpenAiPricing));

        ledger.UpsertAttempt(Obs("a1", "gpt", input: 1_000, output: 0));
        var merged = ledger.UpsertAttempt(
            Obs("a1", "gpt", input: 1_000, output: 100) with
            {
                ExecutionKind = UsageExecutionKind.Compaction,
                CompactionCheckpointId = "cp-1",
            }
        );

        merged.ExecutionKind.Should().Be(UsageExecutionKind.Compaction);
        merged.CompactionCheckpointId.Should().Be("cp-1");
    }

    [Fact]
    public void SeedFromRecords_DerivesPartialCompleteness_ForALegacyRowThatCarriesAnEstimate()
    {
        // A row persisted before #682 deserializes with the Unavailable default beside a populated
        // two-category estimate. It is not unavailable (there is a number) and not complete (cache
        // categories were ignored when it was priced), so Partial is the honest re-derivation.
        var ledger = new UsageLedger("conv-1");
        ledger.SeedFromRecords(
            [Obs("a1", "gpt", input: 1_000, output: 100) with { EstimatedPublicCostMicros = 4_500 }],
            foldedRevision: 1
        );

        ledger.SnapshotRecords().Single().CostCompleteness.Should().Be(CostCompleteness.Partial);
    }

    [Fact]
    public void SeedFromRecords_DoesNotRelabelAnExplicitlyStampedCompleteness()
    {
        var ledger = new UsageLedger("conv-1");
        ledger.SeedFromRecords(
            [
                Obs("a1", "gpt", input: 1_000, output: 100) with
                {
                    EstimatedPublicCostMicros = 4_500,
                    CostCompleteness = CostCompleteness.Complete,
                },
            ],
            foldedRevision: 1
        );

        ledger.SnapshotRecords().Single().CostCompleteness.Should().Be(CostCompleteness.Complete);
    }
}
