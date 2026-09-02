using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Models;
using AchieveAi.LmDotnetTools.LmMultiTurn.UsageAccounting;
using FluentAssertions;
using LmMultiTurn.Tests.Persistence;
using Xunit;

namespace AchieveAi.LmDotnetTools.LmMultiTurn.Tests.UsageAccounting;

/// <summary>
///     The per-agent cost rollup contract (#681; spec 679 §4.3): every producer path that feeds the ONE
///     root ledger — the root loop, a direct sub-agent sink, a nested root forwarding its subtree, a
///     continuation, a compaction pass — lands on the row of the execution that spent it, deduplicated by
///     provider attempt, and the rows sum to the conversation total because they are one fold of one ledger.
/// </summary>
public sealed class ExecutionRollupContractTests : IAsyncLifetime
{
    private const string Root = "root-1";
    private readonly ConversationStoreHarness _harness = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _harness.DisposeAsync();

    public static TheoryData<string> AllKinds => ConversationStoreHarness.AllKinds;

    private static UsageMessage Usage(string generationId, int input, int output, int cacheRead = 0) =>
        new()
        {
            Usage = new Usage
            {
                PromptTokens = input,
                CompletionTokens = output,
                InputTokenDetails = cacheRead > 0 ? new InputTokenDetails { CachedTokens = cacheRead } : null,
            },
            GenerationId = generationId,
        };

    private static UsageRecord Record(UsageMessage message, string owner, UsageExecutionKind kind, string model) =>
        UsageRecordMapper.FromUsageMessage(message, owner, kind, model);

    [Fact]
    public void RootLedger_PrimaryUsage_FoldsIntoTheRootRow()
    {
        var ledger = new UsageLedger(Root);
        ledger.RecordUsage(Record(Usage("gen-1", 100, 40), Root, UsageExecutionKind.Primary, "model-A"));

        var rows = ConversationUsageAggregate.FoldByExecution(ledger.SnapshotRecords());

        var row = rows.Should().ContainSingle().Subject;
        row.ExecutionId.Should().Be(Root);
        row.ExecutionKinds.Should().Equal(UsageExecutionKind.Primary);
        row.InputTokens.Should().Be(100);
        row.OutputTokens.Should().Be(40);
        row.TotalTokens.Should().Be(ledger.Snapshot().TotalTokens);
    }

    [Fact]
    public void DirectSubAgentSink_FoldsIntoTheSubAgentsOwnRow()
    {
        // The SubAgentManager relay records a descendant's usage under the sub-agent's OWN thread id.
        var ledger = new UsageLedger(Root);
        ledger.RecordUsage(Record(Usage("gen-1", 100, 40), Root, UsageExecutionKind.Primary, "model-A"));
        ledger.RecordUsage(Record(Usage("gen-7", 500, 20), "subagent-agent-1", UsageExecutionKind.SubAgent, "model-B"));

        var rows = ConversationUsageAggregate.FoldByExecution(ledger.SnapshotRecords());

        rows.Select(r => r.ExecutionId).Should().Equal(Root, "subagent-agent-1");
        var child = rows[1];
        child.ExecutionKinds.Should().Equal(UsageExecutionKind.SubAgent);
        child.InputTokens.Should().Be(500);
        AgentExecutionRef.AgentIdFromThreadId(child.ExecutionId).Should().Be("agent-1");
        ledger
            .SnapshotRecords()
            .Should()
            .OnlyContain(r => r.RootConversationId == Root, "the ledger re-stamps its root");
    }

