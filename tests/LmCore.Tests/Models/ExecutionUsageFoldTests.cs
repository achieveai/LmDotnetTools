using AchieveAi.LmDotnetTools.LmCore.Models;
using FluentAssertions;

namespace AchieveAi.LmDotnetTools.LmCore.Tests.Models;

/// <summary>
///     The per-agent rollup (#681; spec 679 §4.3): the same records the conversation aggregate folds,
///     grouped by execution id instead of model, deduplicated the same way, so per-agent rows and the root
///     total are two views over ONE ledger.
/// </summary>
public class ExecutionUsageFoldTests
{
    private static UsageRecord Record(
        string attemptId,
        string? parentExecutionId,
        UsageExecutionKind kind,
        long input,
        long output,
        long revision = 0,
        long? estimated = null,
        long? reported = null,
        CostCompleteness completeness = CostCompleteness.Unavailable,
        string? compactionCheckpointId = null,
        string requestedModel = "model-A",
        string? effectiveModel = null
    ) =>
        new()
        {
            LogicalCallId = attemptId,
            ProviderAttemptId = attemptId,
            RootConversationId = "root",
            ParentExecutionId = parentExecutionId,
            ExecutionKind = kind,
            RequestedModel = requestedModel,
            EffectiveModel = effectiveModel,
            Revision = revision,
            InputTokens = input,
            OutputTokens = output,
            EstimatedPublicCostMicros = estimated,
            ProviderReportedCostMicros = reported,
            CostCompleteness = completeness,
            CompactionCheckpointId = compactionCheckpointId,
        };

    [Fact]
    public void FoldByExecution_GroupsEveryKindUnderItsEmittingExecution()
    {
        var records = new[]
        {
            Record("root:g1", null, UsageExecutionKind.Primary, 100, 10),
            Record("root:g2", null, UsageExecutionKind.Continuation, 50, 5),
            Record("subagent-agent-1:g1", "subagent-agent-1", UsageExecutionKind.SubAgent, 200, 20),
            Record(
                "subagent-agent-1:cp",
                "subagent-agent-1",
                UsageExecutionKind.Compaction,
                30,
                3,
                compactionCheckpointId: "cp-1"
            ),
            Record("wf-ctrl:g1", "wf-ctrl", UsageExecutionKind.WorkflowController, 10, 1),
            Record("wf-task:g1", "wf-task", UsageExecutionKind.WorkflowTask, 20, 2),
        };

        var rows = ConversationUsageAggregate.FoldByExecution(records);

        rows.Select(r => r.ExecutionId).Should().Equal("root", "subagent-agent-1", "wf-ctrl", "wf-task");

        var root = rows.Single(r => r.ExecutionId == "root");
        root.InputTokens.Should().Be(150);
        root.OutputTokens.Should().Be(15);
        root.AttemptCount.Should().Be(2);
        root.ExecutionKinds.Should().Equal(UsageExecutionKind.Primary, UsageExecutionKind.Continuation);
        root.CompactionAttemptCount.Should().Be(0);

        var child = rows.Single(r => r.ExecutionId == "subagent-agent-1");
        child.InputTokens.Should().Be(230);
        child.AttemptCount.Should().Be(2);
        child.CompactionAttemptCount.Should().Be(1);
        child.ExecutionKinds.Should().Equal(UsageExecutionKind.SubAgent, UsageExecutionKind.Compaction);
    }

    [Fact]
    public void FoldByExecution_DedupsByProviderAttempt_SoARelayedRecordCountsOnce()
    {
        // A sub-agent's attempt is observed twice: once by its own loop's capture and once by the parent's
        // relay (same ProviderAttemptId, higher revision). It must count once in its execution's row.
        var records = new[]
        {
            Record("subagent-agent-1:g1", "subagent-agent-1", UsageExecutionKind.SubAgent, 100, 10, revision: 1),
            Record("subagent-agent-1:g1", "subagent-agent-1", UsageExecutionKind.SubAgent, 120, 12, revision: 2),
        };

        var rows = ConversationUsageAggregate.FoldByExecution(records);

        var row = rows.Should().ContainSingle().Subject;
        row.InputTokens.Should().Be(120);
        row.AttemptCount.Should().Be(1);
    }

