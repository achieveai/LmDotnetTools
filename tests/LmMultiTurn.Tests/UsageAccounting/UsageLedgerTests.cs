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
        bool finalized = false) =>
        new()
        {
            LogicalCallId = attemptId,
            ProviderAttemptId = attemptId,
            RootConversationId = "conv-1",
            RequestedModel = model,
            InputTokens = input,
            OutputTokens = output,
            Finalized = finalized,
        };

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
        var ledger = new UsageLedger(
            "conv-1",
            onAggregateUpdated: aggregate => totals.Add(aggregate.TotalTokens));

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
            new StubResolver("model-A", promptPerMillion: 2m, completionPerMillion: 8m));

        ledger.UpsertAttempt(Obs("a1", "model-A", input: 1000, output: 500));

        var snap = ledger.Snapshot();
        snap.EstimatedPublicCostMicros.Should().Be(6000); // 1000*$2/M + 500*$8/M => $0.006
        snap.ProviderReportedCostMicros.Should().BeNull();
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
}