    [Fact]
    public void NestedRootForwarding_AttributesTheNestedRootsOwnTurns_ToItsExecution_WithoutDoubleCounting()
    {
        // A workflow controller is a nested root: its own ledger forwards every merged record to the parent
        // conversation's ledger. Its OWN turns arrive there as Primary records that, before #681, carried no
        // parent execution and were indistinguishable from the parent's own turns.
        var parent = new UsageLedger(Root);
        var nested = new UsageLedger("wf-ctrl", forwardTo: parent);

        nested.RecordUsage(Record(Usage("gen-1", 300, 30), "wf-ctrl", UsageExecutionKind.Primary, "model-A"));
        nested.RecordUsage(Record(Usage("gen-2", 700, 70), "subagent-agent-7", UsageExecutionKind.SubAgent, "model-A"));
        // Cumulative re-observation of the controller's own attempt: one billable record, not two.
        nested.RecordUsage(Record(Usage("gen-1", 300, 30), "wf-ctrl", UsageExecutionKind.Primary, "model-A"));

        var rows = ConversationUsageAggregate.FoldByExecution(parent.SnapshotRecords());

        rows.Select(r => r.ExecutionId).Should().Equal("subagent-agent-7", "wf-ctrl");
        rows[1].AttemptCount.Should().Be(1);
        rows[1].InputTokens.Should().Be(300);
        rows.Sum(r => r.TotalTokens).Should().Be(parent.Snapshot().TotalTokens);
        parent.Snapshot().TotalTokens.Should().Be(nested.Snapshot().TotalTokens);

        var nestedOwnRow = parent.SnapshotRecords().Single(r => r.ProviderAttemptId == "wf-ctrl:gen-1");
        nestedOwnRow.ParentExecutionId.Should().Be("wf-ctrl", "the forwarded copy names the nested root that spent it");
        nestedOwnRow.RootConversationId.Should().Be(Root);
        nested
            .SnapshotRecords()
            .Single(r => r.ProviderAttemptId == "wf-ctrl:gen-1")
            .ParentExecutionId.Should()
            .BeNull("inside its own ledger the nested root is still the root");
    }

    [Fact]
    public void Continuation_MapsToTheExecutionThatContinued()
    {
        var ledger = new UsageLedger(Root);
        ledger.RecordUsage(Record(Usage("gen-1", 100, 40), Root, UsageExecutionKind.Primary, "model-A"));
        ledger.RecordUsage(Record(Usage("gen-2", 120, 10), Root, UsageExecutionKind.Continuation, "model-A"));

        var row = ConversationUsageAggregate.FoldByExecution(ledger.SnapshotRecords()).Should().ContainSingle().Subject;

        row.ExecutionId.Should().Be(Root);
        row.ExecutionKinds.Should().Equal(UsageExecutionKind.Primary, UsageExecutionKind.Continuation);
        row.AttemptCount.Should().Be(2);
        row.InputTokens.Should().Be(220);
    }

