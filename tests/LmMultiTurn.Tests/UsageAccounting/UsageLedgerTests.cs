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
}
