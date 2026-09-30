using AchieveAi.LmDotnetTools.LmCore.Models;
using AchieveAi.LmDotnetTools.LmMultiTurn.DualLayer;
using AchieveAi.LmDotnetTools.LmMultiTurn.UsageAccounting;
using FluentAssertions;
using Xunit;

namespace LmMultiTurn.Tests.DualLayer;

/// <summary>
/// The executor is built before the planner, so its usage sink exists before the planner's ledger does.
/// Nothing the executor spends in that gap may be lost.
/// </summary>
public class DeferredUsageSinkTests
{
    private sealed class RecordingSink : IUsageSink
    {
        public List<UsageRecord> Records { get; } = [];

        public void RecordUsage(UsageRecord observation) => Records.Add(observation);
    }

    private static UsageRecord Record(string id) =>
        new()
        {
            LogicalCallId = id,
            ProviderAttemptId = id,
            RootConversationId = "executor-thread-1",
            RequestedModel = "cheap-model",
            InputTokens = 10,
        };

    [Fact]
    public void Records_held_before_the_target_is_set_are_replayed_in_arrival_order()
    {
        var sink = new DeferredUsageSink();
        var target = new RecordingSink();

        sink.RecordUsage(Record("a"));
        sink.RecordUsage(Record("b"));
        sink.Target = target;

        target.Records.Select(r => r.LogicalCallId).Should().Equal("a", "b");
    }

    [Fact]
    public void Records_after_the_target_is_set_go_straight_through()
    {
        var target = new RecordingSink();
        var sink = new DeferredUsageSink { Target = target };

        sink.RecordUsage(Record("a"));

        target.Records.Should().ContainSingle().Which.LogicalCallId.Should().Be("a");
    }

    [Fact]
    public void A_second_target_is_refused_so_usage_is_never_split_across_two_ledgers()
    {
        var first = new RecordingSink();
        var sink = new DeferredUsageSink { Target = first };

        var act = () => sink.Target = new RecordingSink();

        act.Should().Throw<InvalidOperationException>();
        sink.Target.Should().BeSameAs(first);
    }

    [Fact]
    public void Executor_thread_id_is_the_reserved_prefix_plus_the_planner_thread()
    {
        DualLayerThreadIds.ExecutorFor("thread-1").Should().Be("executor-thread-1");
        DualLayerThreadIds.ExecutorFor("thread-1").Should().StartWith(DualLayerThreadIds.ExecutorPrefix);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void Executor_thread_id_needs_a_planner_thread(string plannerThreadId)
    {
        var act = () => DualLayerThreadIds.ExecutorFor(plannerThreadId);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void A_sub_agent_the_executor_spawns_has_the_thread_its_conversation_would_give_it()
    {
        // Found by hand: every reader (tab replay, socket gate, agent list) forms a child's thread from
        // the conversation's id. Scoped by the executor's own id, the child's transcript was unreachable.
        var executorThreadId = DualLayerThreadIds.ExecutorFor("thread-1");

        SubAgentThreadIds.For(executorThreadId, "agent-1").Should().Be(SubAgentThreadIds.For("thread-1", "agent-1"));
        DualLayerThreadIds.TryGetPlannerThreadId(executorThreadId, out var planner).Should().BeTrue();
        planner.Should().Be("thread-1");
    }

    [Theory]
    [InlineData("executor-")]
    [InlineData("thread-1")]
    [InlineData(null)]
    public void Only_a_real_executor_thread_maps_to_a_planner(string? threadId)
    {
        DualLayerThreadIds.TryGetPlannerThreadId(threadId, out _).Should().BeFalse();
    }
}