    [Fact]
    public void FoldByExecution_PreferredCost_IsProviderReportedElseEstimate_AndCompletenessIsStrict()
    {
        var records = new[]
        {
            Record(
                "root:g1",
                null,
                UsageExecutionKind.Primary,
                100,
                10,
                estimated: 1_000,
                reported: 1_500,
                completeness: CostCompleteness.Complete
            ),
            Record(
                "root:g2",
                null,
                UsageExecutionKind.Primary,
                100,
                10,
                estimated: 700,
                completeness: CostCompleteness.Partial
            ),
        };

        var row = ConversationUsageAggregate.FoldByExecution(records).Should().ContainSingle().Subject;

        row.PreferredCostMicros.Should().Be(2_200);
        row.EstimatedPublicCostMicros.Should().Be(1_700);
        row.ProviderReportedCostMicros.Should().Be(1_500);
        row.EstimatedCostCompleteness.Should().Be(CostCompleteness.Partial);
        row.CostProvenance.Should().Be(CostProvenance.PublicEstimate, "one attempt had to fall back to an estimate");
    }

    [Fact]
    public void FoldByExecution_UnpricedExecution_HasNullCost_NeverZero()
    {
        var row = ConversationUsageAggregate
            .FoldByExecution([Record("root:g1", null, UsageExecutionKind.Primary, 100, 10)])
            .Should()
            .ContainSingle()
            .Subject;

        row.PreferredCostMicros.Should().BeNull();
        row.EstimatedCostCompleteness.Should().Be(CostCompleteness.Unavailable);
        row.CostProvenance.Should().Be(CostProvenance.Unavailable);
    }

    [Fact]
    public void FoldByExecution_ForAnAgentThatSwitchedModels_BreaksTheRowDownByModel()
    {
        // The row sums ACROSS models, so "which model did this agent spend on" is unanswerable from it alone.
        // The breakdown belongs here rather than on ModelUsageRow, which answers the conversation-wide
        // question and stays unchanged.
        var records = new[]
        {
            Record(
                "subagent-agent-1:g1",
                "subagent-agent-1",
                UsageExecutionKind.SubAgent,
                100,
                10,
                estimated: 5,
                reported: 9,
                completeness: CostCompleteness.Complete,
                requestedModel: "claude-opus-5"
            ),
            Record(
                "subagent-agent-1:g2",
                "subagent-agent-1",
                UsageExecutionKind.SubAgent,
                40,
                4,
                estimated: 2,
                requestedModel: "claude-haiku-4-5"
            ),
        };

        var row = ConversationUsageAggregate.FoldByExecution(records).Should().ContainSingle().Subject;

        row.TotalTokens.Should().Be(154);
        row.PerModel.Select(m => m.ModelId).Should().Equal("claude-haiku-4-5", "claude-opus-5");
        row.PerModel.Sum(m => m.TotalTokens).Should().Be(row.TotalTokens);

        var opus = row.PerModel.Single(m => m.ModelId == "claude-opus-5");
        opus.InputTokens.Should().Be(100);
        opus.OutputTokens.Should().Be(10);
        opus.AttemptCount.Should().Be(1);
        opus.PreferredCostMicros.Should().Be(9);
        opus.EstimatedCostCompleteness.Should().Be(CostCompleteness.Complete);

        var haiku = row.PerModel.Single(m => m.ModelId == "claude-haiku-4-5");
        haiku.InputTokens.Should().Be(40);
        haiku.PreferredCostMicros.Should().Be(2);
        haiku.EstimatedCostCompleteness.Should().Be(CostCompleteness.Partial);
    }

    [Fact]
    public void FoldByExecution_GroupsTheBreakdownByEffectiveModel_NotTheRequestedOne()
    {
        // A provider that served a different model than the one asked for must show up under the model that
        // actually ran, matching how the conversation-wide fold groups (UsageRecord.EffectiveModelId).
        var records = new[]
        {
            Record(
                "subagent-agent-1:g1",
                "subagent-agent-1",
                UsageExecutionKind.SubAgent,
                100,
                10,
                requestedModel: "claude-opus-5",
                effectiveModel: "claude-opus-5-20260101"
            ),
        };

        var row = ConversationUsageAggregate.FoldByExecution(records).Should().ContainSingle().Subject;

        row.PerModel.Should().ContainSingle().Which.ModelId.Should().Be("claude-opus-5-20260101");
    }

    [Fact]
    public void FoldByExecution_RowsSumToTheConversationTotal()
    {
        var records = new[]
        {
            Record("root:g1", null, UsageExecutionKind.Primary, 100, 10, estimated: 5),
            Record("subagent-agent-1:g1", "subagent-agent-1", UsageExecutionKind.SubAgent, 200, 20, estimated: 7),
        };

        var rows = ConversationUsageAggregate.FoldByExecution(records);
        var total = ConversationUsageAggregate.Fold("root", records, foldedRevision: 2);

        rows.Sum(r => r.TotalTokens).Should().Be(total.TotalTokens);
        rows.Sum(r => r.EstimatedPublicCostMicros).Should().Be(total.EstimatedPublicCostMicros);
    }
}