    [Fact]
    public void CompactionPass_IsCountedOnItsOwnersRow_AndKeepsItsCheckpoint()
    {
        var ledger = new UsageLedger(Root);
        ledger.RecordUsage(Record(Usage("gen-1", 100, 40), Root, UsageExecutionKind.Primary, "model-A"));
        ledger.RecordUsage(
            Record(Usage("gen-c1", 5_000, 400), Root, UsageExecutionKind.Compaction, "model-A") with
            {
                CompactionCheckpointId = "cp-1",
            }
        );

        var row = ConversationUsageAggregate.FoldByExecution(ledger.SnapshotRecords()).Should().ContainSingle().Subject;

        row.CompactionAttemptCount.Should().Be(1);
        row.AttemptCount.Should().Be(2);
        row.ExecutionKinds.Should().Contain(UsageExecutionKind.Compaction);
        ledger
            .SnapshotRecords()
            .Single(r => r.ExecutionKind == UsageExecutionKind.Compaction)
            .CompactionCheckpointId.Should()
            .Be("cp-1");
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public async Task PersistedRecords_ReloadedThroughAFreshHandle_FoldToTheSameRows(string kind)
    {
        var store = _harness.Open(kind);
        var ledger = new UsageLedger(Root, new FlatPricing(("model-A", 1m), ("model-B", 10m)));
        ledger.RecordUsage(Record(Usage("gen-1", 1_000, 0), Root, UsageExecutionKind.Primary, "model-A"));
        ledger.RecordUsage(
            Record(Usage("gen-2", 1_000, 0), "subagent-agent-1", UsageExecutionKind.SubAgent, "model-B")
        );
        ledger.RecordUsage(
            Record(Usage("gen-c1", 2_000, 0), Root, UsageExecutionKind.Compaction, "model-A") with
            {
                CompactionCheckpointId = "cp-1",
            }
        );
        var expected = ConversationUsageAggregate.FoldByExecution(ledger.SnapshotRecords());

        await ConversationUsageProjection.SaveAsync(
            store,
            ledger.Snapshot(UsageCompleteness.Complete),
            ledger.SnapshotRecords()
        );

        var reloaded = await ConversationUsageProjection.LoadRecordsAsync(_harness.Reopen(kind), Root);
        var rows = ConversationUsageAggregate.FoldByExecution(reloaded);

        rows.Should().BeEquivalentTo(expected, o => o.WithStrictOrdering());
        rows.Single(r => r.ExecutionId == Root).CompactionAttemptCount.Should().Be(1);
        rows.Sum(r => r.PreferredCostMicros ?? 0).Should().Be(ledger.Snapshot().PreferredCostMicros);
    }

    [Fact]
    public void SubAgentSpend_IsPricedByTheSubAgentsOwnModel()
    {
        // #670: a split-model sub-agent's spend is attributed to ITS model, never the parent's.
        var ledger = new UsageLedger(Root, new FlatPricing(("model-A", 1m), ("model-B", 10m)));
        ledger.RecordUsage(Record(Usage("gen-1", 1_000, 0), Root, UsageExecutionKind.Primary, "model-A"));
        ledger.RecordUsage(
            Record(Usage("gen-2", 1_000, 0), "subagent-agent-1", UsageExecutionKind.SubAgent, "model-B")
        );

        var rows = ConversationUsageAggregate.FoldByExecution(ledger.SnapshotRecords());

        rows.Single(r => r.ExecutionId == Root).PreferredCostMicros.Should().Be(1_000);
        rows.Single(r => r.ExecutionId == "subagent-agent-1").PreferredCostMicros.Should().Be(10_000);
        rows.Should().OnlyContain(r => r.CostProvenance == CostProvenance.PublicEstimate);
        rows.Should().OnlyContain(r => r.EstimatedCostCompleteness == CostCompleteness.Complete);
    }

    [Fact]
    public void MissingPricingCategories_KeepTheRowPartial()
    {
        // Before category-complete pricing (#682) a rate card without a cache-read rate prices a cached
        // request as a lower bound and says so; the rollup must carry that stamp, not launder it.
        var ledger = new UsageLedger(Root, new FlatPricing(("model-A", 1m)));
        ledger.RecordUsage(
            Record(Usage("gen-1", 1_000, 0, cacheRead: 400), Root, UsageExecutionKind.Primary, "model-A")
        );

        var row = ConversationUsageAggregate.FoldByExecution(ledger.SnapshotRecords()).Should().ContainSingle().Subject;

        row.EstimatedCostCompleteness.Should().Be(CostCompleteness.Partial);
        row.PreferredCostMicros.Should().NotBeNull();
    }

    /// <summary>Prompt-only flat rates, no cache or reasoning rates, so cached requests price Partial.</summary>
    private sealed class FlatPricing(params (string Model, decimal PromptPerMillion)[] rates) : IPricingResolver
    {
        public ModelPricing? Resolve(string modelId)
        {
            foreach (var (model, rate) in rates)
            {
                if (model == modelId)
                {
                    return new ModelPricing
                    {
                        ModelId = modelId,
                        PromptPerMillion = rate,
                        CompletionPerMillion = rate,
                    };
                }
            }

            return null;
        }
    }
}
